@echo off
rem Prepares this PC for ondemand-mesh-vr: Python 3.11 environment, PyTorch for this GPU, models, data, tests.
rem Safe to run again (what is done is skipped). Options: see tools\setup_pc.py, e.g.
rem     setup_pc.bat --checkpoints-from E:\checkpoints
setlocal
cd /d "%~dp0"
where py >nul 2>&1
if not errorlevel 1 (
    py -3 tools\setup_pc.py %*
) else (
    python tools\setup_pc.py %*
)
echo.
pause
