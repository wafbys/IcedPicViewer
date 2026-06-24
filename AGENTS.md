# AI 协作指南 — IcedPicViewer

> **目标**：高效交付高质量代码，同时保持良好的开发体验。规则要实用，而不是为了约束而约束。

## 项目背景

这是一个基于 **WinUI 3 + Windows App SDK** 的图片查看器桌面应用（MSIX 打包）。

- 使用 **MasonryPanel** 实现瀑布流视觉效果（这是用户明确选择保留的设计，不建议轻易改成虚拟化列表）。
- 采用 **边扫边灌到 200 张停**（scanner 后台 yield source → 50ms batch flush → UI thread 灌入 gallery，第一张图约 200ms 内可见 → 满 200 张后 drain 退出，scanner 继续跑 TotalCount 增长但不自动灌图）+ **Load More 按钮**（手动一次 200 张）+ **滚动到底自动加载**（距底 1000px 触发，实现 preload 无缝）。
- 使用 CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection。
- 重视**可维护性**，但反对为了“正确”而过度设计。

## 核心协作原则

1. **用户意图优先于规则**  
   当严格遵守某条规则会导致明显不好的体验、拖慢进度或违背你的真实需求时，请主动告诉我。我们可以讨论权衡。

2. **检查现有代码，再动手**  
   改动前先搜索项目中是否有类似实现。能复用就复用，能小改就不大改。

3. **手动验证为主**  
   - 项目当前无单元测试基础设施（v0.10.0 已移除）。改完必须 `dotnet build` 干净通过 + 手动验证关键路径。
   - 公共方法的复杂业务逻辑,先想清楚边界用例,再写代码。
   - 不引入测试框架除非有真实痛点。

4. **Build 必须通过**  
   任何改动完成后，必须能干净编译（`dotnet build` 0 errors / 0 warnings）。这是底线。

5. **主动暴露问题和权衡**  
   如果你发现需求模糊、存在明显取舍、或者当前做法有风险，请直接说出来。不要为了“听话”而硬做。

6. **提交信息必须用中文**  
   Git commit message 一律使用中文。

## 日常工作流程（推荐）

1. 理解需求 + 确认边界（必要时问问题）。
2. 搜索现有实现，避免重复造轮子。
3. 实现功能。
4. 本地构建 + 手动验证关键路径。
5. 提交前自检：是否干净？是否符合用户真实意图？
6. 提交代码（中文信息）。

## 必须遵守的硬性规则

- **构建必须通过**：改完必须 `dotnet build` 干净通过。
- **中文提交**：所有 commit message 用中文。
- **检查重复**：新增功能前先找项目里有没有类似代码。
- **WinUI 禁忌**：
  - 不要用 `Window.Current`、`CoreDispatcher` 等已废弃的东西。
  - 大列表优先考虑虚拟化（但本项目因视觉要求保留了 MasonryPanel）。
  - **单图模式键盘事件不要用 XAML-layer 机制**（`AddHandler(KeyDownEvent)` / `KeyboardAccelerator` / `SetWindowSubclass`）——在 MSIX packaged + Win11 25H2 下全部失效。必须用 Win32 `SetWindowsHookEx(WH_KEYBOARD, ..., dwThreadId=GetCurrentThreadId())` thread-scope hook 装到 UI thread 的 message queue,绕过 XAML 焦点和 HWND 机制。详见下面"键盘导航实现"章节。
  - **改 ThemeResource brush 名前必须查权威 list,不要凭 Fluent 1 旧名猜**。WinUI 3 真实存在的 brush 是 Fluent 2 命名(`SubtleFillColorSecondaryBrush` / `SolidBackgroundFillColorBaseBrush` / `ControlStrokeColorDefaultBrush` / `CardStrokeColorDefaultBrush` / `LayerFillColorDefaultBrush` 等);Fluent 1 旧名(`SystemControlBackgroundChromeMediumLowBrush` / `SystemControlBackgroundChromeHighBrush` / `SystemControlBackgroundBlackHighBrush` 等)在 WinAppSDK 2.2+ 全部**不存在**,build 不会报但跑起来 XamlParseException。验证方法:在 `App.OnLaunched` 顶部 reflection 枚举 `Application.Current.Resources.MergedDictionaries` 全部 key 写到 `%LOCALAPPDATA%\IcedPicViewer\brushes.txt`,`grep` 查目标名。cf6734d / a48b77a / fc16401 这 3 个 commit 修过这个坑(顶 bar / Page / minimap),未来再改 ThemeResource 不要重复踩。
