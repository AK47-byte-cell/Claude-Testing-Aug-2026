"""Post-transcription renaming: find the file number (spoken, or inferred from the names in the
transcript matched against the matter list) and rename the audio and transcript accordingly.

Nothing leaves the machine. The original name is kept in the manifest so a rename can be undone.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from pathlib import Path

from .matters import FILE_NUMBER_RE, Match, MatterIndex, normalise

log = logging.getLogger(__name__)

KEYWORDS_RE = re.compile(
    r"\b(dossier|dossiers|file|files|matter|num[eé]ro|number|no\.?|n°|#)\b", re.IGNORECASE
)
DATE_STEM_RE = re.compile(r"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2})")
_FORBIDDEN = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

# ---- numbers spoken in words --------------------------------------------------------------

_UNITS = {
    "zero": 0, "un": 1, "une": 1, "deux": 2, "trois": 3, "quatre": 4, "cinq": 5, "six": 6, "sept": 7,
    "huit": 8, "neuf": 9, "dix": 10, "onze": 11, "douze": 12, "treize": 13, "quatorze": 14, "quinze": 15,
    "seize": 16,
    "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "seven": 7, "eight": 8, "nine": 9, "ten": 10,
    "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15, "sixteen": 16,
    "seventeen": 17, "eighteen": 18, "nineteen": 19,
}
_TENS = {
    "vingt": 20, "vingts": 20, "trente": 30, "quarante": 40, "cinquante": 50, "soixante": 60,
    "twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80,
    "ninety": 90,
}
_SCALES = {"cent": 100, "cents": 100, "hundred": 100, "mille": 1000, "thousand": 1000}
_GLUE = {"et", "and"}


def words_to_digits(text: str) -> str:
    """Replace numbers written in French or English words with digits ("douze trente-quatre" -> "12 34").

    Each spoken group becomes its own number, so "douze trente-quatre cinquante-six" gives "12 34 56",
    which the file-number patterns then recognise. Everything else is left untouched.
    """
    out: list[str] = []
    group: list[int] = []      # components of the number being built
    last_kind = None           # "unit" | "tens" | "teen" | "scale" | "glue"

    def flush():
        nonlocal group, last_kind
        if group:
            out.append(str(sum(group)))
        group, last_kind = [], None

    for raw in text.split():
        word = normalise(raw).strip()
        parts = [p for p in re.split(r"[\s-]+", word) if p] if word else []
        if not parts:
            flush()
            out.append(raw)
            continue
        hyphenated = "-" in raw.strip(".,;:!?")
        numeric_parts = all(p in _UNITS or p in _TENS or p in _SCALES or p in _GLUE for p in parts)
        if not numeric_parts:
            flush()
            out.append(raw)
            continue
        if not hyphenated and last_kind in ("unit", "teen") and parts[0] in _UNITS:
            flush()  # "deux trois" are two separate numbers
        for p in parts:
            if p in _GLUE:
                if group:
                    last_kind = "glue"
                else:
                    out.append(raw)
                continue
            if p in _SCALES:
                scale = _SCALES[p]
                if group:
                    small = group.pop() if last_kind in ("unit", "teen", "tens") else 1
                    group.append(small * scale)
                else:
                    group.append(scale)
                last_kind = "scale"
            elif p in _TENS:
                tens = _TENS[p]
                if p in ("vingt", "vingts") and last_kind == "unit" and group and group[-1] == 4:
                    group[-1] = 80
                elif last_kind in ("tens", "teen") or (last_kind == "unit" and not hyphenated) or last_kind == "unit":
                    flush()
                    group.append(tens)
                else:
                    group.append(tens)
                last_kind = "tens"
            else:
                value = _UNITS[p]
                if last_kind == "tens" and group and group[-1] in (60, 80) and value >= 10:
                    group[-1] += value            # soixante-dix, quatre-vingt-dix(-sept)
                    last_kind = "teen"
                elif last_kind == "tens" and value < 10:
                    group[-1] += value
                    last_kind = "unit"
                elif last_kind == "teen" and group and group[-1] in (70, 90) and value < 10 \
                        and group[-1] % 10 == 0:
                    group[-1] += value            # soixante-dix-sept said as "soixante dix sept"
                    last_kind = "unit"
                elif last_kind in ("unit", "teen") and group:
                    flush()
                    group.append(value)
                    last_kind = "teen" if value >= 10 else "unit"
                else:
                    group.append(value)
                    last_kind = "teen" if value >= 10 else "unit"
        if hyphenated:
            flush()
    flush()
    return " ".join(out)


# ---- spoken file number -------------------------------------------------------------------

_DASH_WORDS_RE = re.compile(r"\s*\b(tiret|trait d'union|dash|hyphen)\b\s*", re.IGNORECASE)

_LOOSE_PATTERNS = (
    re.compile(r"(?<!\d)(\d{4})\s*[-–—]\s*(\d{2})(?!\d)"),      # 1234-56, 1234 - 56
    re.compile(r"(?<!\d)(\d{2})\s*(\d{2})\s*[-–— ]\s*(\d{2})(?!\d)"),  # 12 34-56, 12 34 56
    re.compile(r"(?<!\d)(\d{4})\s+(\d{2})(?!\d)"),               # 1234 56
    re.compile(r"(?<!\d)(\d{6})(?!\d)"),                         # 123456
)


@dataclass
class SpokenNumber:
    number: str
    near_keyword: bool
    position: int


def find_spoken_numbers(text: str, head_chars: int = 2500) -> list[SpokenNumber]:
    """File numbers said in the recording, most credible first.

    A strict "1234-56" counts anywhere in the opening text. Looser forms ("12 34 56", "123456") count only
    as the first number following "dossier" / "file" / "numéro" and the like, and such a keyword vouches
    for one number only, the first one after it.
    """
    head = _DASH_WORDS_RE.sub("-", words_to_digits(text[:head_chars]))
    candidates: dict[int, tuple[str, bool]] = {}   # start position -> (number, strict)
    for m in FILE_NUMBER_RE.finditer(head):
        candidates[m.start()] = (f"{m.group(1)}-{m.group(2)}", True)
    for pat in _LOOSE_PATTERNS:
        for m in pat.finditer(head):
            if m.start() not in candidates:
                digits = "".join(m.groups())
                candidates[m.start()] = (f"{digits[:4]}-{digits[4:6]}", False)
    positions = sorted(candidates)
    vouched: set[int] = set()
    for kw in KEYWORDS_RE.finditer(head):
        nxt = next((p for p in positions if kw.end() <= p <= kw.end() + 60), None)
        if nxt is not None:
            vouched.add(nxt)
    found: dict[str, SpokenNumber] = {}
    for pos in positions:
        number, strict = candidates[pos]
        near = pos in vouched
        if not strict and not near:
            continue
        if number not in found or (near and not found[number].near_keyword):
            found[number] = SpokenNumber(number, near, pos)
    return sorted(found.values(), key=lambda s: (not s.near_keyword, s.position))


# ---- proposal -----------------------------------------------------------------------------

@dataclass
class Proposal:
    number: str | None = None
    label: str | None = None
    method: str = "none"            # spoken | names | none
    confidence: str = "low"         # high | medium | low
    candidates: list[Match] = field(default_factory=list)
    note: str = ""

    @property
    def found(self) -> bool:
        return self.number is not None


def propose(text: str, index: MatterIndex, *, min_score: float = 0.0, margin: float = 1.5) -> Proposal:
    """Decide the file number for a transcript. ``min_score`` 0 means "as distinctive as one word that
    identifies a single matter in the list"."""
    if min_score <= 0:
        min_score = index.unique_word_score()
    spoken = find_spoken_numbers(text)
    if spoken:
        best = spoken[0]
        matter = index.get(best.number)
        if matter is not None:
            return Proposal(best.number, matter.label, "spoken", "high",
                            note="file number said in the recording and found in the matter list")
        if best.near_keyword:
            return Proposal(best.number, None, "spoken", "medium",
                            note="file number said after 'dossier'/'file' but absent from the matter list")
        # A bare 1234-56 somewhere in the text without context: keep it as a hint only.
        hint = best.number
    else:
        hint = None

    matches = index.match_names(text)
    if matches:
        top = matches[0]
        second = matches[1].score if len(matches) > 1 else 0.0
        if top.score >= min_score and top.score >= margin * second:
            return Proposal(top.matter.number, top.matter.label, "names", "medium", matches,
                            note="names in the recording match this matter: " + ", ".join(top.matched))
        return Proposal(hint, index.get(hint).label if hint and index.get(hint) else None, "names" if not hint
                        else "spoken", "low", matches, note="several matters could match")
    if hint:
        return Proposal(hint, None, "spoken", "low", note="a file number appears in the text without context")
    return Proposal()


