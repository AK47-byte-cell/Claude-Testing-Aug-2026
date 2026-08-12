#!/usr/bin/env python3
"""Self-contained tests for extract_pdfs.

Creates a set of throwaway email files in a temporary folder, runs the
extractor over them, and checks that the right PDFs come out. Nothing is
written outside the temporary folder, and no internet access is used.

Run it with:

    python selftest.py

A line reading "ALL TESTS PASSED" at the end means the tool is working on
this machine. This exercises the ``.eml`` path (pure standard library) and
the ``.msg`` walking logic (via light-weight stand-ins for the extract-msg
package, so it runs even when that package is not installed).
"""

from __future__ import annotations

import tempfile
from email.message import EmailMessage
from pathlib import Path

import extract_pdfs as E

MIN_PDF = b"%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"


def _make_fixtures(emails: Path) -> None:
    def write(msg: EmailMessage, name: str) -> None:
        (emails / name).write_bytes(msg.as_bytes())

    m = EmailMessage()
    m["Subject"] = "Contract"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="pdf", filename="contract.pdf")
    write(m, "01_simple.eml")

    m = EmailMessage()
    m["Subject"] = "mixed"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="pdf", filename="invoice.pdf")
    m.add_attachment(MIN_PDF, maintype="application", subtype="pdf", filename="statement.pdf")
    m.add_attachment(b"not a pdf", maintype="application",
                     subtype="vnd.openxmlformats-officedocument.wordprocessingml.document",
                     filename="memo.docx")
    write(m, "02_mixed.eml")

    m = EmailMessage()
    m["Subject"] = "octet ext"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="octet-stream", filename="scan.pdf")
    write(m, "03_octet_ext.eml")

    m = EmailMessage()
    m["Subject"] = "magic only"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="octet-stream", filename="document.bin")
    write(m, "04_magic.eml")

    m = EmailMessage()
    m["Subject"] = "unicode"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="pdf",
                     filename="Mise en demeure – Réponse (été).pdf")
    write(m, "05_unicode.eml")

    inner = EmailMessage()
    inner["Subject"] = "inner"
    inner.set_content("inner")
    inner.add_attachment(MIN_PDF, maintype="application", subtype="pdf", filename="forwarded.pdf")
    outer = EmailMessage()
    outer["Subject"] = "Fwd"
    outer.set_content("see forward")
    outer.add_attachment(inner)  # message/rfc822
    write(outer, "06_forwarded.eml")

    m = EmailMessage()
    m["Subject"] = "no name"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="pdf")
    write(m, "07_noname.eml")

    for i in (8, 9):
        m = EmailMessage()
        m["Subject"] = f"scan {i}"
        m.set_content("body")
        m.add_attachment(MIN_PDF, maintype="application", subtype="pdf", filename="scan.pdf")
        write(m, f"{i:02d}_scan.eml")

    m = EmailMessage()
    m["Subject"] = "nasty"
    m.set_content("body")
    m.add_attachment(MIN_PDF, maintype="application", subtype="pdf",
                     filename="..\\..\\Windows\\System32\\evil:bad?.pdf")
    write(m, "10_traversal.eml")

    m = EmailMessage()
    m["Subject"] = "inline"
    m.set_content("body")
    m.add_related(MIN_PDF, maintype="application", subtype="pdf", filename="inline.pdf")
    write(m, "11_inline.eml")

    (emails / "notes.txt").write_text("ignore me")


def _check(name: str, condition: bool) -> bool:
    status = "ok  " if condition else "FAIL"
    print(f"  [{status}] {name}")
    return condition


