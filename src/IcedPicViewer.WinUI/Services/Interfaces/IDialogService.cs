// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Service for showing modal <c>ContentDialog</c>s. Wrapping <c>XamlRoot</c>
/// resolution and the <see cref="Microsoft.UI.Xaml.Controls.ContentDialog"/>
/// construction here keeps view models free of <c>Microsoft.UI.Xaml</c>
/// dependencies — same pattern as <see cref="IFolderPickerService"/>.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a single-button info dialog. Returns when the user dismisses it.
    /// Safe to call when no main window is available (no-op, returns immediately).
    /// </summary>
    Task ShowInfoAsync(
        string title,
        string content,
        string closeButtonText = "OK");

    /// <summary>
    /// Shows a two-button confirmation dialog. Returns <c>true</c> when the
    /// user clicks the primary button; <c>false</c> on close/cancel.
    /// </summary>
    Task<bool> ShowConfirmAsync(
        string title,
        string content,
        string primaryButtonText,
        string closeButtonText = "Cancel",
        bool defaultIsPrimary = false);
}
