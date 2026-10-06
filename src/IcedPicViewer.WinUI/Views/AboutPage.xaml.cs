// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Core.Text;
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

        // Full build label: version, configuration and commit. The window title
        // carries the shorter `DisplayVersion` form; this page is where you go
        // to identify an exact binary, so it includes the commit.
        VersionTextBlock.Text = $"版本：{BuildInfo.FullLabel}";
        IntroTextBlock.Text = AboutCopy.WinUiIntro();
        FfmpegDescTextBlock.Text = AboutCopy.FfmpegDescriptionZh();

        CacheDescTextBlock.Text = AboutCopy.CacheDescriptionZh();
        ClearCacheButton.Content = UiCopy.ClearCache;

        Loaded += OnLoaded;
    }

    private ThumbnailCacheStats _cacheStats;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshCacheStatsAsync();
    }

    /// <summary>
    /// Reads the cache snapshot on a worker thread (GetStats walks the whole
    /// shard tree) and renders it. Never throws: a failure shows the
    /// "unavailable" text and leaves the clear button disabled.
    /// </summary>
    private async Task RefreshCacheStatsAsync()
    {
        CacheStatsRing.IsActive = true;
        CacheStatsRing.Visibility = Visibility.Visible;
        try
        {
            _cacheStats = await Task.Run(App.GetService<IThumbnailDiskCache>().GetStats);
            CacheStatsTextBlock.Text = AboutCopy.CacheSummary(_cacheStats);
            ClearCacheButton.IsEnabled = _cacheStats.EntryCount > 0;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"AboutPage.RefreshCacheStatsAsync: {ex.GetType().Name}: {ex.Message}");
            CacheStatsTextBlock.Text = UiCopy.CacheStatsUnavailable;
        }
        finally
        {
            CacheStatsRing.IsActive = false;
            CacheStatsRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Clears the persisted thumbnail cache after an explicit confirmation that
    /// says how much is about to go. The gallery's own screen contents are
    /// untouched (they live in the in-memory cache / the visible items); only
    /// the on-disk copy goes, so the next open of a folder regenerates it.
    /// </summary>
    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        try
        {
            var confirmed = await App.GetService<IDialogService>().ShowConfirmAsync(
                UiCopy.ClearCacheTitle,
                UiCopy.ClearCacheConfirm(_cacheStats.EntryCount, _cacheStats.TotalBytes),
                primaryButtonText: UiCopy.ClearCache,
                closeButtonText: UiCopy.Cancel,
                defaultIsPrimary: false);

            if (confirmed)
            {
                var freed = await Task.Run(App.GetService<IThumbnailDiskCache>().Clear);
                Trace.TraceInformation(
                    $"AboutPage: cleared thumbnail cache, freed {freed / (1024.0 * 1024):F1} MB");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AboutPage.ClearCacheButton_Click: {ex.GetType().Name}: {ex.Message}");
        }

        // Re-read the truth either way: it also restores the button's state.
        await RefreshCacheStatsAsync();
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
    ///
    /// The LGPL text ships in the package under License\, so it resolves via
    /// <c>ms-appx:///</c>. If that lookup fails, fall back to the loose file
    /// next to the exe rather than leaving the link dead.
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
            Trace.TraceWarning(
                $"AboutPage.LicenseLink_Click: ms-appx lookup failed ({ex.GetType().Name}: {ex.Message}); " +
                "falling back to the loose License\\ffmpeg-LGPL.txt next to the exe.");

            try
            {
                var loose = Path.Combine(AppContext.BaseDirectory, "License", "ffmpeg-LGPL.txt");
                if (!File.Exists(loose))
                {
                    Trace.TraceError($"AboutPage.LicenseLink_Click: license not found at {loose}");
                    return;
                }

                var looseFile = await StorageFile.GetFileFromPathAsync(loose);
                await Launcher.LaunchFileAsync(looseFile);
            }
            catch (Exception fallbackEx)
            {
                Trace.TraceError(
                    $"AboutPage.LicenseLink_Click fallback failed: {fallbackEx.GetType().Name}: {fallbackEx.Message}");
            }
        }
    }
}
