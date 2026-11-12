"""Helpers to test the Unity app on a Meta Quest connected to this PC with a USB cable (adb).

Double-click the .bat files next to this script, or run from the repository root:
    python tools/quest/quest.py check          # headset connected and authorised? Android recent enough? app installed?
    python tools/quest/quest.py install        # install Builds/OnDemandMeshVR.apk (--run also starts it)
    python tools/quest/quest.py usb --serve    # the app reaches the edge server through the cable; start the server
    python tools/quest/quest.py wifi --serve   # the app reaches the edge server over Wi-Fi; start it on the LAN
    python tools/quest/quest.py logs           # live Unity logs of the app (Ctrl+C to stop)
    python tools/quest/quest.py pull           # copy the CSV timings from the headset to benchmarks/results/quest/
    python tools/quest/quest.py session usb    # one measured session (usb | wifi): server, fps, results, summary

USB mode uses "adb reverse": on the headset, 127.0.0.1:8000 is forwarded to this PC's port 8000, so the app's default
address works unchanged, with no Wi-Fi and no firewall rule. It tests the pipeline, not the Wi-Fi.
Wi-Fi mode writes this PC's LAN address into edge_server.txt in the app's data folder on the headset; the app reads it
at start (EdgeClient.ResolveUrl), so switching between the two modes needs no rebuild.

`session usb|wifi` does a whole measured session: sets the link, starts the edge server with its own log, restarts the
app, records the headset's frame rate (VrApi lines in logcat, one per second) while you look around, and when you
press Enter copies everything to benchmarks/results/quest/<timestamp>_<link>/ and writes summary.md
(benchmarks/quest_report.py). In wifi mode the cable stays plugged in, but only for the fps log: the USB tunnel is
removed, so the app's requests really travel over the Wi-Fi (the server log records the address they came from).
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from pathlib import Path
from typing import Optional

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
from edge.netinfo import lan_ipv4_addresses  # noqa: E402
from benchmarks.quest_report import FPS_FIELDS, parse_vrapi_line, write_report  # noqa: E402

PROJECT_SETTINGS = ROOT / "unity" / "OnDemandMeshVR" / "ProjectSettings" / "ProjectSettings.asset"
OVERRIDE_FILE = "edge_server.txt"  # must match EdgeClient.ServerOverrideFile
RESULT_FILES = ("edge_timings.csv", "ondemand_events.csv")
UNITY_EDITORS = Path("C:/Program Files/Unity/Hub/Editor")
GUIDE = "README.md, section 8"
QUEST_RESULTS = ROOT / "benchmarks" / "results" / "quest"
SERVER_LOG = ROOT / "benchmarks" / "results" / "tmp" / "edge_requests.csv"  # edge.server's default --log-csv
APK = ROOT / "unity" / "OnDemandMeshVR" / "Builds" / "OnDemandMeshVR.apk"  # written by Unity: Tools > Quest


# ----------------------------------------------------------------------------- discovery
def find_adb() -> Optional[str]:
    """adb from PATH, ANDROID_SDK_ROOT / ANDROID_HOME, or the one shipped with Unity's Android Build Support."""
    candidates = [shutil.which("adb")]
    for var in ("ANDROID_SDK_ROOT", "ANDROID_HOME"):
        if os.environ.get(var):
            candidates.append(str(Path(os.environ[var]) / "platform-tools" / "adb.exe"))
    candidates += [str(p) for p in sorted(
        UNITY_EDITORS.glob("*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"), reverse=True)]
    return next((c for c in candidates if c and Path(c).exists()), None)


def project_android_settings(text: Optional[str] = None) -> tuple[Optional[str], Optional[int]]:
    """(package name, minimum Android API level) from the Unity project's ProjectSettings.asset."""
    if text is None:
        text = PROJECT_SETTINGS.read_text(encoding="utf-8") if PROJECT_SETTINGS.exists() else ""
    package = None
    block = re.search(r"^  applicationIdentifier:[ \t]*\r?\n((?:    .*\r?\n)*)", text, re.M)
    if block:
        found = re.search(r"^    Android: (\S+)", block.group(1), re.M)
        package = found.group(1) if found else None
    sdk = re.search(r"^  AndroidMinSdkVersion: (\d+)", text, re.M)
    return package, int(sdk.group(1)) if sdk else None


