// Copyright (c) IcedPicViewer. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace IcedPicViewer.Avalonia.Services;

/// <summary>
/// Minimal modal OK/Cancel dialog without extra packages.
/// </summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var alertOnly = title.Contains("无法", StringComparison.Ordinal);

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
            MaxWidth = 420,
        };

        var ok = new Button
        {
            Content = alertOnly ? "知道了" : "确定",
            Padding = new Thickness(16, 8),
            Margin = new Thickness(4),
        };
        var cancel = new Button
        {
            Content = "取消",
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
