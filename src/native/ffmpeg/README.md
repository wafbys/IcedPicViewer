# FFmpeg shared natives

Used by `IcedPicViewer.Core` / FFmpeg.AutoGen for **video thumbnails** and
**animated WebP frames** (the platform imaging stack serves WebP as a still).

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

`win-x64` is the only RID this repo builds or fetches for — the product is
Windows-only (x64).

## How the DLLs reach the app

`IcedPicViewer.csproj`'s `CopyFFmpegDllsToOutDir` target copies
`src/native/ffmpeg/win-x64/*.dll` flat into `$(OutDir)` — which is also the MSIX
install root — because FFmpeg's `LoadLibrary("avcodec-63")` does not search
subdirectories. Missing natives produce build warning `IPV001` at build time and
silently disable video thumbs/probe at runtime.

## Fallback

| Order | Location |
|-------|----------|
| 1 | Env `IPV_FFMPEG_ROOT` = directory containing avutil |
| 2 | App output root (populated by the build target above; = MSIX install root) |
| 3 | `runtimes\win-x64\native` (framework-style layout) |
| 4 | `native\` subfolder |

(Windows-only product: the resolver probes no other RID layouts.)

**DLL 不提交到 Git。**

## License

Prefer **LGPL shared** builds (user-replaceable). See WinUI `License/ffmpeg-LGPL.txt`.
