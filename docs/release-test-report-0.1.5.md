# Codex Agent Switch 0.1.5 发布测试报告

日期：2026-08-04

主验收系统：Windows 10 22H2 x64（10.0.19045）

发布目录：`artifacts/release/0.1.5`

## P0 根因

0.1.4 的“运行任务”页面完全由 XAML 静态示例和本地控件状态组成，没有绑定任务服务；“启动 Codex”只创建了无界面的 `codex app-server --stdio` 子进程。应用没有主 Thread/Turn 入口、流式通知绑定、Worker 调度、任务持久化或真实历史，因此新建 Codex Desktop 对话也不可能进入该页面。

## 0.1.5 实现

- 新增 `ControlledTaskSession`、真实 Turn、消息和 Worker 运行记录模型。
- 新增 `CodexMainAgentSession`：真实调用 `thread/start`、`thread/resume`、`turn/start`、`thread/read`、`turn/interrupt`，接收 `item/agentMessage/delta`、审批请求和 `turn/completed`。
- 新增 `ControlledTaskService`：读取当前 Profile，按路由模式调用 Native 或 External Worker，把真实 Worker 结果交给 Sol 最终审查，并执行 Profile 回退策略。
- 新增 SQLite `controlled_tasks` 表和仓储；迁移为 `CREATE TABLE IF NOT EXISTS`，不改写既有 profiles、providers、task_groups 或 usage_snapshots。
- Worker 与主代理 Usage 均在现有 Usage Ledger 中归档；不存在的令牌字段继续标记为不可取得，不伪造。
- “运行任务”页删除全部静态任务，提供真实任务输入、工作目录、Thread 继续、取消和审批入口，只显示持久化任务。
- “历史记录”和首页最近任务删除静态示例，读取真实任务、消息、Worker 与经济报告。
- 首页按钮改为“连接任务服务”，明确不会打开另一个 Codex Desktop 窗口；运行任务时也会自动连接 App Server。

## 自动测试

- Debug solution build：0 警告、0 错误。
- Release solution build：0 警告、0 错误。
- Core tests：66/66 通过。
- Bootstrapper tests：19/19 通过。
- SQLite controlled task tests：3/3 通过。

## 真实端到端测试

`ControlledTaskEndToEndTests.User_input_runs_worker_then_sol_streams_persists_usage_and_resumes_thread` 在真实 Codex App Server 上通过：

1. 从用户输入创建持久化任务和主 Thread。
2. 创建真实 Native Luna Worker Thread/Turn并取得结果。
3. 把 Worker 结果交给 gpt-5.6-sol 主 Turn做最终回答。
4. 实际观察 `OutputDelta`、`TurnCompleted` 以及 WorkerRunning → MainAgentRunning → Completed。
5. 归档 Worker 与主代理两类 Usage。
6. 对同一主 Thread 提交第二个 Turn，恢复后返回预期标记。
7. 重新创建 SQLite 仓储后仍能读取两个 Turn、消息、Worker 和同一 Thread ID。

加强后的真实 E2E 再次通过，耗时约 18 秒。

## WinUI 与发布形态验收

- Debug 浅色物理鼠标路径：10/10 通过。
- Debug 深色键盘路径：10/10 通过。
- 从 0.1.4 隔离安装升级到 0.1.5 后，安装文件为 0.1.5.0，既有 `data/codex-agent-switch.db` 被迁移到新安装并可正常打开。
- 0.1.5 安装版浅色物理鼠标路径：10/10 通过；真实 Sol/Worker 结果出现在 WinUI Automation 控件树，重启后历史可导出。
- 0.1.5 便携版深色键盘路径：10/10 通过；真实任务、Usage 和历史均可用。
- 上述 UI 测试运行于 Windows 10 22H2 x64、119 DPI（约 125% 缩放）。

## C 盘物理写入审计

便携版、安装版和 Runtime Bootstrapper 在正常 C 盘 TEMP 环境下启动前后，对 C 盘 `.nuget`、`.dotnet`、`.net`、VBCSCompiler 和 NuGetScratch 物理项做快照：9 → 9，变化 0，`C_DRIVE_RELEASE_AUDIT=PASSED`。

## 发布文件

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `CodexAgentSwitch-win10-x64.zip` | 94,262,349 | `9d0ba9d63b70a6ef0743bd0a2d0384712de73fb90a7202b5553bed30c23b93d6` |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | 237,592,037 | `7c711db7a250ea7761709d16b042e3854bc3a178e79e5c7a52da535e13eebf12` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | 413,147,501 | `601b6bd0efd10b4f0d9c79c8756b97badf130d28749bad7ec9ed58afefcadcda` |

`release-manifest.json` 中全部文件的大小和 SHA-256 已重新计算并匹配。Portable App、Setup 和 Runtime Bootstrapper 的文件版本均为 0.1.5.0，产品版本关联提交 `d2cdafa6a0ba1bb7497d6b078c888af5a5d4b53c`。捆绑 Windows App Runtime 安装器的 Microsoft Authenticode 签名有效。
