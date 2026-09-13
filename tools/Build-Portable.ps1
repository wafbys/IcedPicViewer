#Requires -Version 5.1
<#
.SYNOPSIS
  Build the unpackaged "green" (portable) IcedPicViewer for Windows x64.

.DESCRIPTION
  Produces a folder that runs by double-clicking IcedPicViewer.exe — no MSIX
  install, no package identity, no %LOCALAPPDATA%\Packages entry.

  Unpackaged is mandatory here: the default MSIX-packaged build dies with
  REGDB_E_CLASSNOTREG when its .exe is started directly, because the process
  has no package identity. `WindowsPackageType=None` is what makes a real
  double-clickable exe.

  The output is 100% green: a portable.marker file next to the exe makes
  AppDataPaths keep settings.json / window geometry / crash.log / temp video
  in .\data instead of %LOCALAPPDATA%\IcedPicViewer. Delete the marker to go
  back to the profile path.

.PARAMETER Flavor
  selfcontained (default)
      Bundles the .NET 10 runtime AND the Windows App SDK runtime. Runs on a
      clean Windows 11 x64 machine with nothing preinstalled. ~480 MB.
  framework
      Small output; the target machine needs the .NET 10 desktop runtime and
      a matching Windows App Runtime. ~340 MB.

.PARAMETER Output
  Output folder. Default: <repo>\artifacts\portable (git-ignored).

.PARAMETER Zip
  Also write <Output>.zip (Compress-Archive, Optimal).

.EXAMPLE
  ./tools/Build-Portable.ps1
  ./tools/Build-Portable.ps1 -Flavor framework -Output .\artifacts\portable-fd -Zip
#>
param(
    [ValidateSet('selfcontained', 'framework')]
    [string] $Flavor = 'selfcontained',

    [string] $Output,

    [switch] $Zip,

    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $RepoRoot 'src/IcedPicViewer.WinUI/IcedPicViewer.csproj'
if (-not (Test-Path $project)) { throw "Project not found: $project" }

if (-not $Output) { $Output = Join-Path $RepoRoot 'artifacts/portable' }
$Output = [IO.Path]::GetFullPath($Output)

# FFmpeg natives are not in git and the build only warns (IPV001) when they
# are missing — but video thumbs/probe silently die at runtime. Fail loudly.
$ffmpegProbe = Join-Path $RepoRoot 'src/native/ffmpeg/win-x64/avutil-60.dll'
if (-not (Test-Path $ffmpegProbe)) {
    throw "FFmpeg natives missing ($ffmpegProbe). Run: ./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64"
}

if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }

# Flavor-clean build. A packaged build and an unpackaged build write DIFFERENT
# resource indexes into the same bin tree: packaged merges the Windows App SDK
# framework resources into IcedPicViewer.pri (~2.3 MB), unpackaged leaves them
# in the separate Microsoft.UI.*.pri files (~66 KB index). MSBuild's incremental
# build happily reuses the other flavor's index, and the app then dies with an
# unhandled COMException a few seconds after launch:
#   "The resource loader cache doesn't have loaded MUI entry" /
#   "Cannot locate resource from ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml".
# Observed 2026-09-13 on a portable build published right after a packaged one.
# Removing just the flavor-sensitive artifacts keeps the build fast (no full
# re-restore) and still forces both the index and the AppX leftovers to be
# regenerated for the unpackaged layout.
$outDir = Join-Path $RepoRoot 'src/IcedPicViewer.WinUI/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64'
$staleItems = @(
    (Join-Path $outDir 'IcedPicViewer.pri'),   # stale merged index → MRT failures
    (Join-Path $outDir 'resources.pri'),       # packaged index, wrong for unpackaged
    (Join-Path $outDir 'AppX')                 # packaged layout leftovers
)
foreach ($item in $staleItems) {
    if (Test-Path $item) {
        Write-Host "Removing stale packaged artifact: $item"
        try {
            Remove-Item -Recurse -Force $item -ErrorAction Stop
        }
        catch {
            throw ("Could not remove $item ($($_.Exception.Message)). Close anything holding the build output " +
                   "(editor, grep tool, running app) and retry — a stale resource index silently breaks the app at runtime.")
        }
    }
}

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-p:Platform=x64',
    '-p:WindowsPackageType=None',
    # Keep the culture folders. In an unpackaged/self-contained build
    # Microsoft.ui.xaml.dll lives in this folder and loads its localized
    # resources from <culture>\Microsoft.ui.xaml.dll.mui. The project's
    # RemoveUnwantedCultures target deletes every culture folder (fine for
    # MSIX, where the Windows App Runtime framework package supplies MUI), and
    # without them the app throws an unhandled COMException a few seconds in:
    # "The resource loader cache doesn't have loaded MUI entry". Costs ~3 MB.
    '-p:IcedPicViewerKeepCultures=true',
    '-o', $Output
)
if ($Flavor -eq 'selfcontained') {
    $publishArgs += '-p:WindowsAppSDKSelfContained=true'
    $publishArgs += '-p:SelfContained=true'
}

