// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// OS shell integration: reveal in file manager, move to trash.
/// Platform shells (Avalonia / WinUI) provide concrete implementations.
/// </summary>
public interface IShellService
{
    /// <summary>
    /// Opens the file manager and selects <paramref name="path"/> when possible.
    /// For archives, pass the archive file path (not the entry key).
    /// </summary>
    void RevealInFolder(string path);

    /// <summary>
    /// True when the path looks like a UNC / network location where trash
    /// may not be available (caller should confirm before permanent delete).
    /// </summary>
    bool IsNetworkPath(string path);

    /// <summary>
    /// Moves <paramref name="path"/> to the platform trash/recycle bin when
    /// supported; otherwise permanently deletes. Returns false on failure
    /// and sets <paramref name="error"/>.
    /// </summary>
    bool TryDelete(string path, bool preferTrash, out string? errorMessage);
}