- **资源清理**：IDisposable 必须正确释放（尤其是 FileSystemWatcher、CancellationTokenSource、Stream）。
- **异常处理**：禁止空 catch 吞掉异常，至少要用 `Trace.TraceError` 记录。
- **扫描/加载 pipeline 不变量**（v0.14.0+）：
  - scanner 永远在 `Task.Run` 包住的 worker thread,`yield` 不能跑在 UI thread
  - `IngestScanBatch` 内部必须 fire-and-forget **单一** `DrainPageFillAsync` 循环(用 `_pageFillInFlight` flag),**不要**每次都启动新 `LoadNextPageAsync`(多重并发会把 UI thread marshal 压垮,导致"窗口消失"症状)
  - `IngestScanBatch` 启动 drain 条件 + drain 内部退出条件: `Images.Count < PageSize` —— **混合模式**(边扫边灌到 200 张停)的不变量,200 张后 drain 退出,scanner 继续跑但不再自动灌图
  - **不要**再加回 page fill timer;不要在 fire-and-forget 路径 set `IsLoadingMore`(那是 `LoadMoreAsync` / "Load More" 按钮的)
  - `LoadDirectoryAsync` 轮询完成条件:`!_pageFillInFlight && !IsLoadingMode` —— **不要**等 `_remainingFilePaths.Count == 0`(drain 提前退会留 source,会卡死轮询)
  - `ScanPageSize=30` / `PageSize=200` 两个 page size 分工,不要合并
  - `GetImageSizeAsync` 必须经 6 路 `_sizeFetchSemaphore` 限流
  - `IsThumbnailLoading` setter 必须经 `dispatcher.TryEnqueue`,不能 worker thread 直接写
  - 详见下面 "Gallery 扫描/加载 pipeline" 章节

## 关于“其他详细规则”

本项目之前有一堆 `.github/instructions/` 下的长文档（设计原则、性能、无障碍等）。  
**现在这些文件已不再是强制阅读材料**。旧的详细指令文件及目录已彻底删除，仅保留轻量化的 AGENTS.md。

真正重要的东西已经浓缩在本文件里。如果你不确定某个领域的最佳实践，可以直接问我，我会结合实际情况给出建议。

## 构建与运行命令

**本项目仅支持 x64 架构**。

```powershell
$Platform = 'x64'
```

