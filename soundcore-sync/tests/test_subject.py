import io
import json
import urllib.error

import pytest

from soundcore_sync.subject import OllamaSubjectEngine, clean_title


@pytest.mark.parametrize("raw, expected", [
    ('Titre : "Rencontre avec M. Tremblay"', "Rencontre avec M. Tremblay"),
    ("Title: Call with opposing counsel re: expert report.", "Call with opposing counsel re expert report"),
    ("Dictée dossier 1234-56 lettre à l'assureur\nautre ligne", "Dictée dossier lettre à l'assureur"),
    ("«Réunion de préparation / audience»", "Réunion de préparation audience"),
    ("one two three four five six seven eight nine ten", "one two three four five six seven eight"),
    ("", None),
    ("   \n  ", None),
    ("1234-56", None),
])
def test_clean_title(raw, expected):
    assert clean_title(raw) == expected


class FakeResponse(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def test_engine_builds_request_and_cleans_reply(monkeypatch):
    seen = {}

    def fake_urlopen(req, timeout):
        seen["url"] = req.full_url
        seen["body"] = json.loads(req.data)
        seen["timeout"] = timeout
        return FakeResponse(json.dumps({"message": {"content": ' "Appel avec Me Lavoie" '}}).encode())

    monkeypatch.setattr("urllib.request.urlopen", fake_urlopen)
    engine = OllamaSubjectEngine(model="m", url="http://127.0.0.1:11434/", max_chars=50, timeout=7, max_words=5)
    title = engine.title("Bonjour, ceci est une dictée assez longue pour être envoyée au modèle local.", "fr")
    assert title == "Appel avec Me Lavoie"
    assert seen["url"] == "http://127.0.0.1:11434/api/chat" and seen["timeout"] == 7
    body = seen["body"]
    assert body["model"] == "m" and body["stream"] is False
    prompt = body["messages"][0]["content"]
    assert "in French" in prompt and "at most 5 words" in prompt
    assert "Bonjour, ceci est une dictée assez longue pour" in prompt
    assert "envoyée au modèle" not in prompt  # cut at max_chars


def test_engine_skips_when_ollama_down(monkeypatch):
    calls = []

    def fake_urlopen(req, timeout):
        calls.append(1)
        raise urllib.error.URLError("connection refused")

    monkeypatch.setattr("urllib.request.urlopen", fake_urlopen)
    engine = OllamaSubjectEngine()
    assert engine.title("x" * 100, None) is None
    assert engine.title("x" * 100, None) is None  # backs off: no second call for a while
    assert len(calls) == 1


def test_engine_reports_model_errors(monkeypatch):
    monkeypatch.setattr("urllib.request.urlopen",
                        lambda req, timeout: FakeResponse(json.dumps({"error": "model not found"}).encode()))
    assert OllamaSubjectEngine().title("x" * 100, None) is None


def test_engine_ignores_tiny_text(monkeypatch):
    monkeypatch.setattr("urllib.request.urlopen", lambda req, timeout: (_ for _ in ()).throw(AssertionError))
    assert OllamaSubjectEngine().title("bonjour", None) is None
