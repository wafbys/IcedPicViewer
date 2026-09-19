# AI 协作指南 — IcedPicViewer

> **目标**：高效交付高质量代码。规则要实用，不是为约束而约束。

## 项目背景

本地媒体浏览器（图片 + 视频 + 压缩包展平）。

**Core（平台无关库）+ WinUI（Windows 原生壳）双工程**：Core 承载全部共享逻辑，WinUI 是唯一产品入口。Core 与 WinUI 同级维护——不存在「谁只是附属」的说法。

| 路径 | 角色 |
|------|------|
| `src/IcedPicViewer.Core` | 平台无关库（`net11.0`）：模型/设置、`MediaCatalog`、`ArchiveHelper`、`DirectoryScanner`、`VideoFrameExtractor`、`IShellService` 等。**禁止**引用 WinUI。被 WinUI 引用 |
| `src/IcedPicViewer.WinUI` | Windows 原生 UI（WinUI 3 + WASDK 2.5，.NET 11，MSIX，**x64 only**）：图库/查看器、`MediaPlayerElement` 播放、幻灯片、全屏 chrome、`WH_KEYBOARD` 键盘、DI Hosting |
| `tests/IcedPicViewer.Core.Tests` | Core 的 xUnit 测试（只引 Core）。已进 `IcedPicViewer.slnx` |
| FFmpeg | **二进制不进 git**。`tools/Fetch-FFmpegNatives.ps1 -Rid win-x64` → `src/native/ffmpeg/win-x64/`；构建时 `CopyFFmpegDllsToOutDir` 拷到输出根（即包安装根，`LoadLibrary` 不搜子目录）。运行时：`IPV_FFMPEG_ROOT` → 输出目录 → 系统路径。`FFmpegBootstrap` 成功后 `av_log_set_level(AV_LOG_ERROR)` |
| 视频播放 | `MediaPlayerElement` + 系统编解码；部分容器 FFmpeg remux（`VideoMetadataService`）；LibVLC 软渲染回退（`VlcImageSurface`，无 HWND VideoView） |

- tag `winui-baseline`：历史快照，仅供 diff，**不是**「WinUI 已冻结」。
- **MasonryPanel**：默认 **3 列铺满**，非虚拟化，勿擅自改成虚拟化列表。
- **混合加载**：边扫边灌到 200 停 → Load More / 滚底再灌。
- **不**自动恢复上次打开的文件夹。
- **VM 布局**：`GalleryViewModel` + `ViewerViewModel` + 页 `ViewerView`；项 `MediaItem`:`IMediaEntry`（`ImageItem`/`VideoItem`）；加载 `IMediaLoader`/`MediaLoader`。
- CommunityToolkit.Mvvm。
- 改共享行为动 **Core**，并确认 **WinUI** 仍符合下方契约；改壳特有交互只动 WinUI。反对过度设计。

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
```

- WASDK **2.5.x** ↔ 目标机 Windows App Runtime **2.5**（MSIX 拉 framework；跨主版本会启动失败）。
- 目标框架 **`net11.0-windows10.0.26100.0`**（SDK 版本由仓库根 `global.json` 固定）。**不要**给 `Microsoft.Extensions.Caching.Abstractions` / `Configuration.Abstractions` / `DependencyInjection.Abstractions` / `Diagnostics.Abstractions` / `FileProviders.Abstractions` / `Hosting.Abstractions` / `Logging.Abstractions` / `Options` / `Primitives` 加 `PackageReference`——.NET 11 起这 9 个已在共享框架内，显式引用会 `NU1510`，且版本错配会在运行期抛 `MissingMethodException`。`Microsoft.Extensions.Hosting` 仍需显式引用，版本必须与共享框架同号（当前 `11.0.0-rc.1.*`；GA 后换 `11.0.0`）。
- WinApp CLI 经 `Microsoft.Windows.SDK.BuildTools.WinApp` 引入；控制台 “vX.Y is available” 时可升该包。
- ⚠️ **不要**直接双击 `bin\...\IcedPicViewer.exe`——MSIX 需 package identity，直接跑会 `REGDB_E_CLASSNOTREG`。必须用 `dotnet run`。
- 平台固定 **x64**（`Platform=x64`）；solution 里 WinUI 仅映射 x64。
- **本项目不做发布打包**，只用 `dotnet run` 启动（它注册调试包身份）。不要引入 `.msix` 打包流程，也不要引入未打包 / 绿色版的 `WindowsPackageType=None` 或 `SelfContained` 发布路径。

#### 数据与持久化

- 全部可变状态（`settings.json`、`window_settings.txt`、`crash.log`、`TempVideo\`）都在 **`%LOCALAPPDATA%\IcedPicViewer`**。
- **新增任何持久化路径必须走 Core `AppDataPaths`**（`Root` / `SettingsFile` / `TempVideoDir` / `CrashLogFile`，或在其上 `Path.Combine`），不要再手写 `LocalApplicationData`。它没有环境变量或 marker 覆盖机制，就是唯一来源。
- Mica 在 `MainWindow.xaml` 里以 `<MicaBackdrop />` 直接声明（有 package identity，正常生效）。
- About 页 LGPL 链接走 `ms-appx:///License/ffmpeg-LGPL.txt`，并有 `<exe>\License\ffmpeg-LGPL.txt` 的 loose-file 回退；改该页时别把 fallback 删了。

