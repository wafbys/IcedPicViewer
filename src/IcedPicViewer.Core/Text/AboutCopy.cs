// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;

namespace IcedPicViewer.Core.Text;

/// <summary>Shared About dialog / page copy (Chinese).</summary>
public static class AboutCopy
{
    public const string Title = "关于 IcedPicViewer";

    public static string WinUiIntro()
        => "一个基于 WinUI 3 + Windows App SDK 的图片 / 视频查看器。本地优先、纯查看器，不上传、不联网。";

    public static string FfmpegDescriptionZh()
        => "版本 8.1 (BtbN 构建，LGPL 2.1+ shared)。用于读取视频元数据 (分辨率、时长、音轨) 和首帧缩略图。";

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
