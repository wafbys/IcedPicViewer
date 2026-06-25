# AI 协作指南 — IcedPicViewer

> **目标**：高效交付高质量代码。规则要实用，不是为约束而约束。

## 项目背景

基于 **WinUI 3 + Windows App SDK** 的图片查看器（MSIX 打包，x64，.NET 10）。

- **MasonryPanel** 瀑布流布局（用户明确选择保留，不要改虚拟化列表）。
- **混合加载模式**：边扫边灌到 200 张停 → scanner 继续后台跑但不再自动灌 → 用户点 "Load More" 或滚到底手动加载。
- CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection。
- 反对为了"正确"而过度设计。

## 构建与运行

```powershell
$Platform = 'x64'

# 构建
dotnet build -c Debug -p:Platform=$Platform

# 运行（唯一正确方式，不要直接双击 .exe）
dotnet run -c Debug -p:Platform=$Platform

# 发布
dotnet publish -c Release -p:Platform=$Platform
```

⚠️ 不要直接双击 `bin\...\IcedPicViewer.exe`——MSIX packaged 需要 package identity，直接跑会 `REGDB_E_CLASSNOTREG`。`dotnet run` 通过 `winapp.exe launch` 注册 debug identity 后启动。

## 核心原则

1. **用户意图优先**——规则和体验冲突时说出来讨论。
2. **先查现有代码再动手**——能复用就复用，能小改就不大改。
3. **手动验证为主**——项目无单元测试，改完 `dotnet build` 干净通过 + 手动验关键路径。不引入测试框架除非有真实痛点。
4. **主动暴露权衡**——需求模糊或有风险直接说，不要硬做。
5. **Commit 用中文**。

## 硬性规则

### 通用
- `dotnet build` 0 errors / 0 warnings —— 底线。
- Commit message 中文。
- IDisposable 必须正确释放（FileSystemWatcher、CancellationTokenSource、Stream）。
- 禁止空 catch 吞异常，至少 `Trace.TraceError` 记录。

### WinUI 禁忌
- 不用 `Window.Current`、`CoreDispatcher` 等已废弃 API。
- 大列表优先虚拟化（但本项目 MasonryPanel 除外）。
- **ThemeResource brush 名只认 Fluent 2 命名**。`SubtleFillColorSecondaryBrush` / `SolidBackgroundFillColorBaseBrush` / `ControlStrokeColorDefaultBrush` / `CardStrokeColorDefaultBrush` / `LayerFillColorDefaultBrush` 等真实存在；Fluent 1 旧名（`SystemControlBackgroundChromeMediumLowBrush` 等）在 WinAppSDK 2.2+ 全不存在，build 不报但运行时 `XamlParseException`。
- **键盘事件只用 WH_KEYBOARD hook**（详见下方"键盘导航"章节）。不用 `AddHandler(KeyDownEvent)` / `KeyboardAccelerator` / `SetWindowSubclass`。

### Gallery 扫描/加载 pipeline 不变量

核心：**边扫边灌到 200 张停**。scanner → worker thread yield source → 50ms batch flush → UI thread 灌入 → 满 200 张后 drain 退出，scanner 继续跑 TotalCount 但不自动灌。

```
scanner (worker thread)
    │ yield source
    ▼
RunScanAndBatchAsync ── batch ≤100 或 50ms batchStartTick ── FlushScanBatch
    │ dispatcher TryEnqueue
    ▼
IngestScanBatch (UI) ── _remainingFilePaths.AddRange + TotalCount++ → 触发 DrainPageFillAsync
    │ (fire-and-forget, 单一消费者循环)
    ▼
DrainPageFillAsync ── 串行 LoadNextPageAsync(≤30) ── 6 路 fetchahead ── Images.Add
    │ 直到 Images.Count ≥ 200 或 queue 空
    ▼
LoadThumbnailAsync (worker, 6 路 semaphore) ── BitmapImage ── item.Thumbnail
```

**不变量清单**：