### 整 solution

```powershell
dotnet test IcedPicViewer.slnx -c Debug
```

含 Core 测试；WinUI UI 仍以手动验关键路径为主。

## 核心原则

1. **用户意图优先**——规则和体验冲突时说出来讨论。
2. **先查现有代码再动手**——能复用就复用，能小改就不大改。
3. **Core 与 WinUI 同级**——共享逻辑在 Core，壳特有交互在 WinUI。任务落在哪就改哪；触及共享语义时两边都要核对。
4. **验证分层**——
   - **Core**：有真实痛点就写测；`tests/IcedPicViewer.Core.Tests`；`dotnet test` 绿。
   - **WinUI**（含图库 pipeline、播放）：不堆 ViewModel 单测；`dotnet build` 0 warnings + **手动验关键路径**。
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
- **ThemeResource brush 名只认 Fluent 2 命名**。`SubtleFillColorSecondaryBrush` / `SolidBackgroundFillColorBaseBrush` / `ControlStrokeColorDefaultBrush` / `CardStrokeColorDefaultBrush` / `LayerFillColorDefaultBrush` 等真实存在；Fluent 1 旧名（`SystemControlBackgroundChromeMediumLowBrush` 等）在 WinAppSDK 2.2+（本项目 2.5）全不存在，build 不报但运行时 `XamlParseException`。
- **键盘事件只用 WH_KEYBOARD hook**（详见下方"键盘导航"子章节）。不用 `AddHandler(KeyDownEvent)` / `KeyboardAccelerator` / `SetWindowSubclass`。

#### 键盘导航（`WH_KEYBOARD` thread-scope hook）

最终方案在 `src/IcedPicViewer.WinUI/MainWindow.xaml.cs`（搜索 `WH_KEYBOARD` / `InstallKeyboardHook` / `KeyboardHookProc` / `UnhookWindowsHookEx`）。

**为什么继续用 WH_KEYBOARD（不要换）**：
- `Microsoft.UI.Input.InputKeyboardSource.GetForWindowId` 在 WASDK 文档里仍是 **Experimental**（仅 experimental moniker），**不能**当作生产键盘方案替换 WH_KEYBOARD。2026-09 复核 WASDK 2.5.1 / `windows-app-sdk-2.0` 稳定视图：该方法签名仍带 `[Windows.Foundation.Metadata.Experimental]`，且文档页会从稳定 moniker 回退到 `2.0-experimental`。
- XAML `KeyDown` / `AddHandler(KeyDownEvent)` / `KeyboardAccelerator` 在 MSIX 下对**不依赖焦点**的查看器快捷键不可靠（焦点在 `Frame.Navigate` 后不稳定；Accelerator 文档写 global 仍常依赖焦点启动路由）。
- `SetWindowSubclass` 拿到的 HWND 往往是 XAML island 子窗，不是真正收键盘的顶层 window——注册成功但 `WM_KEYDOWN` 不来。
- 因此生产路径固定为 thread-scope `WH_KEYBOARD`；窗口关闭时在 `AppWindow_Closing` 里 `UnhookWindowsHookEx` 清理（`_hookHandle != IntPtr.Zero` 才卸，失败 `Trace.TraceError`，禁止空 catch）。

**机制**：`SetWindowsHookEx(WH_KEYBOARD, ..., dwThreadId=GetCurrentThreadId())` 装到 UI thread message queue → 收到所有键盘事件（不依赖焦点/HWND/XAML 路由）→ 回调 `TryEnqueue` 投递 `HandleViewerKey`。

