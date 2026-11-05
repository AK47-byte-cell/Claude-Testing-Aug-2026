"""Short subject titles for recordings, generated on this machine by a local language model (Ollama).

Nothing leaves the laptop: Ollama serves the model at http://127.0.0.1:11434. If it is not installed
or not running, titles are simply skipped and names fall back to the client name from the matter list.
"""
from __future__ import annotations

import json
import logging
import re
import time
import urllib.error
import urllib.request
from typing import Protocol

from .matters import FILE_NUMBER_RE
from .renamer import sanitise

log = logging.getLogger(__name__)

PROMPT = (
    "You name audio recordings for a law office. Below is the beginning of a transcript{lang}.\n"
    "Reply with ONE short title only, at most {max_words} words, in the same language as the transcript, "
    "saying what kind of recording it is (meeting, call, dictation, note, hearing...) and its main subject "
    "or the person it concerns. No file number, no date, no quotation marks, no explanation, no final "
    "period.\n\nTranscript:\n{text}\n\nTitle:"
)
LANG_NAMES = {"fr": " in French", "en": " in English"}
_PREFIX_RE = re.compile(r"^\s*(titre|title|sujet|subject)\s*[:：-]\s*", re.IGNORECASE)


class SubjectEngine(Protocol):
    def title(self, text: str, language: str | None) -> str | None: ...


def clean_title(raw: str | None, max_words: int = 8) -> str | None:
    """Turn the model's reply into a safe, short file-name fragment, or None if unusable."""
    if not raw:
        return None
    line = next((ln for ln in raw.strip().splitlines() if ln.strip()), "")
    line = _PREFIX_RE.sub("", line).strip().strip('"\'«»“”*_`').strip()
    line = FILE_NUMBER_RE.sub("", line)
    line = sanitise(line, max_len=90)
    words = line.split()
    if not words:
        return None
    line = " ".join(words[:max_words]).rstrip(" .,;:-")
    return line or None


class OllamaSubjectEngine:
    def __init__(self, model: str = "qwen2.5:3b", url: str = "http://127.0.0.1:11434",
                 max_chars: int = 3000, timeout: float = 120.0, max_words: int = 8):
        self.model = model
        self.url = url.rstrip("/")
        self.max_chars = max_chars
        self.timeout = timeout
        self.max_words = max_words
        self._unavailable_until = 0.0

    def _post(self, payload: dict) -> dict:
        req = urllib.request.Request(
            self.url + "/api/chat", data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"}, method="POST",
        )
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def title(self, text: str, language: str | None = None) -> str | None:
        if time.time() < self._unavailable_until:
            return None
        excerpt = text.strip()[: self.max_chars]
        if len(excerpt) < 20:
            return None
        prompt = PROMPT.format(lang=LANG_NAMES.get(language or "", ""), max_words=self.max_words, text=excerpt)
        payload = {
            "model": self.model, "stream": False,
            "messages": [{"role": "user", "content": prompt}],
            "options": {"temperature": 0.2, "num_predict": 48},
        }
        try:
            reply = self._post(payload)
        except urllib.error.URLError as exc:
            self._unavailable_until = time.time() + 600
            log.warning("Ollama not reachable at %s (%s); subject titles skipped for 10 min. "
                        "Install Ollama and run: ollama pull %s", self.url, exc.reason, self.model)
            return None
        except (OSError, ValueError) as exc:
            log.warning("Ollama request failed: %s", exc)
            return None
        if "error" in reply:
            log.warning("Ollama error: %s (is the model pulled? ollama pull %s)", reply["error"], self.model)
            self._unavailable_until = time.time() + 600
            return None
        content = (reply.get("message") or {}).get("content", "")
        title = clean_title(content, self.max_words)
        log.debug("Ollama title: %r -> %r", content, title)
        return title
