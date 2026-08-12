# -*- mode: python ; coding: utf-8 -*-
# PyInstaller build recipe for the standalone Windows app.
#
#   Build locally:   pyinstaller --clean --noconfirm EmailPDFExtractor.spec
#   Result:          dist/EmailPDFExtractor.exe   (a single, double-click file)
#
# GitHub builds this automatically on Windows -- see
# .github/workflows/build-windows.yml -- so you normally just download the exe.

import os
from PyInstaller.utils.hooks import collect_all

# Bundle optional packages when they are installed so the exe can open Outlook
# .msg files and winmail.dat (TNEF) attachments without anything extra.
datas, binaries, hiddenimports = [], [], []
for _pkg in ("extract_msg", "tnefparse"):
    try:
        _d, _b, _h = collect_all(_pkg)
        datas += _d
        binaries += _b
        hiddenimports += _h
    except Exception:
        pass  # package not installed -> exe still handles .eml files

icon = "app.ico" if os.path.exists("app.ico") else None

a = Analysis(
    ["extract_pdfs.py"],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="EmailPDFExtractor",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,          # windowed app: opens the GUI, no console window
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=icon,
)
