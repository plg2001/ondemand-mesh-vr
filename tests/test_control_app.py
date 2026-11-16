"""EdgeControl.exe (tools/control): built from the sources that are in the repository, and the command lines it runs
are accepted by the scripts it calls (edge.server, tools/quest/quest.py, tools/setup_pc.py), so that renaming an option
in a script does not break a button that is only noticed when clicked. No Windows needed: the sources are only read."""
import importlib.util
import re
import shlex
from pathlib import Path

from edge import server

ROOT = Path(__file__).resolve().parents[1]
SOURCES = sorted((ROOT / "tools" / "control" / "src").glob("*.cs"))


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


build = _load("control_build", ROOT / "tools" / "control" / "build.py")
quest = _load("quest", ROOT / "tools" / "quest" / "quest.py")


def test_exe_is_built_from_these_sources():
    stamp = build.read_stamp()
    assert SOURCES and build.EXE.exists(), "EdgeControl.exe missing: python tools/control/build.py (on Windows)"
    assert stamp.get("sources") == build.sources_digest(), "sources changed after the build: python tools/control/build.py"
    assert stamp.get("exe") == build.file_digest(build.EXE), "EdgeControl.exe is not the one built: python tools/control/build.py"


def test_every_option_the_panel_passes_exists():
    known = set()
    for script in ("edge/server.py", "tools/quest/quest.py", "tools/setup_pc.py", "benchmarks/requirements_tables.py"):
        known |= set(re.findall(r'add_argument\(\s*"(--[a-z0-9-]+)"', (ROOT / script).read_text(encoding="utf-8")))
    used = set()
    for path in SOURCES:  # string literals that start with an option: "--backend", "--server-args=" + ...
        used |= set(re.findall(r'"(--[a-z][a-z0-9-]*)(?=[="])', path.read_text(encoding="utf-8")))
    own = {"--snapshot", "--query-gpu", "--format"}  # EdgeControl's own option, nvidia-smi's
    assert used - own, "no option found in the sources: the pattern above is out of date"
    assert not used - own - known, f"options that no script accepts: {sorted(used - own - known)}"


def test_server_options_reach_edge_server_intact():
    # quest.py takes them as one --server-args=... value, splits it with shlex, and edge.server must accept the result
    value = "--backend model --model meshanything --time-budget-s 300 --objects box_01 --no-quality"
    args = quest.build_parser().parse_args(["--port", "8000", "wifi", "--serve", "--ip", "192.168.1.5", f"--server-args={value}"])
    assert (args.command, args.serve, args.ip, args.server_args) == ("wifi", True, "192.168.1.5", value)
    parsed = server.parse_args(shlex.split(args.server_args))
    assert (parsed.backend, parsed.model, parsed.time_budget_s, parsed.objects, parsed.no_quality) == \
        ("model", "meshanything", 300, "box_01", True)
    classical = server.parse_args(["--port", "8000", "--backend", "classical", "--method", "ball_pivoting",
                                   "--target-faces", "1000", "--save-meshes", "benchmarks/results/edge_meshes"])
    assert (classical.method, classical.target_faces) == ("ball_pivoting", 1000)


def test_scene_objects_the_panel_offers_exist_in_the_unity_scene():
    # the panel reads them from the scene.json the app on the headset uses; --objects must name those ids
    scene = (ROOT / "unity" / "OnDemandMeshVR" / "Assets" / "StreamingAssets" / "Scenes" / "demo_room" / "scene.json")
    ids = re.findall(r'"id":\s*"([^"]+)"', scene.read_text(encoding="utf-8"))
    assert "box_01" in ids and len(ids) == len(set(ids)) >= 2
