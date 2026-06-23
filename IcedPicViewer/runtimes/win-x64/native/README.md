# FFmpeg native runtime DLLs

**Source**: https://github.com/BtbN/FFmpeg-Builds/releases/latest
**Build**: `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip` (BtbN auto-build, 2026-06-22)
**License**: LGPL 2.1+ (LGPL shared build — uses the LGPL-compatible configuration: no non-free/encumbered code, distributed as dynamic libraries that the user can replace).

## Files

| File | Purpose |
|------|---------|
| `avcodec-62.dll` | Codec encoder + decoder library (largest file: ~67 MB) |
| `avformat-62.dll` | Container mux + demux |
| `avutil-60.dll` | Core utilities |
| `swresample-6.dll` | Audio resampling |
| `swscale-9.dll` | Image scaling + colorspace conversion |
| `avfilter-11.dll` | Filter graph (currently unused by IcedPicViewer) |
| `avdevice-62.dll` | Input/output device abstraction (currently unused) |

## LGPL compliance notes

LGPL 2.1+ requires:
1. **Source notice**: LGPL text accompanies the binaries (we will provide it in
   `License\ffmpeg-LGPL.txt` when video support ships).
2. **Reverse engineering for relinking**: the user must be able to swap the
   LGPL DLLs. MSIX-packaged deployment satisfies this — the DLLs sit at a
   predictable path inside the AppX package, and the user can replace them
   in-place.
3. **License prominence**: the app's AboutPage must credit FFmpeg + LGPL.
   Tracked as TODO in the video support work.

## Probe

To verify these DLLs load correctly inside the MSIX-packaged app at runtime,
set the env var before launching:

```powershell
$env:IPV_FFMPEG_PROBE = '1'
dotnet run -c Debug -p:Platform=x64
```

For a deeper test (file probe + thumbnail decode):

```powershell
$env:IPV_FFMPEG_PROBE = '1'
$env:IPV_FFMPEG_PROBE_VIDEO = 'C:\path\to\some.mp4'
$env:IPV_FFMPEG_PROBE_THUMBNAIL = '1'
dotnet run -c Debug -p:Platform=x64
```

Output is written to `%LOCALAPPDATA%\IcedPicViewer\ffmpeg-probe.log`
(thumbnail at `probe-thumb.jpg` in the same folder).