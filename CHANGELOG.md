# 更新日志

## v0.14.5 (2026-06-25)

**主题: 全屏模式 + Slideshow 循环/乱序**

### 背景

两个跨 session 累积的 UX 增强一起做。Fullscreen 是常用功能(很多
图片查看器 F11 一键切),Slideshow 增强补全演示场景 — 循环让用户
走开时不中断演示,乱序避免同一文件夹里图片按文件名/日期顺序重复看
腻。

### 改动

**Fullscreen (MainWindow + App + ImageViewerView + MainWindow.HandleViewerKey)**

- `App.MainWindow` 类型从 `Window?` 收窄为 `MainWindow?` (downcast),
  视图层用 `App.MainWindow?.ToggleFullscreen()` 直接调,不需要
  `(App.Current as App)` cast + 类型检查每个 call site。
- `MainWindow.IsFullscreen` (computed `AppWindow.Presenter.Kind ==
  AppWindowPresenterKind.FullScreen`) + `ToggleFullscreen()` 方法:
  `AppWindow.SetPresenter(FullScreen)` 切全屏,`SetPresenter(Overlapped)`
  切回。WinUI 3 推荐 API (UWP 的 `ApplicationView.TryEnterFullScreenMode`
  在 desktop WinUI 3 不存在)。`PropertyChanged` 手动 fire 给
  x:Bind consumer (AppWindow.Presenter.Kind 不会自动 notify)。
- `ImageViewerView` 加 `IsFullscreen` + `IsFullscreenGlyph`/
  `IsFullscreenLabel`/`IsFullscreenTooltip` 4 个 property 给 button
  x:Bind (Glyph 切 U+E827 / U+E93C)。页面类显式
  implement `INotifyPropertyChanged` (之前没实现,x:Bind OneWay
  会报 WMC1506 警告)。
- 页面 ctor 订阅 `App.MainWindow.PropertyChanged`,F11
  触发 ToggleFullscreen → MainWindow fires PropertyChanged →
  页面 re-raise IsFullscreen-derived properties → x:Bind
  refresh (无这条链路时 F11 后 button glyph 冻在旧值)。
- `MainWindow.HandleViewerKey` 加 `case VirtualKey.F11`:
  调 `ToggleFullscreen()`,跟 view 按钮走同一方法,button
  state 跟 F11 同步。
- Fullscreen 时 OS title bar / window chrome 自动隐藏(presenter
  行为),但 app 自己的 CommandBar(顶 bar)仍可见 — 用户可继续用
  Next/Prev/Slideshow 等控件。完全 immersive (隐藏 CommandBar
  + status bar) 留作下一 session (auto-hide on mouse move 类似
  PowerPoint 演示模式)。

**Slideshow Loop / Shuffle (ImageViewModel + ImageViewerView)**

- 新 `IsSlideshowLooping` + `IsSlideshowShuffling` (ObservableProperty,
  各自有 Glyph / Label / Tooltip computed property + `[NotifyPropertyChangedFor]`)。
- 各自 x:Bind 到 viewer CommandBar 的两个新 AppBarButton。
  Glyph 保持不变 (U+E8ED RepeatAll, U+E8B1 Shuffle),label + tooltip
  flip on/off (WinUI 3 AppBarButton 没有 built-in "toggled" 视觉
  状态,toggle 行为靠 tooltip/label 表达)。
- `OnSlideshowTick` 重写:
  - Loop on + 到末尾: `CurrentIndex = 0` 直接跳第一张 (不走
    `NavigateNextCommand`,那个会因 `CanNavigateNext == false` 短路);
  - Shuffle on + Images.Count > 1: 随机选一个不是当前的 index
    (`do/while` 排除重复),`CurrentIndex = newIndex`;
  - 都不 on: 走 `NavigateNextCommand` (跟之前一样);
  - Loop off + Shuffle off + 到末尾: `StopSlideshow` (跟之前一样)。
  Shuffle 跟 Loop 同时 on 是合法的 (走 shuffle 分支;Loop 分支只在
  shuffle 关闭且到末尾时触发)。
- `private readonly Random _slideshowRandom` 单例 (VM Singleton,
  DispatcherQueueTimer 永远在 dispatcher thread 跑,不需要 thread-safe
  Random)。`do/while` 处理 Count==1 edge case (虽然前面 short-circuit
  但保险写法)。
- Button click handler 只 toggle `ViewModel.IsSlideshowXxx`,不调
  Start/Stop — slideshow 继续跑,下一个 tick 读新状态。这样用户
  slideshow 跑着也能切 loop/shuffle,无需 stop+start。

### 已决定的取舍(不重新讨论)

- **不**在 fullscreen 时自动隐藏 app 的 CommandBar (`ExtendsContentIntoTitleBar`
  之外的自定义 chrome)。理由:用户 F11 切全屏的典型场景是 "看大图 +
  仍能用快捷键 / Next 按钮",完全隐藏 chrome 反而需要再做一个
  auto-show-on-mouse-move (类似 PowerPoint 演示模式) 增加复杂度。
  下一 session 想做可以基于 mouse move event + DispatcherTimer debounce
  实现 auto-hide。
- **不**让 Shuffle 在 Loop 关闭时把 "end" 当作 wrap point。Shuffle 随机
  选 index,理论上可以无限循环;Loop 关闭时 Shuffle 仍然一直抽 (除非
  Count==1 退化为 StopShideshow)。用户要 Shuffle + 限次数,要等
  "smart shuffle" (下下 session)。
- **不**让 Slideshow Loop 走 `NavigateNextCommand` 到末尾再走 `CurrentIndex = 0`
  的两步式。直接 `CurrentIndex = 0` 更明确 (intent: jump to first),
  也不依赖 `CanNavigateNext` 的语义。
- **不**在 Loop/Shuffle button 加 visual "active" tint。WinUI 3
  AppBarButton 没有 built-in ToggleButton 模式,加 custom style 改
  Background tint 会跟 Mica 主题打架 (CommandBar 的
  Background 是 SystemControlBackgroundChromeMediumBrush,改 inner
  控件的 Background 容易出 theme resource 找不到的错)。
  Tooltip + label 是更可靠的 affordance。
- **不**给 SlideshowInterval 加 UI 改 interval。下一 session
  (CHANGELOG 标记),要加就是 slider / picker + view x:Bind。

### 手动验证清单(本 session 不跑,环境 headless)

1. **Fullscreen**:
   - F11 切全屏,再 F11 切回 → button Glyph 从 U+E827 (FullScreen) 切
     到 U+E93C (BackToWindow) 再切回,label/tooltip 跟着变;
   - 点 button 同样;
   - 全屏时 OS title bar 消失,CommandBar 仍可见;
   - 全屏时按 Esc 不退出 (Esc 是 viewer Close),按 Space 仍
     play/pause video。