**3 个关键实现细节**：
1. hook callback 同步 return（`TryEnqueue` 投递，await 链从 work item 开始跑）
2. try-catch 双层防护，不允许异常 reach OS
3. `wParam`/`lParam` 用 `unchecked((int)IntPtr)` cast（不用 `IntPtr.ToInt32()`，Win11 25H2 下 64-bit 高 32 位有 garbage 会抛 `OverflowException`）

**易错点**：`HandleViewerKey` 从 `viewer.ViewModel` 拿 VM，不是 `viewer.DataContext`（`ViewerView` 用 `x:Bind`，DataContext 始终是 null）。

**调试**：键盘 hook 问题可通过 crash.log（未处理异常）和 Trace 输出诊断。

### Gallery 扫描/加载 pipeline 不变量

**产品语义**：**边扫边灌到 200 张停**。

#### 统一术语（Core 与 WinUI 共用同一套代码名，禁止另起一套）

| 术语 | 代码名 | 含义 |
|------|--------|------|
| 媒体定位 | `MediaRef` | 文件或压缩包条目（**不是**平台图形 `ImageSource`） |
| 媒体种类 | `MediaKind` | `Image` / `Video` |
| 项契约 | `IMediaEntry` | `Id`/`Media`/`Name`/`IsVideo`/`FileSize`；WinUI 实现 = `MediaItem` |
| 图库集合 | `Items` | 已灌入瀑布流的媒体项 |
| 当前项 | `SelectedItem` | 查看器/选中项 |
| 当前文件夹 | `FolderPath` | 正在浏览的目录 |
| 剩余队列 | `_remainingSources` + `_remainingLock` | 已发现未入 `Items` 的 `MediaRef` |
| 发现数 | `DiscoveredCount` | **扫描期唯一写源**：`IngestScanBatch` 绝对赋值 `DiscoveredCount = discovered`（禁止再叠加 Progress 计数）。监视器增删可 `++`/`--` |
| 加载态 | `LoadingState` + `IsScanning` | Core：`Idle`/`Scanning`/`Error`/`Completed`；失败用 `Error` 非 `Completed` |
| 自动灌 / Load More 块 | `PageSize = 200` | drain gate：`Items.Count < PageSize` |
| scan 每轮 | `ScanPageSize = 30` | |
| batch | `ScanBatchSize = 100` / `ScanBatchMs = 50` | |
| flush 链 | `FlushScanBatch` → `IngestScanBatch` → `DrainPageFillAsync` | |
| Load More | `LoadMoreAsync` / `LoadMoreCommand` + `CanLoadMore` / `IsLoadingMore` | |
| 缩略图 | `LoadThumbnailAsync` + `_thumbnailLoadSemaphore`（`ThumbConcurrency = 6`） | |
| 状态文案 | `StatusText` + `UpdateStatus` | **一律**经 Core `GalleryStatusFormatter`（**中文**）；禁止另写一套字符串 |
| 对话框 / 工具栏 | `UiCopy` | **中文**公共文案（删除确认、打开文件夹、加载更多…）；XAML 与 VM 标签对齐 |
| 查看器已加载数 | `ItemCount`（`x:Bind` 不能绑 `Items.Count`，故镜像一个 `int` 属性） | 勿与 `DiscoveredCount` 混淆 |

#### 仅平台实现细节

以下为平台 API 造成的实现选择，**不是**契约差异；改 Core 契约时不必同步这些。

| 点 | WinUI 实现 |
|----|-----------|
| 项实现 | `MediaItem` + `ImageItem`/`VideoItem`（实现 Core `IMediaEntry`） |
| 缩略图像素类型 | `BitmapImage` / `WinImageSource` |
| 加载器 | `IMediaLoader` / `MediaLoader` |
| drain 取尺寸 | `LoadNextPageAsync` + `_sizeFetchSemaphore` |
| UI marshal | `DispatcherQueue.TryEnqueue` |
| 解码 / 播放 | WIC / `MediaPlayerElement`（LibVLC 软渲染回退） |
| 键盘 | `WH_KEYBOARD` thread-scope hook |
| 扫描路径提示 | `CurrentScanningPath` |
| `ImageItem` 类名 | 表示 `MediaKind.Image` 的子类，不是历史误名 |

**共用流程**：

```
RunScanAndBatchAsync ── ScanBatchSize/ScanBatchMs ── FlushScanBatch
    → IngestScanBatch(_remainingSources, DiscoveredCount)
    → DrainPageFillAsync（Items.Count < PageSize, 每轮 ScanPageSize）
    → LoadThumbnailAsync（_thumbnailLoadSemaphore）
```

