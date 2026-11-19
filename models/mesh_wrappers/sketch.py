"""MeshPad-style sketches: line drawings of a mesh, made the way MeshPad's training data and demo make them.

MeshPad (github.com/DerKleineLi/meshpad-release: meshpad/utils/pyrender_sketch.py and render_sketch() in
demo/gradio_demo.py) is conditioned on a 224x224 RGB image with black strokes on white, obtained by Canny edge
detection on a depth render and on a camera-space normal render (flat shading) of the mesh. The mesh is scaled so that
its farthest vertex is at 0.5 and seen from distance sqrt(2.5), vertical field of view pi/4.8 (37.5 deg), looking at
the origin with +Y up. The demo's default view is azimuth 45 deg, elevation 30 deg (training sampled azimuths in
-90..90 and elevations in 0..60). The two renders come from Open3D ray casting instead of OpenGL: no display needed.

Frames: MeshPad tokenises and generates meshes in the same +Y-up frame its sketches are rendered in (the demo's "gen"
frame: process_mesh() and render_sketch() apply rotate_gt2gen to the meshes the demo stores, and get_mesh_from_output_verts()
turns the model's output back with rotate_gen2gt; its +Z-up "gt" frame is only that storage). Our objects are +Y up
(glTF): they are rendered as they are, and a generated mesh is already in their frame.
"""
from __future__ import annotations

import io
from pathlib import Path
from typing import Union

import numpy as np

SIZE = 224
YFOV = np.pi / 4.8
DISTANCE = float(np.sqrt(2.5))
DEFAULT_AZIMUTH = 45.0
DEFAULT_ELEVATION = 30.0


def camera_location(elevation_deg: float = DEFAULT_ELEVATION, azimuth_deg: float = DEFAULT_AZIMUTH) -> np.ndarray:
    """MeshPad's angle2location(): returns [cam_y, cam_z, cam_x] of its spherical formula, in a +Y-up world."""
    z, xy = np.radians(elevation_deg), np.radians(azimuth_deg)
    horizontal = DISTANCE * np.cos(z)
    return np.array([horizontal * np.cos(xy), DISTANCE * np.sin(z), -horizontal * np.sin(xy)])


def render_depth_normals(vertices: np.ndarray, faces: np.ndarray, eye: np.ndarray,
                         size: int = SIZE) -> tuple[np.ndarray, np.ndarray]:
    """z-depth (0 = background) and camera-space normals mapped to [0, 1] (faces flipped towards the camera, background
    white), as MeshPad's shader (shaders/mesh.frag) and render_depth_and_normals() produce them."""
    import open3d as o3d

    forward = -eye / np.linalg.norm(eye)
    right = np.cross(forward, [0.0, 1.0, 0.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, forward)
    f = 0.5 * size / np.tan(0.5 * YFOV)
    u, v = np.meshgrid(np.arange(size) + 0.5, np.arange(size) + 0.5)
    x, y = (u - size / 2) / f, -(v - size / 2) / f  # image rows go down, camera y goes up
    dirs = x[..., None] * right + y[..., None] * up + forward  # unit forward component: t_hit is the z-depth
    rays = np.concatenate([np.broadcast_to(eye, dirs.shape), dirs], axis=-1).astype(np.float32)

    mesh = o3d.t.geometry.TriangleMesh()
    mesh.vertex.positions = o3d.core.Tensor(np.asarray(vertices, dtype=np.float32))
    mesh.triangle.indices = o3d.core.Tensor(np.asarray(faces, dtype=np.int32))
    scene = o3d.t.geometry.RaycastingScene()
    scene.add_triangles(mesh)
    hit = scene.cast_rays(o3d.core.Tensor(rays))
    t = hit["t_hit"].numpy()
    inside = np.isfinite(t)
    depth = np.where(inside, t, 0.0).astype(np.float32)

    n = hit["primitive_normals"].numpy()
    n_cam = np.stack([n @ right, n @ up, n @ -forward], axis=-1)  # OpenGL view space: z towards the viewer
    n_cam[~inside] = -1.0  # background colour 0 -> -1 after the shader mapping
    flip = n_cam[..., 2] < 0  # faces seen from behind are flipped: orientation does not matter
    n_cam[flip] = -n_cam[flip]
    normal = np.round((n_cam + 1) / 2 * 255) / 255  # 8-bit render target
    return depth, normal


def sketch_from_renders(depth: np.ndarray, normal: np.ndarray) -> np.ndarray:
    """Strokes (True) = Canny edges of the depth image OR of the grey normal image (MeshPad's recipe)."""
    from skimage import feature
    from skimage.color import rgb2gray

    depth_edges = feature.canny(depth)
    normal_edges = feature.canny(rgb2gray(normal))
    normal_edges[1, 1:-1] = 0  # Canny artefacts along the image border
    normal_edges[1:-1, 1] = 0
    normal_edges[1:-1, -2] = 0
    normal_edges[-2, 1:-1] = 0
    return depth_edges | normal_edges


def render_sketch(vertices: np.ndarray, faces: np.ndarray, azimuth_deg: float = DEFAULT_AZIMUTH,
                  elevation_deg: float = DEFAULT_ELEVATION) -> np.ndarray:
    """Sketch of a +Y-up mesh centred at the origin: (224, 224) bool, True = stroke. Scaled as render_sketch()."""
    v = np.asarray(vertices, dtype=np.float64)
    v = v / max(float(np.linalg.norm(v, axis=1).max()) * 2, 1.0)
    depth, normal = render_depth_normals(v, faces, camera_location(elevation_deg, azimuth_deg))
    return sketch_from_renders(depth, normal)


def sketch_to_png(strokes: np.ndarray) -> bytes:
    """1-bit PNG, white background, black strokes (about 1-2 KB for 224x224)."""
    from PIL import Image

    buf = io.BytesIO()
    Image.fromarray(~np.asarray(strokes, dtype=bool)).convert("1").save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def load_sketch(source: Union[bytes, str, Path, np.ndarray]) -> np.ndarray:
    """Any image (PNG bytes, a file, an array) -> (224, 224) bool strokes. Transparent pixels count as white (as the
    demo's place_on_white_background), other sizes are resized with nearest neighbour (its resize4inference); a pixel
    is a stroke when its grey value is below 50%. Raises ValueError for data that is not an image."""
    from PIL import Image, UnidentifiedImageError

    if isinstance(source, np.ndarray):
        arr = np.asarray(source)
        if arr.dtype == bool:
            img = Image.fromarray(~arr)
        else:
            img = Image.fromarray(arr.astype(np.uint8))
    else:
        try:
            img = Image.open(io.BytesIO(source) if isinstance(source, (bytes, bytearray)) else source)
            img.load()
        except (UnidentifiedImageError, OSError) as e:
            raise ValueError(f"not an image: {e}") from e
    img = img.convert("RGBA")
    white = Image.new("RGBA", img.size, (255, 255, 255, 255))
    img = Image.alpha_composite(white, img).convert("L")
    if img.size != (SIZE, SIZE):
        img = img.resize((SIZE, SIZE), Image.NEAREST)
    return np.asarray(img) < 128


def sketch_tensor(strokes: np.ndarray):
    """(1, 3, 224, 224) float32 tensor in [0, 1], white = 1: what MeshPad's pil_to_tensor() gives the model."""
    import torch

    img = np.where(np.asarray(strokes, dtype=bool), 0.0, 1.0).astype(np.float32)
    return torch.from_numpy(np.repeat(img[None, None], 3, axis=1))
