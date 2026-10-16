using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Provides abstraction for frame-based navigation.
/// </summary>
public interface INavigationService
{
    bool CanGoBack { get; }

    /// <summary>
    /// Navigates to <typeparamref name="TPage"/>. Pass a transition to
    /// override the frame's default page transition — the gallery → viewer
    /// hand-off suppresses it so the connected animation is the only
    /// transition (a running page transition steals the animation and its
    /// snapshot then hangs on screen until the service times it out).
    /// </summary>
    void NavigateTo<TPage>(NavigationTransitionInfo? transition = null) where TPage : Page;

    void GoBack();

    /// <summary>
    /// Initializes the navigation service with the root frame.
    /// Should be called once from the main window after the frame is created.
    /// </summary>
    void Initialize(Frame frame);
}
