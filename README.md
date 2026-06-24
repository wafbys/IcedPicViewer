# IcedPicViewer

简单的图片查看器，基于 WinUI 3 构建。

## 功能

- 文件夹浏览
- 缩略图网格视图（瀑布流布局）
- 增量加载 + 滚动到底部自动加载更多（首次只加载前 150 张，支持手动/自动继续加载）
- 图片查看（Fit / 1:1 模式切换）
- 键盘导航（左右箭头切换图片，ESC 关闭）
- 图片信息显示（文件名、尺寸、文件大小、所在目录 / 压缩包）
- GIF 动画支持
- 窗口状态记忆（关闭后重新打开恢复位置和大小）
- 删除图片（右键菜单 / Delete 键删除，支持回收站）
- **读取压缩包内图片**（ZIP / RAR / 7Z + tar.gz/bz2/xz）：Open Folder 后压缩包内图片与普通图片一起展平到瀑布流中；损坏 / 加密的压缩包在状态栏报告

## 操作指南

### 缩略图视图（主界面）

| 操作 | 行为 |
|------|------|
| 双击图片 | 打开图片查看器 |
| 鼠标悬停图片 | 显示半透明遮罩，底部显示图片名称 |
| 右键点击图片 | 弹出上下文菜单，可选择"打开文件位置"或"删除" |
| 向下滚动到底部 | 自动触发加载更多（带防抖，适合大文件夹） |
| 点击底部“Load More”按钮 | 手动加载下一批图片（自动加载的后备方式） |

> 大文件夹首次只加载前 150 张图片，继续滚动或点击按钮可按需加载后续内容，同时保持原有瀑布流视觉效果。

### 图片查看器

| 操作 | 行为 |
|------|------|
| 点击工具栏 Close 或按 ESC | 关闭查看器，返回缩略图视图 |
| 点击工具栏 "<" / ">" 或按左右箭头 | 切换上一张 / 下一张图片 |
| 点击工具栏 Fit / 1:1 | 切换显示模式（适应窗口 / 实际像素） |
| 右键点击任意位置 | 弹出上下文菜单，可选择"打开文件位置"或"删除" |
| 按 Delete 键 | 删除当前图片（本地图片移至回收站，网络图片需确认） |
| 在 1:1 模式下拖动小地图 | 快速导航大图 |

### 删除行为说明

- **本地磁盘图片**：直接移至回收站，不弹出确认对话框
- **网络路径图片**（如 \\server\share）：弹出确认对话框，确认为网络路径后执行删除
- **压缩包内图片**：**不支持删除**。点击删除会弹出 ContentDialog 标题"无法删除"并说明原因；如需删除，请到文件资源管理器中处理整个压缩包（本工程不实现"重写压缩包以移除某 entry"功能，代价大）

## 版本

- v0.14.7 - Chrome 浮动 overlay (RowSpan + A 模式 hit-zone) + 修 App.MainWindow ctor 期间 null bug + 短期 Load More 智能预加载 (PageSize 150→200, threshold 200→1000px) + 状态栏加视频计数
- v0.14.7-fix - Slideshow shuffle / loop wrap 分支显式 await ShowCurrentImageAsync (修 v0.14.6 引入的 regression:direct CurrentIndex set 绕过 NavigateNextCommand → DisplayImage 不刷新)
- v0.14.6 - Slideshow interval slider 改 double + Smart shuffle 整 cycle 内不重复 + OnIsSlideshowShufflingChanged 清 queue + 数字键 0-9 跳到 0%/10%-90% (VLC 习惯)
- v0.14.5 - 真正的全屏 (MainWindow.IsFullscreen 绑 AppWindow.Presenter.Kind + F11 WH_KEYBOARD hook 拦截) + Slideshow loop / shuffle 按钮 + 视频 archive 支持
- v0.14.4 - EXIF 自动旋转 + Slideshow 初始集成 + ThumbnailCache 容量自适应 + 1:1 视频 transport controls 钉在底部 + HEIC/AVIF decoder 探测
- v0.14.3 - 修 v0.14.2 视频播放 XAML 布局 bug (PlayOverlay 仍被拦截 + transport controls 渲染异常)
- v0.14.2 - IThumbnailCache 共享 LRU + 视频 archive 支持 + 1:1 视频模式 + 修 PlayOverlay 被遮挡 bug
- v0.14.1 - 视频播放集成 (MediaPlayerElement + Space 键盘 + 完整 lifecycle)
- v0.14.0 - 视频数据通路 (MediaItem/VideoItem + FFmpeg + ▶ overlay + About page + LGPL)
- v0.13.x - Refresh 按钮 + 坏 archive 不中断扫描 + 单图模式键盘导航修复(12 commits, WH_KEYBOARD thread-scope hook)
- v0.12.0 - 读取压缩包内图片 (ZIP/RAR/7Z) + 状态栏错误汇总 + 压缩包内图不可删 + overlay 显示位置
- v0.11.0 - 修 0xC0000602 启动崩溃 + 改 framework-dependent 发布模式
- v0.10.0 - 删除测试项目 + 架构精简为仅 x64
- v0.9.2 - 修复 dotnet publish 产物无法启动
- v0.7.0 - 滚动到底部自动加载更多 + Load More 按钮修复（保留瀑布流视觉）
- v0.6.0 - 增量加载（首次 150 张 + 手动 Load More）
- v0.5.0 - 双击打开图片、悬停显示文件名、右键打开文件位置、修复键盘焦点问题
- v0.4.0 - 文件监控、回收站删除、路径显示、修复计数问题
- v0.3.0 - 修复 1:1 模式下小地图刷新问题
- v0.2.0 - 键盘导航、图片信息显示、窗口状态记忆
- v0.1.0-alpha - 初始版本

## 构建发布

项目已清理为仅 x64、无多语言支持(无 af-ZA 等文件夹)、无测试项目,**MSIX packaged 部署**(`WindowsPackageType` 不设 → 默认 MSIX)。

**前置条件**(目标机器):

1. **.NET 10 Runtime**(x64)—— [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0) 选 "Desktop Runtime" 或 "ASP.NET Core Runtime"(x64)
2. **Windows App Runtime**——通过 MSIX 安装时**自动**装上(framework package 依赖,不需要单独下载 WindowsAppRuntime installer)

```powershell
cd IcedPicViewer
dotnet publish -c Release -p:Platform=x64
```

产物在 `bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages\` 下的 `IcedPicViewer_0.12.0.0_x64.msix`(还有几个 dependency .msix 一起)。把 `IcedPicViewer_0.12.0.0_x64.msix` 拖到目标机器双击安装(sideload);Windows 自动装上 framework package 依赖。

**dev box 调试运行**(注册 debug package identity 后启动,**这是开发期间唯一的正确启动方式**):

```powershell
dotnet run -c Debug -p:Platform=x64
```

⚠️ **不要直接双击 `bin\.../IcedPicViewer.exe`**。MSIX-packaged 模式下 .exe 需要 package identity 才能找到 framework package 注册的 WinRT 类,直接跑会 `REGDB_E_CLASSNOTREG`,在 `DeploymentManagerCS.AutoInitialize.get_Options()` 静态构造函数里就崩。`dotnet run` 通过 `Microsoft.Windows.SDK.BuildTools.WinApp` 调用 `winapp.exe launch` 注册 debug identity 后才启。

**版本号**:title bar 显示的 `IcedPicViewer (abc1234)` 里 `abc1234` 是 build 时从 `git rev-parse --short HEAD` 抓的 short commit hash,让你一眼认出当前在跑哪个版本(MSIX debug run 启动时 bin 路径不直观)。