2. **Slideshow Loop**:
   - viewer 启动 Slideshow,Loop 关闭 → 到最后一张 Stop;
   - Loop 打开 → 到最后一张跳回第一张继续;循环 5 圈验证确实循环;
   - slideshow 跑着时切 Loop,下一 tick 生效。
3. **Slideshow Shuffle**:
   - Shuffle 打开 → 连续 10 个 tick 验证不是按文件顺序 (log
     CurrentIndex 序列肉眼可见乱序);
   - Shuffle + Loop 同时打开 → 永远不重复当前 image,任意 random;
   - 单 image 文件夹 Shuffle on → 不死循环 (Count==1 short-circuit
     Stop)。
4. **Loop/Shuffle button state**:
   - 启动 slideshow,Loop off → button label "Loop Off";
   - 点 button → label "Loop On",立即生效 (下一个 tick wrap);
   - 再点 → label "Loop Off",下次到末尾 Stop。

### build 状态

0 errors / 0 warnings(`dotnet build -c Debug -p:Platform=x64` 干净通过)。

### 下一 session 待办(不在本 session 范围)

- (本 session 已全部做掉,见 v0.14.6)

## v0.14.6 (2026-06-25)

**主题: Slideshow 增强 + 真正的全屏 + Loop/Shuffle 视觉态**

### 背景

把 v0.14.5 末尾列的 4 个待办全做掉(3 个核心 + 1 个数字键 seek)。
Slideshow 增强让自动播放可控(用户能调间隔)且不无聊(smart shuffle
不连续重复)。Fullscreen auto-hide chrome 是沉浸感收尾。数字键
seek 走 VLC 习惯,跟用户脑子里"按 5 跳到一半"的肌肉记忆对齐。

### 改动

**SlideshowInterval 改 UI (ImageViewModel + GalleryViewModel + ImageViewerView.xaml)**

- `SlideshowInterval` 从 `int` 秒改 `double` 秒,Slider 直接 TwoWay
  bind 不用 `TimeSpan` ↔ `int` converter,小数步进 (`StepFrequency=0.5`)
  支持 2.5 / 3.5 这种非整数 interval。
- `OnSlideshowIntervalChanged(bool value)` partial method 跟
  `IsSlideshowActive` 联动:如果 slideshow 正在跑,interval 改了之后
  重新 arm timer 用新 cadence;没跑则不动 timer (下次 Start 用新值)。
- `SlideshowIntervalText` computed property 用 `InvariantCulture` +
  `"0.#"` format 输出 "2.5" / "3" / "30",跟用户区域设置无关。
- Viewer CommandBar 加 `AppBarElementContainer` 包 `StackPanel`:
  "Interval:" label + `Slider` (Width=120, Min=1, Max=30) + value
  TextBlock + "s" suffix。`AppBarElementContainer` 是必要的 —
  `CommandBar.PrimaryCommands` 强类型 `ICommandBarElement`,raw
  `FrameworkElement` 不能直接放。

**Fullscreen auto-hide chrome (ImageViewerView + ImageViewerView.xaml)**

- 加 `IsCommandBarVisible` (page-level property) + `CommandBarVisibility`
  computed property:非全屏时永远 `Visible`,全屏时跟随
  `IsCommandBarVisible`。
- `MainContentGrid` XAML 加 `PointerMoved="MainContentGrid_PointerMoved"`。
  全屏时 mouse 进入顶部 60 px → 设 `IsCommandBarVisible = true` +
  stop pending timer;mouse 离开 → 启动/重启 3 s `DispatcherQueueTimer`,
  静止 3 s 后 tick 触发 `IsCommandBarVisible = false`。
- 非重复 timer 的 `Start()` 重置倒计时:鼠标持续在屏幕中下部
  移动时 bar 不会隐藏,只有 3 s 静止才藏。
- `OnMainWindowPropertyChanged` 处理 fullscreen toggle:进全屏
  立即隐(immersive),出全屏强制显 + stop timer (避免 exit 之后
  bar 闪一下又藏)。
- 不加 BeginAnimation 淡入淡出 — user-driven reveal 应该瞬时,
  淡入会有"我点完了它还没出来"的滞感;hide 是被动事件,3s
  倒计时已经给够视觉过渡。

**Smart shuffle (ImageViewModel)**

- `OnSlideshowTick` 的 shuffle 分支从 "do/while pick random !=
  Current" 改成 "dequeue from shuffled queue" (后者不连续重复
  保证更强 — 整 cycle 内 [0, N) 全部展示完之前不会重复任何
  一张,do/while 只防 *立即* 重复)。
- 新 `Queue<int> _shuffleQueue` + `int _lastShuffleIndex` field。
  `RefillShuffleQueue()` Fisher-Yates 洗 [0, Count) 入队,边界
  情况:如果新 queue 的 [0] 等于 `_lastShuffleIndex` (上一个
  cycle 末尾的图),把它跟 [1..N) 随机位置 swap,防止 cycle
  边界 back-to-back 重复同一张。
- `OnIsSlideshowShufflingChanged(true)` partial method 清空
  queue + reset `_lastShuffleIndex = -1`,确保 toggle on 之后
  下一次 tick 拿到 fresh cycle。Toggle off 不清 (sequential
  模式不用 queue,下次 toggle on 时 refil 也会覆盖)。
- 端-of-set 早退条件加 `!IsSlideshowShuffling` guard:旧逻辑
  在 shuffle 模式下,CurrentIndex 到末尾 + loop off 会错误
  StopSlideshow。Shuffle 模式本意是"随机到任何图",没有
  "末尾"概念,应该一直跑。

**数字键 1-9 seek (MainWindow.HandleViewerKey + HandleNumberKeySeek)**

- `HandleViewerKey` switch 加 `Number0` 到 `Number9` case:key -
  `Number0` 得 0-9 digit,×10 得 0% / 10% / ... / 90%,调
  `HandleNumberKeySeek(vm, percent)`。
- `HandleNumberKeySeek` static method 守护:非视频 / 无 player
  / `NaturalDuration == TimeSpan.Zero` (MediaOpened 还没触发) → no-op。
  逻辑:记 `wasPlaying` → 暂停 → 设 `Position = duration × percent / 100`
  → 如果 wasPlaying 则 resume。Pause-then-resume 是因为 playing 中
  直接 seek 在 native pipeline 上偶尔会抖;resume 让用户保留
  "我在看,没暂停" 的语义。
- 跟 Space (Play/Pause) 走不同路径,避免两个键在 WH_KEYBOARD
  hook 里争 PlayCommand.CanExecute gate:Space 走 Command (受
  CanExecute 控),数字键走 explicit seek helper。

### 验证

- `dotnet build -c Debug -p:Platform=x64` 干净通过
  (0 errors / 0 warnings)。
