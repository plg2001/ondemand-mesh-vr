"""The SDPA replacement must match a naive softmax attention with flash-attn (>=2.1) semantics."""
import math

import pytest
import torch

from mesh_wrappers.flash_shim import sdpa_flash_attention_forward as shim


def reference(q, k, v, mask2d, causal, scale=None):
    # q,k,v: (B, L, H, D); bottom-right causal; mask2d (B, Lk) 1 = keep
    B, Lq, H, D = q.shape
    Lk = k.shape[1]
    scale = scale or 1 / math.sqrt(D)
    s = torch.einsum("bqhd,bkhd->bhqk", q, k) * scale
    allow = torch.ones(B, 1, Lq, Lk, dtype=torch.bool)
    if mask2d is not None:
        allow &= mask2d[:, None, None, :].bool()
    if causal:
        allow &= (torch.arange(Lk)[None, :] <= torch.arange(Lq)[:, None] + (Lk - Lq))[None, None]
    s = s.masked_fill(~allow, float("-inf"))
    p = torch.softmax(s, -1).nan_to_num(0.0)
    out = torch.einsum("bhqk,bkhd->bqhd", p, v)
    if mask2d is not None:
        out = out * mask2d[:, -Lq:, None, None]
    return out


@pytest.mark.parametrize("lq,lk,causal", [(16, 16, True), (1, 40, True), (8, 24, True), (12, 12, False)])
def test_matches_reference(lq, lk, causal):
    torch.manual_seed(0)
    q, k, v = torch.randn(2, lq, 4, 8), torch.randn(2, lk, 4, 8), torch.randn(2, lk, 4, 8)
    out = shim(q, k, v, None, lq, causal)
    torch.testing.assert_close(out, reference(q, k, v, None, causal), rtol=1e-4, atol=1e-5)


def test_padding_mask_and_scale():
    torch.manual_seed(1)
    q, k, v = torch.randn(2, 10, 2, 16), torch.randn(2, 10, 2, 16), torch.randn(2, 10, 2, 16)
    mask = torch.ones(2, 10)
    mask[1, 7:] = 0  # right padding on sample 1
    out = shim(q, k, v, mask, 10, False, softmax_scale=0.1)
    torch.testing.assert_close(out, reference(q, k, v, mask, False, 0.1), rtol=1e-4, atol=1e-5)
    assert torch.all(out[1, 7:] == 0)


def test_top_left_rule_for_single_query():
    # transformers: use_top_left_mask disables causal masking when q_len == 1 (same as bottom-right)
    q, k, v = torch.randn(1, 1, 2, 8), torch.randn(1, 9, 2, 8), torch.randn(1, 9, 2, 8)
    a = shim(q, k, v, None, 1, True, use_top_left_mask=True)
    b = shim(q, k, v, None, 1, True, use_top_left_mask=False)
    torch.testing.assert_close(a, b)


def test_install_makes_fa2_opt_equal_to_eager():
    from transformers import OPTConfig, OPTForCausalLM
    import transformers.modeling_flash_attention_utils as fau
    from transformers.models.opt import modeling_opt

    from mesh_wrappers import flash_shim

    flash_shim.install()
    assert fau._flash_attention_forward is shim and modeling_opt._flash_attention_forward is shim
    cfg = OPTConfig(hidden_size=64, num_hidden_layers=2, num_attention_heads=4, ffn_dim=128, vocab_size=50,
                    max_position_embeddings=64, word_embed_proj_dim=64)
    torch.manual_seed(0)
    eager = OPTForCausalLM(cfg).eval()
    fa2 = OPTForCausalLM._from_config(OPTConfig(**cfg.to_dict()), use_flash_attention_2=True).eval()
    fa2.load_state_dict(eager.state_dict())
    assert fa2.config._attn_implementation == "flash_attention_2"
    ids = torch.randint(4, 50, (1, 12))
    with torch.no_grad():
        torch.testing.assert_close(fa2(ids).logits, eager(ids).logits, rtol=1e-4, atol=1e-4)
        g1 = eager.generate(ids, max_new_tokens=8, do_sample=False)
        g2 = fa2.generate(ids, max_new_tokens=8, do_sample=False)
    assert torch.equal(g1, g2)
