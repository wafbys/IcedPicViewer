// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Media;

/// <summary>
/// Snapshot of the persisted thumbnail cache: how many entries it holds, how
/// much disk they occupy, and the budget the cache is allowed to use.
/// </summary>
/// <param name="EntryCount">Number of persisted thumbnails.</param>
/// <param name="TotalBytes">Total size on disk, in bytes.</param>
/// <param name="BudgetBytes">Soft budget for the whole cache, in bytes (0 when unknown).</param>
public readonly record struct ThumbnailCacheStats(int EntryCount, long TotalBytes, long BudgetBytes)
{
    /// <summary>Share of the budget in use, clamped to [0, 1] (0 when the budget is unknown).</summary>
    public double BudgetUsedFraction =>
        BudgetBytes <= 0 ? 0 : Math.Clamp((double)TotalBytes / BudgetBytes, 0, 1);
}