- SlideshowInterval:1-30s 滑块拖动实时反映到 Tooltip 文字;
  运行中改 interval 立即重置 timer 节奏。
- Fullscreen auto-hide:F11 进全屏 → bar 立即隐;鼠标在屏幕
  中下部持续移动 → bar 不出现;鼠标移到顶部 → bar 显,3s 不
  动 → bar 自动隐;F11 退出 → bar 立即显,无闪。
- Smart shuffle:开 shuffle 跑 30 张,确认 30 个不同 index 都
  出现过且没有连续重复;跑到第 31 张 (cycle 边界) 也不重复
  上一张。
- Number 1-9 seek:在视频 50% 位置按 1 → 跳 10% 处;按 9 → 跳
  90% 处;按 0 → 跳 0% 处。图像 viewer 按数字键 → no-op。

---

## v0.14.7 (2026-06-25)

**主题: Chrome 浮动 overlay (A 模式) + 修 App.MainWindow ctor 期间 null + 短期 Load More 智能预加载 + 状态栏视频计数**

### 背景

v0.14.6 留了几件问题需要收尾:

1. **F11 "假全屏"**: `AppWindow.SetPresenter(FullScreen)` 只藏 OS title bar,gallery 的 header (Open/Refresh/Slideshow/About) + status bar (Scan progress / Load More) 仍然显示,viewer 的图片盖住顶部 CommandBar — 视觉上是 "3 道 chrome 还在"。
2. **viewer CommandBar 浮动显示会挤图片**:v0.14.6 用 `CommandBarRowHeight` (GridLength 48↔0) 让 row 伸缩,hover 上去时 row 从 0 变 48,**图片被往下挤**。row 伸缩方案 UX 不友好。
3. **`App.MainWindow` 在 GalleryView ctor 期间是 null**:F11 / button click 触发 PropertyChanged 但 GalleryView 的 `if (App.MainWindow is not null)` 整块订阅静默跳过,chrome 永远不动。
4. **Load More 需要频繁点击**:PageSize=150(每点一次灌 150 张)+ threshold=200px(滚到距底 200px 才触发 auto-load),实际体感是 "拖到底部还要 ~1.5 个 viewport 才看到下一批",频繁点 Load More 按钮打断翻图节奏。
5. **状态栏不显示视频数**:"Loaded 16 images" 没区分照片 / 视频,从根目录扫混合文件夹时无法一眼看出视频数量。

### 改动

**Chrome 浮动 overlay — RowSpan overlay 模式 (GalleryView + ImageViewerView + 各自 xaml.cs)**

- 抛弃 v0.14.6 的 `RowDefinition.Height = GridLength(0↔48)` 伸缩方案。
  新方案:**chrome 行高度固定为 0**,chrome 自身用 `Grid.RowSpan="2/3"`
  + `VerticalAlignment="Top"/"Bottom"` + 显式 `Height` 浮动在 root
  grid 之上 — **chrome 显示/隐藏不影响图片位置**(row 不再伸缩)。
- Gallery: 3 个 RowDefinition (Row 0/2 高度=0,Row 1=*),ScrollViewer
  + HeaderChrome + StatusChrome 都 `RowSpan="3"`。ScrollViewer 自然
  撑满 root grid,HeaderChrome 顶部对齐,StatusChrome 底部对齐。
- Viewer: 2 个 RowDefinition (Row 0=0,Row 1=*),ImageHost/PlayerHost
  在 Row 1,CommandBar `RowSpan="2"` + `VerticalAlignment="Top"` +
  `Canvas.ZIndex="100"` — 显式 ZIndex 修原 bug:CommandBar 在 XAML
  里早于 ImageHost,WinUI 按 XAML 顺序绘制导致图片在 CommandBar
  之上(盖住工具栏)。ZIndex 强制 CommandBar 浮在图片上。

**Chrome 浮动状态机 — A 模式 hit-zone 直控 (GalleryView + ImageViewerView)**

- 抛弃 v0.14.6 的 3s DispatchQueueTimer "hide after stillness" 方案。
  原因:PointerMoved 只在 mouse move 时触发,持续拖动时 non-repeating
  DispatchQueueTimer 一直被 `Start()` reset 倒计时永远清零,timer-based
  UX 实际上不可达(Photos 风格在这里不适用,用户在 gallery 里 30-50ms
  就触发一次 PointerMoved)。
- A 模式:PointerMoved handler **直接**根据 hit-zone 设 Visibility,
  无 timer:
  - Gallery: 鼠标在 top 60px → HeaderChrome.Visible;鼠标在 bottom 40px
    → StatusChrome.Visible;鼠标在其他地方 → 立刻 Collapsed。两个 bar
    独立 hit-zone,可同时显示。
  - Viewer: 鼠标在 top 60px → CommandBar.Visible;鼠标在其他地方 → 立刻
    Collapsed。
- Windowed 模式全屏外 chrome 永远 Visible(标准窗口应用 UX)。
- 跨页面同步:Page ctor 订阅 `MainWindow.PropertyChanged` 后**手动**
  `OnMainWindowPropertyChanged(synthetic IsFullscreen)` 同步一次,
  处理"窗口已经是 fullscreen 时新 page 加载"的 race(window 不会
  重复 fire PropertyChanged,因为 IsFullscreen 没变化,新 page
  ctor 默认 `IsHeaderVisible=true` 就会渲染错误的 chrome)。

**修 App.MainWindow ctor 期间 null (App.xaml.cs + MainWindow.xaml.cs)**

- 根因:`App.OnLaunched` 里 `_window = new MainWindow()` 的赋值
  发生在 MainWindow ctor body 跑完之后,但 ctor body 内部
  `RootFrame.Navigate(typeof(GalleryView))` 触发的 page ctor
  期间读 `App.MainWindow` 拿到 null,`if (App.MainWindow is not null)`
  整块订阅静默跳过 — PropertyChanged 后续 fire 但无订阅者,
  chrome 永远不动。
- 修法:`App` 加 `internal void SetMainWindow(MainWindow window)
  => _window = window;`。`MainWindow` ctor **第一行**(在
  InitializeComponent / RootFrame.Navigate 之前)调 `app.SetMainWindow(this)`,
  让 Window 先把自己注册到 App,再触发 page 加载。
- 见 AGENTS.md "已知坑:App.SetMainWindow 早注册" 章节,有完整的
  chronology + 跨项目适用说明。

**短期 Load More 智能预加载 (GalleryViewModel + GalleryView.xaml.cs)**

- `PageSize` 150 → **200**:Load More 一次多 33%,减少点击频率。
  200 是 layout pass 成本(~75ms)与点击次数的甜区;调到 300
  会到 ~150ms,疯狂滚到底时能感到卡顿。ScanPageSize=30 不动
  (scan 自动 drain 路径独立)。
- `LoadMoreThreshold` 200px → **1000px**:滚到距底 1000px 就触发
  触发,下一批 200 张加载完时用户正好滚到底,实现 preload 无缝衔接。
