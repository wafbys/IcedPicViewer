# FFmpeg shared natives (per RID)

Used by `IcedPicViewer.Core` / FFmpeg.AutoGen for **video thumbnails**.

Binaries are **not** committed (large LGPL shared builds). Fetch them with:

```powershell
# From repo root (Windows PowerShell)
./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64
./tools/Fetch-FFmpegNatives.ps1 -Rid linux-x64
```

```bash
# From repo root (Linux / macOS)
./tools/Fetch-FFmpegNatives.sh linux-x64
./tools/Fetch-FFmpegNatives.sh osx-arm64   # uses Homebrew layout hint if no archive
```

## Layout

```
src/native/ffmpeg/
  win-x64/       avutil-*.dll, avcodec-*.dll, ...
  win-arm64/
  linux-x64/     libavutil.so*, libavcodec.so*, ...
  linux-arm64/
  osx-x64/       libavutil*.dylib, ...
  osx-arm64/
```

`IcedPicViewer.Avalonia.csproj` copies each existing RID folder to  
`bin/.../runtimes/{rid}/native/`.

## Fallback

| Platform | Fallback if folder empty |
|----------|---------------------------|
| Windows x64 | `src/IcedPicViewer.WinUI/runtimes/win-x64/native` (already in repo) |
| Linux | Distro packages, e.g. `sudo apt install libavcodec-dev libavformat-dev libavutil-dev libswscale-dev` (libs under `/usr/lib/...`) |
| macOS | `brew install ffmpeg` → `/opt/homebrew/lib` or `/usr/local/lib` |
| Any | Env `IPV_FFMPEG_ROOT` = directory containing avutil / libavutil |

## License

Prefer **LGPL shared** builds (user-replaceable). See WinUI `License/ffmpeg-LGPL.txt`.
