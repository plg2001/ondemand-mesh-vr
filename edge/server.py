"""Edge server prototype: receives a sparse point cloud, answers with the reconstructed mesh as .glb.

Run from the repository root:
    python -m edge.server                                      # mock backend (convex hull): no weights, no GPU
    python -m edge.server --mock-delay-s 3                     # mock + 3 s of simulated inference per request
    python -m edge.server --backend model --model meshanything # real model (weights in checkpoints/)
    python -m edge.server --backend classical --method ball_pivoting   # classical reconstruction, milliseconds
    python -m edge.server --host 0.0.0.0                       # reachable from other devices on the LAN (Quest)
    python -m edge.server --save-meshes out/meshes             # also keep every mesh sent, as .glb

Interactive API docs (try every endpoint from the browser): http://127.0.0.1:8000/docs

Protocol
    GET  /health                          JSON: backend, model, device, requests served
    POST /reconstruct?format=xyz|xyzn     body = little-endian float32, N x 3 (xyz) or N x 6 (xyz + normals)
         &object_id=<label>&seed=<int>    -> 200 model/gltf-binary, or JSON {"detail": ...} with 4xx/5xx
    GET  /shapes                          point clouds stored on the edge (data/inputs/manifest.json)
    GET  /shapes/{name}/points?points=N   float32 xyz of a stored cloud (scenario: the edge holds the scene)
    GET  /shapes/{name}/mesh?points=N     .glb reconstructed from a stored cloud
    GET  /requests?since=<unix time>      CSV of the requests logged since then (sessions on another PC fetch it)
    GET  /meshes/{file}                   a mesh saved with --save-meshes

Coordinates: points are expressed in the frame of the files in data/inputs (right-handed, +Y up, the glTF
frame); the mesh is returned in the same frame and glTFast converts it to Unity's frame on import.

Timing headers, in milliseconds: X-Queue-Ms (waiting for the model: one reconstruction at a time),
X-Preprocess-Ms, X-Inference-Ms, X-Postprocess-Ms, X-Export-Ms and X-Server-Ms (total, from the moment the
request body has been received to the moment the response is ready). On the client,
round-trip time - X-Server-Ms = network transfer + HTTP overhead.

Accuracy: when the object id is one of the scene's objects (--scene, default demo_room) the server compares the mesh
with the original one, i.e. with the dense reference cloud that data/scenes/build_scene.py samples from it
(data/scenes/<scene>/<id>_ref.npy, same local frame as the points and the mesh). Headers X-FScore (F-score at 1% of
the bounding-box diagonal), X-Precision, X-Recall, X-Chamfer-L2 and X-Quality-Ms: the time of this evaluation, which
is included in X-Server-Ms (subtract it for the latency of the reconstruction alone; --no-quality turns it off).
/health also gives typical_s (median server time of the last meshes, or the mock delay) and budget_s (time budget,
0 = none): the client's inputs for estimating how long a request will take; and server_time (seconds since 1970), so
that a client on another PC can measure the difference between the two clocks. Every log row has t_epoch, the same
kind of time: unlike the local "timestamp" it does not depend on the server's time zone.
"""
from __future__ import annotations

import argparse
import asyncio
import csv
import datetime as dt
import io
import json
import re
import statistics
import sys
import threading
import time
import uuid
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import PlainTextResponse, Response
import numpy as np

from edge import ROOT  # also puts models/ on sys.path for the mesh_wrappers imports below
from edge.reconstruction import (ClassicalBackend, MockBackend, ModelBackend, Reconstruction, ReconstructionError, decode_points,
                                 encode_points, export_glb)
from mesh_wrappers.mesh_io import quality_metrics
from mesh_wrappers.pointcloud import load_pointcloud

DATA_INPUTS = ROOT / "data" / "inputs"
DEFAULT_LOG = ROOT / "benchmarks" / "results" / "tmp" / "edge_requests.csv"
SCENES = ROOT / "data" / "scenes"
LOG_FIELDS = ["timestamp", "request_id", "source", "object_id", "status", "backend", "model", "points_in",
              "bytes_in", "bytes_out", "vertices", "triangles", "queue_ms", "preprocess_ms", "inference_ms",
              "postprocess_ms", "export_ms", "server_ms", "decode_steps", "detail", "client", "user_agent",
              "quality_ms", "fscore", "precision", "recall", "chamfer_l2", "mesh_file", "t_epoch"]
MESH_FILE = re.compile(r"^[A-Za-z0-9_.-]+\.glb$")
KEEP_ROWS = 10000  # requests kept in memory for GET /requests


