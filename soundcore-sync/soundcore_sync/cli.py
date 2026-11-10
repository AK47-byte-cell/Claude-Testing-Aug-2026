"""Command line: soundcore-sync run | pull | transcribe | scan | status | init."""
from __future__ import annotations

import argparse
import asyncio
import logging
import logging.handlers
import signal
import sys
import threading
from pathlib import Path

from . import __version__
from .config import EXAMPLE_CONFIG, Config
from .manifest import Manifest

log = logging.getLogger("soundcore_sync")


def setup_logging(cfg: Config, verbose: bool) -> None:
    level = logging.DEBUG if verbose else getattr(logging, cfg.logging.level, logging.INFO)
    fmt = logging.Formatter("%(asctime)s %(levelname)-7s %(name)s: %(message)s", "%Y-%m-%d %H:%M:%S")
    root = logging.getLogger()
    root.setLevel(level)
    console = logging.StreamHandler(sys.stdout)
    console.setFormatter(fmt)
    root.addHandler(console)
    if cfg.logging.file:
        cfg.logging.file.parent.mkdir(parents=True, exist_ok=True)
        fh = logging.handlers.RotatingFileHandler(cfg.logging.file, maxBytes=2_000_000, backupCount=3,
                                                  encoding="utf-8")
        fh.setFormatter(fmt)
        root.addHandler(fh)
    logging.getLogger("bleak").setLevel(logging.WARNING)
    logging.getLogger("faster_whisper").setLevel(logging.WARNING)


def build_engine(cfg: Config):
    from .transcriber import WhisperEngine

    tc = cfg.transcription
    return WhisperEngine(tc.model, tc.device, tc.compute_type, tc.language, tc.beam_size, tc.vad_filter,
                         tc.initial_prompt, tc.condition_on_previous_text)


def build_subject_engine(cfg: Config):
    from .subject import CompositeSubjectEngine, HeuristicSubjectEngine, OllamaSubjectEngine

    sc = cfg.subject
    if not sc.enabled or sc.backend == "off":
        return None
    heuristic = HeuristicSubjectEngine(sc.max_words)
    if sc.backend == "heuristic":
        return heuristic
    ollama = OllamaSubjectEngine(sc.model, sc.url, sc.max_chars, sc.timeout_seconds, sc.max_words)
    return ollama if sc.backend == "ollama" else CompositeSubjectEngine(ollama, heuristic)


def cmd_subject(cfg: Config, args) -> int:
    """Test the local title generation on a transcript file or a piece of text."""
    engine = build_subject_engine(cfg)
    if engine is None:
        print("Subject titles are disabled in the config.")
        return 1
    text = Path(args.file).read_text(encoding="utf-8") if args.file else args.text
    if not text:
        print("Give --file transcript.txt or --text \"...\"")
        return 2
    print(f"Backend: {cfg.subject.backend}" + (f" ({cfg.subject.model} at {cfg.subject.url})"
                                                 if cfg.subject.backend != "heuristic" else ""))
    title = engine.title(text, None)
    print(f"Title: {title!r}" if title else "No title (with ollama: is it running and the model pulled?)")
    return 0 if title else 1


def cmd_init(cfg: Config, args) -> int:
    path = Config.default_path()
    if path.exists() and not args.force:
        print(f"{path} already exists (use --force to overwrite).")
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(EXAMPLE_CONFIG, encoding="utf-8")
        print(f"Wrote {path}")
    cfg = Config.load(path)
    cfg.ensure_dirs()
    print(f"Inbox folder:   {cfg.folders.inbox}")
    print(f"State folder:   {cfg.folders.state_dir}")
    print("Edit the config file to change the folders or the model, then run: soundcore-sync run")
    return 0


def cmd_scan(cfg: Config, args) -> int:
    from .ble import RecorderClient

    print(f"Scanning for the recorder for {cfg.recorder.scan_timeout_seconds:.0f} s...")
    device = asyncio.run(RecorderClient.scan(cfg.recorder.scan_timeout_seconds))
    if device is None:
        print("Not found. Take the recorder out of its case (or tap it), make sure the soundcore app on "
              "your phone is closed, and try again.")
        return 1
    print(f"Found: {device.name or '(unnamed)'} at {device.address}")
    return 0


def cmd_pull(cfg: Config, args) -> int:
    from .ble import RecorderNotFound
    from .puller import Puller

    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    try:
        saved = asyncio.run(Puller(cfg, manifest).pull_once())
    except RecorderNotFound:
        print("Recorder not found (asleep, in its case, or connected to the phone).")
        return 1
    print(f"{saved} new recording(s) saved to {cfg.folders.inbox}")
    return 0


