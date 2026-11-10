"""Short subject titles for recordings, produced on this machine.

Two engines, both local:
  * HeuristicSubjectEngine: the first sentence of the dictation, minus the file number and filler words.
    Whisper's transcript is all it needs. This is the default.
  * OllamaSubjectEngine: a small language model served by Ollama at http://127.0.0.1:11434 reads the
    start of the transcript and writes a title. Optional; when Ollama is not running the heuristic is used.
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
from .renamer import sanitise, words_to_digits

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
_THINK_RE = re.compile(r"<think>.*?</think>", re.DOTALL | re.IGNORECASE)
_THINKING_MODELS = ("qwen3", "deepseek-r1", "magistral", "gpt-oss")

# What people say before getting to the point.
_FILLERS = {
    "alors", "bon", "donc", "euh", "ok", "okay", "voila", "voilà", "bonjour", "allo", "allô", "hum", "um", "uh",
    "so", "well", "right", "hello", "hi", "yes", "oui", "non", "no", "note", "dictee", "dictée", "dictation",
    "memo", "mémo", "ceci", "this", "est", "is", "une", "un", "a", "an", "la", "le", "les", "the",
}
_NUMBER_PHRASE_RE = re.compile(
    r"\b(dossier|dossiers|file|files|matter|num[eé]ro|number|no\.?|n°)\b\s*(?:\d[\d\s-]*\d)?", re.IGNORECASE
)
_SENTENCE_SPLIT_RE = re.compile(r"(?<=[.!?;:])\s+|\n+")


class SubjectEngine(Protocol):
    def title(self, text: str, language: str | None) -> str | None: ...


def clean_title(raw: str | None, max_words: int = 8) -> str | None:
    """Turn the model's reply into a safe, short file-name fragment, or None if unusable."""
    if not raw:
        return None
    raw = _THINK_RE.sub("", raw)
    line = next((ln for ln in raw.strip().splitlines() if ln.strip()), "")
    line = _PREFIX_RE.sub("", line).strip().strip('"\'«»“”*_`').strip()
    line = FILE_NUMBER_RE.sub("", line)
    line = sanitise(line, max_len=90)
    words = line.split()
    if not words:
        return None
    line = " ".join(words[:max_words]).rstrip(" .,;:-")
    return line or None


class HeuristicSubjectEngine:
    """The first thing said, once the file number and the small talk are removed."""

    name = "heuristic"

    def __init__(self, max_words: int = 8, head_chars: int = 600):
        self.max_words = max_words
        self.head_chars = head_chars

    def title(self, text: str, language: str | None = None) -> str | None:
        head = words_to_digits(text.strip()[: self.head_chars])
        head = _NUMBER_PHRASE_RE.sub(" ", head)
        head = FILE_NUMBER_RE.sub(" ", head)
        for sentence in _SENTENCE_SPLIT_RE.split(head):
            words = sentence.split()
            while words and re.sub(r"[^\w]", "", words[0]).lower() in _FILLERS:
                words.pop(0)
            words = [w for w in words if re.search(r"\w", w)]
            if len(words) >= 2:
                title = clean_title(" ".join(words), self.max_words)
                return title[0].upper() + title[1:] if title else None
        return None


class CompositeSubjectEngine:
    """Try the model first; fall back to the heuristic when it gives nothing."""

    name = "auto"

    def __init__(self, primary, fallback):
        self.primary = primary
        self.fallback = fallback

    def title(self, text: str, language: str | None = None) -> str | None:
        try:
            title = self.primary.title(text, language)
        except Exception:
            log.exception("Subject model failed; using the first sentence instead")
            title = None
        return title or self.fallback.title(text, language)


class OllamaSubjectEngine:
    name = "ollama"

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
        if any(family in self.model.lower() for family in _THINKING_MODELS):
            payload["think"] = False  # Qwen3 and friends otherwise spend the token budget "thinking"
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
