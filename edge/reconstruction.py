"""Reconstruction backends of the edge server, plus the binary point cloud wire format.

A backend turns a sparse point cloud into a triangle mesh expressed in the *same frame as the input points*.

* ``MockBackend``  convex hull of the points (+ optional artificial delay). No weights, no GPU, a few ms:
  used to develop and test the headset <-> edge protocol on a weak PC.
* ``ModelBackend`` one of the three generative models through ``models/mesh_wrappers``. The network is
  loaded once at start-up and kept in memory; each request only pays preprocessing + generation.
* ``ClassicalBackend`` a classical surface reconstruction (``mesh_wrappers.classical``: convex hull, alpha shape,
  Ball Pivoting, Poisson), milliseconds on the CPU, no weights: the baseline the generative models must beat.

Torch and the upstream code are imported lazily, so the mock backend starts in about a second.
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Optional

import numpy as np
import trimesh

from mesh_wrappers.mesh_io import cleanup, transform_vertices, triangles_to_mesh
from mesh_wrappers.pointcloud import estimate_normals, resample, unit

# ----------------------------------------------------------------------------- wire format
# A point cloud travels as raw little-endian float32: N x 3 (xyz) or N x 6 (xyz + normals), no header.
# 2048 points = 24 KB, i.e. exactly the "sparse storage" footprint the headset would keep in memory.
POINT_FORMATS = {"xyz": 3, "xyzn": 6}
MIN_POINTS = 16
MAX_POINTS = 200_000


def decode_points(body: bytes, fmt: str = "xyz") -> tuple[np.ndarray, Optional[np.ndarray]]:
    """Raw request body -> (xyz[N,3], normals[N,3] or None). Raises ValueError with a readable message."""
    cols = POINT_FORMATS.get(fmt)
    if cols is None:
        raise ValueError(f"unknown format {fmt!r}: use one of {', '.join(POINT_FORMATS)}")
    stride = cols * 4
    if not body:
        raise ValueError("empty body: send the points as little-endian float32 values")
    if len(body) % stride:
        raise ValueError(f"body is {len(body)} bytes, not a multiple of {stride} "
                         f"(format {fmt} = {cols} float32 per point)")
    n = len(body) // stride
    if not MIN_POINTS <= n <= MAX_POINTS:
        raise ValueError(f"{n} points received, expected between {MIN_POINTS} and {MAX_POINTS}")
    arr = np.frombuffer(body, dtype="<f4").reshape(n, cols).astype(np.float32)
    if not np.isfinite(arr).all():
        raise ValueError("the point cloud contains NaN or infinite values")
    xyz = np.ascontiguousarray(arr[:, :3])
    normals = np.ascontiguousarray(arr[:, 3:6]) if cols == 6 else None
    return xyz, normals


def encode_points(xyz: np.ndarray, normals: Optional[np.ndarray] = None) -> bytes:
    arr = xyz if normals is None else np.concatenate([xyz, normals], axis=1)
    return np.ascontiguousarray(arr, dtype="<f4").tobytes()


def export_glb(mesh: trimesh.Trimesh) -> bytes:
    return mesh.export(file_type="glb")


# ----------------------------------------------------------------------------- results / errors
class ReconstructionError(RuntimeError):
    """The backend ran but produced no usable mesh. ``status_code`` is the HTTP status to answer with."""

    def __init__(self, message: str, status_code: int = 422):
        super().__init__(message)
        self.status_code = status_code


@dataclass
class Reconstruction:
    mesh: trimesh.Trimesh
    timings_ms: dict[str, float]          # preprocess / inference / postprocess
    info: dict = field(default_factory=dict)


# ----------------------------------------------------------------------------- mock backend
def hull_mesh(xyz: np.ndarray) -> trimesh.Trimesh:
    """Convex hull of the cloud; axis-aligned box if the points are degenerate (coplanar / collinear)."""
    try:
        hull = trimesh.PointCloud(xyz).convex_hull
        if len(hull.faces):
            return hull
    except Exception:  # scipy QhullError on flat inputs
        pass
    lo, hi = xyz.min(0), xyz.max(0)
    extents = np.maximum(hi - lo, 1e-3 * max(float(np.max(hi - lo)), 1e-3))
    return trimesh.creation.box(extents=extents, transform=trimesh.transformations.translation_matrix((lo + hi) / 2))


class MockBackend:
    """Fake reconstruction: geometrically tied to the input (same position/size) but computed in ms."""

    def __init__(self, delay_s: float = 0.0):
        self.delay_s = float(delay_s)

    def describe(self) -> dict:
        return {"backend": "mock", "model": "convex-hull", "simulated_inference_s": self.delay_s}

    def reconstruct(self, xyz: np.ndarray, normals: Optional[np.ndarray] = None, seed: int = 0) -> Reconstruction:
        t0 = time.perf_counter()
        mesh = hull_mesh(xyz)
        if self.delay_s > 0:  # simulates a model busy on the GPU: later requests queue behind it
            time.sleep(self.delay_s)
        inference_ms = (time.perf_counter() - t0) * 1e3
        return Reconstruction(mesh, {"preprocess": 0.0, "inference": inference_ms, "postprocess": 0.0},
                              {"method": "convex_hull"})


# ----------------------------------------------------------------------------- real models
class ClassicalBackend:
    """Classical surface reconstruction of the points (mesh_wrappers.classical). The headset sends points only, so the
    normals that Ball Pivoting and Poisson need are estimated here, as for the generative models (preprocess)."""

    def __init__(self, method: str = "ball_pivoting", target_faces: Optional[int] = None):
        from mesh_wrappers import classical

        if method not in classical.METHODS:
            raise RuntimeError(f"unknown method {method!r}; choose one of {', '.join(classical.METHODS)}")
        self.method, self.target_faces = method, target_faces or None
        self.label = classical.LABELS[method]
        # One small reconstruction now, so that loading Open3D (~2 s) is not paid by the first request. A bumpy ball,
        # not an exact sphere: points all on one sphere are a degenerate case for the alpha shape (no triangles). It
        # is only a warm-up, so whatever happens the server starts.
        rng = np.random.default_rng(0)
        ball = rng.normal(size=(512, 3))
        ball *= ((1.0 + 0.05 * rng.standard_normal(len(ball))) / np.linalg.norm(ball, axis=1))[:, None]
        try:
            self.reconstruct(ball.astype(np.float32))
        except (ReconstructionError, ValueError, IndexError, RuntimeError):
            pass

    def describe(self) -> dict:
        return {"backend": "classical", "model": self.method, "model_display": f"{self.label} (classical)",
                "target_faces": self.target_faces or 0}

    def reconstruct(self, xyz: np.ndarray, normals: Optional[np.ndarray] = None, seed: int = 0) -> Reconstruction:
        from mesh_wrappers import classical

        t0 = time.perf_counter()
        normals_source = "unused"
        if self.method in classical.NEEDS_NORMALS:
            if normals is None:
                normals, normals_source = estimate_normals(xyz), "estimated-open3d"
            else:
                normals, normals_source = unit(normals), "client"
        t1 = time.perf_counter()
        result = classical.reconstruct(xyz, normals, self.method, self.target_faces)
        if len(result.mesh.faces) == 0:
            raise ReconstructionError(f"{self.label} produced no triangles from these {len(xyz)} points", 422)
        return Reconstruction(result.mesh, {"preprocess": (t1 - t0) * 1e3, "inference": result.seconds * 1e3,
                                            "postprocess": 0.0},
                              {"method": self.method, "normals_source": normals_source, **result.params})


class ModelBackend:
    """MeshAnything V2 / FastMesh / TreeMeshGPT, loaded once and reused for every request."""

    def __init__(self, model: str, variant: Optional[str] = None, device: str = "auto", precision: str = "auto",
                 attn: str = "auto", time_budget_s: Optional[float] = None, max_new_tokens: Optional[int] = None,
                 upsample: str = "repeat"):
        import mesh_wrappers.loading  # noqa: F401  (points HF_HUB_CACHE at checkpoints/ before transformers loads)
        import torch

        from benchmarks.profiler import StageTimer
        from mesh_wrappers import flash_shim
        from mesh_wrappers.registry import get_wrapper_class

        self._torch = torch
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        elif device.startswith("cuda") and not torch.cuda.is_available():
            raise RuntimeError("CUDA requested but torch.cuda.is_available() is False")
        if precision == "auto":  # fp16 weights: fits a 2 GB GPU and halves the bytes read per token
            precision = "fp16-full" if device.startswith("cuda") else "fp32"
        self.device, self.precision = device, precision
        self.time_budget_s, self.max_new_tokens, self.upsample = time_budget_s, max_new_tokens, upsample

        self.wrapper = get_wrapper_class(model)(variant=variant, device=device, precision=precision)
        if not self.wrapper.repo_available():
            raise RuntimeError(f"{self.wrapper.display_name}: code not found in {self.wrapper.repo_dir}; "
                               f"run: python models/setup_models.py")
        ok, msg = self.wrapper.weights_available()
        if not ok:
            raise RuntimeError(f"{self.wrapper.display_name}: weights not available ({msg}); "
                               f"run: python models/setup_models.py --models {self.wrapper.name}")
        self.wrapper.attn_backend = ("n/a (native SDPA)" if self.wrapper.name == "treemeshgpt"
                                     else flash_shim.setup_attention(attn, device))
        t0 = time.perf_counter()
        self.wrapper.load()
        self.load_s = time.perf_counter() - t0
        # per-stage timers stay installed for the whole server lifetime (decode steps, ms/token, time budget)
        self.timer = StageTimer(device)
        for stage, obj, attr in self.wrapper.stage_targets():
            self.timer.wrap(stage, obj, attr)

    def describe(self) -> dict:
        w = self.wrapper
        name = self._torch.cuda.get_device_name(self.device) if self.device.startswith("cuda") else "cpu"
        return {"backend": "model", "model": w.name, "model_display": w.display_name, "variant": w.variant,
                "device": self.device, "device_name": name, "precision": self.precision,
                "attn": w.attn_backend, "load_s": round(self.load_s, 2), "time_budget_s": self.time_budget_s}

    def reconstruct(self, xyz: np.ndarray, normals: Optional[np.ndarray] = None, seed: int = 0) -> Reconstruction:
        from benchmarks.profiler import GenerationBudgetExceeded, cuda_sync

        w = self.wrapper
        t0 = time.perf_counter()
        normals_source = "unused"
        if w.needs_normals:
            if normals is None:  # the headset keeps xyz only: normals are estimated here, on the edge
                normals, normals_source = estimate_normals(xyz), "estimated-open3d"
            else:
                normals, normals_source = unit(normals), "client"
        rng = np.random.default_rng(seed)
        rx, rn, rinfo = resample(xyz, normals, w.native_points, self.upsample, rng,
                                 downsample=getattr(w, "downsample", "random"))
        normalizer = w.normalizer(rx)
        t1 = time.perf_counter()

        self.timer.reset()
        self.timer.deadline = t1 + self.time_budget_s if self.time_budget_s else None
        try:
            gen = w.generate(normalizer.apply(rx), rn, self.max_new_tokens, seed)
        except GenerationBudgetExceeded as e:
            raise ReconstructionError(f"time budget of {self.time_budget_s} s exceeded ({e})", 504) from e
        except (self._torch.OutOfMemoryError, MemoryError) as e:
            raise ReconstructionError(f"out of memory on {self.device}: {str(e).splitlines()[0][:200]}", 503) from e
        finally:
            self.timer.deadline = None
            cuda_sync(self.device)
        t2 = time.perf_counter()

        if gen.mesh is not None:
            mesh = cleanup(gen.mesh)
        elif gen.triangles is not None:
            mesh = triangles_to_mesh(gen.triangles)
        else:
            raise ReconstructionError("the model returned no geometry")
        k = w.output_scale  # model output frame -> normalised input frame -> input cloud frame
        mesh = transform_vertices(mesh, lambda v: normalizer.invert(np.asarray(v) * k))
        if len(mesh.faces) == 0:
            raise ReconstructionError("the model produced an empty mesh")
        t3 = time.perf_counter()

        steps = self.timer.summary()
        info = {"normals_source": normals_source, "resample": rinfo["resample"], "truncated": bool(gen.truncated),
                "decode_steps": steps.get("decode_steps"), "ms_per_token_p50": steps.get("ms_per_token_p50")}
        return Reconstruction(mesh, {"preprocess": (t1 - t0) * 1e3, "inference": (t2 - t1) * 1e3,
                                     "postprocess": (t3 - t2) * 1e3}, info)
