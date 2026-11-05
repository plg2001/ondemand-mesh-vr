"""Clone the official repositories at pinned commits and download the pretrained weights.

    python models/setup_models.py                      # repos + default weights (MA V2, TMG 7bit, FastMesh V1K)
    python models/setup_models.py --skip-weights       # only clone the code (enough for mock mode)
    python models/setup_models.py --models treemeshgpt --tmg-variants 7bit 9bit
    python models/setup_models.py --fastmesh-variants V1K V4K

Upstream sources are never modified: compatibility is handled at runtime (models/mesh_wrappers).
Pinned commits and weight hashes live in models/upstream.lock.json.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from mesh_wrappers.loading import CHECKPOINTS, LOCK_FILE, THIRD_PARTY, sha256_file  # noqa: E402

REPOS = {
    "meshanything": ("MeshAnythingV2", "https://github.com/buaacyw/MeshAnythingV2.git"),
    "fastmesh": ("FastMesh", "https://github.com/jhkim0759/FastMesh.git"),
    "treemeshgpt": ("TreeMeshGPT", "https://github.com/sail-sg/TreeMeshGPT.git"),
}
HF_WEIGHTS = {
    "meshanything": [("Yiwen-ntu/meshanythingv2", ["config.json", "model.safetensors"]),
                     ("facebook/opt-350m", ["config.json"])],
    "fastmesh:V1K": [("WopperSet/FastMesh-V1K", ["config.json", "model.safetensors"]),
                     ("facebook/opt-350m", ["config.json"])],
    "fastmesh:V4K": [("WopperSet/FastMesh-V4K", ["config.json", "model.safetensors"])],
}
TMG_GDRIVE = {"7bit": "1IERM76szBQq9oAMoFw1Sbgp3kx97_Kib", "9bit": "19Auv48x7kgoODRS7dij8QijWzDqRjq97"}


def git(*args, cwd=None) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


def load_lock() -> dict:
    return json.loads(LOCK_FILE.read_text(encoding="utf-8")) if LOCK_FILE.exists() else {"repos": {}, "weights": {}}


def save_lock(lock: dict) -> None:
    tmp = LOCK_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")
    tmp.replace(LOCK_FILE)  # atomic: concurrent readers never see a half-written file


def setup_repo(key: str, lock: dict, update: bool) -> None:
    dirname, url = REPOS[key]
    dest = THIRD_PARTY / dirname
    pinned = lock["repos"].get(key, {}).get("commit")
    if not dest.exists():
        print(f"[clone] {url}")
        git("clone", "--quiet", url, str(dest))
    elif update:
        git("fetch", "--quiet", "origin", cwd=dest)
    if pinned and not update:
        if git("rev-parse", "HEAD", cwd=dest) != pinned:
            try:
                git("checkout", "--quiet", pinned, cwd=dest)
            except subprocess.CalledProcessError:  # shallow clone without that commit
                git("fetch", "--quiet", "--unshallow", cwd=dest)
                git("checkout", "--quiet", pinned, cwd=dest)
    elif update:
        git("checkout", "--quiet", "origin/HEAD", cwd=dest)
    sha = git("rev-parse", "HEAD", cwd=dest)
    dirty = bool(git("status", "--porcelain", cwd=dest))
    lock["repos"][key] = {"dir": f"third_party/{dirname}", "url": url, "commit": sha}
    print(f"[repo]  {dirname:15s} {sha[:12]}{'  (WARNING: local modifications)' if dirty else ''}")


def download_hf(key: str, lock: dict) -> None:
    from huggingface_hub import HfApi, hf_hub_download

    for repo, files in HF_WEIGHTS[key]:
        revision = HfApi().model_info(repo).sha
        for fname in files:
            print(f"[hf]    {repo}/{fname} @ {revision[:10]}")
            path = Path(hf_hub_download(repo, fname, revision=revision))
            entry = {"source": f"hf://{repo}/{fname}", "revision": revision, "bytes": path.stat().st_size}
            if fname.endswith(".safetensors"):
                entry["sha256"] = sha256_file(path)
            lock["weights"][f"{repo}/{fname}"] = entry


def download_tmg(variant: str, lock: dict) -> None:
    import gdown

    dest = CHECKPOINTS / "treemeshgpt" / f"treemeshgpt_{variant}.pt"
    dest.parent.mkdir(parents=True, exist_ok=True)
    if not dest.exists():
        print(f"[gdrive] treemeshgpt_{variant}.pt")
        out = gdown.download(id=TMG_GDRIVE[variant], output=str(dest) + ".part", quiet=False)
        if out is None:
            raise SystemExit(
                f"Google Drive download failed (quota?). Download manually from "
                f"https://drive.google.com/uc?id={TMG_GDRIVE[variant]} and save it as {dest}")
        shutil.move(out, dest)
    lock["weights"][f"treemeshgpt_{variant}"] = {
        "source": f"gdrive://{TMG_GDRIVE[variant]}", "path": dest.relative_to(CHECKPOINTS.parent).as_posix(),
        "bytes": dest.stat().st_size, "sha256": sha256_file(dest)}
    print(f"[ok]    {dest} ({dest.stat().st_size / 2**30:.2f} GB)")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", nargs="+", default=list(REPOS), choices=list(REPOS))
    ap.add_argument("--skip-weights", action="store_true")
    ap.add_argument("--update", action="store_true", help="move repos to latest upstream HEAD and re-pin")
    ap.add_argument("--tmg-variants", nargs="+", default=["7bit"], choices=list(TMG_GDRIVE))
    ap.add_argument("--fastmesh-variants", nargs="+", default=["V1K"], choices=["V1K", "V4K"])
    args = ap.parse_args()

    THIRD_PARTY.mkdir(exist_ok=True)
    lock = load_lock()
    lock.setdefault("repos", {})
    lock.setdefault("weights", {})
    for key in args.models:
        setup_repo(key, lock, args.update)
    save_lock(lock)
    if args.skip_weights:
        return
    for key in args.models:
        if key == "meshanything":
            download_hf("meshanything", lock)
        elif key == "fastmesh":
            for v in args.fastmesh_variants:
                download_hf(f"fastmesh:{v}", lock)
        else:
            for v in args.tmg_variants:
                download_tmg(v, lock)
        save_lock(lock)
    print(f"lock file: {LOCK_FILE}")


if __name__ == "__main__":
    main()
