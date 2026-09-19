# FFmpeg shared natives

Used by `IcedPicViewer.Core` / FFmpeg.AutoGen for **video thumbnails**.

Binaries are **not** committed (large LGPL shared builds). Fetch them with:

```powershell
# From repo root (Windows PowerShell)
./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64
```

## Layout

```
src/native/ffmpeg/
  win-x64/       avutil-*.dll, avcodec-*.dll, ...
```

`win-x64` is the only RID this repo builds for. The script still accepts
`win-arm64` / `linux-x64` / `linux-arm64` for anyone cross-fetching, but nothing
in the tree consumes them.

## How the DLLs reach the app

`IcedPicViewer.csproj`'s `CopyFFmpegDllsToOutDir` target copies
`src/native/ffmpeg/win-x64/*.dll` flat into `$(OutDir)` — which is also the MSIX
install root — because FFmpeg's `LoadLibrary("avcodec-62")` does not search
subdirectories. Missing natives produce build warning `IPV001` at build time and
silently disable video thumbs/probe at runtime.

## Fallback

| Order | Location |
|-------|----------|
| 1 | Env `IPV_FFMPEG_ROOT` = directory containing avutil |
| 2 | App output directory (populated by the build target above) |
| 3 | System PATH |

**DLL 不提交到 Git。**

## License

Prefer **LGPL shared** builds (user-replaceable). See WinUI `License/ffmpeg-LGPL.txt`.
