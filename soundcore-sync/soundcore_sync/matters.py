"""The list of client files (dossiers), read from OneDrive, and name matching against a transcript.

A matter is identified by a file number of the form 1234-56. The list can be:
  * a folder whose sub-folders or files are named "1234-56 Tremblay c. Assurances XYZ" (any separator
    after the number), which is the usual way a firm's OneDrive is organised; or
  * a CSV / text file with one matter per line: "1234-56, Tremblay c. Assurances XYZ, extra, keywords".
"""
from __future__ import annotations

import csv
import logging
import math
import re
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

log = logging.getLogger(__name__)

FILE_NUMBER_RE = re.compile(r"(?<!\d)(\d{4})-(\d{2})(?!\d)")
_LEADING_NUMBER_RE = re.compile(r"^\s*(\d{4}-\d{2})\s*[-_–—:.,]*\s*(.*?)\s*$")

# Words that appear in many matter names and say nothing about which one is meant.
STOPWORDS = {
    "inc", "ltee", "ltd", "cie", "co", "corp", "corporation", "compagnie", "company", "societe", "society",
    "groupe", "group", "les", "des", "une", "the", "and", "for", "del", "von", "van", "der",
    "assurance", "assurances", "insurance", "mutuelle", "generale", "general", "canada", "quebec", "montreal",
    "ville", "city", "succession", "estate", "dossier", "file", "contre", "versus", "autres", "others",
    "construction", "constructions", "entreprises", "entreprise", "enterprises", "services", "service",
    "gestion", "management", "immobilier", "immobiliere", "investissements", "holdings", "holding",
    "capital", "international", "national", "transport", "transports", "industries", "industrie",
    "maitre", "monsieur", "madame", "docteur", "mme", "mlle",
}


def normalise(text: str) -> str:
    """Lower-case, strip accents, keep letters/digits/spaces."""
    text = unicodedata.normalize("NFKD", text)
    text = "".join(ch for ch in text if not unicodedata.combining(ch))
    text = text.lower().replace("'", " ").replace("’", " ")
    return re.sub(r"[^a-z0-9]+", " ", text)


def tokens_of(text: str, min_len: int = 3) -> set[str]:
    return {t for t in normalise(text).split() if len(t) >= min_len and not t.isdigit() and t not in STOPWORDS}


@dataclass(frozen=True)
class Matter:
    number: str
    label: str
    keywords: tuple[str, ...] = ()

    @property
    def tokens(self) -> frozenset[str]:
        return frozenset(tokens_of(self.label) | {t for k in self.keywords for t in tokens_of(k)})


@dataclass
class Match:
    matter: Matter
    score: float
    matched: list[str] = field(default_factory=list)


class MatterIndex:
    def __init__(self, matters: list[Matter]):
        self.matters: dict[str, Matter] = {}
        for m in matters:
            if m.number in self.matters:
                existing = self.matters[m.number]
                self.matters[m.number] = Matter(m.number, existing.label, existing.keywords + (m.label,) + m.keywords)
            else:
                self.matters[m.number] = m
        self._df: dict[str, int] = {}
        for m in self.matters.values():
            for t in m.tokens:
                self._df[t] = self._df.get(t, 0) + 1

    def __len__(self) -> int:
        return len(self.matters)

    def get(self, number: str) -> Matter | None:
        return self.matters.get(number)

    def idf(self, token: str) -> float:
        n = max(len(self.matters), 1)
        return math.log(n / self._df.get(token, 1)) + 1.0

    def unique_word_score(self) -> float:
        """The score of a single word that occurs in exactly one matter's name."""
        return math.log(max(len(self.matters), 1)) + 1.0

    def match_names(self, text: str, limit: int = 5) -> list[Match]:
        """Score every matter by the distinctive words of its name found in ``text``."""
        words = tokens_of(text)
        if not words or not self.matters:
            return []
        results: list[Match] = []
        for m in self.matters.values():
            hits = sorted(m.tokens & words)
            if hits:
                results.append(Match(m, sum(self.idf(t) for t in hits), hits))
        results.sort(key=lambda r: (-r.score, r.matter.number))
        return results[:limit]

    # ---- loading ------------------------------------------------------------------------

    @classmethod
    def load(cls, source: Path | None) -> MatterIndex:
        if source is None:
            return cls([])
        source = Path(source)
        if source.is_dir():
            return cls(list(_from_folder(source)))
        if source.is_file():
            return cls(list(_from_csv(source)))
        log.warning("Matter list not found: %s", source)
        return cls([])


def _from_folder(folder: Path):
    try:
        entries = sorted(folder.iterdir())
    except OSError as exc:
        log.warning("Cannot read the matter folder %s: %s", folder, exc)
        return
    for entry in entries:
        m = _LEADING_NUMBER_RE.match(entry.stem if entry.is_file() else entry.name)
        if m:
            yield Matter(m.group(1), m.group(2) or entry.name)


def _from_csv(path: Path):
    with open(path, encoding="utf-8-sig", newline="") as fh:
        sample = fh.read(4096)
        fh.seek(0)
        delimiter = max(";,\t", key=sample.count)
        for row in csv.reader(fh, delimiter=delimiter):
            cells = [c.strip() for c in row if c.strip()]
            if not cells:
                continue
            m = FILE_NUMBER_RE.search(cells[0])
            if not m:
                continue
            number = f"{m.group(1)}-{m.group(2)}"
            rest = cells[1:] or [cells[0].replace(m.group(0), "").strip(" -_:,")]
            yield Matter(number, rest[0] or number, tuple(rest[1:]))
