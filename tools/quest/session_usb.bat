@echo off
rem One measured session on the Quest through the USB cable: starts the edge server, records fps,
rem copies the results to benchmarks\results\quest\<timestamp>_usb\ and writes summary.md. Press Enter to end it.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py session usb %*
echo.
pause
