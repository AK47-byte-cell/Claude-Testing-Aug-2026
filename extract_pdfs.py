#!/usr/bin/env python3
"""Extract PDF attachments from saved email files.

Point this at a folder full of saved emails (``.eml`` and/or Outlook
``.msg`` files) and it will pull every attached (or inline) PDF out of
them and drop the PDFs into a separate output folder.

Two ways to run it on Windows:

* Double-click ``Extract PDFs.bat`` (or run the script with no arguments)
  to get a small point-and-click window that asks for the input and
  output folders.
* Run it from a command prompt / drag folders or files onto the ``.bat``
  for batch and scripted use.

Only Python 3.8+ is required for ``.eml`` files -- everything used there
ships with Python. Reading Outlook ``.msg`` files additionally needs the
``extract-msg`` package (``pip install extract-msg``); the script keeps
working on ``.eml`` files even when it is not installed and tells you what
to install if it meets a ``.msg`` file.
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from email import policy
from email.message import EmailMessage
from email.parser import BytesParser
from pathlib import Path
from typing import Iterable, Iterator, Optional

# Content types that mean "this is a PDF", regardless of file extension.
PDF_CONTENT_TYPES = {
    "application/pdf",
    "application/x-pdf",
    "application/acrobat",
    "application/vnd.pdf",
    "application/x-bzpdf",
    "application/x-gzpdf",
    "text/pdf",
    "text/x-pdf",
}

# Email files we know how to open.
EML_SUFFIXES = {".eml"}
MSG_SUFFIXES = {".msg"}
EMAIL_SUFFIXES = EML_SUFFIXES | MSG_SUFFIXES

# TNEF ("winmail.dat") wrappers some Outlook senders produce.
TNEF_CONTENT_TYPES = {"application/ms-tnef", "application/vnd.ms-tnef"}

# Names Windows will not let you create a file with (any extension).
_WINDOWS_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}

# Characters that are illegal in Windows file names (plus control chars).
_ILLEGAL_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

# Skip any single email file larger than this before parsing, so a crafted
# multi-gigabyte message cannot exhaust memory. Generous enough for real
# scanned-document attachments; override with --max-mb (0 disables).
DEFAULT_MAX_BYTES = 1024 * 1024 * 1024  # 1 GB

# Keep the whole output path comfortably under the classic Windows 260-char
# limit (long-path support is off by default on most machines).
_MAX_PATH_BUDGET = 250


@dataclass
class FoundPdf:
    """A single PDF located inside an email, ready to be written out."""

    data: bytes
    filename: str          # sanitized, guaranteed to end in .pdf
    source: Path           # the email file it came from
    original_name: str     # the attachment name as it appeared in the email


@dataclass
class Summary:
    """Tally of what happened, printed at the end of a run."""

    emails_scanned: int = 0
    pdfs_saved: int = 0
    saved_paths: list[Path] = field(default_factory=list)
    skipped_msg: list[Path] = field(default_factory=list)
    errors: list[tuple[Path, str]] = field(default_factory=list)


# --------------------------------------------------------------------------- #
# File-name helpers
# --------------------------------------------------------------------------- #
def sanitize_filename(name: Optional[str], default: str = "attachment") -> str:
    """Turn an arbitrary attachment name into a safe Windows file name.

    Strips any directory components (defends against ``..\\..`` style path
    traversal in a malicious attachment name), removes illegal characters,
    trims trailing dots/spaces, avoids reserved device names, and caps the
    length so long names do not blow past Windows' path limits.
    """
    if not name:
        name = default
    # Keep only the final path component -- never let an attachment name
    # steer us into another directory.
    name = name.replace("\\", "/").split("/")[-1]
    name = _ILLEGAL_CHARS.sub("_", name)
    # Windows silently drops trailing dots and spaces; do it explicitly.
    name = name.rstrip(". ").strip()
    if not name:
        name = default

    # Guard against reserved device names like CON or LPT1. Windows keys these
    # on the segment before the FIRST dot, so "CON.report.pdf" is reserved too.
    stem = name.split(".", 1)[0]
    if stem.upper() in _WINDOWS_RESERVED:
        name = "_" + name

    # Cap length while preserving the extension.
    if len(name) > 200:
        root, dot, ext = name.rpartition(".")
        if dot and len(ext) <= 10:
            name = root[: 200 - len(ext) - 1] + "." + ext
        else:
            name = name[:200]
    return name


def ensure_pdf_extension(name: str) -> str:
    """Make sure the file name ends in ``.pdf`` (case-insensitively)."""
    return name if name.lower().endswith(".pdf") else name + ".pdf"


def fit_path_length(directory: Path, filename: str, limit: int = _MAX_PATH_BUDGET) -> str:
    """Shorten *filename* so ``directory\\filename`` stays under *limit* chars.

    Windows' default maximum path length is 260 characters; a long output
    folder plus a long attachment name can exceed it and make the write
    fail. Trim the name (keeping its ``.pdf`` extension) rather than losing
    the PDF. Leaves a little room for a `` (1)`` collision suffix.
    """
    budget = limit - (len(str(directory)) + 1) - 6  # 6 = room for " (12)"
    if len(filename) <= budget or budget < 8:
        # Already fits, or the directory itself is so long that trimming the
        # name cannot help -- let any resulting error surface normally.
        return filename
    root, dot, ext = filename.rpartition(".")
    if dot and 0 < len(ext) <= 10:
        keep = max(1, budget - len(ext) - 1)
        return root[:keep] + "." + ext
    return filename[:budget]


def unique_path(directory: Path, filename: str, overwrite: bool = False) -> Path:
    """Return a path in *directory* that does not clobber an existing file.

    On a collision, appends `` (1)``, `` (2)`` ... before the extension --
    the same convention Windows Explorer uses -- so nothing is silently
    overwritten (important when many emails carry ``scan.pdf``).

    Symlinks are never written through: a pre-existing link (even a broken
    one) is treated as "taken" so the write always lands on a real file
    inside *directory*.
    """
    def taken(p: Path) -> bool:
        return p.exists() or p.is_symlink()

    target = directory / filename
    if overwrite and not target.is_symlink():
        return target
    if not taken(target):
        return target
    stem = target.stem
    suffix = target.suffix
    counter = 1
    while True:
        candidate = directory / f"{stem} ({counter}){suffix}"
        if not taken(candidate):
            return candidate
        counter += 1


# --------------------------------------------------------------------------- #
# PDF detection
# --------------------------------------------------------------------------- #
def looks_like_pdf(data: Optional[bytes]) -> bool:
    """True if the bytes *begin* with the ``%PDF-`` marker.

    Checks the signature at the start (allowing only leading whitespace), not
    anywhere in the payload -- otherwise an email body or document that merely
    mentions ``%PDF-`` would be mistaken for a PDF.
    """
    if not data:
        return False
    return data.lstrip()[:5] == b"%PDF-"


def is_pdf_part(content_type: str, filename: Optional[str], data: Optional[bytes]) -> bool:
    """Decide whether an email part is a PDF, by type, name, or content."""
    ctype = content_type.lower()
    if ctype in PDF_CONTENT_TYPES:
        return True
    if filename and filename.lower().endswith(".pdf"):
        return True
    # Fall back to sniffing the magic bytes, but never for text parts -- an
    # email's plain-text or HTML body must not be mistaken for an attachment.
    if ctype.startswith("text/"):
        return False
    return looks_like_pdf(data)


# --------------------------------------------------------------------------- #
# .eml parsing (standard library only)
# --------------------------------------------------------------------------- #
def extract_from_eml(path: Path) -> list[FoundPdf]:
    """Pull every PDF out of an RFC-822 ``.eml`` file."""
    with path.open("rb") as fp:
        message = BytesParser(policy=policy.default).parse(fp)

    found: list[FoundPdf] = []
    counter = 0
    # walk() descends into multipart bodies and message/rfc822 attachments,
    # so forwarded emails and their attachments are covered too.
    for part in message.walk():
        if part.is_multipart():
            continue
        content_type = part.get_content_type()
        # TNEF wrappers are handled separately below, not as raw attachments.
        if content_type.lower() in TNEF_CONTENT_TYPES:
            continue
        filename = part.get_filename()  # policy.default decodes RFC-2047 names

        try:
            data = part.get_payload(decode=True)
        except Exception:
            data = None

        if not is_pdf_part(content_type, filename, data):
            continue
        if not data:
            continue

        counter += 1
        found.append(_build_found_pdf(path, filename, data, counter))

    # Some Outlook mail arrives with attachments wrapped in a TNEF
    # (winmail.dat) blob. Extract those too when the optional helper exists.
    found.extend(_extract_tnef_pdfs(message, path, len(found)))
    return found


def _extract_tnef_pdfs(message: EmailMessage, path: Path, start_index: int) -> list[FoundPdf]:
    """Best-effort extraction of PDFs from a winmail.dat (TNEF) part."""
    found: list[FoundPdf] = []
    counter = start_index
    for part in message.walk():
        if part.is_multipart():
            continue
        ctype = part.get_content_type().lower()
        fname = (part.get_filename() or "").lower()
        if ctype not in TNEF_CONTENT_TYPES and fname != "winmail.dat":
            continue
        try:
            import tnefparse  # optional dependency
        except ImportError:
            return found  # silently skip; documented as optional
        try:
            blob = part.get_payload(decode=True)
            if not blob:
                continue
            tnef = tnefparse.TNEF(blob)
            for att in getattr(tnef, "attachments", []):
                data = getattr(att, "data", None)
                name = getattr(att, "name", None)
                if isinstance(name, bytes):
                    name = name.decode("utf-8", "replace")
                if data and is_pdf_part("application/octet-stream", name, data):
                    counter += 1
                    found.append(_build_found_pdf(path, name, data, counter))
        except Exception:
            continue
    return found


# --------------------------------------------------------------------------- #
# .msg parsing (needs the extract-msg package)
# --------------------------------------------------------------------------- #
def extract_from_msg(path: Path) -> list[FoundPdf]:
    """Pull every PDF out of an Outlook ``.msg`` file.

    Raises ``ImportError`` if the ``extract-msg`` package is missing so the
    caller can report it once and keep processing other files.
    """
    import extract_msg  # raises ImportError if not installed

    found: list[FoundPdf] = []
    message = extract_msg.openMsg(str(path)) if hasattr(extract_msg, "openMsg") \
        else extract_msg.Message(str(path))
    try:
        _walk_msg_attachments(message, path, found, 0)
    finally:
        try:
            message.close()
        except Exception:
            pass
    return found


def _walk_msg_attachments(message, path: Path, found: list[FoundPdf], counter: int) -> int:
    """Recursively scan an extract-msg message and its embedded messages."""
    for att in getattr(message, "attachments", []):
        data = getattr(att, "data", None)

        # Embedded .msg messages surface as a message object rather than
        # raw bytes -- recurse into them.
        if data is not None and not isinstance(data, (bytes, bytearray)):
            if hasattr(data, "attachments"):
                counter = _walk_msg_attachments(data, path, found, counter)
            continue
        if not data:
            continue

        name = (
            getattr(att, "longFilename", None)
            or getattr(att, "shortFilename", None)
            or getattr(att, "name", None)
        )
        if is_pdf_part("application/octet-stream", name, bytes(data)):
            counter += 1
            found.append(_build_found_pdf(path, name, bytes(data), counter))
    return counter


# --------------------------------------------------------------------------- #
# Shared helpers
# --------------------------------------------------------------------------- #
def _build_found_pdf(source: Path, raw_name: Optional[str], data: bytes, index: int) -> FoundPdf:
    """Normalise a discovered attachment into a FoundPdf record."""
    original = raw_name or ""
    if raw_name:
        clean = sanitize_filename(raw_name)
    else:
        # No name in the email -> synthesize one from the source email name.
        clean = sanitize_filename(f"{source.stem}_attachment_{index}")
    clean = ensure_pdf_extension(clean)
    return FoundPdf(data=data, filename=clean, source=source, original_name=original)


def dispatch(path: Path) -> list[FoundPdf]:
    """Extract PDFs from a single email file, choosing the parser by type."""
    suffix = path.suffix.lower()
    if suffix in EML_SUFFIXES:
        return extract_from_eml(path)
    if suffix in MSG_SUFFIXES:
        return extract_from_msg(path)
    return []


def iter_email_files(inputs: Iterable[Path], recursive: bool) -> Iterator[Path]:
    """Yield every email file under the given inputs, without duplicates.

    Only ``.eml``/``.msg`` files are ever read and only ``.pdf`` files are
    ever written, so there is no risk of re-reading our own output -- the
    input and output folders may safely be the same.
    """
    seen: set[Path] = set()
    for item in inputs:
        if item.is_file():
            candidates: Iterable[Path] = [item]
        elif item.is_dir():
            pattern = "**/*" if recursive else "*"
            candidates = sorted(p for p in item.glob(pattern) if p.is_file())
        else:
            candidates = []
        for candidate in candidates:
            if candidate.suffix.lower() not in EMAIL_SUFFIXES:
                continue
            try:
                resolved = candidate.resolve()
            except OSError:
                resolved = candidate
            if resolved in seen:
                continue
            seen.add(resolved)
            yield candidate


# --------------------------------------------------------------------------- #
# Driver
# --------------------------------------------------------------------------- #
def run(
    inputs: list[Path],
    output_dir: Path,
    recursive: bool = False,
    group_by_email: bool = False,
    prefix_source: bool = False,
    overwrite: bool = False,
    dry_run: bool = False,
    max_bytes: int = DEFAULT_MAX_BYTES,
    log=print,
) -> Summary:
    """Extract PDFs from *inputs* into *output_dir* and return a Summary."""
    summary = Summary()
    missing_msg_reported = False

    for email_path in iter_email_files(inputs, recursive):
        summary.emails_scanned += 1

        # Guard against pathologically large messages exhausting memory.
        if max_bytes:
            try:
                size = email_path.stat().st_size
            except OSError:
                size = 0
            if size > max_bytes:
                limit_mb = max_bytes // (1024 * 1024)
                summary.errors.append((email_path, f"skipped: larger than {limit_mb} MB"))
                log(f"  ! Skipping {email_path.name}: larger than {limit_mb} MB")
                continue

        try:
            pdfs = dispatch(email_path)
        except ImportError:
            summary.skipped_msg.append(email_path)
            if not missing_msg_reported:
                log(
                    "  ! Skipping Outlook .msg files -- install support with:\n"
                    "        pip install extract-msg"
                )
                missing_msg_reported = True
            continue
        except Exception as exc:  # keep going even if one email is corrupt
            summary.errors.append((email_path, str(exc)))
            log(f"  ! Could not read {email_path.name}: {exc}")
            continue

        if not pdfs:
            continue

        log(f"  {email_path.name}: {len(pdfs)} PDF(s)")
        for pdf in pdfs:
            destination_dir = output_dir
            if group_by_email:
                destination_dir = output_dir / sanitize_filename(email_path.stem)
            filename = pdf.filename
            if prefix_source:
                filename = ensure_pdf_extension(
                    sanitize_filename(f"{email_path.stem} - {pdf.filename}")
                )

            if dry_run:
                target = destination_dir / filename
                log(f"      would save -> {target}")
                summary.pdfs_saved += 1
                summary.saved_paths.append(target)
                continue

            try:
                destination_dir.mkdir(parents=True, exist_ok=True)
            except OSError as exc:
                summary.errors.append((email_path, f"cannot create folder {destination_dir}: {exc}"))
                log(f"      ! Cannot create folder {destination_dir}: {exc}")
                continue

            filename = fit_path_length(destination_dir, filename)
            target = unique_path(destination_dir, filename, overwrite=overwrite)
            try:
                target.write_bytes(pdf.data)
            except OSError as exc:
                summary.errors.append((email_path, f"writing {filename}: {exc}"))
                log(f"      ! Failed to write {filename}: {exc}")
                continue
            log(f"      saved -> {target}")
            summary.pdfs_saved += 1
            summary.saved_paths.append(target)

    return summary


def explain_empty_result(summary: Summary) -> Optional[str]:
    """A friendly reason when nothing was extracted, or None if PDFs were saved."""
    if summary.pdfs_saved:
        return None
    if summary.emails_scanned == 0:
        return ("No .eml or .msg email files were found there. Make sure you "
                "picked the folder that actually contains the saved emails.")
    if summary.skipped_msg and len(summary.skipped_msg) == summary.emails_scanned:
        return ("Every email was an Outlook .msg file, which needs an extra "
                "add-on. Install it with:  pip install extract-msg")
    return (f"Looked in {summary.emails_scanned} email(s) but none contained a "
            f"PDF attachment.")


def print_summary(summary: Summary, dry_run: bool, log=print) -> None:
    log("")
    log("=" * 60)
    verb = "Would extract" if dry_run else "Extracted"
    log(f"{verb} {summary.pdfs_saved} PDF(s) from {summary.emails_scanned} email file(s).")
    if summary.skipped_msg:
        log(f"Skipped {len(summary.skipped_msg)} Outlook .msg file(s) "
            f"(install extract-msg to include them).")
    if summary.errors:
        log(f"{len(summary.errors)} file(s) had problems:")
        for path, err in summary.errors:
            log(f"   - {path.name}: {err}")
    reason = explain_empty_result(summary)
    if reason:
        log(reason)
    log("=" * 60)


# --------------------------------------------------------------------------- #
# Command line + GUI entry points
# --------------------------------------------------------------------------- #
def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="extract_pdfs",
        description="Extract PDF attachments from saved email files (.eml / .msg).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Examples:\n"
            "  extract_pdfs C:\\Emails -o C:\\Emails\\PDFs\n"
            "  extract_pdfs C:\\Emails --recursive --group-by-email\n"
            "  extract_pdfs message1.eml message2.msg -o .\n"
            "\nRun with no arguments (or double-click the .bat) for a "
            "point-and-click window."
        ),
    )
    parser.add_argument(
        "inputs", nargs="*", type=Path,
        help="Email files and/or folders containing them.",
    )
    parser.add_argument(
        "-o", "--output", type=Path, default=None,
        help="Folder to save extracted PDFs into "
             "(default: an '_Extracted_PDFs' folder next to the input).",
    )
    parser.add_argument(
        "-r", "--recursive", action="store_true",
        help="Search sub-folders too.",
    )
    parser.add_argument(
        "--group-by-email", action="store_true",
        help="Create one sub-folder per source email (keeps provenance clear).",
    )
    parser.add_argument(
        "--prefix-source", action="store_true",
        help="Prefix each PDF's name with the source email's file name.",
    )
    parser.add_argument(
        "--overwrite", action="store_true",
        help="Overwrite existing files instead of adding ' (1)', ' (2)' ...",
    )
    parser.add_argument(
        "--max-mb", type=float, default=1024,
        help="Skip any single email file larger than this many megabytes "
             "(default: 1024; use 0 for no limit).",
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="Show what would be extracted without writing any files.",
    )
    parser.add_argument(
        "--gui", action="store_true",
        help="Force the point-and-click window even when arguments are given.",
    )
    return parser


def default_output_for(inputs: list[Path]) -> Path:
    """Pick a sensible default output folder based on the first input."""
    first = inputs[0]
    base = first if first.is_dir() else first.parent
    return base / "_Extracted_PDFs"


def run_gui() -> int:
    """Point-and-click front-end for non-technical users / double-clicking."""
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox, ttk
    except Exception:
        print(
            "A graphical window is not available in this environment.\n"
            "Run from a command prompt instead, e.g.:\n"
            "    python extract_pdfs.py C:\\Emails -o C:\\Emails\\PDFs",
            file=sys.stderr,
        )
        return 2

    import threading

    root = tk.Tk()
    root.title("Extract PDFs from emails")
    root.minsize(560, 0)
    try:
        root.eval("tk::PlaceWindow . center")
    except Exception:
        pass

    pad = {"padx": 12, "pady": 6}
    in_var = tk.StringVar()
    out_var = tk.StringVar()
    group_var = tk.BooleanVar(value=True)
    status_var = tk.StringVar(value="")

    tk.Label(
        root, justify="left",
        text="Pull every PDF attachment out of your saved emails (.eml / .msg)\n"
             "and copy them into a folder of their own.",
    ).grid(row=0, column=0, columnspan=3, sticky="w", **pad)

    tk.Label(root, text="Emails folder:").grid(row=1, column=0, sticky="e", **pad)
    tk.Entry(root, textvariable=in_var, width=48).grid(row=1, column=1, sticky="we", **pad)

    tk.Label(root, text="Save PDFs to:").grid(row=2, column=0, sticky="e", **pad)
    tk.Entry(root, textvariable=out_var, width=48).grid(row=2, column=1, sticky="we", **pad)

    tk.Checkbutton(
        root, variable=group_var,
        text="Keep each email's PDFs in their own sub-folder "
             "(helps track which email each PDF came from)",
        justify="left", wraplength=520,
    ).grid(row=3, column=0, columnspan=3, sticky="w", **pad)

    progress = ttk.Progressbar(root, mode="indeterminate")
    status = tk.Label(root, textvariable=status_var, justify="left", fg="#444")
    status.grid(row=5, column=0, columnspan=3, sticky="w", **pad)

    root.columnconfigure(1, weight=1)

    def browse_in() -> None:
        chosen = filedialog.askdirectory(title="Folder containing your emails")
        if chosen:
            in_var.set(chosen)
            if not out_var.get():
                out_var.set(str(Path(chosen) / "_Extracted_PDFs"))

    def browse_out() -> None:
        chosen = filedialog.askdirectory(title="Where to save the PDFs")
        if chosen:
            out_var.set(chosen)

    tk.Button(root, text="Browse...", command=browse_in).grid(row=1, column=2, **pad)
    tk.Button(root, text="Browse...", command=browse_out).grid(row=2, column=2, **pad)

    def show_done(summary: Summary, out_dir: str) -> None:
        body = (f"Extracted {summary.pdfs_saved} PDF(s) from "
                f"{summary.emails_scanned} email file(s).")
        reason = explain_empty_result(summary)
        if reason:
            body += f"\n\n{reason}"
        if summary.skipped_msg:
            body += (f"\n\nSkipped {len(summary.skipped_msg)} Outlook .msg file(s). "
                     f"To include them, install extract-msg (see the README).")
        if summary.errors:
            body += f"\n\n{len(summary.errors)} file(s) had problems:"
            for path, err in summary.errors[:5]:
                body += f"\n   - {path.name}: {err}"
            if len(summary.errors) > 5:
                body += f"\n   ... and {len(summary.errors) - 5} more."
        if summary.pdfs_saved:
            body += f"\n\nPDFs saved in:\n{out_dir}"
        status_var.set(f"Done - {summary.pdfs_saved} PDF(s) saved.")
        messagebox.showinfo("Done", body)

    def start() -> None:
        in_dir = in_var.get().strip()
        out_dir = out_var.get().strip()
        if not in_dir:
            messagebox.showwarning("Choose a folder", "Please choose the folder that holds your emails.")
            return
        if not Path(in_dir).is_dir():
            messagebox.showwarning("Folder not found", f"This folder does not exist:\n{in_dir}")
            return
        if not out_dir:
            out_dir = str(Path(in_dir) / "_Extracted_PDFs")
            out_var.set(out_dir)

        run_btn.config(state="disabled")
        progress.grid(row=4, column=0, columnspan=3, sticky="we", **pad)
        progress.start(12)
        status_var.set("Working... this can take a while for large folders.")

        results: dict = {}

        def worker() -> None:
            lines: list[str] = []
            try:
                results["summary"] = run(
                    inputs=[Path(in_dir)],
                    output_dir=Path(out_dir),
                    recursive=True,
                    group_by_email=group_var.get(),
                    log=lines.append,
                )
                results["lines"] = lines
            except Exception as exc:  # never let the worker die silently
                results["error"] = exc

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()

        def poll() -> None:
            if thread.is_alive():
                root.after(120, poll)
                return
            progress.stop()
            progress.grid_remove()
            run_btn.config(state="normal")
            if "error" in results:
                status_var.set("Something went wrong.")
                messagebox.showerror(
                    "Error",
                    f"Could not finish extracting:\n\n{results['error']}\n\n"
                    f"Check that the 'Save PDFs to' folder is somewhere you can write to.",
                )
                return
            show_done(results["summary"], out_dir)

        root.after(120, poll)

    run_btn = tk.Button(root, text="Extract PDFs", default="active", command=start)
    run_btn.grid(row=6, column=0, columnspan=3, pady=(4, 12))

    root.mainloop()
    return 0


def _configure_console_encoding() -> None:
    """Avoid UnicodeEncodeError when printing accented file names on Windows.

    A classic Windows console defaults to a legacy code page (e.g. cp1252)
    that cannot represent characters like the en-dash in
    'Mise en demeure - Reponse.pdf'. Re-encode our output as UTF-8 and
    replace anything unprintable rather than crashing the whole run.
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


