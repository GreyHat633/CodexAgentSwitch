CAS Universal Economic Audit
============================

用途
----
这是 Codex Agent Switch 的“长期通用经济审计器”。

正常情况下，以后每个版本完成并打好 Git tag 后：

1. 双击 RUN_AUDIT.cmd
2. 不需要填写版本号、时间段、commit 或 session 文件
3. 工具自动选择“最新两个 v* 版本标签”
   例如：
       v0.2.6.1 -> v0.2.6.2
4. 自动从 E:\AI\CODEX\.codex\sessions 中寻找对应 Sol/Luna 会话
5. 输出四项指标：
   - Actual Cost
   - Sol Displacement
   - Delegation Coverage
   - Adoption Efficiency

输出位置
--------
默认会在本工具同级目录下自动创建：

.\reports\<版本_时间>\

例如工具放在：
E:\AISPace\Tools\CAS_Economic_Audit\

则报告会生成到：
E:\AISPace\Tools\CAS_Economic_Audit\reports\v0.2.6.2_20260814-074500\

每次运行的全部生成文件都放在对应的独立子文件夹中，包括：
- Economic_Audit.md
- Economic_Audit.json

不会往桌面或 CAS 源码仓库写报告，因此不会污染桌面，也不会制造 CAS Git dirty 状态。

自动模式
--------
直接双击 RUN_AUDIT.cmd。

它自动取 Git 中最新两个语义版本标签：
    最新标签 = FinalRef
    上一个标签 = BaseRef

因此以后从：
    v0.2.6.2 -> v0.2.6.3
    v0.2.6.3 -> v0.2.7
都不需要重新生成脚本。

手动模式
--------
特殊情况下可手动指定：

powershell.exe -NoProfile -ExecutionPolicy Bypass ^
  -File .\CAS_Universal_Economic_Audit.ps1 ^
  -BaseRef v0.2.6.1 ^
  -FinalRef v0.2.6.2

配置
----
audit.config.json 中长期保存：

- CAS canonical repo
- Codex sessions root
- 报告输出目录
- 模型价格

当前已配置 Astra、Sol、Terra、Luna。价格单位为 USD / 1M tokens，
并在 audit.config.json 的 PricingAsOf 中记录核对日期。

当前 Sol 基线价格：
gpt-6-astra
  Input: 10 / 1M
  Cached: 1 / 1M
  Output: 50 / 1M

gpt-5.6-sol
  Input: 4 / 1M
  Cached: 0.4 / 1M
  Output: 20 / 1M

gpt-5.6-terra
  Input: 2 / 1M
  Cached: 0.2 / 1M
  Output: 12 / 1M

gpt-5.6-luna
  Input: 0.2 / 1M
  Cached: 0.02 / 1M
  Output: 1.2 / 1M

2026-09-05 官方核对来源：
https://developers.openai.com/api/docs/models/gpt-6-astra
https://developers.openai.com/api/docs/models/gpt-5.6-sol
https://developers.openai.com/api/docs/models/gpt-5.6-terra
https://developers.openai.com/api/docs/models/gpt-5.6-luna

Sol-equivalent 不再使用单一倍率，而是把同一组输入、缓存输入和输出
token 分别按 Sol 单价重新计算。API 美元价格只是经济性代理指标，
不等同于 Codex 套餐的额度扣减。

重要：
如果以后模型或官方价格变化，只需要更新 audit.config.json。
不需要重新写审计脚本。

它如何避免“又审到旧版本”
------------------------
旧 0.2.6.1 专用脚本把以下内容写死了：

- BaseRef
- Round1Ref
- FinalRef
- 日期
- 时间窗口
- 版本关键词
- 历史 worktree 名

所以拿它跑 0.2.6.2 仍然会得到 0.2.6.1。

通用版不再写死这些东西。

它会：
1. 自动读取最新两个版本标签
2. 读取这两个标签的 Git 时间
3. 优先寻找明确出现“最终版本号/最终 commit”的 Codex Main 会话
4. 通过时间重叠找到对应 Luna 子代理会话
5. 只有找不到明确版本证据时，才退回 repo-path 匹配，并在报告里给 Warning

注意事项
--------
1. 这是“版本经济审计”，最好在版本完成、commit/tag 已完成后运行。
2. 如果当前 repo 还有未提交修改，报告会警告；Git 覆盖率只统计两个 tag 之间已经提交的 diff。
3. Paid-point balance 只是账户级观察，不等于 normalized model cost。
4. Delegation Coverage / Adoption Efficiency 是机械代理指标，不代表语义难度或代码质量。
5. MainAdjusted 不自动等于 Worker 失败。
6. 如果出现新模型而 audit.config.json 没有价格，工具不会猜价格，会明确警告 Unknown model。
7. External DeepSeek 等非 Codex-native provider 的完整成本只有在相应 token/cost evidence 可被本工具读取时才能纳入；当前默认四指标以 Codex Sol/Luna JSONL 为可靠数据源。
8. 工具不会调用 Sol、Luna、DeepSeek、OpenCode，也不会修改源码/Git/session JSONL。

推荐放置
--------
建议把整个文件夹长期保存到例如：

E:\AISPace\Tools\CAS_Economic_Audit\

以后每个 CAS 版本发布完双击一次即可。


版本修正
--------
本包已修复：
- PowerShell Markdown 输出行中的反引号转义导致的 ParserError。
- FileStream / StreamReader 构造方式改为 Windows PowerShell 5.1 兼容写法。
- RUN_AUDIT.cmd 会先做 PowerShell 语法预检，再正式执行审计。


再次修正
--------
上一版 RUN_AUDIT.cmd 的内联 PowerShell 语法检查错误地把 CMD 的 ^ 管道转义字符传进了 PowerShell。
本版不再使用内联 -Command。

现在流程为：
RUN_AUDIT.cmd
  -> CHECK_SYNTAX.ps1
  -> CAS_Universal_Economic_Audit.ps1

这样 CMD 和 PowerShell 的转义规则完全分离，适用于 Windows PowerShell 5.1。


会话识别规则修正（Fixed3）
-------------------------
上一版仍可能把“只是讨论了未来版本号”的故障排查/规划会话算进版本开发成本。

现在 Main 会话必须满足更强证据之一：
1. 会话中出现最终 commit hash；或
2. 会话中出现目标版本号，并且确实对 src/ 或 tests/ 下的开发文件产生了修改行为。

因此：
“讨论 0.2.6.2，但没有开发源码”的旧会话不会再因为版本号关键词被计入。

Worker 会话仍可通过：
- 自身明确开发证据；或
- 与强 Main 开发会话的时间重叠
被纳入。

如果找不到强 Main 锚点，fallback 也不再只靠 repo 路径，而要求 repo + 实际开发文件修改证据。
