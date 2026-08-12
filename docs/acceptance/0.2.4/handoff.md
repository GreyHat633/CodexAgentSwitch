# 0.2.4 → 下一阶段交接

## 当前状态

- 源码 HEAD：`d175c9e`，分支 `feat/0.2.4-main-cost-guard`。
- 安装版本：`0.2.4+d175c9ee7a03cfee19ebb18989011a13ccd47207`，位于 `E:\AISPace\Codex Agent Switch`。
- 自动化：核心 241/241、Bootstrapper 19/19；App Release x64 0 警告/0 错误。
- 发布包与哈希见同目录 `acceptance-report.md`。
- 总体验收：代码/构建/包/部署通过；自然主动分工仍待一次经授权的替代性重测，因此未标记 Plan PASS。

## 下一步入口

1. 不修改 0.2.4 的 synthetic fixture。经用户明确授权后，新建一个 Main Session，用不含 Agent/Worker 关键词的中等开发任务进行一次自然重测。
2. 观察到第一份真实 Worker Job 创建后立即停止；同时记录 PreToolUse Hook 是否在首次 mutation 前执行。
3. 若仍失败，优先检查运行安装是否确为 `d175c9e`、项目数据库中最具体注册项目与 AppliedSnapshot、ToolHost pipe 及 `required = true` MCP；不要增加提示词规避缺陷。
4. 若通过，在 `acceptance-report.md` 追加线程、Task/Job ID、fixture clean 状态和 Hook 证据，再决定是否标记 0.2.4 PASS。
5. Zen live call 需用户先执行 `opencode auth login` 并选择当前 catalog 中模型；保持 raw model ID，不新增 Agent Switch API Key。

## 回滚

- 旧安装：`E:\AISPace\Codex Agent Switch.backup-20260812-015627`。
- 安装器已经把既有 `data` 迁移到新安装目录；回滚前先停止路径严格位于当前安装目录下的 App/ToolHost，并备份当前 `data`，不要删除数据库或凭据。

## 协作记录

0.2.4 的 Main Cost Guard、Ownership/Hook、Context Economy、Provider Registry、运行时集成和中文化均采用有界工作包并由 MAIN 进行定向审查；没有重复实现已委派包。最后的嵌套 cwd 修复因受管调度器传输已关闭而由 MAIN 接管，原因记为 `WORKER_CAPABILITY_MISSING`。

