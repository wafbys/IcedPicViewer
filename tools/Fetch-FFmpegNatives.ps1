#Requires -Version 5.1
<#
.SYNOPSIS
  Download FFmpeg LGPL shared libraries into src/native/ffmpeg/{rid}/

.PARAMETER Rid
  win-x64 | win-arm64 | linux-x64 | linux-arm64

.NOTES
  Sources: BtbN/FFmpeg-Builds (Linux/Windows). macOS: use Homebrew or set IPV_FFMPEG_ROOT.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string] $Rid,

    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$dest = Join-Path $RepoRoot "src/native/ffmpeg/$Rid"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Map RID → BtbN asset selector, pinned to the FFmpeg 8.1 branch (LGPL shared).
# BtbN names branch assets with the commit, e.g.
#   ffmpeg-n8.1.2-52-g5a03dfa0f6-win64-lgpl-shared-8.1.zip
# so the glob must stay loose around the version. Do NOT tighten this into a
# `-latest-` name: that fragment no longer exists upstream, the fallback then
# picks a master nightly, and master carries next-major sonames
# (avutil-61 / avcodec-63) that FFmpeg.AutoGen 8.1.0 cannot bind to.
$map = @{
    'win-x64'      = @{ urlPart = 'win64';      ext = 'zip';    glob = 'ffmpeg-n8.1*win64-lgpl-shared*.zip' }
    'win-arm64'    = @{ urlPart = 'winarm64';   ext = 'zip';    glob = 'ffmpeg-n8.1*winarm64-lgpl-shared*.zip' }
    'linux-x64'    = @{ urlPart = 'linux64';    ext = 'tar.xz'; glob = 'ffmpeg-n8.1*linux64-lgpl-shared*.tar.xz' }
    'linux-arm64'  = @{ urlPart = 'linuxarm64'; ext = 'tar.xz'; glob = 'ffmpeg-n8.1*linuxarm64-lgpl-shared*.tar.xz' }
}

$meta = $map[$Rid]
$api = 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
Write-Host "Querying $api ..."
$rel = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'IcedPicViewer-FetchFFmpeg' }
# Master nightlies are excluded everywhere: they are FFmpeg master (next major
# sonames) and would break the AutoGen binding at runtime, silently.
$candidates = @($rel.assets | Where-Object { $_.name -notlike 'ffmpeg-N-*' })
$asset = $candidates | Where-Object { $_.name -like $meta.glob } | Select-Object -First 1
if (-not $asset) {
    # Fallback: any *lgpl-shared* for this arch, still no master nightlies.
    $asset = $candidates | Where-Object {
        $_.name -match [regex]::Escape($meta.urlPart) -and $_.name -match 'lgpl-shared'
    } | Select-Object -First 1
}
if (-not $asset) {
    throw "No BtbN LGPL shared asset found for $Rid. Check https://github.com/BtbN/FFmpeg-Builds/releases"
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$archive = Join-Path $tmp $asset.name
Write-Host "Downloading $($asset.browser_download_url) ..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing

$extract = Join-Path $tmp 'extract'
New-Item -ItemType Directory -Force -Path $extract | Out-Null
if ($meta.ext -eq 'zip') {
    Expand-Archive -Path $archive -DestinationPath $extract -Force
} else {
    # tar.xz
    tar -xJf $archive -C $extract
}

# Find bin/ or lib/ with shared libs
$libDir = Get-ChildItem -Path $extract -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('bin', 'lib') } |
    Where-Object {
        (Get-ChildItem $_.FullName -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'avutil' }).Count -gt 0
    } |
    Select-Object -First 1

if (-not $libDir) {
    throw "Could not locate avutil shared library inside archive."
}

# Soname guard. FFmpeg.AutoGen 8.1.0 hard-codes the FFmpeg 8.x library names
# (avutil-60 / avcodec-62 / ...); a mismatched set does not fail the build —
# FFmpegBootstrap catches the load error and just disables video thumbs, so
# silently fetching master here costs hours of "why is video broken".
$expected = [ordered]@{ avutil = 60; avcodec = 62; avformat = 62; avfilter = 11; swscale = 9; swresample = 6 }
$names = @(Get-ChildItem $libDir.FullName -Force | Select-Object -ExpandProperty Name)
$missing = @()
foreach ($lib in $expected.Keys) {
    if (-not ($names | Where-Object { $_ -match "^(?:lib)?$lib[^0-9]*$($expected[$lib])" })) {
        $missing += "$lib/$($expected[$lib])"
    }
}
if ($missing.Count -gt 0) {
    throw ("FFmpeg natives do not match FFmpeg.AutoGen 8.1.0 — missing sonames: $($missing -join ', '). " +
           "Asset '$($asset.name)' is likely a master/nightly build; pick an n8.1 (lgpl-shared) asset from " +
           "https://github.com/BtbN/FFmpeg-Builds/releases. Do not 'upgrade' the sonames without bumping " +
           "FFmpeg.AutoGen and the avutil-60/avcodec-62 checks in src/IcedPicViewer.WinUI/IcedPicViewer.csproj.")
}

Write-Host "Copying from $($libDir.FullName) → $dest"
Get-ChildItem $libDir.FullName -File | Copy-Item -Destination $dest -Force

# Write stamp
@(
    "rid=$Rid"
    "source=$($asset.browser_download_url)"
    "name=$($asset.name)"
    "fetched=$(Get-Date -Format o)"
) | Set-Content (Join-Path $dest 'SOURCE.txt')

# Convenience: mirror win-x64 into WinUI project path (build expects it there).
if ($Rid -eq 'win-x64') {
    $winuiNative = Join-Path $RepoRoot 'src/IcedPicViewer.WinUI/runtimes/win-x64/native'
    New-Item -ItemType Directory -Force -Path $winuiNative | Out-Null
    Get-ChildItem $dest -File -Filter '*.dll' | Copy-Item -Destination $winuiNative -Force
    Write-Host "Also mirrored *.dll → $winuiNative"
}

Write-Host "Done. $(@(Get-ChildItem $dest -File).Count) files in $dest"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
