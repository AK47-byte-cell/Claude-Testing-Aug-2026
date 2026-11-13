"""Visual cues: desktop notifications and a shared status for the tray icon.

Windows: winotify if installed (click opens the file), otherwise a PowerShell toast.
macOS: osascript. Linux: notify-send. Every backend is best effort and never raises.
"""
from __future__ import annotations

import logging
import os
import shutil
import subprocess
import sys
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path

log = logging.getLogger(__name__)

APP_ID = "soundcore-sync"
# An AppUserModelID that always has a Start Menu entry, so Windows shows the toast.
_POWERSHELL_APP_ID = r"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe"

STATES = {
    "idle": "En attente",
    "scanning": "Recherche de l'enregistreur",
    "pulling": "Transfert depuis l'enregistreur",
    "transcribing": "Transcription en cours",
    "error": "Erreur (voir le journal)",
}


@dataclass
class Status:
    """What the tool is doing right now; the tray icon reads it, the loops write it."""

    state: str = "idle"
    text: str = ""
    changed_at: float = field(default_factory=time.time)
    _listeners: list[Callable[[Status], None]] = field(default_factory=list, repr=False)
    _lock: threading.Lock = field(default_factory=threading.Lock, repr=False)

    def set(self, state: str, text: str = "") -> None:
        with self._lock:
            self.state, self.text, self.changed_at = state, text, time.time()
            listeners = list(self._listeners)
        for cb in listeners:
            try:
                cb(self)
            except Exception:
                log.debug("status listener failed", exc_info=True)

    def listen(self, cb: Callable[[Status], None]) -> None:
        with self._lock:
            self._listeners.append(cb)

    @property
    def label(self) -> str:
        base = STATES.get(self.state, self.state)
        return f"{base} : {self.text}" if self.text else base


def open_path(path: Path | str) -> None:
    """Open a file or folder with the desktop's default application."""
    path = str(path)
    try:
        if sys.platform == "win32":
            os.startfile(path)
        elif sys.platform == "darwin":
            subprocess.Popen(["open", path])
        else:
            subprocess.Popen(["xdg-open", path])
    except OSError as exc:
        log.warning("Could not open %s: %s", path, exc)


class Notifier:
    def __init__(self, enabled: bool = True, status: Status | None = None):
        self.enabled = enabled
        self.status = status or Status()
        self._backend = self._pick_backend() if enabled else self._noop
        self._lock = threading.Lock()

    # ---- status shortcuts -------------------------------------------------------------------

    def state(self, state: str, text: str = "") -> None:
        self.status.set(state, text)

    # ---- notifications ----------------------------------------------------------------------

    def send(self, title: str, message: str, open_on_click: Path | None = None) -> None:
        if not self.enabled:
            return
        with self._lock:
            try:
                self._backend(title, message, open_on_click)
            except Exception as exc:  # noqa: BLE001 - never let a toast break the work
                log.debug("notification failed: %s", exc)

    def _pick_backend(self):
        if sys.platform == "win32":
            try:
                import winotify  # noqa: F401

                return self._winotify
            except ImportError:
                return self._powershell
        if sys.platform == "darwin":
            return self._osascript
        if shutil.which("notify-send"):
            return self._notify_send
        return self._noop

    @staticmethod
    def _noop(title: str, message: str, open_on_click: Path | None) -> None:
        log.info("[notification] %s: %s", title, message)

    @staticmethod
    def _winotify(title: str, message: str, open_on_click: Path | None) -> None:
        from winotify import Notification

        toast = Notification(app_id=APP_ID, title=title, msg=message, duration="short",
                             launch=str(open_on_click) if open_on_click else "")
        toast.show()

    @staticmethod
    def powershell_toast_script(title: str, message: str) -> str:
        q = lambda s: s.replace("'", "''")
        return (
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, "
            "ContentType = WindowsRuntime] | Out-Null; "
            "$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent("
            "[Windows.UI.Notifications.ToastTemplateType]::ToastText02); "
            "$x = $t.GetElementsByTagName('text'); "
            f"$x.Item(0).AppendChild($t.CreateTextNode('{q(title)}')) | Out-Null; "
            f"$x.Item(1).AppendChild($t.CreateTextNode('{q(message)}')) | Out-Null; "
            "$n = [Windows.UI.Notifications.ToastNotification]::new($t); "
            f"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{_POWERSHELL_APP_ID}').Show($n)"
        )

    @classmethod
    def _powershell(cls, title: str, message: str, open_on_click: Path | None) -> None:
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command",
             cls.powershell_toast_script(title, message)],
            creationflags=flags, timeout=15, check=False, capture_output=True,
        )

    @staticmethod
    def _osascript(title: str, message: str, open_on_click: Path | None) -> None:
        q = lambda s: s.replace("\\", "\\\\").replace('"', '\\"')
        subprocess.run(
            ["osascript", "-e", f'display notification "{q(message)}" with title "{q(title)}"'],
            timeout=15, check=False, capture_output=True,
        )

    @staticmethod
    def _notify_send(title: str, message: str, open_on_click: Path | None) -> None:
        subprocess.run(["notify-send", "-a", APP_ID, title, message], timeout=15, check=False)


def hms(seconds: float) -> str:
    s = int(seconds)
    h, m, sec = s // 3600, s % 3600 // 60, s % 60
    return f"{h} h {m:02d} min" if h else f"{m} min {sec:02d} s"
