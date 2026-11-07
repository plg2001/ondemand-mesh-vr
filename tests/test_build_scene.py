"""Scene builder: per-object local clouds, poses (on-floor, rotation), Unity copy. Primitives only: offline."""
import importlib.util
import json
from pathlib import Path

import numpy as np
import pytest

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("build_scene", ROOT / "data" / "scenes" / "build_scene.py")
build_scene_mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(build_scene_mod)

CONFIG = {
    "name": "test_room", "points_per_object": 256, "reference_points": 1000, "seed": 0,
    "objects": [
        {"id": "box_a", "shape": "box", "position": [0.5, 0.0, 2.0], "rotation_y_deg": 90, "size": 0.8},
        {"id": "box_b", "shape": "box", "position": [-0.5, 1.0, 3.0], "size": 0.5, "on_floor": False},
        {"id": "ball", "shape": "sphere", "position": [0.0, 0.2, 1.5], "size": 0.4, "points": 100},
    ],
}


@pytest.fixture
def scene(tmp_path):
    out, unity = tmp_path / "out", tmp_path / "unity"
    return build_scene_mod.build_scene(CONFIG, out, unity, verbose=False), out, unity


def test_files_and_totals(scene):
    s, out, unity = scene
    assert [e["id"] for e in s["objects"]] == ["box_a", "box_b", "ball"]
    for e in s["objects"]:
        pts = np.loadtxt(out / e["points"], ndmin=2)
        assert pts.shape == (e["point_count"], 3)
        assert np.load(out / e["reference"]).shape == (1000, 6)
        assert (unity / e["points"]).exists() and not (unity / e["reference"]).exists()  # headset: sparse only
    assert s["objects"][2]["point_count"] == 100  # per-object override
    assert s["totals"]["points"] == 256 + 256 + 100
    assert s["totals"]["bytes_xyz_f32"] == (256 + 256 + 100) * 12
    assert json.loads((unity / "scene.json").read_text()) == s


def test_points_are_in_the_local_canonical_frame(scene):
    s, out, _ = scene
    pts = np.loadtxt(out / "box_a.xyz")
    # box extents (1.0, 0.6, 0.4) canonicalised: centred, longest side 1, independent of the scene pose
    assert np.all(pts.min(0) >= np.array([-0.5, -0.3, -0.2]) - 1e-5)
    assert np.all(pts.max(0) <= np.array([0.5, 0.3, 0.2]) + 1e-5)
    assert s["objects"][0]["local_bounds_max"] == [0.5, 0.3, 0.2]


def test_pose_on_floor_rotation_and_scale(scene):
    s, _, _ = scene
    a, b, ball = s["objects"]
    assert a["scale"] == [0.8, 0.8, 0.8]
    np.testing.assert_allclose(a["rotation"], [0, np.sin(np.pi / 4), 0, np.cos(np.pi / 4)], atol=1e-6)  # xyzw
    assert a["position"][1] == pytest.approx(0.3 * 0.8)   # half height: the bottom touches y = 0
    assert b["position"] == [-0.5, 1.0, 3.0]                # on_floor: false keeps the given pose
    assert ball["position"][1] == pytest.approx(0.2 + 0.5 * 0.4, abs=1e-3)  # floor at 0.2, radius 0.2


def test_unity_copy_drops_stale_clouds(tmp_path):
    unity = tmp_path / "unity"
    unity.mkdir()
    (unity / "old_object.xyz").write_text("0 0 0\n")
    (unity / "old_object.xyz.meta").write_text("meta")
    build_scene_mod.build_scene(CONFIG, tmp_path / "out", unity, verbose=False)
    assert not (unity / "old_object.xyz").exists() and not (unity / "old_object.xyz.meta").exists()


@pytest.mark.parametrize("bad,msg", [
    ({"objects": []}, "no objects"),
    ({"objects": [{"id": "a", "shape": "box"}, {"id": "a", "shape": "box"}]}, "unique"),
    ({"objects": [{"id": "a", "shape": "teapot"}]}, "unknown shape"),
])
def test_invalid_configs(tmp_path, bad, msg):
    with pytest.raises(ValueError, match=msg):
        build_scene_mod.build_scene(bad, tmp_path, None, verbose=False)
