"""Classical surface reconstruction from a point cloud: the baselines the generative models must beat.

All on the CPU with Open3D 0.18 / trimesh, from milliseconds to about a second for a few thousand points:

- hull           convex hull (the edge server's mock backend). No normals. Closed and convex: exact on convex
                 objects (box, sphere), it fills every concavity of the others.
- alpha          alpha shape (Edelsbrunner & Muecke 1994): the union of balls of radius alpha around the points.
                 No normals. alpha = ALPHA_FACTOR x the cloud's spacing.
- ball_pivoting  Ball Pivoting (Bernardini et al. 1999): a ball rolled over the points lays a triangle where it
                 touches three of them. Needs normals. Radii = BPA_FACTORS x spacing.
- poisson        screened Poisson (Kazhdan & Hoppe 2013): the surface is the level set of a smooth indicator
                 function fitted to the oriented normals. Needs oriented normals. Closed and smooth; the vertices with
                 the lowest density (Poisson extrapolates where there are no points) are trimmed.

Every parameter comes from the cloud itself (its spacing = median distance to the nearest neighbour), never from the
reference mesh, so the comparison with the generative models stays fair. With target_faces any result is
decimated (quadric error, Garland & Heckbert 1997) to that many triangles, to compare at the same weight as the
generative models' light "artist" meshes (hundreds to a few thousand triangles).
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Optional

import numpy as np
import trimesh

METHODS = ("hull", "alpha", "ball_pivoting", "poisson")
NEEDS_NORMALS = frozenset({"ball_pivoting", "poisson"})
LABELS = {"hull": "convex hull", "alpha": "alpha shape", "ball_pivoting": "Ball Pivoting", "poisson": "Poisson"}

ALPHA_FACTOR = 4.0              # alpha radius in units of the spacing (chosen on the bunny: 3 -> F 0.80, 4 -> 0.88, 6 -> 0.86)
BPA_FACTORS = (1.0, 2.0, 4.0)   # ball radii in units of the spacing (small balls first: detail, big ones: gaps)
POISSON_DEPTH = 7               # octree depth: 2^7 cells across the object (bunny: depth 6-8 within 0.01 of F-score)
POISSON_TRIM_QUANTILE = 0.02    # vertices below this density quantile are removed (extrapolated surface)


@dataclass
class ClassicalResult:
    mesh: trimesh.Trimesh
    seconds: float                       # reconstruction (+ decimation), normals excluded
    params: dict = field(default_factory=dict)


def point_spacing(xyz: np.ndarray) -> float:
    """Median distance from each point to its nearest neighbour: the cloud's sampling step."""
    from scipy.spatial import cKDTree

    d, _ = cKDTree(xyz).query(xyz, k=2)
    return float(np.median(d[:, 1]))


def _o3d_cloud(xyz: np.ndarray, normals: Optional[np.ndarray]):
    import open3d as o3d

    pcd = o3d.geometry.PointCloud(o3d.utility.Vector3dVector(np.asarray(xyz, dtype=np.float64)))
    if normals is not None:
        pcd.normals = o3d.utility.Vector3dVector(np.asarray(normals, dtype=np.float64))
    return pcd


def _to_trimesh(o3d_mesh) -> trimesh.Trimesh:
    mesh = trimesh.Trimesh(np.asarray(o3d_mesh.vertices), np.asarray(o3d_mesh.triangles), process=True)
    mesh.remove_unreferenced_vertices()
    return mesh


def _to_o3d(mesh: trimesh.Trimesh):
    import open3d as o3d

    return o3d.geometry.TriangleMesh(o3d.utility.Vector3dVector(np.asarray(mesh.vertices, dtype=np.float64)),
                                     o3d.utility.Vector3iVector(np.asarray(mesh.faces, dtype=np.int32)))


def _alpha_shape(xyz: np.ndarray, alpha: float, spacing: float, params: dict) -> trimesh.Trimesh:
    """Open3D's alpha shape fails ("invalid unordered_map key") on degenerate clouds, e.g. points exactly on a
    mathematical sphere (co-spherical, so the Delaunay tetrahedra are not unique): then it is retried with the points
    moved by 1e-4 of their spacing, which changes nothing measurable on real clouds; if that fails too, an empty mesh
    with params["error"] is returned instead of stopping a whole benchmark."""
    import open3d as o3d

    for attempt in range(2):
        points = xyz if attempt == 0 else xyz + np.random.default_rng(0).normal(scale=1e-4 * spacing, size=xyz.shape)
        try:
            mesh = _to_trimesh(o3d.geometry.TriangleMesh.create_from_point_cloud_alpha_shape(
                _o3d_cloud(points, None), alpha))
            if attempt:
                params["jitter"] = 1e-4 * spacing
                params["retried_after"] = params.pop("error")
            return mesh
        except (IndexError, RuntimeError) as e:
            params["error"] = f"{type(e).__name__}: {e}"
    return trimesh.Trimesh(np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64))


def reconstruct(xyz: np.ndarray, normals: Optional[np.ndarray], method: str,
                target_faces: Optional[int] = None) -> ClassicalResult:
    """A mesh of the cloud with one classical method, in the cloud's frame. normals: (N, 3), needed by
    ball_pivoting and poisson (pass the true ones, or estimate them as the edge server does)."""
    import open3d as o3d

    if method not in METHODS:
        raise ValueError(f"unknown method {method!r}; choose one of {', '.join(METHODS)}")
    if method in NEEDS_NORMALS and normals is None:
        raise ValueError(f"{method} needs normals")
    xyz = np.asarray(xyz, dtype=np.float64)
    t0 = time.perf_counter()
    spacing = point_spacing(xyz)
    params: dict = {"spacing": round(spacing, 6)}
    if method == "hull":
        mesh = trimesh.PointCloud(xyz).convex_hull
    elif method == "alpha":
        alpha = ALPHA_FACTOR * spacing
        params["alpha"] = round(alpha, 6)
        mesh = _alpha_shape(xyz, alpha, spacing, params)
    elif method == "ball_pivoting":
        radii = [f * spacing for f in BPA_FACTORS]
        params["radii"] = [round(r, 6) for r in radii]
        mesh = _to_trimesh(o3d.geometry.TriangleMesh.create_from_point_cloud_ball_pivoting(
            _o3d_cloud(xyz, normals), o3d.utility.DoubleVector(radii)))
    else:  # poisson
        params.update(depth=POISSON_DEPTH, trim_quantile=POISSON_TRIM_QUANTILE)
        o3d_mesh, densities = o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
            _o3d_cloud(xyz, normals), depth=POISSON_DEPTH)
        densities = np.asarray(densities)
        if len(densities):
            o3d_mesh.remove_vertices_by_mask(densities < np.quantile(densities, POISSON_TRIM_QUANTILE))
        mesh = _to_trimesh(o3d_mesh)
    if target_faces and len(mesh.faces) > target_faces:
        params["faces_before_decimation"] = int(len(mesh.faces))
        mesh = _to_trimesh(_to_o3d(mesh).simplify_quadric_decimation(target_number_of_triangles=int(target_faces)))
    return ClassicalResult(mesh, time.perf_counter() - t0, params)
