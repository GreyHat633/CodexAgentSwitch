# Codex Agent Switch 0.2.0 — UI / UX 重构开发 Plan

> 版本定位：**0.2.0 = Usability / UI & Interaction Overhaul**
>
> 目标：在不破坏 0.1.13 已验证运行链路的前提下，把 Codex Agent Switch 从“技术验证可用”提升到“用户可以长期日常使用”。

---

## 0. 稳定基线与硬约束

### 已验证基线

以下能力在 0.1.13 已有真实运行证据，0.2.0 视为稳定基线，不得为了 UI 重构而重新设计：

**SOL + LUNA**
- Native Custom Worker 可用。
- `cas_luna_worker` 可实际启动。
- `fork_turns="none"` 已通过真实 runtime 验证。
- 实际 Worker 模型为 `gpt-5.6-luna`，reasoning effort 为 `medium`。
- Worker Result 可正常返回给 Sol。

**SOL + DeepSeek**
- `Delegate worker` → Agent Switch Scheduler → External Worker → DeepSeek API 链路已通过真实 runtime 验证。
- Applied Worker 能由 Agent Switch 正确解析。
- `deepseek-v4-flash` 可正常返回 ResultPacket。
- 不再使用旧的 Native `cas_external_worker` / encrypted collaboration payload 路径。

### 硬约束

1. 不重构 Sol + Luna 和 Sol + DeepSeek 的核心执行链。
2. 不修改 Codex Runtime。
3. 不重新设计 Native / External Worker 通信协议。
4. UI 如需状态，只允许接入现有真实状态源，或新增只读投影 / ViewModel 聚合层。
5. 不允许根据“用户点击了按钮”伪造 `Running / Completed / Failed`。
6. 开发、自动测试和 UI 验收阶段真实 AI/API 调用次数必须为 0。
7. 不要为了 UI 清理顺手重构无关模块。
8. 不要反复扫描全仓库；先用 UI 文案、页面类名、ViewModel 名、已有 Scheduler/Task 类型做定向定位。
9. 不得覆盖 0.1.13 发布产物；0.2.0 使用独立版本与包。
10. 若某项真实数据当前无法取得，UI 必须诚实显示“暂不可取得 / 当前接口不支持”，不能制造假数据。

---

# 1. 执行策略：先定位，再实现，避免反复重构

本 Plan 是一次重量级 UI 调整。为控制 Sol / Luna 额度和返工风险，必须按以下方式执行。

## 1.1 Phase 0 只读定位

先不要改代码。

优先使用以下现有 UI 文案定位页面，不做全仓库泛扫：

- `调度器已就绪`
- `Scheduler 状态`
- `原生 Codex 模式`
- `CodexAgentSwitch 模式`
- `原生 Codex 项目配置`
- `应用配置并启动 Codex`
- `用量与预算`
- `服务商`
- `已保存方案`
- `当前方案`
- `活动任务`
- `不可取得`

定位并记录：

1. 首页 View / ViewModel。
2. 项目配置页 View / ViewModel。
3. 用量与预算页 View / ViewModel。
4. 服务商页 View / ViewModel。
5. 方案管理页 View / ViewModel。
6. 活动任务 / 历史页。
7. 全局导航 / Shell。
8. Scheduler 状态来源。
9. Active Task / Delegation / Result 状态来源。
10. Applied Project / Applied Snapshot 数据来源。
11. Usage / Provider 数据来源。
12. 当前托盘 / Close 行为实现位置。

### Phase 0 输出一个简短内部实施映射

格式：

```text
页面 → View → ViewModel → 真实状态源 → 需要改动
```

只列本 Plan 涉及的文件。

完成这个映射后直接进入实现，不需要用户再次确认；除非发现实现会破坏已验证运行链路。

---

# 2. 产品级交互模型

0.2.0 以后，用户必须能用下面这个心智模型理解软件：

```text
方案 = 我希望 Sol / Worker 怎么工作
项目配置 = 把某个方案应用到某个项目
首页 = 看项目目前是否已配置、是否在运行、是否有任务
启动 Codex = 从已配置项目启动官方 Codex
Agent Switch 后台 = 接收并调度真实 Worker 任务
活动任务 = 看 Agent Switch 现在正在做什么
```

不要要求用户理解：

- Scheduler
- TaskPacket
- ResultPacket
- Delegation State
- Applied Snapshot
- Executor
- NativeInvocation

