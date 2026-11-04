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
    return WhisperEngine(tc.model, tc.device, tc.compute_type, tc.language, tc.beam_size, tc.vad_filter)


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
    loop = TranscribeLoop(cfg, manifest, build_engine(cfg))
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
    for row in manifest.recent(8):
        extra = f" — {row['error'][:80]}" if row["error"] else ""
        print(f"  {row['updated_at']}  {row['status']:6}  {Path(row['path']).name}{extra}")
    return 0


def cmd_run(cfg: Config, args) -> int:
    from .puller import Puller
    from .transcriber import TranscribeLoop

    cfg.ensure_dirs()
    manifest = Manifest(cfg.folders.state_dir / "manifest.sqlite")
    stop = threading.Event()
    threads: list[threading.Thread] = []
    if cfg.recorder.enabled and not args.no_pull:
        threads.append(threading.Thread(target=Puller(cfg, manifest).run_forever, args=(stop,),
                                        name="puller", daemon=True))
    if cfg.transcription.enabled and not args.no_transcribe:
        loop = TranscribeLoop(cfg, manifest, build_engine(cfg))
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
    try:
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
    p.set_defaults(func=cmd_run)
    sub.add_parser("pull", help="pull new recordings once").set_defaults(func=cmd_pull)
    p = sub.add_parser("transcribe", help="transcribe pending files once")
    p.add_argument("--file", help="transcribe this one file")
    p.set_defaults(func=cmd_transcribe)
    sub.add_parser("scan", help="check whether the recorder is visible").set_defaults(func=cmd_scan)
    sub.add_parser("status", help="show what has been pulled and transcribed").set_defaults(func=cmd_status)
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
