# IcedPicViewer

本地图片 / 视频查看器。**Core（平台无关库）+ WinUI（Windows 原生壳）双工程**：

| 路径 | 说明 |
|------|------|
| `src/IcedPicViewer.Core` | 平台无关库：扫描、归档、设置、FFmpeg 抽帧等（`net11.0`） |
| `src/IcedPicViewer.WinUI` | Windows 原生 UI（WinUI 3 + WASDK 2.5，.NET 11，MSIX，**x64 only**） |
| `tests/IcedPicViewer.Core.Tests` | Core 单元/集成测试（xUnit；已进 solution） |

tag `winui-baseline`：历史快照，仅供 diff。协作约定与**定稿架构**见 `AGENTS.md`。

### 定稿命名（摘录）

| 概念 | 代码 |
|------|------|
| 媒体定位 | `MediaRef` + `MediaKind` |
| 图库项契约 | `IMediaEntry`（WinUI 实现 `MediaItem`） |
| 图库集合 | `Items` |
| 查看器 | `ViewerView` + `ViewerViewModel` |
| 加载 | `IMediaLoader` / `MediaLoader` |
| 文案 | `GalleryStatusFormatter` / `UiCopy`（中文） |

## 功能（产品能力）

- 打开文件夹 → 递归扫描；**ZIP / RAR / tar.\*** 内媒体展平进同一瀑布流（**不含 7z**，见下）
- **瀑布流**（3 列铺满宽度，间距 8）
- **混合加载**：边扫边灌到约 200 张停；Load More / 滚到底继续
- 悬停信息：文件名 / 尺寸·时长·大小 / 位置
- 查看器：Fit / 1:1、幻灯片（间隔 / 循环 / 随机）、删除与打开文件位置
- 视频缩略图：FFmpeg 首帧（Core）
- 目录监控、Refresh、About（含 FFmpeg LGPL 说明）、窗口几何记忆
- **不**自动打开上次目录

### 实现要点

| 能力 | 实现 |
|------|------|
| 平台 | Windows x64（MSIX） |
| 图片解码 | WIC |
| 视频播放 | `MediaPlayerElement`（系统编解码；部分容器 FFmpeg remux）；LibVLC 软渲染回退（`VlcImageSurface`） |
| 键盘全局路径 | `WH_KEYBOARD` hook（见 `AGENTS.md`） |
| GIF | 平台 / 查看器路径 |

### 支持的格式（诚实版）

| 类别 | 扩展名 | 实际能力 |
|------|--------|----------|
| **图片（解码）** | `.jpg` `.jpeg` `.png` `.gif` `.bmp` `.webp` `.tiff` `.tif` `.ico` | 系统 WIC |
| **图片（扫描会进列表）** | 另含 `.heic` `.avif` | **扩展名会扫到**；需系统/商店 HEIF / AV1 扩展 |
| **视频** | `.mp4` `.mkv` `.mov` `.avi` `.webm` `.flv` | 缩略图：FFmpeg；播放：见上表 |
| **压缩包内媒体** | `.zip` `.rar` `.tar` `.tgz` / `tar.gz` 等 | SharpCompress 可读 |
| **7z** | — | **不支持**（扫描可能记 error，不展开内容） |

扩展名清单以 `IcedPicViewer.Core` 的 `MediaCatalog` / `ArchiveHelper` 为准。

## 操作指南

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

全屏 chrome：仅顶/底热区显示工具栏；翻图不闪栏。

## 构建与运行

需 .NET SDK **11**（版本由仓库根 `global.json` 固定，`rollForward: latestFeature`）。全工程统一 `net11.0`（WinUI 为 `net11.0-windows10.0.26100.0`）。

### Core

```bash
dotnet build src/IcedPicViewer.Core/IcedPicViewer.Core.csproj -c Debug
dotnet test tests/IcedPicViewer.Core.Tests/IcedPicViewer.Core.Tests.csproj -c Debug
```

### WinUI（Windows x64）

```powershell
# 视频/图片原生库，不进 git；缺失时 build 仅警告 IPV001，运行期会静默失去视频缩略图
./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64

# 启动（Debug / Release 二选一）
dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug   -p:Platform=x64
dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Release -p:Platform=x64
```

前置：**已开启 Windows 开发者模式** + .NET 11 runtime + **Windows App Runtime 2.5**。

- `-p:Platform=x64` **不可省略**：该工程只支持 x64，默认 `AnyCPU` 会失败。
- **本项目不做发布打包**——`dotnet run` 会注册调试包身份并启动，不需要安装 MSIX。
- ⚠️ **不要**直接双击 `bin\...\IcedPicViewer.exe`：缺 package identity 会 `REGDB_E_CLASSNOTREG`。

#### Debug vs Release

| | Debug | Release |
|---|---|---|
| 用途 | 日常开发、调试 | **评估性能 / 看真实吞吐** |
| 输出目录 | `src/IcedPicViewer.WinUI/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/` | `src/IcedPicViewer.WinUI/bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/` |

两种配置输出目录互相独立，不会污染彼此。评估 .NET 11 的性能收益**必须用 Release**：Runtime Async 等优化依赖 JIT 的 fast path，Debug 下不做这些优化，差异基本看不到。

#### 排查用环境变量

```powershell
# FFmpeg 解码诊断 → <app data>\ffmpeg-probe.log
$env:IPV_FFMPEG_PROBE='1'
# 首异常日志（XAML 初始化崩溃排查）→ <app data>\firstchance.log
$env:IPV_FIRSTCHANCE_LOG='1'
```

数据固定在 `%LOCALAPPDATA%\IcedPicViewer\`；原生库查找顺序 `IPV_FFMPEG_ROOT` → 输出目录 → 系统路径。

### 验证约定

- 改 **Core**：build + `dotnet test` 绿。
- 改 **WinUI**：`dotnet build` 干净 + 手动验关键路径。
- 详见 `AGENTS.md`。

### 环境变量 / 设置

- `IPV_FFMPEG_ROOT` — 含 `avutil` 的目录（Core 抽帧）
- `IPV_FFMPEG_PROBE` — `1` 时启动 FFmpeg 诊断探针（开发用）
- `IPV_FIRSTCHANCE_LOG` — `1` 时把 first-chance 异常写入 `<app data>\firstchance.log`（开发用）
- 所有可变状态固定在 **`%LocalApplicationData%\IcedPicViewer\`**（`settings.json`、`window_settings.txt`、`crash.log`、`TempVideo\`），统一由 Core `AppDataPaths` 解析

FFmpeg 拉取产物在 `src/native/ffmpeg/{rid}/`（**不进 git**）。

## 版本

- **v0.15.0** - Core 抽离；tag `winui-baseline`
- **v0.15.x** - 能力对齐与打磨；Core 测试与加固；PageUp/PageDown 瀑布流翻页等
- v0.14.7 - WinUI：Chrome 浮动 overlay、Load More 预加载、状态栏视频计数等
- v0.14.x - 视频 / Slideshow / 全屏 / EXIF / archive 等（详见 `CHANGELOG.md`）
- 更早版本见 `CHANGELOG.md`

## 许可

应用代码以仓库为准。捆绑 FFmpeg（LGPL 2.1+）与 LibVLC 遵循各自许可证。  
LGPL 全文：`src/IcedPicViewer.WinUI/License/ffmpeg-LGPL.txt`（构建后复制到输出目录）。
