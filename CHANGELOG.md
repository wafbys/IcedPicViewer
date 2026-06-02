# 更新日志

## v0.9.1 (2026-06-02)

**主题:深度 review 收尾(P3–P7),覆盖之前 v0.9.0 之后的所有改进**

### 资源释放与健壮性(P3)

- `GalleryViewModel._remainingFilePaths` 加 `_remainingLock` 保护,`LoadNextPageAsync` 与 `OnFileChanged.Deleted` 并发访问不再 race
- 删除 `ImageItem.IsLoading` 死字段(从未被设为 true),同步删除 `GalleryView.xaml` 中相应 `ProgressRing` 节点
- `ImageViewModel` 构造里两个 lambda 订阅改 named method,`Dispose` 时取消订阅
- `OnFileChanged` dispatcher lambda 整体加 `try-catch`,异常时 `Trace.TraceError` 而非被 WinUI 静默吞掉
- `App.OnLaunched` 订阅 `_window.AppWindow.Closing`,关闭时显式 `Dispose` `GalleryViewModel` 和 `ImageViewModel`
- 删除 `GalleryViewModel._loadedThumbnailCount` 死字段
- 抽 `UpdateStatusText()` helper,消 `Created` / `Deleted` / `LoadDirectory` / `LoadMore` 四处重复的格式化逻辑
- `GalleryViewModel._canLoadMore` 装饰 `[NotifyPropertyChangedFor]` + `[NotifyCanExecuteChangedFor]`,删手动 `OnCanLoadMoreChanged` partial method

### 跨 await cancellation 保护(P4)

- `LoadNextPageAsync` / `OnFileChanged` lambda 跨 `await` 后可能误入新 `Images`,加 `ct.IsCancellationRequested` 早退
- `LoadDirectoryAsync` 的 `linkedCts` 改 `using var`,不再泄漏
- `ShowImageAsync` 切图时立即 `DisplayImage = null`,旧图立刻消失(之前大图场景 100-500ms 视觉延迟)
- 消除 `LoadNextPageAsync` 内的 `itemCount` 闭包变量,直接用 `Images.Count`
- `StartWatching` 加 `try-catch` + `StatusText` 反馈
- `LoadDirectoryAsync` 的 cts 替换改用 `Interlocked.Exchange`,代码更清晰

### 事件订阅泄漏与 UX 状态(P5)

- `ImageViewerView.DisplayImageChanged` lambda 订阅改为 named method,`Unloaded` 时取消订阅
- `GalleryView.OpenImageViewer` 加 `_currentImageViewModel == null` 守卫
- `GalleryView.OnGalleryViewUnloaded` 删 `Unloaded -= self`,Page 复用时清理仍能触发
- `ImageViewModel.CanLoadMoreImages` 加 `!IsLoadingMore`,LoadMore 按钮加载中正确 disable
- `OnFileChanged.Modified` / `Renamed` 跨 `await LoadThumbnailAsync` 后加 token 检查(P4-A 漏修)
- `ImageItem` 4 个字段改回 `init`,只 `UpdatePath` 用 `private set`

### 损坏图片 UX(P6)

- `ImageItem` 加 `OriginalSizeText` derived property,损坏图不显示 "0×0" 而是 "Unknown"
- `GalleryView.xaml` 改用 `OriginalSizeText`
- 新增 4 个 `OriginalSizeText` 单元测试,覆盖所有边界

### 维护(P7)

- 清理工作区 `bin/obj`(P6 提交后再次堆积的 4 个目录)
- `CHANGELOG.md` 追记 P3–P6 的所有 commit

### 指标对比(v0.9.0 → v0.9.1)

| 维度 | v0.9.0 | v0.9.1 |
|---|---|---|
| 单元测试 | `33/33` | `37/37` |
| `dispose` 泄漏 | 仍有 App 退出路径 | 全清理 |
| 跨 await cancellation 漏洞 | 3 处 | `0` |
| 静默失败 | 1 处(`StartWatching`) | `0` |
| 事件订阅泄漏 | 2 处 | `0` |
| 损坏图 UX | 显示 "0×0" | 显示 "Unknown" |

## v0.9.0 (2026-06-02)

**主题:大规模代码质量与可靠性清理(P0–P3 完整战役)**

### 项目治理与构建(P0)

