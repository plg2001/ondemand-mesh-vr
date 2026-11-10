@echo off
rem The app on the Quest reaches the edge server through the USB cable (no Wi-Fi, no firewall),
rem then the server starts in this window (mock backend, 2 s per object). Close the window to stop it.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py usb --serve %*
echo.
pause
