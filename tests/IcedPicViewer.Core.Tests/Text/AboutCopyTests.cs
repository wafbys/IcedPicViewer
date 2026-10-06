// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;
using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Core.Tests.Text;

/// <summary>
/// The About page's cache line is the only place the user sees the cache's
/// footprint, so its wording/units are pinned here.
/// </summary>
public sealed class AboutCopyTests
{
    [Fact]
    public void CacheSummary_ReportsCountSizeBudgetAndShare()
    {
        var stats = new ThumbnailCacheStats(
            EntryCount: 3915,
            TotalBytes: 745L * 1024 * 1024,
            BudgetBytes: 4L * 1024 * 1024 * 1024);

        Assert.Equal("3915 个缩略图 · 745.0 MB / 上限 4.0 GB（18%）", AboutCopy.CacheSummary(stats));
    }

    [Fact]
    public void CacheSummary_EmptyCache()
        => Assert.Equal(
            "0 个缩略图 · 0 B / 上限 1.0 GB（0%）",
            AboutCopy.CacheSummary(new ThumbnailCacheStats(0, 0, 1L * 1024 * 1024 * 1024)));

    [Fact]
    public void CacheSummary_UnknownBudget_DoesNotDivideByZero()
        => Assert.Equal(
            "3 个缩略图 · 2.0 KB / 上限 0 B（0%）",
            AboutCopy.CacheSummary(new ThumbnailCacheStats(3, 2048, 0)));

    [Fact]
    public void BudgetUsedFraction_ClampsToRange()
    {
        var budget = 1000L;
        Assert.Equal(0.5, new ThumbnailCacheStats(1, 500, budget).BudgetUsedFraction);
        Assert.Equal(1.0, new ThumbnailCacheStats(1, 5000, budget).BudgetUsedFraction);
        Assert.Equal(0.0, new ThumbnailCacheStats(1, 500, 0).BudgetUsedFraction);
    }

    [Fact]
    public void ClearCacheConfirm_NamesCountSizeAndReassuresAboutOriginals()
    {
        var text = UiCopy.ClearCacheConfirm(entryCount: 2449, totalBytes: 261L * 1024 * 1024);

        Assert.Contains("2449", text);
        Assert.Contains("261.0 MB", text);
        Assert.Contains("原始文件不受影响", text);
    }
}
