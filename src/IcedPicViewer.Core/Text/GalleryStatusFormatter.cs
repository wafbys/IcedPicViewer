// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using IcedPicViewer.Core.Media;

namespace IcedPicViewer.Core.Text;

/// <summary>
/// Shared Chinese status-bar copy for the gallery.
/// The shell only supplies counts / paths / names.
/// </summary>
public static class GalleryStatusFormatter
{
    public const string IdleDefault = "打开文件夹开始浏览";

    /// <summary>e.g. "12 张图片" or "10 张图片 · 2 个视频".</summary>
    public static string FormatItemBreakdown(int imageCount, int videoCount)
    {
        if (videoCount > 0)
            return $"{imageCount} 张图片 · {videoCount} 个视频";
        return $"{imageCount} 张图片";
    }

    public static string FormatScanningStarted()
        => "扫描中…";

    public static string FormatScanning(
        int discoveredCount,
        string itemBreakdown,
        string? currentPath = null,
        int scanErrorCount = 0,
        string? firstSkippedFileName = null,
        string? firstSkippedReason = null,
        string? queryLabel = null)
    {
        var err = FormatErrorSuffix(scanErrorCount, firstSkippedFileName, firstSkippedReason);
        var prefix = queryLabel is null ? "" : $"筛选 {queryLabel} · ";
        if (!string.IsNullOrEmpty(currentPath))
            return $"{prefix}扫描中：{currentPath}（已发现 {discoveredCount}）{err}";
        return $"{prefix}扫描中… 已发现 {discoveredCount}，显示 {itemBreakdown}{err}";
    }

    public static string FormatGallery(
        string itemBreakdown,
        int discoveredCount,
        int remainingCount,
        int scanErrorCount = 0,
        string? firstSkippedFileName = null,
        string? firstSkippedReason = null,
        string? queryLabel = null,
        int? matchedCount = null)
    {
        var err = FormatErrorSuffix(scanErrorCount, firstSkippedFileName, firstSkippedReason);
        if (queryLabel is not null)
        {
            var matched = matchedCount ?? (discoveredCount - remainingCount);
            return $"筛选 {queryLabel}：显示 {itemBreakdown} / 匹配 {matched}（共 {discoveredCount}，还可加载 {remainingCount}）{err}";
        }
        if (remainingCount > 0)
            return $"显示 {itemBreakdown} / {discoveredCount}（还可加载 {remainingCount}）{err}";
        return $"已加载 {itemBreakdown}{err}";
    }

    /// <summary>
    /// Human label for the active filter/search, or null when the view shows
    /// everything. e.g. <c>图片 · 查找 "*.jpg"</c>.
    /// </summary>
    public static string? FormatQueryLabel(MediaFilter filter, string? search)
    {
        var parts = new List<string>(2);
        if (filter == MediaFilter.Image) parts.Add("图片");
        else if (filter == MediaFilter.Video) parts.Add("视频");
        if (!string.IsNullOrWhiteSpace(search)) parts.Add($"查找 \"{search.Trim()}\"");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public static string FormatLoadingMore(int loadedCount, int discoveredCount)
        => $"加载中 {loadedCount} / {discoveredCount}…";

    /// <summary>
    /// Status-bar segment for the background thumbnail backfill — the pass that
    /// pre-generates thumbnails for items the user has not scrolled to yet, so
    /// they are already on disk when they get there. Empty while it is not
    /// running (including after it finishes).
    /// </summary>
    public static string FormatBackfillProgress(int completed, int total)
    {
        if (total <= 0) return "";
        var clamped = Math.Clamp(completed, 0, total);
        var percent = (int)Math.Round(clamped * 100.0 / total);
        return $"后台生成缩略图 {clamped}/{total}（{percent}%）";
    }

    /// <summary>
    /// Appends <see cref="FormatBackfillProgress"/> to a status line, so the
    /// backfill shows up alongside whatever the gallery is already saying
    /// (scanning, loaded counts, filters).
    /// </summary>
    public static string AppendBackfill(string status, int completed, int total)
    {
        var progress = FormatBackfillProgress(completed, total);
        return progress.Length == 0 ? status : $"{status} · {progress}";
    }

    public static string FormatError(string message)
        => $"错误：{message}";

    public static string FormatCancelled()
        => "已取消";

    public static string FormatFolderPickerUnavailable()
        => "文件夹选择器未就绪";

    public static string FormatWatchUnavailable(string message)
        => $"目录监视不可用：{message}";

    public static string FormatDeleteFailed(string error)
        => $"删除失败：{error}";

    public static string FormatDeleted(string name, bool movedToTrash)
        => movedToTrash ? $"已移至回收站：{name}" : $"已删除：{name}";

    public static string FormatArchiveDeleteNotSupported()
        => "无法删除：不支持删除压缩包内媒体";

    public static string FormatSlideshowActive(double intervalSeconds, bool looping, bool shuffling)
    {
        var text = $"幻灯片 每 {intervalSeconds:0.#} 秒";
        if (looping) text += " · 循环";
        if (shuffling) text += " · 随机";
        return text;
    }

    public static string FormatSlideshowFinished()
        => "幻灯片结束";

    public static string FormatVideoLoadFailed()
        => "视频加载失败（编解码 / 路径）";

    public static string FormatVideoError(string message)
        => $"视频错误：{message}";

    public static string FormatErrorSuffix(
        int scanErrorCount,
        string? firstSkippedFileName = null,
        string? firstSkippedReason = null)
    {
        if (scanErrorCount <= 0)
            return "";

        if (scanErrorCount == 1 && !string.IsNullOrEmpty(firstSkippedFileName))
        {
            return string.IsNullOrEmpty(firstSkippedReason)
                ? $" — 跳过 1 个文件（{firstSkippedFileName}）"
                : $" — 跳过 1 个文件（{firstSkippedFileName}：{firstSkippedReason}）";
        }

        if (!string.IsNullOrEmpty(firstSkippedFileName))
            return $" — 跳过 {scanErrorCount} 个文件（首个：{firstSkippedFileName}）";

        return $" · {scanErrorCount} 个扫描错误";
    }
}
