# Codex Agent Switch 0.2.7.4 Acceptance Report

Date: 2026-09-05

Workspace: `E:\AISPace\Codex Agent Switch Project`

## Result

GPT-6 Astra is implemented as a first-class main-agent and Native Worker role. The four CAS roles now share one catalog, Profile and onboarding choices are intersected with the live Codex App Server `model/list` response, and model plus reasoning effort are validated exactly before launch or execution. Existing Profiles are preserved; newly created Profiles default to Astra with Luna as Worker.

Automated regression, real App Server catalog discovery, real Astra main-agent execution, real Astra Worker execution, Release build, packaging, installer startup, E-drive installation and artifact integrity checks passed.

## Live Codex evidence

Codex CLI: `codex-cli 0.153.2`

The current account returned:

| CAS role | Model ID | Default | Live reasoning efforts |
| --- | --- | --- | --- |
| Astra | `gpt-6-astra` | yes | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` |
| Sol | `gpt-5.6-sol` | no | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` |
| Terra | `gpt-5.6-terra` | no | `low`, `medium`, `high`, `xhigh`, `max`, `ultra` |
| Luna | `gpt-5.6-luna` | no | `low`, `medium`, `high`, `xhigh`, `max` |

The real Astra main-agent test returned the exact marker `CAS_ASTRA_MAIN_OK`. The real Native Astra Worker test returned `CAS_ASTRA_WORKER_OK`, and its transient thread was deleted afterward.

## Automated validation

| Check | Result |
| --- | --- |
| `CodexAgentSwitch.Tests` Release | 435/435 passed |
| `CodexAgentSwitch.Bootstrapper.Tests` Release | 19/19 passed |
| Real Astra/catalog integration group | 5/5 passed; three opt-in checks executed and two unrelated opt-in checks exited by guard |
| Solution Release build | passed; 0 warnings, 0 errors |
| Release manifest size/SHA-256 validation | passed |
| ZIP entry-point validation | passed |
| Windows App Runtime installer signature | valid; Microsoft Corporation |
| Portable, installed and runtime-bootstrapper startup | passed |
| Physical C-drive residual comparison | passed; no monitored changes |

Final C-drive audit evidence: `.tmp/c-drive-release-audit-20260905-114234/result.json`.

## Release artifacts

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | 247117051 | `f2c63e36a5b71994c5fefcd0c3b0ac100950a2e7cab980a37726335c49309f21` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | 380383028 | `567498f9cbe6f81984580bc679d3f1bcf07d86985f5d7618e5db89dd6a030f6b` |
| `CodexAgentSwitch-win10-x64.zip` | 36862941 | `57e565967a77157ef6c6b1e09f29a57951458be5f28e6fee3d287370a3afb738` |
| `CodexAgentSwitch-win10-x64.zip.sha256` | 98 | `55040b099451348b6cdbfbbe13109c5329b36e95c5308db5c8866de27bd13620` |

## Visual acceptance boundary

The final packaged app started successfully with isolated E-drive data and a 1024 x 720 launch size. The available Computer Use runtime did not expose native Windows application surfaces, so a genuine 125% DPI visual interaction inspection and a new screenshot could not be captured in this run. No PowerShell UI-Automation proxy was substituted. The obsolete README screenshot reference was removed rather than publishing stale or fabricated visual evidence.
