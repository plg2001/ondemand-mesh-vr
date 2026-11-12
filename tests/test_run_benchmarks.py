"""Benchmark sweep: configuration names and commands (normals included) and the summary table's normals column."""
from pathlib import Path

from benchmarks.run_benchmarks import config_tag, inference_cmd, parse_args
from benchmarks.summarize import write_summary


def test_config_tag_keeps_old_names_for_default_normals():
    assert config_tag("real", "meshanything", "box", 2048) == "real_meshanything_box_n2048"
    assert config_tag("real", "meshanything", "box", 2048, "auto") == "real_meshanything_box_n2048"
    assert config_tag("real", "meshanything", "box", 2048, "estimate") == "real_meshanything_box_n2048_normals-estimate"


def test_inference_cmd_passes_the_normals_and_options():
    args = parse_args(["--modes", "real", "--normals", "file", "estimate", "--quality", "--time-budget-s", "300"])
    assert args.normals == ["file", "estimate"]
    cmd = inference_cmd(args, "python", "real", "meshanything", "box", 2048, "estimate", Path("r.json"),
                        Path("r.csv"), variant=None)
    assert cmd[cmd.index("--normals") + 1] == "estimate"
    assert "--quality" in cmd and cmd[cmd.index("--time-budget-s") + 1] == "300.0"
    assert "--variant" not in cmd
    assert parse_args([]).normals == ["auto"]


def _run(normals_source, fscore):
    return {"mode": "real", "model": "meshanything", "variant": "default", "device": "cuda", "precision": "fp16-full",
            "status": "ok", "input": {"shape": "box", "input_points": 2048, "normals_source": normals_source},
            "timings": {"inference_s": 70.0}, "mesh": {"vertices": 176, "triangles": 340},
            "quality": {"chamfer_l2": 0.001, "fscore@0.01": fscore}}


def test_summary_keeps_runs_with_different_normals_apart():
    md = write_summary([_run("file", 0.70), _run("estimated-open3d", 0.55),
                        {"mode": "real", "model": "fastmesh", "status": "oom",
                         "input": {"shape": "box", "input_points": 2048, "normals_requested": "estimate"}}])
    rows = [line for line in md.splitlines() if line.startswith("| meshanything")]
    assert len(rows) == 2
    assert any("| dalla mesh |" in r and "| 0.700 |" in r for r in rows)
    assert any("| stimate |" in r and "| 0.550 |" in r for r in rows)
    assert any(line.startswith("| fastmesh") and "| stimate | oom |" in line for line in md.splitlines())
