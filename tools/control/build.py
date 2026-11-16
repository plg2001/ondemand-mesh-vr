"""Builds EdgeControl.exe, the Windows control panel of the project, from tools/control/src/*.cs.

    python tools/control/build.py            # compile: EdgeControl.exe in the repository root (a few seconds)
    python tools/control/build.py --check    # only say whether EdgeControl.exe matches the sources

The panel is C# for the .NET Framework 4.8, which is part of every Windows 10/11: the .exe runs on any Windows PC with
nothing to install. It is compiled with a Roslyn C# compiler, the one of the .NET SDK or the one inside Unity (which the
project needs anyway), against the .NET Framework assemblies in C:\\Windows\\Microsoft.NET. Standard library only.

The compilation is deterministic. build.stamp records the sha256 of the sources and of the .exe built from them:
tests/test_control_app.py compares them, so a change to the sources without a rebuild does not go unnoticed.
Development aid: EdgeControl.exe --snapshot <folder> saves a picture of every page and exits.
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SOURCES = HERE / "src"
MANIFEST = HERE / "app.manifest"
ICON = HERE / "EdgeControl.ico"
STAMP = HERE / "build.stamp"
EXE = ROOT / "EdgeControl.exe"
REFERENCES = ("mscorlib.dll", "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll",
              "System.Web.Extensions.dll", "System.Management.dll")


def sources_digest() -> str:
    """sha256 of the sources, independent of line endings (git may check them out with CRLF or LF)."""
    h = hashlib.sha256()
    for path in sorted(SOURCES.glob("*.cs")) + [MANIFEST]:
        h.update(path.name.encode() + b"\0")
        h.update(path.read_bytes().replace(b"\r\n", b"\n") + b"\0")
    h.update(ICON.read_bytes())
    return h.hexdigest()


def file_digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_stamp() -> dict:
    if not STAMP.exists():
        return {}
    # splitlines + strip: git may check the stamp out with CRLF line endings
    return dict(line.strip().split(" ", 1) for line in STAMP.read_text(encoding="utf-8").splitlines() if " " in line.strip())


def _version(text: str) -> tuple:
    return tuple(int(x) for x in re.findall(r"\d+", text)[:3])


def find_compiler() -> Optional[list[str]]:
    """[runtime, csc.dll]: the newest .NET SDK's Roslyn, else Unity's (newest editor)."""
    dotnet = shutil.which("dotnet")
    if dotnet:
        try:
            listing = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True, timeout=30).stdout
        except (OSError, subprocess.TimeoutExpired):
            listing = ""
        sdks = []
        for line in listing.splitlines():
            m = re.match(r"(\S+) \[(.+)\]", line.strip())
            if m:
                csc = Path(m.group(2)) / m.group(1) / "Roslyn" / "bincore" / "csc.dll"
                if csc.exists():
                    sdks.append((_version(m.group(1)), csc))
        if sdks:
            return [dotnet, str(max(sdks)[1])]
    program_files = os.environ.get("ProgramFiles", r"C:\Program Files")
    editors = sorted(glob.glob(os.path.join(program_files, "Unity", "Hub", "Editor", "*")), key=lambda p: _version(Path(p).name),
                     reverse=True)
    for editor in editors:
        data = Path(editor) / "Editor" / "Data"
        csc, runtime = data / "DotNetSdkRoslyn" / "csc.dll", data / "NetCoreRuntime" / "dotnet.exe"
        if csc.exists() and runtime.exists():
            return [str(runtime), str(csc)]
    return None


def framework_dir() -> Optional[Path]:
    windir = Path(os.environ.get("WINDIR", r"C:\Windows"))
    for bits in ("Framework64", "Framework"):
        d = windir / "Microsoft.NET" / bits / "v4.0.30319"
        if (d / "System.Windows.Forms.dll").exists():
            return d
    return None


def build() -> int:
    if sys.platform != "win32":
        print("EdgeControl is a Windows program: build it on Windows.")
        return 1
    compiler, framework = find_compiler(), framework_dir()
    if compiler is None:
        print("No C# compiler found: install the .NET SDK (https://dot.net) or Unity (it has one inside).")
        return 1
    if framework is None:
        print("The .NET Framework 4.x is missing (C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319).")
        return 1
    out = EXE.with_suffix(".new.exe")
    args = [*compiler, "-nologo", "-noconfig", "-nostdlib+", "-target:winexe", "-platform:anycpu", "-optimize+",
            "-deterministic", "-debug-", "-langversion:latest", "-utf8output", "-warnaserror-", f"-out:{out}",
            f"-win32manifest:{MANIFEST}", f"-win32icon:{ICON}",
            *[f"-r:{framework / name}" for name in REFERENCES], *sorted(str(p) for p in SOURCES.glob("*.cs"))]
    print(f"compiler: {' '.join(compiler)}")
    r = subprocess.run(args, capture_output=True, text=True)
    output = (r.stdout + r.stderr).strip()
    if output:
        print(output)
    if r.returncode != 0:
        out.unlink(missing_ok=True)
        print("build FAILED")
        return 1
    try:
        os.replace(out, EXE)
    except PermissionError:
        print(f"{EXE.name} is open: close it and run this again (the new build is in {out.name}).")
        return 1
    STAMP.write_text(f"sources {sources_digest()}\nexe {file_digest(EXE)}\n", encoding="utf-8", newline="\n")
    print(f"built {EXE} ({EXE.stat().st_size // 1024} KB)")
    return 0


def check() -> int:
    stamp = read_stamp()
    if not EXE.exists():
        print(f"{EXE.name} missing: python tools/control/build.py")
        return 1
    if stamp.get("sources") != sources_digest():
        print("the sources changed after the last build: python tools/control/build.py")
        return 1
    if stamp.get("exe") != file_digest(EXE):
        print(f"{EXE.name} is not the one built from these sources: python tools/control/build.py")
        return 1
    print(f"{EXE.name} is up to date")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="only check that EdgeControl.exe matches the sources")
    args = ap.parse_args(argv)
    return check() if args.check else build()


if __name__ == "__main__":
    sys.exit(main())
