# Phase 0 审计与技术验证

日期：2026-08-03  
目标：在不修改 `E:/AISPace/主模型项目区` 与 `E:/AISPace/子代理工作区` 的前提下，确认复用边界、当前协议证据和 WinUI 3 可行性。

## 结论

- 新项目已建立在独立 Git 仓库 `E:/AISPace/Codex Agent Switch`。
- 技术栈继续采用 .NET 8 + WinUI 3；最小 x64 应用已实际构建并启动。
- 现有 Sol-Luna 实现可作为兼容后端，但不会复制其 Luna 命名到通用领域接口。
- 当前 App Server 运行链路可用：本次只读 Worker 经 App Server 成功创建 Thread、完成 Turn 并返回结构化 Result。
- Store 包目录中的 `codex.exe` 受 WindowsApps ACL 限制，普通 PowerShell 直接执行返回 Access denied。因此 schema/help 命令必须由可执行的 Codex 解析器路径或 App Server 客户端能力发现完成。
- 当前协议方法名以运行时能力发现和生成 Schema 为准；静态字符串扫描只作为候选清单。

## 本机环境

| 项目 | 证据 | 状态 |
|---|---|---|
| .NET SDK | 8.0.204 | 通过 |
| 主验收系统 | Windows 10 家庭中文版 22H2，10.0.19045 x64 | 通过 |
| Windows SDK | 10.0.22621.0（TargetPlatformMinVersion 10.0.19041.0） | 通过 |
| Visual Studio | 2022 Community 17.9.6，位于 `E:/AVG/Visual Studio` | 通过 |
| WinUI 模板 | `dotnet new list` 未发现 WinUI | 不阻塞 |
| Windows App SDK | Microsoft.WindowsAppSDK 1.8.260710003 | 通过 |
| WinUI 构建 | `dotnet build ... -p:Platform=x64`，0 警告、0 错误 | 通过 |
| WinUI 启动 | 1280×800 窗口、NavigationView 与页面 UIA 树可读取 | 通过 |

构建、NuGet、HTTP 缓存与 TEMP 均定向到本仓库的 `.dotnet`、`.nuget` 与 `.tmp`，不把新工具状态写入 C 盘用户目录。

Windows 10 主机已安装多代 Windows App Runtime，其中包含 1.8 x64；但当前 SDK 的 framework-dependent 启动仍返回 `0x80670016`，说明不能只按大版本判断。开发默认使用 self-contained 输出；发布同时提供独立 Bootstrapper，对精确 Runtime、架构与最低 OS 版本做检测，并在明确确认后运行捆绑的官方安装器。本次未在 C 盘安装或升级 Runtime。

## 可复用模块

| 既有模块 | 当前职责 | 新项目边界 |
|---|---|---|
| `tools/luna-orchestrator/src/app-server-client.ts` | JSONL stdio、initialize/initialized、请求与通知、审批拒绝、诊断 | 迁移为 `ICodexAppServerClient` 基础设施实现 |
| `tools/luna-orchestrator/src/thread-manager.ts` | thread/start、resume、read、delete；turn/start、interrupt | 由 `NativeCodexWorkerAdapter` 包装 |
| `tools/luna-orchestrator/src/job-manager.ts` | Job 登记、恢复、等待、期限、结果与清理门 | 迁移为通用 `WorkerOrchestrator`，不暴露 Luna 名称 |
| `tools/luna-orchestrator/src/policy-loader.ts` | Worker Policy 加载与哈希校验 | 保留为兼容适配器策略输入 |
| `tools/luna-orchestrator/src/schema-validator.ts` | Task/Result Schema 校验 | 作为协议边界验证器 |
| `tools/luna-orchestrator/src/path-policy.ts` | allowlist、traversal 与 scope 冲突 | 抽象为 `IScopeGuard` |
| `tools/luna-orchestrator/src/worktree-manager.ts` | owned worktree 创建与幂等清理 | 由原生 Worker 适配器复用 |
| `E:/AISPace/子代理工作区/worker-policy.json` | Policy 2.0.0、Skill/Schema 哈希、禁止递归代理 | 兼容模式输入，不由新应用覆盖 |

通用接口必须覆盖：能力发现、Spawn、ReadStatus、Wait、Steer、Cancel、Delete；结果统一映射为 provider-neutral 的 Job/Result/Error。

## 当前协议证据

已从当前安装包静态发现以下候选方法/事件：

- `thread/start`, `thread/read`, `thread/resume`, `thread/delete`
- `turn/start`, `turn/interrupt`
- `turn/started`, `turn/completed`, `turn/diff/updated`
- `item/started`, `item/completed`
- `item/permissions/requestApproval`, `item/tool/requestUserInput`
- `thread/status/changed`

这些名称不是参数 Schema 的证明。应用启动时必须：

1. 检测可执行的 Codex 命令与版本。
2. 尝试生成当前版本 Schema，并缓存版本与哈希。
3. 解析能力与模型/推理强度，而不是硬编码枚举。
4. Schema 不兼容时进入可读诊断状态，不创建 Thread。

## 验证记录

- Portable Worker Policy：`POLICY_VALIDATION=PASSED`。
- Task/Result/Error Schema：`SCHEMA_VALIDATION=PASSED`。
- 既有 orchestrator：17/17 单元测试通过，TypeScript 构建通过。
- App Server：能力检查为 ready；真实 Luna Thread/Turn 完成并返回 Schema-valid Result。
- 新应用：WinUI 3 x64 Debug 构建 0 警告、0 错误；窗口启动与 UIA 可访问树通过。
- Store 目录 `codex.exe --version` / `app-server --help`：Access denied；未把此项描述为已通过。

## 风险与处理

1. Store ACL 阻止直接执行包内二进制：启动器支持显式路径、PATH shim 与可注入命令；诊断页显示实际失败原因。
2. App Server 协议随版本变化：生成 Schema + 最低兼容检查 + 原始 JSON 扩展字段保留。
3. 旧实现与 Luna 强耦合：只包装，不直接复制命名；在统一接口回归等价后再移除兼容层。
4. Usage 字段可能不可用：数据库保存 `unavailable`，不写估算冒充事实。
5. Windows 10 视觉兼容：应用不依赖 Mica、Win11 专属标题栏、Snap Layout 或系统材质；浅色、深色和高对比度均使用显式纯色资源，字体按 `Segoe UI Variable` 到 `Segoe UI` 回退。
