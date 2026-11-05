
import pytest

from soundcore_sync.matters import Matter, MatterIndex
from soundcore_sync.renamer import (
    build_stem,
    find_spoken_numbers,
    propose,
    rename_set,
    sanitise,
    words_to_digits,
)


@pytest.mark.parametrize("spoken, expected", [
    ("dossier douze trente-quatre cinquante-six, rencontre", "dossier 12 34 56 rencontre"),
    ("dossier douze trente quatre cinquante six", "dossier 12 34 56"),
    ("le dossier quatre-vingt-dix-sept vingt-trois soixante-dix-huit", "le dossier 97 23 78"),
    ("file twelve thirty-four fifty-six", "file 12 34 56"),
    ("file one thousand two hundred thirty four, fifty six", "file 1234 56"),
    ("nous avons deux témoins et trois experts, vingt et un documents",
     "nous avons 2 témoins et 3 experts, 21 documents"),
    ("soixante-dix-sept personnes et quatre-vingts dossiers", "77 personnes et 80 dossiers"),
    ("aucun nombre ici", "aucun nombre ici"),
])
def test_words_to_digits(spoken, expected):
    assert words_to_digits(spoken) == expected


@pytest.mark.parametrize("text, numbers", [
    ("Dossier 1234-56, rencontre avec M. Tremblay.", [("1234-56", True)]),
    ("Dossier numéro 12 34 56. Rencontre.", [("1234-56", True)]),
    ("dossier douze trente-quatre cinquante-six", [("1234-56", True)]),
    ("dossier mille deux cent trente-quatre tiret cinquante-six", [("1234-56", True)]),
    ("file twelve thirty four dash fifty six", [("1234-56", True)]),
    ("dossier 123456", [("1234-56", True)]),
    ("On a reçu 123456 dollars.", []),                       # loose form needs a keyword
    ("Le montant est de 1234-56 selon le relevé.", [("1234-56", False)]),
    ("dossier 9876-54 puis le dossier 1234-56", [("9876-54", True), ("1234-56", True)]),
    ("Au dossier 9876-54. Référence 1234-56 ailleurs.", [("9876-54", True), ("1234-56", False)]),
])
def test_find_spoken_numbers(text, numbers):
    assert [(s.number, s.near_keyword) for s in find_spoken_numbers(text)] == numbers


@pytest.fixture
def index():
    return MatterIndex([
        Matter("1234-56", "Tremblay c. Assurances Intact"),
        Matter("2345-67", "Gagnon c. Assurances Intact"),
        Matter("3456-78", "Beaulieu c. Desjardins"),
    ])


def test_propose_spoken_number_in_list_is_high(index):
    p = propose("Dossier 1234-56. Rencontre avec monsieur Gagnon.", index)
    assert (p.number, p.label, p.method, p.confidence) == ("1234-56", "Tremblay c. Assurances Intact", "spoken", "high")


def test_propose_spoken_number_not_in_list_is_medium(index):
    p = propose("Dossier 9999-99, note au dossier.", index)
    assert (p.number, p.label, p.method, p.confidence) == ("9999-99", None, "spoken", "medium")


def test_propose_from_names_when_unambiguous(index):
    p = propose("Appel de monsieur Beaulieu au sujet de Desjardins.", index)
    assert (p.number, p.method, p.confidence) == ("3456-78", "names", "medium")
    assert p.candidates[0].matter.number == "3456-78"


def test_propose_ambiguous_names_is_low(index):
    p = propose("Discussion avec Intact au sujet des assurances.", index)
    assert p.confidence == "low" and p.number is None and len(p.candidates) == 2


def test_propose_nothing(index):
    p = propose("Note générale sans nom ni numéro.", index)
    assert not p.found and p.method == "none"


def test_propose_without_matter_list():
    p = propose("Dossier 1234-56, note.", MatterIndex([]))
    assert (p.number, p.confidence) == ("1234-56", "medium")


def test_sanitise_and_build_stem():
    assert sanitise('Tremblay c. "XYZ"/ inc.  ') == "Tremblay c. XYZ inc"
    assert build_stem("2026-11-05_14-35-07_soundcore", "1234-56", "Tremblay c. Assurances XYZ") == \
        "2026-11-05_14-35 - 1234-56 - Tremblay c. Assurances XYZ"
    assert build_stem("Memo du matin", "1234-56", None) == "Memo du matin - 1234-56"
    assert build_stem("2026-11-05_14-35-07_soundcore", None, "X") == "2026-11-05_14-35 - X"
    long = build_stem("2026-11-05_14-35-07_soundcore", "1234-56", "A" * 300, max_len=60)
    assert len(long) <= 60


def test_rename_set_moves_companions_and_avoids_collisions(tmp_path):
    audio = tmp_path / "a.ogg"
    audio.write_bytes(b"x")
    (tmp_path / "a.txt").write_text("t")
    (tmp_path / "a.md").write_text("m")
    (tmp_path / "new.ogg").write_bytes(b"taken")
    moves = rename_set(audio, "new")
    assert moves[audio] == tmp_path / "new (2).ogg"
    assert (tmp_path / "new (2).txt").read_text() == "t" and (tmp_path / "new (2).md").is_file()
    assert not audio.exists()
