@echo off
rem ---------------------------------------------------------------------------
rem  Extract PDFs from saved emails -- double-click launcher for Windows.
rem
rem  * Double-click this file to open a point-and-click window.
rem  * Or drag a folder (or email files) onto this file to process them.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
rem Show accented file names correctly in the console.
chcp 65001 >nul
cd /d "%~dp0"

rem Find Python. Use "&&" so each check reads its OWN live result -- never
rem test %errorlevel% inside a parenthesised block (it is frozen at parse
rem time, which would make the fallbacks dead code).
set "PYTHON="
where py >nul 2>nul && set "PYTHON=py"
if not defined PYTHON (
    where python >nul 2>nul && set "PYTHON=python"
)
if not defined PYTHON (
    where python3 >nul 2>nul && set "PYTHON=python3"
)

if not defined PYTHON (
    echo.
    echo Python was not found on this computer.
    echo Please install Python 3 from https://www.python.org/downloads/
    echo and be sure to tick "Add Python to PATH" during setup, then try again.
    echo.
    pause
    exit /b 1
)

if "%~1"=="" (
    rem Nothing dropped: open the point-and-click window.
    %PYTHON% "%~dp0extract_pdfs.py"
    goto finish
)

rem Files/folders were dropped onto this launcher. Process each one, passing
rem it as a SINGLE quoted argument so characters such as ^& ( ) in a file
rem name can never be interpreted by cmd.exe as commands.
:next
if "%~1"=="" goto finish
%PYTHON% "%~dp0extract_pdfs.py" "%~1"
shift
goto next

:finish
echo.
pause
endlocal
