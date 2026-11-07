@echo off
rem Double-click to start the edge server of ondemand-mesh-vr.
rem Default: mock backend (convex hull) with 2 s of simulated inference per object.
rem Options typed after the name replace the default, e.g. from a terminal:
rem     start_edge_server.bat --mock-delay-s 0
rem     start_edge_server.bat --backend model --model meshanything
rem     start_edge_server.bat --help
rem Stop the server with Ctrl+C (then answer Y), or close the window.

setlocal
cd /d "%~dp0"
title Edge server - ondemand-mesh-vr

set "PYTHON=.venv\Scripts\python.exe"
set "DEFAULT_ARGS=--mock-delay-s 2"

if not exist "%PYTHON%" (
    echo [edge] Python environment not found: %CD%\%PYTHON%
    echo [edge] Create it first: see section 1 of README.md
    goto :end
)

if "%~1"=="" goto :default
"%PYTHON%" -m edge.server %*
goto :stopped

:default
rem a second double-click must not start a second server on the same port
curl.exe -s -o nul -m 2 http://127.0.0.1:8000/health >nul 2>&1
if not errorlevel 1 (
    echo [edge] A server is already running on http://127.0.0.1:8000 - nothing to do.
    goto :end
)
echo [edge] Starting with: %DEFAULT_ARGS%
"%PYTHON%" -m edge.server %DEFAULT_ARGS%

:stopped
echo.
echo [edge] Server stopped.

:end
echo.
pause
