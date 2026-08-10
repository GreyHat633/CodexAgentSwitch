# Codex Agent Switch 0.2.1 acceptance report

Date: 2026-08-10  
Host: Windows 10 22H2 x64  
Display: current long-term 125% scaling; reported window DPI 119/120  
Repository: `E:\AISPace\Codex Agent Switch Project`

## UI click-through gate

| Control | Action | Expected | Actual | Result |
| --- | --- | --- | --- | --- |
| 启动 Codex | Invoke the visible global action with one applied isolated project | Start or activate Codex Desktop and show feedback | `OpenAI.Codex_2p2nqsd0c76g0!App` activated; success bar named the project and path | PASS |
| 项目配置 | Invoke from Dashboard | Navigate to the native project adapter | Project configuration subtitle and controls became reachable | PASS |
| 保存 | Create a profile and save all edited values | Persist the profile | Profile survived restart and export | PASS |
| 取消 | Open a new profile editor and cancel | Close without persisting | Editor closed and no profile was created | PASS |
| Worker 推理强度 | Select `xhigh`, save, export | Persist independently of main-agent effort | `workerPolicy.reasoningEffort` exported as `xhigh` | PASS |
| Provider 配置 | Open editor, store an isolated placeholder credential, save | Persist without plaintext data leakage | Saved with visible success feedback; no network request was made | PASS |
| Provider 停用 | Enable and then disable the isolated provider | Persist disabled state and show feedback | `服务商已停用` was visible and survived restart | PASS |
| 测试当前 Worker | Explicitly click while the current profile is native | Produce a clear, recoverable failure without an API call | `当前外部 Worker 测试失败` explained that the profile did not enable an external Worker | PASS |
| 暂停 / 恢复 | Invoke both header actions | Scheduler state and button text change in both directions | `Agent Switch 已暂停` / `Agent Switch 已就绪` observed | PASS |
| 活动任务 | Invoke header action | Navigate to Scheduler-backed activity page | Activity subtitle and empty Scheduler state rendered | PASS |
| 关键导航 | Select Provider, Usage, History, Settings | Open each real page | Each page-specific subtitle was observed | PASS |

The Provider result bar was found inside the collapsed editor during this gate. That made card-level `停用` and `测试当前 Worker` feedback invisible. It was moved outside the editor and the affected clicks were repeated successfully. The transport and Provider HTTP implementation were not changed.

Evidence:

- `screenshots/dashboard-launch-success-125dpi.png`
- `screenshots/provider-worker-failed-125dpi.png`
- `screenshots/profile-editor-125dpi.png`
- `screenshots/profile-created-125dpi.png`
- `screenshots/usage-1366-effective-125dpi.png`
- `evidence/dashboard-launch-input.jsonl`
- `evidence/profile-ui-smoke-light.json`

The existing 0.2.0 1366 layout matrix remains valid regression evidence. New 0.2.1 captures were taken under the current 125% DPI environment. No Windows display setting or DPI-awareness override was changed.

## Automated regression

- Debug solution build: passed, 0 warnings, 0 errors.
- Release solution build: passed, 0 warnings, 0 errors.
- `CodexAgentSwitch.Bootstrapper.Tests`: 19/19 passed.
- `CodexAgentSwitch.Tests`: 128/128 passed.
- Total: 147/147 passed.

## Economic acceptance

The real 0.2.1 implementation was used instead of a separate Sol-only A/B rerun.

- Delegated packages: three sequential `cas_luna_worker` jobs, default active Worker count one.
- Adoption: two adopted, one partially adopted after the Worker explicitly left responsive XAML/tests incomplete.
- Sol duplication: none; Sol performed bounded review, integration, missing-gap completion, and UI/release acceptance.
- Directed follow-ups/escalations: five across the three jobs.
- Review depth: focused for normal UI/profile work; deep R2 for native JSONL semantics and integration correctness.
- Validation quality: 147/147 automated tests plus the click-through matrix above.

Read-only JSONL totals from the start of this 0.2.1 task (`2026-08-09T17:55:00Z` cutoff):

| Role | Input | Cached | Uncached | Output | Reasoning | Token events |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sol root | 35,583,211 | 35,068,032 | 515,179 | 100,438 | 28,550 | 277 |
| Luna workers combined | 5,649,342 | 5,441,024 | 208,318 | 25,015 | 3,109 | 119 |

The counts are session token-accounting totals, not a billing estimate. Cache reuse was high. This run does not establish a fixed savings percentage: delegation prevented duplicate core implementation, while the two R2 correction cycles and detailed UI acceptance increased review cost. The economic outcome is therefore recorded as **unable to determine**, with verified quality and clear work ownership.

## Release packaging

- Portable ZIP: SHA-256 `e9a8150b5092559f4e6ed124eade820f5d39554630ea240d6b87cf67cb4979b0`
- Setup bundle ZIP: SHA-256 `307c00dba479b032a0baf9984d278b53f21fdac52287e5c80a394f84f555b42f`
- Compact runtime ZIP: SHA-256 `ae501de22c28cff05ade4846fdc148f100677d2e6b112a0a98dab5ba2cf85282`
- C-drive physical-write audit: passed; portable app, installed app, and runtime bootstrapper all launched from E-drive staging with no monitored C-drive changes.
