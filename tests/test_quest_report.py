"""Quest session report: VrApi parsing, clock offset, matching app requests to server rows (no headset needed)."""
import csv
import datetime as dt
import json

from benchmarks.quest_report import (FPS_FIELDS, estimate_offset, match_server_rows, parse_vrapi_line, split_startup,
                                     summarize)

# a real line from a Quest 2 (adb logcat -v epoch -s VrApi), shortened at the end
LINE = ("         1794393384.004  8362  9205 I VrApi   : FPS=72/72,Prd=35ms,Tear=0,Early=0,Stale=0,"
        "Stale2/5/10/max=0/0/0/0,VSnc=1,Lat=-1,Fov=0,CPU4/GPU=2/3,1171/441MHz,OC=FF,TA=0/70/70,SP=N/N/N,"
        "Mem=1804MHz,Free=2455MB,PLS=0,Temp=23.0C/0.0C,TW=2.25ms,App=6.31ms,GD=0.00ms,CPU&GPU=13.14ms,"
        "LCnt=2(DR72,LM2),GPU%=0.64,CPU%=0.19(W0.23),CFL=12.33/18.11,ICFLp95=15.47,LD=0,SF=1.00,LP=0,DVFS=0")


def test_parse_vrapi_line():
    row = parse_vrapi_line(LINE)
    assert row == {"headset_time": 1794393384.004, "pid": 8362, "fps": 72, "fps_target": 72, "stale": 0,
                   "app_ms": 6.31, "cpu_gpu_ms": 13.14, "cpu_level": 2, "gpu_level": 3, "cpu_util": 0.19,
                   "gpu_util": 0.64, "temp_c": 23.0, "free_mb": 2455}
    assert set(row) | {"process"} == set(FPS_FIELDS)


def test_parse_vrapi_ignores_other_lines():
    assert parse_vrapi_line("--------- beginning of main") is None
    assert parse_vrapi_line("  1794393384.004  8362  9205 I VrApi   : Kill pid 123") is None
    assert parse_vrapi_line("11-11 11:16:34.004  8362  9205 I VrApi   : FPS=72/72") is None  # not -v epoch


T0 = dt.datetime(2026, 11, 11, 11, 14, 30)
OFFSET = 14.1  # headset clock ahead of the PC


def _iso(t: dt.datetime) -> str:
    return t.isoformat(timespec="milliseconds")


def _rows(n=4, offset=OFFSET):
    """App rows (headset clock, logged after the load) and the server rows of the same requests (PC clock)."""
    app, server = [], []
    for i in range(n):
        received = T0 + dt.timedelta(seconds=2.1 * i)
        server.append({"timestamp": _iso(received), "object_id": f"obj{i}", "status": "ok", "client": "127.0.0.1"})
        logged = received + dt.timedelta(seconds=offset + (2010 + 25 + 28) / 1e3)
        app.append({"timestamp": _iso(logged), "label": f"obj{i}", "source": "on-demand", "server_ms": "2010",
                    "network_ms": "50", "load_ms": "28", "total_ms": "2088"})
    return app, server


def test_estimate_offset_and_match():
    app, server = _rows()
    server.insert(0, {"timestamp": _iso(T0 - dt.timedelta(minutes=8)), "object_id": "obj0", "status": "ok"})  # editor
    assert abs(estimate_offset(app, server) - OFFSET) < 0.01
    matched = match_server_rows(app, server, OFFSET)
    assert [m["object_id"] for m in matched] == ["obj0", "obj1", "obj2", "obj3"]
    assert all(m["client"] == "127.0.0.1" for m in matched)
    assert match_server_rows(app, server, OFFSET + 30) == [None] * 4  # wrong clock: nothing matches


def _write(path, rows, fields=None):
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields or list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)


def test_summarize_session_folder(tmp_path):
    app, server = _rows()
    _write(tmp_path / "edge_timings.csv", app)
    _write(tmp_path / "edge_requests.csv", server)
    start = T0.timestamp() - 5
    fps = [{"headset_time": start + OFFSET + 1 + s, "pid": 1, "process": "com.x.app", "fps": 72 if s % 4 else 60,
            "fps_target": 72, "stale": 1 if s == 3 else 0, "app_ms": 6.0, "temp_c": 30} for s in range(20)]
    fps.append({"headset_time": start + OFFSET + 2, "pid": 2, "process": "com.oculus.vrshell", "fps": 90,
                "fps_target": 90})
    _write(tmp_path / "fps.csv", fps, FPS_FIELDS)
    (tmp_path / "session.json").write_text(json.dumps(
        {"link": "usb", "package": "com.x.app", "started_pc": start, "ended_pc": start + 30,
         "clock_offset_s": {"start": OFFSET, "end": OFFSET}}))
    text = summarize(tmp_path)
    assert "cavo USB" in text and "+14.10 s (misurato con adb)" in text
    assert "4 da 127.0.0.1" in text
    assert "Obiettivo 72 fps; campioni: 20 s, di cui 1 di avvio" in text  # the shell's 90 Hz line is left out
    assert "- Avvio: fps 60; frame scartati 0" in text
    assert "secondi sotto il 90% dell'obiettivo: 4 (21%)" in text
    assert "Frame scartati (Stale) a regime: 1" in text


