# UI Design System

## 平台基线

- 主验收：Windows 10 22H2 x64（19045）。
- 增强兼容：Windows 11；不要求或依赖 Mica、Snap Layout、专属标题栏或系统材质。
- 技术：WinUI 3 + Windows App SDK 1.8 + .NET 8。
- 所有背景、卡片、边框和状态均有纯色 Light/Dark/HighContrast 资源。

## 字体

- Display：`Segoe UI Variable Display, Segoe UI`。
- Text：`Segoe UI Variable Text, Segoe UI`。
- Windows 10 未安装 Variable 字体时自动回退到 Segoe UI。
- 正文最小 14px；标题 32px；不捆绑字体文件。

## 令牌

| 类别 | 值 |
|---|---|
| 间距 | 4 / 8 / 12 / 16 / 24 / 32 |
| 控件圆角 | 8 |
| 卡片圆角 | 12 |
| 大容器圆角 | 16 |
| 页面内边距 | 32, 24, 32, 32 |
| 卡片内边距 | 20 |
| 紧凑卡片内边距 | 16 |

## 纯色主题

Light：窗口 `#F3F3F3`，卡片 `#F9F9F9`。  
Dark：窗口 `#202020`，卡片 `#2B2B2B`。  
High Contrast：使用 Windows 系统 Window/WindowText 颜色。

强调色仍取系统 Accent Resource，但不作为唯一状态表达；所有状态同时显示文本或图标。

## 交互规则

- 页面只保留一个主要 Accent 操作。
- 破坏性操作必须显示后果、要求确认，并提供可恢复路径。
- 高级协议、Thread ID、原始 JSON 与日志只在诊断/Expander 中出现。
- 加载、空、失败、等待批准、接近预算与重复劳动均有独立完整状态。
- 动画仅用于导航和状态变化；尊重系统减少动画设置。

## 无障碍

- 导航、进度与主操作使用 AutomationProperties.Name。
- 键盘顺序遵循视觉顺序；常用功能不藏在仅鼠标可达区域。
- 高对比度不依赖透明叠加；所有内容能在 100%～200% DPI 下换行。
