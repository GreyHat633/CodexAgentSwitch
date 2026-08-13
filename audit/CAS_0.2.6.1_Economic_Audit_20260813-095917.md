# Codex Agent Switch 0.2.6.1 Economic Audit

Generated: 2026-08-13 09:59:19

## Executive Summary

| Round | Actual Cost | All-Sol Equivalent | Sol Displacement | Saving |
|---|---:|---:|---:|---:|
| R1 | 89.74 | 166.11 | 57.5% | 46% |
| R2 | 199.9 | 274.06 | 33.8% | 27.1% |
| TOTAL | 289.64 | 440.17 | 42.7% | 34.2% |

## 1. Actual Cost

| Round | Sol Actual | Luna Actual | Actual Total | Observed Paid-Point Decrease | Balance Increase Detected |
|---|---:|---:|---:|---:|---|
| R1 | 70.65 | 19.09 | 89.74 | 34.351281 | False |
| R2 | 181.36 | 18.54 | 199.9 | 155.246304 | True |

## 2. Sol Displacement

| Round | Luna Sol-Equivalent | All-Sol Equivalent | Displacement | Theoretical Saving |
|---|---:|---:|---:|---:|
| R1 | 95.46 | 166.11 | 57.5% | 46% |
| R2 | 92.7 | 274.06 | 33.8% | 27.1% |
| TOTAL | 188.16 | 440.17 | 42.7% | 34.2% |

## 3. Delegation Coverage

Mechanical proxy: Worker-touched files / final changed files, plus the changed-line footprint of those files.

| Round | Dev File Coverage | Dev Line Footprint | Core File Coverage | Core Line Footprint | Confidence |
|---|---:|---:|---:|---:|---|
| R1 | 40% (2/5) | 62.3% | 25% (1/4) | 46.3% | Medium-High |
| R2 | 11.1% (2/18) | 22.1% | 7.7% (1/13) | 10.8% | Medium-High |

## 4. Adoption Efficiency

Mechanical proxy: whether Worker-touched files survive the round diff, and whether Sol later touched the same file.

| Round | Worker Files | Retained | Direct Adopt | Main Adjusted | Reverted | Confidence |
|---|---:|---:|---:|---:|---:|---|
| R1 | 2 | 100% | 0% | 100% | 0% | Medium |
| R2 | 2 | 100% | 0% | 100% | 0% | Medium |

## R2 Continuation Tax Proxy

Sol normalized cost before the first observed R2 Luna token event: **5.16**.

This is only a mechanical relocalization/continuation proxy, not proof that every early Sol token was overhead.

## Method / Limits

- Actual Cost and Sol Displacement use incremental `token_count` deltas, so a single Sol session spanning R1/R2 is split correctly by time.
- Luna is normalized at 1/5 of Sol pricing; reasoning tokens are already included in output and are not double-charged.
- Paid-point balances are account-level observations; top-ups or stale balance events are flagged instead of silently treated as usage.
- Delegation Coverage is a Git/session mutation footprint proxy. It does not claim that one changed line equals one unit of difficulty.
- Adoption Efficiency distinguishes retained, directly retained, Main-adjusted, and reverted Worker-touched files. Main adjustment is not automatically a failure.
- No model call is used by this audit.
