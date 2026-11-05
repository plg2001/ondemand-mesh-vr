import pytest
import yaml

from benchmarks.flops import decoder_step_flops, estimate_workload, meta_flops_step
from mesh_wrappers.loading import SPECS_DIR


@pytest.mark.parametrize("spec", ["meshanything_v2.yaml", "treemeshgpt.yaml", "fastmesh.yaml"])
def test_analytic_step_matches_flopcounter(spec):
    s = yaml.safe_load((SPECS_DIR / spec).read_text())
    dec = next(c for c in s["components"] if c["kind"] == "decoder_ar")
    dec = {k: v for k, v in dec.items() if k not in ("name", "kind", "head_mlp_params")}
    for ctx in (257, 2000):
        analytic = decoder_step_flops(dec, ctx)
        measured = meta_flops_step(dec, ctx)
        assert abs(analytic - measured) / measured < 0.02, (spec, ctx, analytic, measured)


def test_workload_scales_with_faces():
    s = yaml.safe_load((SPECS_DIR / "meshanything_v2.yaml").read_text())
    a, b = estimate_workload(s, n_faces=400), estimate_workload(s, n_faces=800)
    assert b.decode_steps == 2 * a.decode_steps
    assert b.flops_total > 1.9 * a.flops_total
    assert b.peak_kv_bytes["fp16"] > a.peak_kv_bytes["fp16"]