**不变量**：scanner 在 worker；`batchStartTick`；`_pageFillInFlight` ≠ `IsLoadingMore`；drain gate `Items.Count < PageSize`；缩略图经 UI 线程回写；切目录取消 CTS。

## 定稿架构（终点站）

以下为 **一致性工作的终点**。达到即收手；**不要**再为「更统一」做下列之外的重构。

### 已定稿（必须保持）

领域命名 / 项契约 / 加载器 / UI marshal 等见上方"统一术语表"。补充：

1. **Core 与 WinUI 同级**：共享逻辑在 Core，壳特有能力在 WinUI。
2. **中文 UI 文案**：状态栏 `GalleryStatusFormatter`；对话框/按钮 `UiCopy`；About `AboutCopy`；视频错误 `VideoPlaybackCopy`。
3. **展示格式**：`MediaDisplay`（大小/时长/像素/InfoLine）。
4. **图库 pipeline 语义**（边扫边灌 200）；`DiscoveredCount` 扫描期单写源。

### 收手判据

- `src/` 无下方「禁止」旧名（CHANGELOG / AGENTS 禁词列表除外）。
- Core `dotnet test` 绿；WinUI `dotnet build` 0/0。
- 新功能：产品语义与术语表对齐。

**禁止**：`Images` / `AutoCap` / `_remainingFilePaths` / `CurrentFolderPath` / `IsBusy`（作加载态）/ `LoadMoreImages*` / `CurrentImage` / Gallery 级 `TotalCount` / 领域模型 `ImageSource` / `ImageViewerView` / `ImageViewModel` / `GalleryItemViewModel` / `IImageLoader` 等旧名重现。

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

#### 2. `<controls:XamlControlsResources />` 不能删

`App.xaml` 必须显式 merge `<controls:XamlControlsResources />`。删了会导致 `TitleBar` 等控件 default style 找不到 `TabViewButtonBackground` 等 theme resource，启动时 `XamlParseException`。

#### 3. `x:Bind` 页面的 VM 在 `page.ViewModel` 字段

`{x:Bind}` 风格的 page 不设 `DataContext`。拿 VM 走 `page.ViewModel` property，不要用 `page.DataContext is XxxViewModel`（永远 false）。

#### 4. 直接跑 unpackaged `.exe` 会炸

MSIX package identity 缺失 → `REGDB_E_CLASSNOTREG`。只用 `dotnet run` / 正确部署路径。

#### 5. 缩略图 / FullImage 必须 UI 线程赋值

worker 解码后 `DispatcherQueue.TryEnqueue` 再写绑定属性。

#### 6. 删 culture 补丁已移除

不要再往 csproj 加自定义 target 去删输出目录的 satellite culture 文件夹。官方机制是 `<SatelliteResourceLanguages>`（空值 = 不保留任何语言的 satellite 程序集），已在工程里配置。

#### 7. 本项目不打包；若将来要 .msix 必须带 `GenerateAppxPackageOnBuild=true`

只用 `dotnet run` 启动，不做发布打包。若将来确有需要：裸 `dotnet publish -c Release -p:Platform=x64` **不产出 MSIX**——只生成 layout 和 `IcedPicViewer.build.appxrecipe`。必须加 `-p:GenerateAppxPackageOnBuild=true`，产物落在 `<project>\AppPackages\`（含 `Microsoft.WindowsAppRuntime.<major>.msix` 依赖包）。另：缺 `mspdbcmf.exe`（未装 Windows SDK / 调试工具）时会出一条 symbol 包警告，对旁加载无影响。

## 常见 AI 易犯错误

- 把 Core 当"壳的附属"而忽略其契约（Core 与 WinUI 同级）。
- 只改 WinUI 却把共享语义写死在壳里（该进 Core 的进 Core）。
- **非平台概念起两套名**（如 `Images`/`Items`、`AutoCap`/`PageSize`）——应用「统一术语」表。
- 看到问题就自己发明新抽象或新服务。
- 为了"最佳实践"大幅改动用户明确不想改的地方（如 MasonryPanel）。
- 写了一堆代码才发现项目里早有类似实现。
- 过度追求零 warning，浪费时间在不紧要的清理上。
- 用户要"简单方案"时还在推"更正确但更复杂"的设计。

---

**最后**：**Core 与 WinUI 同级，共用同一套术语**。规则服务**高效 + 靠谱 + 尊重真实需求**。觉得某条在当前任务中不合适，随时说出来一起调整。
