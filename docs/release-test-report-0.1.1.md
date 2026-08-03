# Codex Agent Switch 0.1.1 release-blocker test report

## Scope and primary environment

- Release: `0.1.1`
- Primary OS: Windows 10 Home China 22H2 x64, build 19045
- Physical display: 1920×1080, 119 DPI (approximately 125%)
- UI stack: WinUI 3 / .NET 8 / Windows App SDK 1.8.10
- Theme/material policy: solid light/dark/high-contrast resources only; no Mica, acrylic, Windows 11 title-bar extension, Snap Layout, or system-material dependency
- Font policy: `Segoe UI Variable Text, Segoe UI` and `Segoe UI Variable Display, Segoe UI`

The host has one physical 119-DPI display. Real 100% and 150% display-scale runs were not available without changing the user's system-wide display configuration. The PerMonitorV2 manifest, rasterization-scale-aware window sizing, and smoke script are present, but those two physical scale points remain an explicit release-coverage gap rather than a claimed pass.

## Release-blocker cause and fix

### Before

The source and installed 0.1.0 contained two overlapping causes:

1. The common `NavigationView` used `PaneDisplayMode="Auto"`, while the window was resized with raw physical pixels. At 119 DPI the effective XAML size could cross a `NavigationView` breakpoint. Input traces showed cancellation/capture-loss bubbling through `SplitView:RootSplitView` and `Grid:ContentGrid`.
2. Startup selected the first navigation item and then explicitly navigated to the same page. That created duplicate/stale page elements in the automation tree.
3. A large set of visually enabled content buttons had no `Click` or `Command` at all. Examples included Dashboard “更改方案”, task controls, history export, settings backup/restore, onboarding navigation, and several profile buttons. The performance profile button was incorrectly bound to single-agent mode.

`VisualTreeHelper.FindElementsInHostCoordinates` and `OriginalSource` did not identify a custom transparent overlay. At the visible “启动 Codex” coordinate the top hit chain was `TextBlock -> ContentPresenter -> Button:StartCodexButton`. Therefore the requested “specific obscuring element” is: **no project-owned obscuring element existed**. The common input disruptor was the internal `NavigationView` `SplitView:RootSplitView` mode/capture path; the remainder of the apparent click failure was caused by inert placeholder buttons.

The “边界检查。” text comes from `DashboardPage`'s `PageHeader.Subtitle`; it is not an overlay.

### After

- The root `NavigationView` now uses an always-inline left pane (`PaneDisplayMode="Left"`, `IsPaneOpen="True"`) and the shared `Frame` remains hit-test visible.
- Window dimensions are interpreted as DIPs, multiplied by `XamlRoot.RasterizationScale`, and clamped to the active display work area.
- Duplicate initial navigation was removed.
- A single MainWindow content-action router wires real page buttons after the navigated page is loaded. Routed pointer traces record `pointer-pressed`, `button-click`, `action-completed`, or `action-failed` without logging input content.
- Previously inert main actions now produce navigation, persisted profile/provider state, task-state changes, or real export/backup files. Dedicated existing actions such as Codex start/stop and diagnostics remain intact.
- Physical mouse, Tab focus, Enter, Space, and click-after-scroll are covered by `scripts/ui-smoke.ps1` against real WinUI controls rather than ViewModel-only commands.

## DeepSeek V4 catalog and migration

- Base URL: `https://api.deepseek.com`
- Default: `deepseek-v4-flash` / “DeepSeek V4 Flash 0731”
  - Chat Completions: supported
  - Responses: supported
  - current Codex Worker: supported
- Secondary: `deepseek-v4-pro` / “DeepSeek V4 Pro”
  - Chat Completions: supported
  - Responses/current Codex Worker: disabled
  - UI/capability message: “不支持当前Worker协议”
- Fallback catalog contains only the two V4 IDs.
- `deepseek-chat` and `deepseek-reasoner` remain only in migration code/tests, not in presets, UI defaults, or fallback lists.
- Both legacy IDs migrate to `deepseek-v4-flash`. The profile's existing reasoning-effort value is preserved, so a reasoner profile retains its thinking-mode intent.
- Connection tests send the currently selected model and do not replace it with the first discovered model or a response alias.

