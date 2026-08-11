# Codex Agent Switch 0.2.4 验收报告

日期：2026-08-12

源码工作区：`E:\AISPace\主模型项目区\state\worktrees\cas-024-phase2`

计划：`E:\AISPace\主模型项目区\plan\CodexAgentSwitch_0.2.4_Development_Plan.md`

## 结论

代码实现、定向测试、完整自动化回归、Release x64 构建、中文 UI 检查、打包、C 盘审计和 E 盘升级部署均通过。唯一一次自然主动分工验收完成了最小定位与合法 Ownership Decision，但 `delegate_worker` 因嵌套 fixture cwd 无法关联已应用的父项目 Profile 而拒绝创建 Job。

该缺陷已在 `d175c9e` 修复，并新增父项目解析、最具体项目优先和相似前缀拒绝测试；完整回归通过。由于计划明确禁止多轮自然实验，本轮没有第二次自然验收，因此总体状态为 **未达到 Plan PASS / 自然重测待授权**，而不是已通过。

## 功能验收

### Main Cost Guard

- 配置位置：`src/CodexAgentSwitch.Domain/Usage/MainCostGuardModels.cs` 的 `MainCostGuardOptions` 与 `NormalizedCostWeights`。
- 默认阈值：25、40、60 normalized credits；连续 `MAIN / INVESTIGATION_UNRESOLVED` 进入 40、60，之后钳制在 60。
- 其他合法 MAIN checkpoint 清零当前窗口；新 package 与 Worker 边界恢复初始阶段。
- 运行时按规范化 cwd + exact SessionId 隔离；只接受精确 Sol/Main usage 且 cwd 或 project 匹配的记录。
- Cached/Uncached/Output 独立计权；Reasoning 不重复收费。

### Ownership Gate 与 Hook

- Lease 持久化：SQLite `work_package_leases`，包含 package、cwd、owner、kind、declared scopes、状态与 cost window index。
- 无有效 MAIN Lease 的确定性 mutation：拒绝。
- WORKER Lease 下 Main 尝试同包写入：拒绝。
- MAIN Lease 仅允许 cwd 与声明 scope 内操作；只读允许，未知命令交回既有安全策略。
- Worker review complete、new package、cost checkpoint、package complete 会使旧 Lease 失效。
- 实际 Hook：`E:\AISPace\主模型项目区\.codex\hooks.json`，SHA-256 `B8ED5490DD984C73B0C1D5367119982F1F5DFD0117A1F89F3B367920DF3F2207`。
- matcher：`Bash|apply_patch|Edit|Write`；命令指向 `E:\AISPace\Codex Agent Switch\ToolHost\CodexAgentSwitch.ToolHost.exe --hook pre-tool-use ...`。
- MCP：`E:\AISPace\主模型项目区\.codex\config.toml`，`required = true`。
- Load/trust 边界：项目已处于 trusted 状态，唯一自然会话成功调用 MCP；该会话没有进入首次 mutation，所以没有把 PreToolUse 真实 mutation 触发写成 PASS。

### Context Economy

- Compact Result/Checkpoint：已实现有界、确定性 replay，并验证 provenance/input。
- Session Context Budget：按年龄、turn 和 normalized cost 输出 Continue/Compact/Rollover。
- App Server：使用真实 `thread/compact`、`thread/start` RPC；context compaction 通过官方 item 生命周期事件观察。
- Rollover：创建新 Thread 并回放 checkpoint；默认接口不伪造宿主能力。
- Worker resume：使用调度器终态事件契约，不引入 Main 模型轮询。

### OpenCode Zen 与中文化

- Onboarding、Providers、Profiles 使用统一 Provider Registry。
- 动态 catalog 可包含任意 raw model ID；刷新失败不丢已保存选择。
- Zen auth 只探测 OpenCode 登录，不读取 Credential Store 中的 API Key ref。
- 用户可见的 Zen 状态、错误、刷新/保存/测试提示及诊断页经济/所有权字段已中文化。
- 保留 `OpenCode Zen`、`CLI`、Provider/model ID、命令、PATH、协议枚举及外部 raw stderr 原文。
- 本机缺少 Zen 登录，真实模型调用未运行。

## 自动化与构建

