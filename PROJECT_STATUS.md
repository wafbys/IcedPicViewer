# IcedPicViewer 项目现状快照

> **快照时间**:2026-06-02
> **版本**:v0.9.0
> **目的**:供后续接手 / 翻看 git 历史时快速了解项目状态

---

## 项目定位

`IcedPicViewer` 是一个**自用型图片查看器**,基于 **WinUI 3 + Windows App SDK** 构建,采用 **MasonryPanel** 瀑布流布局呈现缩略图,主界面 + 单图模式两层结构。

- **运行平台**:Windows 10/11(x64)
- **打包方式**:**unpackaged**(`WindowsPackageType=None`,免安装,可直接跑 `IcedPicViewer.exe`)
- **目标用户**:作者自用,无商店分发计划

---

## 技术栈

| 维度 | 选型 | 版本 |
|---|---|---|
| .NET | `net10.0-windows10.0.26100.0` | .NET 10 |
| UI 框架 | WinUI 3(`Microsoft.WindowsAppSDK`) | `2.0.1` |
| MVVM | `CommunityToolkit.Mvvm`(源生成器) | `8.2.2` |
| DI | `Microsoft.Extensions.Hosting`(只用 `ServiceCollection`) | `8.0.0` |
| 图像处理 | `Microsoft.UI.Xaml.Media.Imaging.BitmapImage`(内置) | — |
| 文件 I/O | `System.IO` + `FileSystemWatcher` | — |
| 测试 | MSTest + Moq | latest |

> 显式**未使用**的依赖:`SixLabors.ImageSharp` / `Microsoft.Graphics.Win2D` / `StyleCop.Analyzers` —— 已在 v0.9.0 清理。

---

## 架构总览

```
App (DI 容器 + WinUI 启动)
 ├─ MainWindow (Frame 容器 + 中央化键盘处理 + 窗口状态保存/恢复)
 │   └─ RootFrame
 │       └─ GalleryView (缩略图瀑布流 + 滚动到底自动加载更多)
 │           └─ ImageViewerView (单图 + Fit/1:1 切换 + 小地图导航)
 │
 ├─ Services (接口 + 实现,DI 注入)
 │   ├─ IDirectoryScanner → DirectoryScanner(扫描 + FileSystemWatcher)
 │   ├─ IImageLoader      → ImageLoader(流式解码 + 缩略图 LRU 缓存)
 │   ├─ INavigationService → NavigationService(Frame 包装)
 │   └─ IFolderPickerService → FolderPickerService(现代 FolderPicker)
 │
 ├─ ViewModels
 │   ├─ GalleryViewModel  (Singleton,持 Images 集合 + 缩略图并发控制)
 │   └─ ImageViewModel    (Singleton,与 Gallery 共享状态)
 │
 └─ Models
     ├─ ImageItem         (图片元数据 + BitmapImage 缩略图/全图)
     └─ LoadingState      (枚举:Idle/Scanning/LoadingImages/Error/Completed)

Controls
 ├─ MasonryPanel         (瀑布流布局,非虚拟化)
 └─ MasonryLayoutEngine  (FindShortestColumn / CalculateColumnHeights 纯函数)
```

### 关键设计决策

- **MasonryPanel 非虚拟化**:用户明确要求保留瀑布流视觉,放弃 `ItemsRepeater` 虚拟化方案。配合 `GalleryViewModel.PageSize=150` 增量加载缓解内存压力
- **缩略图 LRU 缓存 200 条目**:`400px` 缩略图最坏 `~30-80 MB`,避免大文件夹无界增长
- **DI Singleton 选择**:
  - `GalleryViewModel` Singleton(共享 `Images` 集合 + `FileSystemWatcher`)
  - `ImageViewModel` Singleton(`GalleryView` 准备的实例 = `ImageViewerView` 接收的实例,避免重新加载图片)
  - `MainViewModel` 已删除(无业务用途,纯空壳)
- **键盘事件集中化**:`MainWindow.RootGrid_KeyDown` 处理 `Left` / `Right` / `Delete` / `Escape`,避免在每个 Page 重复注册
- **窗口状态文件**:存 `%LOCALAPPDATA%\IcedPicViewer\window_settings.txt`,符合 unpackaged WinUI 标准做法

---

## 代码统计(2026-06-02)

| 模块 | 源码文件 | 源代码行数 |
|---|---|---|
| 主项目 | 14(.cs + .xaml + .csproj + 配置) | `~2,200` |
| 测试项目 | 6 | `~700` |
| **合计** | 20 | **`~2,900`** |

主项目关键文件(按行数):

| 文件 | 行数 | 角色 |
|---|---|---|
| `GalleryViewModel.cs` | ~440 | 核心:扫描 + 缩略图 + 监控 + 状态 |
| `ImageViewModel.cs` | ~270 | 单图模式:导航 + 删除 + 全图加载 |
| `ImageViewerView.xaml.cs` | ~250 | 单图 UI + Fit/1:1 + 小地图 |
| `GalleryView.xaml.cs` | ~190 | 缩略图 UI + 滚动 debounce |
| `MainWindow.xaml.cs` | ~125 | 根窗口 + 键盘 + 状态持久化 |
| `ImageLoader.cs` | ~135 | 流式解码 + LRU 缓存 |
| `MasonryPanel.cs` | ~120 | 瀑布流 Panel |

---

## 质量指标

