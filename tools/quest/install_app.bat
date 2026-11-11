@echo off
rem Installs Builds\OnDemandMeshVR.apk (built by Unity: Tools > Quest > Prepare and build APK) on the connected Quest
rem and starts it. Needs the headset connected with the cable and USB debugging accepted.
setlocal
cd /d "%~dp0..\.."
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)
".venv\Scripts\python.exe" tools\quest\quest.py install --run %*
echo.
pause