def cmd_transcribe(cfg: Config, args) -> int:
    from .transcriber import TranscribeLoop

    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    loop = TranscribeLoop(cfg, manifest, build_engine(cfg), build_subject_engine(cfg))
    if args.file:
        out = loop.process_file(Path(args.file))
        return 0 if out else 1
    loop.tc.settle_seconds = 0
    pending = loop.pending()
    if not pending:
        print("Nothing to transcribe.")
        return 0
    print(f"{len(pending)} file(s) to transcribe")
    done = 0
    for path in pending:
        loop.is_ready(path)  # first observation
        if loop.is_ready(path) and loop.process_file(path):
            done += 1
    print(f"{done}/{len(pending)} transcribed")
    return 0 if done == len(pending) else 1


def cmd_status(cfg: Config, args) -> int:
    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    print(f"Config:        {cfg.source or '(defaults; run soundcore-sync init)'}")
    print(f"Inbox:         {cfg.folders.inbox}")
    print(f"Transcripts:   {cfg.folders.transcripts or '(next to the audio files)'}")
    print(f"Model:         {cfg.transcription.model} on {cfg.transcription.device}")
    print(f"Pulled from recorder: {manifest.pulled_count()}")
    counts = manifest.transcript_counts()
    print(f"Transcriptions: done={counts.get('done', 0)} failed={counts.get('failed', 0)}")
    pending = manifest.pending_proposals()
    if pending:
        print(f"Rename proposals waiting for review: {len(pending)} (run: soundcore-sync review)")
    for row in manifest.recent(8):
        extra = f" — {row['error'][:80]}" if row["error"] else ""
        print(f"  {row['updated_at']}  {row['status']:6}  {Path(row['path']).name}{extra}")
    return 0


def cmd_matters(cfg: Config, args) -> int:
    from .matters import MatterIndex
    from .renamer import propose

    index = MatterIndex.load(cfg.rename.matters_source)
    print(f"Matter list: {cfg.rename.matters_source or '(not configured: set rename.matters_source)'}")
    print(f"{len(index)} file number(s) loaded")
    for m in list(index.matters.values())[:5]:
        print(f"  {m.number}  {m.label}")
    if args.text:
        prop = propose(args.text, index, min_score=cfg.rename.min_score, margin=cfg.rename.margin)
        print(f"Result: number={prop.number} label={prop.label} method={prop.method} "
              f"confidence={prop.confidence}\n  {prop.note}")
        for c in prop.candidates:
            print(f"  candidate {c.matter.number} {c.matter.label} score={c.score:.1f} words={c.matched}")
    return 0


def cmd_review(cfg: Config, args) -> int:
    """Accept or reject pending rename proposals."""
    from .renamer import Proposal
    from .transcriber import TranscribeLoop

    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    rows = manifest.pending_proposals()
    if not rows:
        print("No pending proposals.")
        return 0
    loop = TranscribeLoop(cfg, manifest, None)
    for row in rows:
        path = Path(row["path"])
        if not path.is_file():
            manifest.set_proposal_status(path, "gone")
            continue
        print(f"\n{path.name}\n  proposed: {row['file_number']} {row['label'] or ''}  "
              f"({row['method']}, {row['confidence']})\n  {row['note']}")
        if row["candidates"]:
            print(f"  candidates: {row['candidates']}")
        if args.accept_all:
            answer = "y"
        elif args.reject_all:
            answer = "n"
        else:
            answer = input("  rename? [y]es / [n]o / file number (1234-56) / [s]kip: ").strip().lower()
        if answer in ("s", ""):
            continue
        if answer == "n":
            manifest.set_proposal_status(path, "rejected")
            continue
        number, label = row["file_number"], row["label"]
        if answer != "y":
            number = answer
            m = loop.matters().get(number)
            label = m.label if m else None
        new_txt = loop.apply_rename(path, Proposal(number, label, "review", "high"), row["subject"])
        manifest.set_proposal_status(path, "accepted")
        print(f"  -> {new_txt.name if new_txt else 'renamed'}")
    return 0


def cmd_undo(cfg: Config, args) -> int:
    """Give a renamed recording (and its transcript) its original name back."""
    from .renamer import rename_set

    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    current = Path(args.file)
    if not current.is_absolute():
        current = cfg.folders.inbox / current
    original = manifest.original_name(current)
    if original is None:
        print(f"{current.name} was not renamed by soundcore-sync (or was undone already).")
        return 1
    moves = rename_set(current, original.stem, companions=cfg.rename.companions)
    new_audio = moves[current]
    new_txt = next((n for n in moves.values() if n.suffix == ".txt"), None)
    manifest.move_transcript(current, new_audio, new_txt)
    manifest.forget_rename(current)
    print(f"Restored: {new_audio.name}")
    return 0