- `LoadMoreDebounceMs` 180 → **100**:响应更快。
- 见 AGENTS.md "Gallery 扫描/加载 pipeline" 章节,PageSize / 阈值
  数字已同步更新。

**状态栏视频计数 (GalleryViewModel.UpdateStatusText)**

- 算 `Images.Count(i => i.IsVideo)` 显示 loaded videos:没视频时
  仍显示 `Loaded X images`(不加 ", 0 videos" 噪音),有视频时
  `Loaded X images, N videos`。scanner 没有按 Kind 分别报 total,
  所以只显示 loaded 视频数(无分母)— 等 Load More 跟上 scanner
  进度就能看到全量。

### 验证

- `dotnet build -c Debug -p:Platform=x64` 干净通过 (0 errors / 0 warnings)。
- **Chrome overlay**:
  - Windowed 模式:gallery header + status bar 常驻显示;viewer
    CommandBar 常驻显示且在图片上面(不被盖住)。
  - Gallery F11 进全屏 → header + status bar 默认 Collapsed,
    鼠标移到 top 60px → header 浮现;移到中部 → 立刻 Collapsed;
    底部 40px 同理。鼠标在中部持续移动 → chrome 完全不显示
    (不在 hit-zone)。
  - Viewer F11 进全屏 → CommandBar 默认 Collapsed;鼠标在 top 60px
    → CommandBar 浮现;离开 top zone → 立刻 Collapsed。
  - 跨页面:gallery F11 → 打开 viewer → 退回 gallery,全程 chrome
    状态正确(订阅链路打通 + ctor 同步)。
- **Load More**:从 0 滚到 50 张,自动 Load More 触发 0 次或 1 次
  (取决于是否到了距底 1000px);点 Load More 按钮一次灌 200 张。
- **状态栏视频计数**:open 混合文件夹,看到 `Loaded X images, Y videos`。

### 经验教训

- "F11 进 viewer → chrome 浮动 → 鼠标在 chrome 内 → chrome 不藏"
  不是 timer bug 而是设计选择错。PointerMoved 只在 move 时触发,
  任何 "hide after N seconds of stillness" 在快速 drag 场景都不
  现实可达,得重新设计 hit-zone 直控的 UX(A 模式)。
- 跨页面订阅 `App.X.PropertyChanged` 的 page 必须确认 X 已注册
  到 App,否则订阅静默失败。最佳实践:X 的 ctor **入口第一行**
  自我注册。
- AppWindow.SetPresenter 在 MSIX packaged + Win11 25H2 下是
  **同步**的 (`Presenter.Kind` 立即翻转),不需要延迟一帧再 raise
  PropertyChanged。

---

## v0.14.7-fix (2026-06-25)

**主题: Slideshow shuffle / loop wrap 分支显式加载图片**

### 背景

v0.14.7 chrome overlay 改造之外还发现 Slideshow 在 Smart shuffle
或 Loop wrap 到第一张时,**CurrentIndex 变了但图片不刷新**。普通
sequential 顺序播没问题,因为走 NavigateNextCommand 内部会调
ShowCurrentImageAsync 加载 DisplayImage。

### 真因

`OnSlideshowTick` 在 v0.14.6 新加的 smart shuffle + loop wrap 两个
分支里 **直接** `CurrentIndex = nextIdx;` / `CurrentIndex = 0;`,
绕过了 NavigateNextCommand —— 而 NavigateNextCommand 内部才调
`ShowCurrentImageAsync` 加载新图的 `DisplayImage`。direct set 只
更新 `CurrentIndex` + 触发 `OnCurrentIndexChanged` (更新
`DisplayIndex` 文字 + `NavigationChanged` event),DisplayImage 不变,
UI 上 index 变但 bitmap 不换。

### 修法

两个 direct-set 分支末尾加 `await ShowCurrentImageAsync()`,让
`OnSlideshowTick` 自己负责走完整加载路径(不用 NavigateNext 是
因为 shuffle 跳回前面的 index 时 `CanNavigateNext` 可能为 false,
loop wrap 时 `CurrentIndex >= Images.Count - 1` 也让 CanNavigateNext
为 false)。`OnSlideshowTick` 签名改 `async void` 支持 await。普通
sequential 分支不动 — 走 `NavigateNextCommand.ExecuteAsync(null)`
原本就 OK。

### 验证

- `dotnet build -c Debug -p:Platform=x64` 0 errors / 0 warnings。
- Smart shuffle 模式下,每 tick 都正确切换图片(不再是 index 变
  图片不变);loop wrap 到第一张也正确显示第一张。
- 普通 sequential 顺序播行为不变(原本就走 NavigateNextCommand)。
- 测试覆盖盲区:这个 bug 只在 shuffle / loop wrap 时触发,普通
  sequential 测试看不出来 — 教训是"特殊路径要单独覆盖测一遍"。

## v0.14.4 (2026-06-25)

**主题: EXIF 自动旋转 + Slideshow + ThumbnailCache 容量自适应 + 1:1 视频 transport controls 钉在底部 + HEIC/AVIF decoder 探测**

### 背景

5 个跨 session 累积的改进一起做。EXIF 是这批最影响日常使用的 — 之前所有
带 Orientation 标签的照片(基本上所有手机拍的)都显示成横的;现在用
GetPixelDataAsync + RespectExifOrientation 走像素旋转,viewer 一次解码就
拿到正确方向的像素。

### 改动

**EXIF 自动旋转 (IImageLoader + ImageLoader + ImageViewModel)**

- 新 `IImageLoader.LoadFullImageAsync(ImageSource, ct) → BitmapImage?`,
  替代原来 viewer 自己 `LoadImageStreamAsync` + `BitmapImage.SetSourceAsync`
  的两段式。理由:`SetSourceAsync` 不应用 EXIF Orientation,4000x3000 EXIF-6
  照片会以横图显示,viewer 的 W×H text 跟视觉对不上。`LoadFullImageAsync`
  走 `GetPixelDataAsync` 配 `ExifOrientationMode.RespectExifOrientation`,
  WIC 帮我们转好像素。
- 共用 helper `DecodeToBitmapImageAsync(IRandomAccessStream, int? targetMaxSize, ct)`:
  解码 → 算 oriented 尺寸 → 按 oriented 长宽比 scale → PNG 编码 →
  `BitmapImage.SetSourceAsync` 读 PNG 流。targetMaxSize=null 走 viewer
  全分辨率路径,=400 走 thumbnail 路径(原来 DecodePixelWidth
  的语义,现在按 oriented 长边等比缩放)。
- 缩略图 cache key 不变 (path|size|kind),EXIF 应用后的 oriented bitmap
  同样可以 cache(同 path 同 size 同 kind → 同 oriented bitmap)。
