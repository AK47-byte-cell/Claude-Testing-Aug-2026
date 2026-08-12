@echo off
rem ---------------------------------------------------------------------------
rem  Build the standalone EmailPDFExtractor.exe on THIS Windows PC.
rem
rem  You only need this if you want to build the app yourself. Otherwise just
rem  download the ready-made EmailPDFExtractor.exe from the project's GitHub
rem  "Releases" page (it is built automatically).
rem
rem  Requires Python 3 installed with "Add Python to PATH" ticked.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
cd /d "%~dp0"

set "PYTHON="
where py >nul 2>nul && set "PYTHON=py -3"
if not defined PYTHON (
    where python >nul 2>nul && set "PYTHON=python"
)
if not defined PYTHON (
    echo Python was not found. Install it from https://www.python.org/downloads/
    echo and tick "Add Python to PATH" during setup, then run this again.
    pause
    exit /b 1
)

echo Installing build tools (PyInstaller + .msg / winmail.dat support)...
%PYTHON% -m pip install --upgrade pip pyinstaller extract-msg tnefparse
if errorlevel 1 goto error

echo.
echo Building EmailPDFExtractor.exe ...
%PYTHON% -m PyInstaller --clean --noconfirm EmailPDFExtractor.spec
if errorlevel 1 goto error

echo.
echo ===========================================================
echo  Done!  Your app is here:   dist\EmailPDFExtractor.exe
echo  Double-click it to run, or copy it anywhere you like.
echo ===========================================================
echo.
pause
exit /b 0

:error
echo.
echo Build failed -- see the messages above.
pause
exit /b 1
