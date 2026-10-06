// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Service for letting the user pick a single media file with the modern
/// Windows App SDK picker. Used by the gallery's "打开文件" action, which
/// loads the file's folder and jumps to it in the viewer.
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// Shows a file picker filtered to <paramref name="extensions"/>.
    /// </summary>
    /// <param name="commitButtonText">Commit-button caption.</param>
    /// <param name="extensions">Allowed extensions, e.g. <c>.jpg</c> / <c>.mp4</c>.</param>
    /// <returns>The selected file's full path, or null if cancelled / unavailable.</returns>
    Task<string?> PickFileAsync(string commitButtonText, IReadOnlyList<string> extensions);
}