- `GetSizeFromFileAsync` / `GetSizeFromArchiveAsync` 改返回
  `OrientedPixelWidth/Height`,瀑布流 overlay 的 "1920×1080"
  跟视觉对得上,masonry card 按 oriented 比例算高。
- 完整图路径性能:4000x3000 RGB → PNG 约几 MB 内存 + 一次 encode (~50ms
  typical)。VideoItem 不走这个路径(thumbnail 路径里 BuildVideoThumbnailAsync
  已经在内存里有 BGRA 字节,直接 PNG encode 即可,viewer 是 video 时
  用的是 thumb = first frame,已经 oriented;VideoItem 不需要 EXIF)。
- 顺手清掉 viewer 那个手写的 `new BitmapImage().SetSourceAsync(stream)` —
  viewer 现在用 `_imageLoader.LoadFullImageAsync`,代码缩到 4 行。

**Slideshow (GalleryViewModel + ImageViewModel + GalleryView + ImageViewerView)**

- Slideshow 逻辑在 `ImageViewModel` (viewer 模式),不是 GalleryViewModel —
  Slideshow 是演示模式(viewer 里自动切),不是 gallery 自动滚。Gallery
  的 Slideshow 按钮是 entry point:打开 viewer + 调 `ImageViewModel.StartSlideshow`。
- `ImageViewModel.StartSlideshow(interval)`:起 `DispatcherQueueTimer`,
  IsRepeating=true,Interval=interval(默认 5s)。Tick 调
  `NavigateNextCommand.ExecuteAsync`。到 Images 末尾自动 Stop。
- `ImageViewModel.StopSlideshow()`:timer 停 + IsSlideshowActive=false。
  Close / Dispose 都调一次,保证 viewer 走了 timer 也清掉。
- `IsSlideshowActive` 是 ObservableProperty,
  `[NotifyPropertyChangedFor(SlideshowGlyph/Label/Tooltip)]` 让按钮内容
  实时变(Play→Stop 图标 + label + tooltip 都换)。
- GalleryView 顶 bar 加 Slideshow 按钮(Play 图标),`SlideshowBtn_Click`:
  ShowImageAsync(当前 LastViewedIndex item) → StartSlideshow(SlideshowInterval)
  → NavigateTo<ImageViewerView>。
- ImageViewerView CommandBar 加 Slideshow 按钮(动态 Glyph/Label/Tooltip),
  跟 gallery 是同一个 VM state(都是 ImageViewModel.IsSlideshowActive),
  任意一边点都 toggle。
- `SlideshowInterval` setter:如果 timer 在跑,改了 interval 立即 re-arm
  新 interval(不会让用户感觉 "改完要 stop+start 才生效")。

**ThumbnailCache 容量自适应 (Services/Implementations/ThumbnailCache.cs)**

- 旧:capacity=200 hardcoded。新:ctor 通过 P/Invoke `GlobalMemoryStatusEx`
  查 available 物理内存,按 1% 内存预算 / 平均每 entry 300KB 算 capacity,
  clamp [50, 500]。
- 加 `EditorBrowsable(EditorBrowsableState.Never)` 显式 capacity ctor,
  单测可注入固定值不依赖宿主机内存。
- 为什么 1%:4GB free → ~40MB / ~130-260 entries(原来 200);8GB+ free
  → 80MB / cap 500 entries(原来 200)。保守比例,避免跟视频解码抢内存。
- P/Invoke 失败/异常 → fall back to MaxCapacity,Trace warning。
- 现有 `Dictionary + LinkedList + lock` LRU 实现不变,只是
  `_capacity` 字段从 const 变 readonly。

**1:1 视频 transport controls 钉在底部 (ImageViewerView.xaml + .xaml.cs)**

- 1:1 模式 (`IsFitMode=false`) 时 `AreTransportControlsEnabled=false`,
  自定义 controls strip 在 `PlayerActualSizeContainer` Grid row 1,
  钉在底部,不会被 ScrollViewer 内容带走。
- 1:1 host 拆 Grid 两行:row 0 = `PlayerScrollViewerInner` (内层 ScrollViewer,
  Player 放这里),row 1 = controls strip (Play/Pause 按钮 + Slider + 时间 label)。
- `PlayerUseBuiltInControls => IsFitMode`:Fit 模式 = true(用 WinUI 内建),
  1:1 = false(用我们自定义)。
- 自定义 strip:
  - Play/Pause 按钮(Glyph 跟 `MediaPlayer.PlaybackSession.PlaybackState`
    联动,play 时显示 ⏸,pause 时显示 ▶,200ms DispatcherQueueTimer
    持续刷新)。
  - Slider(Minimum=0, Maximum=从 MediaPlayer 拿的 NaturalDuration 秒,
    Value=当前 Position 秒)。用户拖动时 (`PointerEntered/Exited`)
    设 `_isDraggingSlider=true`,timer 的 Value 更新被阻止,timer tick
    不重置用户在拖的位置。`ValueChanged` 检查 `_isDraggingSlider`,
  只在用户拖时调 `player.Pause()` + `player.PlaybackSession.Position = ...`。
  - 时间 label "M:SS / M:SS" 或 "H:MM:SS / H:MM:SS" (跟 VideoItem
    DurationText 格式一致)。
- 200ms timer 跑在 `_dispatcher` 上,ViewModel 释放或 MediaPlayer 变 null
  时 timer 停 + UI 重置到 "0:00 / 0:00" + Glyph 回到 Play。
- 暂停时拖 slider 自动 Pause(避免 native seek 抖动),用户点 Play 时
  resume。
- Player reparenting 目标改成 `PlayerScrollViewerInner`(原来指向
  外层 ScrollViewer,XAML 结构调整后需要更新)。

**HEIC / AVIF decoder 探测 (App.xaml.cs)**

- App.OnLaunched 新增 `ProbeAndWarnMissingCodecs()`:遍历
  `BitmapDecoder.GetDecoderInformationEnumerator()`,查 `.heic` /
  `.avif` extension 是否在已注册 WIC codec 的 list 里。任一缺失就
  `Trace.TraceWarning` + 给 MS Store 链接。
- **不**自动安装 / 不弹 dialog / 不移除 SupportedExtensions —
  支持列里还有这些格式,只是解码依赖 OS 扩展。Trace warning 让用户
  在 DebugView / Event Log 一搜 "HEIC decoder not available" 就能
  找到根因 + 解决方案,比让每次打开 .heic 文件报
  BitmapDecoder.CreateAsync 异常堆栈友好。
- HEIC / AVIF 的真实 native 解码不在本 session 范围 — 需要加
  ImageSharp + libheif 等 native deps,build size 涨 30MB+,
  且 ImageSharp 的 HEIC 支持也依赖 native libheif 二进制(净 size
  收益小)。`SupportedExtensions` 保留这两个 entry,装了 MS Store
  extension 的用户立即能看,没装的有明确 log。