class RequestLog:
    """One CSV row per request (git-ignored by default): raw material for the thesis tables.

    `client` is the address the request came from: 127.0.0.1 for the Unity editor on this PC and for a headset
    connected through the USB cable (adb reverse), the headset's LAN address over Wi-Fi.
    A log written with other columns (older version) is renamed aside instead of being mixed with the new rows.
    """

    def __init__(self, path: Optional[Path]):
        self.path = path
        self._lock = threading.Lock()

    def write(self, row: dict) -> None:
        if self.path is None:
            return
        with self._lock:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            new = not self.path.exists()
            if not new:
                with self.path.open(encoding="utf-8") as f:
                    header = f.readline().strip()
                if header != ",".join(LOG_FIELDS):
                    stamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
                    self.path.rename(self.path.with_name(f"{self.path.stem}_old_{stamp}{self.path.suffix}"))
                    new = True
            with self.path.open("a", newline="", encoding="utf-8") as f:
                w = csv.DictWriter(f, fieldnames=LOG_FIELDS, extrasaction="ignore")
                if new:
                    w.writeheader()
                w.writerow(row)


def _ms(x: Optional[float]) -> str:
    return "" if x is None else f"{x:.1f}"


def load_references(scene_dir: Path) -> dict[str, np.ndarray]:
    """object id -> dense reference cloud (N x 3) sampled from the object's original mesh by build_scene.py."""
    return {f.name[:-len("_ref.npy")]: np.load(f)[:, :3] for f in sorted(scene_dir.glob("*_ref.npy"))}


def _safe_name(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]", "_", text)[:60] or "mesh"