def app_files_dir(package: str) -> str:
    """Application.persistentDataPath of the app on the headset."""
    return f"/sdcard/Android/data/{package}/files"


def server_answers(base_url: str, timeout: float = 2.0) -> bool:
    try:
        with urllib.request.urlopen(f"{base_url}/health", timeout=timeout) as r:
            return r.status == 200
    except OSError:
        return False


class Adb:
    def __init__(self, path: str):
        self.path = path

    def run(self, *args: str, timeout: float = 60) -> subprocess.CompletedProcess:
        return subprocess.run([self.path, *args], capture_output=True, text=True, timeout=timeout)

    def shell(self, *args: str) -> str:
        return self.run("shell", *args).stdout.strip()

    def device(self) -> tuple[str, Optional[str]]:
        """('device' | 'unauthorized' | 'offline' | 'none', serial)."""
        out = self.run("devices").stdout
        rows = [line.split() for line in out.splitlines()[1:] if line.strip() and not line.startswith("*")]
        ready = [r for r in rows if len(r) >= 2 and r[1] == "device"]
        if ready:
            return "device", ready[0][0]
        if rows:
            return (rows[0][1] if len(rows[0]) > 1 else "unknown"), rows[0][0]
        return "none", None


def require_device(adb: Adb) -> Optional[str]:
    state, serial = adb.device()
    if state == "device":
        return serial
    hints = {
        "none": "No headset found. Is it switched on and connected with a USB DATA cable? Is Developer Mode on?",
        "unauthorized": "Put on the headset and accept 'Allow USB debugging' (tick 'Always allow from this computer').",
        "offline": "The headset is offline: unplug and plug the cable again.",
    }
    print(hints.get(state, f"Headset state '{state}': unplug and plug the cable again."))
    print(f"Step by step: {GUIDE}")
    return None


# ----------------------------------------------------------------------------- commands
def cmd_check(adb: Adb, args) -> int:
    print(f"adb: {adb.path}")
    serial = require_device(adb)
    if not serial:
        return 1
    model = adb.shell("getprop", "ro.product.model")
    sdk = adb.shell("getprop", "ro.build.version.sdk")
    package, min_sdk = project_android_settings()
    print(f"headset: {model} (serial {serial}), Android API level {sdk}")
    if min_sdk and sdk.isdigit() and int(sdk) < min_sdk:
        print(f"PROBLEM: the Unity project requires API level {min_sdk} or newer, the headset has {sdk}. Update the "
              f"headset software, or lower 'Minimum API Level' in Unity: Project Settings > Player > Android > "
              f"Other Settings.")
    else:
        print(f"Android version: OK (project needs {min_sdk or '?'})")
    if package:
        installed = f"package:{package}" in adb.shell("pm", "list", "packages", package).split()
        print(f"app {package}: {'installed' if installed else 'not installed yet (Build And Run from Unity)'}")
        override = adb.shell("cat", f"{app_files_dir(package)}/{OVERRIDE_FILE}")
        print("server address on the headset: " + (override if override.startswith("http")
                                                    else "default of the app (http://127.0.0.1:8000)"))
    reverse = adb.run("reverse", "--list").stdout.strip()
    print("USB tunnel: " + (reverse.replace("\n", "; ") if reverse else "off"))
    print("edge server on this PC: " + ("running" if server_answers(f"http://127.0.0.1:{args.port}") else "not running"))
    return 0


