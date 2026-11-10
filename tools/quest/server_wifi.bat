@echo off
rem The app on the Quest reaches the edge server over Wi-Fi (this PC's LAN address is written on the headset),
rem then the server starts in this window, reachable from the network. Close the window to stop it.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py wifi --serve %*
echo.
pause
