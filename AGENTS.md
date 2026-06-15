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

**构建** (产出 AppX 布局到 `bin\$(Platform)\$(Configuration)\$(TargetFramework)\win-x64\AppX\`):
```powershell
dotnet build -c Debug -p:Platform=$Platform
```

**运行** (注册 debug package identity 后启动;**这是开发期间唯一的正确启动方式**):
```powershell
dotnet run -c Debug -p:Platform=$Platform
```

⚠️ **不要直接双击 `bin\.../IcedPicViewer.exe`**。MSIX-packaged 模式下 .exe 需要 package identity 才能找到 framework package 注册的 WinRT 类,直接跑会 `REGDB_E_CLASSNOTREG`,在 `DeploymentManagerCS.AutoInitialize.get_Options()` 静态构造函数里就崩。`dotnet run` 通过 `Microsoft.Windows.SDK.BuildTools.WinApp` 调用 `winapp.exe launch` 注册 debug identity 后才启。

**发布** (产出 sideloadable .msix):
```powershell
dotnet publish -c Release -p:Platform=$Platform
```
产物在 `bin\$(Platform)\Release\$(TargetFramework)\win-x64\AppPackages\` 下的 `IcedPicViewer_0.12.0.0_x64.msix`(还有几个 dependency .msix 一起)。

**部署清单**(给别人用时):
1. 目标机器先装 .NET 10 runtime
2. 把 .msix 拖到目标机器双击安装(sideload);Windows 自动装上 framework package 依赖
3. 不需要单独装 Windows App Runtime standalone installer(MSIX manifest 声明了 framework package 依赖)

---

### 部署模式:为什么用 MSIX packaged,不是 unpackaged

**本项目 (IcedPicViewer)**:MSIX packaged(`WindowsPackageType` 不设 → 默认 MSIX)。配套:
- `Package.appxmanifest` 在项目根,声明 framework package 依赖
- `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet → `dotnet run` 走 `winapp.exe launch` 注册 debug identity
- 无需 `Bootstrapper` 注册表项、无需 standalone installer

**unpackaged 模式(2026-06-15 之前用过,已弃)**:
- 需要目标机装 WinAppRuntime standalone + 写 Bootstrapper 注册表项
- 用户开发机缺这个,Bootstrap API 检测到 runtime 缺就弹错
- 即使装上后还会撞下面那个 XAML 资源 bug

**关键 insight**:MSIX packaged 路径用 framework package 通过 appxmanifest 解析,完全绕开 Bootstrapper;MSIX 部署也比"拷文件夹 + 装 standalone"对用户更友好。

---

### 已知坑:`XamlControlsResources` 必须在 App.xaml 显式 merge

`Microsoft.UI.Xaml.Controls.XamlControlsResources` 是 WinUI 3 Fluent 2 theme resource dictionary。`TitleBar` 等控件的 default style 内部引用 `TabViewButtonBackground` 等 theme resources,只有 merge 了这个 dictionary 才能找到。

**不能删**。commit `2b3030e "App.xaml: 删除冗余的 XamlControlsResources"` 是基于错误推理("WinUI 3 不用这个")——实际后果:启动时 `MainWindow.InitializeComponent()` 抛 `XamlParseException: Cannot find a Resource with the Name/Key TabViewButtonBackground`,MainWindow 起不来。`App.xaml` 必须保留:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <controls:XamlControlsResources />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

---

### 历史教训 —— "OS 硬冲突"理论是过度诊断 (2026-06-15 自我纠错)

之前 `AGENTS.md` 大段写"WinAppSDK 2.2.0 在 Win 11 25H2 上 native DLL build 27xxx > OS 平台 26100 → fail-fast,本机跑不起来"——**整套理论是错的**。证伪方式:

- `C:\Users\YF\WAFBYS\Src\Playground\`(同一 SDK 2.2.0, MSIX packaged)在本机**完美运行**(`Playground.exe` 启动 6s 还在跑,MainWindow 正常)
- IcedPicViewer 改成 MSIX packaged(本 commit)后**也完美运行**(`IcedPicViewer.exe` 启动 25s 还活着,MainWindow 显示正常,无 unhandled exception)

**真实问题从来不是 OS build check**,是**unpackaged 模式缺 Standalone installer**。一旦走 MSIX packaged,framework package 走 appxmanifest 解析,跟 OS build 没关系。

**技术细节正确 ≠ 整体解读正确**:
- 用户的开发机是 **正版的 Windows 11 25H2**(CurrentBuild 26200.8655,BuildLabEx `26100.1.amd64fre.ge_release.240331-1435` —— 24H2 原始平台被 25H2 Enablement Package 激活后保留的正常 BuildLab,**不是被 hack 改过版本号**)
- Microsoft 官方从 2025 年开始大量用 **Enablement Package** 方式发 H2 更新:24H2 和 25H2 共享同一 servicing branch,25H2 核心二进制(ntoskrnl.exe、kernel32.dll、apisetschema.dll 等)停留在 26100 平台,KB5054156 等 Enablement Package 解锁 25H2 特性
- 看到 CurrentBuild=26200 + DisplayVersion=25H2 + BuildLabEx=26100.1.ge_release.240331-1435 **就是正版的 25H2**,不是被 hack 改版本号;ProductName 注册表仍显示 "Windows 10 Pro" 也是从 21H2 起的兼容性老毛病,不能当证据

**教训**:
- 看到"老 platform + 新 build 号"组合时,先想 Microsoft 是否用 Enablement Package(25H2 确实用了),不要急着下"假版本"或"OS 不兼容"结论
- 测试假设时,**找一个反例**(同 SDK 跑的别人项目)就能证伪一个普适性结论
- 复杂系统里症状可以叠加;同一个错误码可能由完全不同原因触发,**诊断时要锁定到具体调用栈**

---

### 关于 api-set schema 的有用事实(2026-06-15 验证,保留)

`api-ms-win-*.dll` 系列(api-set)是**虚拟 forwarder**,由 `apisetschema.dll` 解析 serve。`System32` 里"找不到文件"是正常现象,不是缺失;grep 字符串 0 match 是因为 schema 用二进制编码。要验证某 api-set 是否真的可用,正确做法是 PowerShell `[System.Runtime.InteropServices.NativeLibrary]::Load("api-ms-win-xxx.dll")`,而不是看文件存不存在。

之前对 IcedPicViewer 0xC0000602 错误的"api-set 缺失"猜测**已推翻**;这个事实本身仍然有用(下次不要被"找不到 api-set DLL"表象误导)。

---

### 实现细节

`IcedPicViewer.csproj` 末尾的自定义 MSBuild target:
- `SyncWinUIBuildOutputToPublish` —— 修 WindowsAppSDK 的 bug:它的 WinUI targets 只 hook 了 build 的 `GetCopyToOutputDirectoryItems`,**没 hook publish 的 `GetCopyToPublishDirectoryItems`**。不修的话 **unpackaged** publish 出来的 exe 缺 `App.xbf`/`MainWindow.xbf`/`Assets`/`Views`,启动后立刻崩(0xC000027B)。
  - **当前 (MSIX packaged) 下是 no-op**,因为 `Condition="'$(WindowsPackageType)' == 'None'"` 不满足
  - 保留 target 作为"如果以后要切回 unpackaged 部署"的兜底;微软 2.2.0 仍未修这个 bug
- `RemoveUnwantedCultures` —— 同时清 `$(OutDir)` 和 `$(PublishDir)` 中 BCP 47 格式的 culture 子目录,实测 publish 产物从 222 MB / 572 文件减到 216 MB / 389 文件(86 个 culture 目录消失)。Build 和 Publish 两阶段都跑。

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