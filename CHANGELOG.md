# 更新日志

## v0.14.1 (2026-06-24)

**主题:视频播放集成 — MediaPlayerElement + Space 键盘 + 完整 lifecycle**

### 背景

v0.14.0 接好了视频数据通路(Gallery 永远静态首帧 + ▶ overlay,Viewer 也默认静态首帧),但点击 ▶ 什么都做不了。本 session 把"点/Space 才创建 MediaPlayerElement 并 dispose"补完:在 viewer 里点 ▶ 或按 Space 才真正创建 MediaPlayer 开始播放,离开(Next/Prev/Close/Dispose)立即释放 native 句柄。

### 改动

**`ImageViewModel` 视频播放状态机**:

- 新 `MediaPlayer? MediaPlayer` 属性(private setter, `SetProperty` 走 INotifyPropertyChanged)。`null` ↔ 真实 player 二态。View 通过 `PropertyChanged` 监听变化后调 `Player.SetMediaPlayer(...)`(WinUI 3 的 `MediaPlayerElement.MediaPlayer` 是 read-only,不能 x:Bind)。
- 新 `IsVideoPlaying: bool` (`[ObservableProperty]`),`PlayerCommand.CanExecute = IsVideo && !IsVideoPlaying && _mediaPlayer == null`。Player 在场 / 正在播 / 已经被设过 —— 三种状态都拒再启,避免重复创建。
- 新 `PlayCommand` (`[RelayCommand]`) = `PlayAsync()`:
  1. `StorageFile.GetFileFromPathAsync(video.Source.Path)` → `MediaSource.CreateFromStorageFile`
  2. `new MediaPlayer()` 设 Source + Play
  3. 设 `MediaPlayer` 属性(`PropertyChanged` 触发 view 调 `SetMediaPlayer`)
  4. 设 `IsVideoPlaying = true` → PlayerHost 切到 Visible
  5. `player.Play()`
  - 为什么用 `StorageFile.GetFileFromPathAsync` 而不是 `new Uri(path)` / `MediaSource.CreateFromUri`:`file://` URI 在 MSIX packaged 沙箱下行为不一致,StorageFile 是 WinAppSDK MSIX 下经过验证的路径,跟 Gallery 早已有的 `FileStream` 访问权限一致。
- 新 `StopAndDisposePlayer()` 私有 helper,**幂等**:
  1. `MediaPlayer = null` (触发 view `SetMediaPlayer(null)` detach)
  2. `IsVideoPlaying = false` (PlayerHost 切回 Collapsed,PlayOverlay 切回 Visible)
  3. `oldPlayer.Pause()` → `oldPlayer.Source = null` → `oldPlayer.Dispose()`
  - Pause 先于 Source = null 是为了避免"playing 中途 source 突然变 null"导致的 native 竞态。Dispose 而不是 Close —— WinAppSDK 2.2.x 暴露的 `Windows.Media.Playback.MediaPlayer` 是 `IDisposable`,UWP 的 `Close()` 在 CsWinRT projection 里就是 `Dispose`。
- 6 个 visibility helper 全是 computed property:
  - `ImageHostVisibility` = `!IsVideo ? Visible : Collapsed`
  - `PlayerHostVisibility` = `IsVideo ? Visible : Collapsed`
  - `IsPlayOverlayVisibility` = `IsVideo && !IsVideoPlaying ? Visible : Collapsed`
  - `FitModeBtnVisibility` = `!IsVideo ? Visible : Collapsed` (视频没有 1:1 概念)
  - 都通过 `[NotifyPropertyChangedFor]` 挂在 `CurrentImage` 和 `IsVideoPlaying` 上,切换 item 自动刷新。
- **`StopAndDisposePlayer()` 调用点 5 处**(导航边界全覆盖):
  - `NavigatePreviousAsync` / `NavigateNextAsync` 开头
  - `Close` 开头(在 `_loadCts?.Cancel()` 之前,确保 native handle 在 Frame pop 前释放)
  - `DeleteAsync` 切换到新 item 之后
  - `ShowImageAsync` 入口(双击 gallery card 打开 viewer)
  - `Dispose`(singleton teardown,关 app 时最后一道防线)

**`ImageViewerView.xaml` 表面拆分**:

- 原来 Grid.Row="1" 直接平铺三个元素(`FitContainer` / `ActualSizeContainer` / `MinimapOverlay`)。现在包到外层 `ImageHost` Grid 里。视频时整个 ImageHost 隐藏(`Visibility="{x:Bind ViewModel.ImageHostVisibility, Mode=OneWay}"`)。
- 新加 `PlayerHost` Grid(同样 Grid.Row="1",跟 ImageHost 同层但 visibility 互斥):
  - `<Image Source="{x:Bind ViewModel.DisplayImage, Mode=OneWay}">` 底层显示静态首帧(同 Gallery 的 Thumbnail,跟用户在瀑布流点开看到的一致 —— 不会有"点了发现不是同一张"的惊吓)。Player 上面的 surface 是不透明的,实际看不见,但保留这个底层让"未播放状态"和"播放状态"切换时不需要重新 load bitmap。
  - `<Button x:Name="PlayOverlay">` Segoe Fluent `&#xE768;` Play 字体图标 36pt,`#CC000000` 半透黑底 80×80 圆角,`Visibility` 绑 `IsPlayOverlayVisibility`。理由同 v0.14.0 gallery card:必须跨任何首帧颜色都清晰可读,Fluent 2 没有"高对比度实心圆形"专用 brush。
  - `<MediaPlayerElement x:Name="Player" AreTransportControlsEnabled="True" Stretch="Uniform">` 标准 WinUI 传输控件(play/pause/scrubber/volume)。`MediaPlayer` 不 x:Bind(read-only),由 code-behind `OnViewModelPropertyChanged` 监听 VM 的 `PropertyChanged` 调 `Player.SetMediaPlayer(...)`。
- `FitModeBtn` 加 `Visibility="{x:Bind ViewModel.FitModeBtnVisibility, Mode=OneWay}"`,视频时隐藏(Fit/1:1 概念对视频没意义,1:1 滚 1920×1080 在 1080p 屏上大多数时候是反效果)。

**`ImageViewerView.xaml.cs` lifecycle 同步**:

- 构造里订阅 `ViewModel.PropertyChanged` → `OnViewModelPropertyChanged`,匹配 `nameof(MediaPlayer)` 时调 `Player.SetMediaPlayer(ViewModel.MediaPlayer)`(既处理 player→null detach,也处理 null→player attach)。
- 加 `PlayOverlay_Click` → 委派给 `ViewModel.PlayCommand`(CanExecute 检查同 Space 路径)。
- `OnUnloaded` 加 2 行清理:退订 `PropertyChanged` + `Player.SetMediaPlayer(null)`。VM 也会在 Dispose / Close / Next 边界 dispose player,但 view 这边先 detach 是 XAML 树清理的一部分,避免"page 销毁了但 element 还 hold 着一个 native player 引用"。

**`MainWindow.HandleViewerKey` Space 键**:

- 加 `case Windows.System.VirtualKey.Space`:
  - `if (vm.PlayCommand.CanExecute(null)) vm.PlayCommand.Execute(null)`
- 跟 Left/Right/Delete/Escape 同一套 CanExecute 模式,跟 WH_KEYBOARD thread-scope hook 的"快速 return + TryEnqueue 派发"不变量兼容。
- **为什么不会跟 MediaPlayerElement 自己的 Space 处理打架**:PlayCommand 的 `CanExecute` 在 `IsVideoPlaying` 已是 true 时返回 false。`WH_KEYBOARD` hook callback 在 key dispatch **之前** 跑(VM PlayCommand 的 CanExecute 检查还是 false),通过 `CallNextHookEx` 之后 Space 才到达 focused element(player)由它自己处理。两侧都拿到 Space 但分别做正确的事:hook 拒绝重复启动,player 正常 pause。注释里写了这个时序保证。
- **不**在 hook 路径手动 dispose player(那是 VM 的职责,Close / Next 已经覆盖),hook 只管启动。

### 已决定的取舍(不重新讨论)

- **不**做 1:1 视频模式:1920×1080 视频在 1080p 屏上 1:1 是反效果(Fit 已经 Stretch=Uniform 居中)。FitModeBtn 视频时直接隐藏。1:1 视频如果将来要做,得加 scrollable 视频 surface(类似 ActualSizeContainer 但用 MediaPlayerElement),留作未来。
- **不**复用 IImageLoader LRU 给 video:`IImageLoader.LoadThumbnailAsync` 签了 `ImageSource`、返回 `BitmapImage?`,video 解码返回的是 `MediaPlayer`,类型空间不同。统一 LRU 要加 `IThumbnailCache` 抽象(下一 session 待办),本 session 不做。
- **不**改 `NavigateNextAsync` 走"先 dispose,再 await new image"的两段式:在 sync 路径头部 `StopAndDisposePlayer()` 一次就够了,async 部分 VM 自己会处理新 image 的 load。额外拆分反而把 dispose 和 image-load 的顺序耦合到 await 上,出 bug 时更难 trace。
- **不**在 `MediaEnded` 自动 stop + dispose:让 player 留在 ended 状态,transport controls 显示 ▶ 让用户点重播。Auto-stop 会让用户不得不再次点 play 才能 re-watch,UX 更差。
- **不**在 `OnViewModelPropertyChanged` 里做 null-coalesce 处理"old player 没 detach 就 set new player"的情况:VM 内部 `StopAndDisposePlayer` 已经把 `MediaPlayer = null` 写在最前,view 这边拿到的总是"先 detach,再 attach 新"的序列。

### 手动验证清单(本 session 不跑,环境 headless)

1. 双击 video card → viewer 显示静态首帧 + 居中 ▶ 圆形按钮 + Fit/1:1 按钮已隐藏。
2. 点 ▶ → 按钮消失,MediaPlayerElement 出现,自动开始播放,内置传输控件可见。
3. 视频播放中按 Space → 走 player 自带传输控件的 pause(不是 hook 的 PlayCommand,因为 `IsVideoPlaying` 已是 true,`CanExecute` 拒)。
4. 视频播放中按 Space 再按 → 继续播放(transport controls)。
5. 视频播放中按 →(Next)→ 当前 video dispose,新 video 显示静态首帧 + ▶ 按钮。
6. 视频播放中点 X 关闭 → video dispose,回到 gallery。
7. 视频播放中关 app → `ImageViewModel.Dispose` 兜底 dispose,无声卡死 / 进程残留。
8. 切到 image 卡片 → `IsVideo` 变 false,PlayerHost 隐,ImageHost 显,FitModeBtn 显,行为同 v0.13.x 完全一致。
9. AppDomain 重载 / 重复开关 viewer 不应泄漏 player(测试 10 次开关,Memory profiler 看 `Windows.Media.Playback.MediaPlayer` 实例数 = 0)。

### build 状态

0 errors / 0 warnings(`dotnet build -c Debug -p:Platform=x64` 干净通过)。

### 下一 session 待办(不在本 session 范围)

- 统一 `IThumbnailCache` service(image 已有 IImageLoader LRU,video 现在没 cache,首次解码后 `item.Thumbnail` 充当隐式 cache)。
- 视频 archive 支持(AVIO 自定义 read callback 或 temp file extract,见 `VideoMetadataService` 注释里的 deferred note)。
- 1:1 视频模式(可选 UX,见上方取舍段)。

## v0.14.0 (2026-06-24)

**主题:视频支持数据通路(MediaItem/VideoItem + FFmpeg + ▶ overlay + About page + LGPL) — 下一 session 接入 MediaPlayerElement**

### 背景

