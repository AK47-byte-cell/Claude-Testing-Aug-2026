import os
import time

import pytest

from soundcore_sync.matters import MatterIndex, resolve_source

openpyxl = pytest.importorskip("openpyxl")


def workbook(path, rows, sheet="Dossiers", extra_sheet_first=False):
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = sheet
    if extra_sheet_first:
        ws.title = "Notes"
        ws.append(["Ceci est une feuille sans numéros"])
        ws = wb.create_sheet(sheet)
    for r in rows:
        ws.append(r)
    wb.save(path)
    return path


def test_excel_with_headers(tmp_path):
    f = workbook(tmp_path / "2026-11-04 AKS Liste dossiers.xlsm", [
        ["Liste des dossiers", None, None, None],
        ["No dossier", "Client", "Partie adverse", "Assureur", "Ouverture"],
        ["1234-56", "Tremblay, Jean", "Assurances XYZ", "Intact", "2026-01-05"],
        ["2345-67", "Gagnon c. Beaulieu", "Beaulieu Construction", "Desjardins", "2026-02-10"],
        [None, None, None, None, None],
        ["3456-78", "Succession Lavoie", None, "Intact", "2026-03-01"],
        ["total", 3, None, None, None],
    ])
    index = MatterIndex.load(f)
    assert len(index) == 3
    m = index.get("1234-56")
    assert m.label == "Tremblay, Jean"
    assert m.keywords == ("Assurances XYZ", "Intact")       # the date is not a keyword
    assert "beaulieu" in index.get("2345-67").tokens
    assert index.match_names("Appel de Jean Tremblay au sujet de XYZ.")[0].matter.number == "1234-56"


def test_excel_number_column_not_first_and_label_by_header(tmp_path):
    f = workbook(tmp_path / "liste.xlsx", [
        ["Avocat", "Statut", "Numéro", "Nom du dossier", "Notes"],
        ["AKS", "Actif", "1234-56", "Tremblay c. XYZ", "expertise à venir"],
        ["AKS", "Fermé", "2345-67", "Gagnon c. Beaulieu", None],
        ["MLA", "Actif", "pas un numéro", "Ignoré", None],
    ])
    index = MatterIndex.load(f)
    assert sorted(index.matters) == ["1234-56", "2345-67"]
    assert index.get("1234-56").label == "Tremblay c. XYZ"
    assert "expertise à venir" in index.get("1234-56").keywords


def test_excel_without_headers_uses_first_text_column(tmp_path):
    f = workbook(tmp_path / "liste.xlsx", [
        ["1234-56", 15, "Tremblay c. XYZ"],
        ["2345-67", 8, "Gagnon c. Beaulieu"],
    ], extra_sheet_first=True)
    index = MatterIndex.load(f)
    assert index.get("1234-56").label == "Tremblay c. XYZ"


def test_excel_without_numbers_gives_empty_index(tmp_path):
    f = workbook(tmp_path / "liste.xlsx", [["Client", "Ville"], ["Tremblay", "Laval"]])
    assert len(MatterIndex.load(f)) == 0


def test_pattern_picks_latest_dated_file(tmp_path):
    for name in ("2026-10-01 AKS Liste dossiers.xlsm", "2026-11-04 AKS Liste dossiers.xlsm",
                 "2026-09-15 AKS Liste dossiers.xlsm", "~$2026-11-04 AKS Liste dossiers.xlsm"):
        (tmp_path / name).write_bytes(b"x")
    old = tmp_path / "2026-10-01 AKS Liste dossiers.xlsm"
    os.utime(old, (time.time() + 100, time.time() + 100))   # newer mtime, but older date in the name
    assert resolve_source(tmp_path / "*AKS Liste dossiers.xlsm").name == "2026-11-04 AKS Liste dossiers.xlsm"
    assert resolve_source(tmp_path / "*nothing*") is None
    assert resolve_source(tmp_path / "missing.xlsx") is None
    assert resolve_source(old) == old


def test_pattern_without_dates_uses_mtime(tmp_path):
    a = tmp_path / "liste a.xlsx"
    b = tmp_path / "liste b.xlsx"
    a.write_bytes(b"x")
    b.write_bytes(b"x")
    os.utime(a, (time.time() + 100, time.time() + 100))
    assert resolve_source(tmp_path / "liste *.xlsx") == a


def test_loop_keeps_previous_list_when_reload_fails(tmp_path, monkeypatch):
    from soundcore_sync import transcriber as tr
    from tests.test_transcriber import make_loop

    loop = make_loop(tmp_path)
    loop.cfg.rename.matters_source = workbook(tmp_path / "liste.xlsx", [["No", "Client"], ["1234-56", "Tremblay"]])
    assert len(loop.matters()) == 1
    loop._matters_loaded_at = 0  # force a reload

    def boom(source):
        raise PermissionError("locked by Excel")

    monkeypatch.setattr(tr.MatterIndex, "load", staticmethod(boom))
    assert len(loop.matters()) == 1
    loop._matters_loaded_at = 0
    monkeypatch.setattr(tr.MatterIndex, "load", staticmethod(lambda source: MatterIndex([])))
    assert len(loop.matters()) == 1   # an empty reload does not wipe a good list