def cmd_install(adb: Adb, args) -> int:
    apk = Path(args.apk) if args.apk else APK
    if not apk.exists():
        print(f"APK not found: {apk}")
        print("Build it in Unity first: Tools > Quest > Prepare and build APK.")
        return 1
    if not require_device(adb):
        return 1
    print(f"Installing {apk.name} ({apk.stat().st_size / 1048576:.0f} MB) on the headset: about a minute...")
    r = adb.run("install", "-r", "-d", str(apk), timeout=900)  # -r keeps the app's data, -d allows older builds
    out = (r.stdout + r.stderr).strip()
    if r.returncode != 0 or "Success" not in out:
        print(out)
        if "OLDER_SDK" in out:
            print("The headset's Android is older than the app's Minimum API Level: update the headset software "
                  "or, in Unity, run Tools > Quest > Prepare project and build again.")
        elif "INSUFFICIENT_STORAGE" in out:
            print("The headset is out of space: free some in the Quest library, then try again.")
        elif "INSTALL_FAILED_UPDATE_INCOMPATIBLE" in out or "SIGNATURES" in out:
            print("An older build signed differently is installed: uninstall it in the headset library, then retry.")
        return 1
    package, _ = project_android_settings()
    print("Installed. In the headset: Library > filter 'Unknown sources' > OnDemandMeshVR.")
    if args.run and package:
        adb.shell("monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1")
        print("Start requested: put the headset on.")
    return 0


def cmd_usb(adb: Adb, args) -> int:
    if not require_device(adb):
        return 1
    r = adb.run("reverse", f"tcp:{args.port}", f"tcp:{args.port}")
    if r.returncode != 0:
        print(f"adb reverse failed: {r.stderr.strip()}")
        return 1
    package, _ = project_android_settings()
    if package:  # back to the app's default address (127.0.0.1), which the tunnel now serves
        adb.shell("rm", "-f", f"{app_files_dir(package)}/{OVERRIDE_FILE}")
    print(f"USB tunnel on: on the headset, http://127.0.0.1:{args.port} now reaches this PC.")
    print("The app uses its default address. If it was already running, restart it.")
    print("The tunnel is lost when the cable is unplugged or the headset restarts: run this again then.")
    return serve(args, "127.0.0.1") if args.serve else 0


def cmd_wifi(adb: Adb, args) -> int:
    addresses = lan_ipv4_addresses()
    if not addresses and not args.ip:
        print("No local network address found: is this PC connected to the Wi-Fi / LAN?")
        return 1
    ip = args.ip or addresses[0]
    if len(addresses) > 1 and not args.ip:
        print(f"This PC has several addresses ({', '.join(addresses)}): using {ip}. Use --ip to choose another.")
    url = f"http://{ip}:{args.port}"
    state, _ = adb.device()
    if state == "device":
        if not write_server_address(adb, url):
            return 1
        print(f"The app on the headset will use {url}. If it was already running, restart it.")
        print("Headset and PC must be on the same Wi-Fi network (not a guest network, which isolates devices).")
        print("You can now unplug the cable.")
    elif args.serve:  # no headset: start the server anyway, the network can be tested from a phone
        print("Headset not connected: its address will be written next time you run this with the cable.")
    else:
        require_device(adb)
        return 1
    print(f"Network test without the headset: from a phone on the same Wi-Fi open {url}/health")
    print("  if it shows {\"status\":\"ok\", ...} the headset will reach the server too; if it does not load,")
    print("  check the Windows firewall and that the Wi-Fi is a PRIVATE network.")
    return serve(args, "0.0.0.0", lan_url=url) if args.serve else 0


def write_server_address(adb: Adb, url: str) -> bool:
    """Writes edge_server.txt in the app's data folder on the headset; the app reads it at start."""
    package, _ = project_android_settings()
    if not package:
        print(f"Cannot read the app's package name from {PROJECT_SETTINGS}")
        return False
    files = app_files_dir(package)
    adb.shell("mkdir", "-p", files)
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, newline="\n", encoding="utf-8") as f:
        f.write(url + "\n")
        tmp = f.name
    try:
        r = adb.run("push", tmp, f"{files}/{OVERRIDE_FILE}")
    finally:
        os.unlink(tmp)
    if r.returncode != 0:
        print(f"Could not write the address on the headset: {r.stderr.strip()}")
        print("Start the app once on the headset (it creates its data folder), then run this again.")
        return False
    adb.run("reverse", "--remove", f"tcp:{url.rsplit(':', 1)[1]}")  # make sure the app really goes through the Wi-Fi
    return True