def test_split_startup_keeps_the_launch_apart():
    # first seconds of the app on the Quest 2: one black frame, then stale frames, then steady 72 fps
    rows = [{"fps": "1", "stale": "43"}, {"fps": "75", "stale": "15"}, {"fps": "72", "stale": "0"},
            {"fps": "50", "stale": "24"}, {"fps": "72", "stale": "0"}]
    startup, steady = split_startup(rows, 72)
    assert len(startup) == 2 and len(steady) == 3  # a later dip stays in the steady part
    assert split_startup(rows[:2], 72) == (rows[:2], [])


def test_summarize_old_folder_without_session(tmp_path):
    app, _ = _rows(2)
    _write(tmp_path / "edge_timings.csv", app)
    text = summarize(tmp_path)
    assert "non registrato" in text and "gli fps si registrano solo con" in text


def test_summarize_warns_when_a_clock_jumps(tmp_path):
    app, _ = _rows(2)
    _write(tmp_path / "edge_timings.csv", app)
    session = {"link": "wifi", "started_pc": T0.timestamp() - 5, "ended_pc": T0.timestamp() + 30,
               "clock_offset_s": {"start": OFFSET, "end": OFFSET}}
    (tmp_path / "session.json").write_text(json.dumps(session))
    assert "Attenzione" not in summarize(tmp_path)
    session["clock_offset_s"] = {"start": OFFSET, "end": 0.05}  # PC clock resynchronised during the session
    (tmp_path / "session.json").write_text(json.dumps(session))
    assert "+14.10 s all'inizio, +0.05 s alla fine" in summarize(tmp_path)


def test_summary_lists_accuracy_and_failures(tmp_path):
    app, server = _rows(2)
    server[0].update(status="ok", model="meshanything", triangles="340", server_ms="66400", fscore="0.7011",
                     precision="0.5968", recall="0.8496", chamfer_l2="0.001010", quality_ms="82.0")
    server[1].update(status="error 504", server_ms="121672.5")
    _write(tmp_path / "edge_timings.csv", app[:1])
    _write(tmp_path / "edge_requests.csv", server, list(server[0].keys()))
    text = summarize(tmp_path)
    assert "| obj0 | meshanything | 340 | 66.4 | 0.70 | 0.60 | 0.85 | 0.00101 |" in text
    assert "82 ms" in text and "obj1 (error 504, dopo 121.7 s)" in text


def test_clocks_line_up_a_server_on_another_pc_in_another_time_zone(tmp_path):
    from benchmarks.quest_report import Clocks

    headset_tz = 3600.0                      # the headset writes local times at UTC+1
    server_ahead = 7.5                       # the lab PC's clock is 7.5 s ahead of this PC
    app, server = _rows(3, offset=0.0)       # app rows logged on the headset clock = PC clock here
    clocks = Clocks(headset_utc_offset_s=headset_tz, server_offset_s=server_ahead)
    for s in server:                         # the server logs its own clock as t_epoch, and a UTC local time
        t_pc = Clocks(headset_utc_offset_s=headset_tz).app(s["timestamp"])
        s["t_epoch"] = f"{t_pc + server_ahead:.3f}"
        s["timestamp"] = "2000-01-01T00:00:00.000"   # nonsense: must not be used when t_epoch is there
    matched = match_server_rows(app, server, 0.0, clocks=clocks)
    assert [m["object_id"] for m in matched] == ["obj0", "obj1", "obj2"]
    assert abs(estimate_offset(app, server, clocks=clocks)) < 0.01


def test_summary_of_a_session_with_the_server_on_another_pc(tmp_path):
    app, server = _rows(2)
    _write(tmp_path / "edge_timings.csv", app)
    (tmp_path / "session.json").write_text(json.dumps(
        {"link": "wifi", "server_remote": True, "server_clock_offset_s": {"start": 2.0, "end": 2.2},
         "server": {"label": "MeshAnything V2", "device_name": "NVIDIA GeForce RTX 4090"}}))
    text = summarize(tmp_path)
    assert "Server su un altro PC: MeshAnything V2 su NVIDIA GeForce RTX 4090" in text and "+2.10 s" in text
