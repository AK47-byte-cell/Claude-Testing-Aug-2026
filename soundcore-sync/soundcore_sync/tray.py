"""A small icon next to the clock: grey when idle, blue while transferring from the recorder,
orange while transcribing, red after an error. Needs the optional packages pystray and Pillow."""
from __future__ import annotations

import logging
import subprocess
import sys
import threading
from collections.abc import Callable
from pathlib import Path

from .notify import Status, open_path

log = logging.getLogger(__name__)

COLOURS = {
    "idle": (150, 150, 150),
    "scanning": (150, 150, 150),
    "pulling": (52, 120, 246),
    "transcribing": (240, 150, 30),
    "error": (220, 50, 50),
}


def available() -> bool:
    """True when pystray and Pillow are installed and a desktop is present (pystray raises a
    display error, not an ImportError, on a headless Linux)."""
    try:
        import PIL  # noqa: F401
        import pystray  # noqa: F401
    except Exception as exc:  # noqa: BLE001
        log.debug("tray icon unavailable: %s", exc)
        return False
    return True


def make_image(colour: tuple[int, int, int], size: int = 64):
    from PIL import Image, ImageDraw

    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = size // 8
    d.ellipse((pad, pad, size - pad, size - pad), fill=colour + (255,))
    # a small "microphone" bar in the middle
    w = size // 6
    d.rounded_rectangle((size // 2 - w // 2, size // 4, size // 2 + w // 2, size // 2 + size // 10),
                        radius=w // 2, fill=(255, 255, 255, 230))
    return img


def console_python() -> str:
    exe = Path(sys.executable)
    if exe.name.lower() == "pythonw.exe":
        return str(exe.with_name("python.exe"))
    return str(exe)


def open_review_console(config_path: Path | None) -> None:
    """Open a terminal window running `soundcore-sync review`."""
    cmd = [console_python(), "-m", "soundcore_sync"]
    if config_path:
        cmd += ["--config", str(config_path)]
    cmd.append("review")
    try:
        if sys.platform == "win32":
            subprocess.Popen(cmd, creationflags=subprocess.CREATE_NEW_CONSOLE)
        elif sys.platform == "darwin":
            script = " ".join(f"'{c}'" for c in cmd)
            subprocess.Popen(["osascript", "-e", f'tell application "Terminal" to do script "{script}"'])
        else:
            subprocess.Popen(["x-terminal-emulator", "-e", *cmd])
    except OSError as exc:
        log.warning("Could not open the review console: %s", exc)


class Tray:
    def __init__(self, status: Status, *, inbox: Path, log_file: Path | None, config_path: Path | None,
                 pending_count: Callable[[], int], on_quit: Callable[[], None]):
        self.status = status
        self.inbox = inbox
        self.log_file = log_file
        self.config_path = config_path
        self.pending_count = pending_count
        self.on_quit = on_quit
        self._icon = None
        self._stop = threading.Event()

    def _menu(self):
        import pystray

        def review_text(_item=None):
            n = self._safe_pending()
            return f"Confirmer les numéros de dossier ({n})" if n else "Confirmer les numéros de dossier"

        return pystray.Menu(
            pystray.MenuItem(lambda _i: "soundcore-sync : " + self.status.label, None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Ouvrir le dossier des enregistrements", lambda: open_path(self.inbox)),
            pystray.MenuItem("Ouvrir le journal", lambda: open_path(self.log_file) if self.log_file else None),
            pystray.MenuItem(review_text, lambda: open_review_console(self.config_path),
                             enabled=lambda _i: self._safe_pending() > 0),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quitter", self._quit),
        )

    def _safe_pending(self) -> int:
        try:
            return int(self.pending_count())
        except Exception:  # noqa: BLE001
            return 0

    def _quit(self, *_):
        self._stop.set()
        try:
            self.on_quit()
        finally:
            if self._icon is not None:
                self._icon.stop()

    def _refresh_loop(self):
        last = None
        while not self._stop.wait(1.5):
            key = (self.status.state, self.status.text, self._safe_pending())
            if key == last or self._icon is None:
                continue
            last = key
            try:
                self._icon.icon = make_image(COLOURS.get(self.status.state, COLOURS["idle"]))
                self._icon.title = "soundcore-sync : " + self.status.label
                self._icon.update_menu()
            except Exception:
                log.debug("tray refresh failed", exc_info=True)

    def run(self) -> None:
        """Blocks until Quit is chosen. Must run on the main thread on macOS."""
        import pystray

        self._icon = pystray.Icon("soundcore-sync", make_image(COLOURS["idle"]),
                                  "soundcore-sync : " + self.status.label, self._menu())
        threading.Thread(target=self._refresh_loop, name="tray-refresh", daemon=True).start()
        self._icon.run()
        self._stop.set()
