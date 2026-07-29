# AI 协作指南 — IcedPicViewer

> **目标**：高效交付高质量代码。规则要实用，不是为约束而约束。

## 项目背景

本地媒体浏览器（图片 + 视频 + 压缩包展平）。**共享 Core + 两套一等公民 UI 壳**：

| 路径 | 角色 |
|------|------|
| `src/IcedPicViewer.Core` | 平台无关：模型/设置、`MediaCatalog`、`ArchiveHelper`、`DirectoryScanner`、`VideoFrameExtractor`、`IShellService` 等。**禁止**引用 WinUI / Avalonia |
| `src/IcedPicViewer.Avalonia` | **跨平台壳**（Win / macOS / Linux）：Fluent 浅色 UI、图库/查看器、**LibVLC** 软渲染（`VlcBitmapSurface`）、幻灯片、全屏热区 chrome |
| `src/IcedPicViewer.WinUI` | **Windows 原生壳**（WinUI 3 + WASDK 2.3，MSIX，**x64 only**）：图库/查看器、**MediaPlayerElement** 播放、幻灯片、全屏 chrome、`WH_KEYBOARD` 键盘。功能继续演进，与 Avalonia **并行维护**，不是只读基线 |
| `tests/IcedPicViewer.Core.Tests` | Core 单元/集成测试（xUnit）。只引用 Core，不拉任一 UI 壳。已进 `IcedPicViewer.slnx` |
| FFmpeg | **二进制不进 git**。`tools/Fetch-FFmpegNatives.*` → `src/native/ffmpeg/{rid}/`；Win 会镜像到 WinUI `runtimes/win-x64/native`。运行时：`IPV_FFMPEG_ROOT` → 输出目录 → 系统路径。`FFmpegBootstrap` 成功后 `av_log_set_level(AV_LOG_ERROR)`（两壳共用 Core 抽帧） |
| 视频播放 | **Avalonia**：LibVLC（Windows/Mac NuGet；Linux 系统 libvlc 或 `IPV_LIBVLC_ROOT`）。**禁止** `LibVLCSharp.Avalonia.VideoView`。**WinUI**：`MediaPlayerElement` + 系统编解码；部分容器走 FFmpeg remux（见 `VideoMetadataService`） |

- 基线 tag：`winui-baseline`（迁移前最后纯 WinUI 快照，便于 diff；**当前** WinUI 仍在 `src/IcedPicViewer.WinUI` 活跃开发）。
- **MasonryPanel** 瀑布流（两壳各有控件实现）：默认 **3 列铺满**，非虚拟化，勿擅自改成虚拟化列表。
- **混合加载**（两壳同一产品语义）：边扫边灌到 200 停 → Load More / 滚底再灌。
- **不**自动恢复上次打开的文件夹（两壳）。
- **VM 布局**：
  - Avalonia：`MainViewModel` partial — `MainViewModel.cs` + `MainViewModel.Gallery.cs` + `MainViewModel.SlideshowViewer.cs`；项模型 `GalleryItemViewModel`。
  - WinUI：`GalleryViewModel` + `ImageViewModel`（查看器）；项模型 `MediaItem` / `ImageItem` / `VideoItem`；DI Hosting。
- CommunityToolkit.Mvvm（两壳）。
- 反对为了"正确"而过度设计。改共享行为优先动 **Core**；改交互/壳特有路径分别动对应工程。

## 构建与运行

### Avalonia（跨平台）

```powershell
dotnet build src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
dotnet run --project src/IcedPicViewer.Avalonia/IcedPicViewer.Avalonia.csproj -c Debug
```

全屏 chrome：仅顶/底热区显示工具栏；**翻图不得 PeekChrome**；Opacity 淡入淡出。

跨平台视频：Linux 需本机 VLC/libvlc；macOS 用 NuGet Mac 包；FFmpeg 见 `src/native/ffmpeg/README.md`。

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

### Core 测试

