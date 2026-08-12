# Extract PDFs from saved emails

A small Windows tool that scans a folder of saved email files and pulls every
**attached (or inline) PDF** out of them into a separate folder — so you don't
have to open each email and "Save As" one attachment at a time.

It reads:

- **`.eml`** files — the standard email format (Outlook, Thunderbird, Apple
  Mail, Gmail "Show original / Download message", most mail servers).
- **`.msg`** files — Outlook's own format (what you get when you drag a message
  out of Outlook, or use *File → Save As*). This needs one free add-on package;
  see [Reading Outlook .msg files](#reading-outlook-msg-files) below.

Only PDFs are pulled out — Word documents, images, and other attachments are
left alone.

---

## Quick start (no command line)

1. **Install Python** (one time). Download it from
   <https://www.python.org/downloads/> and, on the first setup screen, tick
   **"Add Python to PATH"**, then click *Install Now*.
2. **Download this folder** to your PC (e.g. to your Desktop).
3. **Double-click `Extract PDFs.bat`.**
   A small window walks you through two steps:
   - choose the folder that holds your saved emails, then
   - choose where to save the extracted PDFs.
4. When it finishes it tells you how many PDFs it saved and where.

### Even quicker: drag and drop

Drag a **folder of emails** (or one or more email files) directly **onto
`Extract PDFs.bat`**. The PDFs are written to a new `_Extracted_PDFs` folder
right next to what you dropped.

---

## Using it from a command prompt

Open a Command Prompt in this folder and run:

```bat
python extract_pdfs.py "C:\Path\To\Emails" -o "C:\Path\To\Save\PDFs"
```

Common variations:

```bat
:: Search sub-folders too
python extract_pdfs.py "C:\Emails" -o "C:\PDFs" --recursive

:: Keep provenance: one sub-folder of PDFs per source email
python extract_pdfs.py "C:\Emails" -o "C:\PDFs" --group-by-email

:: Preview only -- list what WOULD be extracted, write nothing
python extract_pdfs.py "C:\Emails" --dry-run

:: Process specific files
python extract_pdfs.py message1.eml message2.msg -o "C:\PDFs"
```

### Options

| Option | What it does |
| --- | --- |
| `-o`, `--output` | Folder to save PDFs into. Default: an `_Extracted_PDFs` folder next to the input. |
| `-r`, `--recursive` | Also look inside sub-folders. |
| `--group-by-email` | Put each email's PDFs in their own sub-folder (named after the email). Good for keeping track of which PDF came from which message. |
| `--prefix-source` | Prefix each PDF's file name with the source email's name. |
| `--overwrite` | Overwrite files that already exist instead of adding ` (1)`, ` (2)`, … |
| `--dry-run` | Show what would be extracted without writing anything. |
| `--gui` | Force the point-and-click window even when you pass arguments. |

Run `python extract_pdfs.py --help` to see them all.

---

## How it decides something is a PDF

An attachment is saved if **any** of these is true, so PDFs are caught even
when a sender mislabels them:

- its type is `application/pdf` (or a common PDF variant), **or**
- its file name ends in `.pdf`, **or**
- its contents begin with the `%PDF-` marker that every PDF starts with.

It also looks **inside forwarded emails** (an email attached to another email),
so PDFs buried in a forward are extracted too.

### Names and duplicates are handled safely

- Nothing is ever overwritten by default: if `scan.pdf` already exists, the
  next one is saved as `scan (1).pdf`, then `scan (2).pdf`, and so on.
- Accented and non‑English file names (e.g. `Mise en demeure – Réponse.pdf`)
  are preserved.
- Attachment names are cleaned of characters Windows doesn't allow, and any
  attempt by an attachment name to write outside your chosen output folder is
  neutralised.

---

## Reading Outlook `.msg` files

`.eml` files work with a plain Python install. To also read Outlook `.msg`
files, install one free package (one time). In a Command Prompt in this folder:

```bat
pip install extract-msg
```

Or install every optional extra at once (including support for `winmail.dat`
attachments) with:

```bat
pip install -r requirements.txt
```

If you run the tool on `.msg` files without this installed, it simply skips
them and reminds you of the command above — your `.eml` files are still
processed.

---

## Troubleshooting

- **"Python was not found."** Install Python from the link above and make sure
  **"Add Python to PATH"** is ticked during setup, then try again.
- **A graphical window doesn't appear.** Run it from a Command Prompt instead
  (see [above](#using-it-from-a-command-prompt)). This can happen on stripped‑down
  Python installs; the command-line mode always works.
- **`.msg` files are skipped.** Install `extract-msg` (see the section above).
- **Something went wrong with one email.** The tool keeps going and prints a
  list of any files it couldn't read at the end, so one bad email never stops
  the rest.

---

## What's in this folder

| File | Purpose |
| --- | --- |
| `extract_pdfs.py` | The program. |
| `Extract PDFs.bat` | Double-click / drag-and-drop launcher for Windows. |
| `requirements.txt` | Optional add-ons (`.msg` and `winmail.dat` support). |
| `README.md` | This file. |

---

## Note on confidential material

Email attachments in a professional setting are frequently confidential or
privileged. This tool runs **entirely on your own computer** — it does not send
anything over the internet. Keep the extracted PDFs somewhere consistent with
your organisation's file-handling rules.