| 规则 | 说明 |
|------|------|
| scanner 永远在 worker thread | `Task.Run(RunScanAndBatchAsync)` 包住整个 `await foreach`，yield 不走 UI 同步上下文 |
| `batchStartTick` 锚点 | batch 空时设 `batchStartTick = now`，保证第一张 source 50ms 内必 flush |
| 单一 consumer `DrainPageFillAsync` | `_pageFillInFlight` flag 保证同时只有一个 drain 循环，串行 `await LoadNextPageAsync` |
| `_pageFillInFlight` ≠ `IsLoadingMore` | fire-and-forget 路径用 `_pageFillInFlight`，`IsLoadingMore` 归 Load More 按钮 |
| `ScanPageSize=30` / `PageSize=200` | scan-time 用 30（避免 layout 卡死），Load More 用 200 |
| drain gate: `Images.Count < PageSize` | 满 200 张后不再自动触发 drain |
| 轮询完成条件 | `!_pageFillInFlight && !IsLoadingMore`（不等 `_remainingFilePaths.Count == 0`，drain 提前退会留 source） |
| 6 路 `_sizeFetchSemaphore` | `GetImageSizeAsync` 必须限流，WinRT BitmapDecoder 是 STA-bound |
| `IsThumbnailLoading` setter | 必须经 `dispatcher.TryEnqueue`，不能 worker thread 直接写 |
| ctor 设 `IsThumbnailLoading = true` | 外层 try/finally 保证所有出口都 `= false`（cache 命中/取消/解码成功/失败） |

**绝对不要做的事**：
- 把 `await foreach` 同步吞光再统一 Load（用户看空白几十秒）
- 加回 page fill timer（已删，fire-and-forget 是正解）
- 把 `ScanPageSize` 调到 150（150 次 layout pass 卡死 UI）
- `GetImageSizeAsync` 不做限流全并发（marshal 100+ 次压垮 UI thread）
- `LoadThumbnailAsync` 内直接 `item.IsThumbnailLoading = false`（跨线程 COMException）

**取消语义**：切目录时旧 cts `Cancel()`+`Dispose()` → scanner 退出 await foreach → 旧 batch 检查 token 直接 return → LoadNextPageAsync 内 await 期间早退。

### 键盘导航（WH_KEYBOARD thread-scope hook）

最终方案在 `MainWindow.xaml.cs`（搜索 `WH_KEYBOARD` / `InstallKeyboardHook` / `KeyboardHookProc`）。

**机制**：`SetWindowsHookEx(WH_KEYBOARD, ..., dwThreadId=GetCurrentThreadId())` 装到 UI thread message queue → 收到所有键盘事件（不依赖焦点/HWND/XAML 路由）→ 回调 `TryEnqueue` 投递 `HandleViewerKey`。

**3 个关键实现细节**：
1. hook callback 同步 return（`TryEnqueue` 投递，await 链从 work item 开始跑）
2. try-catch 双层防护，不允许异常 reach OS
3. `wParam`/`lParam` 用 `unchecked((int)IntPtr)` cast（不用 `IntPtr.ToInt32()`，Win11 25H2 下 64-bit 高 32 位有 garbage 会抛 `OverflowException`）

**易错点**：`HandleViewerKey` 从 `viewer.ViewModel` 拿 VM，不是 `viewer.DataContext`（`ImageViewerView` 用 `x:Bind`，DataContext 始终是 null）。

**调试**：hook 诊断 log 写 `%LOCALAPPDATA%\IcedPicViewer\kbd.log`。未来 WinAppSDK 升级后可以考虑 `Microsoft.UI.Input.InputKeyboardSource.GetForWindowId(WindowId).KeyDown`。

## 已知坑

### 1. `App.SetMainWindow` 必须早于 ctor 内 Navigate

MainWindow ctor 第一行就调 `SetMainWindow(this)`，再走 `InitializeComponent` + Navigate。否则 page ctor 内读 `App.MainWindow` 是 null——订阅静默跳过。

```csharp
// MainWindow.xaml.cs ctor
public MainWindow() {
    if (Application.Current is App app) app.SetMainWindow(this);  // 第一行
    InitializeComponent();
    ...
}
```

### 2. `XamlControlsResources` 不能删

`App.xaml` 必须显式 merge `<controls:XamlControlsResources />`。删了会导致 `TitleBar` 等控件 default style 找不到 `TabViewButtonBackground` 等 theme resource，启动时 `XamlParseException`。

### 3. `x:Bind` 页面的 VM 在 `page.ViewModel` 字段

`{x:Bind}` 风格的 page 不设 `DataContext`。拿 VM 走 `page.ViewModel` property，不要用 `page.DataContext is XxxViewModel`（永远 false）。

## 常见 AI 易犯错误

- 看到问题就自己发明新抽象或新服务。
- 为了"最佳实践"大幅改动用户明确不想改的地方（如 MasonryPanel）。
- 写了一堆代码才发现项目里早有类似实现。
- 过度追求零 warning，浪费时间在不紧要的清理上。
- 用户要"简单方案"时还在推"更正确但更复杂"的设计。

---

**最后**：这套规则的核心是**高效 + 靠谱 + 尊重用户真实需求**。觉得某条规则在当前任务中不合适，随时说出来一起调整。
