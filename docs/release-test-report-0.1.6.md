# Codex Agent Switch 0.1.6 发布测试报告

日期：2026-08-04
主验收系统：Windows 10 22H2 x64（10.0.19045）
发布目录：`artifacts/release/0.1.6`

## P0 收敛结果

- 生产界面只保留“原生 Codex 模式”和“CodexAgentSwitch 模式”；没有 MCP 模式入口或模拟运行任务。
- 原生模式明确仅“配置并启动原生 Codex”，不声称接管独立 Codex Desktop 对话、精确主线程 Usage 或阻止原生会话重复劳动。
- 受控模式持久化项目、工作目录、对话、主 Thread、每轮 Profile 快照、真实消息、Worker、Usage 和历史；重启后可继续同一主 Thread。
- External Provider 由任务快照解析，真实 DeepSeek V4 Flash 请求不会解析为 Luna；失败严格按 Profile 回退或停止。
- Sol/Terra/Luna 被定义为角色。App Server 真实目录在本机返回 `gpt-5.6-terra`（默认）和 `gpt-5.6-luna`；未提供 `gpt-5.6-sol` 时，方案编辑器不显示 Sol，旧方案会得到明确警告，且保存、启动、执行都会拒绝该无效模型。不会后台映射为 Terra 或其他模型。

## 批准操作模式

| 方案选项 | Codex 策略 | 沙箱 | 用户含义 |
|---|---|---|---|
| 安全模式 | `untrusted` | `read-only` | 非可信命令和越界写入均须批准 |
| 自动模式 | `on-request` | `workspace-write` | 正常项目工作区读写，风险操作批准 |
| 完全自动 | `never` | `danger-full-access` | 不请求批准；仅限完全可信任务 |

外部 Worker 始终是 Provider HTTP 文本调用，完全自动不会把本地 shell/文件权限授予 Provider。

## 已完成验证

- Release 构建：0 警告、0 错误。
- 核心单元测试：85/85 通过；安装器测试：19/19 通过。
- 真实 Codex App Server 验证：`model/list` 与独立原生 Worker Thread/Turn 通过，实际目录为 `gpt-5.6-terra`（默认）、`gpt-5.6-luna`、`gpt-5.5`、`gpt-5.4-mini`。
- 真实 DeepSeek 受控 E2E：创建项目和对话；主代理委派；对 `https://api.deepseek.com` 的 `deepseek-v4-flash` 请求成功；Provider/模型/Usage 归档；Luna Worker 调用数为 0；同一主 Thread 成功继续第二轮；重新打开 SQLite 仓储可恢复项目、对话、Thread 与两轮记录。
- 一次 DeepSeek HTTP 503 被按当前 `StopDelegation` Profile 正确停止，没有静默切换 Luna；随后的独立真实请求成功。
- 0.1.5 → 0.1.6 隔离 Setup 升级：0.1.5 安装、迁移既有 `data/codex-agent-switch.db`、0.1.6 覆盖升级、隐藏窗口启动均通过。安装器生成可恢复备份；Profile 的 ID/名称和 DeepSeek 凭据引用经实际 SQLite/Windows Credential Manager 集成测试保持一致。数据库字节哈希在新版首次启动后会因新增表和迁移时间戳变化，故以语义完整性而非字节哈希作为验收依据。
- C 盘只读审计：以 E 盘 `TEMP` 和 bundle 提取目录运行，监控项 182 → 182、物理变化 0；便携版、安装版和 Runtime Bootstrapper 均完成实际隐藏启动。最终发布物原始结果：`.tmp/c-drive-release-audit-20260804-104956/result.json`；稳定摘要：`docs/acceptance/c-drive-release-audit-0.1.6.json`。

## 发布文件

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `CodexAgentSwitch-win10-x64.zip` | 94,334,768 | `a3f5129bf56e38bd58febc776c4978f1782ad2f5e92204799ec15c644e47b2ac` |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | 237,664,379 | `71451ce7e238839295fa5046fab9fe0f01ee16d6fd256d9763df5aa32ecec625` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | 413,292,194 | `86044968e30e1981dd4625b9431b974e73864b98d76e6830262f5561cc7de887` |

当前 `release-manifest.json` 已逐项验证大小与 SHA-256；便携 App、Setup 和 Runtime Bootstrapper 的文件版本均为 `0.1.6.0`，产品版本为 `0.1.6+992f55ae4cdbbc32d538317b9870de2d3eaee9ec`，Runtime 安装器 Authenticode 签名为 Microsoft 有效签名。
