// Copyright (c) IcedPicViewer. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Avalonia.Services;

/// <summary>
/// Minimal modal OK/Cancel dialog without extra packages.
/// </summary>
public static class ConfirmDialog
{
    /// <param name="alertOnly">
    /// If true, only an acknowledge button (info); otherwise OK/Cancel confirm.
    /// </param>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, bool alertOnly = false)
    {
        var tcs = new TaskCompletionSource<bool>();

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
            MaxWidth = 420,
        };

        var ok = new Button
        {
            Content = alertOnly ? UiCopy.GotIt : UiCopy.Ok,
            Padding = new Thickness(16, 8),
            Margin = new Thickness(4),
        };
        var cancel = new Button
        {
            Content = UiCopy.Cancel,
            Padding = new Thickness(16, 8),
            Margin = new Thickness(4),
            IsVisible = !alertOnly,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(buttons);
        root.Children.Add(messageBlock);

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = root,
        };

        ok.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }
}
