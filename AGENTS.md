# AI 协作指南 — IcedPicViewer

> **目标**：高效交付高质量代码，同时保持良好的开发体验。规则要实用，而不是为了约束而约束。

## 项目背景

这是一个基于 **WinUI 3 + Windows App SDK** 的图片查看器桌面应用（MSIX 打包）。

- 使用 **MasonryPanel** 实现瀑布流视觉效果（这是用户明确选择保留的设计，不建议轻易改成虚拟化列表）。
- 采用 **增量加载**（首次 150 张 + Load More + 滚动到底自动加载）。
- 使用 CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection。
- 重视**可维护性**，但反对为了“正确”而过度设计。

## 核心协作原则

1. **用户意图优先于规则**  
   当严格遵守某条规则会导致明显不好的体验、拖慢进度或违背你的真实需求时，请主动告诉我。我们可以讨论权衡。

2. **检查现有代码，再动手**  
   改动前先搜索项目中是否有类似实现。能复用就复用，能小改就不大改。

3. **测试要务实**  
   - 新的公共方法和复杂业务逻辑必须有测试。
   - UI 胶水代码、探索性改动可以轻量。
   - 不强制严格 TDD，但欢迎在复杂功能上采用“先写测试再实现”的方式。

4. **Build + 测试必须通过**  
   任何改动完成后，必须能干净编译 + 相关测试通过。这是底线。

5. **主动暴露问题和权衡**  
   如果你发现需求模糊、存在明显取舍、或者当前做法有风险，请直接说出来。不要为了“听话”而硬做。

6. **提交信息必须用中文**  
   Git commit message 一律使用中文。

## 日常工作流程（推荐）

1. 理解需求 + 确认边界（必要时问问题）。
2. 搜索现有实现，避免重复造轮子。
3. 实现功能 + 补充必要测试。
4. 本地构建 + 运行相关测试。
5. 提交前自检：是否干净？是否符合用户真实意图？
6. 提交代码（中文信息）。

## 必须遵守的硬性规则

- **构建与测试**：改完必须 `dotnet build` + 跑相关测试。
- **中文提交**：所有 commit message 用中文。
- **检查重复**：新增功能前先找项目里有没有类似代码。
- **WinUI 禁忌**：
  - 不要用 `Window.Current`、`CoreDispatcher` 等已废弃的东西。
  - 大列表优先考虑虚拟化（但本项目因视觉要求保留了 MasonryPanel）。
  - 关注焦点管理（单图模式键盘事件曾因此出问题）。
- **资源清理**：IDisposable 必须正确释放（尤其是 FileSystemWatcher、CancellationTokenSource、Stream）。
- **异常处理**：禁止空 catch 吞掉异常，至少要用 `Trace.TraceError` 记录。

## 关于“其他详细规则”

本项目之前有一堆 `.github/instructions/` 下的长文档（设计原则、性能、无障碍等）。  
**现在这些文件已不再是强制阅读材料**。旧的详细指令文件及目录已彻底删除，仅保留轻量化的 AGENTS.md。

真正重要的东西已经浓缩在本文件里。如果你不确定某个领域的最佳实践，可以直接问我，我会结合实际情况给出建议。

## 构建与运行命令

**本项目仅支持 x64 架构**。

```powershell
$Platform = 'x64'
```

**构建**：
```powershell
dotnet build -c Debug -p:Platform=$Platform
```

**运行（推荐，带包身份）**：
```powershell
dotnet run -c Debug -p:Platform=$Platform
```

**发布（自包含可分发的产物）**：
```powershell
dotnet publish -c Release -p:Platform=$Platform
```
产物在 `IcedPicViewer\bin\$(Platform)\Release\$(TargetFramework)\win-x64\publish\`，~220 MB，包含 .NET 运行时和 WindowsAppSDK 运行时，**不依赖系统安装 Windows App Runtime**。双击 `IcedPicViewer.exe` 即可启动。别人 fresh clone 这份代码、装好 .NET 10 SDK 后跑这个命令就能直接产出可分发的 exe。

> 实现细节见 `IcedPicViewer.csproj` 末尾的两个 target：
> 1. `SyncWinUIBuildOutputToPublish` —— 修 WindowsAppSDK 2.0 的 bug：它的 targets 只 hook 了 build 的 `GetCopyToOutputDirectoryItems`，**没 hook publish 的 `GetCopyToPublishDirectoryItems`**。不修的话 publish 出来的 exe 缺 `App.xbf`/`MainWindow.xbf`/`IcedPicViewer.pri`/`Assets`/`Views`，启动后立刻崩（`0xC000027B`）。
> 2. `CopyWindowsAppRuntimeBootstrapToOutput` —— 修另一个 WindowsAppSDK 2.0 的坑：设了 `RuntimeIdentifier=win-x64` 后，`Microsoft.WindowsAppRuntime.Bootstrap.dll` 被丢到 `runtimes\win-x64\native\`，但它的 P/Invoke loader 走的是标准 Windows DLL 搜索（只看应用目录和 PATH），找不到就初始化失败。手动把 bootstrapper 拷到 build 输出根。

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