"""Summary of a Quest session folder (benchmarks/results/quest/<timestamp>[_usb|_wifi]/) as Markdown.

    python -m benchmarks.quest_report benchmarks/results/quest/20261111_112533
    python -m benchmarks.quest_report benchmarks/results/quest/*_usb benchmarks/results/quest/*_wifi

A folder written by `tools/quest/quest.py session` holds:
    edge_timings.csv, ondemand_events.csv   from the app (headset clock, local time; files are cumulative)
    edge_requests.csv                       server log of the session (PC clock), with the client address
    fps.csv                                 one VrApi line per second per process (headset clock, epoch seconds)
    session.json                            link (usb / wifi), session window (PC clock), headset - PC clock offset
Older folders (only the two app CSVs, from `quest.py pull`) are summarised too: no link, no fps.

Clocks: the headset sets its clock from the network, the PC may not (Windows Time service stopped), so the two
can differ by seconds. session.json stores the measured offset (headset - PC); without it the offset is estimated
by matching each app request with the server row of the same object.
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import re
import statistics
import sys
from pathlib import Path
from typing import Optional

FPS_FIELDS = ["headset_time", "pid", "process", "fps", "fps_target", "stale", "app_ms", "cpu_gpu_ms",
              "cpu_level", "gpu_level", "cpu_util", "gpu_util", "temp_c", "free_mb"]
SESSION_GAP_S = 60.0  # two app requests further apart than this are taken as different app runs
CLOCK_JUMP_S = 0.5  # headset - PC offset changing more than this between start and end: a clock was reset
MESH_WINDOW_S = 2.0  # fps samples this long after a mesh was shown are attributed to it (dips last 1-2 s)

# "  1794393384.400  8362  9205 I VrApi   : FPS=72/72,Prd=35ms,..."   (adb logcat -v epoch -s VrApi)
_LOGCAT = re.compile(r"^\s*(\d+\.\d+)\s+(\d+)\s+\d+\s+[VDIWEF]\s+VrApi\s*:\s*(.*)$")
_VRAPI = {
    "fps": (r"FPS=(\d+)/\d+", int), "fps_target": (r"FPS=\d+/(\d+)", int), "stale": (r"Stale=(\d+)", int),
    "app_ms": (r"App=([\d.]+)ms", float), "cpu_gpu_ms": (r"CPU&GPU=([\d.]+)ms", float),
    "cpu_level": (r"CPU4/GPU=(\d+)/\d+", int), "gpu_level": (r"CPU4/GPU=\d+/(\d+)", int),
    "cpu_util": (r"CPU%=(-?[\d.]+)", float), "gpu_util": (r"GPU%=(-?[\d.]+)", float),
    "temp_c": (r"Temp=([\d.]+)C", float), "free_mb": (r"Free=(\d+)MB", int),
}


def parse_vrapi_line(line: str) -> Optional[dict]:
    """One `adb logcat -v epoch -s VrApi` line with FPS=... -> dict (FPS_FIELDS minus process), else None."""
    m = _LOGCAT.match(line)
    if not m or "FPS=" not in m.group(3):
        return None
    row = {"headset_time": float(m.group(1)), "pid": int(m.group(2))}
    for key, (pattern, cast) in _VRAPI.items():
        found = re.search(pattern, m.group(3))
        row[key] = cast(found.group(1)) if found else None
    return row


# ----------------------------------------------------------------------------- loading
def _read_csv(path: Path) -> list[dict]:
    if not path.exists():
        return []
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def _epoch(local_iso: str) -> float:
    """Timestamps in the CSVs are local wall-clock time without zone (same zone on PC and headset)."""
    return dt.datetime.fromisoformat(local_iso).timestamp()


def _f(row: dict, key: str) -> Optional[float]:
    try:
        return float(row[key])
    except (KeyError, TypeError, ValueError):
        return None


def _session_offset(session: dict) -> Optional[float]:
    vals = [v for v in (session.get("clock_offset_s") or {}).values() if isinstance(v, (int, float))]
    return statistics.mean(vals) if vals else None


def _expected_server_time(h: dict, offset: float) -> float:
    """PC time at which the server logged the request (body received) of app row h (logged after the load)."""
    after_body_ms = (_f(h, "server_ms") or 0) + (_f(h, "network_ms") or 0) / 2 + (_f(h, "load_ms") or 0)
    return _epoch(h["timestamp"]) - offset - after_body_ms / 1e3


def estimate_offset(app_rows: list[dict], server_rows: list[dict], window_s: float = 120.0) -> Optional[float]:
    """headset - PC clock offset from same-object pairs: the densest 1 s cluster of differences, its median."""
    diffs = []
    for h in app_rows:
        for s in server_rows:
            if s.get("object_id") == h.get("label") and s.get("status", "ok") == "ok":
                d = _expected_server_time(h, 0.0) - _epoch(s["timestamp"])
                if abs(d) <= window_s:
                    diffs.append(d)
    if not diffs:
        return None
    diffs.sort()
    best = max((sum(1 for d in diffs if a <= d <= a + 1.0), a) for a in diffs)[1]
    return statistics.median([d for d in diffs if best <= d <= best + 1.0])


def match_server_rows(app_rows: list[dict], server_rows: list[dict], offset: float,
                      tolerance_s: float = 1.0) -> list[Optional[dict]]:
    """For every app request the server row of the same object nearest to the expected time (or None)."""
    used, out = set(), []
    for h in app_rows:
        t = _expected_server_time(h, offset)
        best, best_d = None, tolerance_s
        for i, s in enumerate(server_rows):
            if i in used or s.get("object_id") != h.get("label"):
                continue
            d = abs(_epoch(s["timestamp"]) - t)
            if d <= best_d:
                best, best_d = i, d
        if best is not None:
            used.add(best)
        out.append(server_rows[best] if best is not None else None)
    return out


# ----------------------------------------------------------------------------- statistics
def _median(vals) -> Optional[float]:
    vals = [v for v in vals if v is not None]
    return statistics.median(vals) if vals else None


def _range(vals, fmt="{:.0f}") -> str:
    vals = [v for v in vals if v is not None]
    if not vals:
        return "–"
    if len(vals) == 1:
        return fmt.format(vals[0])
    return f"mediana {fmt.format(statistics.median(vals))} ({fmt.format(min(vals))}-{fmt.format(max(vals))})"


def _first_of_run(rows: list[dict]) -> list[bool]:
    flags, prev = [], None
    for r in rows:
        t = _epoch(r["timestamp"])
        flags.append(prev is None or t - prev > SESSION_GAP_S)
        prev = t
    return flags


def link_of(client: str) -> str:
    if not client:
        return "?"
    return "cavo USB o editor" if client.startswith("127.") or client == "::1" else "Wi-Fi"


def summarize(folder: Path) -> str:
    folder = Path(folder)
    session = json.loads((folder / "session.json").read_text(encoding="utf-8")) \
        if (folder / "session.json").exists() else {}
    app = [r for r in _read_csv(folder / "edge_timings.csv") if r.get("source") == "on-demand"]
    events = _read_csv(folder / "ondemand_events.csv")
    server = _read_csv(folder / "edge_requests.csv")
    fps = _read_csv(folder / "fps.csv")

    offset = _session_offset(session)
    offset_note = "misurato con adb" if offset is not None else None
    if offset is None:
        offset = estimate_offset(app, server)
        offset_note = "stimato abbinando le richieste al log del server" if offset is not None else None

    start, end = session.get("started_pc"), session.get("ended_pc")
    if start and end and offset is not None:  # the app CSVs are cumulative: keep this session only
        app = [r for r in app if start + offset <= _epoch(r["timestamp"]) <= end + offset + 5]
        events = [r for r in events if start + offset <= _epoch(r["timestamp"]) <= end + offset + 5]

    lines = [f"# Sessione Quest: {folder.name}", ""]
    link = session.get("link")
    lines.append(f"- Collegamento: **{ {'usb': 'cavo USB', 'wifi': 'Wi-Fi'}.get(link, link) if link else 'non registrato'}**"
                 + (f" (server {session['server_url']})" if session.get("server_url") else ""))
    if start and end:
        lines.append(f"- Durata: {(end - start) / 60:.1f} min (dalle {dt.datetime.fromtimestamp(start):%H:%M:%S}, "
                     "orologio del PC)")
    if offset is not None:
        lines.append(f"- Orologio del visore - orologio del PC: {offset:+.2f} s ({offset_note})")
    measured = session.get("clock_offset_s") or {}
    if all(isinstance(measured.get(k), (int, float)) for k in ("start", "end")) and \
            abs(measured["end"] - measured["start"]) > CLOCK_JUMP_S:
        lines.append(f"- **Attenzione**: la differenza tra gli orologi è cambiata durante la sessione "
                     f"({measured['start']:+.2f} s all'inizio, {measured['end']:+.2f} s alla fine: un orologio è stato "
                     "risincronizzato?). Gli abbinamenti tra righe del visore e del PC possono essere sbagliati: "
                     "meglio ripetere la sessione.")
    if session.get("headset"):
        lines.append(f"- Visore: {session['headset']}")
    lines.append(f"- Richieste all'Edge: {len(app)}; mesh dalla cache: "
                 f"{sum(1 for e in events if e.get('source') == 'cache')}")
    lines.append("")

    if app:
        first = _first_of_run(app)
        lines += ["## Tempi per richiesta (misurati dall'app)", "",
                  f"Prima richiesta di ogni avvio dell'app (pausa > {SESSION_GAP_S:.0f} s) separata dalle altre.", "",
                  "| Misura | Prima richiesta | Richieste successive |", "|---|---|---|"]
        for key, label in (("network_ms", "Rete (andata e ritorno - server), ms"),
                           ("load_ms", "Caricamento del .glb, ms"), ("server_ms", "Server, ms"),
                           ("total_ms", "Totale, ms")):
            a = [_f(r, key) for r, f in zip(app, first) if f]
            b = [_f(r, key) for r, f in zip(app, first) if not f]
            lines.append(f"| {label} | {_range(a)} | {_range(b)} |")
        lines.append("")
        if offset is not None and server:
            matched = match_server_rows(app, server, offset)
            clients = {}
            for s in matched:
                key = s.get("client") or "" if s else None
                clients[key] = clients.get(key, 0) + 1
            parts = []
            for c, n in clients.items():
                if c is None:
                    parts.append(f"{n} senza riga del server")
                elif c == "":
                    parts.append(f"{n} con indirizzo non registrato (server di una versione precedente)")
                else:
                    parts.append(f"{n} da {c} ({link_of(c)})")
            lines += ["Provenienza delle richieste secondo il server: " + "; ".join(parts) + ".", ""]

    edge_ev = [e for e in events if e.get("source") == "edge"]
    cache_ev = [e for e in events if e.get("source") == "cache"]
    if events:
        lines += ["## Dall'ingresso in vista alla mesh", "",
                  "- Dall'Edge (in ordine): " + (", ".join(f"{e['object']} {_f(e, 'visible_to_mesh_ms') / 1e3:.1f} s"
                                                         for e in edge_ev) or "–"),
                  f"- Dalla cache: {_range([_f(e, 'visible_to_mesh_ms') for e in cache_ev], '{:.1f}')} ms", ""]

    package = session.get("package")
    app_fps = [r for r in fps if not package or r.get("process") == package]
    if start and end and offset is not None:
        app_fps = [r for r in app_fps if start + offset <= float(r["headset_time"]) <= end + offset]
    lines += ["## Frame rate (VrApi, un campione al secondo)", ""]
    if not app_fps:
        if not (folder / "fps.csv").exists():
            note = "Nessun dato: gli fps si registrano solo con `quest.py session`."
        elif not fps:
            note = ("Nessuna riga VrApi registrata: il visore era in standby (non indossato) o il cavo è stato "
                    "staccato durante la sessione.")
        else:
            note = (f"Nessuna riga del processo dell'app ({package}) nella sessione: processi registrati: "
                    f"{', '.join(sorted({r.get('process', '?') for r in fps}))}.")
        lines += [note, ""]
    else:
        target = _median([_f(r, "fps_target") for r in app_fps]) or 72
        startup, steady = split_startup(app_fps, target)
        lines += [f"- Obiettivo {target:.0f} fps; campioni: {len(app_fps)} s, di cui {len(startup)} di avvio "
                  "dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto"]
        if startup:
            lines.append(f"- Avvio: fps {', '.join(str(int(_f(r, 'fps') or 0)) for r in startup)}; frame scartati "
                         f"{sum(int(_f(r, 'stale') or 0) for r in startup)}")
        if steady:
            f_vals = [_f(r, "fps") for r in steady]
            low = sum(1 for v in f_vals if v is not None and v < 0.9 * target)
            edge_t = [_epoch(e["timestamp"]) for e in edge_ev]
            cache_t = [_epoch(e["timestamp"]) for e in cache_ev]

            def after(times, r):  # the sample's second overlaps the 2 s after a mesh was shown
                return any(0.0 <= float(r["headset_time"]) - t <= MESH_WINDOW_S for t in times)

            near_first = [_f(r, "fps") for r in steady if after(edge_t[:1], r)]
            near_edge = [_f(r, "fps") for r in steady if after(edge_t[1:], r) and not after(edge_t[:1], r)]
            near_cache = [_f(r, "fps") for r in steady if after(cache_t, r) and not after(edge_t, r)]
            quiet = [_f(r, "fps") for r in steady if not after(edge_t + cache_t, r)]
            lines += [f"- fps a regime: {_range(f_vals)}; secondi sotto il 90% dell'obiettivo: {low} "
                      f"({100 * low / len(steady):.0f}%)",
                      f"- fps nei {MESH_WINDOW_S:.0f} s dopo la prima mesh dall'Edge: {_range(near_first)}; dopo le "
                      f"altre dall'Edge: {_range(near_edge)}; dopo quelle dalla cache: {_range(near_cache)}; "
                      f"lontano dalle mesh: {_range(quiet)}",
                      f"- Frame scartati (Stale) a regime: {sum(int(_f(r, 'stale') or 0) for r in steady)}",
                      f"- Tempo dell'app per frame (App): {_range([_f(r, 'app_ms') for r in steady], '{:.1f}')} ms",
                      f"- Uso CPU / GPU: {_range([_f(r, 'cpu_util') for r in steady], '{:.2f}')} / "
                      f"{_range([_f(r, 'gpu_util') for r in steady], '{:.2f}')}",
                      f"- Temperatura massima: {max((_f(r, 'temp_c') or 0) for r in steady):.0f} °C"]
        lines.append("")
    return "\n".join(lines)


def split_startup(app_fps: list[dict], target: float) -> tuple[list[dict], list[dict]]:
    """Leading samples of the app's launch (black frames, system shell handing over, a first App time of minutes)
    up to the first second at >= 90% of the target with no stale frame."""
    for i, r in enumerate(app_fps):
        if (_f(r, "fps") or 0) >= 0.9 * target and not (_f(r, "stale") or 0):
            return app_fps[:i], app_fps[i:]
    return app_fps, []


def write_report(folder: Path) -> Path:
    out = Path(folder) / "summary.md"
    out.write_text(summarize(folder), encoding="utf-8")
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="+", type=Path)
    ap.add_argument("--print", action="store_true", help="also print the summary")
    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")  # Windows console (cp1252)
    for folder in args.folders:
        if not folder.is_dir():
            print(f"not a folder: {folder}", file=sys.stderr)
            return 1
        out = write_report(folder)
        print(f"written {out}")
        if args.print:
            print(out.read_text(encoding="utf-8"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