这些术语可以继续存在于代码内部，但默认 UI 不暴露。

---

# 3. 首页改造成“运行总览 / 控制台”

这是 0.2.0 最高优先级。

## 3.1 顶部状态从“Scheduler”改成用户语言

不要再显示：

```text
调度器已就绪
Scheduler 状态
```

改为：

```text
Agent Switch 已就绪
Agent Switch 正在工作
Agent Switch 已暂停
Agent Switch 运行异常
```

建议内部状态：

```text
Stopped
Ready
Working
Paused
Faulted
```

UI 映射：

```text
Stopped  → 未启动
Ready    → 已就绪
Working  → 正在工作
Paused   → 已暂停
Faulted  → 运行异常
```

顶部状态必须包含：
- Agent Switch 后台状态。
- 活动任务数量。
- 当前存在活动任务的项目数量。
- 若有异常，提供“查看详情”入口。
- 关闭窗口后仍在托盘运行的提示，只在合适位置展示一次，不要重复塞满页面。

工作态必须视觉上明显不同于待机态。

待机：

```text
● Agent Switch 已就绪
当前没有活动任务
```

工作：

```text
● Agent Switch 正在工作
2 个活动任务 · 1 个项目
```

暂停：

```text
● Agent Switch 已暂停
不会接受新的 Worker 委派
```

---

# 4. 首页增加“项目状态”核心区域

首页不要再主要强调“工作模式”卡片。

新增：

```text
项目状态
```

每个项目至少显示：
- 项目名。
- 路径（次要信息，可弱化）。
- 当前 Applied 方案。
- 主代理。
- Worker。
- 当前状态。
- 最后应用时间。
- 操作按钮。

待机示例：

```text
TestSpace
SOL + LUNA

主代理   GPT-5.6 Sol · High
Worker   GPT-5.6 Luna · Medium
状态     已就绪
最后应用 19:40

[启动 Codex] [重新配置]
```

运行示例：

```text
TestSpace
SOL + LUNA

● 正在工作
当前阶段   Worker 执行中
Worker     Luna
已运行     00:34

[打开 Codex] [查看任务]
```

必须区分：
- 项目已配置。
- Codex Desktop 已启动。
- Agent Switch 后台已运行。
- 当前存在活动 Worker Task。
- 当前 Worker Task 已完成。

不能混为一谈。

---

# 5. “项目配置”和“启动 Codex”彻底分责

当前重复：
- 首页：`启动 Codex`
- 项目配置页：`应用配置并启动 Codex`

0.2.0 必须消除冲突。

项目配置页主要按钮统一为：

```text
应用到项目
```

或：

```text
保存并应用
```

应用成功流程：

```text
选择方案
↓
选择项目
↓
应用到项目
↓
显示成功反馈
↓
自动返回首页
↓
首页立即显示新的项目 / 方案状态
```

Codex 的启动入口统一放在首页项目卡：

```text
[启动 Codex]
```

---

# 6. 项目配置页简化

项目配置页不是工作台，不显示大量运行状态。

推荐布局：

左侧：

```text
项目

☑ TestSpace
□ DarkGrey
□ DarkGrey_RPG
```

右侧：

```text
即将应用

方案       SOL + LUNA
主代理     gpt-5.6-sol · high
Worker     gpt-5.6-luna · medium
路由       Economic
项目       TestSpace
```

底部：

```text
[取消] [应用到项目]
```

修复要求：
- 不再显示让用户误以为必须从这里启动 Codex 的主按钮。
- 不再让配置页承担运行监控。
- 应用成功后自动回首页。
- 未选项目时按钮禁用，但必须明确说明“请选择至少一个项目”。

---

# 7. 活动任务必须变成真实可用功能

统一 UI Task State 映射：

```text
Queued      → 等待执行
Delegated   → 已委派
Running     → Worker 执行中
Returned    → Worker 已返回
Reviewing   → 主代理审查中
Completed   → 已完成
Failed      → 失败
Cancelled   → 已取消
```

活动任务卡至少显示：
- 项目名。
- 任务名 / TaskId 的可读标题。
- 方案。
- Worker 类型（Native / External）。
- Worker 模型名（若真实可取得）。
- 当前阶段。
- 开始时间。
- 已运行时长。
- 最近更新时间。
- 成功 / 失败状态。