## Provider lifecycle acceptance

The real Provider page was driven through WinUI controls with an isolated Windows Credential Manager prefix:

| Operation | Result |
| --- | --- |
| Enter API Key and save | Passed; database stores only credential reference |
| Select model | Passed; Flash default and Pro warning verified |
| Replace Key | Passed |
| Enable | Passed |
| Disable | Passed |
| Restart and restore configuration | Passed; disabled state, credential reference, and Flash selection restored |
| Test connection | Passed as a real request; intentionally invalid test credential produced a visible recoverable failure |
| Delete Provider | Passed; real WinUI Click fired, repository view changed to unconfigured, test credential removed |
| Secret redaction | Passed; neither test secret appears in database, logs, traces, or exports |

Evidence: `state/acceptance/cas-011-provider-lifecycle/provider-ui-smoke.json`.

## Per-page real-control smoke

| Page | Main action and state assertion | Installed light | Portable dark |
| --- | --- | --- | --- |
| 首页 | 更改方案 -> ProfilesPage visible | Passed (physical mouse) | Passed (Enter) |
| 配置方案 | 新建方案 -> persisted current profile/title changed | Passed | Passed |
| Provider | 添加 Provider -> editor/API Key control reachable | Passed | Passed |
| 运行任务 | 继续等待 after scroll -> same-Thread state displayed | Passed | Passed |
| 用量与预算 | real ComboBox accepted keyboard selection | Passed | Passed |
| 历史记录 | 导出报告 -> Markdown file created | Passed | Passed |
| 设置 | 备份配置 -> ZIP created | Passed | Passed |
| 诊断 | 导出脱敏日志 -> ZIP created | Passed | Passed |
| 首次启动向导 | 下一步 -> progress advanced | Passed (Space) | Passed (Space) |
| 公共焦点链 | Tab retained focus inside app | Passed | Passed |

Separate light-theme smoke also recorded physical mouse click-after-scroll. Evidence:

- `state/acceptance/cas-011-ui-light/ui-smoke-light.json`
- `state/acceptance/cas-011-upgrade-final/installed-ui-smoke.json`
- `state/acceptance/cas-011-portable-final/portable-ui-smoke.json`

## Build, tests, upgrade, and runtime

- Release solution build: passed, 0 warnings / 0 errors.
- Core tests: 51/51 passed.
- Bootstrapper/runtime tests: 18/18 passed.
- 0.1.0 -> 0.1.1 real Setup upgrade on Win10 build 19045:
  - old version: `0.1.0`
  - new version: `0.1.1`
  - database SHA-256 identical before/after
  - profile marker preserved
  - isolated Windows credential preserved
  - old executable retained in timestamped backup
  - installed payload hash equals release payload hash
- RuntimeSupport is included in the Setup Bundle. The independent WinForms bootstrapper checks Win10 build/x64 and complete Windows App Runtime 1.8 registration before launch, and starts the bundled installer only after explicit confirmation.
- Bundled Runtime installer Authenticode: `Valid`, signer `Microsoft Corporation`.

Upgrade evidence: `state/acceptance/cas-011-upgrade-final/upgrade-report.json`.

## Release artifacts

| File | SHA-256 |
| --- | --- |
| `CodexAgentSwitch-win10-x64.zip` | `57b94902ba7e6bfe019a2b43071a4fc500abf3aca5d07f7a28cb2333cc971053` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | `5ab7f7b5d07a24810f567fa0b053db190978f74f76acc212c5e3b00e1f6c8e50` |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | `966cd7cd6ffe610dca368f0e4ddb5081a2c1114dbc5851338edf56b434d3b3c8` |
| Runtime installer | `b8cda840267ab72797f654f801f9a064ab6d9e508cedee3df79f772f104db6d6` |

The portable and installed executables both report product version `0.1.1`. The 0.1.0 artifacts were not overwritten.
