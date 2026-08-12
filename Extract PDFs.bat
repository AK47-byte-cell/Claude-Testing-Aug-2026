@echo off
rem ---------------------------------------------------------------------------
rem  Extract PDFs from saved emails -- double-click launcher for Windows.
rem
rem  * Double-click this file to open a point-and-click window.
rem  * Or drag a folder (or email files) onto this file to process them.
rem ---------------------------------------------------------------------------
setlocal
rem Show accented file names correctly in the console.
chcp 65001 >nul
cd /d "%~dp0"

rem Prefer the Windows "py" launcher, fall back to "python".
where py >nul 2>nul
if %errorlevel%==0 (
    set "PYTHON=py"
) else (
    where python >nul 2>nul
    if %errorlevel%==0 (
        set "PYTHON=python"
    ) else (
        echo.
        echo Python was not found on this computer.
        echo Please install Python 3 from https://www.python.org/downloads/
        echo and be sure to tick "Add Python to PATH" during setup.
        echo.
        pause
        exit /b 1
    )
)

%PYTHON% "%~dp0extract_pdfs.py" %*

echo.
pause
endlocal
