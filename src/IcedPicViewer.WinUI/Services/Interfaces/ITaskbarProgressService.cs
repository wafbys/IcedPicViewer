// Copyright (c) IcedPicViewer. All rights reserved.

using System;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Taskbar (shell) progress on the window's taskbar button. Best-effort:
/// when the shell integration is unavailable every call is a no-op.
/// </summary>
public interface ITaskbarProgressService
{
    /// <summary>Attach to the main window's HWND (call once at startup).</summary>
    void Attach(IntPtr windowHandle);

    /// <summary>Endless "busy" animation — used while a folder scan runs.</summary>
    void SetIndeterminate();

    /// <summary>Remove the progress indicator.</summary>
    void Clear();
}
