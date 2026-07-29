// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Text;

/// <summary>Shared About dialog / page copy (Chinese).</summary>
public static class AboutCopy
{
    public const string Title = "关于 IcedPicViewer";

    public static string AvaloniaBody(string versionLabel, string commitShort, string licensePathLine, string settingsPath)
        => $"IcedPicViewer (Avalonia)  {versionLabel}  ({commitShort})\n\n" +
           "跨平台图库：瀑布流、压缩包展平 (ZIP/RAR/tar.*)、EXIF、GIF、\n" +
           "幻灯片、视频缩略图 (FFmpeg LGPL shared) + 播放 (LibVLC)。\n\n" +
           "格式说明见 README — HEIC/AVIF 依赖平台编解码；不支持 7z。\n" +
           "FFmpeg 为 LGPL 2.1+（可替换 natives 位于 runtimes/<rid>/native）。\n" +
           $"LGPL 文本：\n{licensePathLine}\n\n" +
           $"设置文件：\n{settingsPath}";

    public static string WinUiIntro()
        => "一个基于 WinUI 3 + Windows App SDK 的图片 / 视频查看器。本地优先、纯查看器，不上传、不联网。";

    public static string FfmpegDescriptionZh()
        => "版本 8.1 (BtbN 构建，LGPL 2.1+ shared)。用于读取视频元数据 (分辨率、时长、音轨) 和首帧缩略图。";
}
