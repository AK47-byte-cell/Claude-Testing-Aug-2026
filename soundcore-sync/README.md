# soundcore-sync

Pulls recordings off a **soundcore Work** (Anker D3200) voice recorder over Bluetooth, straight to
your computer, and transcribes them locally with **faster-whisper**. No soundcore app, no phone,
no cloud account. Point the inbox folder at OneDrive and the audio and transcripts are synced
like any other file.

```
record ──Bluetooth──▶ inbox folder (OneDrive) ──▶ transcript .txt / .md next to the audio
         automatic                                 automatic, in the background
```

Before: record, open the soundcore app, export each file to OneDrive by hand, start the Python
script, wait, fetch the transcript. After: record, put the recorder on the desk, read the transcript.

## How it works

* **Puller.** Every 20 seconds the program looks for the recorder over Bluetooth Low Energy.
  When the recorder is awake and not held by the phone, it connects, lists the recordings, downloads
  every one it has not seen before, decrypts it on the spot (the recorder stores audio encrypted) and
  writes a standard `.ogg` (Opus) file named after the recording time, for example
  `2026-11-04_14-35-07_soundcore.ogg`. Nothing is ever deleted from the recorder.
* **Transcriber.** The same program watches the inbox folder. Every new audio file (from the
  recorder or dropped there by any other means) is transcribed once it has stopped changing, and
  `name.txt` is written beside it with the same settings and clean-up as the previous batch script
  (medium model on CPU in int8, beam size 6, voice-activity filter, the bilingual legal-dictation
  prompt, no conditioning on previous text, punctuation clean-up, paragraphs of about 900
  characters). Set `write_markdown = true` to also get a timestamped `name.md`. Files that already
  have a `.txt` are left alone, so the existing backlog is not redone.
* **Renamer.** Once the transcript exists, the tool looks for the file number. If you said it in the
  recording ("dossier 1234-56", in digits or in words, with or without "tiret"), the audio and the
  transcript are renamed on the spot: `2026-11-05_14-35 - 1234-56 - Tremblay c. Assurances XYZ.ogg`.
  The client name comes from your list of files on OneDrive (`rename.matters_source`: a folder whose
  sub-folders start with the file number, or a CSV). If you did not say the number, the names in the
  recording are matched against that list; a confident, unambiguous match is recorded as a proposal
  (or applied, with `auto_on_name_match = true`), and `soundcore-sync review` lets you accept it, type
  another number, or reject it. The original name is kept, and `soundcore-sync undo "<file>"` restores
  it. Nothing is sent to any outside service.