def create_app(backend, inputs_dir: Path = DATA_INPUTS, log_csv: Optional[Path] = DEFAULT_LOG,
               verbose: bool = True, objects: Optional[set[str]] = None,
               references: Optional[dict[str, np.ndarray]] = None, save_meshes: Optional[Path] = None) -> FastAPI:
    """objects: if given, POST /reconstruct serves only these object ids and refuses the others at once (HTTP 409,
    not logged), so that on a slow edge the client's queue is not blocked by objects that cannot finish.
    references: object id -> dense reference cloud of the original mesh: the accuracy of each mesh sent is measured.
    save_meshes: folder where every mesh sent is also written, as <request id>_<object id>.glb."""
    app = FastAPI(title="ondemand-mesh-vr edge server", version="0.1.0",
                  description="Sparse point cloud in, reconstructed mesh (.glb) out.")
    model_lock = asyncio.Lock()  # one reconstruction at a time: the model owns the GPU
    log = RequestLog(log_csv)
    state = {"served": 0, "started": time.time(), "durations": [], "rows": []}
    describe = backend.describe()
    label = describe.get("model_display", describe["model"])
    budget_s = getattr(backend, "time_budget_s", None) or 0.0

    def record(row: dict) -> None:
        log.write(row)
        state["rows"] = (state["rows"] + [row])[-KEEP_ROWS:]

    async def reconstruct_and_reply(xyz, normals, *, source: str, object_id: str, seed: int, bytes_in: int,
                                    request: Optional[Request] = None) -> Response:
        t_received = time.perf_counter()
        request_id = uuid.uuid4().hex[:8]
        row = {"timestamp": dt.datetime.now().isoformat(timespec="milliseconds"), "t_epoch": f"{time.time():.3f}",
               "request_id": request_id,
               "source": source, "object_id": object_id, "backend": describe["backend"],
               "model": describe["model"], "points_in": len(xyz), "bytes_in": bytes_in}
        if request is not None:
            row.update(client=request.client.host if request.client else "",
                       user_agent=request.headers.get("user-agent", ""))
        async with model_lock:
            t_start = time.perf_counter()
            try:
                rec: Reconstruction = await asyncio.to_thread(backend.reconstruct, xyz, normals, seed)
                t_export = time.perf_counter()
                glb = await asyncio.to_thread(export_glb, rec.mesh)
                export_ms = (time.perf_counter() - t_export) * 1e3
            except ReconstructionError as e:
                record(dict(row, status=f"error {e.status_code}", detail=str(e),
                               server_ms=_ms((time.perf_counter() - t_received) * 1e3)))
                raise HTTPException(e.status_code, str(e)) from e
            except Exception as e:  # unexpected failure inside the model: report it instead of a bare 500
                detail = f"{type(e).__name__}: {str(e).splitlines()[0][:300] if str(e) else ''}"
                record(dict(row, status="error 500", detail=detail,
                               server_ms=_ms((time.perf_counter() - t_received) * 1e3)))
                print(f"[edge] {request_id} failed: {detail}", file=sys.stderr, flush=True)
                raise HTTPException(500, detail) from e
        # outside the model lock: the next reconstruction does not wait for the evaluation
        quality, quality_ms = None, None
        reference = references.get(object_id) if references else None
        if reference is not None:
            t_quality = time.perf_counter()
            quality = await asyncio.to_thread(quality_metrics, rec.mesh, reference)
            quality_ms = (time.perf_counter() - t_quality) * 1e3
        mesh_file = ""
        if save_meshes is not None:
            save_meshes.mkdir(parents=True, exist_ok=True)
            mesh_file = f"{request_id}_{_safe_name(object_id or source)}.glb"
            (save_meshes / mesh_file).write_bytes(glb)
        server_ms = (time.perf_counter() - t_received) * 1e3
        queue_ms = (t_start - t_received) * 1e3
        tm = rec.timings_ms
        state["served"] += 1
        state["durations"] = (state["durations"] + [server_ms / 1e3])[-10:]
        row.update(status="ok", bytes_out=len(glb), vertices=len(rec.mesh.vertices), triangles=len(rec.mesh.faces),
                   queue_ms=_ms(queue_ms), preprocess_ms=_ms(tm["preprocess"]), inference_ms=_ms(tm["inference"]),
                   postprocess_ms=_ms(tm["postprocess"]), export_ms=_ms(export_ms), server_ms=_ms(server_ms),
                   decode_steps=rec.info.get("decode_steps") or "", quality_ms=_ms(quality_ms), mesh_file=mesh_file)
        if quality:
            row.update(fscore=f"{quality['fscore@0.01']:.4f}", precision=f"{quality['precision']:.4f}",
                       recall=f"{quality['recall']:.4f}", chamfer_l2=f"{quality['chamfer_l2']:.6f}")
        record(row)
        if verbose:
            name = object_id or source
            accuracy = f", F-score {quality['fscore@0.01']:.2f}" if quality else ""
            print(f"[edge] {request_id} {name}: {len(xyz)} points -> {len(rec.mesh.faces)} triangles "
                  f"({len(glb) / 1024:.1f} KB) | queue {queue_ms:.0f} ms, inference {tm['inference']:.0f} ms, "
                  f"total {server_ms:.0f} ms{accuracy}", flush=True)
        headers = {
            "X-Request-Id": request_id, "X-Backend": describe["backend"], "X-Model": describe["model"],
            "X-Model-Label": label,
            "X-Points-In": str(len(xyz)), "X-Vertices": str(len(rec.mesh.vertices)),
            "X-Triangles": str(len(rec.mesh.faces)), "X-Queue-Ms": f"{queue_ms:.2f}",
            "X-Preprocess-Ms": f"{tm['preprocess']:.2f}", "X-Inference-Ms": f"{tm['inference']:.2f}",
            "X-Postprocess-Ms": f"{tm['postprocess']:.2f}", "X-Export-Ms": f"{export_ms:.2f}",
            "X-Server-Ms": f"{server_ms:.2f}",
        }
        if rec.info.get("decode_steps"):
            headers["X-Decode-Steps"] = str(rec.info["decode_steps"])
        if rec.info.get("truncated"):
            headers["X-Truncated"] = "1"
        if quality:
            headers.update({"X-FScore": f"{quality['fscore@0.01']:.4f}", "X-Precision": f"{quality['precision']:.4f}",
                            "X-Recall": f"{quality['recall']:.4f}", "X-Chamfer-L2": f"{quality['chamfer_l2']:.6f}",
                            "X-Quality-Ms": f"{quality_ms:.2f}"})
        return Response(content=glb, media_type="model/gltf-binary", headers=headers)

    def load_stored_cloud(name: str, points: int):
        manifest_path = inputs_dir / "manifest.json"
        if not manifest_path.exists():
            raise HTTPException(404, f"no {manifest_path.name} in {inputs_dir}: run python data/inputs/generate_pointclouds.py")
        shapes = json.loads(manifest_path.read_text(encoding="utf-8")).get("shapes", {})
        if name not in shapes:
            raise HTTPException(404, f"unknown shape {name!r}; available: {', '.join(sorted(shapes))}")
        clouds = shapes[name].get("clouds", {})
        if str(points) not in clouds:
            raise HTTPException(404, f"{name}: no cloud with {points} points; available: {', '.join(clouds)}")
        files = clouds[str(points)].get("files", {})
        for kind in ("npy", "ply", "xyz"):  # npy/ply also carry the true normals
            entry = files.get(kind)
            if entry and (inputs_dir / entry["path"]).exists():
                path = inputs_dir / entry["path"]
                xyz, normals = load_pointcloud(path)
                return xyz, normals, path
        raise HTTPException(404, f"{name} n={points}: files listed in the manifest but missing on disk; "
                                 f"run python data/inputs/generate_pointclouds.py")

    @app.get("/health")
    async def health() -> dict:
        durations = state["durations"]
        typical_s = statistics.median(durations) if durations else describe.get("simulated_inference_s", 0.0)
        return {"status": "ok", **describe, "label": label, "requests_served": state["served"],
                "uptime_s": round(time.time() - state["started"], 1),
                "objects": sorted(objects) if objects is not None else "all",
                "typical_s": round(float(typical_s), 1), "budget_s": float(budget_s), "server_time": time.time(),
                "accuracy_objects": sorted(references) if references else []}

    @app.post("/reconstruct", response_class=Response,
              responses={200: {"content": {"model/gltf-binary": {}}, "description": "the reconstructed mesh"}})
    async def reconstruct(request: Request, fmt: str = Query("xyz", alias="format"), object_id: str = "",
                          seed: int = 0) -> Response:
        """Body: the point cloud as raw little-endian float32 (N x 3, or N x 6 with format=xyzn)."""
        if objects is not None and object_id not in objects:
            raise HTTPException(409, f"object {object_id!r} is not served by this server "
                                     f"(--objects {','.join(sorted(objects))})")
        body = await request.body()
        try:
            xyz, normals = decode_points(body, fmt)
        except ValueError as e:
            raise HTTPException(400, str(e)) from e
        return await reconstruct_and_reply(xyz, normals, source="client", object_id=object_id, seed=seed,
                                           bytes_in=len(body), request=request)

    @app.get("/requests", response_class=PlainTextResponse)
    async def requests_since(since: float = 0.0) -> PlainTextResponse:
        """The requests logged since a unix time, as CSV with the log's columns: a session run on another PC
        fetches its own rows this way (the server's log file stays on this PC)."""
        out = io.StringIO()
        w = csv.DictWriter(out, fieldnames=LOG_FIELDS, extrasaction="ignore", lineterminator="\n")
        w.writeheader()
        w.writerows(r for r in state["rows"] if float(r.get("t_epoch") or 0) >= since)
        return PlainTextResponse(out.getvalue(), media_type="text/csv")

    @app.get("/meshes/{name}", response_class=Response,
             responses={200: {"content": {"model/gltf-binary": {}}, "description": "a mesh saved with --save-meshes"}})
    async def saved_mesh(name: str) -> Response:
        path = save_meshes / name if save_meshes is not None and MESH_FILE.match(name) else None
        if path is None or not path.is_file():
            raise HTTPException(404, f"no saved mesh {name!r} (is the server started with --save-meshes?)")
        return Response(content=path.read_bytes(), media_type="model/gltf-binary")

    @app.get("/shapes")
    async def shapes() -> dict:
        manifest_path = inputs_dir / "manifest.json"
        if not manifest_path.exists():
            return {"shapes": {}}
        data = json.loads(manifest_path.read_text(encoding="utf-8")).get("shapes", {})
        return {"shapes": {name: sorted(int(n) for n in entry.get("clouds", {})) for name, entry in data.items()}}

    @app.get("/shapes/{name}/points", response_class=Response)
    async def shape_points(name: str, points: int = 2048) -> Response:
        xyz, _, _ = load_stored_cloud(name, points)
        return Response(content=encode_points(xyz), media_type="application/octet-stream",
                        headers={"X-Points": str(len(xyz)), "X-Format": "xyz"})

    @app.get("/shapes/{name}/mesh", response_class=Response,
             responses={200: {"content": {"model/gltf-binary": {}}, "description": "the reconstructed mesh"}})
    async def shape_mesh(request: Request, name: str, points: int = 2048, seed: int = 0) -> Response:
        xyz, normals, _ = load_stored_cloud(name, points)
        return await reconstruct_and_reply(xyz, normals, source="stored", object_id=f"{name}_n{points}", seed=seed,
                                           bytes_in=0, request=request)

    return app


