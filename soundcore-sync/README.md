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
  `name.txt` (plain paragraphs) and `name.md` (timestamped) are written beside it. Files that already
  have a `.txt` are left alone, so an existing backlog is not redone.
* A small database in `~/.soundcore-sync/` remembers what was pulled and transcribed, so nothing is
  done twice, even after a restart or a OneDrive re-sync.

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
   work OneDrive (shown as "Alexandra - GESTION GASLAW INC" in Explorer); `echo $env:OneDriveCommercial` in
   PowerShell prints the real path. Pick the model: `medium` is a
   good balance on a CPU; `large-v3` is better and slower; add `compute_type = "int8"` on a laptop
   without a GPU. Leave `language = ""` and each file is detected as French or English on its own.
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

## Development

```
pip install -e ".[dev]"
pytest
```

The tests cover configuration, the manifest, the frame builder and list parser, the key wrap and
chunk decryption, the Ogg/Opus container, a simulated end-to-end download through the client, and the
transcription loop with a stub engine. They need no Bluetooth hardware and no model download.

## Licence and credits

GPL-3.0-or-later, like the rest of this repository. The Bluetooth protocol and decryption code in
`soundcore_sync/ble/` is adapted from [andrewseago/d3200-ble-note-downloader](https://github.com/andrewseago/d3200-ble-note-downloader)
(GPL-3.0-or-later), building on protocol notes by [tacshi/Soundcore](https://github.com/tacshi/Soundcore)
and [doofusdavid/soundcore-pull](https://github.com/doofusdavid/soundcore-pull). Not affiliated with
Anker or soundcore.
