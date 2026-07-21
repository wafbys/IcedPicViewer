// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace IcedPicViewer.Services.Implementations;

public sealed class DialogService : IDialogService
{
    public async Task ShowInfoAsync(
        string title,
        string content,
        string closeButtonText = "OK")
    {
        var xamlRoot = TryGetXamlRoot();
        if (xamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    public async Task<bool> ShowConfirmAsync(
        string title,
        string content,
        string primaryButtonText,
        string closeButtonText = "Cancel",
        bool defaultIsPrimary = false)
    {
        var xamlRoot = TryGetXamlRoot();
        if (xamlRoot is null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultIsPrimary ? ContentDialogButton.Primary : ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Returns the active window's <c>XamlRoot</c>, or null when the app
    /// hasn't initialised a window yet (unit tests, headless hosts). Mirrors
    /// <see cref="FolderPickerService"/>'s "no window" guard so callers can
    /// safely invoke dialog methods without try/catch.
    /// </summary>
    private static XamlRoot? TryGetXamlRoot()
    {
        try
        {
            return App.MainWindow?.Content?.XamlRoot;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DialogService: failed to resolve XamlRoot: {ex.Message}");
            return null;
        }
    }
}