首页只显示最近 1~3 个活动任务摘要，并提供：

```text
[查看全部活动任务]
```

---

# 8. 顶部全局状态跨页面保留

任务运行时，用户切到：
- 项目配置
- 方案
- 服务商
- 用量
- 设置

任务必须继续运行。

Shell 顶部始终可以看到：

```text
● Agent Switch 正在工作 · 1 个活动任务
```

点击进入活动任务页。

---

# 9. 暂停、恢复和退出

`暂停接管` 的语义：

> 不再接受新的 Worker 委派。

不要默认杀掉正在运行的 Worker。

若当前有任务：

```text
当前仍有 2 个任务正在运行。
暂停后不会再接受新的 Worker 任务。
```

暂停后提供：

```text
恢复接管
```

退出时：
- 无活动任务：正常退出。
- 有活动任务：弹出确认。

```text
仍有 2 个任务正在运行。

退出 Agent Switch 可能导致这些任务无法继续被调度。

[取消]
[等待任务完成]
[仍然退出]
```

---

# 10. 正式明确托盘行为

关闭主窗口默认：

```text
关闭窗口
→ 隐藏到系统托盘
→ Agent Switch 后台继续工作
```

首次提示：

```text
Agent Switch 仍将在后台运行。
可以从系统托盘重新打开。
```

托盘菜单至少包含：
- 打开 Agent Switch。
- 当前状态。
- 暂停 / 恢复接管。
- 退出。

---

# 11. 用量与预算页面重做

不要再用大号“不可取得”卡占满页面。

状态区分：

```text
可取得
暂不可取得
当前接口不支持
暂无数据
```

有原因就展示原因。

External Worker / DeepSeek 若 ResultPacket 有 Usage：
- Provider。
- Model。
- Input Tokens。
- Output Tokens。
- Total Tokens。
- 最近调用时间。
- 最近调用成功 / 失败。

Native Worker 若无法取得独立 Usage：

```text
Luna 独立 Token Usage
当前 Codex Native Worker 接口未提供。
```

可展示本地真实统计：
- 今日 External Worker 调用次数。
- 最近任务 Worker 类型。
- 最近任务是否成功。
- 最近 Worker 结果是否被采用（若已有真实状态）。
- 当前配置的单任务 / 今日 / 每月预算上限。

不要制造无法验证的“节省百分比”。

---

# 12. 用量页响应式布局

最低验收：
- 以本机长期实际使用的 Windows 125% DPI 环境为准。
- 在当前环境可取得的有效窗口尺寸下检查响应式布局。
- 不再把 100% DPI 下的 1600×900、1920×1080 截图作为 0.2.0 Release Gate。
- 必须确认无文字截断、控件溢出、异常空白和不可操作问题。

响应规则：

```text
宽：3 列
中：2 列
窄：1 列
```

使用自适应 Grid / Wrap / MinWidth。

禁止出现：
- 右侧卡片被挤成狭窄竖条。
- 同时左边还有大量无意义空白。
- 文本明显被截断。

---

# 13. 服务商页面重做

Provider 卡统一结构。

DeepSeek 示例：

```text
DeepSeek

状态       ● 已启用
凭据       已配置
模型       deepseek-v4-flash
最近调用   成功 · 19:31

[配置] [测试]
```

原生 Codex：

```text
原生 Codex

状态       ● 已连接
支持模型   Sol / Terra / Luna
认证       Codex Desktop
API Key    无需配置
```

至少区分：
- 已配置。
- 已启用。
- 当前方案正在使用。
- 最近调用成功 / 失败。
- 未配置。
- 异常。

按钮区必须响应式，不能被挤出屏幕。

---

# 14. 方案页清理无意义灰色竖条

必须定位灰条真实来源，例如：
- ScrollBar
- SelectionPresenter
- Placeholder
- Template Column
- 固定宽度占位
- 状态 Badge 容器

从模板层正确修复。

禁止仅改透明颜色掩盖。

推荐选中态：

```text
▌ SOL + LUNA                      当前
```

使用：
- 左侧 Accent Border。
- 轻微背景高亮。
- `当前` Badge。

未选中方案不显示无意义右侧竖条。

---

# 15. 统一状态视觉语义

建立共享状态视觉层：

