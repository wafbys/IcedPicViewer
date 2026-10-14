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

# Map RID → BtbN asset selector, pinned to the FFmpeg 9.0 branch (LGPL shared).
# BtbN names branch assets either after the branch tip or with the commit, e.g.
#   ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip
#   ffmpeg-n9.0.1-52-g5a03dfa0f6-win64-lgpl-shared-9.0.zip
# so the glob must stay loose around the version. Do NOT tighten this into a
# `-latest-` name: the fallback then picks a master nightly, and master carries
# next-major sonames that FFmpeg.AutoGen 9.0.1.1 cannot bind to.
$map = @{
    'win-x64'      = @{ urlPart = 'win64';      ext = 'zip';    glob = 'ffmpeg-n9.0*win64-lgpl-shared*.zip' }
    'win-arm64'    = @{ urlPart = 'winarm64';   ext = 'zip';    glob = 'ffmpeg-n9.0*winarm64-lgpl-shared*.zip' }
    'linux-x64'    = @{ urlPart = 'linux64';    ext = 'tar.xz'; glob = 'ffmpeg-n9.0*linux64-lgpl-shared*.tar.xz' }
    'linux-arm64'  = @{ urlPart = 'linuxarm64'; ext = 'tar.xz'; glob = 'ffmpeg-n9.0*linuxarm64-lgpl-shared*.tar.xz' }
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

# Soname guard against the DECLARED FFmpeg set. The expected library names are
# read from <FFmpegExpectedSonames> in the WinUI project — the single source of
# truth, also checked by the build's IPV002 warning. FFmpeg.AutoGen 9.0.1.1
# binds to exactly those sonames. A mismatched set does not fail the build; it
# disables video thumbs at runtime, silently, so catching it here is the
# cheapest possible fix.
$csproj = Join-Path $RepoRoot 'src/IcedPicViewer.WinUI/IcedPicViewer.csproj'
if (-not (Test-Path $csproj)) {
    throw "Project file not found: $csproj (needed to read the declared FFmpeg sonames)."
}
$declared = [regex]::Match((Get-Content $csproj -Raw), '<FFmpegExpectedSonames>([^<]+)</FFmpegExpectedSonames>')
if (-not $declared.Success) {
    throw "Could not read <FFmpegExpectedSonames> from $csproj."
}
$expected = @($declared.Groups[1].Value -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$names = @(Get-ChildItem $libDir.FullName -Force | Select-Object -ExpandProperty Name)
$missing = @()
foreach ($token in $expected) {
    if ($token -notmatch '^([A-Za-z0-9_]+)-(\d+)$') {
        throw "Unexpected soname '$token' in <FFmpegExpectedSonames> ($csproj) — expected e.g. avutil-61."
    }
    $lib = $Matches[1]
    $ver = $Matches[2]
    if (-not ($names | Where-Object { $_ -match "^(?:lib)?$lib[^0-9]*$ver" })) {
        $missing += "$lib/$ver"
    }
}
if ($missing.Count -gt 0) {
    throw ("FFmpeg natives do not match the declared sonames in $csproj — missing: $($missing -join ', '). " +
           "Asset '$($asset.name)' is likely a master/nightly build; pick an n9.0 (lgpl-shared) asset from " +
           "https://github.com/BtbN/FFmpeg-Builds/releases. Do not change the sonames without bumping " +
           "FFmpeg.AutoGen and <FFmpegExpectedSonames> in that same project.")
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