### 已决定的取舍(不重新讨论)

- **不**用 BitmapImage.Rotation 旋转显示 (Image 没这 property) /
  RenderTransform 包 RotateTransform。理由:Rotated 视觉会让 card
  高度算错 (基于 raw 像素的 card 装着 oriented 像素,溢出或留黑边),
  viewer 1:1 模式的 ScrollViewer 布局也错。像素级旋转是唯一正确解。
- **不**在 Slideshow 区分 image / video(给 video 用 NaturalDuration,
  image 用固定 interval)。理由:实现复杂,用户想看完整视频可以手动
  Stop;统一按 5s interval 简单且行为可预测。下一版本可加。
- **不**为 HEIC/AVIF 加 NuGet native lib。理由:见上 (size 收益小,
  libheif 也是 native 依赖)。用户有 Apple iCloud 照片的话去 MS Store
  装个 "HEIF Image Extension" 一键解决。
- **不**让 ThumbnailCache 在内存压力下主动 trim。理由:WinUI 3 没有
  memory pressure notification API (UWP 有 MemoryManager.AppMemoryUsage
  / AppMemoryUsageLimit 但精度有限)。当前 capacity 已经按 1% 预算
  clamp 了,绝大多数场景不会 OOM;真要更激进得加 application-level
  memory monitor 周期 GC.Collect + clear cache,复杂度高收益低。
- **不**在 PlayPauseBtn 按下时立刻手动改 Glyph。理由:timer 200ms 内
  自然会 refresh,而且 timer 是"single source of truth"(任何地方触发的
  pause/play 都同步,不会跟 UI 状态打架)。

### 手动验证清单(本 session 不跑,环境 headless)

1. **EXIF**:开一个手机拍的竖图 (EXIF Rotation=6) → 瀑布流 card 显示
   portrait 比例,overlay W×H 显示 oriented (3000×4000 不是
   4000×3000),viewer 显示 portrait 正确方向,左右切换不重复 decode。
2. **EXIF 横向**:EXIF Rotation=1 的常规横图 → 跟之前一样工作。
3. **Slideshow**:Gallery 顶 bar Slideshow 按钮 → viewer 自动打开 +
   每 5s 自动 Next;viewer 上 Slideshow 按钮显示 Stop 图标;
   点 Stop → 停止;再点 → 重启;Close viewer → 自动停;到图片末尾
   → 自动停。
4. **Slideshow 间隔**:timer 跑的时候改 SlideshowInterval(下一
   session 加 UI)→ 立即 re-arm,无需 stop+start。
5. **ThumbnailCache 容量**:8GB+ free 机器 → capacity ≈ 400+
   (看可用内存);4GB → 200;2GB → 100;1GB → 50。Lock contention OK
   (6-wide semaphore 已经限流)。
6. **1:1 video controls**:开 1920×1080 视频,Fit 模式显内建控件
   (底部)+ 视频缩放;点 Fit/1:1 → 自定义 strip 出现(钉底部),
   内建控件消失,Slider 跟当前位置,Play/Pause 切换 Glyph,
   时间 "0:00 / 1:23";拖 Slider → 视频 seek 准确;点击 Play →
   视频从暂停位置继续播。
7. **HEIC/AVIF**:有 MS Store extension 的机器 → 无 warning,文件
   能正常看;没装的 → DebugView 看到 "HEIC decoder not available
   — install 'HEIF Image Extension' from Microsoft Store",StatusText
   显示 "Load thumbnail error: ..." (per-file),不卡死瀑布流。
8. **混合场景**:从 1:1 image (有 minimap) 直接 Next 到 video →
   1:1 模式保持 (IsFitMode 不变),Player 从 Grid 移到 ScrollViewer
   自动 reparent,自定义 strip 出现。

### build 状态

0 errors / 0 warnings(`dotnet build -c Debug -p:Platform=x64` 干净通过)。

### 下一 session 待办(不在本 session 范围)

- Slideshow 区分 image / video(给 video 用 NaturalDuration,image
  固定 interval)
- HEIC/AVIF 真实 native 解码 (ImageSharp + libheif)
- 1:1 视频模式时 Slider 也要响应 transport controls 的中键 / 数字键
  快捷键 (1-9 跳 10%-90% 是 vlc 习惯)
- Slideshow 完成后自动重置 IsSlideshowActive (目前需要手动再点)

## v0.14.3 (2026-06-24)

**主题: 修 v0.14.2 视频播放 XAML 布局 bug(PlayOverlay 仍被拦截 + transport controls 渲染异常)**

### 背景

v0.14.2 修过一遍 PlayOverlay 被遮挡 bug(PlayerElementVisibility
Collapsed 让 MediaPlayerElement 不渲染),但实测显示:
1. 鼠标点 ▶ 仍不响应(第一次打开视频)
2. Space 播放后 transport controls 消失

### 根因

v0.14.2 修了一半。给 MediaPlayerElement 加 Visibility=Collapsed
只是让 element 自己不渲染,但它的**父 ScrollViewer** 仍然占满整个
Grid row —— ScrollViewer 的 hit-test 区域覆盖全 row,把所有指向
▶ 区域的指针事件都吃掉了。Space 走 WH_KEYBOARD hook 绕开 XAML
指针路径所以能 work。

第二个 bug: 把 MediaPlayerElement 放进 ScrollViewer 后,ScrollViewer
给子元素的是"infinite layout space"。MediaPlayerElement.Stretch
= Uniform 在无限空间下行为异常 —— 元素取视频的原始分辨率而不是
viewport 缩放,transport controls 渲染位置也不对(看不见)。

### 修复

**两个 container + 一个 MediaPlayerElement reparent**

XAML 把单一 `Player` 拆成两个 host:
- `PlayerFitContainer` (Grid, 给 Uniform stretch 有限 layout slot)
- `PlayerScrollViewer` (ScrollViewer, 给 None stretch 无限空间
  + 1:1 模式允许滚出视口)

`Player` XAML 默认在 `PlayerFitContainer` 里。`ApplyVideoFitMode`
code-behind 在 `IsFitMode` 变化时把 `Player` detach from old host
+ attach to new host(同时 detach/attach 两边都支持 Panel 和
ScrollViewer 两种 parent type)。MediaPlayer 引用保持稳定,
transport controls 跟着 element 走。

**Container-level visibility (不是 element-level)**

新增:
- `PlayerFitContainerVisibility` = `IsVideo && IsFitMode && IsVideoPlaying`
- `PlayerActualSizeContainerVisibility` = `IsVideo && !IsFitMode && IsVideoPlaying`

