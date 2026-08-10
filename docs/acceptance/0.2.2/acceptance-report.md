# Codex Agent Switch 0.2.2 acceptance report

Date: 2026-08-10  
Host: Windows 10 22H2 x64  
Display: 125% scaling; reported window DPI 119  
Canonical workspace: `E:\AISPace\Codex Agent Switch Project`  
Isolated development branch: `feat/0.2.2-external-runtime`, commit `508633a`

## Required final matrix

| Gate | Result | Evidence |
| --- | --- | --- |
| External Tool Loop | PASS | Real DeepSeek completed model → tool → result → model loops and returned final results. |
| Shell | PASS | DeepSeek requested a real read-only PowerShell location command and consumed its result. |
| File Create | PASS | DeepSeek created `sol-ds-022.txt` with exact content `SOL_DS_022_FILE_OK`. |
| Patch | PASS | Unified diff plus Codex Add/Update/Delete patch forms are covered; the live repair modified `Program.cs`. |
| Build/Test | PASS | Live minimal .NET project build was executed by the external worker; repository Debug/Release gates also passed. |
| Self-repair | PASS | Live run observed the expected failed build, patched the source, rebuilt, and returned `SOL_DS_022_SELF_REPAIR_OK`. |
| ReadOnly | PASS | Mutating shell and patch operations are denied while harmless read-only shell commands execute. |
| Workspace Full Access | PASS | Project development commands execute; harmless access outside the project was denied and reported to the model. |
| Full Access | PASS | Explicit Full Access allowed a harmless read outside the workspace. No destructive system action was used. |
| Cancellation | PASS | Cancellation and timeout kill the active process; delayed marker tests prove no background write continued. |

## Regression and transport

- Sol+Luna regression: PASS. Native transport and `fork_turns` behavior were not refactored. The full 183-test gate includes native orchestration regressions.
- Sol+DeepSeek text regression: PASS. The live provider returned `SOL_DS_022_TEXT_OK` before tool tests.
- Provider configuration regression: PASS. Schema v4 migration and persistence tests passed; Profile UI completed full CRUD/restart/export smoke.

Transport changes were intentionally limited to the external-provider boundary:

- OpenAI-compatible request/response DTOs now encode and decode structured function/tool calls and tool-result messages.
- The external adapter now creates an `ExternalToolSession`, runs the bounded tool loop, aggregates usage/activity, and returns a compact result.
- The local host executes `shell` and `apply_patch`, enforces permission and write scopes, and reports structured stdout, stderr, exit code, denial, duration, and changed files.
- Provider base URL, API endpoint, credential reference/storage, and native Luna transport remain unchanged.

## Economic Policy and delegation

- Capability routing: PASS. Coding work requires Patch, Shell, BuildAndTest, MultiTurn, and SelfRepair; adapters missing them are rejected before spawn.
- External coding delegation: PASS at the orchestration contract and runtime levels. Pipeline tests select an external profile and preserve its immutable TaskPacket snapshot; the live DeepSeek run independently proved the selected runtime can execute the coding loop.
- Sol reimplementation: zero for the two delegated implementation packages. Managed worker tasks `20260810-CAS022-001-L1` (provider codec) and `20260810-CAS022-002-L1` (patch host) were adopted after bounded R2 review. Sol performed integration, follow-up compatibility work based on live evidence, UI/release acceptance, and canonical-workspace merge.
- Duplicate work: none detected. Economic outcome: likely saved work; worker token data was unavailable, so no numeric savings percentage is claimed.

## Live DeepSeek usage

Provider: DeepSeek  
Model: `deepseek-v4-flash`  
Completed run: 2026-08-10T05:29:26Z

| Stage | Turns | Tool calls | Failed | Denied | Total tokens |
| --- | ---: | ---: | ---: | ---: | ---: |
| Text | 1 | 0 | 0 | 0 | 121 |
| Read-only shell | 2 | 1 | 0 | 0 | 1,193 |
| File create | 2 | 1 | 0 | 0 | 1,552 |
| Self-repair | 5 | 4 | 1 | 0 | 7,266 |
| Workspace denial | 2 | 1 | 1 | 1 | 1,535 |
| Full Access | 2 | 1 | 0 | 0 | 1,463 |
| Total | 14 | 8 | 2 | 1 | 13,130 |

API cost is not reported because no pricing configuration was available; the runtime reports token usage and only computes cost when pricing is configured.

## Automated and UI verification

- `CodexAgentSwitch.Tests`: 164/164 passed in Debug and again inside Release packaging.
- `CodexAgentSwitch.Bootstrapper.Tests`: 19/19 passed in Debug and again inside Release packaging.
- x64 Debug and Release app builds: 0 warnings, 0 errors.
- Canonical merged workspace: 164/164 + 19/19 passed; x64 Debug build 0 warnings, 0 errors.
- Dark Profile UI smoke against the final package: 10/10 passed at DPI 119.
- Configured-provider screenshot: `E:\AISPace\TestSpace\cas-022-ui\profile-final-dark-configured\profile-external-permission.png`.
- Full clean-data UI report: `E:\AISPace\TestSpace\cas-022-ui\profile-final-dark-clean\profile-ui-smoke-dark.json`.

## Release packaging and C-drive audit

- Portable ZIP: SHA-256 `fddd34f3cd4d1257e7a9701c80da9a25b736f107ccfdced0d609d26ed4404cf1`.
- Setup bundle ZIP: SHA-256 `297c6aeac7d4b2d5ea0276cf5ce801eb4620d9c1cf02d8add72c923049510c71`.
- Compact runtime ZIP: SHA-256 `58f6a46eacf2eeb6cc14945c8ff6663413abfc3f8a92068c2a26ac77fac2dc8f`.
- C-drive audit: PASS. Portable, installed, and runtime-bootstrapper entry points started; monitored physical entries remained 2 before and after, with an empty `physicalChanges` list.

## Known limitations

- Workspace Full Access is a conservative lexical command/path gate implemented by the host; it is not an OS- or kernel-level sandbox. Child tools could contain behavior that cannot be proven from their command line alone.
- External providers must support OpenAI-compatible structured function calling. Text-only tool-call emulation is not implemented.
- Only `shell` and `apply_patch` are exposed in 0.2.2; broader Codex tool parity is intentionally deferred.
- API cost is observable only when provider pricing is configured; this acceptance run proves tokens, not billed currency.
- The release target is Windows x64. macOS and Linux packages and runtime behavior were not validated.
