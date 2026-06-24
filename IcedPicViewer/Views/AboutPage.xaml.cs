// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.Diagnostics;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace IcedPicViewer.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        this.InitializeComponent();

        // Surface the build's commit hash as the version string. Same
        // string MainWindow puts in its title bar, so the user can
        // confirm the binary they're looking at is the one they expect.
        VersionTextBlock.Text = $"Version: {BuildInfo.CommitShort}";
    }

    /// <summary>
    /// Back to the gallery. Uses the shared INavigationService so the
    /// navigation history is consistent with the rest of the app
    /// (the WH_KEYBOARD hook in MainWindow also depends on Frame
    /// state being correct).
    /// </summary>
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigationService = App.GetService<INavigationService>();
        navigationService.GoBack();
    }

    /// <summary>
    /// Open the bundled LGPL 2.1 license text in the user's default
    /// text editor. We resolve the file via <c>ms-appx:///</c> so the
    /// path is correct regardless of where the AppX package was
    /// installed (Program Files\WindowsApps\<hash>\ on most machines,
    /// but MSIX can install to other locations per machine policy).
    /// </summary>
    private async void LicenseLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // StorageFile.GetFileFromApplicationUriAsync handles the
            // ms-appx scheme lookup and returns a StorageFile pointing
            // at the actual on-disk path inside the AppX install
            // location. Launcher.LaunchFileAsync then hands it off to
            // the shell, which picks the default app for .txt.
            var file = await StorageFile.GetFileFromApplicationUriAsync(
                new Uri("ms-appx:///License/ffmpeg-LGPL.txt"));
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AboutPage.LicenseLink_Click failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
