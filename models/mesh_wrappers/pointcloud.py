"""Point cloud I/O, normal estimation, resampling and per-model normalisation."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import numpy as np


# --------------------------------------------------------------------------- I/O
def load_pointcloud(path: str | Path) -> tuple[np.ndarray, Optional[np.ndarray]]:
    """Return (xyz[N,3] float32, normals[N,3] or None). Supports .npy .xyz .xyzn .txt .pts .ply."""
    path = Path(path)
    ext = path.suffix.lower()
    if ext == ".npy":
        arr = np.load(path).astype(np.float32)
    elif ext in (".xyz", ".xyzn", ".txt", ".pts"):
        arr = np.loadtxt(path, dtype=np.float32, ndmin=2)
    elif ext == ".ply":
        import open3d as o3d

        pcd = o3d.io.read_point_cloud(str(path))
        xyz = np.asarray(pcd.points, dtype=np.float32)
        nrm = np.asarray(pcd.normals, dtype=np.float32) if pcd.has_normals() else None
        if len(xyz) == 0:
            raise ValueError(f"{path}: no points read")
        return xyz, nrm
    else:
        raise ValueError(f"unsupported point cloud format: {ext}")
    if arr.shape[1] < 3:
        raise ValueError(f"{path}: expected at least 3 columns, got {arr.shape}")
    xyz = arr[:, :3]
    nrm = arr[:, 3:6] if arr.shape[1] >= 6 else None
    return np.ascontiguousarray(xyz), (np.ascontiguousarray(nrm) if nrm is not None else None)


def save_pointcloud(path: str | Path, xyz: np.ndarray, normals: Optional[np.ndarray] = None) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    ext = path.suffix.lower()
    xyz = np.asarray(xyz, dtype=np.float32)
    if ext == ".npy":
        np.save(path, np.concatenate([xyz, normals], 1) if normals is not None else xyz)
    elif ext == ".xyz":  # coordinates only: the "sparse storage" format
        np.savetxt(path, xyz, fmt="%.6f")
    elif ext == ".xyzn":
        np.savetxt(path, np.concatenate([xyz, normals], 1), fmt="%.6f")
    elif ext == ".ply":
        import open3d as o3d

        pcd = o3d.geometry.PointCloud(o3d.utility.Vector3dVector(xyz.astype(np.float64)))
        if normals is not None:
            pcd.normals = o3d.utility.Vector3dVector(np.asarray(normals, dtype=np.float64))
        o3d.io.write_point_cloud(str(path), pcd, write_ascii=False)
    else:
        raise ValueError(f"unsupported point cloud format: {ext}")
    return path


# --------------------------------------------------------------------------- normals
def estimate_normals(xyz: np.ndarray, k: int = 30) -> np.ndarray:
    """KNN PCA normals (Open3D), consistently oriented, falling back to outward orientation."""
    import open3d as o3d

    n = len(xyz)
    k = int(max(3, min(k, n - 1)))
    pcd = o3d.geometry.PointCloud(o3d.utility.Vector3dVector(xyz.astype(np.float64)))
    pcd.estimate_normals(o3d.geometry.KDTreeSearchParamKNN(knn=k))
    try:
        pcd.orient_normals_consistent_tangent_plane(k)
    except RuntimeError:
        pass
    nrm = np.asarray(pcd.normals, dtype=np.float32)
    # make the majority point away from the centroid (closed-object prior)
    outward = xyz - xyz.mean(0, keepdims=True)
    if np.mean(np.sum(nrm * outward, 1) > 0) < 0.5:
        nrm = -nrm
    return unit(nrm)


def unit(v: np.ndarray) -> np.ndarray:
    norm = np.linalg.norm(v, axis=1, keepdims=True)
    out = v / np.maximum(norm, 1e-12)
    bad = norm[:, 0] < 1e-12
    out[bad] = np.array([0.0, 1.0, 0.0], dtype=out.dtype)
    return out.astype(np.float32)


# --------------------------------------------------------------------------- resampling
def farthest_point_indices(xyz: np.ndarray, n: int, rng: np.random.Generator) -> np.ndarray:
    """Plain numpy FPS (O(N*n)); fine for N <= ~1e5."""
    N = len(xyz)
    idx = np.empty(n, dtype=np.int64)
    idx[0] = rng.integers(N)
    d = np.full(N, np.inf)
    for i in range(1, n):
        d = np.minimum(d, np.sum((xyz - xyz[idx[i - 1]]) ** 2, 1))
        idx[i] = int(np.argmax(d))
    return idx


def resample(xyz: np.ndarray, normals: Optional[np.ndarray], n_target: int, method: str = "repeat",
             rng: Optional[np.random.Generator] = None, jitter_sigma: float = 0.005,
             downsample: str = "random") -> tuple[np.ndarray, Optional[np.ndarray], dict]:
    """Bring a cloud to exactly n_target points.

    Upsampling (N < n_target): ``repeat`` duplicates points (no fabricated geometry),
    ``jitter`` adds isotropic Gaussian noise (sigma relative to bbox diagonal) to duplicates.
    Downsampling: ``random`` (what MeshAnything/FastMesh do) or ``fps``.
    """
    rng = rng or np.random.default_rng(0)
    N = len(xyz)
    info = {"input_points": N, "model_points": n_target, "resample": "none"}
    if N == n_target:
        return xyz, normals, info
    if N > n_target:
        idx = farthest_point_indices(xyz, n_target, rng) if downsample == "fps" else rng.choice(N, n_target, replace=False)
        info["resample"] = f"down-{downsample}"
        return xyz[idx], (normals[idx] if normals is not None else None), info
    extra = rng.choice(N, n_target - N, replace=True)
    idx = np.concatenate([np.arange(N), extra])
    out_xyz = xyz[idx].copy()
    if method == "jitter":
        diag = float(np.linalg.norm(xyz.max(0) - xyz.min(0)))
        out_xyz[N:] += rng.normal(0.0, jitter_sigma * diag, size=(len(extra), 3)).astype(np.float32)
    info["resample"] = f"up-{method}"
    info["duplication_factor"] = round(n_target / N, 2)
    return out_xyz, (normals[idx] if normals is not None else None), info


# --------------------------------------------------------------------------- normalisation
@dataclass
class Normalizer:
    """x_model = (x - center) * scale ; inverse maps model-space meshes back to input space."""

    center: np.ndarray = field(default_factory=lambda: np.zeros(3, np.float32))
    scale: float = 1.0
    name: str = "identity"

    def apply(self, xyz: np.ndarray) -> np.ndarray:
        return ((xyz - self.center) * self.scale).astype(np.float32)

    def invert(self, xyz: np.ndarray) -> np.ndarray:
        return (np.asarray(xyz, dtype=np.float64) / self.scale + self.center).astype(np.float32)

    def to_dict(self) -> dict:
        return {"name": self.name, "center": [float(c) for c in self.center], "scale": float(self.scale)}


def _bbox_center(xyz: np.ndarray) -> np.ndarray:
    return ((xyz.min(0) + xyz.max(0)) / 2).astype(np.float32)


def normalizer_meshanything(xyz: np.ndarray) -> Normalizer:
    """MeshAnythingV2 main.py: centre bbox, scale max |coord| to 0.9995."""
    c = _bbox_center(xyz)
    return Normalizer(c, float(0.9995 / np.abs(xyz - c).max()), "meshanything_v2")


def normalizer_fastmesh(xyz: np.ndarray) -> Normalizer:
    """FastMesh apply_normalize: centre bbox, longest side -> 2*0.95."""
    c = _bbox_center(xyz)
    return Normalizer(c, float(2 * 0.95 / (xyz.max(0) - xyz.min(0)).max()), "fastmesh")


def normalizer_treemeshgpt(xyz: np.ndarray) -> Normalizer:
    """TreeMeshGPT: centre bbox, divide by bbox diagonal (then clip to +-0.5)."""
    c = _bbox_center(xyz)
    return Normalizer(c, float(1.0 / (np.linalg.norm(xyz.max(0) - xyz.min(0)) + 1e-6)), "treemeshgpt")


def storage_bytes(n_points: int, with_normals: bool = False, dtype_bytes: int = 4) -> int:
    """Raw in-memory footprint of the sparse cloud (what the headset would keep resident)."""
    return n_points * (6 if with_normals else 3) * dtype_bytes
