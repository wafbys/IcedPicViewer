// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IcedPicViewer;

public partial class App : Application
{
    private static IServiceProvider? _services;
    private Window? _window;

    /// <summary>
    /// Strongly-typed accessor for the singleton <see cref="MainWindow"/>.
    /// Pages call this from the view layer to toggle fullscreen and
    /// otherwise reach window-level state without going through
    /// Application.Current casts on every call site.
    /// </summary>
    public static MainWindow? MainWindow => (MainWindow?)((App)Current)._window;

    public App()
    {
        // Microsoft official guidance: rely on the auto-generated
        // WindowsAppSDK bootstrap initializer (driven by csproj's
        // WindowsAppSdkBootstrapInitialize=true). It pins the runtime to
        // Microsoft.WindowsAppSDK 2.2.0 and the matching WindowsAppRuntime
        // 2.2 package on the target machine. If the runtime is missing or
        // the wrong version, the bootstrap throws and the process aborts
        // BEFORE App() runs — we can't intercept that from here, but the
        // user gets a system-level error pointing at the package.
        // https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-run-time
        //
        // We also subscribe to UnhandledException so we can show a friendly
        // dialog with the runtime installer URL if XAML init still fails
        // (e.g. on Insider builds where the runtime's OS build check trips).
        UnhandledException += OnUnhandledException;

        InitializeComponent();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Last-resort friendly error: catches anything that escapes the
        // normal error handling — including the OS build check fail-fast
        // (which WinUI surfaces as a CLR COMException during Application.Start).
        try
        {
            var caption = "IcedPicViewer failed to start";
            var message =
                "IcedPicViewer requires the Windows App Runtime 2.2 standalone installer.\n\n" +
                "Download and run:\n" +
                "https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe\n\n" +
                "Then re-launch this app.\n\n" +
                "If the issue persists, your Windows build may not be supported.\n\n" +
                "Details: " + e.Message;
            _ = MessageBoxW(IntPtr.Zero, message, caption,
                0x00000010 /* MB_ICONERROR */ | 0x00040000 /* MB_SETFOREGROUND */);
        }
        catch
        {
            Trace.TraceError($"Unhandled exception: {e.Exception}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IDirectoryScanner, DirectoryScanner>();
        services.AddSingleton<IImageLoader, ImageLoader>();

        // Shared thumbnail LRU. Consumed by both IImageLoader (BitmapDecoder
        // thumbs) and IVideoMetadataService (FFmpeg first-frame thumbs) so
        // the 200-cap budget covers both kinds and a path can't be cached
        // twice under different shapes. Capacity tuning lives in
        // ThumbnailCache.
        services.AddSingleton<IThumbnailCache, ThumbnailCache>();

        // Video metadata + thumbnail extraction (FFmpeg-backed).
        // Singleton — service holds no per-request state, and the
        // constructor fires the FFmpeg warm-up task (see
        // VideoMetadataService doc comment for why).
        services.AddSingleton<IVideoMetadataService, VideoMetadataService>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        // Folder picker (modern Windows App SDK picker)
        services.AddTransient<IFolderPickerService, FolderPickerService>();

        // Modal dialogs (ContentDialog). Singleton — stateless, just
        // wraps XamlRoot resolution and ContentDialog construction so VMs
        // don't take a dependency on Microsoft.UI.Xaml.
        services.AddSingleton<IDialogService, DialogService>();

        // ViewModels - appropriate lifetimes
        // GalleryViewModel: Singleton (owns current gallery + file watcher + shared collection)
        // ImageViewModel: Singleton (instance prepared in GalleryView with ShowImageAsync
        //   is the same one received in ImageViewerView; this restores single image mode
        //   functionality while we keep the masonry visual).
        services.AddSingleton<GalleryViewModel>();
        services.AddSingleton<ImageViewModel>();
    }

    public static T GetService<T>() where T : class
    {
        if (_services == null)
            throw new InvalidOperationException("Services not configured");
        return _services.GetRequiredService<T>();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Activate();

        // Trigger FFmpeg native warm-up at app startup. The first call
        // into the FFmpeg native DLLs costs ~6.5 s (LoadLibrary +
        // AutoGen wrapper JIT); doing it on the UI thread when the
        // user first opens a video would freeze the window for that
        // whole window. The service ctor schedules a fire-and-forget
        // Task.Run that absorbs the cost on a worker thread during
        // startup. By the time the user navigates to a folder with
        // videos, the native side is already warm.
        //
        // We resolve the service (rather than just `new`-ing it) so
        // DI also gets a chance to wire its dependencies; the resolved
        // instance is otherwise unused here.
        _ = GetService<IVideoMetadataService>();

        // Probe the installed WIC codecs and warn if HEIC / AVIF are
        // not registered. IcedPicViewer's SupportedExtensions list
        // includes both formats, but BitmapDecoder.CreateAsync
        // throws if no WIC codec handles the file. Listing the
        // decoders up front and writing a Trace warning means the
        // user has a single line in DebugView / Event Log to grep
        // for when "why don't my .heic files show thumbnails?" comes
        // up, instead of the more confusing "BitmapDecoder.CreateAsync
        // threw" stack trace they get when the decode fails.
        ProbeAndWarnMissingCodecs();

        // FFmpeg probe (development-only). Runs only when:
//   - IPV_FFMPEG_PROBE=1 env var (does NOT propagate through MSIX
//     `winapp.exe launch` — see FFmpegProbeService doc comment), OR
//   - %LOCALAPPDATA%\IcedPicViewer\ffmpeg-probe.flag file exists, OR
//   - FFmpegProbeService.ForceRunForDiagnostic is flipped at compile time.
// By default the app boots without touching FFmpeg, so users who don't
// enable the probe see no behavior change. Results go to
// %LOCALAPPDATA%\IcedPicViewer\ffmpeg-probe.log. See FFmpegProbeService.
// Fire-and-forget — OnLaunched is void and the probe's RunAsync does
// its own Task.Run to keep decode off the UI thread.
if (FFmpegProbeService.IsProbeRequested || File.Exists(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer",
            "ffmpeg-probe.flag")))
{
    _ = new FFmpegProbeService().RunAsync();
}
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Dispose DI-owned singletons that hold OS resources (file watcher, cts, semaphore, cache).
        // IServiceProvider doesn't auto-dispose singletons, so we do it explicitly here.
        if (_services is null) return;
        (_services.GetService<GalleryViewModel>() as IDisposable)?.Dispose();
        (_services.GetService<ImageViewModel>() as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Enumerate the WIC codecs installed on the host and warn if a
    /// format in <see cref="IImageLoader.SupportedExtensions"/> is
    /// not actually decodable. The most common case is .heic /
    /// .avif on a stock Windows install — both ship in the MS Store
    /// ("HEIF Image Extension" and "AV1 Video Extension") but
    /// neither is installed by default. Without this check, the
    /// user gets a generic "LoadThumbnailAsync error: ..." line in
    /// the log per HEIC file in their folder; with the check, the
    /// log has a single Trace.Warning at startup naming the missing
    /// format and pointing at the MS Store link to install.
    /// </summary>
    private static void ProbeAndWarnMissingCodecs()
    {
        try
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var codec in Windows.Graphics.Imaging.BitmapDecoder.GetDecoderInformationEnumerator())
            {
                // FileExtensions is already a parsed list (the WIC
                // metadata is split into individual entries like
                // [".heic", ".heif", ".hif"]), so we just collect
                // the lowercased entries directly.
                foreach (var ext in codec.FileExtensions)
                {
                    if (string.IsNullOrEmpty(ext)) continue;
                    extensions.Add(ext.StartsWith('.') ? ext : "." + ext);
                }
            }
            if (!extensions.Contains(".heic"))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "App: HEIC decoder not available — install 'HEIF Image Extension' from Microsoft Store "
                    + "(https://apps.microsoft.com/) to view .heic files.");
            }
            if (!extensions.Contains(".avif"))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "App: AVIF decoder not available — install 'AV1 Video Extension' from Microsoft Store "
                    + "(https://apps.microsoft.com/) to view .avif files.");
            }
        }
        catch (Exception ex)
        {
            // Codec probe is best-effort. If the enumerator itself
            // fails (very old Windows, weird WIC install), the
            // gallery's per-file error path still kicks in and
            // surfaces a status bar message — we just don't get the
            // convenient "here's the MS Store link" hint.
            System.Diagnostics.Trace.TraceWarning($"App: codec probe failed: {ex.Message}");
        }
    }
}
