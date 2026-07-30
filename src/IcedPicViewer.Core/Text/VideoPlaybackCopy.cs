// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Text;

/// <summary>
/// User-facing Chinese copy for video playback failures (WinUI MediaPlayer path).
/// Shell maps platform error codes to these templates.
/// </summary>
public static class VideoPlaybackCopy
{
    public const string CannotPlayTitle = "无法播放此视频";

    public static string CategorySourceNotSupported()
        => "Media Foundation 不支持此视频格式 (SourceNotSupported)。\n" +
           "通常是 codec 不被当前 Windows 解码，或者 codec 头损坏 / 非标。";

    public static string CategoryDecodingError()
        => "解码时发生错误 (DecodingError)。视频文件可能在传输中断、不完整，或 codec 参数异常。";

    public static string CategoryNetworkError()
        => "网络错误 (NetworkError)。此项目仅播放本地文件，不应触发此错误 — 可能是文件被外部 AV 扫描拦截。";

    public static string CategoryAborted()
        => "播放被中止 (Aborted)。通常是用户切到下一张 / 关闭查看器时正在解码。";

    public static string CategoryUnknown()
        => "未知播放错误。";

    public static string CodecUnknownLine()
        => "(codec 未知 — 扫描时未识别)";

    public static string DetailsHeader()
        => "— 详细信息 —";

    public static string CodecHintProRes(string codec)
        => "此文件用 Apple ProRes (" + codec + ") 编码 — 这是 macOS / Final Cut Pro 生态的专业 codec，Windows Media Foundation 不带 decoder，本应用也无法播放。\n\n" +
           "三个解决方法 (按推荐度):\n" +
           "1. 用 VLC / mpv / PotPlayer 等第三方播放器打开 — 它们自带 ProRes decoder，无需额外安装。\n" +
           "2. 安装 LAV Filters (https://github.com/Nevcairiel/LAVFilters/releases)，把 ProRes decoder 注入 Media Foundation — 装好后本应用和其他 WMP / Edge 视频也能播。\n" +
           "3. 用 FFmpeg / HandBrake 转封装成 H.264/AAC 的 mp4 (会显著损失质量且耗时较长):\n" +
           "   ffmpeg -i input.mov -c:v libx264 -crf 18 -c:a aac output.mp4";

    public static string CodecHintHevc()
        => "此文件用 HEVC / H.265 编码。Win10 默认不带 HEVC decoder，Win11 大多数版本自带但偶尔缺失。\n\n" +
           "建议: Microsoft Store 搜索 'HEVC Video Extensions' (免费 / 收费版功能一致)，装完即可在本应用播放。\n" +
           "或者用 FFmpeg 转 H.264 之后再用 — ffmpeg -i input.mov -c:v libx264 -crf 20 -c:a aac output.mp4";

    public static string CodecHintVp8()
        => "此文件用 VP8 编码（常见于 .webm）。Windows Media Foundation 无法解码；本应用会改用内置 LibVLC 播放。\n" +
           "若仍失败，请确认已安装 VideoLAN LibVLC 运行库，或用系统 VLC / mpv 打开。";

    public static string CodecHintVp9()
        => "此文件用 VP9 编码。Windows Media Foundation 通常不带 VP9 decoder；本应用会改用内置 LibVLC 播放。\n" +
           "也可转码: ffmpeg -i input.webm -c:v libx264 -crf 22 -c:a aac output.mp4";

    public static string CodecHintAv1()
        => "此文件用 AV1 编码。Win10 / 部分 Win11 缺少 MF decoder；本应用会改用内置 LibVLC 播放。\n" +
           "也可安装 Microsoft Store「AV1 Video Extension」，或 FFmpeg 转 H.264。";

    public static string CodecHintGeneric(string codec)
        => $"此文件用非主流 codec ({codec}) 编码，Windows Media Foundation 很可能无法解码。\n" +
           "本应用会优先用内置 LibVLC 尝试播放；仍失败时请用 VLC / mpv 打开，或转 H.264+AAC 的 mp4。";

    public static string GetCodecSpecificHint(string? codec)
    {
        if (string.IsNullOrEmpty(codec)) return string.Empty;
        var c = codec.Trim().ToLowerInvariant();
        if (c.StartsWith("prores", StringComparison.Ordinal))
            return CodecHintProRes(codec);
        if (c is "hevc" or "h265" || c.StartsWith("hevc", StringComparison.Ordinal))
            return CodecHintHevc();
        if (c is "vp8" || c.StartsWith("vp8", StringComparison.Ordinal))
            return CodecHintVp8();
        if (c is "vp9" || c.StartsWith("vp9", StringComparison.Ordinal))
            return CodecHintVp9();
        if (c is "av1" || c.StartsWith("av1", StringComparison.Ordinal))
            return CodecHintAv1();
        return CodecHintGeneric(codec);
    }

    public static string PrePlayRemuxNotMp4Compatible()
        => "无法将此视频重新封装为 MP4 — 视频使用的 codec 不被 MP4 容器支持，本应用的播放引擎也不直接支持该 codec。";

    public static string PrePlayRemuxFailed()
        => "FFmpeg 重新封装失败。可能是视频文件已损坏，或 codec / 容器组合不被 MP4 支持。";

    public static string PrePlayFileNotFound()
        => "找不到视频文件 (可能在浏览到此处后被移动 / 删除 / 重命名)。";

    public static string PrePlayDirectoryNotFound()
        => "视频所在目录不存在 (可能被移动 / 删除)。";

    public static string PrePlayUnauthorized()
        => "没有读取此视频文件的权限 (可能被其他进程独占，或目录权限不足)。";

    public static string PrePlayIoError()
        => "读取视频文件时发生 I/O 错误。可能是文件被外部 AV 扫描程序锁定，或磁盘出错。";

    public static string PrePlayGeneric()
        => "播放准备失败。";

    public static string ClassifyPrePlayException(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("not MP4-compatible", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("codec likely", StringComparison.OrdinalIgnoreCase))
            return PrePlayRemuxNotMp4Compatible();
        if (msg.StartsWith("RemuxToMp4:", StringComparison.Ordinal))
            return PrePlayRemuxFailed();

        return ex switch
        {
            FileNotFoundException => PrePlayFileNotFound(),
            DirectoryNotFoundException => PrePlayDirectoryNotFound(),
            UnauthorizedAccessException => PrePlayUnauthorized(),
            IOException => PrePlayIoError(),
            _ => PrePlayGeneric(),
        };
    }

    public static string ComposeErrorMessage(string? codecHint, string categoryHint, string detailsBlock)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(codecHint)) parts.Add(codecHint);
        parts.Add(categoryHint);
        parts.Add(DetailsHeader() + "\n" + detailsBlock);
        return string.Join("\n\n", parts);
    }

    public static string FormatPlaybackDetails(string hresultHex, string errorCategory, string codecLine, string systemMessage)
        => $"HRESULT: {hresultHex}\n" +
           $"类别: {errorCategory}\n" +
           $"视频 codec: {codecLine}\n" +
           $"系统消息: {systemMessage}";

    public static string FormatPrePlayDetails(string exceptionType, string codecLine, string systemMessage)
        => $"异常类型: {exceptionType}\n" +
           $"视频 codec: {codecLine}\n" +
           $"系统消息: {systemMessage}";
}
