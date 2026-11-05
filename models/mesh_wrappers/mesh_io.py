"""Mesh construction from triangle soups, cleanup, export (.obj/.glb), statistics and quality."""
from __future__ import annotations

from pathlib import Path
from typing import Iterable, Optional

import numpy as np
import trimesh


def triangles_to_mesh(triangles: np.ndarray) -> trimesh.Trimesh:
    """(F,3,3) triangle soup -> cleaned trimesh, same cleanup the upstream repos apply."""
    tri = np.asarray(triangles, dtype=np.float64).reshape(-1, 3, 3)
    tri = tri[np.all(np.isfinite(tri.reshape(-1, 9)), axis=1)]
    if len(tri) == 0:
        return trimesh.Trimesh(vertices=np.zeros((0, 3)), faces=np.zeros((0, 3), dtype=np.int64), process=False)
    verts = tri.reshape(-1, 3)
    faces = np.arange(len(verts)).reshape(-1, 3)
    return cleanup(trimesh.Trimesh(vertices=verts, faces=faces, process=False))


def cleanup(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
    mesh.merge_vertices()
    mesh.update_faces(mesh.nondegenerate_faces())
    mesh.update_faces(mesh.unique_faces())
    mesh.remove_unreferenced_vertices()
    try:
        mesh.fix_normals()
    except Exception:  # networkx-free fallback paths can fail on odd topology
        pass
    return mesh


def transform_vertices(mesh: trimesh.Trimesh, fn) -> trimesh.Trimesh:
    if len(mesh.vertices):
        mesh.vertices = fn(np.asarray(mesh.vertices))
    return mesh


def export_mesh(mesh: trimesh.Trimesh, base: str | Path, formats: Iterable[str] = ("obj", "glb")) -> dict:
    base = Path(base)
    base.parent.mkdir(parents=True, exist_ok=True)
    out = {}
    for fmt in formats:
        p = base.with_suffix(f".{fmt}")
        mesh.export(p)
        out[fmt] = {"path": str(p), "bytes": p.stat().st_size}
    return out


def mesh_stats(mesh: trimesh.Trimesh) -> dict:
    nv, nf = len(mesh.vertices), len(mesh.faces)
    stats = {"vertices": int(nv), "triangles": int(nf)}
    if nf:
        stats.update(
            watertight=bool(mesh.is_watertight),
            euler_number=int(mesh.euler_number),
            area=float(mesh.area),
            bbox_extent=[float(x) for x in mesh.extents],
            # bytes of an indexed float32/uint32 GPU buffer (positions + indices): what the headset renders
            gpu_buffer_bytes=int(nv * 12 + nf * 12),
        )
    return stats


def quality_metrics(mesh: trimesh.Trimesh, reference: np.ndarray, n_samples: int = 20000,
                    tau: float = 0.01, seed: int = 0) -> Optional[dict]:
    """Chamfer-L2 and F-score@tau vs a dense reference cloud, in the reference's bbox-diagonal units."""
    from scipy.spatial import cKDTree

    if len(mesh.faces) == 0 or reference is None or len(reference) == 0:
        return None
    rng_state = np.random.get_state()
    np.random.seed(seed)
    pts, _ = trimesh.sample.sample_surface(mesh, n_samples)
    np.random.set_state(rng_state)
    diag = float(np.linalg.norm(reference.max(0) - reference.min(0)))
    a, b = np.asarray(pts) / diag, np.asarray(reference) / diag
    d_ab, _ = cKDTree(b).query(a)
    d_ba, _ = cKDTree(a).query(b)
    precision, recall = float(np.mean(d_ab < tau)), float(np.mean(d_ba < tau))
    f = 2 * precision * recall / (precision + recall) if precision + recall > 0 else 0.0
    return {"chamfer_l2": float(np.mean(d_ab ** 2) + np.mean(d_ba ** 2)),
            "chamfer_l1": float(np.mean(d_ab) + np.mean(d_ba)),
            f"fscore@{tau}": f, "precision": precision, "recall": recall}