def serve(args, host: str, lan_url: Optional[str] = None) -> int:
    local = f"http://127.0.0.1:{args.port}"
    if server_answers(local):
        if lan_url and not server_answers(lan_url):
            print("An edge server is running but only for this PC (127.0.0.1): close its window, then run this again.")
            return 1
        print("An edge server is already running: keeping it.")
        return 0
    cmd = [sys.executable, "-m", "edge.server", "--host", host, "--port", str(args.port),
           *shlex.split(args.server_args)]
    print("Starting the edge server: " + " ".join(cmd[2:]))
    print("Close this window (or Ctrl+C) to stop it.\n")
    try:
        return subprocess.call(cmd, cwd=ROOT)
    except KeyboardInterrupt:
        return 0


def cmd_logs(adb: Adb, args) -> int:
    if not require_device(adb):
        return 1
    adb.run("logcat", "-c")
    print("Unity logs from the headset (Ctrl+C to stop). Start or restart the app now.\n")
    try:
        return subprocess.call([adb.path, "logcat", "-v", "time", "-s", "Unity:V"])
    except KeyboardInterrupt:
        return 0


def pull_files(adb: Adb, package: str, dest: Path) -> int:
    copied = 0
    for name in RESULT_FILES:
        r = adb.run("pull", f"{app_files_dir(package)}/{name}", str(dest / name))
        if r.returncode == 0:
            copied += 1
            print(f"copied {name} ({(dest / name).stat().st_size} bytes)")
        else:
            print(f"{name}: not on the headset yet (no reconstruction logged?)")
    return copied


def cmd_pull(adb: Adb, args) -> int:
    if not require_device(adb):
        return 1
    package, _ = project_android_settings()
    if not package:
        print(f"Cannot read the app's package name from {PROJECT_SETTINGS}")
        return 1
    dest = QUEST_RESULTS / dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    dest.mkdir(parents=True, exist_ok=True)
    copied = pull_files(adb, package, dest)
    if not copied:
        dest.rmdir()
        return 1
    print(f"saved in {dest}")
    return 0


# ----------------------------------------------------------------------------- measured session
def clock_offset(adb: Adb, samples: int = 5) -> Optional[float]:
    """Headset clock - PC clock in seconds, from the `date` sample with the shortest adb round trip."""
    best = None
    for _ in range(samples):
        t0 = time.time()
        out = adb.shell("date", "+%s.%N")
        t1 = time.time()
        try:
            headset = float(out)
        except ValueError:
            continue
        if best is None or t1 - t0 < best[0]:
            best = (t1 - t0, headset - (t0 + t1) / 2)
    return None if best is None else round(best[1], 3)


