# Codex Agent Switch 0.1.2 修复与验收报告

## 结论

0.1.2 在现有 0.1.1 架构上增量完成环境状态布局、真实 Profile Editor、完整应用图标和发布链路修复。主验收系统为 Windows 10 22H2 x64（10.0.19045），实际显示 DPI 119（约 124%，对应 125% 档）。Release 构建 0 警告、0 错误；核心测试 62/62、Bootstrapper 测试 18/18 通过。

## 环境状态重叠根因与修复

修复前的每个状态项使用 `Grid`，图标和文字都声明了 `Grid.Column="1"`，但容器没有定义任何 `ColumnDefinitions`。WinUI 因而把两者都放入隐式第 0 列，状态文字从图标位置开始绘制，形成统一重叠；根因是共享状态模板的列布局错误，不是状态值、字体或某一行的特殊数据。

修复后每行显式使用 `Auto` 图标列和 `*` 文本列，`ColumnSpacing=10`，文字允许换行并设置 `MinWidth=0`，图标与文字顶部对齐。不使用硬编码空格或物理像素偏移。

真实 FrameworkElement 布局追踪结果：浅色长文本和深色短文本均为 `overlapArea=0`，图标至文字间距 9.68 DIP；长文本在窗口缩放下按列宽换行。

## “新建方案”伪交互根因与 Profile Editor

0.1.1 的 `profile:new` 处理器只把当前 Profile 克隆为新 ID，并把名称写死为“自定义方案”，随后立即设为默认。它没有编辑表单、字段绑定或用户确认，因此视觉上像改标题，也存在覆盖当前使用意图的风险。

0.1.2 复用原有 Profile、ProfileService 和 SQLite Repository，新增真实 ContentDialog 编辑器与双向绑定，支持：

- 新建、编辑、复制、删除、设置默认、立即启用；
- 主代理 Sol / Terra / Luna 与 low / medium / high / xhigh 推理强度；
- Worker 开关、原生/外部来源、原生 Worker、最大数量；
- Economic / Balanced / Performance / Single / Manual 路由；
- 回退动作、单任务/每日/月度预算、Token 和请求上限；
- 必填、唯一名称、数值范围、Worker/路由一致性校验；
- 内置预设与用户方案标识；
- 导入/导出沿用同一验证链，导出对象不含 API Key、凭据引用或密钥值。

新建使用新 GUID 和空白必填名称，不修改当前 Profile；复制使用自动唯一名称。保存后刷新并选中新项，可立即启用；重启后从 SQLite 恢复。操作区采用两列三行，在 1024 DIP 窗口中“设置默认”“立即启用”均完整显示。

## 图标设计与接入

原创方案共三套：

1. Switchboard Hub：双向切换核心连接三个 Worker 节点，表达模型切换和主代理编排；最终采用。
2. Relay Orbit：中心路由器与轨道节点，强调任务接力。
3. Console Fork：控制台面板中的分叉路径，强调路由控制。

最终图标使用克制的深蓝底、青蓝路由线和高对比节点，不依赖现有品牌 Logo。源文件为 SVG，同时导出 ICO 和 16、20、24、32、40、44、48、64、96、128、150、256、310、512px PNG。已接入：应用 PE、窗口 `AppWindow.SetIcon`、Setup PE/窗体、Runtime Bootstrapper PE/窗体、便携版侧车 ICO、安装后快捷方式和开始菜单快捷方式 IconLocation。

ICO 结构包含 16 至 256px 的九个关键帧；App、Setup、Bootstrapper 的嵌入图标与 32px 基准图平均像素差均为 9.335（阈值 16），通过。安装后快捷方式指向 `installed\AppIcon.ico,0`。

## 回归结果

| 验收项 | 便携版 | 安装/升级版 | 结果 |
|---|---:|---:|---|
| 环境状态长/短文本、浅/深色、换行 | 是 | 是 | 0 交叠，9.68 DIP 间距 |
| 新建真实 Profile | 是 | 是 | 真实对话框、写入 SQLite |
| 编辑、复制、设置默认、立即启用、删除 | 是 | 是 | 每项有真实 UI 状态变化 |
| 重启恢复 | 是 | 是 | 新 Profile 重启后可见 |
| 导出不含 API Key/凭据字段 | 是 | 是 | JSON 内容扫描通过 |
| 首页、Provider、任务、预算、历史、设置、诊断、向导主操作 | 是 | 是 | WinUI 控件实际触发通过 |
| 鼠标、Tab、Enter、Space、滚动后点击 | 是 | 是 | 通过 |
| 应用/Setup/Bootstrapper/快捷方式图标 | 是 | 是 | PE 与 LNK 属性验证通过 |

UI 生命周期测试在浅色便携版和深色安装版分别创建随机命名方案，修改代理字段、复制、启用、导出、删除，终止并重启进程后继续操作。0.1.1 安装版先通过旧 UI 创建“自定义方案”，再原地升级到 0.1.2；升级前后数据库 SHA-256 均为 `7F005CA746E142E1C8483228A11BEA1DC1295CC419B3F0CCFC8E3012B1F0F06F`，字节完全一致，随后 0.1.2 深色 UI 成功加载该旧方案。

## 缩放覆盖边界

当前 Win10 实机只有一个 1920×1080 显示器，窗口报告 DPI 119、XamlRoot scale 1.239583。125% 档已实际运行；100% 和 150% 使用相同 DIP 布局规则及几何断言，但未改变用户系统全局缩放，因此不能表述为物理实机覆盖。要关闭这两项硬件验收缺口，需要在 DPI 96 和 144 的 Win10 22H2 会话各运行一次 `scripts/layout-ui-smoke.ps1` 与 `scripts/ui-smoke.ps1`。

## 发布与升级

- 版本：0.1.2 / 0.1.2.0；
- 0.1.1 原目录未覆盖；
- Setup 原地升级退出码 0，Profile 数据保持；
- Windows App SDK Runtime 1.8 安装器已打包，Authenticode 状态 Valid，签名者 Microsoft Corporation；
- Runtime 下载流程支持复用已验签同版本安装器、临时下载、验签后原子替换和三次重试；
- manifest 中全部文件的大小和 SHA-256 已重新计算并逐项匹配。

发布 SHA-256：

- `CodexAgentSwitch-compact-runtime-win10-x64.zip`: `685ed5185c3791e0f759d9cbb8d6a33b7f4255f9ebfafb9d6f8a1a8c93f82645`
- `CodexAgentSwitch-Setup-Bundle-win10-x64.zip`: `8f5c8f6e04aa3ea5b3fdfff4a2e654f8f9c5861836e3b35874b412b152a55add`
- `CodexAgentSwitch-win10-x64.zip`: `aa4368fb02fe40fcfbc10811106d9c79629d7ba4ddd8909db86b928a0c293655`

## 本机磁盘写入纠正

一次手工 `dotnet publish` 和首次启动旧 Setup 时没有先覆盖系统默认 TEMP/NuGet 路径，误写 C:。已停止相关进程，将本轮创建的 75 个对象、435.42 MB 移至 E: 可恢复隔离目录，C: 可用空间恢复约 0.403 GB；未触碰其他 C: 文件，D: 未被本轮写入且仍有 86.377 GB 可用。构建、打包、布局、全页面、Profile 和 Provider 脚本现均把 CLI home、NuGet、TEMP/TMP 和单文件解压目录固定到仓库或验收目录的 E: 路径。
