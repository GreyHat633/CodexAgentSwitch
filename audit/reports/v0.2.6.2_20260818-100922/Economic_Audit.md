# Codex Agent Switch v0.2.6.2 Economic Audit

Generated: 2026-08-18 10:09:22

**Range:** v0.2.6.1 -> v0.2.6.2

**Session selection:** StrongDevelopmentEvidence

**Audit window:** 08/14/2026 06:21:55 -> 08/14/2026 07:24:57

## Executive Summary

| Actual Cost | All-Sol Equivalent | Sol Displacement | Saving |
|---:|---:|---:|---:|
| 462.49 | 542.13 | 18.4% | 14.7% |

## 1. Actual Cost

| Main Actual | Worker Actual | Actual Total | Paid-Point Decrease | Balance Increase Detected |
|---:|---:|---:|---:|---|
| 442.59 | 19.91 | 462.49 | 0 | False |

## 2. Sol Displacement

| Worker Sol-Equivalent | All-Sol Equivalent | Displacement | Theoretical Saving |
|---:|---:|---:|---:|
| 99.55 | 542.13 | 18.4% | 14.7% |

## 3. Delegation Coverage

Mechanical proxy: Worker-touched files / final changed files, plus changed-line footprint.

| Dev File Coverage | Dev Line Footprint | Core File Coverage | Core Line Footprint | Confidence |
|---:|---:|---:|---:|---|
| 29.4% (5/17) | 60.4% | 23.1% (3/13) | 56.4% | Medium-High |

## 4. Adoption Efficiency

Mechanical proxy: Worker-touched files retained in final diff, and whether Main touched them later.

| Worker Files | Retained | Direct Adopt | Main Adjusted | Reverted | Confidence |
|---:|---:|---:|---:|---:|---|
| 6 | 83.3% | 0% | 83.3% | 16.7% | Medium |

## Main-before-first-Worker Proxy

Main normalized cost before the first observed Worker token event: **37.16**.

This is only a mechanical proxy; it does not prove that all early Main cost was orchestration overhead.

## Relevant Sessions

| Model | Role | Start | End | Selection |
|---|---|---|---|---|
| gpt-5.6-sol | Main | 08/14/2026 06:31:55 | 08/14/2026 07:53:02 | final-commit-anchor |
| gpt-5.6-luna | Worker | 08/14/2026 06:35:24 | 08/14/2026 06:38:39 | explicit-worker-dev |
| gpt-5.6-luna | Worker | 08/14/2026 06:43:52 | 08/14/2026 06:51:48 | worker-overlap |

## Warnings

- Repository has uncommitted changes. Git-based coverage uses only v0.2.6.1..v0.2.6.2 and ignores current uncommitted changes.

## Method / Limits

- Latest two semantic-version tags are selected automatically in zero-touch mode.
- Main sessions require strong release evidence: final-commit hit, or target-version hit plus real `src/`/`tests/` mutation evidence.
- Worker child sessions may be selected by explicit development evidence or temporal overlap with a strong Main release session.
- Token cost uses incremental `token_count` deltas. Reasoning tokens are not double-charged.
- Model pricing comes from `audit.config.json`; unknown models are reported instead of guessed.
- Paid-point balance is account-level evidence, not a normalized-cost oracle.
- Delegation Coverage and Adoption Efficiency are mechanical proxies, not semantic work-quality measurements.
- Git metrics use committed `$BaseRef..$FinalRef`; uncommitted work is not silently included.
- The audit is read-only except for writing this report directory.
- No Sol/Luna/DeepSeek/OpenCode call is made by the audit.
