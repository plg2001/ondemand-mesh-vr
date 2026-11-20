"""New-PC setup (tools/setup_pc.py): GPU -> PyTorch build, environment paths, weights copied from another folder."""
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("setup_pc", ROOT / "tools" / "setup_pc.py")
setup_pc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(setup_pc)


def test_parse_nvidia_smi():
    assert setup_pc.parse_nvidia_smi("NVIDIA GeForce GT 1030, 6.1, 582.66\n") == ("NVIDIA GeForce GT 1030", 6.1, "582.66")
    assert setup_pc.parse_nvidia_smi("Tesla K80, [N/A], 470.1") == ("Tesla K80", None, "470.1")  # old driver
    assert setup_pc.parse_nvidia_smi("") is None


def test_torch_build_follows_the_gpu():
    variant = setup_pc.torch_variant
    assert variant(("GeForce GT 1030", 6.1, "582"), "win32") == "cu118"      # this PC: Pascal
    assert variant(("GeForce RTX 4090", 8.9, "550"), "linux") == "cu118"     # Ada still has CUDA 11.8 kernels
    assert variant(("GeForce RTX 5090", 12.0, "570"), "linux") == "cu128"    # Blackwell needs CUDA 12.8
    assert variant(("Old GPU", None, "470"), "linux") == "cu118"
    assert variant(None, "linux") == "cpu"
    assert variant(None, "darwin") is None                                   # PyPI's build (CPU / Apple GPU)
    assert variant(("GeForce RTX 4090", 8.9, "550"), "linux", forced="cu128") == "cu128"


def test_venv_python_per_platform(tmp_path):
    assert setup_pc.venv_python(tmp_path, "win32") == tmp_path / ".venv" / "Scripts" / "python.exe"
    assert setup_pc.venv_python(tmp_path, "linux") == tmp_path / ".venv" / "bin" / "python"


LOCK = {"weights": {
    "treemeshgpt_7bit": {"path": "checkpoints/treemeshgpt/treemeshgpt_7bit.pt", "bytes": 5},
    "Org/model/model.safetensors": {"revision": "abc123", "bytes": 7},
    "Org/model/config.json": {"revision": "abc123", "bytes": 2},
}}


def test_weight_files_follow_the_lock(tmp_path):
    files = {name: path for name, path, _ in setup_pc.weight_files(LOCK["weights"], tmp_path)}
    assert files["treemeshgpt_7bit"] == tmp_path / "treemeshgpt" / "treemeshgpt_7bit.pt"
    assert files["Org/model/model.safetensors"] == \
        tmp_path / "huggingface" / "models--Org--model" / "snapshots" / "abc123" / "model.safetensors"


def test_copy_weights_copies_only_what_is_needed(tmp_path, monkeypatch):
    lock = tmp_path / "upstream.lock.json"
    lock.write_text(json.dumps(LOCK))
    monkeypatch.setattr(setup_pc, "LOCK", lock)
    src, dst = tmp_path / "disk", tmp_path / "checkpoints"
    for name, path, size in setup_pc.weight_files(LOCK["weights"], src):
        if name != "Org/model/config.json":  # one file missing on the disk
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"x" * size)
    (src / "huggingface" / "models--Org--model" / "blobs").mkdir()
    (src / "huggingface" / "models--Org--model" / "blobs" / "deadbeef").write_bytes(b"x" * 7)  # not copied
    assert setup_pc.copy_weights(src, dst, dry_run=True) == (2, 0, ["Org/model/config.json"])
    assert not dst.exists()
    assert setup_pc.copy_weights(src, dst, dry_run=False) == (2, 0, ["Org/model/config.json"])
    assert (dst / "treemeshgpt" / "treemeshgpt_7bit.pt").read_bytes() == b"xxxxx"
    assert not (dst / "huggingface" / "models--Org--model" / "blobs").exists()
    assert setup_pc.copy_weights(src, dst, dry_run=False)[:2] == (0, 2)  # second run: nothing to do


def test_launchers_are_windows_only():
    # both PCs of the project (this one and the lab's) are Windows: one program, EdgeControl.exe (tools/control,
    # tests/test_control_app.py), no shell or batch launchers
    assert (ROOT / "EdgeControl.exe").exists()
    assert not list(ROOT.glob("*.sh")) and not list((ROOT / "tools").rglob("*.sh"))
    attributes = (ROOT / ".gitattributes").read_text(encoding="utf-8")
    assert "*.exe binary" in attributes


def test_platform_tools_url_per_platform():
    url = setup_pc.platform_tools_url
    assert url("win32") == "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
    assert url("darwin").endswith("-darwin.zip") and url("linux").endswith("-linux.zip")


def test_platform_tools_go_where_the_headset_tools_look():
    spec = importlib.util.spec_from_file_location("quest", ROOT / "tools" / "quest" / "quest.py")
    quest = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(quest)
    assert quest.adb_candidates("win32", Path("C:/Users/x"), env={})[-1].parent == setup_pc.PLATFORM_TOOLS


def test_unpack_platform_tools_replaces_the_folder(tmp_path):
    import os
    import zipfile
    import pytest
    exe = "adb.exe" if os.name == "nt" else "adb"
    archive = tmp_path / "pt.zip"
    with zipfile.ZipFile(archive, "w") as z:
        z.writestr(f"platform-tools/{exe}", b"adb")
        z.writestr("platform-tools/AdbWinApi.dll", b"dll")
    dest = tmp_path / "tools" / "platform-tools"
    dest.mkdir(parents=True)
    (dest / "old.txt").write_text("stale")
    adb = setup_pc.unpack_platform_tools(archive, dest)
    assert adb == dest / exe and adb.read_bytes() == b"adb"
    assert sorted(p.name for p in dest.iterdir()) == sorted([exe, "AdbWinApi.dll"])
    assert [p.name for p in dest.parent.iterdir()] == ["platform-tools"]  # no temporary folder left

    broken = tmp_path / "broken.zip"
    with zipfile.ZipFile(broken, "w") as z:
        z.writestr("something/else.txt", b"")
    with pytest.raises(ValueError):
        setup_pc.unpack_platform_tools(broken, dest)
    assert (dest / exe).exists()  # a bad package leaves the working adb alone