| 指标 | 当前值 | 历史峰值 |
|---|---|---|
| 编译错误 | `0` | — |
| 编译警告 | `0` | `1024`(v0.9.0 之前) |
| 单元测试 | `33 / 33` 通过 | — |
| `bin/obj` 体积 | `~96 MB` | `~896 MB`(v0.9.0 之前) |
| `dotnet build slnx` | 正常 | 失败(`MSB4126`,v0.9.0 之前) |
| 死代码 | `0` | 至少 4 处 |
| `TODO` / `FIXME` / `HACK` | `0` | — |
| 空 `catch` 块 | `0`(全部带 `Trace.TraceError`) | — |
| `dispose` 泄漏 | `0` | App 退出漏 dispose `GalleryViewModel`(v0.9.0 之前) |

### 测试覆盖(按模块)

| 模块 | 覆盖率 | 备注 |
|---|---|---|
| `MasonryLayoutEngine` | ✅ 100% | 纯函数 |
| `ImageItem` | ✅ 100% | `FileSizeText` 三档边界 |
| `DirectoryScanner.ScanAsync` | ⚠️ ~70% | 缺权限拒绝/取消路径 |
| `DirectoryScanner.Watch` | ❌ 0% | 难单测,需集成 |
| `ImageLoader` | ⚠️ ~40% | 缺缓存命中 / 错误路径 |
| `NavigationService` | ✅ ~90% | 边界用例 |
| `FolderPickerService` | ⚠️ ~30% | UI 限制,只能测无窗口 |
| `GalleryViewModel` | ⚠️ ~60% | 缺 `OnFileChanged` 回调路径 |
| `ImageViewModel` | ⚠️ ~70% | v0.9.0 新增覆盖,关键路径都有 |
| `MasonryPanel`(实际 Panel) | ❌ 0% | 依赖 WinUI 布局,合理 |
| UI 集成 | ❌ 0% | 需 UI 自动化框架,合理 |

---

## 已知限制

1. **MasonryPanel 非虚拟化**:`10000+` 张图的大文件夹滚动有性能压力,靠增量加载缓解但未根本解决
2. **无网络图片支持**:`GalleryViewModel.DeleteImageAsync` 检测网络路径弹确认,但 `ImageLoader.LoadImageStreamAsync` 仅支持本地 `FileStream`
3. **无国际化**:UI 文本硬编码中英混合(`"确定要永久删除..."` 等),无资源文件
4. **无错误聚合**:异常仅 `Trace.TraceError`,无用户可见的错误反馈(部分场景如 `StatusText = $"Error: {ex.Message}"`)
5. **测试框架限制**:`ImageViewModel` 测试 mock `IImageLoader.LoadImageStreamAsync` 返回 `null`,实际图片解码路径未覆盖
6. **发布配置简化**:仅 x64,无 ARM64(若需要在 Surface/平板上运行需重新加 `<Platforms>`)

---

## 后续可选方向

按价值/工作量排序:

| 方向 | 工作量 | 价值 | 备注 |
|---|---|---|---|
| 添加 `MasonryPanel` 单元测试 | 中 | 中 | 提取纯函数到 `MasonryLayoutEngine` 后,只测布局部分 |
| `DirectoryScanner.Watch` 集成测试 | 中 | 中 | 用临时目录 + 真实 `FileSystemWatcher` 测回调 |
| 国际化(资源文件) | 中 | 中 | 提取硬编码字符串 |
| ARM64 平台恢复 | 低 | 中 | 改回 `<Platforms>x64;ARM64</Platforms>` |
| 网络图片支持(URL) | 高 | 中 | 需要 `HttpClient` 下载 + 临时缓存 |
| 虚拟化瀑布流 | 高 | 高 | 改用 `ItemsRepeater` + 自定义 `Layout`,保视觉换性能 |
| MSIX 商店打包 | 中 | 中 | 设 `<WindowsPackageType>MSIX` + 数字签名 |
| 主题切换(深色/浅色) | 低 | 低 | WinUI 内置支持,加 toggle |

---

## Git 历史(从清理战役起)

```
921572e P3 清理:线程安全、资源释放、死代码、健壮性
408d3a6 P2 清理:加 Dictionary 索引、源生成器改造、流式解码、修复 Renamed、归零 warning
9a9a5ea P1 清理:删 Task.Delay hack、加 LRU 缓存、补 ImageViewModel 测试、迁移窗口状态路径
d0bbe61 简化构建平台:仅保留 x64
a9ca5e5 P0 项目清理:移除 StyleCop、删除死依赖与死代码、修复 slnx 与配置
89e82a7 项目大扫除与文档一致性更新(原始)
bc82a3f 新增 IcedPicViewer.Tests 单元测试项目(原始)
```

4 个新 commit(P0/P1/P2/P3)都是**清理与质量提升**,无新功能特性。

---

## 常用命令

```powershell
# 平台检测 + 构建
$arch = $env:PROCESSOR_ARCHITECTURE
$Platform = if ($arch -eq 'AMD64') { 'x64' } else { $arch }
dotnet build -c Debug -p:Platform=$Platform

# 跑全部测试
dotnet test -c Debug -p:Platform=$Platform

# 单测指定类
dotnet test --filter "FullyQualifiedName~GalleryViewModelTests"

# 清理工作区(主项目 + 测试)
mavis-trash "IcedPicViewer\bin" "IcedPicViewer\obj" "IcedPicViewer.Tests\bin" "IcedPicViewer.Tests\obj"

# 启动(unpackaged 模式,带窗口身份)
dotnet run -c Debug -p:Platform=$Platform
```

---

## 联系 / 维护

- **作者**:YF(项目自用)
- **AI 协作**:Mavis(MiniMax-M3)
- **协作规范**:见 `AGENTS.md`
