using Microsoft.UI.Xaml.Controls;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Provides abstraction for frame-based navigation.
/// </summary>
public interface INavigationService
{
    bool CanGoBack { get; }

    void NavigateTo<TPage>() where TPage : Page;

    void GoBack();

    /// <summary>
    /// Initializes the navigation service with the root frame.
    /// Should be called once from the main window after the frame is created.
    /// </summary>
    void Initialize(Frame frame);
}
