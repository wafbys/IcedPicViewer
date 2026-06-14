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

- v0.12.0 - 读取压缩包内图片 (ZIP/RAR/7Z) + 状态栏错误汇总 + 压缩包内图不可删 + overlay 显示位置 + publish target 自我繁殖 bug 修复
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

项目已清理为仅 x64、无多语言支持(无 af-ZA 等文件夹)、无测试项目。

**前置条件(目标机器必须装两个 runtime,本工程用 framework-dependent 模式不 bundled)**:

1. **.NET 10 Runtime**(x64)—— [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0) 选 "Desktop Runtime" 或 "ASP.NET Core Runtime"(x64)
2. **Windows App Runtime 2.1.3 standalone** —— [https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe](https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe)

> **为什么不是 self-contained?** SDK 2.1.3 带的 native DLL(`CoreMessagingXP.dll` 等)版本 `10.0.27200.1019` 比 Win 11 25H2 GA 的 `build 26200` 还新,DLL 加载时做 OS build check → `0xC0000602` (STATUS_FAIL_FAST_EXCEPTION)。Framework-dependent 模式下 loader 走 OS 自带的 `CoreMessagingXP.dll` v10.0.27108.1016,check 通过。MSIX 安装的 WindowsAppRuntime 在 `C:\Program Files\WindowsApps\`,unpackaged app 访问不到,**不算**前置条件满足。

```powershell
cd IcedPicViewer
dotnet publish -c Release -p:Platform=x64
```

产物在 `bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\IcedPicViewer.exe`(**~83 MB / 94 文件**,标准多文件布局;`Views\` 子目录保留 `GalleryView.xbf` / `ImageViewerView.xbf`,`publish\publish\` 嵌套已修)。

`IcedPicViewer.exe` 本身仅 ~284 KB(启动器),所有 .NET / WinUI / 资源文件在同目录独立存在,方便调试与替换。发布目录干净,无多余语言文件夹。
