"""The list of client files (dossiers), read from OneDrive, and name matching against a transcript.

A matter is identified by a file number of the form 1234-56. The list can be:
  * an Excel workbook (.xlsx / .xlsm) with a column of file numbers and a column of client or matter
    names; the columns are found automatically, every other text in the row becomes a keyword;
  * a folder whose sub-folders or files are named "1234-56 Tremblay c. Assurances XYZ";
  * a CSV / text file with one matter per line: "1234-56, Tremblay c. Assurances XYZ, extra, keywords".
The configured path may contain * or ?; the most recent matching file is used, so a list that is
renamed with the date ("2026-11-04 AKS Liste dossiers.xlsm") keeps working.
"""
from __future__ import annotations

import csv
import glob
import logging
import math
import re
import shutil
import tempfile
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
        path = resolve_source(Path(source))
        if path is None:
            log.warning("Matter list not found: %s", source)
            return cls([])
        if path.is_dir():
            return cls(list(_from_folder(path)))
        if path.suffix.lower() in (".xlsx", ".xlsm", ".xltx", ".xltm"):
            return cls(list(_from_excel(path)))
        return cls(list(_from_csv(path)))


def resolve_source(source: Path) -> Path | None:
    """The file or folder to read. A pattern with * or ? resolves to the most recent match
    (the latest date in the name, then the latest modification time)."""
    text = str(source)
    if not any(ch in text for ch in "*?["):
        return source if source.exists() else None
    matches = [Path(m) for m in glob.glob(text) if not Path(m).name.startswith("~$")]
    if not matches:
        return None

    def key(p: Path):
        m = re.search(r"(\d{4}-\d{2}-\d{2})", p.name)
        try:
            mtime = p.stat().st_mtime
        except OSError:
            mtime = 0.0
        return (m.group(1) if m else "", mtime)

    return max(matches, key=key)


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


# ---- Excel -----------------------------------------------------------------------------------

_LABEL_HEADER_PRIORITY = (
    re.compile(r"\bclient"), re.compile(r"intitul|titre|nom du dossier|objet|description|matter"),
    re.compile(r"\bnom\b|\bname\b"), re.compile(r"partie|parties|demandeur|d[eé]fendeur|assur"),
)
_NUMBER_HEADER_RE = re.compile(r"dossier|num|n°|\bno\b|file|matter")
MAX_EXCEL_ROWS = 50_000


def _cell_text(value) -> str:
    if value is None:
        return ""
    if isinstance(value, float) and value.is_integer():
        value = int(value)
    return str(value).strip()


def _is_number_cell(value) -> bool:
    text = _cell_text(value)
    return bool(text) and FILE_NUMBER_RE.fullmatch(text) is not None


def _load_rows(path: Path):
    """Yield (sheet_title, rows) with rows as lists of cell values. Reads a temporary copy so a
    workbook open in Excel or being synced still loads."""
    try:
        import openpyxl
    except ImportError:
        log.warning("Reading %s needs the openpyxl package (pip install openpyxl)", path.name)
        return
    with tempfile.TemporaryDirectory() as tmp:
        copy = Path(tmp) / path.name
        shutil.copyfile(path, copy)
        wb = openpyxl.load_workbook(copy, read_only=True, data_only=True)
        try:
            for ws in wb.worksheets:
                rows = []
                for i, row in enumerate(ws.iter_rows(values_only=True)):
                    if i >= MAX_EXCEL_ROWS:
                        break
                    rows.append(list(row))
                yield ws.title, rows
        finally:
            wb.close()


def _find_number_column(rows) -> int | None:
    counts: dict[int, int] = {}
    for row in rows[:400]:
        for i, v in enumerate(row):
            if _is_number_cell(v):
                counts[i] = counts.get(i, 0) + 1
    if not counts:
        return None
    col, n = max(counts.items(), key=lambda kv: kv[1])
    return col if n >= 1 else None


def _find_header(rows, number_col: int) -> tuple[int, list[str]]:
    """Index of the header row (the last row before the first file number, if it holds text) and its
    cells, lower-cased and accent-stripped."""
    first_data = next((i for i, r in enumerate(rows) if number_col < len(r) and _is_number_cell(r[number_col])), 0)
    for i in range(first_data - 1, -1, -1):
        cells = [normalise(_cell_text(v)) for v in rows[i]]
        if any(cells) and (number_col < len(cells) and _NUMBER_HEADER_RE.search(cells[number_col] or "") or
                           sum(1 for c in cells if c) >= 2):
            return i, cells
    return -1, []


def _find_label_column(rows, number_col: int, header: list[str]) -> int | None:
    for pattern in _LABEL_HEADER_PRIORITY:
        for i, h in enumerate(header):
            if i != number_col and h and pattern.search(h):
                return i
    # No telling header: the first column after the number whose cells are mostly text.
    width = max((len(r) for r in rows[:400]), default=0)
    for i in list(range(number_col + 1, width)) + list(range(number_col)):
        texts = [_cell_text(r[i]) for r in rows[:400] if i < len(r)]
        texts = [t for t in texts if t]
        if len(texts) >= 2 and sum(1 for t in texts if re.search(r"[a-zA-Z]{3,}", t)) >= 0.7 * len(texts):
            return i
    return None


def _from_excel(path: Path):
    for title, rows in _load_rows(path):
        number_col = _find_number_column(rows)
        if number_col is None:
            continue
        header_idx, header = _find_header(rows, number_col)
        label_col = _find_label_column(rows, number_col, header)
        found = 0
        for i, row in enumerate(rows):
            if i <= header_idx or number_col >= len(row) or not _is_number_cell(row[number_col]):
                continue
            number = _cell_text(row[number_col])
            label = _cell_text(row[label_col]) if label_col is not None and label_col < len(row) else ""
            keywords = []
            for j, v in enumerate(row):
                if j in (number_col, label_col):
                    continue
                text = _cell_text(v)
                if len(text) >= 3 and re.search(r"[a-zA-Z]{3,}", text) and not re.match(r"^\d{4}-\d{2}-\d{2}", text):
                    keywords.append(text[:120])
            yield Matter(number, label or number, tuple(keywords[:10]))
            found += 1
        if found:
            log.info("Matter list: %d file(s) from sheet '%s' of %s (number column %d, name column %s)",
                     found, title, path.name, number_col + 1, (label_col + 1) if label_col is not None else "-")
            return
    log.warning("No column of file numbers (1234-56) found in %s", path.name)