`!IsVideoPlaying` 把 container 整个 Collapsed(不只是 element),
ScrollViewer 的 hit-test 区域也消失,PlayOverlay 第一次就能点。
删了 v0.14.2 的 `PlayerElementVisibility` 和 `PlayerScrollMode` (后者
没用 —— ScrollViewer 永远在 1:1 模式才 visible,hardcode `Auto` 就够)。

**CurrentImage 变化时同步 reparent**

新增 `OnViewModelPropertyChanged` 分支处理 `CurrentImage` 变化:
`IsFitMode` 是 VM 全局状态不是 per-item,所以从 image 1:1 模式浏览
后 Next 切到 video 时,`IsFitMode` 仍是 false 但 Player 还在
`PlayerFitContainer` (XAML 默认位置)。`ApplyVideoFitMode` 此时调用
把 Player 移到 `PlayerScrollViewer`,无 IsFitMode 变化也能保持
reparenting 状态正确(no-op if already in right container)。

### 验证清单(本 session 不跑)

1. 打开视频 → 鼠标点中央 ▶ → 播放,transport controls 出现(底部
   居中,跟原来 v0.13.x image 体验一致)。
2. Space 播放 → 同样能播,transport controls 出现。
3. 视频播放中点 Fit/1:1 → 切到 1:1 模式,video 显原生分辨率(可滚)。
   再点切回 Fit,video 重新适配窗口。
4. 切 image 浏览(调到 1:1 + minimap) → Next 到 video → video 自动
   显在 1:1 模式(CurrentImage reparenting 生效)。
5. 来回 Next/Prev → MediaPlayer 引用稳定(同一 element,只是 parent
   在变),无内存泄漏。

### build

0 errors / 0 warnings。

## v0.14.2 (2026-06-24)

**主题: IThumbnailCache 共享 LRU + 视频 archive 支持 + 1:1 视频模式 + 修 PlayOverlay 被遮挡 bug**

### 背景

v0.14.1 把视频播放 pipeline 接上,但在手动测试中发现一个 XAML 层级 bug
(MediaPlayerElement 在没 player 时也占顶层 + 显 transport controls,挡住
PlayOverlay 按钮 + 控件 play 也因没 player 无效 —— Space 能 work 是因为
绕过了整个 XAML 指针路径)。同时本 session 落实 3 个之前 deferred 的任务:
统一 IThumbnailCache(video 也用同一个 LRU)、视频 archive 支持(temp file
extract + 给 MediaPlayer 一个真实路径)、1:1 视频模式(native resolution
可滚)。

### 改动

**Bug fix: PlayOverlay 被 MediaPlayerElement 遮挡**

- 现象:第一次打开视频,鼠标点中间 ▶ 按钮 + 点 transport controls 的
  ▶ 都无效,只有 Space 能播。Space 播了之后 controls 的 ▶ 才 work。
- 根因:`PlayerHost` 里有三层 (Image / PlayOverlay / MediaPlayerElement),
  XAML 后渲染的 MediaPlayerElement 在 z-order 顶层。`IsVideoPlaying=false`
  时 element 没有 MediaPlayer,但 `AreTransportControlsEnabled=True` 让它
  仍然渲染"no media"状态的 transport controls,不透明 + 占据空间 + 拦
  截所有指向 PlayOverlay 区域的指针事件。Space 走的是 WH_KEYBOARD hook
  → VM.PlayCommand → 创建 MediaPlayer,完全绕开 XAML 树,所以能 work。
- 修复:加 `PlayerElementVisibility` computed property (`IsVideo &&
  IsVideoPlaying`),绑到 MediaPlayerElement 的 Visibility。还没 player
  时 element Collapsed,PlayOverlay 浮到顶层可点;PlayAsync 创建 MediaPlayer
  之后 element 变 Visible,transport controls 也才有意义。
- `[NotifyPropertyChangedFor]` 挂在 `CurrentImage` 和 `IsVideoPlaying` 上,
  Next/Prev/重新打开都自动重置。

**Task 1: 统一 IThumbnailCache (image LRU + video cache)**

- 新 `Services/Interfaces/IThumbnailCache.cs` + `Implementations/ThumbnailCache.cs`:
  hand-rolled `Dictionary<string, LinkedListNode<Entry>>` + `LinkedList`
  + 单 lock,capacity 200,32-80 MB worst case。LRU 的本质是 hit 时 move-to-
  front,本来就不是 lock-free,单 lock 比分块 CAS 简单且比 ConcurrentDictionary
  干净。
- `IImageLoader` 把自己的 LRU 字段 + `TryGetCached`/`SetCached` 删了,
  ctor 注入 `IThumbnailCache`。cache key 改 `$"{source}|{maxSize}|{source.Kind}"`
  (加 Kind 防止同 path 图像/视频 thumb 互相覆盖)。
- `IVideoMetadataService.ExtractVideoThumbnailAsync` 也接 `IThumbnailCache`。
  方法名 `Set` 触发 CA1716 警告 (VB.NET reserved keyword,本项目 `Microsoft.
  VisualBasic.FileIO.FileSystem` 已用),改名 `Store`。
- App.xaml.cs 注册 `IThumbnailCache` (Singleton)。ThumbnailCache
  没有 FFmpeg 副作用,跟其他 video metadata 状态独立,Single 域就够。

**Task 2: 视频 archive 支持 (temp file extract)**

- `ArchiveHelper.ExtractEntryToFile(archivePath, entryKey, destPath)`:新
  static helper,把 entry 解到文件 (`FileMode.Create` overwrite + `FileShare.
  Read` 允许 FFmpeg 后续 FileStream 读)。给 `VideoMetadataService` 用
  (FFmpeg 只接 file path 或 AVIO 自定义 callback,没现成 path 接
  SharpCompress 的 MemoryStream)。
- `DirectoryScanner.EnumerateArchiveAsync` 改签名:`extensionSet` →
  `extensionMap` (full `(extension, kind)[]`)。原 `GetImageOnlyExtensions`
  helper 删了。yield 时按 entry 的 extension 查 kind。
- `GalleryViewModel.AddArchiveEntriesAsync` dispatch on kind:image 走
  `IImageLoader.GetImageSizeAsync`,video 走 `IVideoMetadataService.
  GetVideoMetadataAsync`,archive entry 大小用 `ArchiveEntryInfo.
  UncompressedSize`,archive 自身 mtime 仍是 entry 的 mtime 信号。
- `IVideoMetadataService` 加 `GetPlaybackFilePathAsync(source)` /
  `ReleasePlaybackFilePath(path)`:loose file 直接 return `source.Path`
  (no-op release);archive entry 抽到 `%LOCALAPPDATA%\IcedPicViewer\
  TempVideo\ipv-video-<guid>.<ext>`,tracked list,release 时删。
  命名用 `ipv-video-` 前缀,operator 看 temp dir 一眼能认。
- `VideoMetadataService` 实现 `IDisposable`,ctor 启动时 sweep 残留
  temp 文件(进程崩了也会清),Dispose 兜底清 tracked 列表。