Write-Host "Building portable ($Flavor) -> $Output"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# 100% green: AppDataPaths redirects settings/window geometry/crash log/temp
# video to <exe>\data when this marker sits next to the executable. Without it
# the app would scatter state into %LOCALAPPDATA%\IcedPicViewer.
$markerPath = Join-Path $Output 'portable.marker'
@(
    "IcedPicViewer portable build ($Flavor).",
    "Presence of this file makes the app keep its data in .\data next to the exe.",
    "Delete it to fall back to %LOCALAPPDATA%\IcedPicViewer.",
    "built=$(Get-Date -Format o)"
) | Set-Content -Path $markerPath -Encoding UTF8

# Post-conditions: exactly the things that make the folder runnable.
$required = @(
    (Join-Path $Output 'IcedPicViewer.exe'),
    (Join-Path $Output 'License/ffmpeg-LGPL.txt'),
    (Join-Path $Output 'runtimes/win-x64/native/avcodec-62.dll'),
    $markerPath
)
if ($Flavor -eq 'selfcontained') {
    $required += (Join-Path $Output 'Microsoft.WindowsAppRuntime.dll')
    $required += (Join-Path $Output 'coreclr.dll')
}
foreach ($item in $required) {
    if (-not (Test-Path $item)) { throw "Portable build incomplete — missing: $item" }
}

# MRT index must exist next to the exe. Without it (or with one generated for a
# different layout) the app starts, then dies seconds later on the first
# resource lookup with an unhandled COMException. The index is small for
# framework-dependent builds and ~2 MB when WindowsAppSDKSelfContained merges
# the framework resources, so only its presence is checked, not its size.
$priPath = Join-Path $Output 'IcedPicViewer.pri'
if (-not (Test-Path $priPath)) { throw "Portable build incomplete — missing MRT index: $priPath" }

# The native WinUI MUI resources must survive (see the KeepCultures note above).
# Missing them = "The resource loader cache doesn't have loaded MUI entry" crash.
$mui = Get-ChildItem -Path $Output -Recurse -File -Filter 'Microsoft.ui.xaml.dll.mui' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $mui) {
    throw ("Portable build incomplete — no Microsoft.ui.xaml.dll.mui under $Output. " +
           "The culture folders were stripped (RemoveUnwantedCultures target); pass -p:IcedPicViewerKeepCultures=true.")
}

$sizeMb = [math]::Round(((Get-ChildItem -Recurse -Force -File $Output | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "Done: $Output ($sizeMb MB)"

if ($Zip) {
    $zipPath = "$Output.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Write-Host "Zipping -> $zipPath"
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Zip: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"
}

if ($Flavor -eq 'framework') {
    Write-Host "Note: 'framework' needs .NET 10 desktop runtime + Windows App Runtime on the target machine."
} else {
    Write-Host "Note: 'selfcontained' needs nothing preinstalled — double-click IcedPicViewer.exe."
}
