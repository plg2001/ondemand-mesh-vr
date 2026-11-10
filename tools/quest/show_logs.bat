@echo off
rem Live Unity logs of the app running on the Quest (the Console of the headset). Ctrl+C to stop.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py logs %*
echo.
pause
