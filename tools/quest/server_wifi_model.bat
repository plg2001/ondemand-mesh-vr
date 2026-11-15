@echo off
rem Like server_wifi.bat, but with a generative model instead of the mock: the app on the Quest reaches the edge
rem server over Wi-Fi (this PC's LAN address is written on the headset), then the server starts in this window,
rem reachable from the network. Close the window to stop it.
rem Without options it asks which model to load (1 = MeshAnything V2, 2 = FastMesh, 3 = TreeMeshGPT) and which
rem object to reconstruct (0 = all, the one you look at first; 1-6 = only that one, the others stay points).
rem From a terminal the first option can be the model, the rest goes to the server, e.g.
rem     tools\quest\server_wifi_model.bat meshanything --objects box_01
rem Defaults: --time-budget-s 300 (the server gives up on an object after 5 minutes).
setlocal
cd /d "%~dp0..\.."
title Edge server for the Quest (generative model) - ondemand-mesh-vr
if not exist ".venv\Scripts\python.exe" (
    echo Python environment not found: see section 1 of README.md
    pause
    exit /b 1
)

set "MODEL=%~1"
if "%MODEL%"=="" goto :ask
shift
goto :start

:ask
echo Which model?
echo   1  MeshAnything V2  (the only one that finishes, on simple shapes, on a 2 GB GPU)
echo   2  FastMesh
echo   3  TreeMeshGPT
choice /c 123 /n /m "Type 1, 2 or 3: "
if errorlevel 3 (set "MODEL=treemeshgpt" & goto :askobject)
if errorlevel 2 (set "MODEL=fastmesh" & goto :askobject)
set "MODEL=meshanything"

:askobject
rem the server refuses the other objects (--objects): they stay points, without holding up the queue
set "EXTRA="
echo.
echo Which object?
echo   0  all of them, the one you look at first (normal behaviour)
echo   1  box     (the only one MeshAnything finishes on this PC, in about a minute)
echo   2  sphere
echo   3  bunny
echo   4  cat
echo   5  puppet
echo   6  wand
choice /c 0123456 /n /m "Type a number from 0 to 6: "
if errorlevel 7 (set "EXTRA= --objects wand_01" & goto :run)
if errorlevel 6 (set "EXTRA= --objects puppet_01" & goto :run)
if errorlevel 5 (set "EXTRA= --objects cat_01" & goto :run)
if errorlevel 4 (set "EXTRA= --objects bunny_01" & goto :run)
if errorlevel 3 (set "EXTRA= --objects sphere_01" & goto :run)
if errorlevel 2 (set "EXTRA= --objects box_01" & goto :run)
goto :run

:start
set "EXTRA="
:collect
if "%~1"=="" goto :run
set "EXTRA=%EXTRA% %1"
shift
goto :collect

:run
echo Loading the model takes from 20 s to about a minute; the headset can ask for meshes after "Uvicorn running".
".venv\Scripts\python.exe" tools\quest\quest.py wifi --serve --server-args "--backend model --model %MODEL% --time-budget-s 300%EXTRA%"
echo.
pause
