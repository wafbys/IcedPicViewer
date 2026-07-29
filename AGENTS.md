# AI 协作指南 — IcedPicViewer

> **目标**：高效交付高质量代码。规则要实用，不是为约束而约束。

## 项目背景

本地媒体浏览器（图片 + 视频 + 压缩包展平）。

**Core、WinUI、Avalonia 三者平等**：没有「主交付 / 次要壳」之分。共享逻辑在 Core；每个 UI 工程都是完整产品入口，改行为要对齐语义，不能只维护其中一个。

| 路径 | 角色 |
|------|------|
| `src/IcedPicViewer.Core` | 平台无关库：模型/设置、`MediaCatalog`、`ArchiveHelper`、`DirectoryScanner`、`VideoFrameExtractor`、`IShellService` 等。**禁止**引用 WinUI / Avalonia。被两壳共同引用，本身与两壳同级维护 |
| `src/IcedPicViewer.WinUI` | Windows 原生 UI（WinUI 3 + WASDK 2.3，MSIX，**x64 only**）：图库/查看器、`MediaPlayerElement` 播放、幻灯片、全屏 chrome、`WH_KEYBOARD` 键盘、DI Hosting |
| `src/IcedPicViewer.Avalonia` | 跨平台 UI（Win / macOS / Linux）：Fluent 浅色、图库/查看器、LibVLC 软渲染（`VlcBitmapSurface`）、幻灯片、全屏热区 chrome |
| `tests/IcedPicViewer.Core.Tests` | Core 的 xUnit 测试（只引 Core）。已进 `IcedPicViewer.slnx` |
| FFmpeg | **二进制不进 git**。`tools/Fetch-FFmpegNatives.*` → `src/native/ffmpeg/{rid}/`；Win 可镜像到 WinUI `runtimes/win-x64/native`。运行时：`IPV_FFMPEG_ROOT` → 输出目录 → 系统路径。`FFmpegBootstrap` 成功后 `av_log_set_level(AV_LOG_ERROR)`（Core 抽帧，两壳共用） |
| 视频播放 | **WinUI**：`MediaPlayerElement` + 系统编解码；部分容器 FFmpeg remux（`VideoMetadataService`）。**Avalonia**：LibVLC（Win/Mac NuGet；Linux 系统 libvlc 或 `IPV_LIBVLC_ROOT`）。**禁止** `LibVLCSharp.Avalonia.VideoView` |

- tag `winui-baseline`：迁移前纯 WinUI 快照，仅供历史 diff，**不是**「WinUI 已冻结」。
- **MasonryPanel**（WinUI / Avalonia 各有实现）：默认 **3 列铺满**，非虚拟化，勿擅自改成虚拟化列表。
- **混合加载**（产品语义两壳一致）：边扫边灌到 200 停 → Load More / 滚底再灌。
- **不**自动恢复上次打开的文件夹（两壳）。
- **VM 布局**：
  - WinUI：`GalleryViewModel` + `ImageViewModel`；项 `MediaItem` / `ImageItem` / `VideoItem`。
  - Avalonia：`MainViewModel` partial（`.cs` / `.Gallery` / `.SlideshowViewer`）；项 `GalleryItemViewModel`。
- CommunityToolkit.Mvvm（两壳）。
- 改共享行为动 **Core**，并确认 **WinUI 与 Avalonia** 仍符合约定；改壳特有交互只动对应工程。反对过度设计。

## 构建与运行

### Core

```powershell
dotnet build src/IcedPicViewer.Core/IcedPicViewer.Core.csproj -c Debug
dotnet test tests/IcedPicViewer.Core.Tests/IcedPicViewer.Core.Tests.csproj -c Debug
```

改 scanner / archive / settings / catalog / layout / FFmpeg 抽帧等：build + 测试绿。

### WinUI（Windows x64）

```powershell
# 视频 FFmpeg DLL（不进 git；缺了 build 会 IPV001 警告）
./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64

$Platform = 'x64'
dotnet build src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug -p:Platform=$Platform
dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug -p:Platform=$Platform
dotnet publish src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Release -p:Platform=$Platform
```

