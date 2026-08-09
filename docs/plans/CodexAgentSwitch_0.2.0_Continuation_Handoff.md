# Codex Agent Switch 0.2.0 — 新会话续开发 Handoff

> 用途：在全新的 Codex Desktop 对话中继续 0.2.0 开发，避免重复 Phase 0、重复扫描和重复实现。
>
> 主 Plan 仍以 `CodexAgentSwitch_0.2.0_UI_UX_Plan.md` 为唯一完整需求源；本文件只描述“当前已经做到哪里，以及新会话从哪里继续”。

## 1. 先决条件

开始前确认当前项目已经应用 **SOL + LUNA** 方案，并在应用之后新建本对话。

本对话应使用当前项目已经部署的 Agent Switch 路由规则。需要委派 Native Worker 时，按项目规则调用 `cas_luna_worker`，不要使用旧的 `luna_orchestrator` 方案。

## 2. 不要重新开始 0.2.0

上一对话已经完成了以下工作，不要重新做：

- 已完整读取 0.2.0 UI/UX Plan。
- 已读取经济编排 Skill 及其 references。
- Phase 0 只读定位已经完成。
- 已确认主要真实状态源：
  - Shell / 首页：`IWorkerScheduler`
  - 项目状态：`ProjectService + NativeCodexAppliedSnapshot`
  - 任务历史：持久化 Scheduler
  - Usage：`IUsageLedgerRepository`
  - Provider：Provider 配置、凭据、真实任务结果的联合投影
  - 托盘：已有暂停 / 退出能力
- 已将验证过的 0.1.13 当前内容固定成本地 baseline commit。
- 已创建独立 0.2.0 工作树：
  - `E:\AISPace\主模型项目区\state\worktrees\cas-020-sol`
  - branch：`feat/0.2.0-ui`
- 已开始 Phase 1：共享 UI 状态投影。
- 已新增：
  - `AgentSwitchUiState.cs`
  - `MockAgentSwitchUiStateSource.cs`
  - `UiPresentation.cs`
- 已修改：
  - `App.xaml.cs`
- 已执行过 Application Release build。
- 当前设计原则：真实 Release 状态源只读投影；Debug/测试使用隔离 Mock，不允许 Mock 污染 Release。

## 3. 新会话第一步

不要重新跑 Phase 0。

只做一次最小恢复检查：

1. 打开工作树：
   `E:\AISPace\主模型项目区\state\worktrees\cas-020-sol`
2. 读取主 Plan。
3. `git status --short`
4. `git diff --stat`
5. 只查看上一对话已经修改/新增的 Phase 1 文件，确认当前实现状态。
6. 如果这些改动完整存在，直接从 **Phase 1 收尾 / Phase 2 Shell + 首页** 继续。

不要重新扫描所有 Views、ViewModels、Scheduler、Provider、Usage。
只有在当前实现遇到具体缺口时，才定向读取对应文件。

## 4. Sol + Luna 经济执行规则

这是本次新会话的重要测试目标之一：验证 SOL + LUNA 是否能降低 Sol 的工作量。

Sol 负责：
- 恢复当前进度。
- 确认接口和文件边界。
- 设计跨页面状态模型。
- 审查 Luna 结果。
- 集成、构建、测试和最终 UI 验收。

Luna 只处理边界已经明确的实现包。

优先允许以下 bounded task：

### Luna A — Shell / 首页 / 活动状态
前提：Sol 已确认 Phase 1 状态投影接口稳定。

负责：
- Shell 全局 Agent Switch 状态。
- 首页项目状态卡。
- Ready / Working / Paused / Faulted 展示。
- 活动任务摘要。
- 不修改 Worker 执行链。

### Luna B — 项目配置 / 方案 UI
负责：
- 项目配置页只保留“应用到项目”职责。
- 应用成功后返回首页。
- 方案页移除无意义灰色竖条。
- 不修改 Profile / Apply 的底层语义。

### Luna C — Usage / Provider 响应式 UI
负责：
- 用量页真实数据/不可取得状态重排。
- Provider 卡片重排。
- 1366×768、1600×900、1920×1080 响应式。
- 不修改 Provider API / Worker Adapter 执行逻辑。

一次最多委派 1 个当前可以独立完成的包。
不要为了“用 Luna”而并行创建没有清晰边界的任务。

## 5. 禁止事项

- 不得 reset / 丢弃上一对话已经完成的 Phase 1 改动。
- 不得重新创建 0.1.13 baseline。
- 不得重新创建 `cas-020-sol` worktree。
- 不得重新做全仓 Phase 0。
- 不得重新设计 Sol + Luna / Sol + DeepSeek 执行链。
- 不得使用旧 `luna_orchestrator`。
- 不得真实调用 DeepSeek API。
- 除实际开发委派外，不要用随机字符串反复测试 Luna。
- 不得让 Sol 重做 Luna 已完成的实现；Sol 只做 bounded review / integration。
- 不得为了响应式/UI 顺手重构无关业务代码。

## 6. 当前建议继续顺序

从这里继续：

```text
Phase 1  收尾共享 UI 状态投影
↓
Phase 2  Shell + 首页
↓
Phase 3  项目配置流程
↓
Phase 4  活动任务
↓
Phase 5  方案页
↓
Phase 6  用量页
↓
Phase 7  服务商页
↓
Phase 8  托盘 / 暂停 / 退出
↓
Phase 9  响应式统一
↓
Phase 10 自动回归
↓
Phase 11 Mock UI 状态视觉验收
↓
Phase 12 打包 0.2.0
```

## 7. 本轮额度记录

为了评估 SOL + LUNA 是否真的省额度，在本次新对话结束时额外报告：

- Sol 本轮能取得的 token / quota 信息。
- Luna 创建次数。
- 每个 Luna Task 的目的和边界。
- Luna 是否成功返回。
- Sol 是否重新实现过 Luna 已做的工作（期望：否）。
- 本轮真实 DeepSeek/API 调用次数（必须为 0）。
- 与纯 Sol 的上一对话相比，能否从可取得证据判断 Sol 工作量有所下降。

若无法精确取得 token，不要猜数值，报告“不可取得”，但仍记录 Luna 分工和是否发生重复工作。

## 8. 完整需求仍以主 Plan 为准

本 Handoff 不替代：

`CodexAgentSwitch_0.2.0_UI_UX_Plan.md`

遇到 UI、交互、验收标准冲突时，以主 Plan 为准。

本文件只用于避免新对话重复已经完成的阶段。
