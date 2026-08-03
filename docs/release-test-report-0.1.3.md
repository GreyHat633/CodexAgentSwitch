# Codex Agent Switch 0.1.3 发布阻断修复与 C 盘事故报告

## 结论

0.1.3 在 0.1.2 架构上增量修复发布入口，不修改 Profile、凭据或数据库格式。主程序、安装器和 Runtime Bootstrapper 均由“单文件自解压”改为“自包含多文件”，普通启动不再把约 196 MiB 的应用 payload 解压到 `%TEMP%\.net`。主验收环境仍为 Windows 10 22H2 x64（build 19045）。

## C 盘写入复核与处置

此前“仅 435 MiB”的判断不完整，只统计了当时已经移入 E 盘隔离区的部分。当前能够逐项确认的 Codex Agent Switch 开发、发布与启动写入为 **1,781,492,069 字节（1,698.96 MiB / 1.659 GiB）**：

| 来源 | 字节 | 说明 |
| --- | ---: | --- |
| 先前 E 盘隔离区 | 456,574,906 | 已在本轮前从 C 盘移走 |
| `C:\Users\GreyHat\.nuget` | 684,132,115 | 1,308 个文件 |
| `%TEMP%\.net\CodexAgentSwitch.App` | 618,033,804 | 0.1.0、0.1.1、0.1.2 三次 App 自解压，1,561 个文件 |
| `%TEMP%\.net\CodexAgentSwitch.Setup` | 8,401,272 | 5 个文件 |
| `%TEMP%\VBCSCompiler` | 14,341,448 | 147 个文件，本轮创建的编译器缓存目录 |
| `.dotnet` 本轮新增 | 8,524 | CLI 状态与遥测小文件 |

瞬时 NuGet 下载暂存文件已经消失，因此历史峰值只可判定为“大于 1.659 GiB”，无法再伪造一个精确峰值。

本轮又从 C 盘迁出并释放了 1,324,908,639 字节（1.234 GiB）的仍存数据。迁移采用复制、文件数/字节数/全量 SHA-256 清单校验、源目录改名、Junction 切换、备份移入 E 盘的顺序：

- NuGet 清单：`F65BD311D47454994A9A7EBECCF865D1BA16AA121983AE006E82ED3C703B9060`；
- App extraction 清单：`CBA5C993E3BAD7193ABB439BC3A23AECC0DEC9B9544B7822ED2BB614E15112D2`；
- Setup extraction 清单：`43AEECFFB38FEF8B16EAFA1E41189D04A5C168F150BDF13B7824E9ACC5F943AC`；
- VBCSCompiler 清单：`153FD4EBA6BA99F11CE8BDEA3619B789C6D0A0EC4C9D2D38A920E43090852144`。

物理数据现位于 `E:\AISPace\主模型项目区\state\host-cache\codex-agent-switch`，迁移备份位于 `E:\AISPace\主模型项目区\state\diagnostics\cas-013-c-drive-migration-backups`。C 盘 NuGet、旧 App、Setup、Bootstrapper 与 VBCSCompiler 入口均为指向 E 盘的 Junction。为避免升级移动安装目录时断链，三个 bundle Junction 已重定向到安装目录之外；安全策略不允许删除的旧 Junction 被改名为 `.cas-retarget-20260804` 回滚入口，不包含 payload 数据。

## 根因与代码修复

`scripts/package.ps1` 对主 App 设置了 `PublishSingleFile=true` 与 `IncludeAllContentForSelfExtract=true`，并对 Setup、Bootstrapper 设置了 `PublishSingleFile=true` 与 `IncludeNativeLibrariesForSelfExtract=true`。因此每个版本的普通双击都会由 .NET bundle host 解压到系统默认 C 盘 TEMP。缓存重定向只能掩盖问题，不能修复发布物。

0.1.3 的修复如下：

- App、Setup、Bootstrapper 显式 `PublishSingleFile=false`，仍保持 x64 自包含发布；
- 包装脚本强制验证每个入口同时存在 `.exe`、`.dll`、`.deps.json` 与 `.runtimeconfig.json`；
- Setup Bundle 携带完整 Setup 发布目录，不再只复制单个 EXE；
- compact-runtime 根目录放 Bootstrapper，`App\` 子目录放主程序，避免两套自包含 .NET runtime 的同名文件互相覆盖；
- Bootstrapper 同时兼容 0.1.3 的 `App\` 布局与旧版同目录布局；
- 发布审计检查 PE bundle header offset，非零即阻断，并在系统默认 TEMP 下做实际启动前后物理快照。

## 验证结果

- 核心测试：62/62 通过；
- Bootstrapper 测试：19/19 通过，新增 compact/legacy 布局解析测试；
- Release 解决方案：0 警告、0 错误；
- 系统：Windows 10 22H2 x64，build 19045；
- TEMP：`C:\Users\GreyHat\AppData\Local\Temp`；
- `DOTNET_BUNDLE_EXTRACT_BASE_DIR`：未设置；
- 实际启动：便携 App、安装后的 App、compact Runtime Bootstrapper 均成功；
- 实际安装：Setup CLI 从 0.1.2 原地升级到 0.1.3，目标为 `E:\AISPace\Codex Agent Switch`；版本为 0.1.3.0，旧版本保留为时间戳备份；
- C 盘监控物理条目：启动前 6，启动后 6，差异 0；
- 审计证据：`docs\acceptance\c-drive-release-audit-0.1.3.json`；原始运行输出保存在 `.tmp\c-drive-release-audit-20260804-011937\result.json`。

Windows 标准开始菜单入口本身是位于用户配置目录 C 盘的 `.lnk` 文件。为了同时保留既有“开始菜单图标”功能与不写入大型 payload 的目标，产品仍保留该功能；本轮安装测试通过 `CAS_START_MENU_ROOT` 将测试快捷方式重定向到 E 盘。这个极小 Shell 文件不应被表述为 payload 零写入，完整无 C 写入部署可使用 E 盘快捷方式或保留已有快捷方式。

## 发布物

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `CodexAgentSwitch-win10-x64.zip` | 94,194,500 | `875748d353bdfb85dfbcdcd3df449549411584ba4acae8e7a6f1047bc296b379` |
| `CodexAgentSwitch-compact-runtime-win10-x64.zip` | 237,524,669 | `1b12f0363058e9ed68e118ec65e4000dd23c1cced92363d4397aabc5ca097aec` |
| `CodexAgentSwitch-Setup-Bundle-win10-x64.zip` | 413,013,130 | `652a6be63fa853cbb2c290e022fc8cba656317d412473c3e9405045f319702b8` |

0.1.2 发布物未覆盖。Profile 与凭据格式未变化；Setup 延续既有 `data` 迁移和时间戳备份逻辑，支持从 0.1.2 原地升级。