# ============================================================================ CLI
def parse_args(argv=None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--backend", choices=("mock", "model", "classical"), default="mock",
                   help="mock = convex hull (no weights); model = MeshAnything V2 / FastMesh / TreeMeshGPT; "
                        "classical = hull / alpha shape / Ball Pivoting / Poisson (--method)")
    p.add_argument("--method", default="ball_pivoting", help="classical: hull | alpha | ball_pivoting | poisson")
    p.add_argument("--target-faces", type=int, default=None,
                   help="classical: decimate every mesh to this many triangles (e.g. 1000)")
    p.add_argument("--model", default="meshanything", help="meshanything | fastmesh | treemeshgpt")
    p.add_argument("--variant", default=None, help="FastMesh: V1K | V4K; TreeMeshGPT: 7bit | 9bit")
    p.add_argument("--device", default="auto", help="auto | cpu | cuda")
    p.add_argument("--precision", default="auto",
                   choices=("auto", "fp32", "fp16", "bf16", "fp16-full", "bf16-full"),
                   help="auto = fp16-full on GPU, fp32 on CPU")
    p.add_argument("--attn", default="auto", choices=("auto", "sdpa", "flash"))
    p.add_argument("--time-budget-s", type=float, default=None,
                   help="abort a generation after S seconds (HTTP 504) instead of blocking the queue")
    p.add_argument("--max-new-tokens", type=int, default=None, help="truncate generation (partial meshes)")
    p.add_argument("--mock-delay-s", type=float, default=0.0, help="mock: simulated inference time per request")
    p.add_argument("--host", default="127.0.0.1", help="127.0.0.1 = this PC only; 0.0.0.0 = whole LAN")
    p.add_argument("--port", type=int, default=8000)
    p.add_argument("--log-csv", type=Path, default=DEFAULT_LOG, help="per-request CSV log")
    p.add_argument("--no-log", action="store_true", help="do not write the CSV log")
    p.add_argument("--scene", default="demo_room",
                   help="scene whose objects get an accuracy measure (data/scenes/<scene>/<id>_ref.npy)")
    p.add_argument("--no-quality", action="store_true", help="do not measure the accuracy of the meshes")
    p.add_argument("--save-meshes", type=Path, default=None, help="also write every mesh sent to this folder")
    p.add_argument("--objects", default=None,
                   help="comma-separated object ids to serve (e.g. box_01); the others are refused at once, so a "
                        "slow model does not block the client's queue with objects it cannot finish")
    return p.parse_args(argv)