- WASDK **2.3.x** ↔ 目标机 Windows App Runtime **2.3**（MSIX 拉 framework；跨主版本会启动失败）。
- WinApp CLI 经 `Microsoft.Windows.SDK.BuildTools.WinApp` 引入；控制台 “vX.Y is available” 时可升该包。
- ⚠️ **不要**直接双击 `bin\...\IcedPicViewer.exe`——MSIX 需 package identity，直接跑会 `REGDB_E_CLASSNOTREG`。必须用 `dotnet run`。
- 平台固定 **x64**（`Platform=x64`）；solution 里 WinUI 仅映射 x64。

### Avalonia（Win / macOS / Linux）

```powershell
dotnet build src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
dotnet run --project src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
```

全屏 chrome：仅顶/底热区显示工具栏；**翻图不得 PeekChrome**；Opacity 淡入淡出。

视频：Linux 需本机 VLC/libvlc；macOS 用 NuGet Mac 包；FFmpeg 见 `src/native/ffmpeg/README.md`。

### 整 solution

```powershell
dotnet test IcedPicViewer.slnx -c Debug
```

含 Core 测试；WinUI / Avalonia UI 仍以手动验关键路径为主。

## 核心原则

1. **用户意图优先**——规则和体验冲突时说出来讨论。
2. **先查现有代码再动手**——能复用就复用，能小改就不大改。
3. **三工程平等**——Core / WinUI / Avalonia 无主次。任务落在哪就改哪；触及共享语义时两边 UI 都要核对。
4. **验证分层**——
   - **Core**：有真实痛点就写测；`tests/IcedPicViewer.Core.Tests`；`dotnet test` 绿。
   - **WinUI / Avalonia**（含图库 pipeline、播放）：不堆 ViewModel 单测；`dotnet build` 0 warnings + **手动验关键路径**（改了哪边验哪边；共享语义两边都验）。
   - 禁止为覆盖率写壳测试（如只测 `Math.Clamp`）；禁止测试写真实 `%LocalAppData%` 配置——用 temp 路径（见 `JsonSettingsService(string settingsPath)`）。
5. **主动暴露权衡**——需求模糊或有风险直接说，不要硬做。
6. **Commit 用中文**。

## 硬性规则

### 通用
- `dotnet build` 0 errors / 0 warnings —— 底线（改到的工程都要干净）。
- 改动 Core 行为时：`dotnet test tests/IcedPicViewer.Core.Tests` 通过。
- Commit message 中文。
- IDisposable 必须正确释放（FileSystemWatcher、CancellationTokenSource、Stream）。
- 禁止空 catch 吞异常，至少 `Trace.TraceError` 记录。

### WinUI 禁忌
- 不用 `Window.Current`、`CoreDispatcher` 等已废弃 API。
- 大列表优先虚拟化（但本项目 MasonryPanel 除外）。
- **ThemeResource brush 名只认 Fluent 2 命名**。`SubtleFillColorSecondaryBrush` / `SolidBackgroundFillColorBaseBrush` / `ControlStrokeColorDefaultBrush` / `CardStrokeColorDefaultBrush` / `LayerFillColorDefaultBrush` 等真实存在；Fluent 1 旧名（`SystemControlBackgroundChromeMediumLowBrush` 等）在 WinAppSDK 2.2+ 全不存在，build 不报但运行时 `XamlParseException`。
- **键盘事件只用 WH_KEYBOARD hook**（详见下方"键盘导航"章节）。不用 `AddHandler(KeyDownEvent)` / `KeyboardAccelerator` / `SetWindowSubclass`。

### Gallery 扫描/加载 pipeline 不变量

**产品语义（WinUI 与 Avalonia 必须一致）**：**边扫边灌到 200 张停**。

#### 统一术语（两壳相同代码名，禁止另起一套）

