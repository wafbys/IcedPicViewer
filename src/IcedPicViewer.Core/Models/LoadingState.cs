// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Models;

public enum LoadingState
{
    Idle,
    Scanning,
    /// <summary>Page fill / thumbnail work in progress (reserved; shells may use Completed after scan).</summary>
    LoadingItems,
    Error,
    Completed
}
