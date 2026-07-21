// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core;

/// <summary>
/// Marker for the platform-agnostic core assembly.
/// Domain models, application services, and (later) ViewModels live here.
/// Must not reference WinUI, Avalonia, or other UI frameworks.
/// </summary>
public static class CoreInfo
{
    public const string AssemblyName = "IcedPicViewer.Core";
}
