# FFmpeg native runtime DLLs (not in git)

**这些 `*.dll` 不再纳入版本库**（`avcodec` 约 67MB，会触发 GitHub 警告）。

## 如何获取

在仓库根目录执行（推荐，写入统一目录再同步到此处）：

```powershell
./tools/Fetch-FFmpegNatives.ps1 -Rid win-x64
# 脚本会填充 src/native/ffmpeg/win-x64/
# 若需要 WinUI 工程本地路径，再复制：
Copy-Item src/native/ffmpeg/win-x64/*.dll src/IcedPicViewer.WinUI/runtimes/win-x64/native/ -Force
```

或从 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) 下载  
`ffmpeg-n8.1-latest-win64-lgpl-shared-*.zip`，把 `bin\` 下 DLL 放进本目录。

需要的大致文件：`avcodec-*.dll`、`avformat-*.dll`、`avutil-*.dll`、`swresample-*.dll`、`swscale-*.dll`（以及可选的 avfilter / avdevice）。

## 用途

- WinUI：`IcedPicViewer.csproj` 的 `Content` + `CopyFFmpegDllsToAppX` 会拷进 AppX 根目录供 `LoadLibrary` 使用。
- Avalonia：优先 `src/native/ffmpeg/win-x64/`；若无则回落本目录（若本机已拷贝）。

## License

LGPL 2.1+ shared builds。见 `License/ffmpeg-LGPL.txt`。
