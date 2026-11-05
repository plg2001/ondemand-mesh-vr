"""Generate test point clouds from dense to very sparse.

For every source mesh it samples surface points (with ground-truth normals) at the requested counts
and writes, under data/inputs/<shape>/:
    <shape>_n<N>.ply   xyz + normals (binary PLY)
    <shape>_n<N>.xyz   xyz only (the "sparse storage" representation the headset would keep)
    <shape>_n<N>.npy   (N, 6) float32 = MeshAnything pc_normal format
    <shape>_ref.npy    dense reference cloud (quality metrics)
plus data/inputs/manifest.json.

Sources: trimesh primitives (offline), Open3D sample meshes (Stanford Bunny, Armadillo, Knot: downloaded
on first use), and the demo meshes shipped with the upstream repos (if cloned in third_party/).

    python data/inputs/generate_pointclouds.py                       # default set
    python data/inputs/generate_pointclouds.py --shapes bunny box --counts 100 500 2048
    python data/inputs/generate_pointclouds.py --list
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
import trimesh

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / "models"))
from mesh_wrappers.pointcloud import save_pointcloud, storage_bytes  # noqa: E402

TP = ROOT / "third_party"
SOURCES = {
    # primitives: analytic, offline, known topology
    "box": ("primitive", lambda: trimesh.creation.box(extents=(1.0, 0.6, 0.4))),
    "sphere": ("primitive", lambda: trimesh.creation.icosphere(subdivisions=4, radius=0.5)),
    "cylinder": ("primitive", lambda: trimesh.creation.cylinder(radius=0.3, height=1.0, sections=64)),
    "capsule": ("primitive", lambda: trimesh.creation.capsule(height=0.6, radius=0.25)),
    # Open3D sample data (Stanford scans / classic test meshes)
    "bunny": ("open3d", "BunnyMesh"),
    "armadillo": ("open3d", "ArmadilloMesh"),
    "knot": ("open3d", "KnotMesh"),
    # upstream demo assets (artist-like meshes, in-distribution for the models)
    "tmg_bunny": ("file", TP / "TreeMeshGPT" / "demo" / "luma_bunny.glb"),
    "tmg_cat": ("file", TP / "TreeMeshGPT" / "demo" / "luma_cat.glb"),
    "tmg_pig": ("file", TP / "TreeMeshGPT" / "demo" / "objaverse_pig.obj"),
    "mav2_wand": ("file", TP / "MeshAnythingV2" / "examples" / "wand.obj"),
    "fastmesh_001": ("file", TP / "FastMesh" / "assets" / "001.obj"),
}
DEFAULT_SHAPES = ["bunny", "box", "sphere", "tmg_cat", "mav2_wand", "fastmesh_001"]
DEFAULT_COUNTS = [100, 500, 2048, 8192]


def load_source(name: str) -> trimesh.Trimesh:
    kind, src = SOURCES[name]
    if kind == "primitive":
        return src()
    if kind == "open3d":
        import open3d as o3d

        data = getattr(o3d.data, src)()
        m = o3d.io.read_triangle_mesh(data.path)
        return trimesh.Trimesh(np.asarray(m.vertices), np.asarray(m.triangles), process=True)
    if not Path(src).exists():
        raise FileNotFoundError(f"{src} (run models/setup_models.py --skip-weights to clone the repos)")
    return trimesh.load(src, force="mesh")


def canonicalise(mesh: trimesh.Trimesh) -> tuple[trimesh.Trimesh, dict]:
    """Centre the bbox at the origin and scale the longest side to 1 (+Y up is kept)."""
    lo, hi = mesh.bounds
    center = (lo + hi) / 2
    scale = 1.0 / float((hi - lo).max())
    mesh = mesh.copy()
    mesh.apply_translation(-center)
    mesh.apply_scale(scale)
    return mesh, {"center": center.tolist(), "scale": scale}


def sample(mesh: trimesh.Trimesh, n: int, seed: int) -> tuple[np.ndarray, np.ndarray]:
    pts, fidx = trimesh.sample.sample_surface(mesh, n, seed=seed)
    nrm = mesh.face_normals[fidx]
    return pts.astype(np.float32), nrm.astype(np.float32)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--shapes", nargs="+", default=DEFAULT_SHAPES)
    ap.add_argument("--counts", nargs="+", type=int, default=DEFAULT_COUNTS)
    ap.add_argument("--reference-points", type=int, default=50000)
    ap.add_argument("--formats", nargs="+", default=["ply", "xyz", "npy"])
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--out", type=Path, default=HERE)
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()
    if args.list:
        for k, (kind, src) in SOURCES.items():
            print(f"{k:14s} {kind:9s} {src if kind != 'primitive' else ''}")
        return

    manifest_path = args.out / "manifest.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {"shapes": {}}
    for name in args.shapes:
        try:
            mesh = load_source(name)
        except Exception as e:  # keep going with the other shapes
            print(f"[skip] {name}: {e}")
            continue
        mesh, tf = canonicalise(mesh)
        d = args.out / name
        d.mkdir(parents=True, exist_ok=True)
        ref, ref_n = sample(mesh, args.reference_points, args.seed + 999)
        np.save(d / f"{name}_ref.npy", np.concatenate([ref, ref_n], 1))
        entry = {"source": SOURCES[name][0], "source_faces": int(len(mesh.faces)),
                 "source_vertices": int(len(mesh.vertices)), "canonical_transform": tf,
                 "reference": f"{name}/{name}_ref.npy", "clouds": {}}
        for n in args.counts:
            pts, nrm = sample(mesh, n, args.seed + n)
            files = {}
            for fmt in args.formats:
                p = save_pointcloud(d / f"{name}_n{n}.{fmt}", pts, nrm if fmt != "xyz" else None)
                files[fmt] = {"path": str(p.relative_to(args.out)).replace("\\", "/"), "bytes": p.stat().st_size}
            entry["clouds"][str(n)] = {"files": files, "raw_bytes_xyz_f32": storage_bytes(n),
                                       "raw_bytes_xyz_normals_f32": storage_bytes(n, True)}
        manifest["shapes"][name] = entry
        print(f"[ok]   {name:14s} faces={len(mesh.faces):7d}  counts={args.counts}")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"manifest: {manifest_path}")


if __name__ == "__main__":
    main()