def test_eml(workdir: Path) -> bool:
    emails = workdir / "emails"
    out = workdir / "out"
    emails.mkdir()
    _make_fixtures(emails)

    summary = E.run(inputs=[emails], output_dir=out, recursive=True, log=lambda *a: None)
    files = {p.name for p in out.iterdir()}

    ok = True
    ok &= _check("12 PDFs extracted from 11 emails",
                 summary.pdfs_saved == 12 and summary.emails_scanned == 11)
    ok &= _check("simple contract.pdf", "contract.pdf" in files)
    ok &= _check("docx not extracted", not any("memo" in f for f in files))
    ok &= _check("octet-stream w/ .pdf ext", "scan.pdf" in files)
    ok &= _check("magic-byte detection (.bin -> .pdf)", "document.bin.pdf" in files)
    ok &= _check("unicode filename preserved",
                 "Mise en demeure – Réponse (été).pdf" in files)
    ok &= _check("forwarded/nested PDF", "forwarded.pdf" in files)
    ok &= _check("missing filename synthesized",
                 any(f.startswith("07_noname_attachment_") for f in files))
    ok &= _check("collision numbering", "scan (1).pdf" in files and "scan (2).pdf" in files)
    ok &= _check("path traversal neutralized (no dirs escaped)",
                 all(p.is_file() for p in out.iterdir()) and not (workdir / "Windows").exists())
    ok &= _check("illegal chars stripped from nasty name", "evil_bad_.pdf" in files)
    ok &= _check("inline PDF extracted", "inline.pdf" in files)
    ok &= _check("txt file ignored", not any(f.endswith(".txt") for f in files))
    ok &= _check("all outputs are valid PDFs",
                 all((out / f).read_bytes().startswith(b"%PDF-") for f in files))
    ok &= _check("byte-perfect copy", (out / "contract.pdf").read_bytes() == MIN_PDF)
    return ok


def test_msg_logic() -> bool:
    """Exercise the .msg walking recursion with stand-ins for extract-msg."""
    class FakeAtt:
        def __init__(self, data, longFilename=None, shortFilename=None):
            self.data = data
            self.longFilename = longFilename
            self.shortFilename = shortFilename

    class FakeMsg:
        def __init__(self, attachments):
            self.attachments = attachments

    embedded = FakeMsg([FakeAtt(MIN_PDF, longFilename="embedded_report.pdf")])
    top = FakeMsg([
        FakeAtt(MIN_PDF, longFilename="statement.pdf"),
        FakeAtt(b"not a pdf", longFilename="notes.txt"),
        FakeAtt(MIN_PDF, shortFilename="scan.pdf"),
        FakeAtt(MIN_PDF),               # no name
        FakeAtt(embedded),              # embedded message -> recurse
    ])

    found: list = []
    E._walk_msg_attachments(top, Path("client.msg"), found, 0)
    names = sorted(f.filename for f in found)

    ok = True
    ok &= _check(".msg: 4 PDFs (incl. embedded, excl. non-pdf)", len(found) == 4)
    ok &= _check(".msg: longFilename used", "statement.pdf" in names)
    ok &= _check(".msg: shortFilename fallback", "scan.pdf" in names)
    ok &= _check(".msg: embedded message recursed", "embedded_report.pdf" in names)
    ok &= _check(".msg: unnamed synthesized",
                 any(n.startswith("client_attachment_") for n in names))
    ok &= _check(".msg: bytes preserved", all(f.data == MIN_PDF for f in found))
    return ok


def test_sanitize() -> bool:
    ok = True
    ok &= _check("strips path traversal",
                 "/" not in E.sanitize_filename("../../etc/passwd") and
                 "\\" not in E.sanitize_filename("..\\..\\x"))
    ok &= _check("reserved name CON.pdf -> prefixed",
                 E.sanitize_filename("CON.pdf").startswith("_"))
    ok &= _check("trailing dot/space removed",
                 not E.sanitize_filename("name.  ").endswith((" ", ".")))
    ok &= _check("empty -> default", E.sanitize_filename("") == "attachment")
    ok &= _check("ensure .pdf extension", E.ensure_pdf_extension("x").endswith(".pdf"))
    ok &= _check("already .pdf not doubled", E.ensure_pdf_extension("x.PDF") == "x.PDF")
    return ok


def main() -> int:
    print("Running extract_pdfs self-tests...\n")
    with tempfile.TemporaryDirectory() as tmp:
        workdir = Path(tmp)
        print(".eml extraction:")
        r1 = test_eml(workdir)
        print("\n.msg walking logic:")
        r2 = test_msg_logic()
        print("\nfilename sanitization:")
        r3 = test_sanitize()

    print()
    if r1 and r2 and r3:
        print("ALL TESTS PASSED")
        return 0
    print("SOME TESTS FAILED")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