- 移除 `StyleCop.Analyzers` 及其配置(`stylecop.json`、`.editorconfig` 中 SA 规则)。代码风格改由 `.editorconfig` 显式维护,`.NET Analyzers` 继续保留
- 关闭 `EnforceCodeStyleInBuild`,IDE 风格建议回到 `.editorconfig` 的 `suggestion` 级别
- 删除未使用的 NuGet 依赖:`SixLabors.ImageSharp`、`Microsoft.Graphics.Win2D`
- 删除死代码:`Models/LoadingState.cs` 中无用的 `ImageSourceType` 枚举、空壳 `MainViewModel`
- 修复 `IcedPicViewer.slnx`:补加 `IcedPicViewer.Tests` 项目引用,补 `Configurations` 节点与平台映射(原写法触发 `MSB4126` `dotnet build` 失败)
- 修正 `Controls/MasonryPanel.cs` 自相矛盾的注释(说明仍是主用布局)
- 修复 `.editorconfig` glob:`[*.Tests.cs]` → `[*Tests.cs]`(原写法未匹配实际文件名)
- 删除 csproj / `.editorconfig` 中过时的 `.github/instructions` 死路径引用
- 删除 `Package.appxmanifest` 多余的 `systemAIModels` capability
- `ImageViewModel` 实现 `IDisposable`(CA1001),`_loadCts` 在 `Dispose` 中释放

### 平台简化

- 主项目 `csproj` `<Platforms>` 从 `x86;x64;ARM64` 改为 `x64`
- `slnx` `Configurations` 节点简化为仅 `x64`,对应 Project 平台映射同步收敛
- 删除未使用的 `win-x86.pubxml` / `win-arm64.pubxml` 配置文件(本就不在版本控制中)
- 工作区 `bin/obj` 体积从 `~896 MB` 降至 `~96 MB`(单平台累积),VS 配置管理器只显示 `x64`

### 性能与可靠性(P1)