class FpsRecorder:
    """Follows `adb logcat -v epoch -s VrApi` and appends one fps.csv row per FPS line, with the process name
    (several processes log VrApi lines: the system menu at 90 Hz, the app at 72 Hz, ...)."""

    def __init__(self, adb: Adb, out: Path, package: str):
        self.adb, self.out, self.package = adb, out, package
        self.names: dict[int, str] = {}
        self.last_app: Optional[dict] = None
        self.rows = 0
        self.proc: Optional[subprocess.Popen] = None
        self.thread: Optional[threading.Thread] = None

    def process_name(self, pid: int) -> str:
        if pid not in self.names:
            for line in self.adb.shell("ps", "-A", "-o", "PID,NAME").splitlines()[1:]:
                parts = line.split()
                if len(parts) >= 2 and parts[0].isdigit():
                    self.names[int(parts[0])] = parts[1]
            self.names.setdefault(pid, "?")
        return self.names[pid]

    def start(self) -> None:
        # -T 1: start from the newest line, not from what is still in the buffer
        self.proc = subprocess.Popen([self.adb.path, "logcat", "-T", "1", "-v", "epoch", "-s", "VrApi:I"],
                                     stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
                                     encoding="utf-8", errors="replace")
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self) -> None:
        with self.out.open("w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=FPS_FIELDS)
            w.writeheader()
            for line in self.proc.stdout:
                row = parse_vrapi_line(line)
                if row is None:
                    continue
                row["process"] = self.process_name(row["pid"])
                w.writerow(row)
                f.flush()
                self.rows += 1
                if row["process"] == self.package:
                    self.last_app = row

    def stop(self) -> None:
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
        if self.thread:
            self.thread.join(timeout=5)


def wait_for_server(url: str, seconds: float = 30) -> bool:
    deadline = time.time() + seconds
    while time.time() < deadline:
        if server_answers(url):
            return True
        time.sleep(0.5)
    return False


def copy_server_rows(src: Path, dest: Path, start: float, end: float) -> int:
    """Rows of the shared server log written during the session (PC clock)."""
    if not src.exists():
        return 0
    with src.open(encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return 0
    keep = [r for r in rows if start <= dt.datetime.fromisoformat(r["timestamp"]).timestamp() <= end]
    with dest.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(keep)
    return len(keep)


def cmd_session(adb: Adb, args) -> int:
    serial = require_device(adb)
    if not serial:
        return 1
    package, _ = project_android_settings()
    if not package:
        print(f"Cannot read the app's package name from {PROJECT_SETTINGS}")
        return 1
    if f"package:{package}" not in adb.shell("pm", "list", "packages", package).split():
        print(f"The app {package} is not installed: run install_app.bat first.")
        return 1

    # 1. link
    local = f"http://127.0.0.1:{args.port}"
    if args.link == "usb":
        r = adb.run("reverse", f"tcp:{args.port}", f"tcp:{args.port}")
        if r.returncode != 0:
            print(f"adb reverse failed: {r.stderr.strip()}")
            return 1
        adb.shell("rm", "-f", f"{app_files_dir(package)}/{OVERRIDE_FILE}")
        host, url = "127.0.0.1", local
        print(f"link: USB cable (adb reverse), the app uses {url}")
    else:
        addresses = lan_ipv4_addresses()
        if not addresses and not args.ip:
            print("No local network address found: is this PC connected to the Wi-Fi / LAN?")
            return 1
        ip = args.ip or addresses[0]
        if len(addresses) > 1 and not args.ip:
            print(f"This PC has several addresses ({', '.join(addresses)}): using {ip}. Use --ip to choose another.")
        url = f"http://{ip}:{args.port}"
        if not write_server_address(adb, url):  # also removes the USB tunnel
            return 1
        host = "0.0.0.0"
        print(f"link: Wi-Fi, the app uses {url}. Keep the cable plugged in: it only carries the fps log.")

    dest = QUEST_RESULTS / f"{dt.datetime.now():%Y%m%d_%H%M%S}_{args.link}"
    dest.mkdir(parents=True, exist_ok=True)
    offset_start = clock_offset(adb)
    if offset_start is not None:
        print(f"headset clock - PC clock: {offset_start:+.2f} s (stored; the summary corrects for it)")

    # 2. server, with a log of its own when this session starts it
    server_proc, server_log = None, dest / "edge_requests.csv"
    if server_answers(local):
        if args.link == "wifi" and not server_answers(url):
            print("An edge server is running but only for this PC (127.0.0.1): close its window, then run this again.")
            return 1
        print("An edge server is already running: keeping it; its rows of this session are copied at the end.")
    else:
        cmd = [sys.executable, "-m", "edge.server", "--host", host, "--port", str(args.port),
               "--log-csv", str(server_log), "--save-meshes", str(dest / "meshes"),
               *shlex.split(args.server_args)]
        print("Starting the edge server: " + " ".join(cmd[2:]))
        server_proc = subprocess.Popen(cmd, cwd=ROOT)
        if not wait_for_server(local, 120 if "model" in args.server_args else 30):
            print("The edge server did not start: see the messages above.")
            server_proc.terminate()
            return 1

    # 3. fps, then a fresh start of the app (it reads the server address at start)
    started = time.time()
    fps = FpsRecorder(adb, dest / "fps.csv", package)
    fps.start()
    if not args.no_restart:
        adb.shell("am", "force-stop", package)
        adb.shell("monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1")
        print("App restarted on the headset.")
    print("\nPut on the headset and look around the room: first all objects in view, then away and back.")
    print("Press Enter here to end the session.\n")

    stop = threading.Event()

    def report_fps():
        while not stop.wait(10):
            row = fps.last_app
            if row:
                print(f"[session] {time.time() - started:4.0f} s, app fps {row['fps']}/{row['fps_target']}",
                      flush=True)
            else:
                print(f"[session] {time.time() - started:4.0f} s, no fps line from the app yet "
                      f"({fps.rows} from other processes)", flush=True)

    threading.Thread(target=report_fps, daemon=True).start()
    try:
        input()
    except (EOFError, KeyboardInterrupt):
        pass
    ended = time.time()
    stop.set()
    fps.stop()

    # 4. results
    offset_end = clock_offset(adb)
    if server_proc:
        server_proc.terminate()
        server_proc.wait(timeout=10)
    else:
        print(f"copied {copy_server_rows(SERVER_LOG, server_log, started, ended)} rows of the server log")
    pull_files(adb, package, dest)
    model = adb.shell("getprop", "ro.product.model")
    session = {"link": args.link, "server_url": url, "package": package, "headset": model,
               "started_pc": started, "ended_pc": ended,
               "clock_offset_s": {"start": offset_start, "end": offset_end},
               "server_args": args.server_args, "app_restarted": not args.no_restart, "fps_rows": fps.rows}
    (dest / "session.json").write_text(json.dumps(session, indent=2), encoding="utf-8")
    out = write_report(dest)
    print(f"\nsaved in {dest}")
    print(f"summary: {out}")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=8000, help="edge server port (default 8000)")
    sub = ap.add_subparsers(dest="command", required=True)
    sub.add_parser("check", help="is the headset ready?")
    p = sub.add_parser("install", help="install the .apk built by Unity")
    p.add_argument("--apk", default=None, help=f"path of the .apk (default {APK.relative_to(ROOT)})")
    p.add_argument("--run", action="store_true", help="start the app after installing it")
    for name, text in (("usb", "reach the server through the USB cable"), ("wifi", "reach the server over Wi-Fi")):
        p = sub.add_parser(name, help=text)
        p.add_argument("--serve", action="store_true", help="then start the edge server in this window")
        p.add_argument("--server-args", default="--mock-delay-s 2", help="options for edge.server (default: mock, 2 s)")
        if name == "wifi":
            p.add_argument("--ip", default=None, help="this PC's address to give the headset (default: detected)")
    sub.add_parser("logs", help="live Unity logs from the headset")
    sub.add_parser("pull", help="copy the CSV timings from the headset")
    p = sub.add_parser("session", help="one measured session: link, server, fps, results and summary")
    p.add_argument("link", choices=("usb", "wifi"), help="how the app reaches the edge server")
    p.add_argument("--server-args", default="--mock-delay-s 2", help="options for edge.server (default: mock, 2 s)")
    p.add_argument("--ip", default=None, help="wifi: this PC's address to give the headset (default: detected)")
    p.add_argument("--no-restart", action="store_true", help="do not restart the app at the start of the session")
    args = ap.parse_args(argv)

    adb_path = find_adb()
    if adb_path is None:
        print("adb not found. It comes with Unity's Android Build Support (Unity Hub > Installs > Add modules), "
              f"or with Meta Quest Developer Hub. See {GUIDE}")
        return 1
    commands = {"check": cmd_check, "install": cmd_install, "usb": cmd_usb, "wifi": cmd_wifi, "logs": cmd_logs,
                "pull": cmd_pull, "session": cmd_session}
    return commands[args.command](Adb(adb_path), args)


if __name__ == "__main__":
    sys.exit(main())
