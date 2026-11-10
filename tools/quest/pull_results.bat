@echo off
rem Copies edge_timings.csv and ondemand_events.csv from the Quest to benchmarks\results\quest\.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py pull %*
echo.
pause
