# Generate IcedPicViewer placeholder icon assets.
#
# Outputs a monogram "I" mark (rounded square + bold white "I") at all
# required WinUI 3 / MSIX icon sizes, overwriting the Visual Studio
# template defaults. Designers should replace these later with proper
# art; until then the app has a coherent, recognizable mark.
#
# Invoked from IcedPicViewer.csproj's GenerateIconAssets target.
# Re-runs on every build, so changes to colors/layout propagate.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsDir
)

# MSBuild's <Exec Command="...&quot;$(_IconAssetsDir)&quot;" /> goes through
# cmd.exe first, and a trailing backslash inside a quoted argument comes out
# as `\""` (an escaped quote) — PowerShell then sees a stray `"` appended to
# the path. Strip any trailing quotes/backslashes defensively.
$AssetsDir = $AssetsDir -replace '["\\\s]+$', ''

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
trap {
    Write-Output ('ERROR line {0}: {1}' -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
    Write-Output $_.ScriptStackTrace
    exit 1
}

# Brand colors
$bgColor = [System.Drawing.Color]::FromArgb(255, 30, 58, 95)    # #1E3A5F (deep ice blue)
$fgColor = [System.Drawing.Color]::FromArgb(255, 248, 250, 252) # #F8FAFC (near-white)

# Base canvas (large; resize down for crispness at every output size)
$base = 1024
$bmp = New-Object System.Drawing.Bitmap($base, $base, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

# Rounded-square background
$radius = [int]($base * 0.18)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$d = $radius * 2
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($base - $d, 0, $d, $d, 270, 90)
$path.AddArc($base - $d, $base - $d, $d, $d, 0, 90)
$path.AddArc(0, $base - $d, $d, $d, 90, 90)
$path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush $bgColor), $path)
$path.Dispose()

# Centered "I" monogram
$fontSize = $base * 0.62
$font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$rect = New-Object System.Drawing.RectangleF(0, 0, $base, $base)
$g.DrawString('I', $font, (New-Object System.Drawing.SolidBrush $fgColor), $rect, $sf)
$font.Dispose()
$sf.Dispose()
$g.Dispose()

function Save-Png($bmp, $size, $path) {
    if ($bmp.Width -eq $size -and $bmp.Height -eq $size) {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        return
    }
    $dst = New-Object System.Drawing.Bitmap $size, $size
    $gg = [System.Drawing.Graphics]::FromImage($dst)
    $gg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gg.DrawImage($bmp, 0, 0, $size, $size)
    $gg.Dispose()
    $dst.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
}

function Save-PngRect($bmp, $w, $h, $path) {
    if ($bmp.Width -eq $w -and $bmp.Height -eq $h) {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        return
    }
    $dst = New-Object System.Drawing.Bitmap $w, $h
    $gg = [System.Drawing.Graphics]::FromImage($dst)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.DrawImage($bmp, 0, 0, $w, $h)
    $gg.Dispose()
    $dst.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
}

# Icon sizes required by Package.appxmanifest and Windows shell scaling.
# Naming follows the Windows asset convention: <base>.scale-<dpi>.png
# and <base>.targetsize-<px>_altform-<theme>.png
$sizes = @{
    'StoreLogo.png' = 50
    'Square44x44Logo.png' = 44
    'Square44x44Logo.scale-200.png' = 88
    'Square44x44Logo.targetsize-24_altform-unplated.png' = 24
    'Square44x44Logo.targetsize-48_altform-lightunplated.png' = 48
    'Square150x150Logo.png' = 150
    'Square150x150Logo.scale-200.png' = 300
    'Wide310x150Logo.png' = @{ W = 310; H = 150 }
    'Wide310x150Logo.scale-200.png' = @{ W = 620; H = 300 }
    'SplashScreen.png' = @{ W = 620; H = 300 }
    'SplashScreen.scale-200.png' = @{ W = 1240; H = 600 }
    'LockScreenLogo.scale-200.png' = 48
}

foreach ($name in $sizes.Keys) {
    $spec = $sizes[$name]
    $out = Join-Path $AssetsDir $name
    if ($spec -is [int]) {
        Save-Png $bmp $spec $out
    } else {
        Save-PngRect $bmp $spec.W $spec.H $out
    }
}

# Build a multi-size .ico. We construct it manually as a sequence of
# ICO-directory entries each pointing at a PNG payload, because
# Bitmap.GetHicon only produces a single-size HICON and we want all
# common shell sizes (16/24/32/48/64/128/256) in one file.
$icoSizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBlobs = @()
foreach ($s in $icoSizes) {
    $tmp = New-Object System.Drawing.Bitmap $s, $s
    $gg = [System.Drawing.Graphics]::FromImage($tmp)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.DrawImage($bmp, 0, 0, $s, $s)
    $gg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $tmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $tmp.Dispose()
    $pngBlobs += , $ms.ToArray()
    $ms.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
# ICONDIR header: reserved=0, type=1 (icon), count
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$icoSizes.Count)
# Each ICONDIRENTRY is 16 bytes: width(1), height(1), colorCount(1),
# reserved(1), planes(2), bitCount(2), bytesInRes(4), imageOffset(4)
$entrySize = 16
$dataOffset = 6 + $entrySize * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $s = $icoSizes[$i]
    $w = if ($s -ge 256) { 0 } else { $s }
    $h = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)        # color count (palette; 0 for >=8bpp)
    $bw.Write([byte]0)        # reserved
    $bw.Write([uint16]1)      # color planes
    $bw.Write([uint16]32)     # bits per pixel
    $bw.Write([uint32]$pngBlobs[$i].Length)
    $bw.Write([uint32]$dataOffset)
    $dataOffset += $pngBlobs[$i].Length
}
foreach ($blob in $pngBlobs) { $bw.Write($blob) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $AssetsDir 'AppIcon.ico'), $ico.ToArray())
$bw.Dispose()
$ico.Dispose()
$bmp.Dispose()

Write-Output ('Generated icon assets in: ' + $AssetsDir)