def cmd_run(cfg: Config, args) -> int:
    from .notify import Notifier, Status
    from .puller import Puller
    from .transcriber import TranscribeLoop

    cfg.ensure_dirs()
    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    status = Status()
    notifier = Notifier(cfg.ui.notifications and not args.quiet, status)
    stop = threading.Event()
    threads: list[threading.Thread] = []
    if cfg.recorder.enabled and not args.no_pull:
        threads.append(threading.Thread(target=Puller(cfg, manifest, notifier).run_forever, args=(stop,),
                                        name="puller", daemon=True))
    if cfg.transcription.enabled and not args.no_transcribe:
        loop = TranscribeLoop(cfg, manifest, build_engine(cfg), build_subject_engine(cfg), notifier)
        threads.append(threading.Thread(target=loop.run_forever, args=(stop,), name="transcriber", daemon=True))
    if not threads:
        print("Nothing enabled; check the config.")
        return 1

    def _stop(*_):
        if not stop.is_set():
            log.info("Stopping...")
            stop.set()

    signal.signal(signal.SIGINT, _stop)
    signal.signal(signal.SIGTERM, _stop)
    log.info("soundcore-sync %s starting (inbox: %s)", __version__, cfg.folders.inbox)
    for t in threads:
        t.start()

    tray = None
    if cfg.ui.tray and not args.no_tray:
        from . import tray as tray_mod

        if tray_mod.available():
            tray = tray_mod.Tray(status, inbox=cfg.folders.inbox, log_file=cfg.logging.file,
                                 config_path=cfg.source, on_quit=_stop,
                                 pending_count=lambda: len(manifest.pending_proposals()))
        else:
            log.info("Tray icon not shown: install it with  pip install -e \".[tray]\"")
    try:
        if tray is not None:
            tray.run()  # blocks on the main thread until Quit
            _stop()
        else:
            while any(t.is_alive() for t in threads) and not stop.is_set():
                for t in threads:
                    t.join(timeout=1.0)
    except KeyboardInterrupt:
        _stop()
    stop.set()
    for t in threads:
        t.join(timeout=15)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="soundcore-sync",
                                     description="Pull soundcore Work recordings over Bluetooth and transcribe them.")
    parser.add_argument("--config", "-c", type=Path, help="config file (default: ~/.soundcore-sync/config.toml)")
    parser.add_argument("--verbose", "-v", action="store_true")
    parser.add_argument("--version", action="version", version=__version__)
    sub = parser.add_subparsers(dest="command")

    p = sub.add_parser("run", help="run both loops until stopped (default)")
    p.add_argument("--no-pull", action="store_true", help="do not talk to the recorder")
    p.add_argument("--no-transcribe", action="store_true", help="do not transcribe")
    p.add_argument("--no-tray", action="store_true", help="no icon next to the clock")
    p.add_argument("--quiet", action="store_true", help="no desktop notifications")
    p.set_defaults(func=cmd_run)
    sub.add_parser("pull", help="pull new recordings once").set_defaults(func=cmd_pull)
    p = sub.add_parser("transcribe", help="transcribe pending files once")
    p.add_argument("--file", help="transcribe this one file")
    p.set_defaults(func=cmd_transcribe)
    sub.add_parser("scan", help="check whether the recorder is visible").set_defaults(func=cmd_scan)
    sub.add_parser("status", help="show what has been pulled and transcribed").set_defaults(func=cmd_status)
    p = sub.add_parser("matters", help="show the matter list; --text to test the matching")
    p.add_argument("--text", help="transcript text to test against the list")
    p.set_defaults(func=cmd_matters)
    p = sub.add_parser("subject", help="test the local title generation (Ollama)")
    p.add_argument("--file", help="a transcript .txt")
    p.add_argument("--text", help="text to title")
    p.set_defaults(func=cmd_subject)
    p = sub.add_parser("review", help="accept or reject pending rename proposals")
    p.add_argument("--accept-all", action="store_true")
    p.add_argument("--reject-all", action="store_true")
    p.set_defaults(func=cmd_review)
    p = sub.add_parser("undo", help="restore a renamed recording's original name")
    p.add_argument("file", help="current file name (in the inbox) or path")
    p.set_defaults(func=cmd_undo)
    p = sub.add_parser("init", help="write a config file and create the folders")
    p.add_argument("--force", action="store_true")
    p.set_defaults(func=cmd_init)

    args = parser.parse_args(argv)
    if args.command is None:
        args = parser.parse_args(["run"] + (argv or []))
    cfg = Config.load(args.config)
    setup_logging(cfg, args.verbose)
    return args.func(cfg, args)


if __name__ == "__main__":
    sys.exit(main())
