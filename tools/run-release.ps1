#Requires -Version 5.1
<#
.SYNOPSIS
    以 Release 配置构建并启动 IcedPicViewer（WinUI，x64）。

.DESCRIPTION
    等价于在仓库根执行：
        dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Release -p:Platform=x64

    Release 才会启用 Runtime Async 等 JIT fast path，是评估性能 / 真实吞吐的唯一配置。

.NOTES
    前置：Windows 开发者模式 + .NET 11 runtime + Windows App Runtime 2.5。
    -p:Platform=x64 不可省略（该工程仅支持 x64）。
    不要直接双击 bin 下的 IcedPicViewer.exe：缺 package identity 会 REGDB_E_CLASSNOTREG。
#>
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src/IcedPicViewer.WinUI/IcedPicViewer.csproj'

$code = 0
Push-Location $repoRoot
try {
    & dotnet run --project $project -c Release -p:Platform=x64
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $code