* **Subject titles, on the laptop.** A short title is added to the name:
  `2026-11-05_14-35 - 1234-56 - Tremblay c. Assurances XYZ - Rencontre avec M. Tremblay.ogg`.
  Whisper only converts speech to text, so it cannot say what a recording is about; the title comes
  from one of two local sources. By default it is the **first sentence of the dictation**, minus the
  file number and the "alors, bon, dossier..." preamble, which works well when you start each recording
  by stating the subject ("Rencontre avec M. Tremblay au sujet du rapport d'expert"). Optionally, a
  small language model served by [Ollama](https://ollama.com) on the laptop reads the start of the
  transcript and writes a better title (`subject.backend = "auto"`), falling back to the first sentence
  when Ollama is not running. A recording with a title but no file number is named
  `2026-11-05_14-35 - Rencontre avec M. Tremblay.ogg` and still goes to `review` if the names suggested
  a file. The transcript never leaves the machine either way.
* A small database in `~/.soundcore-sync/` remembers what was pulled, transcribed and renamed, so
  nothing is done twice, even after a restart or a OneDrive re-sync.

Transfer speed is set by the recorder's Bluetooth link: roughly one minute of transfer per ten
minutes of audio. A one-hour meeting is on your disk about six minutes after you set the recorder
down.

## What you need to know about the recorder

* The recorder accepts **one Bluetooth connection at a time**. If the soundcore app on your phone is
  connected, the computer cannot see it. Close the app (on iPhone, which auto-launches it, remove the
  recorder from the app or uninstall the app) and the recorder becomes available.
* The recorder only advertises when it is **awake**: out of its charging case, or tapped.
* The USB-C port is charge-only, which is why Bluetooth is used.
* The protocol was reverse-engineered by the community. The download and decryption path was
  verified on real hardware by the author of the project this is built on, but **not on your unit by
  me**: the first run is the real test (see "First run" below). A firmware update from Anker could
  break it.
* The recorder's encryption gives you a single key per session and does not authenticate the
  computer; anything that can pair with it can read the audio. That is the recorder's design, not
  something this tool adds. Keep it close to the computer and away from public places while it syncs
  if that matters to you.

## Install (Windows)

1. Install Python 3.12 or newer from python.org (tick "Add python.exe to PATH").
2. In PowerShell, inside this folder:

   ```powershell
   py -3 -m venv .venv
   .venv\Scripts\pip install -e .
   .venv\Scripts\soundcore-sync init
   ```

3. Open `%USERPROFILE%\.soundcore-sync\config.toml` in Notepad and set `inbox` to the OneDrive folder
   you want. The default is `%OneDriveCommercial%\Dictation\Import`, the "Dictation > Import" folder of the
   work OneDrive (shown as "Alexandra - GESTION GASLAW INC" in Explorer, and
   `C:\Users\Akallos\OneDrive - GESTION GASLAW INC\Dictation\Import` on disk). The transcription
   settings default to those of the previous script; `large-v3` is more accurate and slower. Leave
   `language = ""` and each file is detected as French or English on its own.
4. Take the recorder out of its case, close the soundcore app on the phone, and check the link:

   ```powershell
   .venv\Scripts\soundcore-sync scan
   ```

5. Start it in the foreground once to watch it work (`Ctrl+C` stops it):

   ```powershell
   .venv\Scripts\soundcore-sync run
   ```

6. Make it start with Windows and run hidden in the background:

   ```powershell
   powershell -ExecutionPolicy Bypass -File install\windows\install-task.ps1
   ```

   `install\windows\uninstall-task.ps1` removes it. The log is at
   `%USERPROFILE%\.soundcore-sync\soundcore-sync.log`.

7. Optional, for model-written titles: install Ollama for Windows from <https://ollama.com/download>
   and set `backend = "auto"` under `[subject]`. Ollama starts with Windows and serves the model
   locally. Either `ollama pull qwen3:4b` (about 2.5 GB), or reuse the Qwen3-4B GGUF that Intel AI
   Playground already put on the laptop: create a text file `Modelfile` containing one line,
   `FROM C:\Users\Akallos\AppData\Local\Programs\AI Playground\resources\models\LLM\ggufLLM\unsloth---Qwen3-4B-GGUF\<the .gguf file>`,
   run `ollama create qwen3:4b -f Modelfile`, and no download is needed. (The OpenVINO folders are a
   different format and cannot be used by Ollama.) `.venv\Scripts\soundcore-sync subject --text "Dossier 1234-56, rencontre avec ..."`
   shows the title either backend would produce. A model title takes a few seconds to a minute on a CPU.

macOS: same steps with `python3 -m venv .venv` and `.venv/bin/...`; copy
`install/macos/com.soundcore-sync.plist` to `~/Library/LaunchAgents/` (edit the two paths) and grant
Bluetooth access to the Python binary when asked. Linux: `install/linux/soundcore-sync.service` as a
user unit; BlueZ must be running.

## First run

The first `run` downloads the Whisper model (a few hundred MB to 3 GB depending on the model) and
then starts both loops. With the recorder awake you should see, in order: "Recorder found",
"Connected", "Recorder lists N recording(s)", "Key exchange complete", then one "Downloading" and
"Saved" line per recording, then "Transcribing" and "Transcript written". If the key exchange fails
or a download does not complete, run with `-v` and send me the log: the frame layouts come from a
community reverse-engineering and a different firmware may shift them.

## Commands

| Command | What it does |
| --- | --- |
| `soundcore-sync run` | Run both loops until stopped (what the background task runs). `--no-pull` / `--no-transcribe` to run one. |
| `soundcore-sync pull` | Look for the recorder once and pull anything new. |
| `soundcore-sync transcribe` | Transcribe everything pending in the inbox once. `--file X` for one file. |
| `soundcore-sync scan` | Is the recorder visible right now? |
| `soundcore-sync status` | Counts and the last few results. |
| `soundcore-sync review` | Accept, correct or reject pending rename proposals (`--accept-all`, `--reject-all`). |
| `soundcore-sync undo "<file>"` | Give a renamed recording and its transcript their original name back. |
| `soundcore-sync matters` | Show the file list as read from OneDrive; `--text "..."` tests the matching on a sample transcript. |
| `soundcore-sync subject --file X.txt` | Ask the local model for a title for one transcript (or `--text "..."`). |
| `soundcore-sync init` | Write the config file and create the folders. |

All commands take `--config PATH` and `-v`.

## Configuration

See `config.example.toml`. The important ones:

| Key | Meaning |
| --- | --- |
| `folders.inbox` | Recordings land here and are watched here. Use a OneDrive folder. |
| `folders.transcripts` | Empty = next to the audio; or a separate folder. |
| `recorder.enabled` | Set to `false` on a machine that should only transcribe (for instance a server watching the OneDrive folder). |
| `transcription.model` / `device` / `compute_type` | faster-whisper settings. |
| `transcription.language` | `""` auto-detect per file, or `fr` / `en`. |
| `transcription.settle_seconds` | A file must be unchanged this long before it is used (OneDrive writes progressively). |
| `rename.matters_source` | Your list of files: a folder of "1234-56 Client" sub-folders or a CSV. Without it only spoken numbers can be used. |
| `rename.mode` | `auto` renames when confident, `propose` only records proposals, `off`. |
| `rename.auto_on_name_match` | Also rename automatically when only the names matched (default: propose). |
| `rename.pattern` | Order of the name parts; missing parts are dropped. Default `{date} - {number} - {label} - {subject}`. |
| `rename.subject_only` | Rename with just the title when no file number was found (default: yes). |
| `subject.backend` | `heuristic` (first sentence, default), `ollama` (local model), `auto` (model, else first sentence), `off`. |
| `subject.model` | Ollama model for `ollama`/`auto`; `qwen3:4b` by default. |

## Development

```
pip install -e ".[dev]"
pytest
```

The tests cover configuration, the manifest, the frame builder and list parser, the key wrap and
chunk decryption, the Ogg/Opus container, a simulated end-to-end download through the client, the
transcription loop with a stub engine, the matter list, spoken-number detection (digits and words,
French and English), the renaming rules and the local title engine (against a fake Ollama). They need no Bluetooth hardware and no model download.

## Licence and credits

GPL-3.0-or-later, like the rest of this repository. The Bluetooth protocol and decryption code in
`soundcore_sync/ble/` is adapted from [andrewseago/d3200-ble-note-downloader](https://github.com/andrewseago/d3200-ble-note-downloader)
(GPL-3.0-or-later), building on protocol notes by [tacshi/Soundcore](https://github.com/tacshi/Soundcore)
and [doofusdavid/soundcore-pull](https://github.com/doofusdavid/soundcore-pull). Not affiliated with
Anker or soundcore.
