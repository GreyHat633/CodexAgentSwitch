# Codex Agent Switch 0.2.3.1 Acceptance Report

Date: 2026-08-11

Workspace: `E:\AISPace\Codex Agent Switch Project`

Plan: `E:\AISPace\主模型项目区\plan\CodexAgentSwitch_0.2.3.1_Quick_Update_Plan.md`

## Result

The two planned code patches are implemented in the canonical workspace. Automated tests, builds, UI launch, live model discovery, and the natural-language delegation-preflight acceptance passed. The plan's final real OpenCode Zen model invocation is blocked by missing external login and therefore remains **not run**, not passed.

## Patch A: Delegation Capability Preflight

- Managed instructions place the capability preflight after minimum localization and before the first substantive implementation package.
- Tool-available and tool-unavailable branches are explicit.
- Micro-task exemptions remain explicit.
- The preflight is not represented as another trigger; the closed trigger set remains exactly eight.
- Instruction and orchestration regression coverage passed.

Natural-language acceptance used a disposable ordinary Git repository, not a worktree:

- Repository: `E:\AISPace\TestSpace\cas-0231-preflight-natural`
- Prompt contains none of: Worker, Luna, DeepSeek, Agent Switch, `delegate_worker`, or “分包”.
- Main localized `repository.py` and the missing atomic-save regression boundary before loading the scheduling tools.
- Main recorded `INITIAL_LOCALIZATION_COMPLETE` as `WORKER / BOUNDED_IMPLEMENTATION`.
- `delegate_worker` created task `cas-0231-json-inventory-impl` for project `cas-0231-preflight-natural`.
- The acceptance run stopped immediately after registration, before implementation or model completion, as required by the Plan.
- Thread: `019ff015-657b-7cb3-ae6b-d32528ba9a37`
- Event evidence: `E:\AISPace\TestSpace\cas-0231-preflight-output\events-final.jsonl`

Two setup-only observations preceded the successful run: the first process used a Windows sandbox helper that was denied by the host, and a later disposable harness used a mismatched project ID. Neither run produced product-workspace edits; the final run corrected both environmental conditions.

## Patch B: OpenCode Zen Provider

Transport decision: the existing external runtime is OpenAI-compatible, while Zen models can use different upstream protocols. The implementation therefore uses the OpenCode CLI as the minimal protocol bridge instead of adding several new protocol stacks.

- Official model discovery URL: `https://opencode.ai/zen/v1/models`
- Persisted selection: raw `model_id`
- Invocation format: `opencode/<model-id>`
- Credential behavior: reuse OpenCode's existing login; no Zen API key is copied into Agent Switch.
- Missing saved model: keep the saved value, mark the provider unavailable, and ask the user to reselect.
- Worker concurrency: 1 for this adapter.
- CLI execution uses argument-list construction, task working directory, cancellation, and `opencode run --auto --model ...`.

Live non-model evidence on 2026-08-11:

- OpenCode CLI installed for acceptance under `E:\AISPace\TestSpace\cas-opencode-cli`.
- CLI version: `1.18.16`.
- `opencode auth list`: `0 credentials`.
- Official model endpoint: object `list`, 61 entries, 61 unique IDs.
- No real model call was made because no user-selected model/login was available.

## Automated validation

All commands used E-drive-local CLI/NuGet/test roots.

| Check | Result |
| --- | --- |
| Focused launcher, orchestration, Zen, and existing provider tests | 56/56 passed |
| `CodexAgentSwitch.Tests` | 188/188 passed |
| `CodexAgentSwitch.Bootstrapper.Tests` | 19/19 passed |
| WinUI App x64 Debug build | passed, 0 warnings, 0 errors |
| Diff whitespace check | passed; only Git CRLF normalization notices |

The provider page was launched from the x64 Debug build with all test data and temporary files rooted under `E:\AISPace\Codex Agent Switch Project\.tmp\cas-0231-ui`. UI Automation confirmed the visible Zen controls `Refresh models`, `Save selection`, and `Test CLI`; visual inspection found no clipping or overlap at 1024 x 720. Screenshot evidence: `E:\AISPace\Codex Agent Switch Project\.tmp\cas-0231-ui\providers-zen-controls.png`.

## Acceptance boundary

- Delegation Preflight: **PASS**.
- OpenCode Zen configuration, dynamic discovery, selection persistence, UI, and CLI bridge: **PASS**.
- Real selected Zen model completing an External Worker request: **BLOCKED / NOT RUN** because OpenCode reports no login credentials.
- Overall Plan PASS: **pending the single real selected-model call** after the user completes `opencode auth login` and selects a currently available model.

No release package was produced in this development step.
