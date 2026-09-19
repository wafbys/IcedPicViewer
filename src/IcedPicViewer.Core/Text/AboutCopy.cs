// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Text;

/// <summary>Shared About dialog / page copy (Chinese).</summary>
public static class AboutCopy
{
    public const string Title = "关于 IcedPicViewer";

    public static string WinUiIntro()
        => "一个基于 WinUI 3 + Windows App SDK 的图片 / 视频查看器。本地优先、纯查看器，不上传、不联网。";

    public static string FfmpegDescriptionZh()
        => "版本 8.1 (BtbN 构建，LGPL 2.1+ shared)。用于读取视频元数据 (分辨率、时长、音轨) 和首帧缩略图。";
}
