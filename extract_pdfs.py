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

# Names Windows will not let you create a file with (any extension).
_WINDOWS_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}

# Characters that are illegal in Windows file names (plus control chars).
_ILLEGAL_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


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

    # Guard against reserved device names like CON or LPT1.
    stem = name.rsplit(".", 1)[0]
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


def unique_path(directory: Path, filename: str, overwrite: bool = False) -> Path:
    """Return a path in *directory* that does not clobber an existing file.

    On a collision, appends `` (1)``, `` (2)`` ... before the extension --
    the same convention Windows Explorer uses -- so nothing is silently
    overwritten (important when many emails carry ``scan.pdf``).
    """
    target = directory / filename
    if overwrite or not target.exists():
        return target
    stem = target.stem
    suffix = target.suffix
    counter = 1
    while True:
        candidate = directory / f"{stem} ({counter}){suffix}"
        if not candidate.exists():
            return candidate
        counter += 1


# --------------------------------------------------------------------------- #
# PDF detection
# --------------------------------------------------------------------------- #
def looks_like_pdf(data: Optional[bytes]) -> bool:
    """True if the bytes begin with the ``%PDF-`` magic marker.

    The header is normally at byte 0, but a few producers emit a little
    junk first, so we look within the first kilobyte.
    """
    if not data:
        return False
    return b"%PDF-" in data[:1024]


def is_pdf_part(content_type: str, filename: Optional[str], data: Optional[bytes]) -> bool:
    """Decide whether an email part is a PDF, by type, name, or content."""
    if content_type.lower() in PDF_CONTENT_TYPES:
        return True
    if filename and filename.lower().endswith(".pdf"):
        return True
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
        is_tnef = ctype in {"application/ms-tnef", "application/vnd.ms-tnef"} or fname == "winmail.dat"
        if not is_tnef:
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
                if is_pdf_part("application/octet-stream", name, data) and data:
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
    counter = 0
    message = extract_msg.openMsg(str(path)) if hasattr(extract_msg, "openMsg") \
        else extract_msg.Message(str(path))
    try:
        counter = _walk_msg_attachments(message, path, found, counter)
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


def iter_email_files(inputs: Iterable[Path], recursive: bool, output_dir: Path) -> Iterator[Path]:
    """Yield every email file under the given inputs.

    Files that live inside *output_dir* are skipped so we never try to
    re-read our own results.
    """
    try:
        output_resolved = output_dir.resolve()
    except OSError:
        output_resolved = output_dir

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
            if output_resolved in resolved.parents:
                continue
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
    log=print,
) -> Summary:
    """Extract PDFs from *inputs* into *output_dir* and return a Summary."""
    summary = Summary()
    missing_msg_reported = False

    for email_path in iter_email_files(inputs, recursive, output_dir):
        summary.emails_scanned += 1
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

            destination_dir.mkdir(parents=True, exist_ok=True)
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
    """Tiny Tkinter front-end for non-technical users / double-clicking."""
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox
    except Exception:
        print(
            "A graphical window is not available in this environment.\n"
            "Run from a command prompt instead, e.g.:\n"
            "    python extract_pdfs.py C:\\Emails -o C:\\Emails\\PDFs",
            file=sys.stderr,
        )
        return 2

    root = tk.Tk()
    root.withdraw()

    messagebox.showinfo(
        "Extract PDFs from emails",
        "Next you'll choose:\n"
        "  1. the folder that holds your saved emails (.eml / .msg)\n"
        "  2. the folder to save the extracted PDFs into.",
    )
    in_dir = filedialog.askdirectory(title="Step 1 of 2: folder containing your emails")
    if not in_dir:
        return 0
    out_dir = filedialog.askdirectory(title="Step 2 of 2: where to save the PDFs")
    if not out_dir:
        return 0

    lines: list[str] = []
    summary = run(
        inputs=[Path(in_dir)],
        output_dir=Path(out_dir),
        recursive=True,
        log=lines.append,
    )
    print_summary(summary, dry_run=False, log=lines.append)

    body = f"Extracted {summary.pdfs_saved} PDF(s) from " \
           f"{summary.emails_scanned} email file(s)."
    if summary.skipped_msg:
        body += (f"\n\nSkipped {len(summary.skipped_msg)} Outlook .msg file(s). "
                 f"To include them, install extract-msg (see the README).")
    if summary.errors:
        body += f"\n\n{len(summary.errors)} file(s) had problems -- see details."
    body += f"\n\nPDFs saved in:\n{out_dir}"
    messagebox.showinfo("Done", body)
    print("\n".join(lines))
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
    )
    print_summary(summary, dry_run=args.dry_run)
    return 0 if not summary.errors else 1


if __name__ == "__main__":
    sys.exit(main())
