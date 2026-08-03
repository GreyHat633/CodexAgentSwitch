# Screenshot Set

截图由 WinUI `RenderTargetBitmap` 在真实 Windows 10 22H2 x64 进程内生成，分辨率 1280×800。

- `phase1-dashboard-light-win10.png`：纯色浅色首页。
- `phase1-dashboard-dark-win10.png`：纯色深色首页。
- `phase1-gallery-light-win10.png`：设计令牌与状态 Gallery。

截图生成路径由 `CAS_CAPTURE_PATH` 指定；`CAS_CAPTURE_PAGE` 和 `CAS_THEME` 用于选择页面与主题。该路径只在测试时启用，不参与普通用户流程。

Phase 7 新增了 Dashboard、Provider、Usage 和 Diagnostics 的 1024×720 截图，运行于该 Win10 主机真实约 125% 显示缩放，覆盖纯色浅色与深色主题。证据边界见 `../acceptance-win10.md`。