| 术语 | 代码名 | 含义 |
|------|--------|------|
| 图库集合 | `Items` | 已灌入瀑布流的媒体项 |
| 当前项 | `SelectedItem` | 查看器/选中项（WinUI 查看器 VM 与 Avalonia 同名） |
| 当前文件夹 | `FolderPath` | 正在浏览的目录 |
| 剩余队列 | `_remainingSources` + `_remainingLock` | 已发现未入 `Items` 的 `ImageSource` |
| 发现数 | `DiscoveredCount` | **扫描期唯一写源**：`IngestScanBatch` 绝对赋值 `DiscoveredCount = discovered`（禁止再叠加 Progress 计数）。监视器增删可 `++`/`--` |
| 加载态 | `LoadingState` + `IsScanning` | Core：`Idle`/`Scanning`/`Error`/`Completed`；失败用 `Error` 非 `Completed` |
| 自动灌 / Load More 块 | `PageSize = 200` | drain gate：`Items.Count < PageSize` |
| scan 每轮 | `ScanPageSize = 30` | |
| batch | `ScanBatchSize = 100` / `ScanBatchMs = 50` | |
| flush 链 | `FlushScanBatch` → `IngestScanBatch` → `DrainPageFillAsync` | |
| Load More | `LoadMoreAsync` / `LoadMoreCommand` + `CanLoadMore` / `IsLoadingMore` | |
| 缩略图 | `LoadThumbnailAsync` + `_thumbnailLoadSemaphore`（`ThumbConcurrency = 6`） | |
| 状态文案 | `StatusText` + `UpdateStatus` | **一律**经 Core `GalleryStatusFormatter`（**中文**）；禁止两壳各写一套字符串 |
| 对话框 / 工具栏 | `UiCopy` | **中文**公共文案（删除确认、打开文件夹、加载更多…）；壳 XAML 与 VM 标签对齐 |
| 查看器已加载数 | WinUI：`ItemCount`（=`Items.Count`）；Avalonia：直接绑 `Items.Count` | 勿与 `DiscoveredCount` 混淆 |

#### 仅平台差异（技术栈，不是第二套产品词）

| 点 | WinUI | Avalonia |
|----|-------|----------|
| VM 拆分 | `GalleryViewModel` + `ImageViewModel` | `MainViewModel` partials |
| 项类型 | `MediaItem` / `ImageItem` / `VideoItem` | `GalleryItemViewModel` |
| drain 取尺寸 | `LoadNextPageAsync` + `_sizeFetchSemaphore` | 随缩略图/解码 |
| UI marshal | `DispatcherQueue.TryEnqueue` | `Dispatcher.UIThread` |
| 解码 / 播放 | WIC / `MediaPlayerElement` | ImageSharp / LibVLC |
| 键盘 | `WH_KEYBOARD` | 窗口 `KeyDown` |
| 扫描路径提示 | `CurrentScanningPath`（可选 UI） | 可无 |

**共用流程**：

```
RunScanAndBatchAsync ── ScanBatchSize/ScanBatchMs ── FlushScanBatch
    → IngestScanBatch(_remainingSources, DiscoveredCount)
    → DrainPageFillAsync（Items.Count < PageSize, 每轮 ScanPageSize）
    → LoadThumbnailAsync（_thumbnailLoadSemaphore）
```

**不变量**：scanner 在 worker；`batchStartTick`；`_pageFillInFlight` ≠ `IsLoadingMore`；drain gate `Items.Count < PageSize`；缩略图经 UI 线程回写；切目录取消 CTS。

**禁止**：`Images` / `AutoCap` / `_remainingFilePaths` / `CurrentFolderPath` / `IsBusy`（作加载态）/ `LoadMoreImages*` / `CurrentImage` / Gallery 级 `TotalCount` 等旧名重现。

### 键盘导航（WinUI：`WH_KEYBOARD` thread-scope hook）

> **仅 WinUI。** Avalonia 用窗口 `KeyDown` / 命令绑定，不适用本节。

最终方案在 `src/IcedPicViewer.WinUI/MainWindow.xaml.cs`（搜索 `WH_KEYBOARD` / `InstallKeyboardHook` / `KeyboardHookProc` / `UnhookWindowsHookEx`）。

**为什么继续用 WH_KEYBOARD（不要换）**：
- `Microsoft.UI.Input.InputKeyboardSource.GetForWindowId` 在 WASDK 文档里仍是 **Experimental**（仅 experimental moniker），**不能**当作生产键盘方案替换 WH_KEYBOARD。
- XAML `KeyDown` / `AddHandler(KeyDownEvent)` / `KeyboardAccelerator` 在 MSIX 下对**不依赖焦点**的查看器快捷键不可靠（焦点在 `Frame.Navigate` 后不稳定；Accelerator 文档写 global 仍常依赖焦点启动路由）。
- `SetWindowSubclass` 拿到的 HWND 往往是 XAML island 子窗，不是真正收键盘的顶层 window——注册成功但 `WM_KEYDOWN` 不来。
- 因此生产路径固定为 thread-scope `WH_KEYBOARD`；窗口关闭时在 `AppWindow_Closing` 里 `UnhookWindowsHookEx` 清理（`_hookHandle != IntPtr.Zero` 才卸，失败 `Trace.TraceError`，禁止空 catch）。

