"""Measurement utilities: stage/step timers, host & GPU memory peaks, NVML/RAPL energy, system info."""
from __future__ import annotations

import functools
import os
import platform
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Callable, Optional

import psutil
import torch


def cuda_sync(device: str) -> None:
    if device.startswith("cuda") and torch.cuda.is_available():
        torch.cuda.synchronize()


# ============================================================================ timers
class GenerationBudgetExceeded(RuntimeError):
    """Raised from inside the decoder step when --time-budget-s is exceeded (timings are kept)."""


class StageTimer:
    """Wraps callables (``obj.attr``) to record wall-clock time of every call, with CUDA sync.

    Wrapping the bound attribute works both for nn.Module.forward (instance attribute shadows the
    class method used by ``__call__``) and for plain methods such as ``encode_latents``.
    """

    def __init__(self, device: str):
        self.device = device
        self.calls: dict[str, list[float]] = {}
        self._restore: list[tuple[object, str, object, bool]] = []
        self.deadline: Optional[float] = None  # perf_counter() value; checked before each decoder step

    def wrap(self, stage: str, obj: object, attr: str) -> None:
        original = getattr(obj, attr)
        had_instance_attr = attr in getattr(obj, "__dict__", {})
        bucket = self.calls.setdefault(stage, [])

        @functools.wraps(original)
        def timed(*args, **kwargs):
            if stage == "decoder_step" and self.deadline is not None and time.perf_counter() > self.deadline:
                raise GenerationBudgetExceeded(f"time budget exceeded after {len(bucket)} decoder calls")
            cuda_sync(self.device)
            t0 = time.perf_counter()
            try:
                return original(*args, **kwargs)
            finally:
                cuda_sync(self.device)
                bucket.append(time.perf_counter() - t0)

        setattr(obj, attr, timed)
        self._restore.append((obj, attr, original, had_instance_attr))

    def remove(self) -> None:
        for obj, attr, original, had in reversed(self._restore):
            if had:
                setattr(obj, attr, original)
            else:
                try:
                    delattr(obj, attr)
                except AttributeError:
                    pass
        self._restore.clear()

    def reset(self) -> None:
        for v in self.calls.values():
            v.clear()

    def summary(self) -> dict:
        out = {}
        for stage, ts in self.calls.items():
            if not ts:
                continue
            if stage == "decoder_step":
                prefill, steps = ts[0], ts[1:]
                out["prefill_s"] = prefill
                out["decode_steps"] = len(steps)
                out["decode_s"] = float(sum(steps))
                if steps:
                    s = sorted(steps)
                    out["ms_per_token_mean"] = 1e3 * sum(s) / len(s)
                    out["ms_per_token_p50"] = 1e3 * s[len(s) // 2]
                    out["ms_per_token_p95"] = 1e3 * s[min(len(s) - 1, int(0.95 * len(s)))]
                    q = max(1, len(steps) // 10)  # latency growth with context length (KV cache)
                    out["ms_per_token_first10pct"] = 1e3 * statistics.mean(steps[:q])
                    out["ms_per_token_last10pct"] = 1e3 * statistics.mean(steps[-q:])
            else:
                out[f"{stage}_s"] = float(sum(ts))
                out[f"{stage}_calls"] = len(ts)
        return out


# ============================================================================ memory
class HostMemoryMonitor:
    """Peak RSS of this process: OS-reported peak (Windows peak_wset / Linux ru_maxrss) + sampling."""

    def __init__(self, interval_s: float = 0.02):
        self.proc = psutil.Process()
        self.interval = interval_s
        self.peak = 0
        self.private_peak = 0  # committed private bytes (Windows) / USS-like; RSS alone hides paged-out pages
        self.baseline = self.proc.memory_info().rss
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None

    def _sample(self):
        mi = self.proc.memory_info()
        self.peak = max(self.peak, mi.rss)
        self.private_peak = max(self.private_peak, getattr(mi, "private", 0) or getattr(mi, "data", 0) or 0)

    def _run(self):
        while not self._stop.is_set():
            self._sample()
            self._stop.wait(self.interval)

    def __enter__(self):
        self.peak = self.proc.memory_info().rss
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        if self._thread:
            self._thread.join()
        self._sample()

    @staticmethod
    def os_peak_bytes() -> Optional[int]:
        mi = psutil.Process().memory_info()
        if hasattr(mi, "peak_wset"):
            return int(mi.peak_wset)
        try:
            import resource

            return int(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss) * 1024
        except ImportError:
            return None


class CudaMemory:
    def __init__(self, device: str):
        self.enabled = device.startswith("cuda") and torch.cuda.is_available()
        self.device = device

    def reset(self) -> None:
        if self.enabled:
            torch.cuda.reset_peak_memory_stats(self.device)

    def snapshot(self) -> dict:
        if not self.enabled:
            return {}
        free, total = torch.cuda.mem_get_info(torch.device(self.device))
        return {"allocated_bytes": torch.cuda.memory_allocated(self.device),
                "peak_allocated_bytes": torch.cuda.max_memory_allocated(self.device),
                "peak_reserved_bytes": torch.cuda.max_memory_reserved(self.device),
                "device_free_bytes": free, "device_total_bytes": total}


# ============================================================================ energy
class NvmlMonitor:
    """GPU energy (total-energy counter on Volta+, else integrated power samples) and device memory peak."""

    def __init__(self, index: int = 0, interval_s: float = 0.05):
        self.ok = False
        self.interval = interval_s
        self.samples: list[tuple[float, float]] = []
        self.mem_peak = 0
        self.energy_counter_mj: Optional[tuple[int, int]] = None
        try:
            import pynvml

            pynvml.nvmlInit()
            self.nv = pynvml
            self.h = pynvml.nvmlDeviceGetHandleByIndex(index)
            self.ok = True
        except Exception:
            return
        self._stop = threading.Event()
        self._thread = None

    def _power_w(self) -> Optional[float]:
        try:
            return self.nv.nvmlDeviceGetPowerUsage(self.h) / 1000.0
        except Exception:
            return None

    def _energy_mj(self) -> Optional[int]:
        try:
            return int(self.nv.nvmlDeviceGetTotalEnergyConsumption(self.h))
        except Exception:
            return None

    def _run(self):
        while not self._stop.is_set():
            p = self._power_w()
            if p is not None:
                self.samples.append((time.perf_counter(), p))
            try:
                self.mem_peak = max(self.mem_peak, self.nv.nvmlDeviceGetMemoryInfo(self.h).used)
            except Exception:
                pass
            self._stop.wait(self.interval)

    def __enter__(self):
        if self.ok:
            self._e0 = self._energy_mj()
            self._t0 = time.perf_counter()
            self._thread = threading.Thread(target=self._run, daemon=True)
            self._thread.start()
        return self

    def __exit__(self, *exc):
        if not self.ok:
            return
        self._stop.set()
        self._thread.join()
        self._t1 = time.perf_counter()
        e1 = self._energy_mj()
        if self._e0 is not None and e1 is not None:
            self.energy_counter_mj = (self._e0, e1)

    def result(self) -> dict:
        if not self.ok:
            return {"gpu_energy_source": "unavailable"}
        out = {"gpu_device_mem_peak_bytes": int(self.mem_peak)}
        if self.energy_counter_mj:
            out["gpu_energy_j"] = (self.energy_counter_mj[1] - self.energy_counter_mj[0]) / 1000.0
            out["gpu_energy_source"] = "nvml_total_energy"
        elif len(self.samples) >= 2:
            e = sum((t1 - t0) * (p0 + p1) / 2 for (t0, p0), (t1, p1) in zip(self.samples, self.samples[1:]))
            out["gpu_energy_j"] = e
            out["gpu_power_mean_w"] = statistics.mean(p for _, p in self.samples)
            out["gpu_energy_source"] = "nvml_power_integration"
        else:
            out["gpu_energy_source"] = "unsupported_by_device"  # e.g. GeForce GT 1030 reports N/A
        return out


class RaplMonitor:
    """CPU package energy on Linux (intel-rapl powercap). Unavailable on Windows."""

    PATH = Path("/sys/class/powercap/intel-rapl:0/energy_uj")

    def __init__(self):
        self.ok = self.PATH.exists() and os.access(self.PATH, os.R_OK)
        self.joules: Optional[float] = None

    def _read(self) -> int:
        return int(self.PATH.read_text())

    def __enter__(self):
        if self.ok:
            self._e0 = self._read()
        return self

    def __exit__(self, *exc):
        if self.ok:
            e1 = self._read()
            if e1 < self._e0:  # counter wrap
                e1 += int(Path("/sys/class/powercap/intel-rapl:0/max_energy_range_uj").read_text())
            self.joules = (e1 - self._e0) / 1e6

    def result(self) -> dict:
        if self.joules is None:
            return {"cpu_energy_source": "unavailable" if not self.ok else "error"}
        return {"cpu_energy_j": self.joules, "cpu_energy_source": "rapl"}


# ============================================================================ system info
def _cpu_name() -> str:
    if sys.platform == "win32":
        try:
            out = subprocess.run(["powershell", "-NoProfile", "-Command",
                                  "(Get-CimInstance Win32_Processor).Name"], capture_output=True, text=True, timeout=10)
            if out.stdout.strip():
                return out.stdout.strip()
        except Exception:
            pass
    if Path("/proc/cpuinfo").exists():
        for line in Path("/proc/cpuinfo").read_text().splitlines():
            if line.startswith("model name"):
                return line.split(":", 1)[1].strip()
    return platform.processor()


@functools.lru_cache(maxsize=1)
def system_info() -> dict:
    vm = psutil.virtual_memory()
    info = {
        "os": f"{platform.system()} {platform.release()} ({platform.version()})",
        "python": platform.python_version(),
        "torch": torch.__version__,
        "cpu": _cpu_name(),
        "cpu_cores_physical": psutil.cpu_count(logical=False),
        "cpu_threads": psutil.cpu_count(),
        "torch_threads": torch.get_num_threads(),
        "ram_total_bytes": vm.total,
        "ram_available_bytes_at_start": vm.available,
        "cuda_available": torch.cuda.is_available(),
    }
    try:
        import transformers

        info["transformers"] = transformers.__version__
    except ImportError:
        pass
    if torch.cuda.is_available():
        p = torch.cuda.get_device_properties(0)
        info.update(gpu=p.name, gpu_compute_capability=f"{p.major}.{p.minor}", gpu_total_bytes=p.total_memory,
                    cuda_runtime=torch.version.cuda, cuda_arch_list=torch.cuda.get_arch_list())
        try:
            import pynvml

            pynvml.nvmlInit()
            info["gpu_driver"] = pynvml.nvmlSystemGetDriverVersion()
        except Exception:
            pass
    try:
        root = Path(__file__).resolve().parents[1]
        info["repo_commit"] = subprocess.run(["git", "rev-parse", "HEAD"], cwd=root, capture_output=True,
                                             text=True).stdout.strip()
    except Exception:
        pass
    return info


def timed(fn: Callable, device: str):
    cuda_sync(device)
    t0 = time.perf_counter()
    out = fn()
    cuda_sync(device)
    return out, time.perf_counter() - t0
