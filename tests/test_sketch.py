"""MeshPad-style sketches (mesh_wrappers/sketch.py) and the yaw-aligned accuracy (no weights, no GPU)."""
import io

import numpy as np
import pytest
import trimesh

pytest.importorskip("open3d")

from mesh_wrappers import sketch  # noqa: E402
from mesh_wrappers.mesh_io import quality_best_yaw, quality_metrics, rotate_y  # noqa: E402


def box_mesh():
    return trimesh.creation.box(extents=(1.0, 0.6, 0.4))


def test_camera_as_meshpad_angle2location():
    eye = sketch.camera_location(30, 45)
    assert np.isclose(np.linalg.norm(eye), np.sqrt(2.5))
    assert np.isclose(np.degrees(np.arcsin(eye[1] / np.linalg.norm(eye))), 30)
    assert np.allclose(sketch.camera_location(0, 0), [np.sqrt(2.5), 0, 0])  # [cam_y, cam_z, cam_x] at azimuth 0
    assert np.allclose(sketch.camera_location(0, 90), [0, 0, -np.sqrt(2.5)])


def test_box_sketch_is_thin_lines_inside_the_frame():
    m = box_mesh()
    strokes = sketch.render_sketch(m.vertices, m.faces)
    assert strokes.shape == (224, 224) and strokes.dtype == bool
    assert 0.005 < strokes.mean() < 0.06  # lines, not a filled silhouette
    assert not strokes[:3].any() and not strokes[-3:].any()  # the object is scaled to stay inside the image


def test_png_roundtrip_is_exact_and_small():
    m = box_mesh()
    strokes = sketch.render_sketch(m.vertices, m.faces)
    png = sketch.sketch_to_png(strokes)
    assert png[:8] == b"\x89PNG\r\n\x1a\n" and len(png) < 4096
    assert np.array_equal(sketch.load_sketch(png), strokes)


def test_load_sketch_composites_on_white_and_resizes():
    from PIL import Image

    rgba = np.zeros((448, 448, 4), np.uint8)  # transparent everywhere = white paper (place_on_white_background)
    rgba[200:248, 100:300] = (0, 0, 0, 255)  # one opaque black bar
    buf = io.BytesIO()
    Image.fromarray(rgba, "RGBA").save(buf, format="PNG")
    strokes = sketch.load_sketch(buf.getvalue())
    assert strokes.shape == (224, 224)
    assert strokes[100:124, 50:150].all() and int(strokes.sum()) == 24 * 100


def test_load_sketch_rejects_non_images():
    with pytest.raises(ValueError, match="not an image"):
        sketch.load_sketch(b"this is not a png")


def test_sketch_tensor_white_is_one_black_is_zero():
    strokes = np.zeros((224, 224), bool)
    strokes[0, 0] = True
    t = sketch.sketch_tensor(strokes)
    assert tuple(t.shape) == (1, 3, 224, 224)
    assert float(t[0, :, 0, 0].abs().sum()) == 0 and float(t[0, :, 1, 1].min()) == 1


def test_quality_best_yaw_undoes_a_rotation():
    m = box_mesh()
    ref, _ = trimesh.sample.sample_surface(m, 5000, seed=0)
    turned = m.copy()
    turned.vertices = rotate_y(m.vertices, 90)  # the long side on the wrong horizontal axis
    unturned = quality_metrics(m, ref)["fscore@0.01"]  # < 1 only because the reference cloud is sparse
    assert quality_metrics(turned, ref)["fscore@0.01"] < 0.5
    _, q = quality_best_yaw(turned, ref)
    assert abs(q["fscore@0.01"] - unturned) < 0.02 and q["yaw_deg"] in (90.0, 270.0)
