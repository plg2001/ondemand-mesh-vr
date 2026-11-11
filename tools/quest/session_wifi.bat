@echo off
rem One measured session on the Quest through the Wi-Fi (the cable stays plugged in for the fps log): starts the edge server, records fps,
rem copies the results to benchmarks\results\quest\<timestamp>_wifi\ and writes summary.md. Press Enter to end it.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py session wifi %*
echo.
pause