def main(argv: Optional[list[str]] = None) -> int:
    _configure_console_encoding()
    parser = build_parser()
    args = parser.parse_args(argv)

    # No inputs, or explicit --gui -> friendly window.
    if args.gui or not args.inputs:
        return run_gui()

    inputs: list[Path] = args.inputs
    missing = [p for p in inputs if not p.exists()]
    if missing:
        for p in missing:
            print(f"Input does not exist: {p}", file=sys.stderr)
        return 2

    output_dir = args.output or default_output_for(inputs)
    max_bytes = int(args.max_mb * 1024 * 1024) if args.max_mb and args.max_mb > 0 else 0

    print(f"Scanning for emails in: {', '.join(str(p) for p in inputs)}")
    print(f"Saving PDFs to:         {output_dir}")
    if args.dry_run:
        print("(dry run -- no files will be written)")
    print("")

    summary = run(
        inputs=inputs,
        output_dir=output_dir,
        recursive=args.recursive,
        group_by_email=args.group_by_email,
        prefix_source=args.prefix_source,
        overwrite=args.overwrite,
        dry_run=args.dry_run,
        max_bytes=max_bytes,
    )
    print_summary(summary, dry_run=args.dry_run)
    return 0 if not summary.errors else 1


if __name__ == "__main__":
    sys.exit(main())
