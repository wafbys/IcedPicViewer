#Requires -Version 5.1
<#
.SYNOPSIS
    以 Debug 配置构建并启动 IcedPicViewer（WinUI，x64）。

.DESCRIPTION
    等价于在仓库根执行：
        dotnet run --project src/IcedPicViewer.WinUI/IcedPicViewer.csproj -c Debug -p:Platform=x64

    应用是 unpackaged（WindowsPackageType=None），不需要开发者模式或包身份；
    dotnet run 直接启动 exe。

    数据写在 exe 旁的 Data\（AppDataPaths）。

.NOTES
    评估性能 / 看真实吞吐请改用 tools/run-release.ps1（Debug 不做 JIT 优化）。
    -p:Platform=x64 不可省略（该工程仅支持 x64）。
#>
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src/IcedPicViewer.WinUI/IcedPicViewer.csproj'

$code = 0
Push-Location $repoRoot
try {
    & dotnet run --project $project -c Debug -p:Platform=x64
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $code
