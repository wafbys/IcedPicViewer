// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Models;

/// <summary>
/// Shared identity surface for a gallery row.
/// WinUI <c>MediaItem</c> implements this.
/// UI bitmaps stay shell-specific (not on this interface).
/// </summary>
public interface IMediaEntry
{
    /// <summary>Stable identity (usually <see cref="MediaRef.ToString"/>).</summary>
    string Id { get; }

    MediaRef Media { get; }

    string Name { get; }

    bool IsVideo { get; }

    long FileSize { get; }
}
