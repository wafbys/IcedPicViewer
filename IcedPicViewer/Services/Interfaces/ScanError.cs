// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Reported by <see cref="IDirectoryScanner.ScanAsync"/> when an entry on
/// disk was *recognised* as a candidate (e.g. by extension or magic bytes)
/// but could not actually be read. The gallery skips these silently; the VM
/// surfaces a count + first-failure in the status bar so the user can
/// identify the offending file.
/// </summary>
/// <param name="Path">Absolute path of the file that could not be read.</param>
/// <param name="Reason">Short human-readable cause, e.g. "unsupported archive format",
/// "locked", "directory access denied".</param>
public sealed record ScanError(string Path, string Reason);
