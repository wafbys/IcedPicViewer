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

# Map RID → BtbN asset name fragment (latest n8.1 LGPL shared).
$map = @{
    'win-x64'      = @{ urlPart = 'win64';   ext = 'zip';   pattern = 'ffmpeg-n8.1-latest-win64-lgpl-shared-*.zip' }
    'win-arm64'    = @{ urlPart = 'winarm64'; ext = 'zip';   pattern = 'ffmpeg-n8.1-latest-winarm64-lgpl-shared-*.zip' }
    'linux-x64'    = @{ urlPart = 'linux64'; ext = 'tar.xz'; pattern = 'ffmpeg-n8.1-latest-linux64-lgpl-shared-*.tar.xz' }
    'linux-arm64'  = @{ urlPart = 'linuxarm64'; ext = 'tar.xz'; pattern = 'ffmpeg-n8.1-latest-linuxarm64-lgpl-shared-*.tar.xz' }
}

$meta = $map[$Rid]
$api = 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
Write-Host "Querying $api ..."
$rel = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'IcedPicViewer-FetchFFmpeg' }
$asset = $rel.assets | Where-Object { $_.name -like $meta.pattern } | Select-Object -First 1
if (-not $asset) {
    # Fallback: any *lgpl-shared* for this arch
    $asset = $rel.assets | Where-Object {
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

Write-Host "Copying from $($libDir.FullName) → $dest"
Get-ChildItem $libDir.FullName -File | Copy-Item -Destination $dest -Force

# Write stamp
@(
    "rid=$Rid"
    "source=$($asset.browser_download_url)"
    "name=$($asset.name)"
    "fetched=$(Get-Date -Format o)"
) | Set-Content (Join-Path $dest 'SOURCE.txt')

Write-Host "Done. $(@(Get-ChildItem $dest -File).Count) files in $dest"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
