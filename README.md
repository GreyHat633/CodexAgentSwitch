# Codex Agent Switch

Codex Agent Switch 是 Windows 10 22H2 x64 优先、Windows 11 增强兼容的 WinUI 3 / .NET 8 桌面控制台，用于管理 Profile、原生 Codex Worker、DeepSeek / OpenAI-Compatible Provider、委派边界、预算、Usage 与可恢复发布。

## 核心保证

- Windows 10 22H2 x64 是主要构建和实机验收环境。
- 不依赖 Mica、Acrylic、Win11 专属标题栏、Snap Layout 或系统材质；浅色、深色、高对比度均有纯色/系统色回退。
- 字体按 `Segoe UI Variable` → `Segoe UI` 回退。
- API Key 只进入 Windows Credential Manager；SQLite 和日志只保存凭据引用。
- 原生 App Server 协议从当前 Codex CLI 生成 Schema，不把历史协议写死。
- Worker 删除前保存终态、结果、Usage 和采用记录；不可取得字段明确标为 `unavailable`。
- 外部 Provider 没有无限重试，100% 预算会暂停新请求，并可回退原生 Luna。

## 开发与验证

```powershell
pwsh -File .\scripts\build.ps1
pwsh -File .\scripts\package.ps1 -Version 0.1.2 -IncludeRuntimeInstaller
```

所有 .NET、NuGet、临时和发布状态均重定向到仓库所在 E 盘目录。交付文档见 [ARCHITECTURE.md](ARCHITECTURE.md)、[SECURITY.md](SECURITY.md)、[PRIVACY.md](PRIVACY.md) 与 `docs/`；最终 Win10 验收记录在 `docs/release-test-report.md`。