**构建** (产出 AppX 布局到 `bin\$(Platform)\$(Configuration)\$(TargetFramework)\win-x64\AppX\`):
```powershell
dotnet build -c Debug -p:Platform=$Platform
```

**运行** (注册 debug package identity 后启动;**这是开发期间唯一的正确启动方式**):
```powershell
dotnet run -c Debug -p:Platform=$Platform
```

⚠️ **不要直接双击 `bin\.../IcedPicViewer.exe`**。MSIX-packaged 模式下 .exe 需要 package identity 才能找到 framework package 注册的 WinRT 类,直接跑会 `REGDB_E_CLASSNOTREG`,在 `DeploymentManagerCS.AutoInitialize.get_Options()` 静态构造函数里就崩。`dotnet run` 通过 `Microsoft.Windows.SDK.BuildTools.WinApp` 调用 `winapp.exe launch` 注册 debug identity 后才启。

**发布** (产出 sideloadable .msix):
```powershell
dotnet publish -c Release -p:Platform=$Platform
```
产物在 `bin\$(Platform)\Release\$(TargetFramework)\win-x64\AppPackages\` 下的 `IcedPicViewer_0.12.0.0_x64.msix`(还有几个 dependency .msix 一起)。

**部署清单**(给别人用时):
1. 目标机器先装 .NET 10 runtime
2. 把 .msix 拖到目标机器双击安装(sideload);Windows 自动装上 framework package 依赖
3. 不需要单独装 Windows App Runtime standalone installer(MSIX manifest 声明了 framework package 依赖)

---

### 部署模式:为什么用 MSIX packaged,不是 unpackaged

**本项目 (IcedPicViewer)**:MSIX packaged(`WindowsPackageType` 不设 → 默认 MSIX)。配套:
- `Package.appxmanifest` 在项目根,声明 framework package 依赖
- `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet → `dotnet run` 走 `winapp.exe launch` 注册 debug identity
- 无需 `Bootstrapper` 注册表项、无需 standalone installer

**unpackaged 模式(2026-06-15 之前用过,已弃)**:
- 需要目标机装 WinAppRuntime standalone + 写 Bootstrapper 注册表项
- 用户开发机缺这个,Bootstrap API 检测到 runtime 缺就弹错
- 即使装上后还会撞下面那个 XAML 资源 bug

**关键 insight**:MSIX packaged 路径用 framework package 通过 appxmanifest 解析,完全绕开 Bootstrapper;MSIX 部署也比"拷文件夹 + 装 standalone"对用户更友好。

---

### 已知坑:`XamlControlsResources` 必须在 App.xaml 显式 merge

`Microsoft.UI.Xaml.Controls.XamlControlsResources` 是 WinUI 3 Fluent 2 theme resource dictionary。`TitleBar` 等控件的 default style 内部引用 `TabViewButtonBackground` 等 theme resources,只有 merge 了这个 dictionary 才能找到。

**不能删**。commit `2b3030e "App.xaml: 删除冗余的 XamlControlsResources"` 是基于错误推理("WinUI 3 不用这个")——实际后果:启动时 `MainWindow.InitializeComponent()` 抛 `XamlParseException: Cannot find a Resource with the Name/Key TabViewButtonBackground`,MainWindow 起不来。`App.xaml` 必须保留:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <controls:XamlControlsResources />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

---

### 历史教训 —— "OS 硬冲突"理论是过度诊断 (2026-06-15 自我纠错)

之前 `AGENTS.md` 大段写"WinAppSDK 2.2.0 在 Win 11 25H2 上 native DLL build 27xxx > OS 平台 26100 → fail-fast,本机跑不起来"——**整套理论是错的**。证伪方式:

- `C:\Users\YF\WAFBYS\Src\Playground\`(同一 SDK 2.2.0, MSIX packaged)在本机**完美运行**(`Playground.exe` 启动 6s 还在跑,MainWindow 正常)
- IcedPicViewer 改成 MSIX packaged(本 commit)后**也完美运行**(`IcedPicViewer.exe` 启动 25s 还活着,MainWindow 显示正常,无 unhandled exception)

**真实问题从来不是 OS build check**,是**unpackaged 模式缺 Standalone installer**。一旦走 MSIX packaged,framework package 走 appxmanifest 解析,跟 OS build 没关系。

**技术细节正确 ≠ 整体解读正确**:
- 用户的开发机是 **正版的 Windows 11 25H2**(CurrentBuild 26200.8655,BuildLabEx `26100.1.amd64fre.ge_release.240331-1435` —— 24H2 原始平台被 25H2 Enablement Package 激活后保留的正常 BuildLab,**不是被 hack 改过版本号**)
- Microsoft 官方从 2025 年开始大量用 **Enablement Package** 方式发 H2 更新:24H2 和 25H2 共享同一 servicing branch,25H2 核心二进制(ntoskrnl.exe、kernel32.dll、apisetschema.dll 等)停留在 26100 平台,KB5054156 等 Enablement Package 解锁 25H2 特性
- 看到 CurrentBuild=26200 + DisplayVersion=25H2 + BuildLabEx=26100.1.ge_release.240331-1435 **就是正版的 25H2**,不是被 hack 改版本号;ProductName 注册表仍显示 "Windows 10 Pro" 也是从 21H2 起的兼容性老毛病,不能当证据

**教训**:
- 看到"老 platform + 新 build 号"组合时,先想 Microsoft 是否用 Enablement Package(25H2 确实用了),不要急着下"假版本"或"OS 不兼容"结论
- 测试假设时,**找一个反例**(同 SDK 跑的别人项目)就能证伪一个普适性结论
- 复杂系统里症状可以叠加;同一个错误码可能由完全不同原因触发,**诊断时要锁定到具体调用栈**

---

### 关于 api-set schema 的有用事实(2026-06-15 验证,保留)

`api-ms-win-*.dll` 系列(api-set)是**虚拟 forwarder**,由 `apisetschema.dll` 解析 serve。`System32` 里"找不到文件"是正常现象,不是缺失;grep 字符串 0 match 是因为 schema 用二进制编码。要验证某 api-set 是否真的可用,正确做法是 PowerShell `[System.Runtime.InteropServices.NativeLibrary]::Load("api-ms-win-xxx.dll")`,而不是看文件存不存在。

之前对 IcedPicViewer 0xC0000602 错误的"api-set 缺失"猜测**已推翻**;这个事实本身仍然有用(下次不要被"找不到 api-set DLL"表象误导)。

### 已知坑:`App.SetMainWindow` 早注册 — MainWindow ctor 期间 `App.MainWindow` 是 null (2026-06-24)

**症状**:Page 构造函数里写

```csharp
if (App.MainWindow is not null) {
    App.MainWindow.PropertyChanged += OnMainWindowPropertyChanged;
}
```

然后 F11 / Esc / button click 等任何会触发 MainWindow 状态变更的事件,**handler 全部不跑**。检查 log 也没有 handler 入口的 trace —— 因为 handler 根本没被订阅上。F11 多次按 chrome 仍显示,因为每次 toggle 都 raise PropertyChanged 但无订阅者。

**真因**:`App.OnLaunched` 里的

```csharp
_window = new MainWindow();   // 赋值发生在 MainWindow ctor body 跑完之后
```

而 `MainWindow` ctor body **内部** 调 `RootFrame.Navigate(typeof(GalleryView))` 触发 `GalleryView` ctor。在 GalleryView ctor 跑的瞬间,`_window` **还是 null**,所以 `App.MainWindow is not null` 是 `false`,整个 `if` 块静默跳过 —— **订阅从未发生**。

`App.MainWindow` 表面看是简单属性 getter,但 ctor 期间 null 是 WinUI 3 模板的隐性陷阱:`OnLaunched` 用 `var x = new X()` 的写法隐含"ctor 跑完才赋值",而 X 的 ctor body 又会触发 page 加载,page 读 App.X 时 X 还没注册。

**修法**:让 `MainWindow` ctor **入口** 自己注册,先于 InitializeComponent 和 Navigate:

```csharp
// MainWindow.xaml.cs ctor
public MainWindow() {
    if (Application.Current is App app) app.SetMainWindow(this);   // 第一行
    InitializeComponent();
    ...
}

// App.xaml.cs
internal void SetMainWindow(MainWindow window) => _window = window;
```

**诊断捷径**:症状是"handler 应该 fire 但不 fire",最低成本验证是加一行 `LogApp("handler entered")` 在 handler body 最顶端跑 log 看。如果**一行都没出现**,99% 是订阅没成功 —— 读 subscribe 处的 `if (App.X is not null)` 这种 guard,检查它依赖的 accessor 在 subscribe 时能不能非 null。

**跨项目适用**:WinUI 3 / UWP / 任何 XAML-based shell,只要 App 暴露 singleton Window 而 Window 自己 Navigate page 进 page ctor,就中招。

---

### Gallery 扫描/加载 pipeline（v0.14.0+）

打开目录的整条 pipeline 是这次重写的重点。**核心目标**:让整盘扫描也能在 ~200ms 内看到第一张图,而不是空白几十秒到几分钟。

**4 个数据通路 + 5 个组件协作**(`IcedPicViewer/ViewModels/GalleryViewModel.cs` + `Services/Implementations/DirectoryScanner.cs`):

```
  scanner (worker thread)
      │ yield source (worker thread,无 SyncContext)
      ▼
  RunScanAndBatchAsync ── 攒 batch (≤100 个 OR 50ms batchStartTick 时间) ── FlushScanBatch
      │ dispatcher TryEnqueue (UI thread queue)
      ▼
  IngestScanBatch (UI thread) ── _remainingFilePaths.AddRange + TotalCount++ + 触发 LoadNextPageAsync
      │ (fire-and-forget)
      ▼
  LoadNextPageAsync (UI thread) ── 6 路 fetchahead GetSize+Meta ── 30 个 ImageItem 灌入 Images
      │ (IsLoadingMore = true 期间防重入)
      ▼
  LoadThumbnailAsync (worker, 6 路 _thumbnailLoadSemaphore) ── BitmapImage ── item.Thumbnail
```

**关键设计决策(都是经验教训,改了会回归卡死)**:

- **scanner 永远在 worker thread**:`Task.Run(RunScanAndBatchAsync)` 包住整个 `await foreach`;`yield` 是 worker 上下文,不抓 UI 同步上下文,scanner 内部 `Directory.GetFileSystemEntries` 阻塞几秒不会卡 UI。
- **`batchStartTick` 而非 `lastFlushTick`**:`batch.Count == 0` 时设 `batchStartTick = now`,**保证第一张 source 50ms 内必 flush**。用 scan 起始时间做锚点的话,scanner 慢(每张 5 秒)要等更久才显示第一张。
- **`IngestScanBatch` 内部 fire-and-forget `LoadNextPageAsync`**:**完全删了 page fill timer**。但 fire-and-forget 路径**不**复用 `IsLoadingMore`(那个 flag 归 `LoadMoreAsync` 管,被 "Load More" 按钮 `CanExecute` 观察),而是使用私有 `_pageFillInFlight` flag + `DrainPageFillAsync` 单一消费者循环(详见下面"单一消费者循环")。否则每次 IngestScanBatch 都启动一个新的 LoadNextPageAsync,整盘扫描时 10+ 并发 GetImageSizeAsync fetchahead 会把 UI thread marshal 压垮 → "窗口不见"症状(进程在,UI 不刷新)。
- **`ScanPageSize=30` vs `PageSize=200`**:scan-time 用 30(避免一次 layout 30 个 Border 让 UI 卡死),手动 Load More 按钮用 200(用户主动操作期望大块加载)。`LoadNextPageAsync` 接受 `pageSize` 参数,默认走 `PageSize`。200 是 150→200 调优结果:Load More 一次多 33% 减少点击频率,但 layout pass 时间从 ~50ms 涨到 ~75ms 仍可接受;改到 300 会到 ~150ms,疯狂滚到底时能感到卡顿。
- **6 路 fetchahead**:`LoadNextPageAsync` 内部 `Task.WhenAll(batch.Select(...))` 并行 30 个 source 的 `GetSourceMetadataAsync + GetImageSizeAsync`。`BitmapDecoder.CreateAsync` 是 WinRT STA-bound,6 路限流(独立 `_sizeFetchSemaphore` 跟 `_thumbnailLoadSemaphore` 对称)避免 marshal 把 UI thread 压垮。
- **单一消费者循环**(`DrainPageFillAsync`):第一个 `IngestScanBatch` 设 `_pageFillInFlight=true` 并 fire-and-forget 启动 drain 循环,之后所有 `IngestScanBatch` 调用都是 no-op(同时 gate 在 `Images.Count < PageSize` 上,见下)。drain 循环内**串行** `await LoadNextPageAsync(Min(ScanPageSize, PageSize-Images.Count))` 直到 `_remainingFilePaths.Count == 0` 或 `Images.Count >= PageSize`,finally 释放 flag。保证同时只有**一个** `LoadNextPageAsync` 在跑(fetchahead 6 路 + 自身 1 路 = 7 路 marshal 上限,UI thread 不死锁)。这是修复"窗口消失"的关键。
- **混合模式:边扫边灌到 200 张停**(v0.14.1+,v0.14.7 调优 PageSize 150→200):drain 每次循环计算精确 `target = PageSize - Images.Count`,到 200 张就 `break` 退出;`IngestScanBatch` 启动条件也 gate 在 `Images.Count < PageSize` 上,200 张之后**不**再触发 drain。**scanner 继续在后台跑,`TotalCount` 持续增长**,只是 `Images` 集合不再自动灌入——用户手动点 "Load More" 触发 `LoadMoreAsync`(默认 `PageSize=200`)拉下一批。`LoadDirectoryAsync` 轮询条件从"`remaining == 0 && !IsLoadingMore`"改为"`!_pageFillInFlight && !IsLoadingMore`"——drain 提前退后 `_remainingFilePaths` 里可能还有几千个 source,不能等它清空。这是用户选择的"既保留立即可见,又把控制权还给用户"的折中。

**`IsThumbnailLoading` 状态机**(`Models/ImageItem.cs`):

- ctor 设 `IsThumbnailLoading = true`(缩略图未就绪,UI 叠 ProgressRing)
- `LoadThumbnailAsync` **外层** try/finally 一定 `IsThumbnailLoading = false`(覆盖所有出口:cache 命中早 return / semaphore 等待被 cancel / 解码成功 / 解码失败)
- set 必须 `dispatcher.TryEnqueue` 派回 UI thread(避免 worker thread 触发 PropertyChanged 跨线程)

**取消语义**(用户切目录时):

- 旧 cts `Cancel()` + `Dispose()` → scanner 看到 token 退出 await foreach
- 旧 batch 通过 dispatcher enqueue 的 IngestScanBatch 检查 token 直接 return,不污染新目录
- LoadNextPageAsync 内部 await 期间检查 token 早退,已 enqueue 的 `Images.Add` 也会看到 cancelled 后 return

**绝对不要**:
- 把 `await foreach (... scanner.ScanAsync ...)` 同步吞光再统一 Load —— 整盘扫描时用户看空白几十秒
- 改回 timer 驱动 page fill(已删)—— IngestScanBatch 内部 fire-and-forget 才是正解
- 把 `ScanPageSize` 调回 150 —— 整批 150 个 `Images.Add` 触发 MasonryPanel 150 次 layout pass,UI 体感卡死
- `GetImageSizeAsync` 不做 6 路限流全并发 —— BitmapDecoder CreateAsync marshal 100+ 次把 UI thread 压垮
- `LoadThumbnailAsync` 内部直接 `item.IsThumbnailLoading = false` 而不通过 dispatcher —— 跨线程 PropertyChanged 报 COMException

---

### 键盘导航实现 — `MainWindow` WH_KEYBOARD thread-scope hook

v0.13.0 之前,单图模式左右键 / Delete / Escape 经过 6 轮失败 + 1 个真修复,期间共 12 个 commit。最终机制在 `IcedPicViewer/MainWindow.xaml.cs`(搜索 `WH_KEYBOARD` / `InstallKeyboardHook` / `KeyboardHookProc`)。

**核心思想**:`SetWindowsHookEx(WH_KEYBOARD, ..., dwThreadId=GetCurrentThreadId())` 装到当前 UI thread 的 message queue。`WH_KEYBOARD` thread-scope hook 收到 thread 内**所有**键盘事件(不依赖焦点,不依赖 HWND,不被 XAML island 路由劫持),回调里把 `wParam` 转 `VirtualKey` 后通过 `DispatcherQueue.TryEnqueue` 投递回 UI thread 执行 `HandleViewerKey`。

**6 个失败方案 + 它们为什么失败**(完整 chronology 在 git log,搜索 `OnNavigatedTo` / `KeyboardAccelerator` / `SetWindowSubclass` / `WH_KEYBOARD` 关键词):

| 方案 | 失败原因 |
|------|------|
| `Loaded` 同步 `Focus()` | Loaded 时 Page 还没 layout,Focus() silent no-op |
| `OnNavigatedTo + DispatcherQueue.TryEnqueue + Focus()` (89e82a7 原版) | MSIX packaged 下 Focus() 真的 no-op(此前 unpackaged 模式可以) |
| `KeyboardAccelerator` 加到 `RootGrid` | 文档说 global scope,实际仍依赖 focused element 启动 routed event 链;无焦点 = 不 invoke |
| `SetWindowSubclass` 在 ctor 里 | `WindowNative.GetWindowHandle(this)` 在 ctor 时刻返回 0(HWND 还没 lazy 创建),hook 注册到不存在 HWND |
| `SetWindowSubclass` 移到 `Activated` 事件 | hook 注册成功(kbd.log: "subclass registered on hwnd=0x1A0978"),但 WM_KEYDOWN 永远不到那个 HWND —— XAML island 内部 child,OS 路由到 island hierarchy 更上层 |
| 任何上述方案 | 任何**未抛**未崩的情况,根因往往是"`AddHandler(KeyDownEvent)` 依赖焦点"或"`SetWindowSubclass` 依赖正确 HWND"——MSIX packaged + Win11 25H2 上两者都不可靠 |

**第 7 个(最终)**: `WH_KEYBOARD` thread-scope hook 装在 dispatcher round-trip 后的 UI thread 上,绕过整个 XAML 焦点 / HWND 体系。

**这套方案踩过的 3 个具体 bug**(全在 `KeyboardHookProc` 注释里):

1. **`vm.NavigateNextCommand.Execute(null)` 内部 `await ShowCurrentImageAsync` 让 callback 路径挂在 await 上,违反 "callback 必须快速返回" 不变量** → 解决:`DispatcherQueue.GetForCurrentThread().TryEnqueue(HandleViewerKey)` 投递,hook 同步 return,await 链从 work item 开始跑。
2. **异常逃出 hook** → 解决:整个 callback try-catch,`HandleViewerKey` 内层再 try-catch 一次;不允许任何异常 reach OS。
3. **`IntPtr.ToInt32()` 在 .NET 6+ 值超 int 范围时抛 `OverflowException`** → 解决:用 `unchecked((int)IntPtr)` 显式 unchecked cast,只 truncate 永不抛(Win11 25H2 + MSIX 下 wParam/lParam 在 64-bit IntPtr 的高 32-bit 有 garbage data,触发 ToInt32() 的 overflow 检查)。

**第 8 个(易错)**: `HandleViewerKey` 必须从 `viewer.ViewModel` 拿 VM,**不**是 `viewer.DataContext` —— `ImageViewerView` 用 `x:Bind` 风格,绑到 `public ImageViewModel ViewModel { get; }` field,`DataContext` 始终是 null。`viewer.DataContext is not ImageViewModel vm` 永远 true,switch 永远不执行。

**调试约定**:hook 相关诊断 log 写 `%LOCALAPPDATA%\IcedPicViewer\kbd.log`,内容:
- `WH_KEYBOARD hook installed ...` / `SetWindowsHookEx FAILED ...`(安装结果)
- `WH_KEYBOARD vk=... page=...`(每次按键触发,看 hook 是否收到)
- `hook callback threw: ...` / `HandleViewerKey threw: ...`(异常捕获,带 stack trace)

保留这些 log 是有意识的选择 —— keyboard nav 修好前我们 8 次靠它定位 bug,未来如果再 break(WinAppSDK 升级、平台变更),直接 `cat` 就能看到 hook 状态、栈、按键序列。不需要重做整套诊断。

**未来如果想换更"现代化"的方案**:理论上 `Microsoft.UI.Input.InputKeyboardSource.GetForWindowId(WindowId).KeyDown` 是 WinUI 3 官方 window-scope 键盘 API,不依赖焦点也不依赖 HWND。但本项目当前 WinAppSDK 2.2.1 的 winmd 里**没有**这个 type(查 `C:\Users\YF\.nuget\packages\microsoft.windowsappsdk.winui\2.2.1\metadata\Microsoft.UI.Xaml.winmd` 搜 `InputKeyboardSource` 无结果)。升 WinAppSDK 后可以试。

---

### 经验教训(2026-06-16 复盘 keyboard nav 修复)

这次修复走过 12 个 commit、6 个失败方案,代价不菲。值得记下来的:

1. **断言"以前可以"要去 log 找真证据,不要靠 commit message 推论**。`89e82a7` 时代 `OnNavigatedTo + Focus` 在 unpackaged 模式能 work,但 `bfcae46` 改 MSIX packaged 后焦点机制变了,再没真验证过 → 浪费了 4 个 commit (f3c3200 / c8e3d5e / a306722 / bf04e5e)。
2. **`page=ImageViewerView` 这种"看起来通过"的 log 不等于 switch 真跑了**。`ec47030` 加 inner log 才暴露 `viewer.DataContext is not ImageViewModel vm` 永远 true —— hook 自己 log 看到的 `RootFrame.Content?.GetType().Name` 和 `HandleViewerKey` 内部的 `is not` check 用的**不是同一个引用路径**。诊断 log 要**端到端**。
3. **stack trace 永远比 message 优先**。`OverflowException` 光看 message 是 "Arithmetic operation resulted in an overflow",完全不知道是哪行;加 `ex.StackTrace` 后直接定位到 `System.IntPtr.ToInt32()`,省了 5 轮猜。
4. **`unchecked` C# 关键字只影响编译期检查**,**不**影响 .NET BCL method 行为。`IntPtr.ToInt32()` 是 .NET runtime method,即使在 `unchecked { ... }` 块里仍按 .NET 6+ 行为抛 `OverflowException`。要 truncate 又不抛,必须用显式 unchecked cast `(int)IntPtr`。
5. **x:Bind 风格 page 的 ViewModel 在 `page.ViewModel` 字段**,**不**在 `page.DataContext`。`{Binding}` 风格两个都能用,x:Bind 风格只认 code-behind property。这个混淆浪费了一次 commit。
6. **同一段代码"以前可以"不代表"现在还可以"**,平台/SDK 升级是隐性杀手。每次 WinAppSDK 升级 / 部署模式切换(MSIX packaged / unpackaged)都要重新验证 keyboard nav 这类 input-sensitive 路径,不能 commit message 看了说"已修过"就跳过。

---

### 实现细节

`IcedPicViewer.csproj` 末尾的自定义 MSBuild target:
- `SyncWinUIBuildOutputToPublish` —— 修 WindowsAppSDK 的 bug:它的 WinUI targets 只 hook 了 build 的 `GetCopyToOutputDirectoryItems`,**没 hook publish 的 `GetCopyToPublishDirectoryItems`**。不修的话 **unpackaged** publish 出来的 exe 缺 `App.xbf`/`MainWindow.xbf`/`Assets`/`Views`,启动后立刻崩(0xC000027B)。
  - **当前 (MSIX packaged) 下是 no-op**,因为 `Condition="'$(WindowsPackageType)' == 'None'"` 不满足
  - 保留 target 作为"如果以后要切回 unpackaged 部署"的兜底;微软 2.2.0 仍未修这个 bug
- `RemoveUnwantedCultures` —— 同时清 `$(OutDir)` 和 `$(PublishDir)` 中 BCP 47 格式的 culture 子目录,实测 publish 产物从 222 MB / 572 文件减到 216 MB / 389 文件(86 个 culture 目录消失)。Build 和 Publish 两阶段都跑。

## 常见 AI 易犯错误（请主动避免）

- 看到问题就自己发明新抽象或新服务。
- 为了“符合最佳实践”而大幅改动用户明确不想改的地方（比如 MasonryPanel）。
- 写了一堆代码后才发现项目里早就有类似实现。
- 过度追求零 warning，导致把时间浪费在无关紧要的清理上。
- 在用户明确想要“简单方案”时，还在推“更正确但更复杂”的设计。

---

**最后**：  
这套规则的核心是**高效 + 靠谱 + 尊重用户真实需求**。  
如果你觉得某条规则在当前任务中不合适，随时告诉我，我们一起调整。