```text
Ready / Connected / Success → Success
Running                     → Accent / Info
Paused / Warning            → Warning
Failed / Faulted            → Error
Unavailable / NoData        → Neutral
```

同一状态在首页、Provider、任务、项目卡中使用相同语言和视觉。

---

# 16. 统一 UI 术语

默认 UI 使用：
- Agent Switch 状态
- 项目
- 当前方案
- Worker
- 活动任务
- 历史
- 用量
- 预算
- 服务商
- 已连接 / 已启用 / 正在工作 / 已暂停 / 失败

避免默认暴露：
- Scheduler
- TaskPacket
- ResultPacket
- Executor
- Applied Snapshot
- Delegation State
- NativeInvocation

高级日志 / Debug 可以保留内部术语。

---

# 17. 空状态完整化

无活动任务：

```text
当前没有活动任务

Agent Switch 已就绪。
当 Sol 将任务委派给 Worker 后，会在这里显示运行状态。
```

无历史：

```text
暂无任务记录
```

无 Provider Usage：

```text
尚无 External Worker 调用记录
```

无项目：

```text
还没有添加项目

[添加项目]
```

不要只留大片空白。

---

# 18. 实时状态绑定

优先复用现有：
- Scheduler state。
- Task / Delegation state。
- Result state。
- Applied Project / Snapshot。
- Provider state。
- Usage state。

实现优先级：
1. 事件通知 / Observable。
2. 现有状态中心订阅。
3. 必要时低频刷新兜底。

禁止高频暴力轮询。

UI 刷新只能做投影，不得改变真实 Scheduler / Task 状态。

---

# 19. 经济执行规则

Sol 负责：
- Phase 0 精确定位。
- 确定状态数据来源。
- 确定跨页面状态模型。
- 审查最终实现。
- 构建 / 测试 / UI 验收。

如果使用 Luna，只允许在 Sol 已明确：
- 具体文件。
- 具体控件 / ViewModel。
- 明确改动范围。
- 验收标准。

之后再委派。

推荐最多拆为 2~3 个互不重叠的 bounded task：

**Luna Task A**
首页 + Shell 全局状态 + 项目卡 + 活动任务摘要。

**Luna Task B**
项目配置页 + 方案页 UI。

**Luna Task C**
用量页 + 服务商页响应式布局。

每个 Task：
- 明确 Allowed Write Scope。
- 不允许全仓扫描。
- 不允许改运行链路。
- 不允许递归分包。
- 返回改动文件、验证结果和风险。

Sol 不得重新实现 Luna 已完成工作，只做 bounded review。

如果 Sol 无法明确指出“委派后自己会跳过哪部分实现”，则不要创建 Luna。

---

# 20. 防返工实现顺序

严格按顺序执行：