# ---- names --------------------------------------------------------------------------------

def sanitise(part: str, max_len: int = 80) -> str:
    part = _FORBIDDEN.sub(" ", part)
    part = re.sub(r"\s+", " ", part).strip(" .")
    return part[:max_len].rstrip(" .")


DEFAULT_PATTERN = "{date} - {number} - {label} - {subject}"


def build_stem(original_stem: str, number: str | None, label: str | None, subject: str | None = None, *,
               pattern: str = DEFAULT_PATTERN, max_len: int = 120) -> str:
    """Assemble the new file stem. Missing parts are dropped; the subject, then the label, are
    shortened if the result exceeds ``max_len``."""
    m = DATE_STEM_RE.match(original_stem)
    values = {
        "date": m.group(1) if m else sanitise(original_stem),
        "number": number or "",
        "label": sanitise(label) if label else "",
        "subject": sanitise(subject) if subject else "",
    }
    order = [f for f in re.findall(r"\{(\w+)\}", pattern) if f in values]
    if "date" not in order:
        order.insert(0, "date")

    def assemble() -> str:
        return " - ".join(values[f] for f in order if values[f])

    stem = assemble()
    for part in ("subject", "label"):
        if len(stem) <= max_len:
            break
        excess = len(stem) - max_len
        values[part] = values[part][: max(0, len(values[part]) - excess - 1)].rstrip(" ,.;:-")
        stem = assemble()
    if len(stem) > max_len:
        stem = stem[:max_len].rstrip(" -.,")
    return stem


def unique_path(path: Path) -> Path:
    if not path.exists():
        return path
    for i in range(2, 100):
        candidate = path.with_name(f"{path.stem} ({i}){path.suffix}")
        if not candidate.exists():
            return candidate
    raise FileExistsError(path)


def rename_set(audio: Path, new_stem: str, companions: tuple[str, ...] = (".txt", ".md")) -> dict[Path, Path]:
    """Rename the audio file and its transcript(s) to ``new_stem``. Returns {old: new}."""
    target = unique_path(audio.with_name(new_stem + audio.suffix))
    final_stem = target.stem
    moves: dict[Path, Path] = {audio: target}
    for ext in companions:
        old = audio.with_suffix(ext)
        if old.is_file():
            moves[old] = old.with_name(final_stem + ext)
    for old, new in moves.items():
        old.rename(new)
    return moves