- `ImageViewerView.FitModeBtn_Click` 删 `Task.Delay(100/200)` hack,改纯事件驱动(`ActualSizeImage.ImageOpened` / `ActualSizeContainer.SizeChanged` 已自动驱动 `UpdateMinimap`)
- `ImageLoader` 缩略图缓存由无界 `ConcurrentDictionary` 改为手写 LRU(`LinkedList` + `Dictionary` + `lock`),容量 200。400px 缩略图最坏 `~30-80 MB`,不再随大文件夹持续涨内存。无新包依赖。
- 窗口状态文件从 exe 目录改存 `%LOCALAPPDATA%\IcedPicViewer\`,符合 unpackaged WinUI 应用标准做法(避免 `Program Files` 只读 / AV 拦截)

### 测试覆盖(P1)

- 新增 `ImageViewModelTests.cs`,6 个测试覆盖 `ShowImage` / `Navigate` / `Close` / `Delete` 关键路径,补齐 `ImageViewModel` 零覆盖缺口

### 性能与文件监控(P2)

- `GalleryViewModel.Images` 加 `Dictionary<string, ImageItem>` 索引(`OrdinalIgnoreCase`),`OnFileChanged` 从 O(n) `FirstOrDefault` 改为 O(1) `TryGetValue`,大文件夹监控不再线性扫描
- `IImageLoader.LoadImageAsync(byte[])` → `LoadImageStreamAsync(Stream)`,调用方持有 `Stream` 直接喂 `BitmapImage.SetSourceAsync`,跳过中间 `byte[]` 缓冲。50MB RAW 不再爆 LOH
- `FileSystemWatcher.Renamed` 事件正确处理:`FileChangeInfo` 加 `OldPath` 可选字段、`DirectoryScanner` 填 `e.OldFullPath`、`ImageItem` 加 `UpdatePath(newPath, newName)` 方法、`OnFileChanged.Renamed` 用 `OldPath` 匹配后 `Remove → UpdatePath → Add` 维护索引一致

### 静态分析(P2)

- 修剩余 11 个 warning 直至归零(`CA1861` × 4、`MSTEST0032` × 2、`CA1305` × 4、`CsWinRT1028/1030` 抑制)
- `MasonryPanel` 标记 `partial`(CsWinRT1028 触发条件)

### 资源释放与健壮性(P3)

- `GalleryViewModel._remainingFilePaths` 加 `_remainingLock` 保护,`LoadNextPageAsync`(worker 线程)与 `OnFileChanged.Deleted`(dispatcher 线程)并发访问不再 race
- 删除 `ImageItem.IsLoading` 死字段(从未被设为 true),同步删除 `GalleryView.xaml` 中相应 `ProgressRing` 节点
- `ImageViewModel` 构造里两个 lambda 订阅改 named method,`Dispose` 时取消订阅,避免 lambda 闭包阻止 GC 回收
- `OnFileChanged` dispatcher lambda 整体加 `try-catch`,异常时 `Trace.TraceError` 而非被 WinUI 静默吞掉
- `App.OnLaunched` 订阅 `_window.AppWindow.Closing`,关闭时显式 `Dispose` `GalleryViewModel` 和 `ImageViewModel`,避免 `_fileWatcher` / `_loadCts` / `_thumbnailLoadSemaphore` 漏清理
- 删除 `GalleryViewModel._loadedThumbnailCount` 死字段(被 `++` 但从未被读取)
- 抽 `UpdateStatusText()` helper,消 `Created` / `Deleted` / `LoadDirectory` / `LoadMore` 四处重复的 `'Loaded X / Y images'` 格式化逻辑
- `GalleryViewModel._canLoadMore` 装饰 `[NotifyPropertyChangedFor]` + `[NotifyCanExecuteChangedFor]`,删手动 `OnCanLoadMoreChanged` partial method

### 指标对比

| 维度 | v0.8.0 | v0.9.0 |
|---|---|---|
| NuGet 直接依赖 | 7(含 2 个死依赖) | 5 |
| `bin/obj` 体积(多平台累积) | `~896 MB` | `~96 MB` |
| 编译警告 | `1024`(StyleCop 噪音) | `0` |
| 单元测试 | `27/27` | `33/33` |
| 死代码 | `ImageSourceType` / `MainViewModel` | 0 |
| 线程安全 race | `_remainingFilePaths` 无锁 | 加 lock |
| 资源释放 | App 退出漏 dispose | `AppWindow.Closing` 显式 dispose |
| 解决方案构建 | `dotnet build` slnx 失败(`MSB4126`) | 正常 |

## v0.8.0 (2026-05-31)

- 项目治理重构：将原有多文件重度指令系统大幅精简，改为轻量实用版 AGENTS.md
- 代码清理：删除长期废弃的 `MasonryLayout.cs`（早期尝试虚拟化布局的实验失败产物）
- 移除冗余结构：清理 `.github` 目录及相关旧指令文件，降低维护负担
- 单图模式增强：在图片查看器中集成 Load More 按钮及到底自动加载更多逻辑
- 整体提升开发体验：强调实用主义、用户意图优先，以及 AI 协作的轻量流程

## v0.7.0 (2026-05-30)

- 实现“滚动到底部自动加载更多”功能（带 180ms debounce 防抖，性能友好）
- 修复 Load More 按钮不响应问题（RelayCommand 命名修正 + 增量状态维护）
- 保留 MasonryPanel 瀑布流视觉效果的同时提供增量加载体验
- Load More 按钮保留作为手动后备，自动加载与手动触发共存
- 新增中文代码注释，符合项目规范

## v0.6.0 (2026-05-30)

- 实现增量加载（首次只加载 150 张，后续通过 Load More 按钮按需加载）
- 优化大文件夹打开性能，保持原有瀑布流视觉

## v0.5.0 (2026-05-19)

- 添加文件监控功能（FileSystemWatcher），支持外部增删改文件自动刷新
- 删除图片支持回收站（本地磁盘直接回收站，网络路径确认后永久删除）
- 修复小尺寸图片显示 0x0 的问题
- 添加图片路径显示
- 修复窗口调整大小后键盘失灵问题
- 修复文件变化时状态栏计数不更新的问题

## v0.4.0 (2026-05-19)

- 修复 1:1 模式下切换图片时小地图不刷新的问题
- 优化 Fit/1:1 模式切换延迟
- 清理代码，移除调试输出

## v0.3.0 (2026-05-18)

- 添加键盘导航（左右箭头切换图片，ESC 关闭）
- 添加图片信息显示（尺寸、文件大小）
- 添加窗口状态记忆（关闭后恢复位置和大小）
- 优化图片尺寸获取

## v0.2.0 (2026-05-18)

- 添加 Fit 和 1:1 显示模式切换功能
- 修复 1:1 模式下水平滚动不可用的问题
- 简化 1:1 模式实现，使用 Stretch="None" 自动按像素尺寸显示

## v0.1.0-alpha (2026-05-17)

- 初始版本
- 文件夹浏览功能
- 缩略图网格视图
- 图片查看器
- 图片切换导航
- 加载进度指示
- GIF 动画支持