```text
Phase 0  定位真实 UI / 状态源
↓
Phase 1  建立共享 UI 状态投影 / ViewModel
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

禁止每改一个页面就重新设计状态模型。

---

# 21. Mock / Design-time 状态用于 UI 验收

开发阶段禁止真实调用 Luna / DeepSeek。

为 UI 验收提供 Debug / Test 状态源，至少可模拟：

- Ready：0 活动任务。
- Working Native：TestSpace / SOL + LUNA / Luna / Worker 执行中。
- Working External：TestSpace / SOL + DSV4F / DeepSeek V4 Flash / Worker 执行中。
- Reviewing：Worker 已返回 / Sol 审查中。
- Failed。
- Paused。
- Faulted。

Mock 只能存在于测试 / Debug 场景，不能污染 Release 真实状态。

---

# 22. 自动回归：保护 0.1.13 核心运行链

UI 重构后必须自动测试。

## SOL + LUNA Apply

确认：

```text
agents.enabled = true
[agents.cas_luna_worker]
AGENTS.md 中存在：
agent_type="cas_luna_worker"
fork_turns="none"
cas-luna-worker.toml 存在
```

## SOL + DeepSeek Apply

确认：
- External Worker route 仍走 Agent Switch Delegate Worker。
- Applied Worker 解析逻辑未变。
- 不生成旧的 Native external worker collaboration 路径。
- 不破坏现有 ExternalWorkerAdapter。

这些测试不得发起真实 AI / API 调用。

---

# 23. UI 验收矩阵

必须在本机长期实际使用的 Windows 125% DPI 环境下检查：

| 有效窗口环境 | 首页 | 项目配置 | 活动任务 | 方案 | 用量 | 服务商 |
|---|---|---|---|---|---|---|
| 当前 125% DPI 有效窗口尺寸 | 必测 | 必测 | 必测 | 必测 | 必测 | 必测 |

Phase 11 验收结论：已完成。现有 1366 有效布局矩阵与规定状态截图保留为验收证据；不再要求补充 100% DPI 下的 1600×900、1920×1080 截图。

验收：
- 不出现文字被截断。
- 不出现右侧内容被挤出窗口。
- 不出现一侧巨大空白、另一侧极度拥挤。
- 主操作按钮始终可见。
- 允许合理滚动，但不能因为固定高度造成控件消失。

---

# 24. 必须提供的最终截图

至少提供：
1. 首页 Ready 状态。
2. 首页 Working / Native Worker Mock 状态。
3. 首页 Working / External Worker Mock 状态。
4. 首页 Paused 状态。
5. 项目配置页。
6. 活动任务页。
7. 用量与预算页。
8. 服务商页。
9. 方案页，明确证明灰色竖条消失。
10. 1366×768 下最容易拥挤的页面截图。

截图必须来自实际运行的 0.2.0 UI，不是设计稿。

---

# 25. 0.2.0 最终验收标准

- [ ] 首页能一眼判断 Agent Switch 是否在工作。
- [ ] `Scheduler` 不再作为主要用户术语。
- [ ] 首页真实显示活动任务数。
- [ ] 首页直接显示项目 → Applied 方案映射。
- [ ] 首页能够启动已配置项目的 Codex。
- [ ] 项目配置页只负责配置。
- [ ] 应用配置成功后自动回首页。
- [ ] 项目配置页不再把“启动 Codex”作为重复主流程。
- [ ] 活动任务存在真实状态展示。
- [ ] 任务运行时切换页面不会丢失全局工作状态。
- [ ] 暂停 / 恢复语义清楚。
- [ ] 有活动任务时退出会警告。
- [ ] 关闭窗口后托盘行为明确。
- [ ] 用量页不再被“不可取得”占满。
- [ ] 用量页只展示真实可取得的 Usage。
- [ ] 服务商页状态语义清楚。
- [ ] 服务商页和用量页响应式正常。
- [ ] 方案页无意义灰色竖条彻底移除。
- [ ] 状态颜色和术语统一。
- [ ] 所有关键空状态完整。
- [x] 当前 125% DPI 环境下的现有有效窗口尺寸可用。
- [x] 响应式布局无文字截断、控件溢出、异常空白和不可操作问题。
- [ ] Sol + Luna 配置生成回归测试通过。
- [ ] Sol + DeepSeek 配置生成回归测试通过。
- [ ] Build / Test 通过。
- [ ] 真实 Luna 调用次数 = 0。
- [ ] 真实 DeepSeek/API 调用次数 = 0。

---

# 26. 版本与产物

版本：

```text
0.2.0
```

不得覆盖 0.1.13。

产物目录 / 文件名必须明确包含 `0.2.0`。

最终报告必须给出：
1. 修改页面。
2. 新增 / 修改 ViewModel。
3. 真实状态源接入方式。
4. 首页新交互流程。
5. 项目配置新流程。
6. 活动任务状态流。
7. 用量页处理方式。
8. Provider 页处理方式。
9. 方案灰条根因和修复。
10. 响应式实现方式。
11. 自动测试结果。
12. UI 验收分辨率。
13. 截图路径。
14. 0.2.0 包路径。
15. 真实 AI/API 调用次数（必须为 0）。

---

# 27. 质量门槛

不要把“功能做出来”当作完成。

本版本核心验收是：

> 用户第一次打开 Agent Switch，不需要阅读说明书，也能理解：
>
> - 我已经给哪些项目配置了什么方案；
> - Agent Switch 当前有没有在工作；
> - 如果在工作，它在替哪个项目做什么；
> - Worker 是 Luna 还是 DeepSeek；
> - 任务现在进行到哪一步；
> - 如何打开 Codex；
> - 如何暂停；
> - 如何恢复；
> - 如何退出；
> - 哪些 Usage 数据是真实可取得的，哪些当前不可取得。

若最终截图仍然需要开发者解释“这个灰块是什么意思”“为什么这里看起来没工作但其实在工作”“为什么按钮在两个页面都有”，则 0.2.0 不能视为完成。
