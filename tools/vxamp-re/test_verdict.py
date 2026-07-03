"""Tests for verdict.py (Task 6) — repack-vs-refit verdict.

Three tests:
1. compare() returns required keys and exact_frac is in [0, 1].
2. VERDICT is a valid decision string ("repack" or "refit").
3. exact_frac < 0.01 for the first pair — the concrete "not a repack" invariant:
   source weights are NOT stored verbatim in the device body.
"""
import vxamp as vx
import verdict


def test_compare_runs_on_a_matched_pair():
    name = vx.pairs()[0][0]
    r = verdict.compare(name)
    assert set(r) >= {"exact_frac", "corr", "max_abs_err"}
    assert 0.0 <= r["exact_frac"] <= 1.0


def test_verdict_is_decided():
    assert verdict.VERDICT in {"repack", "refit"}


def test_exact_frac_is_near_zero_for_first_pair():
    """Source weights are NOT verbatim in the device body — the 'not a repack'
    invariant.  exact_frac must be < 0.01 (any accidental near-zero collision
    from the FIR tail padding stays far below this bound)."""
    name = vx.pairs()[0][0]
    r = verdict.compare(name)
    assert r["exact_frac"] < 0.01, (
        f"exact_frac={r['exact_frac']:.4f} — source weights unexpectedly "
        f"found in device body (expected < 0.01; VERDICT should be 'refit')"
    )
