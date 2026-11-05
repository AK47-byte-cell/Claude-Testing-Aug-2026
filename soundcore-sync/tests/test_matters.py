
from soundcore_sync.matters import FILE_NUMBER_RE, Matter, MatterIndex, normalise, tokens_of


def test_normalise_strips_accents_and_punctuation():
    assert normalise("L'Abbée, Mélanie — c. XYZ") == "l abbee melanie c xyz"


def test_tokens_drop_stopwords_short_words_and_digits():
    assert tokens_of("1234-56 Tremblay c. Assurances Intact inc.") == {"tremblay", "intact"}


def test_index_from_folder(tmp_path):
    (tmp_path / "1234-56 Tremblay c. Assurances XYZ").mkdir()
    (tmp_path / "2345-67 - Succession Gagnon").mkdir()
    (tmp_path / "3456-78_Construction Beaulieu inc").mkdir()
    (tmp_path / "Archives").mkdir()
    (tmp_path / "4567-89 Dupont (note).txt").write_text("x")
    index = MatterIndex.load(tmp_path)
    assert len(index) == 4
    assert index.get("1234-56").label == "Tremblay c. Assurances XYZ"
    assert index.get("2345-67").label == "Succession Gagnon"
    assert index.get("3456-78").label == "Construction Beaulieu inc"
    assert index.get("4567-89").label == "Dupont (note)"


def test_index_from_csv(tmp_path):
    f = tmp_path / "dossiers.csv"
    f.write_text("Numéro;Client;Participants\n1234-56;Tremblay c. XYZ;Jean Tremblay;Me Lavoie\n"
                 "no 2345-67;Gagnon\n\n", encoding="utf-8")
    index = MatterIndex.load(f)
    assert len(index) == 2
    assert index.get("1234-56").keywords == ("Jean Tremblay", "Me Lavoie")
    assert "lavoie" in index.get("1234-56").tokens
    assert index.get("2345-67").label == "Gagnon"


def test_missing_source_gives_empty_index(tmp_path):
    assert len(MatterIndex.load(tmp_path / "nope")) == 0
    assert len(MatterIndex.load(None)) == 0


def test_match_names_prefers_distinctive_words():
    index = MatterIndex([
        Matter("1234-56", "Tremblay c. Assurances Intact"),
        Matter("2345-67", "Gagnon c. Assurances Intact"),
        Matter("3456-78", "Beaulieu c. Desjardins"),
    ])
    matches = index.match_names("Rencontre avec monsieur Tremblay au sujet de la réclamation Intact.")
    assert matches[0].matter.number == "1234-56" and matches[0].matched == ["intact", "tremblay"]
    assert matches[1].matter.number == "2345-67" and matches[1].matched == ["intact"]
    assert matches[0].score > matches[1].score
    assert index.match_names("rien de pertinent") == []


def test_file_number_regex_is_strict():
    assert FILE_NUMBER_RE.search("dossier 1234-56.").group(0) == "1234-56"
    assert FILE_NUMBER_RE.search("12345-56") is None
    assert FILE_NUMBER_RE.search("1234-567") is None