v0.13.x 项目已经具备 FFmpeg native DLL(`runtimes\win-x64\native\` 7 个 LGPL-shared build,`commit b68ee5e` 的 probe 验证通过)。本 session 把视频支持的数据通路接上 —— **不**做播放(MediaPlayerElement、Space 键盘、cache layer、format fallback 全部 deferred 到下一 session)。现在的 UX:Gallery 永远静态首帧 + ▶ overlay;Viewer 默认静态首帧(同图片),下一 session 才加 ▶ 按钮 + MediaPlayerElement。

### 改动

**Models 拆分**(从 `ImageItem` 拆出 `MediaItem` 基类 + `VideoItem` 派生类):

- `Models/ImageSource.cs` 加 `Kind: MediaKind` 字段(默认 `Image`)。`MediaKind` 是新 enum,值 `Image` / `Video`。`ToString()` 不变(同 path 才会有同 kind,key 不冲突)。
- `Models/MediaItem.cs` (新):abstract 基类,持有 `Source`/`Id`/`Name`/`FileSize`/`ModifiedTime`/`OriginalWidth`/`OriginalHeight`/`Thumbnail`/`FullImage`/`IsThumbnailLoading`/`IsThumbnailLoadingVisibility`/`FileSizeText`/`DisplayLocation`/`IsVideo`/`IsVideoVisibility`,以及抽象 `OriginalSizeText`。
- `Models/ImageItem.cs`:`sealed partial : MediaItem`,只保留 `OriginalSizeText`(返回 `"WxH"` 或 `"Unknown"`)。
- `Models/VideoItem.cs` (新):`sealed partial : MediaItem`,加 `Duration: TimeSpan` + `HasAudio: bool` + `DurationText`。`OriginalSizeText` 返回 `"WxH · m:ss"`(或 `"h:mm:ss"`,带分号补零),让瀑布流 overlay 一眼看出是 30s 短片还是 2h 录像。

**DirectoryScanner 接视频扩展名**:

- 扩展名 filter 从 `IEnumerable<string>` 改成 `IEnumerable<(string Extension, MediaKind Kind)>`(breaking on `IDirectoryScanner.ScanAsync`)。
- 调 `IImageLoader.SupportedMedia`(image+video 合并),scanner 内部建 hash map 做 O(1) extension→kind 查找,每个 yield 的 `ImageSource` 自动带正确 `Kind`。
- 视频扩展名:`.mp4 / .mkv / .mov / .avi / .webm / .flv`(BtbN FFmpeg 全覆盖,无需 format fallback)。
- Archive entry 仍只列 image(视频在 archive 内的处理 deferred — 需 AVIO 自定义 read callback 或 temp file extract,复杂度高、收益低)。`GetImageOnlyExtensions` helper 把传入的合并列表里 image 部分筛出来给 `ArchiveHelper.ListEntries` 用。
- 新增 `IImageLoader.GetKindForFile(path)` helper(FileSystemWatcher Created 路径需要按扩展名判断 kind)。

**`IVideoMetadataService` + `VideoMetadataService`**(生产代码,从 `FFmpegProbeService.ExtractFirstFrame` 提精修):

- `GetVideoMetadataAsync(source)`:只开 container 不解帧,几 ms 内返回 `(W, H, Duration, HasAudio)?`。扫页时 6 路 semaphore 限流同 image。
- `ExtractVideoThumbnailAsync(source, maxSize)`:seek 到 ~10%(很多 codec t=0 是黑帧),解码 1 帧,sws_scale 缩到 maxSize 保持长宽比,BGRA8 输出 → `SoftwareBitmap.CreateCopyFromBuffer` → `BitmapEncoder` (PNG) → `InMemoryRandomAccessStream` → `BitmapImage.SetSourceAsync`。MaxSize 默认 400,符合瀑布流需要。
- 完整 native 资源清理(swsCtx / bgraBuffer / packet / frame / codecCtx / fmtCtx 全 finally free),用 probe 一样的 7-sentinel 模式。
- Archive video 入口显式 short-circuit `null`(deferred)。
- **冷启动预热**:首调 FFmpeg API ~6.5s(DLL LoadLibrary + AutoGen JIT)。Ctor fire-and-forget `Task.Run(() => ffmpeg.av_version_info())`,`Interlocked.CompareExchange` 保证 Singleton 范围只跑一次。`App.OnLaunched` 显式 `GetService<IVideoMetadataService>()` 触发 ctor,预热在 app 启动期并行完成,不阻塞 UI。Trace 记成功/失败,失败不抛(实际调用时再 fail 才有意义)。
- 一处 `System.Buffer.MemoryCopy` vs `Windows.Storage.Streams.Buffer` 歧义,显式 `System.` 限定。

**Gallery 数据通路按 Kind dispatch**:

- `GalleryViewModel.LoadNextPageAsync` 的 6 路 fetchahead 内部按 `source.Kind` 分两路:image 走 `IImageLoader.GetImageSizeAsync`,video 走 `IVideoMetadataService.GetVideoMetadataAsync`。两者共享 `_sizeFetchSemaphore`(混合文件夹不会破坏限流)。
- 用 `FetchedMedia` record 统一返回类型(`VideoMeta`/`ImageDimensions` 互斥 nullable),避免 Task.WhenAll 推断出混合 tuple 类型。
- `LoadThumbnailAsync` 按 Kind 分:image 走 `IImageLoader.LoadThumbnailAsync`(有 LRU + mtime cache 命中),video 走 `IVideoMetadataService.ExtractVideoThumbnailAsync`。VideoItem 的 `FullImage` 也设为同一 BitmapImage(静态首帧 = "full" 视图,Viewer 的 `LoadFullImageAsync` FullImage 短路直接显示)。
- `HandleCreatedAsync`(FileSystemWatcher Created)同样按 Kind dispatch。
- `AddArchiveEntriesAsync` 不动(archive entry 还是 image-only)。
- `_imageIndex` 改 `Dictionary<string, MediaItem>`,`Images` 改 `ObservableCollection<MediaItem>`,`RemoveImage` / `DeleteImageAsync` 签名同步。
- `OnImagesCollectionChanged` 内部 cast 同步改成 `MediaItem`。

**Gallery 视觉:▶ overlay**:

- `Views/GalleryView.xaml` DataTemplate `x:DataType` 从 `ImageItem` 改 `MediaItem`(同一模板渲染两种 subtype)。
- 每个 card 加右下角 ▶ 圆形 overlay(`Background="#CC000000"` 半透明黑 + `Foreground="White"` + Segoe Fluent `&#xE768;` Play 字体图标),`Visibility="{x:Bind IsVideoVisibility, Mode=OneTime}"`。注释解释为什么这里用 fixed 半透明黑而不是 ThemeResource brush:overlay 必须跨任何 thumbnail 颜色都清晰可读,Fluent 2 没有"高对比度实心圆形"专用 brush。
- 顶 bar 加 About 按钮(`Click="AboutButton_Click"`),左侧按钮仍用 `StackPanel`,右侧 About 用 `Grid.Column="1"`(左 `*` / 右 `Auto`)。

**Viewer 不变**(下一 session 切 MediaPlayerElement):

- `ImageViewModel.CurrentImage` 改 `MediaItem?`,`Images` 改 `ObservableCollection<MediaItem>`,`LoadFullImageAsync` / `ShowImageAsync` 同步签名。
- 新 `IsVideo` 属性 forwarded from `CurrentItem.IsVideo`。当前没 XAML consumer,留作下一 session ▶ 按钮的绑定入口。
- Viewer xaml 不动 — `DisplayImage` 已经绑到 `item.FullImage`,VideoItem 的 FullImage = 第一帧缩略图,所以 viewer 直接显示静态首帧。

**License + About page + DI 注册**:

- `IcedPicViewer/License/ffmpeg-LGPL.txt`(新):LGPL 2.1 完整正文 + BtbN 构建来源 + LGPL 合规说明(可重新链接 + 署名 + 源码声明)。FOSS 标准的 LGPL 2.1 文本。
- `Views/AboutPage.xaml` (新)+ `.xaml.cs` (新):App identity(版本 = `BuildInfo.CommitShort`)+ FFmpeg 卡片(版本 8.1 / 用途说明中英 / FFmpeg 官网超链接 / BtbN build 超链接 / **LGPL 全文超链接**)。License 链接走 `StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///License/ffmpeg-LGPL.txt"))` + `Launcher.LaunchFileAsync`,用户在默认文本编辑器打开。
- `App.xaml.cs` `ConfigureServices` 注册 `services.AddSingleton<IVideoMetadataService, VideoMetadataService>()`。`OnLaunched` 加 `_ = GetService<IVideoMetadataService>();` 触发预热(见上)。

**csproj 改动**(License 进 AppX):

- `<Content Include="License\**\*.txt" CopyToOutputDirectory="PreserveNewest" />` — 文件先到 build output。
- 新增 `CopyLicenseToAppX` target(类比 `CopyFFmpegDllsToAppX`):WinAppSDK 2.2.x MSIX layout 只自动包含 `Assets/` / `Views/` / `runtimes/` / `Microsoft.UI.Xaml/`,`License/` 会被 silently dropped。target 把 `License\**\*` 复制到 `$(OutDir)AppX\License\`,使 `ms-appx:///` URI 解析能命中。条件 `WindowsPackageType != 'None'`(unpackaged 不需要此 hack)。注释里写明根因和命中症状(`StorageFile.GetFileFromApplicationUriAsync` 抛 file not found)。

### 已决定的取舍(同 v0.13.x 系列风格,不重新讨论)

- **不**加视频缓存层(image 已有 IImageLoader LRU,Video 暂不缓存,首次解码后 `item.Thumbnail` 充当隐式 cache。需统一 LRU 时下 session 加 `IThumbnailCache` 共享 service)。
- **不**加 format 检测 fallback(FFmpeg 8.1 全覆盖 `mp4/mkv/mov/avi/webm/flv`)。
- **不**加视频 archive 支持(deferred — 需 AVIO 自定义 read callback 复杂度过高,本 session 不做)。
- **不**改 MasonryPanel、不改 `LoadNextPageAsync` 6 路 semaphore、不改 `_pageFillInFlight` / `DrainPageFillAsync` 单一消费者循环、不改 `LoadDirectoryAsync` 轮询条件、**不**改 `IsThumbnailLoading` dispatcher 模式。所有 v0.14.0 pipeline 不变量从 v0.13.2 完整继承,AGENTS.md 章节无需更新。
- **不**做 `MediaPlayerElement` 集成、**不**做 Space 键盘播放、**不**做视频 Viewer ▶ 按钮(全部下一 session)。
- **不**用 DataTemplateSelector(MediaItem base + abstract `OriginalSizeText` 让一个 DataTemplate 渲染两种 subtype;`IsVideoVisibility` 控制 ▶ overlay visibility)。
- `MediaItem.OriginalSizeText` 设计为 abstract 而非新增"DurationText"单独 TextBlock:为了一个 DataTemplate + 一次 x:Bind 渲染,避免模板选择器 + DataType 切换的复杂度。视频 overlay 看起来就是 "1920×1080 · 1:23" 一行。

### 手动验证清单(本 session 不跑,环境 headless)

1. 准备测试目录:N 张直接图片 + N 个 mp4/mkv/mov 文件,混合。
2. Open Folder → 瀑布流同时显示图片(无 ▶)和视频(右下角 ▶ 圆形 overlay)。ToolTip 上,视频显示 "1920×1080 · 1:23 · 4.2 MB",图片显示 "1920×1080 · 1.4 MB"。
3. 双击视频 → 单图模式显示静态首帧(同图片,但没有 ▶ 按钮 — 这是本 session 故意)。
4. 顶 bar About 按钮 → AboutPage → FFmpeg 卡片 + 3 个超链接。点 LGPL 链接 → 默认文本编辑器打开完整 LGPL 2.1 文本。
5. 切到下一目录,新建一个 mp4 拖进去 → FileSystemWatcher 看到新建 → 视频正常进入瀑布流(走 video dispatch 路径)。
6. `dotnet run` 启动(不显式调用 `GetService<IVideoMetadataService>()` 的话,cctor 由 DI 触发)→ 打开含视频的目录 → 视频元数据 + 缩略图正常获取,**没有** 6.5s 卡顿(预热在 OnLaunched 已完成)。
7. 状态栏 / 进度 / 缩略图 spinner 行为同 v0.13.2 — pipeline 不变量继承。

### build 状态

0 errors / 0 warnings(`dotnet build -c Debug -p:Platform=x64` 干净通过)。

### 下一 session 待办(不在本 session 范围)

- `MediaPlayerElement` 集成到 `ImageViewerView.xaml`,VideoItem 在 viewer 切换显示 MediaPlayerElement(ImageItem 仍走静态 Image)。
- Space 键盘在 `MainWindow.HandleViewerKey` 加 `VirtualKey.Space` → 触发播放/暂停。
- 统一 `IThumbnailCache` service,让 video 缩略图也走 LRU。
- 视频 archive 支持(AVIO 自定义 read callback 或 temp file extract,见 `VideoMetadataService` 注释里的 deferred note)。

## v0.13.2 (2026-06-16)

**主题:Refresh 按钮 — 手动重新扫描当前目录**

### 背景

应用已经用 `FileSystemWatcher` 监听当前目录的 Created / Deleted / Modified / Renamed 事件自动更新瀑布流。但在以下场景 watcher 不可靠或事件会被错过:

- 网络盘(UNC / SMB)经常丢事件,watcher buffer 溢出静默吞事件
- archive (.zip / .rar)内部 entry 改动 watcher 看不到(watcher 只感知 archive 文件本身,感知不到 entry 级别改动)
- 用户中途误操作 kill 重建了 watcher(`StartWatching` 失败路径下 watcher 是 null)
- 用户希望"重置一下"瀑布流(滚回顶部 + 清掉现有视图再重新扫描)

### 改动

顶 bar 在 **Open Folder** 右边新增 **Refresh** 按钮(字体图标 Segoe Fluent `&#xE72C;` Refresh):

- 实现:`GalleryViewModel.RefreshCommand` 直接调 `LoadDirectoryAsync(CurrentFolderPath)`,复用整套扫描 / 分页 / watcher 重建 / 状态栏更新逻辑,**不引入**新分支
- CanExecute:目录已加载且**不**在 Scanning 中(扫描中按钮 disabled,避免和正在跑的扫描叠加状态混乱)
- Scroll 行为:`Images.Clear()` 让 MasonryPanel 自动滚回顶部;同时 `LastViewedYOffset = 0`,下次从 viewer 退回 gallery 也从顶部开始 — 因为刷新后文件顺序 / 数量可能已变,旧的 offset 不再指向同一张图
- `CurrentFolderPath` 用 `[ObservableProperty]` 暴露,加载成功后 = 设置为新 path;既是 Refresh 的数据源,也方便以后扩展("最近目录"等功能)

### 设计权衡

考虑过做**增量 reconcile**(对比新 scan 结果和当前 `Images` 集合,只 add / remove / update 差异项),但:

- watcher 本来就做增量更新;手动 refresh 的主要价值就是"强制全量重新同步",而 reconcile 还会留下"watcher 漏掉的那条"没补上
- reconcile 需要处理一堆边界(thumbnail 缓存、scroll position、total count、scan errors 列表的去重/合并、watcher 期间到达的事件 vs 新 scan 的 race)
- 现有 `LoadDirectoryAsync` 已经 cancel + dispose 旧 cts,从头跑一遍对几 GB 的图库也只多花一两秒
- 简单胜过聪明 — 真出现"刷新太慢"的痛点再优化

## v0.13.1 (2026-06-17)

**主题:坏 archive 不再中断扫描(DirectoryScanner.ToList 修复)+ 7z 支持评估决定**

### 坏 archive 扫描中断 bug 修复

之前 v0.12.0/v0.13.0 有个隐藏 bug:`DirectoryScanner.EnumerateArchiveAsync` 的 try-catch **只包了** `await Task.Run(...)` 那一行,没包下面的 `foreach`。`ArchiveHelper.ListEntries` 是 `IEnumerable<>` lazy generator,`Task.Run` lambda 内部**只**返回 `IEnumerable` 引用,真正的异常在 `foreach` 第一次 `MoveNext` 时抛 —— 在 try-catch **之外** → 逃出 generator → 逃出 `EnumerateArchiveAsync` → 逃出 `LoadDirectoryAsync` 的 `await foreach` → 被 `LoadDirectoryAsync` 的 outer catch 抓住,设 `StatusText = "Error: {ex.Message}"`,整个瀑布流变空,即使坏 archive 之后**还有**几百张图也看不到。

用户实测触发场景:download 目录里有 1 个 `download.7z`(SharpCompress 不支持) + 多张其他图片。Open Folder 后整个瀑布流空,状态栏显示 "Error: Cannot determine compressed stream type. Supported Reader Formats: ..."。修完后:坏 archive 被报告到状态栏("1 file skipped"),扫描继续,后续图片正常显示。

修复:在 `Task.Run` lambda 内 `ListEntries(...).ToList()` 强制立即 enumerate,异常在 await 时抛,被 try-catch 抓住。

### 7z 支持评估结论:**不支持**

之前 v0.12.0 CHANGELOG 写"ZIP / RAR / 7Z",这是误判。**SharpCompress 0.49.1 不支持 7z 格式**(其长期 TODO,从未实现)。社区其他 7z 选项:

| 包 | .NET 8+ 兼容? | 状态 |
|------|------|------|
| `SevenZipSharp 0.64.0` | ✗ 仅 .NET Framework 4.5 | deprecated,owner 推荐 `SevenZipSharp.Net45` |
| `SevenZipSharp.Net45 1.0.19` | ✗ 仅 .NET Framework 4.5 | 7 年没更新 |
| `7z.Libs 26.1.1` | n/a | 纯 native dll,无 managed wrapper |
| `LZMA-SDK 22.1.1` | 部分 | 5 年没更新,只给 LZMA 原始 SDK,需自己实现 7z 文件结构 |
| `7-Zip.CommandLine 25.1.0` | ✓ | 7za.exe standalone console,需进程外调 |

`.NET 生态 2026 年仍无现代 active maintained 纯 managed 7z 库`。经权衡选择**接受 7z 不支持**:
- SharpCompress 0.49.1 实际支持: ZIP, RAR, TAR, GZip, BZip2, XZ, ZStd
- 7z 文件被 `DirectoryScanner.IsArchive` 嗅探失败 → 走普通文件路径 → extensionSet 不包含 `.7z` → silently skipped(没报告)

**用户体验**: 7z 文件本身不出现在瀑布流,目录里其他图正常显示。状态栏**不**报告 7z(因为它没被识别为 archive,IsArchive 直接返回 false,**没有** ScanError 记录)。如果用户希望"看见 7z 被跳过"的报告,需要先让 IsArchive 对 7z 返回 true 然后 OpenReader 失败 —— 这要等 .NET 生态有现代 7z 库。

### 容错性 audit(用户问"将来有其他不支持或损坏无法读取的都会跳过吗")

确认**所有已知失败点**都 catch + 报告 + 跳过,**不会**中断扫描:

| 失败点 | 捕获位置 | 行为 |
|------|------|------|
| 文件本身不可读 / 不支持 | `ArchiveHelper.IsArchive` line 61 | try-catch,返回 false,当作普通文件 |
| 单个 archive entry 损坏 | `ArchiveHelper.ListEntries` line 103 | per-entry catch,跳过该 entry |
| 整个 archive 不可读 / 损坏 / 加密 | `DirectoryScanner.EnumerateArchiveAsync` line 111 | ScanError 报告 + 跳过 + 扫描继续 |
| 子目录权限/不存在 | `DirectoryScanner` line 45/49 | 跳过该子目录 |
| 单个普通图片损坏 | `ImageLoader.LoadImageStreamAsync` line 48/69 + `LoadThumbnailAsync` line 145/164/179 | catch + 缩略图失败但 ImageItem 保留(`OriginalSizeText` 显示 "Unknown") |
| 任何**未预见**异常 | `LoadDirectoryAsync` line 308 (outer catch) | StatusText 显示 "Error:",LoadingState=Completed 不卡死 |

**未来加新代码**如果忘了 try-catch,同样会中断扫描 —— 这是工程纪律,不能 100% 程序保证。但当前**所有已知失败点**都 catch。

### 改动文件

- `IcedPicViewer/Services/Implementations/DirectoryScanner.cs`:`EnumerateArchiveAsync` 改 `ListEntries(...).ToList()`,外加 14 行注释解释 lazy generator 为什么需要 ToList。`IEnumerable<ArchiveEntryInfo>` 改 `List<ArchiveEntryInfo>`。

build 验证: 0 errors / 0 warnings。

## v0.13.0 (2026-06-16)

**主题:单图模式键盘导航修复(12 commits, 6 个失败方案 + 1 个真修复)+ 标题栏 commit hash + VM/Model 改 partial property 消除 MVVMTK0045**

### 键盘导航 — 12 commit 修复史

之前单图模式左右键 / Delete / Escape 在 MSIX packaged + Win11 25H2 下完全不可用。经历了 6 个失败方案(Loaded 同步 Focus / OnNavigatedTo+DispatcherQueue+Focus / KeyboardAccelerator / SetWindowSubclass 在 ctor / SetWindowSubclass 在 Activated)之后,最终机制是 `MainWindow` 里装一个 thread-scope 的 `WH_KEYBOARD` Win32 hook(`SetWindowsHookEx(WH_KEYBOARD, ..., dwThreadId=GetCurrentThreadId())`),回调里通过 `DispatcherQueue.TryEnqueue` 投递到 UI thread 跑 `HandleViewerKey`。

**为什么 XAML-layer 方案全失败**:MSIX packaged 模式下焦点状态在 `Frame.Navigate` 之后不可靠(`AddHandler` 依赖焦点 / `KeyboardAccelerator` 文档说 global scope 但实际仍依赖焦点启动 routed event),`WindowNative.GetWindowHandle(this)` 返回的是 XAML island 内部 child HWND 不是真正接收 keyboard input 的顶层 window(`SetWindowSubclass` 注册成功但 WM_KEYDOWN 不来)。`WH_KEYBOARD` thread-scope hook 装到 UI thread 的 message queue,绕过 XAML 焦点和 HWND 机制,这是 WinUI 3 在 MSIX 沙箱下唯一能保证 work 的 keyboard 路径。

**最终实现要点**(踩过的 3 个具体 bug,全在 `KeyboardHookProc` 注释里):

1. `WH_KEYBOARD` callback 必须快速 return + 永不抛异常 → callback 整个 try-catch,VM dispatch 用 `DispatcherQueue.TryEnqueue` 投递,await 链在 hook return 之后才跑。
2. `IntPtr.ToInt32()` 在 .NET 6+ 值超 int 范围时抛 `OverflowException`(Win11 25H2 + MSIX 给的 wParam/lParam 在 64-bit IntPtr 高 32-bit 有 garbage) → 用 `unchecked((int)IntPtr)` 显式 unchecked cast,只 truncate 永不抛(`unchecked` C# 关键字**不**影响 .NET BCL method 行为)。
3. `HandleViewerKey` 从 `viewer.ViewModel` 拿 VM 不是 `viewer.DataContext` —— `ImageViewerView` 用 `x:Bind` 风格,`DataContext` 始终是 null,`is not` check 永远 true,switch 永远不执行。

**诊断约定**:`%LOCALAPPDATA%\IcedPicViewer\kbd.log` 记录 hook 安装结果、每次按键触发、异常捕获(带 stack trace)。保留以备未来 regression 排查。完整 chronology 在 `AGENTS.md` "键盘导航实现" 章节。

### 标题栏 commit hash

`MainWindow` ctor 设 `AppTitleBar.Title` 和 `AppWindow.Title` 为 `IcedPicViewer (abc1234)`,hash 来自 `BuildInfo.CommitShort`。`BuildInfo.g.cs` 由 `IcedPicViewer.csproj` 的 `GenerateBuildInfo` target 在 build 时通过 `git rev-parse --short HEAD` 生成(无 git 时 fallback "unknown"),文件写到 `IcedPicViewer/Generated/BuildInfo.g.cs`(已在 `.gitignore` 排除)。两个 title 都设:`AppTitleBar` 是 visual custom title bar,`AppWindow.Title` 给 taskbar hover / Alt+Tab / 屏幕阅读器用。

### 17 个 MVVMTK0045 warning 消除

`CommunityToolkit.Mvvm 8.4.0+` 在 WinRT 场景下对 `[ObservableProperty] private T _foo;` 触发 MVVMTK0045,建议改用 `public partial T Foo { get; set; }`(partial property,C# 13 新特性)。17 处全部迁移,partial method (`OnFooChanged`) 照常 work,所有现有 property 访问路径不变,只 backing storage 变了。

文件:
- `Models/ImageItem.cs`: Thumbnail, FullImage
- `ViewModels/GalleryViewModel.cs`: LoadingState, StatusText, TotalCount, LastViewedIndex, LastViewedYOffset, CanLoadMore, IsLoadingMore
- `ViewModels/ImageViewModel.cs`: CurrentImage, DisplayImage, DisplayActualWidth, DisplayActualHeight, ZoomLevel, IsLoading, CurrentIndex, TotalCount

### csproj 同步改动

- `NoWarn` 加 `CA2020`(`(int)IntPtr` 在 unchecked context 永不抛是故意行为)和 `SYSLIB1054`(P/Invoke for native hook 不走 source-gen AOT 收益),跟现有 `CsWinRT1028;CsWinRT1030` 抑制规则一致。
- 新增 `GenerateBuildInfo` target (BeforeBuild) 跑 git rev-parse,写 `Generated/BuildInfo.g.cs`。注释解释 MSBuild XML `;` 当 list separator 处理(用 `%3B` 转义)的细节。

### commit 链(同版本,无中间版本号)

`1ac25f1` (ViewModel 修复) → `ec47030` (HandleViewerKey VM log) → `49122b7` (`(int)IntPtr` cast) → `c80dcb5` (unchecked + stack trace) → `763364b` (try-catch + TryEnqueue) → `b94da28` (WH_KEYBOARD hook 替代 SetWindowSubclass) → `79403f7` (SetWindowSubclass 移到 Activated) → `bf04e5e` (SetWindowSubclass 替代 KeyboardAccelerator) → `a306722` (KeyboardAccelerator 替代 AddHandler) → `c8e3d5e` (恢复 89e82a7 的 OnNavigatedTo+DispatcherQueue 焦点方案) → `f3c3200` (错误把焦点移到 Loaded) → `d8a45be` (BuildInfo 标题 hash) → `a796f68` (partial property 迁移) → `d8a45be` 后续 (BuildInfo 生成 csproj 改动)。

**build 状态**: 0 errors / 0 warnings。

## v0.12.0 (2026-06-14)

**主题:支持读取压缩包中的图片(ZIP / RAR / 7Z + tar.gz/bz2/xz)**

Open Folder 选目录后,会自动把目录中所有压缩包内的图片也展平到瀑布流中,与普通图片一起浏览。不需要新按钮、不需要新页面,XAML 绑定不动。

**改动**:

- `IcedPicViewer.csproj`: 新增 `SharpCompress 0.49.1` PackageReference(纯 C#,无 native 依赖,framework-dependent publish 不受影响)。
- `Models/ImageSource.cs` (新): `readonly record struct`,统一表达"图片来源"——普通文件(`Path`)或压缩包条目(`Path` = 压缩包路径, `ArchiveEntry` = 条目内路径)。`ToString()` 输出 `path` 或 `path!entry` 作为唯一 Id + 缓存 key。
- `Models/ImageItem.cs`: `Path` 字段升级为 `Source: ImageSource`。`Name` 压缩包条目显示条目文件名(`photo.jpg` 而非 `photo.jpg (inside my.zip)`),保持瀑布流简洁。`FileSize` 对压缩包条目显示解压后大小(用户更关心图本身多大);`ModifiedTime` 用压缩包文件 mtime 作为失效信号。`UpdatePath` → `UpdateSource(ImageSource)`。
- `Services/Implementations/ArchiveHelper.cs` (新): 静态类,封装 SharpCompress 的 `ReaderFactory.OpenReader` + `ArchiveFactory.IsArchive`。`ListEntries` 走 `ReaderFactory`(兼容 7z / solid RAR 顺序读);`OpenEntryStream` **直接物化到 `MemoryStream`** 返回(避免 `BitmapImage` 在 `InputStreamOverStream` 上解码陷入 stuck loading 状态导致黑屏)。`IsArchive` 用 magic byte 嗅探,损坏文件 → 返回 false,被扫描器自动跳过。
- `Services/Implementations/ImageLoader.cs` + `Interfaces/IImageLoader.cs`: 三个方法签名 `string path` → `ImageSource source`,内部分支处理普通文件 vs 压缩包条目。压缩包缩略图/尺寸走"解压 → 拷到 `InMemoryRandomAccessStream` → BitmapImage.SetSourceAsync",和文件分支同套路;LRU 缓存 key 改为 `source.ToString()`,条目级隔离。
- `Services/Implementations/DirectoryScanner.cs` + `Interfaces/IDirectoryScanner.cs`: `ScanAsync` 返回类型 `IAsyncEnumerable<string>` → `IAsyncEnumerable<ImageSource>`。扫描时遇到扩展名在 `ArchiveHelper.ArchiveExtensions` 中(`.zip`/`.rar`/`.7z`/`.tar`/`.tgz` + 复合 `.tar.gz`/`.tar.bz2`/`.tar.xz`/`.tar.zst`)且 magic byte 匹配的文件,打开并枚举条目。新增 `IProgress<ScanError>?` 参数,扫描过程中遇到读不了的文件(损坏/加密/格式不支持)通过 `ScanError(Path, Reason)` 上报,`GalleryViewModel` 在状态栏汇总"跳过了 N 个文件,首个:xxx.zip"。
- `Services/Interfaces/ScanError.cs` (新): 简单的 `record` (`Path`, `Reason`),`Reason` 是 `ClassifyArchiveError` 映射的简短描述(`"unsupported or corrupt archive"` / `"I/O error"` / `"file missing"` / `"access denied"`),不暴露 SharpCompress 的技术消息。
- `ViewModels/GalleryViewModel.cs`: 大改。
  - `_remainingFilePaths` 类型 `List<string>` → `List<ImageSource>`。
  - `_imageIndex` 改用 `StringComparer.Ordinal`(压缩包 entry key 大小写敏感,Windows 路径系统本身已防重名)。
  - 新增 `_scanErrors` + `_scanErrorProgress` 字段,`UpdateStatusText` 把跳过的文件数 + 首个文件名拼到状态栏。
  - `LoadNextPageAsync` 用新增的 `GetSourceMetadataAsync` helper(普通文件用 `FileInfo`,压缩包条目走 `ArchiveHelper.ListEntries` 查 uncompressed size + 用压缩包 mtime)。
  - `OnFileChanged` 拆成 4 个 `Handle*Async` 方法,加压缩包分支:
    - **Created**: 若是新压缩包 → `AddArchiveEntriesAsync` 打开 + 加所有条目(失败也报告到 `_scanErrors`);若是新图片 → 现有逻辑。
    - **Deleted**: 先按 `Id` 直接匹配(普通图片);未命中则遍历 `_imageIndex` 找 `Source.Path` 匹配的所有条目(整个压缩包被删)。
    - **Modified**: 压缩包文件忽略(FileSystemWatcher 粒度太粗,无法定位到具体 entry;安全行为是保留旧缩略图)。
    - **Renamed**: 压缩包重命名 → 删旧 + 重新扫描新路径;普通文件 → 现有 `UpdateSource` 逻辑。
  - `DeleteImageAsync` 入口判断 `Source.IsInArchive` → **弹 `ContentDialog` 说明原因**(标题"无法删除",正文带文件名 + 建议到文件资源管理器处理整个压缩包),而非静默改 `StatusText`(用户已反馈原实现无任何提示)。
  - `LoadThumbnailAsync` 的 mtime 缓存检查对压缩包条目跳过(压缩包 mtime 已在 `ModifiedTime` 字段,但条目级失效需要重读整个压缩包,保留旧缩略图更轻量)。
- `ViewModels/ImageViewModel.cs`: `LoadFullImageAsync` 用 `item.Source`;`ImagePath` 派生属性显示 `Source.ToString()`(普通文件 = 路径;压缩包条目 = `path!entry`)。
- `Views/GalleryView.xaml.cs` + `Views/ImageViewerView.xaml.cs`: `OpenFileLocation_Click` 统一走 `explorer /select "{source.Path}"`,**普通文件高亮该文件,压缩包条目高亮压缩包本身**(Explorer 不能选 zip 内条目,但能高亮 zip 文件,用户立刻看到图来自哪个压缩包)。
- `ViewModels/GalleryViewModel.AddArchiveEntriesAsync` / `HandleRenamedAsync` 中对 `ArchiveHelper.ListEntries` 的调用走 `Task.Run` 避免阻塞 dispatcher。
- 编译期 baseline: 0 errors / 17 warnings,全部是 pre-existing MVVMTK0045(关于 `[ObservableProperty]` 在 WinRT 场景的 AOT 兼容建议,本次改动未引入新 warning,未触及这些字段)。

**已决定的取舍**:

- **不支持嵌套压缩包**(`a.zip` 里的 `b.zip`):`ArchiveHelper.ListEntries` 的扩展名过滤只放图片扩展名,zip 扩展名被过滤掉,自然不递归。如未来需要,把过滤集合扩展即可。
- **不支持加密压缩包**:SharpCompress 抛 `EncryptedArchiveException` → `ArchiveHelper.ListEntries` 内部 try-catch 记录 + 跳过(整个压缩包静默跳过,不打断扫描)。
- **不重写压缩包**(删除压缩包内的条目):`DeleteImageAsync` 入口**弹 `ContentDialog` 拒绝**,状态栏会被覆盖,所以不依赖静默 `StatusText`。
- **压缩包文件的 Modified 事件忽略**:粒度太粗,改一个 entry 也要重读整个压缩包才能确认。
- **`OpenEntryStream` 物化到 `MemoryStream`**:`BitmapImage.SetSourceAsync` 在 `InputStreamOverStream`(非 seekable 的 `IRandomAccessStream` 包装)上有观察到 stuck loading 状态(用户报告"打开压缩包内图片黑屏"),直接物化保证给 WIC 解码器一个 seekable 源,代价是解压后字节数的一次性内存占用(典型 1-20 MB,可接受)。
- **不缓存 archive 句柄**:每次 `LoadThumbnailAsync` / `GetSizeAsync` 都新开 archive + 关闭。对于 < 100 张图的压缩包,中央目录读取 + 关闭成本很低(微秒级);超过 1000 张图的大压缩包,后续版本可加 LRU 句柄池。
- **不压缩 LRU 缓存的 archive entry key 重复问题**:`source.ToString()` 用 `!` 分隔,跟 Windows 文件路径里几乎不存在的 `!` 字符不冲突,即使冲突也是不同源,缓存隔离正确。
- **ScanError 只在状态栏展示首个 + 计数**:不弹窗,避免每个坏文件都打扰用户;不写 log 文件,避免产生大量输出。

**手动验证清单**(未跑——环境是 headless 终端,WinUI app 需要交互):

1. 准备测试目录:几张直接图片 + 1-2 个 zip(含 jpg/png/webp) + 1 个损坏 zip + 1 个加密 zip。
2. Open Folder → 瀑布流显示所有图片(直接 + 压缩包内平铺),缩略图正常。状态栏:`Loaded N images — 1 file skipped (bad.zip: unsupported or corrupt archive)` 之类。
3. 双击压缩包内图片 → 单图模式全尺寸**正常显示**(v0.12.0 修复了 `InputStreamOverStream` stuck loading 导致的黑屏)。
4. 右键压缩包内图片 → 弹 `ContentDialog` 标题"无法删除",确认按钮关闭;打开文件位置 → `explorer /select` 高亮**压缩包本身**。
5. 滚动到底 → 增量加载对压缩包条目同样工作。
6. 删除 / 重命名压缩包文件 → FileWatcher 联动正常,失败时也会进状态栏。

**v0.12.0 发布后 polish**(同一 commit 链内的后续修复,无新版本号):

- `Models/ImageItem.cs` + `Views/GalleryView.xaml`: 瀑布流 overlay 加第三行 `DisplayLocation`(`#999` 字体色, `TextTrimming="CharacterEllipsis"`, `ToolTipService.ToolTip` 绑定同样字符串供 hover 看完整路径)—— 压缩包条目显示**压缩包文件名**, 普通文件显示**父目录路径**。overlay 现在三行:文件名 / `W×H · 大小` / 位置。
- `Views/GalleryView.xaml.cs` + `Views/ImageViewerView.xaml.cs`: 之前的"压缩包条目打开父目录"实现改回 `explorer /select "{source.Path}"` —— 用户反馈"应定位到压缩包文件名上"更直接,选中文件比打开空目录更有用。
- `IcedPicViewer.csproj` (`SyncWinUIBuildOutputToPublish` target): 修自我繁殖 bug。原 target 用 `$(OutDir)**\*.xbf` 递归搜,但 `$(PublishDir) = $(OutDir)/publish/`,所以每轮 publish 都会把 `OutDir\publish\*.xbf` 当成新文件再拷成 `OutDir\publish\publish\*.xbf`(`SkipUnchangedFiles=true` 会跳过内容相同的部分,但若中途 publish 失败留下 stale 文件,`SkipUnchangedFiles` 比对不通过,新文件就真写进去了)。改法: `**\*.xbf` / `**\*.pri` 加 `Exclude="$(OutDir)publish\**"`, `Views\` / `Assets\` / `Microsoft.UI.Xaml\` 子目录的显式 `Include` 仍正常工作。验证: 之前 110 个文件(v0.11.0 状态,含 `publish\publish\` 残留), 现在 94 个文件 / 82.9 MB。

## v0.11.0 (2026-06-03)

**主题:修 0xC0000602 启动崩溃 + 改 framework-dependent 发布模式**

`v0.9.2` 引入的 `self-contained=true` 模式实际**不能直接跑** —— WinAppSDK 2.1.3 self-contained 部署的 native DLL (`CoreMessagingXP.dll` v10.0.27200.1019、`dwmcorei.dll`、`dcomp.dll`、`Microsoft.UI.*` 等) 比 Win 11 25H2 GA 的 OS build `26200` 还新,DLL 加载时做 OS build check → `STATUS_FAIL_FAST_EXCEPTION` (0xC0000602) → `Event Log`: "Faulting module: CoreMessagingXP.dll, version: 10.0.27200.1019"。

**改动**:
- `IcedPicViewer.csproj`: `SelfContained` 和 `WindowsAppSDKSelfContained` 都从 `true` 改 `false`,改用 framework-dependent 模式。loader 走 OS 自带的 `CoreMessagingXP.dll` v10.0.27108.1016,check 通过,app 能起能浏览图片(2026-06-03 用户实测确认)。
- 改后目标机**必须装**:.NET 10 runtime + Windows App Runtime 2.1.3 standalone。
- publish 体积从 216 MB / 389 文件 → **83 MB / 110 文件**(几乎减半)。
- `RemoveUnwantedCultures` target 同步清 `$(OutDir)` 和 `$(PublishDir)`(framework-dependent 模式下 publish/ 仍然有 culture 目录残留,需要清)。
- `AGENTS.md` 追加 PE 解析诊断备忘 + framework-dependent 模式根因 + fix 路径。
- `README.md` 重写部署清单:删"self-contained 模式不完整"过时说明,加 .NET 10 runtime 前置条件。

**期间误判**:之前以为根因是 `api-ms-win-appmodel-runtime-l1-1-1.dll` 等 12 个 API Set DLL 缺失,实测 `GetModuleHandle`/`LoadLibraryEx` 验证 `apisetschema.dll` 全部能 forward 解析,这些 DLL 物理不存在 system32 不代表加载失败。**误导性诊断,已记录到 `AGENTS.md` 备忘段供后人参考**。

## v0.10.0 (2026-06-02)

**主题: 删除测试项目 + 架构精简为仅 x64**

- 彻底移除 `IcedPicViewer.Tests` 测试项目目录（含所有测试文件、MSTest 配置、旧 TestResults）。
- 更新 `IcedPicViewer.slnx`，移除测试项目引用。
- 清理 `IcedPicViewer.csproj` 中关于测试项目的说明注释。
- 同步更新 `AGENTS.md` 和 `CHANGELOG.md`，移除所有测试相关指令与历史记录。
- **架构限制**：项目现在仅支持 x64（`<Platforms>x64</Platforms>`、`<Platform>x64</Platform>`、默认 RuntimeIdentifier=win-x64）。已移除对 x86/ARM64 的支持与相关配置/文档提及（历史记录保留）。
- **彻底删除多语言支持**：设置 `<SatelliteResourceLanguages></SatelliteResourceLanguages>`（空值，完全排除所有 satellite resources），并在 Publish 后自定义 Target 自动删除 publish 目录下所有文化文件夹（包括 en-us、af-ZA 等，匹配 ^[a-z]{2}(-[A-Z]{2})?$ 的目录全部移除）。publish 后文化文件夹总数降至 0。显著减小自包含发布产物体积和文件数量。
- 原因：项目当前阶段决定移除单元测试基础设施，专注于核心功能开发与轻量治理；架构仅保留 x64 以简化构建与发布。

## v0.9.2 (2026-06-02)

**主题:修复 `dotnet publish` 产物无法启动 / 让 publish 默认产出真正自包含**

`dotnet publish` 跑出来的 exe 双击立刻闪退(退出码 `0xC000027B`)。定位到三处独立的 WindowsAppSDK 2.0 bug + 一处路径错位,都修在 csproj 里(`.pubxml` 进了 `.gitignore`,不适合存项目级配置,所以合并进来):

- `IcedPicViewer.csproj` 新增 publish 默认值 (`RuntimeIdentifier=win-x64` / `SelfContained=true` / `WindowsAppSDKSelfContained=true` / `WindowsAppSdkBootstrapInitialize=true`),产物自带 .NET 运行时 + WindowsAppSDK 原生运行时,**目标机器无需装任何 runtime 双击即用**。
- 新增 `SyncWinUIBuildOutputToPublish` target (`AfterTargets="Publish"`):把 `OutDir` 的 `*.xbf` / `*.pri` / `Assets/**` / `Views/**` / `Microsoft.UI.Xaml/**` 同步到 `PublishDir`。**根因**:WindowsAppSDK 2.0 的 WinUI targets 只 hook `GetCopyToOutputDirectoryItems` (build),没 hook `GetCopyToPublishDirectoryItems` (publish)。
- 新增 `CopyWindowsAppRuntimeBootstrapToOutput` target (`AfterTargets="Build"`):把 `Microsoft.WindowsAppRuntime.Bootstrap.dll` 从 `runtimes\win-x64\native\` 拷到 build 输出根。**根因**:bootstrap 是 P/Invoke 加载,只搜应用目录和 PATH,搜不到 `runtimes\` 子目录。
- `IcedPicViewer.Tests.csproj` 配套新增 `CopyWindowsAppRuntimeBootstrapToTestBin` target:test framework 复制 IcedPicViewer build 输出到 test bin 时只认 `deps.json`,上面手动放根目录那一份传不过去,test 端要再来一份。
- 删除 `Properties\PublishProfiles\win-x64.pubxml`(被 .gitignore 排除,改完不会进 git)和 `.gitignore` 里的 `*.pubxml` 行。
- `AGENTS.md` 补充 publish 命令和这三个坑的说明。
- 自包含 publish 产物:~218 MB,559 文件;`dotnet test` 37/37 通过;产物在 `IcedPicViewer\bin\$(Platform)\Release\$(TargetFramework)\win-x64\publish\`。

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
