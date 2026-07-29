# IcedPicViewer

本地图片 / 视频查看器。**Core、WinUI、Avalonia 三者平等**（无主次交付）：

| 路径 | 说明 |
|------|------|
| `src/IcedPicViewer.Core` | 平台无关库：扫描、归档、设置、FFmpeg 抽帧等（两壳共用，同等维护） |
| `src/IcedPicViewer.WinUI` | Windows 原生 UI（WinUI 3 + WASDK 2.3，MSIX，x64） |
| `src/IcedPicViewer.Avalonia` | 跨平台 UI（Win / macOS / Linux，.NET 10 + Avalonia 12） |
| `tests/IcedPicViewer.Core.Tests` | Core 单元/集成测试（xUnit；已进 solution） |

tag `winui-baseline`：迁移前纯 WinUI 快照，仅供历史 diff。协作约定与**定稿架构**见 `AGENTS.md`。

### 定稿命名（摘录）

| 概念 | 代码 |
|------|------|
| 媒体定位 | `MediaRef` + `MediaKind` |
| 图库项契约 | `IMediaEntry`（WinUI `MediaItem` / Avalonia `MediaItemViewModel`） |
| 图库集合 | `Items` |
| 查看器 | WinUI：`ViewerView` + `ViewerViewModel` |
| 加载 | `IMediaLoader` / `AvaloniaMediaLoader` |
| 文案 | `GalleryStatusFormatter` / `UiCopy`（中文） |

## 功能（产品能力；WinUI / Avalonia 对齐）

- 打开文件夹 → 递归扫描；**ZIP / RAR / tar.\*** 内媒体展平进同一瀑布流（**不含 7z**，见下）
- **瀑布流**（3 列铺满宽度，间距 8）
- **混合加载**：边扫边灌到约 200 张停；Load More / 滚到底继续
- 悬停信息：文件名 / 尺寸·时长·大小 / 位置
- 查看器：Fit / 1:1、幻灯片（间隔 / 循环 / 随机）、删除与打开文件位置
- 视频缩略图：FFmpeg 首帧（Core）
- 目录监控、Refresh、About（含 FFmpeg LGPL 说明）、窗口几何记忆
- **不**自动打开上次目录

### 实现差异（能力对等，技术栈不同）

| 能力 | WinUI | Avalonia |
|------|-------|----------|
| 平台 | Windows x64（MSIX） | Win / macOS / Linux |
| 图片解码 | WIC | ImageSharp |
| 视频播放 | `MediaPlayerElement`（系统编解码；部分 remux） | LibVLC + `VlcBitmapSurface` |
| 键盘全局路径 | `WH_KEYBOARD` hook（见 `AGENTS.md`） | 窗口 KeyDown / 命令 |
| GIF | 平台 / 查看器路径 | 自研 `GifAnimationPlayer` |

### 支持的格式（诚实版）

| 类别 | 扩展名 | 实际能力 |
|------|--------|----------|
| **图片（解码）** | `.jpg` `.jpeg` `.png` `.gif` `.bmp` `.webp` `.tiff` `.tif` `.ico` | Avalonia：ImageSharp；WinUI：WIC |
| **图片（扫描会进列表）** | 另含 `.heic` `.avif` | **扩展名会扫到**；Avalonia **无内置 HEIC/AVIF 解码**。WinUI 需系统/商店 HEIF / AV1 扩展 |
| **视频** | `.mp4` `.mkv` `.mov` `.avi` `.webm` `.flv` | 缩略图：FFmpeg；播放：见上表 |
| **压缩包内媒体** | `.zip` `.rar` `.tar` `.tgz` / `tar.gz` 等 | SharpCompress 可读 |
| **7z** | — | **不支持**（扫描可能记 error，不展开内容） |

扩展名清单以 `IcedPicViewer.Core` 的 `MediaCatalog` / `ArchiveHelper` 为准。

## 操作指南（WinUI / Avalonia 大体相同）

| 操作 | 行为 |
|------|------|
| 单击 | 打开查看器 |
| 滚到底 /「加载更多」 | 继续加载 |
| PageUp / PageDown | 瀑布流按视口高度翻页滚动 |
| F5 | 刷新 |
| F11 | 全屏 |
| ← → | 上一张 / 下一张 |
| F | Fit ↔ 1:1 |
| Space | 图片：幻灯片；视频：播放/暂停 |
| 0–9 | 视频 seek 0%…90% |
| Delete | 本地→回收站；网络路径确认后永久删；**压缩包内不可删** |

全屏 chrome：WinUI / Avalonia 各自实现（Avalonia 为顶/底热区）。

## 构建与运行

需 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

### Core

```bash
dotnet build src/IcedPicViewer.Core/IcedPicViewer.Core.csproj -c Debug
dotnet test tests/IcedPicViewer.Core.Tests/IcedPicViewer.Core.Tests.csproj -c Debug
```

### WinUI（Windows x64）

```powershell
./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64   # 首次 / 清仓后
dotnet build src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug -p:Platform=x64
dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug -p:Platform=x64
dotnet publish src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Release -p:Platform=x64
```

前置：.NET 10 + **Windows App Runtime 2.3**。  
**不要**直接双击 MSIX 产物里的 `.exe`（需 package identity）。请用 `dotnet run`。

### Avalonia（Win / macOS / Linux）

```bash
dotnet build src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
dotnet run --project src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
```

#### Avalonia 视频相关 native

| 组件 | Windows | macOS | Linux |
|------|---------|-------|-------|
| **UI / 看图** | 开箱 | 开箱 | 开箱（需图形会话） |
| **LibVLC 播放** | NuGet `VideoLAN.LibVLC.Windows` | NuGet `VideoLAN.LibVLC.Mac` | 系统 `vlc`/`libvlc-dev` 或 `IPV_LIBVLC_ROOT` |
| **FFmpeg 缩略图** | `./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64` | `brew install ffmpeg` 或 Fetch script | Fetch script 或 apt libav* |

### 验证约定

- 改 **Core**：build + `dotnet test` 绿。
- 改 **WinUI / Avalonia**：对应工程 build 干净 + 手动验关键路径；共享语义两边都验。
- 详见 `AGENTS.md`。

### 环境变量 / 设置

- `IPV_FFMPEG_ROOT` — 含 `avutil` / `libavutil` 的目录（Core 抽帧，WinUI / Avalonia 共用）
- `IPV_LIBVLC_ROOT` — 含 `libvlc` 的目录（**Avalonia/Linux**）
- 设置文件：`%LocalApplicationData%/IcedPicViewer/settings.json`（Windows 即 `%LOCALAPPDATA%\…`）

FFmpeg 拉取产物在 `src/native/ffmpeg/{rid}/`（**不进 git**）。

## 版本

- **v0.15.0** - Core 抽离；WinUI 与 Avalonia 同仓平等维护；tag `winui-baseline`
- **v0.15.x** - 两壳能力对齐与打磨；Core 测试与加固；PageUp/PageDown 瀑布流翻页等
- v0.14.7 - WinUI：Chrome 浮动 overlay、Load More 预加载、状态栏视频计数等
- v0.14.x - 视频 / Slideshow / 全屏 / EXIF / archive 等（详见 `CHANGELOG.md`）
- 更早版本见 `CHANGELOG.md`
## 许可

应用代码以仓库为准。捆绑 FFmpeg（LGPL 2.1+）与 LibVLC 遵循各自许可证。  
LGPL 全文：`src/IcedPicViewer.Avalonia/License/ffmpeg-LGPL.txt` 与 `src/IcedPicViewer.WinUI/License/ffmpeg-LGPL.txt`（构建后复制到输出目录）。