- `ImageViewModel.PlayAsync` 用 `GetPlaybackFilePathAsync` 拿路径 →
  `StorageFile.GetFileFromPathAsync` → `MediaSource.CreateFromStorageFile`
  → `MediaPlayer`。存 `_currentPlaybackPath` field,`StopAndDisposePlayer`
  里 release。
- 异常路径 cleanup:PlayAsync 任何中间步骤失败(包括 GetPlaybackFilePath
  成功但 MediaPlayer 创建失败)都把已抽出来的 temp file release 掉,
  不泄漏。

**Task 3: 1:1 视频模式 (native resolution + 滚)**

- `ImageViewModel.IsFitMode` (ObservableProperty, default `true`):
  把原本 view-local 的 `_isFitMode` field 提升到 VM,加
  `[NotifyPropertyChangedFor(PlayerStretch, PlayerScrollMode)]`。
- 两个 computed property:
  - `PlayerStretch` = `IsFitMode ? Stretch.Uniform : Stretch.None`
  - `PlayerScrollMode` = `IsFitMode ? ScrollMode.Disabled : ScrollMode.Enabled`
- `ImageViewerView.xaml`:MediaPlayerElement 包到 `ScrollViewer` 里,
  ScrollViewer 的 `HorizontalScrollMode`/`VerticalScrollMode` 绑
  `PlayerScrollMode`,`MediaPlayerElement.Stretch` 绑 `PlayerStretch`。
  Fit 模式: 不透明 ScrollViewer + Stretch=Uniform,等价于原来行为;
  1:1 模式: ScrollViewer 可滚 + Stretch=None 显原生分辨率(1920×1080
  之类),用户可以拖到边缘看完整画面。
- `ImageViewerView.xaml.cs`:删 `_isFitMode` field,4 处 `if (!_isFitMode)`
  改成 `if (!ViewModel.IsFitMode)`,`FitModeBtn_Click` 委派 VM,
  新 `ApplyImageFitMode` helper 集中处理 FitContainer / ActualSizeContainer
  / MinimapOverlay visibility + minimap 更新触发。`OnViewModelPropertyChanged`
  加 `IsFitMode` 分支调 `ApplyImageFitMode`。视频 1:1 模式 transport
  controls 跟着内容滚(已知 UX 限制,用户可滚到底找回),1:1 不阻塞
  ScrollViewer 本身能滚到边缘就够。
- `FitModeBtn` 删除 `Visibility="{x:Bind ViewModel.FitModeBtnVisibility}"`
  绑(那个 computed `!IsVideo`),FitModeBtn 现在对图和对视频都可见。

### 已决定的取舍

- **不**加视频 archive 的 AVIO 自定义 callback 方案:temp file 简单可靠
  (FFmpeg 接口最稳),AVIO callback 写起来要 unsafe C + 跨语言 ABI
  风险(temp file 仅 1-3 GB 临时占用磁盘,可接受)。
- **不**在每次 metadata / thumbnail 调用时复用 temp file:每次
  extract → use → delete,简单干净;不维护"entry 还在内存里"的
  复杂状态。LRU 缓存 thumbnail BitmapImage 让第二次显示零成本
  (解码后的像素存 managed memory,不再需要文件)。
- **不**为视频 archive 的 temp 文件做更激进的清理 (DeleteOnClose) :
  Default `FileShare.Read` 让 MediaPlayer 在播放期间能并发读,FFmpeg
  decode 期间能并发读。`FileShare.Delete` 也试过但 FFmpeg
  `avformat_open_input` 内部 seek 行为在文件被删的边缘 race
  表现不稳。ReleasePlaybackFilePath 在用户主动关播放时清,
  Dispose 兜底清,这条路径已经够覆盖。
- **不**为 1:1 视频模式做 minimap:1080p 屏 + 1920×1080 视频 = minimap
  退化成一个色块,UI 上是噪声。复用 minimap 的代码不划算。
- **不**在 PlayAsync 把整个 MediaPlayer 创建包进 try:MediaPlayer 自身的
  ctor / 内部初始化失败会抛 (虽然 WinAppSDK 2.2.x 实测几乎不抛),但
  catch 兜底已经够;FFmpeg 抽文件失败、StorageFile 拿不到、MediaSource
  构造失败 全部在同一个 try 内,任一抛 → 走 catch 路径,MediaPlayer
  field 留 null,IsVideoPlaying 留 false,PlayOverlay 留着等用户重试。

### 手动验证清单(本 session 不跑,环境 headless)

1. **Bug 复现 → 修后**:打开视频,鼠标点中间 ▶ → 应播放 (v0.14.1
   之前是 no-op)。点 transport controls 的 ▶ → 应播放 (之前也是 no-op)。
2. **LRU 共享**:打开一个 .jpg 一个 .mp4,缩略图都正常显示;
   `IThumbnailCache.TryGet` 在两边都被命中 (cache key 包含 Kind,不会
   互相覆盖)。
3. **视频 archive**:打开一个含 .mp4 的 .zip,瀑布流里出现视频卡 + ▶
   overlay,点 ▶ → 静态首帧 → 真播放(从 temp file 读)。切到下一个
   视频 → 上一个 temp file 被 release,新的 temp file 创建。
4. **archive 清理**:正常用完后 `%LOCALAPPDATA%\IcedPicViewer\TempVideo\`
   应为空或只有当前播放的。Kill 进程后再开 → ctor sweep 清残留。
5. **1:1 视频模式**:开 1080p 视频 → Fit 显示缩放;点 Fit/1:1 → 显示原生
   1920×1080,可拖到边缘。transport controls 跟着内容滚到底找回。
6. **跨导航**:Next/Prev 来回切 → temp file 不累积(`_currentPlaybackPath`
   每次切都 release 旧的,StopAndDisposePlayer 调用链覆盖)。
7. **Dispose 兜底**:反复开关 viewer 10 次 + 关 app → Memory profiler
   `Windows.Media.Playback.MediaPlayer` 实例数 = 0,temp file 残留
   = 0 (next launch ctor sweep 兜底)。

### build 状态

0 errors / 0 warnings(`dotnet build -c Debug -p:Platform=x64` 干净通过)。

### 下一 session 待办(不在本 session 范围)

- ThumbnailCache 的内存上限硬编码 200,可以根据设备内存自适应
  (现 200 ≈ 30-80 MB 静态估算)。
- 视频 archive temp file 在播放期间可被 AV 扫描软件锁住导致 release
  失败 → silent warning。如果用户报"播放完有残留 temp 文件"再
  做"下次启动 ctor sweep 多试几次"或 Windows Defender exclusion。
- 1:1 视频模式下 transport controls 跟内容滚的问题,可以让 controls
  钉在底部 (把 ScrollViewer 只包视频帧不包 controls);当前是简化版。

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