```powershell
dotnet test tests/IcedPicViewer.Core.Tests/IcedPicViewer.Core.Tests.csproj -c Debug
# 或整 solution（含非测试项目 build）
dotnet test IcedPicViewer.slnx -c Debug
```

改 Core 纯逻辑（scanner / archive / settings / catalog / layout 等）时：`dotnet build` **且** 相关测试通过。UI / 播放仍以手动验关键路径为主。

## 核心原则

1. **用户意图优先**——规则和体验冲突时说出来讨论。
2. **先查现有代码再动手**——能复用就复用，能小改就不大改。
3. **验证分层**——
   - **Core**：有真实痛点就写测；默认只扩 `tests/IcedPicViewer.Core.Tests`。改完 `dotnet test` 绿。
   - **Avalonia / WinUI / VLC / 图库 pipeline**：不堆 ViewModel 单测；`dotnet build` 0 warnings + **手动验关键路径**。
   - 禁止为覆盖率写壳测试（如只测 `Math.Clamp`）；禁止测试写真实 `%LocalAppData%` 配置——用 temp 路径（见 `JsonSettingsService(string settingsPath)`）。
4. **主动暴露权衡**——需求模糊或有风险直接说，不要硬做。
5. **Commit 用中文**。

## 硬性规则

### 通用
- `dotnet build` 0 errors / 0 warnings —— 底线。
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

**产品语义（两壳必须一致）**：**边扫边灌到 200 张停**。scanner 在 worker thread yield → 50ms / ≤100 条 batch flush 到 UI → drain 灌到 200 停；scanner 可继续累计发现数，但不自动再灌；用户 Load More / 滚底再取。

**命名对照**（改代码时按壳找符号，**禁止**跨壳复制粘贴符号名）：

| 概念 | Avalonia `MainViewModel.Gallery` | WinUI `GalleryViewModel` |
|------|----------------------------------|--------------------------|
| 集合 | `Items` | `Images` |
| 剩余队列 | `_remaining` | `_remainingFilePaths` |
| 发现/入队计数 | `DiscoveredCount` | `DiscoveredCount`（节流上报）+ `TotalCount`（入队累计） |
| 加载态 | `IsBusy` 等 | `LoadingState`（`Scanning` / `Completed` …） |
| 自动灌上限 | `AutoCap = 200` | `PageSize = 200`（兼 Load More 块大小） |
| Load More 块 | `PageSize = 200` | `PageSize = 200` |
| scan 每轮灌入 | `ScanPageSize = 30` | `ScanPageSize = 30` |
| batch 常量 | `ScanBatchSize=100` / `ScanBatchMs=50` | `ScanBatchSize=100` / 时间阈值 `50` |
| flush API | `FlushBatch` → `Dispatcher.UIThread.Post` | `FlushScanBatch` → `TryEnqueue(IngestScanBatch)` |
| drain | `DrainPageFillAsync` 内直接 `Items.Add` | `DrainPageFillAsync` → `LoadNextPageAsync` |
| 缩略图 | `_thumbSemaphore`(6) + `LoadThumbnailFireAndForgetAsync` | `_thumbnailLoadSemaphore`(6) + `LoadThumbnailAsync`；尺寸另有 `_sizeFetchSemaphore`(6) + `GetImageSizeAsync` |
| UI marshal | `Dispatcher.UIThread` | `DispatcherQueue.TryEnqueue` |

**Avalonia 流程**（`MainViewModel.Gallery.cs`）：

```
scanner (worker, Task.Run(RunScanAndBatchAsync))
    │ yield source
    ▼
RunScanAndBatchAsync ── ≤100 或 50ms batchStartTick ── FlushBatch
    │ Dispatcher.UIThread.Post
    ▼
FlushBatch ── _remaining.AddRange + DiscoveredCount ── DrainPageFillAsync
    │ _pageFillInFlight 单消费者
    ▼
DrainPageFillAsync ── ≤ScanPageSize(30) ── Items.Add + LoadThumbnailFireAndForgetAsync
    │ 直到 Items.Count ≥ AutoCap(200) 或 queue 空
    ▼
_thumbSemaphore(6) ── AvaloniaImageLoader ── UI 回写 ApplyThumbnail / IsThumbnailLoading
```