**机制**：`SetWindowsHookEx(WH_KEYBOARD, ..., dwThreadId=GetCurrentThreadId())` 装到 UI thread message queue → 收到所有键盘事件（不依赖焦点/HWND/XAML 路由）→ 回调 `TryEnqueue` 投递 `HandleViewerKey`。

**3 个关键实现细节**：
1. hook callback 同步 return（`TryEnqueue` 投递，await 链从 work item 开始跑）
2. try-catch 双层防护，不允许异常 reach OS
3. `wParam`/`lParam` 用 `unchecked((int)IntPtr)` cast（不用 `IntPtr.ToInt32()`，Win11 25H2 下 64-bit 高 32 位有 garbage 会抛 `OverflowException`）

**易错点**：`HandleViewerKey` 从 `viewer.ViewModel` 拿 VM，不是 `viewer.DataContext`（`ImageViewerView` 用 `x:Bind`，DataContext 始终是 null）。

**调试**：键盘 hook 问题可通过 crash.log（未处理异常）和 Trace 输出诊断。

## 已知坑

### WinUI

#### 1. `App.SetMainWindow` 必须早于 ctor 内 Navigate

`MainWindow` ctor 第一行就调 `SetMainWindow(this)`，再走 `InitializeComponent` + Navigate。否则 page ctor 内读 `App.MainWindow` 是 null——订阅静默跳过。

```csharp
// MainWindow.xaml.cs ctor
public MainWindow() {
    if (Application.Current is App app) app.SetMainWindow(this);  // 第一行
    InitializeComponent();
    ...
}
```

#### 2. `XamlControlsResources` 不能删

`App.xaml` 必须显式 merge `<controls:XamlControlsResources />`。删了会导致 `TitleBar` 等控件 default style 找不到 `TabViewButtonBackground` 等 theme resource，启动时 `XamlParseException`。

#### 3. `x:Bind` 页面的 VM 在 `page.ViewModel` 字段

`{x:Bind}` 风格的 page 不设 `DataContext`。拿 VM 走 `page.ViewModel` property，不要用 `page.DataContext is XxxViewModel`（永远 false）。

#### 4. 直接跑 unpackaged `.exe` 会炸

MSIX package identity 缺失 → `REGDB_E_CLASSNOTREG`。只用 `dotnet run` / 正确部署路径。

### Avalonia

#### 1. 禁止 `LibVLCSharp.Avalonia.VideoView`

Avalonia 12 会 `MissingMethodException`。视频表面用 `VlcBitmapSurface`（软渲染回调）。

#### 2. 缩略图 / FullImage 必须 UI 线程赋值

worker 解码后 `Dispatcher.UIThread.InvokeAsync` 再写 `GalleryItemViewModel` 绑定属性。

#### 3. Linux 视频依赖系统 libvlc

无 NuGet 自带 Linux LibVLC；缺库时播放失败，缩略图仍可走 FFmpeg。

## 常见 AI 易犯错误

- 把 Core / WinUI / Avalonia 分主次（「主交付」「对照壳」）——三者平等。
- 只改一边 UI 却假设另一边自动对齐（共享语义两边都看）。
- **非平台概念起两套名**（如 `Images`/`Items`、`AutoCap`/`PageSize`）——应用「统一术语」表。
- 把 WinUI 当只读历史而不修 bug / 不同步 pipeline。
- 看到问题就自己发明新抽象或新服务。
- 为了"最佳实践"大幅改动用户明确不想改的地方（如 MasonryPanel）。
- 写了一堆代码才发现项目里早有类似实现。
- 过度追求零 warning，浪费时间在不紧要的清理上。
- 用户要"简单方案"时还在推"更正确但更复杂"的设计。

---

**最后**：**Core = WinUI = Avalonia**；WinUI/Avalonia **除平台差异外术语一致**。规则服务**高效 + 靠谱 + 尊重真实需求**。觉得某条在当前任务中不合适，随时说出来一起调整。