def main(argv=None) -> int:
    import uvicorn

    args = parse_args(argv)
    if args.backend == "mock":
        backend = MockBackend(args.mock_delay_s)
    elif args.backend == "classical":
        try:
            backend = ClassicalBackend(args.method, args.target_faces)
        except RuntimeError as e:
            print(f"[edge] cannot start: {e}", file=sys.stderr)
            return 1
    else:
        print(f"[edge] loading {args.model} on {args.device} (this takes a while)...", flush=True)
        try:
            backend = ModelBackend(args.model, args.variant, args.device, args.precision, args.attn,
                                   args.time_budget_s, args.max_new_tokens)
        except RuntimeError as e:
            print(f"[edge] cannot start: {e}", file=sys.stderr)
            return 1
    info = backend.describe()
    print("[edge] backend: " + ", ".join(f"{k}={v}" for k, v in info.items()), flush=True)
    log_csv = None if args.no_log else args.log_csv
    if log_csv:
        print(f"[edge] request log: {log_csv}", flush=True)
    print(f"[edge] API docs: http://{'127.0.0.1' if args.host == '0.0.0.0' else args.host}:{args.port}/docs",
          flush=True)
    if args.host == "0.0.0.0":
        from edge.netinfo import lan_ipv4_addresses

        addresses = lan_ipv4_addresses()
        for ip in addresses:
            print(f"[edge] reachable from the local network (e.g. the headset) at http://{ip}:{args.port}", flush=True)
        if addresses:
            print("[edge] if a firewall asks, allow it (Windows: Python on PRIVATE networks; Linux with ufw: "
                  f"sudo ufw allow {args.port}/tcp), or the headset cannot connect", flush=True)
    objects = {o.strip() for o in args.objects.split(",") if o.strip()} if args.objects else None
    if objects:
        print(f"[edge] serving only: {', '.join(sorted(objects))} (other objects are refused at once)", flush=True)
    references = None
    if not args.no_quality:
        references = load_references(SCENES / args.scene) or None
        if references:
            print(f"[edge] accuracy measured for the objects of '{args.scene}': {', '.join(references)}", flush=True)
        else:
            print(f"[edge] no reference clouds in {SCENES / args.scene}: accuracy off "
                  f"(build them with: python data/scenes/build_scene.py)", flush=True)
    if args.save_meshes:
        print(f"[edge] meshes saved in {args.save_meshes}", flush=True)
    app = create_app(backend, log_csv=log_csv, objects=objects, references=references, save_meshes=args.save_meshes)
    uvicorn.run(app, host=args.host, port=args.port, log_level="info", access_log=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