**WinUI 流程**（`GalleryViewModel.cs`）：

```
scanner (worker, Task.Run(RunScanAndBatchAsync))
    │ yield source
    ▼
RunScanAndBatchAsync ── ≤100 或 50ms batchStartTick ── FlushScanBatch
    │ DispatcherQueue.TryEnqueue
    ▼
IngestScanBatch ── _remainingFilePaths.AddRange + TotalCount++ ── DrainPageFillAsync
    │ 仅当 !_pageFillInFlight && Images.Count < PageSize
    ▼
DrainPageFillAsync ── await LoadNextPageAsync(≤ScanPageSize) ── Images.Add
    │ 直到 Images.Count ≥ PageSize(200) 或 queue 空
    ▼
LoadNextPageAsync ── _sizeFetchSemaphore 取尺寸 ── LoadThumbnailAsync(_thumbnailLoadSemaphore)
    ── TryEnqueue 回写 Thumbnail / IsThumbnailLoading
```

**不变量清单**（语义共用；符号见表）：

| 规则 | 说明 |
|------|------|
| scanner 永远在 worker thread | `Task.Run` 包住整个 `await foreach`，yield 不走 UI 同步上下文 |
| `batchStartTick` 锚点 | batch 空时设 `batchStartTick = now`，保证第一张 source 50ms 内必 flush |
| 单一 consumer drain | `_pageFillInFlight` 保证同时只有一个 drain 循环 |
| `_pageFillInFlight` ≠ `IsLoadingMore` | 自动灌用前者；Load More 按钮用后者 |
| `ScanPageSize=30` / 自动灌 200 / Load More 200 | scan-time 小块避免 layout 卡死 |
| drain gate | Avalonia：`Items.Count < AutoCap`；WinUI：`Images.Count < PageSize` |
| 扫描完成等待（WinUI） | `LoadDirectoryAsync` 轮询 `!_pageFillInFlight && !IsLoadingMore` 再 `LoadingState=Completed`；**不**要求剩余队列为空 |
| 缩略图 / 尺寸 6 路限流 | 必须 semaphore；WinUI 的 `GetImageSizeAsync` 尤忌全并发（WinRT STA） |
| 缩略图状态回写 | 必须经 UI dispatcher；禁止 worker 直接写绑定属性 |
| 切目录取消 | 旧 cts `Cancel`+`Dispose`；flush/drain/thumb 检查 token |

**绝对不要做的事**（两壳）：
- 把 `await foreach` 同步吞光再统一 Load
- 加回 page fill timer
- 把 `ScanPageSize` 调到 150
- 缩略图/尺寸探测无 semaphore 全并发
- worker 线程直接写绑定属性
- 只改一壳的 hybrid 语义却不改另一壳（产品行为应对齐；符号可不同）

**取消语义**：切目录时旧 cts `Cancel()`+`Dispose()` → scanner 退出 await foreach → 旧 batch 检查 token 直接 return → drain / LoadMore 内加项前检查 token 早退。

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

- 只改 Avalonia 或只改 WinUI，却假设另一壳自动对齐（共享语义要两边都看）。
- 把 WinUI 当「只读对照」而不修 bug / 不同步 pipeline 不变量。
- 看到问题就自己发明新抽象或新服务。
- 为了"最佳实践"大幅改动用户明确不想改的地方（如 MasonryPanel）。
- 写了一堆代码才发现项目里早有类似实现。
- 过度追求零 warning，浪费时间在不紧要的清理上。
- 用户要"简单方案"时还在推"更正确但更复杂"的设计。

---

**最后**：两壳并行、Core 共享；规则服务**高效 + 靠谱 + 尊重真实需求**。觉得某条在当前任务中不合适，随时说出来一起调整。
