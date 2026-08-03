# Codex Agent Switch 0.1.4 发布测试报告

## 发布范围

0.1.4 在现有 WinUI 3 / .NET 8 架构上增量修复配置方案界面，没有修改 Profile、Provider、凭据或 SQLite 数据结构。

- “新建方案”对话框由单纯拉宽改为横向重排：主代理字段保持紧凑；工作代理来源、具体代理和最大数量同排；路由、回退与说明同排；预算字段每排三个。
- 已保存方案操作区改为四个次要操作按钮和两个主要操作按钮，并使用 `ItemsWrapGrid` 按可用宽度自动换行。
- 配置方案页面增加 1000 有效像素响应式断点：宽窗口使用左右双栏；窄窗口使用上下卡片；顶部操作按钮在窄窗口移到标题下方。
- 版本元数据、打包脚本和发布审计脚本默认版本更新为 0.1.4。

## 构建与 UI 验证

- Release 构建：通过，0 警告、0 错误。
- 核心测试：62/62 通过。
- Bootstrapper 测试：19/19 通过。
- 主验收系统：Windows 10 22H2 x64，build 19045。
- Profile WinUI 自动化：浅色、119 DPI；外部服务商条件显示、新建、立即启用、导出、编辑、复制、删除副本、重启恢复、删除全部通过。
- 宽窗口截图确认四个小按钮同排居中、两个主按钮同排居中。
- 760 像素窄窗口截图确认顶部操作下移、两张方案卡片上下排列，小按钮保持固定可读宽度并自动换行。

## 发布物与完整性

发布目录：`artifacts/release/0.1.4`

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `CodexAgentSwitch-win10-x64.zip` | 94,202,202 | `84d3138343705b34fa8493d43d6c468cd5fad6a434c50dd71dabcd2a1deeb59a` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | 413,028,558 | `ff588cae93964fa8b005e33b1683d52a4183284f063be1a37960be47ba75ae1b` |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | 237,532,048 | `4715cad45fb3858f6523c2d822f5877416feddb6c44abc9503efa7d95d605c4f` |
| `CodexAgentSwitch-win10-x64.zip.sha256` | 98 | `77a48beb079b156597a11afbddafef304274bf346e03d163d082c13d06d3a62b` |

- `release-manifest.json` 的版本为 0.1.4，重新计算的文件大小和哈希全部一致。
- Portable App、Setup 和 Runtime Bootstrapper 的文件版本均为 `0.1.4.0`，产品版本关联提交 `1283f3ca56f961a25a2acaab06d099d843f1a57f`。
- 捆绑的 `WindowsAppRuntimeInstall-x64.exe` Authenticode 状态为 `Valid`，签发者为 Microsoft Corporation。

## 升级与数据保留

在 E 盘隔离目录执行真实安装链：

1. 使用 0.1.3 Setup 安装并启动应用，生成 Profile 数据库。
2. 使用 0.1.4 Setup 原地升级同一目标。
3. 安装后文件版本为 `0.1.4.0`，备份目录中的旧版为 `0.1.3.0`。
4. 升级前后 `data/codex-agent-switch.db` SHA-256 完全一致。
5. 0.1.3 和 0.1.4 安装版都成功创建主窗口。

本次未运行要求把 `%TEMP%` 指回 C 盘的 `c-drive-release-audit.ps1`，以遵守 C 盘只读约束；构建、打包、UI 数据、安装目标、开始菜单测试根和临时目录均定向到当前 E 盘工作区。没有覆盖现有正式安装。
