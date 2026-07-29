// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Models;

/// <summary>
/// Shared gallery load phase for WinUI and Avalonia. Shells use the same names;
/// not every value is used on every path (e.g. drain may stay under Scanning).
/// </summary>
public enum LoadingState
{
    Idle,
    Scanning,
    Error,
    Completed
}
