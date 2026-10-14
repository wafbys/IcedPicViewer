// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Mirrors the persisted recent-folders list into the shell jump list
/// (taskbar right-click menu on the app button).
/// </summary>
public interface IJumpListService
{
    /// <summary>
    /// Rewrites the jump list: an "open folder" task plus up to five recent
    /// folder items. Fire-and-forget safe — failures are traced, never thrown.
    /// </summary>
    Task UpdateRecentFoldersAsync(IReadOnlyList<string> folders);
}
