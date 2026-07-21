# IcedPicViewer

本地图片 / 视频查看器。当前**主交付**为 Avalonia 跨平台桌面应用；WinUI 3 工程保留为 Windows 行为对照基线。

| 路径 | 说明 |
|------|------|
| `src/IcedPicViewer.Avalonia` | **主程序**（Win / macOS / Linux，.NET 10 + Avalonia 12） |
| `src/IcedPicViewer.Core` | 平台无关：扫描、归档、设置、FFmpeg 抽帧等 |
| `src/IcedPicViewer.WinUI` | 过渡期对照（WinUI 3 + WASDK，MSIX，x64） |

迁移前纯 WinUI 快照 tag：`winui-baseline`。

## 功能（Avalonia 主线）

- 打开文件夹 → 递归扫描；**ZIP / RAR / 7Z / tar.\*** 内媒体展平进同一瀑布流
- **瀑布流**（3 列铺满宽度，间距 8，对齐原 WinUI 体感）
- **混合加载**：边扫边灌到约 200 张停；Load More / 滚到底继续
- 悬停卡片：半透明遮罩 + 文件名 / 尺寸·大小 / 位置（ToolTip 同步）
- 查看器：Fit / 1:1（小图居中）、minimap、GIF 动画、EXIF 方向
- 视频：FFmpeg 首帧缩略图 + LibVLC 播放（Space 播放/暂停，0–9 跳转）
- 幻灯片：间隔 / 循环 / 随机；全屏 F11（顶/底热区出工具栏，淡入淡出）
- 删除进回收站（网络路径确认；压缩包内不可删）；打开文件位置
- 目录监控、Refresh（F5）、About、窗口几何记忆
- **不**自动打开上次目录，每次启动手动选择

## 操作指南（Avalonia）

### 缩略图视图

| 操作 | 行为 |
|------|------|
| 单击图片 | 打开查看器 |
| 鼠标悬停 | 遮罩 + 文件名 / 尺寸·大小 / 目录或压缩包名 |
| 右键 | 打开文件位置 / 删除 |
| 滚到底 /「加载更多」 | 继续加载 |
| F5 | 刷新当前文件夹 |
| F11 | 全屏 |

### 图片 / 视频查看器

| 操作 | 行为 |
|------|------|
| Esc / 关闭 | 返回瀑布流并滚到当前项附近 |
| ← → | 上一张 / 下一张 |
| F | Fit ↔ 1:1（1:1 可拖 minimap） |
| Space | 图片：幻灯片开关；视频：播放/暂停 |
| 0–9 | 视频 seek 0%…90% |
| Delete | 删除（策略见下） |
| 全屏下顶/底边缘 | 显示工具栏（中间移动不刷出） |

### 删除行为

- **本地文件**：进回收站（无确认）
- **网络路径**：确认后永久删除
- **压缩包内**：**不可删**（提示到资源管理器处理压缩包）

## 构建与运行

### Avalonia（推荐）

```powershell
dotnet build src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
dotnet run --project src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
```

依赖：.NET 10。Windows 上 FFmpeg native 从 WinUI 工程 `runtimes/win-x64/native` 复制到输出；LibVLC 由 `VideoLAN.LibVLC.Windows` 带入。

设置文件：`%LOCALAPPDATA%\IcedPicViewer\settings.json`（幻灯片、音量、窗口几何等）。

### WinUI（对照，仅 Windows x64）

```powershell
dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug -p:Platform=x64
dotnet publish src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Release -p:Platform=x64
```

前置：.NET 10 Runtime + Windows App Runtime（MSIX 会拉 framework）。  
**不要**直接双击 MSIX 产物里的 `.exe`（无 package identity 会 `REGDB_E_CLASSNOTREG`）。请用 `dotnet run`。

## 版本

- **v0.15.0** - Avalonia 跨平台主线：Core 抽离 + 图库/查看器/视频/幻灯片/全屏/Fluent 浅色 UI；WinUI 迁入 `src/` 作对照；基线 tag `winui-baseline`
- v0.14.7 - WinUI：Chrome 浮动 overlay、Load More 预加载、状态栏视频计数等
- v0.14.x - 视频 / Slideshow / 全屏 / EXIF / archive 等（详见 `CHANGELOG.md`）
- 更早版本见下方历史与 `CHANGELOG.md`

### 历史摘要（WinUI 时代）

- v0.14.7 - Chrome 浮动 overlay + Load More 调整 + 状态栏视频计数
- v0.14.5 - 真全屏 + Slideshow loop/shuffle + 视频 archive
- v0.14.0 - 视频数据通路 + FFmpeg + About + LGPL
- v0.12.0 - 压缩包内图片
- v0.6.0 / v0.7.0 - 增量加载 + 滚底 Load More

## 许可

应用代码以仓库为准。捆绑 FFmpeg（LGPL 2.1+）与 LibVLC 遵循各自许可证；WinUI 包内见 `License/ffmpeg-LGPL.txt`。
