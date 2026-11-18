#Requires -Version 5.1
<#
.SYNOPSIS
    Build IcedPicViewer in Release, deploy it to %USERPROFILE%\App\IcedPicViewer,
    create/update the Start Menu shortcut, and launch it.

.DESCRIPTION
    Same pipeline as SavorReader's run-release.ps1:
      1. dotnet build -c Release -p:Platform=x64 --no-incremental
      2. copy src\IcedPicViewer.WinUI\bin\x64\Release\<tfm>\win-x64 to
         %USERPROFILE%\App\IcedPicViewer, preserving the user's Data folder
      3. create/update %APPDATA%\Microsoft\Windows\Start Menu\Programs\IcedPicViewer.lnk
      4. start the deployed exe

    Release is the only configuration for performance work (Runtime Async and
    other JIT fast paths are disabled in Debug).

.NOTES
    Prerequisites: .NET 11 runtime + Windows App Runtime 2.7.
    The app is unpackaged, so no developer mode or package identity is needed.
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoLaunch,
    [switch]$NoStartMenuShortcut
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\IcedPicViewer.WinUI\IcedPicViewer.csproj'
$tfm = 'net11.0-windows10.0.26100.0'
$rid = 'win-x64'
$output = Join-Path $root "src\IcedPicViewer.WinUI\bin\x64\Release\$tfm\$rid"
$destination = Join-Path $env:USERPROFILE 'App\IcedPicViewer'
$running = Get-Process -Name IcedPicViewer -ErrorAction SilentlyContinue

if ($running) {
    throw 'IcedPicViewer 正在运行。请先关闭它，再执行 release 部署。'
}

if (-not $NoBuild) {
    & dotnet build $project -c Release -p:Platform=x64 --nologo --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "Release 构建失败，退出码 $LASTEXITCODE。"
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $output 'IcedPicViewer.exe'))) {
    throw "找不到 Release 输出：$output\IcedPicViewer.exe"
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null

# Keep the user's Data directory across deployments; replace everything else.
Get-ChildItem -LiteralPath $destination -Force |
    Where-Object { $_.Name -ne 'Data' } |
    Remove-Item -Recurse -Force

Get-ChildItem -LiteralPath $output -Force |
    Where-Object { $_.Name -ne 'Data' } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
    }

$exe = Join-Path $destination 'IcedPicViewer.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "部署后找不到可执行文件：$exe"
}

Write-Host "已部署到 $destination"
Write-Host "数据目录：$(Join-Path $destination 'Data')"

if (-not $NoStartMenuShortcut) {
    try {
        $startMenuPrograms = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)) 'Programs'
        New-Item -ItemType Directory -Force -Path $startMenuPrograms | Out-Null
        $shortcutPath = Join-Path $startMenuPrograms 'IcedPicViewer.lnk'
        $wshShell = New-Object -ComObject WScript.Shell
        $shortcut = $wshShell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $destination
        $shortcut.IconLocation = "$exe,0"
        $shortcut.Description = 'IcedPicViewer local media browser'
        $shortcut.Save()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($wshShell)
        Write-Host "开始菜单快捷方式：$shortcutPath"
    }
    catch {
        Write-Warning "开始菜单快捷方式创建失败：$($_.Exception.Message)"
    }
}

if (-not $NoLaunch) {
    Start-Process -FilePath $exe -WorkingDirectory $destination
    Write-Host 'IcedPicViewer 已启动。'
}
