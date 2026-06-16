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
产物在 `IcedPicViewer\bin\$(Platform)\Release\$(TargetFramework)\win-x64\publish\`,~83 MB(framework-dependent,**不含** .NET 运行时和 WinAppSDK runtime)。

**必须的前置条件**:**目标机器已安装 .NET 10 runtime + Windows App Runtime 2.2 standalone**。WinAppSDK 2.0+ 的 self-contained 模式不完整(`api-ms-win-appmodel-runtime-l1-1-1.dll` 等 DLL 不会被 SDK 嵌入 publish 产物),unpackaged app 启动时必须从 `C:\Windows\System32` 找到这些 DLL。SDK 版本和 runtime 版本必须**严格匹配** — 升级 SDK 到 2.2 后,目标机也得用 2.2 runtime,旧的 2.1.x runtime 会导致启动报版本不匹配。安装包 URL 始终指向 2.0+ 的最新:
```
https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe
```

**部署清单**(给别人用时):
1. 目标机器先装 .NET 10 SDK
2. 目标机器装 Windows App Runtime 2.2 standalone runtime(上面链接)
3. 然后跑 `dotnet publish -c Release -p:Platform=x64` 得产物
4. 把 `publish\` 目录复制到目标机器,双击 `IcedPicViewer.exe`

> ⚠️ **OS 兼容性警告 (2026-06-15 加,2026-06-15 再次修正 —— 2026-06-15 自我纠错)**:用户的开发机是 **正版的 Windows 11 25H2**(CurrentBuild 26200.8655,BuildLabEx `26100.1.amd64fre.ge_release.240331-1435` —— 这是 24H2 原始平台被 25H2 Enablement Package 激活后保留的正常 BuildLab,**不是被 hack 改过版本号**)。详见下方"2026-06-15 我把 25H2 误判为假版本"段的诚实记录。
>
> 在这台正版 25H2 上,**所有 WinAppRuntime 1.6+ 都跑不起来**(framework-dependent 和 self-contained 都崩),根本原因有两层:
> 1. **OS build check fail-fast**(0xC0000602):WinAppRuntime 1.6+ 的 `CoreMessagingXP.dll` 等 native DLL build 是 27xxx(1.6: 27106, 1.7: 27107, 1.8: 27108, 2.2: 27200),而 **Enablement Package 机制下,25H2 的核心二进制(包括 kernel32.dll、apisetschema.dll 等)停留在 26100 平台**。DLL 加载时做 OS build check,看到 "DLL build 27xxx > OS 平台 26100",触发 `STATUS_FAIL_FAST_EXCEPTION` (0xC0000602)。这是**Microsoft 的 Enablement Package 设计与 WinAppSDK 1.6+ 的 native DLL build 不兼容**的硬冲突,**不是单边 bug**。
> 2. **api-set schema 缺条目**(0xC000027B):实测 `apisetschema.dll` 在 System32(v10.0.26100.8521)但 schema 里**未注册 8+ WinRT api-set**(`api-ms-win-core-winrt-l1-1-0.dll` 等)。bootstrap 路径会变成 `0xC000027B`。DISM RestoreHealth / sfc /scannow / WindowsAppRuntime installer / ComponentStore / Recovery / Panther 全部**无源文件可修**。
>
> 用户的开发机只能跑 WinAppSDK **1.5 或更早**(那些版本的 native DLL 是 build 26xxx 时代,跟 26100 平台匹配)。但 1.5 缺 `Microsoft.Windows.Storage.Pickers.FolderPicker`、1.4 缺 `TitleBar.IconSource`,要降 WinAppSDK 得改代码。
>
> **部署给别人用时建议**:**先在自己机器上验证 app 能跑再发布**。任何 Win 11 24H2/25H2 用户都跑不起来,需要 OS 升到 build 27xxx(Win 11 26H1/26H2 之类)或者修本机 api-set schema。**别假设"最新 OS 一定能跑"**——Microsoft 的 Enablement Package 设计导致 25H2 的 native binary 平台停留在 26100,跟 WinAppSDK 1.6+ 的 build 号范围不重叠。

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

> **Publish 模式:framework-dependent (2026-06-15 最终结论)**。
>
> 之前 `SelfContained=true` + `WindowsAppSDKSelfContained=true` 会把 WinAppSDK 2.1.3 的 native DLL(`CoreMessagingXP.dll` v10.0.27200.1019、`dwmcorei.dll`、`dcomp.dll`、`Microsoft.UI.Input.dll`、`Microsoft.Internal.FrameworkUdk.dll`、`Microsoft.UI.Windowing.Core.dll` 等)都部署到 publish 目录。**这些 DLL 比用户的 OS 还新**——用户 OS 是 Win 11 25H2 `build 26200.8655`,这些 DLL 来自 build `27xxx`。DLL 加载时做 OS build check → `STATUS_FAIL_FAST_EXCEPTION` (`0xC0000602`) → `Event Log` 记录 `Faulting module: CoreMessagingXP.dll, version: 10.0.27200.1024`。
>
> 2.1.3 → 2.2.0 升级**没修复这个 bug**(实测 `dd0a7bd`,5 秒 "Still running" 是 false positive,实际 ~4 秒就崩了 0xC0000602)。已 revert 见 `0840109`。
>
> 列出本机所有已装 WinAppRuntime 的 `CoreMessagingXP.dll` 版本:
>
> | WinAppRuntime | CoreMessagingXP.dll | 用户 OS build |
> |---|---|---|
> | 1.6 | 10.0.27106 | 用户 OS = **10.0.26200.8655** |
> | 1.7 | 10.0.27107 | |
> | 1.8 | 10.0.27108 | |
> | 2.2 (当前) | 10.0.27200 | |
>
> **所有 WinAppRuntime 1.6+ 的 native DLL build 都比用户 OS 26200 新**。无论 framework-dependent 还是 self-contained,在这台机器上都会 fail-fast。
>
> **维持 framework-dependent**(83 MB)的原因:
> - self-contained 在该 OS 上同样炸,没必要 +135 MB
> - 部署到**别的 OS ≥ 27106 的机器**上时,framework-dependent 体积优势仍在
> - 任何要真正用本机测试的话,必须先升 OS 到 27xxx(Win 11 Insider 25H2/26H1 之类的),见顶部"OS 兼容性警告"
>
> 改成 framework-dependent(`SelfContained=false` + `WindowsAppSDKSelfContained=false`)后:
> - publish 产物不再带那些 27xxx 的 native DLL(loader 改走 `System32` / `WinSxS`)
> - 目标机**必须装**:
>   1. .NET 10 runtime(`coreclr.dll` / `hostfxr.dll` 不再 bundled)
>   2. Windows App Runtime 2.2 standalone(`Microsoft.WindowsAppRuntime.dll` 不再 bundled)—— installer URL 同上
> - publish 产物体积 **83 MB / 110 文件**
>
> **2026-06-15 追加诚实记录 —— "framework-dependent" 名不副实**:
> 设了 `SelfContained=false` + `WindowsAppSDKSelfContained=false` 后,publish 产物体积确实 83 MB(没把 .NET runtime 和 WinAppSDK runtime 整套 bundled 进来),**但 CoreMessagingXP.dll / Microsoft.UI.Xaml.dll / Microsoft.WindowsAppRuntime.dll 等 native DLL 仍然在 publish 目录里**。`SyncWinUIBuildOutputToPublish` target 只是把它们从 OutDir 复制到 PublishDir;DLL 本身是 NuGet restore 时 Microsoft.WindowsAppSDK.* 子包放进 OutDir 的,删不掉。
>
> Loader 同目录有 DLL 就优先用,**不走 WindowsApps bootstrap 路径**。所以即使我们 `WindowsAppSDKSelfContained=false`,OS build check 仍然看 publish 目录里的 DLL 27200,跟 self-contained 一样的下场。
>
> "framework-dependent" 实际只省了 .NET runtime 那一份,WinAppSDK native DLL 仍然 bundled。3be0326 那个 "framework-dependent 应该能用" 的结论**前提就错了**,得连带修正:
> - 真正的"framework-dependent"(让 loader bootstrap 走 WindowsApps)需要阻止 NuGet 把 native DLL 放 OutDir。WinAppSDK 2.2.0 没提供这个开关
> - 想要 83 MB 体积 + 真正不依赖系统 DLL,**目前没有干净的选项**——除非直接降 WinAppSDK 到 1.5 之前
> - 所以本机无论怎么配 publish 都跑不起来,**就是 OS 26100/26200 (Enablement Package 平台) + WinAppSDK native DLL 27200 的硬冲突**

> **2026-06-15 我把 25H2 误判为假版本 —— 诚实记录**:
> 用户纠正我"BuildLabEx 是 26100 + kernel32 是 26100 不能说明 25H2 是假版本",Microsoft 官方从 2025 年开始大量用 **Enablement Package** 方式发 H2 更新:
> - 24H2 和 25H2 共享同一个 servicing branch
> - 25H2 的核心二进制(ntoskrnl.exe、kernel32.dll、apisetschema.dll 等)停留在 26100 平台
> - KB5054156 等 Enablement Package 解锁 25H2 特性
> - 看到 CurrentBuild=26200 + DisplayVersion=25H2 + BuildLabEx=26100.1.ge_release.240331-1435 **就是正版的 25H2**,不是被 hack 改版本号
> - ProductName 注册表仍显示 "Windows 10 Pro" 也是从 21H2 起的兼容性老毛病,不能当证据
>
> 我错误地**把"底层平台是 24H2"等同于"25H2 是假的"**,这个过度解读已经写进 a68b467 之前的 commit。本次修正:
> - 顶部"OS 兼容性警告"段已删"是 24H2 假装的 25H2"的说法
> - 改成"正版 25H2 + Enablement Package 机制"的正确描述
> - **症状(apiset schema 缺 + native DLL 27200 > 平台 26100)仍然成立**,但解释改成"Enablement Package 设计 + WinAppSDK 1.6+ native DLL build 不兼容",不是"25H2 bug"也不是"假版本"
> - 教训:技术细节正确 ≠ 整体解读正确。看到"老 platform + 新 build 号"组合时,先查 Microsoft 是否官方用 Enablement Package,不要急着下"假版本"结论

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