# IcedPicViewer

简单的图片查看器，基于 WinUI 3 构建。

## 功能

- 文件夹浏览
- 缩略图网格视图（瀑布流布局）
- 增量加载 + 滚动到底部自动加载更多（首次只加载前 150 张，支持手动/自动继续加载）
- 图片查看（Fit / 1:1 模式切换）
- 键盘导航（左右箭头切换图片，ESC 关闭）
- 图片信息显示（尺寸、文件大小）
- GIF 动画支持
- 窗口状态记忆（关闭后重新打开恢复位置和大小）
- 删除图片（右键菜单 / Delete 键删除，支持回收站）

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

## 版本

- v0.7.0 - 滚动到底部自动加载更多 + Load More 按钮修复（保留瀑布流视觉）
- v0.6.0 - 增量加载（首次 150 张 + 手动 Load More）
- v0.5.0 - 双击打开图片、悬停显示文件名、右键打开文件位置、修复键盘焦点问题
- v0.4.0 - 文件监控、回收站删除、路径显示、修复计数问题
- v0.3.0 - 修复 1:1 模式下小地图刷新问题
- v0.2.0 - 键盘导航、图片信息显示、窗口状态记忆
- v0.1.0-alpha - 初始版本

## 构建自包含发布

项目已清理为仅 x64、无多语言支持（无 af-ZA 等文件夹）、无测试项目。

**前置条件(重要!)**:本应用是 unpackaged WinUI 3 app,需要**目标机器已安装 Windows App Runtime 2.0 standalone runtime**(SDK 2.0.1/2.1.3 的 self-contained 模式不完整,无法自包含 WindowsAppRuntime 的所有 native DLL)。下载:

```
https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe
```

装好后 `C:\Windows\System32` 会出现 `api-ms-win-appmodel-runtime-l1-1-1.dll` 等 WindowsAppSDK 依赖 DLL,unpackaged app 才能正常启动。MSIX 安装的 WindowsAppRuntime 在 `C:\Program Files\WindowsApps\`,unpackaged app 访问不到,**不算**前置条件满足。

```powershell
cd IcedPicViewer
dotnet publish -c Release -p:Platform=x64
```

产物在 `bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\IcedPicViewer.exe`(~214 MB,标准多文件布局)。

`IcedPicViewer.exe` 本身仅 ~284 KB(启动器),所有 .NET / WinUI / 资源文件在同目录独立存在,方便调试与替换。发布目录干净,无多余语言文件夹。
