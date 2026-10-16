// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;

namespace IcedPicViewer.Core.Text;

/// <summary>Shared About dialog / page copy (Chinese).</summary>
public static class AboutCopy
{
    public const string Title = "关于 IcedPicViewer";
    public const string ThirdPartySectionTitle = "第三方组件";
    public const string FfmpegHomepageLink = "FFmpeg 官方网站 (ffmpeg.org)";
    public const string FfmpegBuildsLink = "BtbN/FFmpeg-Builds (本项目使用的构建来源)";
    public const string LicenseFullTextLink = "LGPL 2.1 许可证全文 (本项目内嵌副本)";

    public static string WinUiIntro()
        => "一个基于 WinUI 3 + Windows App SDK 的图片 / 视频查看器。本地优先、纯查看器，不上传、不联网。";

    public static string FfmpegDescriptionZh()
        => "版本 9.0 (BtbN 构建，LGPL 2.1+ shared)。用于读取视频元数据 (分辨率、时长、音轨)、首帧缩略图，以及动图 WebP 的逐帧解码。";

    public const string FfmpegStatusChecking = "运行状态：检测中…";

    /// <summary>
    /// Runtime FFmpeg truth line. The description above states what the build
    /// ships and expects; this states what actually loaded in the running process
    /// — the failure mode that used to be invisible (a missing or version-
    /// mismatched native set silently disables video thumbnails).
    /// </summary>
    public static string FfmpegStatus(bool ready, string? version, string? directory)
    {
        if (!ready)
            return "运行状态：不可用——视频缩略图、视频元数据与动图 WebP 无法解码（FFmpeg 原生库缺失或与 FFmpeg.AutoGen 版本不符）。";

        var versionPart = string.IsNullOrEmpty(version) ? string.Empty : $"（{version}）";
        var dirPart = string.IsNullOrEmpty(directory) ? string.Empty : $"；{directory}";
        return $"运行状态：已加载{versionPart}{dirPart}";
    }

    public const string ProjectLinkText = "项目源码 (GitHub)";
    public const string AppLicenseLinkText = "MIT 许可证 (GitHub)";

    public static string AppLicenseZh()
        => "应用代码以 MIT 许可证发布（见仓库根 LICENSE）——捆绑依赖均为宽松 / 弱 copyleft 且动态链接，不改变应用许可。";

    public const string LibVlcSectionTitle = "LibVLC";

    public static string LibVlcDescriptionZh()
        => "版本 3.0.24（VideoLAN 原生库，LGPL 2.1+ shared）。用于媒体播放回退：Media Foundation 无法 remux / 解码的容器与编码（VP8/WebM、ProRes 等）经 LibVLC 软渲染播放；原生库随包安装，可在原地替换。";

    public const string OtherComponentsTitle = "其他组件";

    public static string OtherComponentsZh()
        => "SharpCompress（MIT，压缩包读取）、CommunityToolkit.Mvvm（MIT，MVVM 基元）、FFmpeg.AutoGen（MIT，FFmpeg 绑定）、LibVLCSharp（LGPL 2.1+，LibVLC 托管绑定）。";

    public const string CacheSectionTitle = "缩略图缓存";

    public static string CacheDescriptionZh()
        => "缩略图会按无损 PNG 存到本地，重开目录直接读取，不必重新解码原图或重新抽帧（无损是刻意的：不拿画质换容量）。超过上限时自动淘汰最久未使用的条目。";

    /// <summary>
    /// One-line cache status: entry count, size on disk, budget and share used.
    /// </summary>
    public static string CacheSummary(ThumbnailCacheStats stats)
    {
        var percent = (int)Math.Round(stats.BudgetUsedFraction * 100);
        return $"{stats.EntryCount} 个缩略图 · {MediaDisplay.FormatDataSize(stats.TotalBytes)}" +
               $" / 上限 {MediaDisplay.FormatDataSize(stats.BudgetBytes)}（{percent}%）";
    }
}
