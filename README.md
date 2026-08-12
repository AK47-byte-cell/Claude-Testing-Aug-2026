# Email PDF Extractor

A small Windows app that scans a folder of saved emails and pulls every
**attached (or inline) PDF** out of them into a separate folder — so you don't
have to open each email and "Save As" one attachment at a time.

It reads:

- **`.eml`** files — the standard email format (Outlook, Thunderbird, Apple
  Mail, Gmail "Show original / Download message", most mail servers).
- **`.msg`** files — Outlook's own format (what you get when you drag a message
  out of Outlook, or use *File → Save As*). **Built into the app** — nothing
  extra to install.

Only PDFs are pulled out — Word documents, images, and other attachments are
left alone.

---

## Get the app (recommended)

You do **not** need Python or a command line. A ready-made Windows program is
built automatically.

1. Go to the **[Releases page](../../releases/latest)** and download
   **`EmailPDFExtractor.exe`**
   ([direct link](../../releases/latest/download/EmailPDFExtractor.exe)).
2. Double-click it. The app window opens.
3. Choose your emails folder, choose where to save the PDFs, and click
   **Extract PDFs**.

That's the whole thing — a single `.exe` you can keep on your Desktop or copy
onto a USB stick. Windows SmartScreen may warn the first time because the app
isn't code-signed; choose *More info → Run anyway*.

> The first build takes a few minutes after code is pushed. If the Releases
> page is empty, the build is still running — check the repository's **Actions**
> tab, or build it yourself (see [below](#build-the-app-yourself)).

---

## Using the app

The window has three things to set:

1. **Emails folder** — the folder that holds your saved `.eml` / `.msg` files.
2. **Save PDFs to** — where the extracted PDFs should go (it suggests an
   `_Extracted_PDFs` folder for you).
3. **Keep each email's PDFs in their own sub-folder** — on by default, so you
   can always tell which email each PDF came from. Turn it off to put every PDF
   in one flat folder.

Click **Extract PDFs**. A progress bar runs, the results appear in the log,
and when it's done you can click **Open output folder**. Sub-folders are
searched automatically.

---

## Reading Outlook `.msg` files

The downloaded `.exe` already handles `.msg` files (and `winmail.dat` / TNEF
attachments) — nothing to install.

You only need to install anything if you run the **Python script** directly
(see below) *and* have `.msg` files; in that case:

```bat
pip install extract-msg
```

If a `.msg` file is met without that installed, the script simply skips it and
tells you this command — your `.eml` files are still processed.

---

## Running from Python instead (optional)

If you'd rather not use the `.exe`, the same program runs as a plain Python
script (Python 3.8+). Double-click **`Extract PDFs.bat`** for the same window,
drag a folder onto it, or use the command line:

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
| `--group-by-email` | Put each email's PDFs in their own sub-folder (named after the email). |
| `--prefix-source` | Prefix each PDF's file name with the source email's name. |
| `--overwrite` | Overwrite files that already exist instead of adding ` (1)`, ` (2)`, … |
| `--max-mb` | Skip any single email file larger than this many MB (default `1024`; `0` = no limit). |
| `--dry-run` | Show what would be extracted without writing anything. |

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
- Accented and non-English file names (e.g. `Mise en demeure – Réponse.pdf`)
  are preserved.
- Attachment names are cleaned of characters Windows doesn't allow, and any
  attempt by an attachment name to write outside your chosen output folder is
  neutralised.

---

## Build the app yourself

You normally don't need to — GitHub builds `EmailPDFExtractor.exe` for you (see
[`.github/workflows/build-windows.yml`](.github/workflows/build-windows.yml)).
But if you want to build it on your own Windows PC, with Python installed just
double-click **`build_windows_exe.bat`**. It installs the build tools and
produces `dist\EmailPDFExtractor.exe`.

---

## Troubleshooting

- **Windows SmartScreen blocks it.** The app isn't code-signed. Click
  *More info → Run anyway*. (It runs entirely on your computer; see the privacy
  note below.)
- **The Releases page has no file yet.** The automatic build is still running —
  check the **Actions** tab, or build it yourself.
- **Using the Python script and `.msg` files are skipped.** Install `extract-msg`
  (`pip install extract-msg`). The downloaded `.exe` doesn't need this.
- **Something went wrong with one email.** The app keeps going and lists any
  files it couldn't read at the end, so one bad email never stops the rest.

---

## What's in this repository

| File | Purpose |
| --- | --- |
| `extract_pdfs.py` | The program (GUI + command line). |
| `Extract PDFs.bat` | Double-click / drag-and-drop launcher for the Python version. |
| `EmailPDFExtractor.spec` | PyInstaller recipe that builds the `.exe`. |
| `build_windows_exe.bat` | Build the `.exe` yourself on Windows. |
| `.github/workflows/build-windows.yml` | Builds and publishes the `.exe` automatically. |
| `app.ico` | The app's icon. |
| `selftest.py` | Built-in checks (`python selftest.py`) that verify it works. |
| `requirements.txt` | Optional add-ons for the Python version. |

---

## Note on confidential material

Email attachments in a professional setting are frequently confidential or
privileged. This app runs **entirely on your own computer** — it does not send
anything over the internet. Keep the extracted PDFs somewhere consistent with
your organisation's file-handling rules.
