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

**构建**:
```powershell
dotnet build -c Debug -p:Platform=$Platform
```

**运行(推荐,带包身份)**:
```powershell
dotnet run -c Debug -p:Platform=$Platform
```

**发布(自包含可分发的产物)**:
```powershell
dotnet publish -c Release -p:Platform=$Platform
```
产物在 `IcedPicViewer\bin\$(Platform)\Release\$(TargetFramework)\win-x64\publish\`,~218 MB(framework-dependent 模式的 ~83 MB 在 2026-06-15 弃用,见下方"Publish 模式"段)。

**部署清单**(给别人用时):
1. 跑 `dotnet publish -c Release -p:Platform=x64` 得产物
2. 把 `publish\` 目录复制到目标机器,双击 `IcedPicViewer.exe`
3. **目标机器无需装 .NET runtime,无需装 WinAppRuntime**——self-contained 全部 bundled

> **历史事实**:`CHANGELOG.md` 中 `v0.9.x` 多条 commit 提到"publish 产物能跑"——**这是错误的**,只验证了文件完整性,没真启动过 .exe。实测(`24063cd` 之后)publish 产物在缺 WinAppRuntime 2.x standalone runtime 的机器上 0xC0000602 退出。本节"必须的前置条件"是基于此实测的诚实结论。

> **PE 解析诊断备忘(2026-06-03,WinAppSDK 2.1.3 时代)**:`System.Reflection.PortableExecutable` 解析 publish 目录各 native DLL 的 import table,确认 0xC0000602 根因。**用户当前 system 缺 12 个 WinRT/UWP 相关的 api-set DLL**(`system32` + `WinSxS` 都找不到),全部来自 `Microsoft.WindowsAppRuntime.dll` / `Microsoft.WindowsAppRuntime.Bootstrap.dll` / `Microsoft.UI.Xaml.dll` 的 P/Invoke:
>
> | 缺失 DLL | 来源 |
> |---|---|
> | `api-ms-win-appmodel-runtime-l1-1-1.dll` | WinAppRuntime + Bootstrap(**最核心** — WinRT 入口 `RoGetActivationFactory` 在里面) |
> | `api-ms-win-core-libraryloader-l1-2-0.dll` | WinAppRuntime + Bootstrap |
> | `api-ms-win-power-base-l1-1-0.dll` | WinAppRuntime |
> | `api-ms-win-power-setting-l1-1-0.dll` | XAML + WinAppRuntime |
> | `api-ms-win-power-setting-l1-1-1.dll` | WinAppRuntime |
> | `api-ms-win-core-winrt-l1-1-0.dll` | XAML + WinAppRuntime |
> | `api-ms-win-core-winrt-string-l1-1-0.dll` | XAML + WinAppRuntime |
> | `api-ms-win-core-winrt-error-l1-1-0.dll` | XAML + WinAppRuntime + Bootstrap |
> | `api-ms-win-core-winrt-error-l1-1-1.dll` | XAML |
> | `api-ms-win-ro-typeresolution-l1-1-0.dll` | XAML + WinAppRuntime |
> | `api-ms-win-core-path-l1-1-0.dll` | XAML + WinAppRuntime |
> | `api-ms-win-dx-d3dkmt-l1-1-0.dll` | XAML |
>
> 装 WinAppRuntime standalone installer(aka.ms URL)后,这 12 个 DLL 会部署到 `C:\Windows\System32`,app 启动时 Windows loader 能找到,0xC0000602 消失。**IcedPicViewer.exe 自身 import table 只 12 个基础 KERNEL32/USER32/ole32 等**,所以 .exe 自身能起;问题出在它加载 `Bootstrap.dll` → 链式 P/Invoke 这些缺失 DLL → fail-fast。

> 实现细节见 `IcedPicViewer.csproj` 末尾的自定义 MSBuild target：
> - `SyncWinUIBuildOutputToPublish` —— 修 WindowsAppSDK 的 bug：它的 WinUI targets 只 hook 了 build 的 `GetCopyToOutputDirectoryItems`，**没 hook publish 的 `GetCopyToPublishDirectoryItems`**。不修的话 publish 出来的 exe 缺 `App.xbf`/`MainWindow.xbf`/`Assets`/`Views`，启动后立刻崩（`0xC000027B`）。**在 2.2.0 下已重新验证仍必须**（2026-06-15，临时注释掉 target 后 publish 产物缺 15 个文件：所有 `.xbf` / `.pri` / 11 个 tile & icon PNG；微软 2.2.0 没修这个 bug）。**不可删**。
> - `RemoveUnwantedCultures` —— 同时清 `$(OutDir)` 和 `$(PublishDir)` 中 BCP 47 格式的 culture 子目录,实测 publish 产物从 222 MB / 572 文件减到 216 MB / 389 文件(86 个 culture 目录消失)。
> - ~~`CopyWindowsAppRuntimeBootstrapToOutput`~~ —— 已在 2.1.3 升级时永久删除(只在 `PublishSingleFile=true` 时需要,改用 multi-file publish 后自然不需要)。

> **Publish 模式:self-contained (2026-06-15 改回)**。
>
> **历史路径**:
> - 2026-06-03 之前:self-contained (WinAppSDK 2.1.3) — native DLL build 27200 比 OS build 26200 新,触发 OS build check,`STATUS_FAIL_FAST_EXCEPTION` (0xC0000602)。
> - 2026-06-03:改 framework-dependent (83 MB) — 规避 2.1.3 build mismatch,但依赖系统 apiset schema。这台机器的 schema 缺 8+ WinRT api-set,启动 0xC000027B(在 `Microsoft.UI.Xaml.dll` 内 fail)。
> - 2026-06-15:改回 self-contained (218 MB) — **WinAppSDK 2.2.0 自带 native DLL 不再触发 OS build check**(实测 OK,用户机器 build 26200,2.2 runtime DLL build 27108 / 10.0.26100.x,反而比 OS 旧,正好)。完全自包含,**不依赖**系统 apiset schema,部署即可用。
>
> 体积多 135 MB 是"不依赖用户系统健康"的代价。在这台反复撞 apiset 问题的机器上,是正确取舍。

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