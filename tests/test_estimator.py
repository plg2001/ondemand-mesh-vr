from benchmarks.estimator import decode_time_s
from benchmarks.flops import WorkloadEstimate


def tiny_workload(T=10):
    dec = {"d_model": 1024, "n_layers": 24, "n_heads": 16, "ffn_dim": 4096, "vocab": 132, "params": 300_000_000,
           "ffn": "relu", "head_mlp_params": 0}
    return WorkloadEstimate(params_total=300_000_000, flops_total=0.0, flops_by_component={}, decode_steps=T,
                            cond_tokens=0, kv_bytes_per_token={"fp16": 0.0, "int4": 0.0, "fp32": 0.0},
                            weights_bytes={}, peak_kv_bytes={}, decoder=dec, one_shot_flops=0.0,
                            activation_bytes_fp16=0.0)


def test_bandwidth_bound_decoding_hand_computed():
    # 300M params in fp16 = 600 MB read per token; 10 GB/s effective -> 60 ms/token; compute negligible
    dev = {"gpu_fp16_tflops": 1000.0, "gpu_fp32_tflops": 1000.0, "mem_bandwidth_gbs": 10.0,
           "efficiency_memory": 1.0, "efficiency_compute": 1.0, "per_step_overhead_ms": 0.0}
    t = decode_time_s(tiny_workload(10), dev, {"efficiency_memory": 1, "efficiency_compute": 1}, "fp16", "nominal")
    assert abs(t - 10 * 0.06) < 1e-3