| 项目 | 结果 |
| --- | --- |
| `SchedulerIpcServerTests` 定向修复 | 8/8 通过 |
| `CodexAgentSwitch.Tests` Release | 241/241 通过 |
| `CodexAgentSwitch.Bootstrapper.Tests` Release | 19/19 通过 |
| WinUI App Release x64 | 通过，0 警告、0 错误 |
| `git diff --check` | 通过，仅 Git 行尾提示 |

中文 UI 视觉证据：

- `E:\AISPace\主模型项目区\state\acceptance\cas024-ui\diagnostics-localized-bottom.png`：经济/所有权卡片完整可见，无裁切重叠；空值显示“无”，状态中文化。
- `E:\AISPace\主模型项目区\state\acceptance\cas024-ui\providers-localized.png`：OpenCode Zen 卡片与中文状态可见，无裁切重叠。
- UI Automation 元素记录同目录下 `.elements.txt` 文件；Zen 展开控件包含“刷新模型”“保存选择”。

## 发布包

目录：`E:\AISPace\主模型项目区\state\worktrees\cas-024-phase2\artifacts\release\0.2.4`

| 文件 | 大小 | SHA-256 |
| --- | ---: | --- |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | 217,498,827 | `48497c0ed594e59aa3254fe39f0482bda8d70bc446c44ea21978c15e355f207d` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | 327,482,797 | `1f887d6c72f377e0d76e321c5111bf67f989aa52531ced4f86f86d5705daaef0` |
| `CodexAgentSwitch-win10-x64.zip` | 28,936,225 | `f371accd9c984b4bc3a3f3874f6ce863f1a2fcd3385a398832f8ec348090cfc5` |
| `CodexAgentSwitch-win10-x64.zip.sha256` | 98 | `49dc80a44176683512a52240db0ee6deb056090bf08c4f483c1fe747e2156822` |

清单与现场重算一致；App/ToolHost FileVersion 均为 `0.2.4.0`，ProductVersion 为 `0.2.4+d175c9ee7a03cfee19ebb18989011a13ccd47207`。

## C 盘与部署

- 审计结果：`E:\AISPace\主模型项目区\state\worktrees\cas-024-phase2\.tmp\c-drive-release-audit-20260812-015536\result.json`。
- 结论：PASSED；受监控物理条目 2 → 2，`physicalChanges` 为空；三个入口均启动。
- 部署目录：`E:\AISPace\Codex Agent Switch`。
- 安装记录时间：2026-08-12 01:56:28 +08:00。
- 记录 payload SHA-256 与现场值均为 `f371accd9c984b4bc3a3f3874f6ce863f1a2fcd3385a398832f8ec348090cfc5`。
- 旧版备份：`E:\AISPace\Codex Agent Switch.backup-20260812-015627`。
- 既有数据库保留：`E:\AISPace\Codex Agent Switch\data\codex-agent-switch.db`，验收时 196,608 字节。
- 新 App 已从安装目录启动。

## 唯一自然验收

- Thread：`019ff1eb-aa46-76f1-9682-c720b2f17baa`。
- Fixture：`E:\AISPace\主模型项目区\state\acceptance\cas024-natural`，停止后 Git HEAD `56f0ae1` 且状态干净。
- Prompt 未包含计划禁止的 Agent/Worker/subagent/子代理/“请分工”等提示词。
- Main 完成最小定位，记录合法 repartition，并准备单一有界实现包。
- `delegate_worker` 未创建 Job：当时运行版本只能按 cwd 完全相等查找应用了 Profile 的项目，fixture 是已应用父项目下的嵌套目录。
- 缺陷修复：`d175c9e`；新测试覆盖父目录命中、最具体注册项目优先和相似前缀拒绝。
- 按“一次自然验收”上限停止，没有再次派发。

## 未关闭项

1. 获得明确授权后，用新 Main Session 做一次替代性自然验收，并在第一份真实 Worker Job 创建后立即停止；这是 0.2.4 总体 PASS 的必要条件。
2. 在真实 mutation 上观测一次 PreToolUse Hook 命中，补齐 live Hook 执行证据。
3. 用户完成 `opencode auth login` 并选定模型后，可补做真实 Zen 模型调用；这不影响 registry/UI 自动化结论，但当前不能标记 live provider PASS。

