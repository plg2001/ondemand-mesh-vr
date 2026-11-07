"""Build a test scene for the on-demand pipeline: several objects, each stored as a sparse point cloud.

The scene is authored as ordinary meshes placed in a room: a YAML file lists, for every object, its shape,
position, rotation and size. This script runs once, offline. It samples each object's surface into a sparse
cloud and writes a scene description with every object's pose. The headset keeps only these clouds; the edge
rebuilds an object's mesh when it is needed.

    python data/scenes/build_scene.py                          # data/scenes/demo_room.yaml
    python data/scenes/build_scene.py data/scenes/my_room.yaml --points 500
    python data/scenes/build_scene.py --no-unity               # do not copy into the Unity project

Output in data/scenes/<name>/ (git-ignored, rebuildable):
    scene.json      objects (id, shape, points file, pose, local bounds, source mesh size) + scene totals
    <id>.xyz        sparse cloud of the object in its LOCAL frame: what the headset stores and sends
    <id>_ref.npy    dense reference cloud (xyz + normals), local frame, for quality metrics
scene.json and the .xyz files are also copied to unity/OnDemandMeshVR/Assets/StreamingAssets/Scenes/<name>/.

Frames: glTF convention (right-handed, +Y up, metres). An object's points live in its local frame (the shape
canonicalised: bounding box centred on the origin, longest side 1); position / rotation / scale place it in the
scene exactly like a glTF node. Unity converts like glTFast does for any .glb: position (-x, y, z), rotation
quaternion (x, -y, -z, w), scale unchanged.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path
from typing import Optional

import numpy as np
import yaml
from scipy.spatial.transform import Rotation

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / "data" / "inputs"))
sys.path.insert(0, str(ROOT / "models"))
from generate_pointclouds import SOURCES, canonicalise, load_source, sample  # noqa: E402
from mesh_wrappers.pointcloud import save_pointcloud, storage_bytes  # noqa: E402

DEFAULT_CONFIG = HERE / "demo_room.yaml"
UNITY_SCENES = ROOT / "unity" / "OnDemandMeshVR" / "Assets" / "StreamingAssets" / "Scenes"
FRAME_NOTE = ("gltf: right-handed, +Y up, metres; points in each object's local frame; "
              "Unity = position (-x, y, z), rotation (x, -y, -z, w)")


def object_rotation(entry: dict) -> Rotation:
    if "rotation_deg" in entry:  # extrinsic X, then Y, then Z, in degrees
        return Rotation.from_euler("xyz", entry["rotation_deg"], degrees=True)
    return Rotation.from_euler("y", float(entry.get("rotation_y_deg", 0.0)), degrees=True)


def object_pose(entry: dict, bounds_min: np.ndarray, bounds_max: np.ndarray) -> tuple[list, list, list]:
    """(position, quaternion xyzw, scale) of one object. With on_floor (default) position.y is the floor
    height under the object: the pose is lifted so that the lowest point of the object touches it."""
    scale = float(entry.get("size", 1.0))  # canonical shapes have longest side 1 -> size in metres
    rot = object_rotation(entry)
    pos = np.asarray(entry.get("position", [0.0, 0.0, 0.0]), dtype=np.float64)
    if entry.get("on_floor", True):
        corners = np.array([[x, y, z] for x in (bounds_min[0], bounds_max[0])
                            for y in (bounds_min[1], bounds_max[1]) for z in (bounds_min[2], bounds_max[2])])
        pos = pos + np.array([0.0, -rot.apply(corners * scale)[:, 1].min(), 0.0])
    return [round(float(v), 6) for v in pos], [round(float(v), 6) for v in rot.as_quat()], [scale] * 3


def build_scene(config: dict, out_dir: Path, unity_dir: Optional[Path] = None,
                points_override: Optional[int] = None, verbose: bool = True) -> dict:
    name = config.get("name", "scene")
    default_points = int(points_override or config.get("points_per_object", 2048))
    ref_points = int(config.get("reference_points", 20000))
    seed = int(config.get("seed", 0))
    objects = config.get("objects") or []
    if not objects:
        raise ValueError("the scene has no objects")
    ids = [o.get("id") for o in objects]
    if None in ids or len(set(ids)) != len(ids):
        raise ValueError("every object needs a unique 'id'")

    out_dir.mkdir(parents=True, exist_ok=True)
    cache: dict[str, tuple] = {}  # the same shape can be placed several times
    entries, totals = [], {"objects": 0, "points": 0, "bytes_xyz_f32": 0, "source_vertices": 0,
                           "source_triangles": 0, "source_mesh_bytes": 0}
    for i, obj in enumerate(objects):
        shape = obj.get("shape")
        if shape not in SOURCES:
            raise ValueError(f"{obj['id']}: unknown shape {shape!r}; available: {', '.join(SOURCES)}")
        if shape not in cache:
            source = load_source(shape)
            mesh, _ = canonicalise(source)
            cache[shape] = (mesh, len(source.vertices), len(source.faces))
        mesh, n_vertices, n_faces = cache[shape]
        n = int(points_override or obj.get("points", default_points))
        pts, _ = sample(mesh, n, seed + 7919 * i)
        ref, ref_n = sample(mesh, ref_points, seed + 7919 * i + 999)
        save_pointcloud(out_dir / f"{obj['id']}.xyz", pts)  # coordinates only: the headset's sparse storage
        np.save(out_dir / f"{obj['id']}_ref.npy", np.concatenate([ref, ref_n], 1))

        bmin, bmax = mesh.vertices.min(0), mesh.vertices.max(0)
        position, rotation, scale = object_pose(obj, bmin, bmax)
        mesh_bytes = n_vertices * 12 + n_faces * 12  # float32 positions + uint32 indices, as in mesh_stats()
        entries.append({
            "id": obj["id"], "shape": shape, "points": f"{obj['id']}.xyz", "reference": f"{obj['id']}_ref.npy",
            "point_count": n, "bytes_xyz_f32": storage_bytes(n),
            "position": position, "rotation": rotation, "scale": scale,
            "local_bounds_min": [round(float(v), 6) for v in bmin],
            "local_bounds_max": [round(float(v), 6) for v in bmax],
            "source_vertices": n_vertices, "source_triangles": n_faces, "source_mesh_bytes": mesh_bytes,
        })
        totals["objects"] += 1
        totals["points"] += n
        totals["bytes_xyz_f32"] += storage_bytes(n)
        totals["source_vertices"] += n_vertices
        totals["source_triangles"] += n_faces
        totals["source_mesh_bytes"] += mesh_bytes
        if verbose:
            print(f"[ok] {obj['id']:14s} {shape:13s} {n:6d} points  pos={position}  size={scale[0]}")

    scene = {"version": 1, "name": name, "frame": FRAME_NOTE, "points_per_object": default_points,
             "objects": entries, "totals": totals}
    (out_dir / "scene.json").write_text(json.dumps(scene, indent=2) + "\n", encoding="utf-8")
    if unity_dir is not None:
        copy_to_unity(scene, out_dir, unity_dir)
    if verbose:
        kb = totals["bytes_xyz_f32"] / 1024
        print(f"scene '{name}': {totals['objects']} objects, {totals['points']} points = {kb:.1f} KB as float32 "
              f"(original meshes: {totals['source_triangles']} triangles, "
              f"{totals['source_mesh_bytes'] / 1024:.0f} KB)")
        print(f"written to {out_dir}" + (f" and {unity_dir}" if unity_dir else ""))
    return scene


def copy_to_unity(scene: dict, out_dir: Path, unity_dir: Path) -> None:
    """Copy what the headset needs (scene.json + sparse clouds); drop clouds of objects no longer in the scene."""
    unity_dir.mkdir(parents=True, exist_ok=True)
    keep = {e["points"] for e in scene["objects"]}
    for old in unity_dir.glob("*.xyz"):
        if old.name not in keep:
            old.unlink()
            meta = old.with_name(old.name + ".meta")
            if meta.exists():
                meta.unlink()
    for e in scene["objects"]:
        shutil.copy2(out_dir / e["points"], unity_dir / e["points"])
    shutil.copy2(out_dir / "scene.json", unity_dir / "scene.json")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("config", nargs="?", type=Path, default=DEFAULT_CONFIG, help="scene YAML")
    ap.add_argument("--points", type=int, default=None, help="points per object (overrides the YAML)")
    ap.add_argument("--out", type=Path, default=None, help="output folder (default data/scenes/<name>)")
    ap.add_argument("--no-unity", action="store_true", help="do not copy the scene into the Unity project")
    args = ap.parse_args(argv)
    config = yaml.safe_load(args.config.read_text(encoding="utf-8"))
    name = config.get("name", args.config.stem)
    out_dir = args.out or HERE / name
    unity_dir = None if args.no_unity else UNITY_SCENES / name
    build_scene(config, out_dir, unity_dir, args.points)
    return 0


if __name__ == "__main__":
    sys.exit(main())
