// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using IcedPicViewer.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Media.Playback;

namespace IcedPicViewer;

public sealed partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    private const string SettingsFile = "window_settings.txt";
    private const string AppDataFolder = "IcedPicViewer";

    private Windows.Graphics.RectInt32 _lastWindowedBounds;

    private static string GetSettingsPath()
    {
        // Unpackaged WinUI apps should not write next to the exe (Program Files may be
        // read-only or flagged by AV). Use %LOCALAPPDATA%\<AppName>\ instead.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, SettingsFile);
    }

    /// <summary>
    /// True when the window is currently in fullscreen mode
    /// (no title bar, no window chrome, fills the screen). Bound by
    /// the viewer's fullscreen button glyph/label/tooltip so the
    /// toggle shows the right state at all times — not just after
    /// the user clicks the button, but also when F11 toggles it
    /// from outside the view or after the OS reverts on focus loss.
    /// </summary>
    public bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>
    /// Toggle between normal (overlapped) and fullscreen presentation.
    /// Uses the WinUI 3 <see cref="Microsoft.UI.Windowing.AppWindow.SetPresenter"/>
    /// API rather than the UWP <c>ApplicationView.TryEnterFullScreenMode</c>
    /// pattern — the latter doesn't exist in desktop WinUI 3 and
    /// <c>SetPresenter</c> is the documented replacement. Switching
    /// presenters is a one-frame operation; the system remembers
    /// the prior overlapped size / position so toggling back restores
    /// the same window geometry.
    /// </summary>
    public void ToggleFullscreen()
    {
        var prevIsFullscreen = IsFullscreen;
        if (IsFullscreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        else
        {
            // Capture current bounds before entering fullscreen so we can
            // restore them later (both on toggle back and on next launch).
            _lastWindowedBounds = new Windows.Graphics.RectInt32(
                AppWindow.Position.X, AppWindow.Position.Y,
                AppWindow.Size.Width, AppWindow.Size.Height);
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        OnPropertyChanged(nameof(IsFullscreen));
        OnPropertyChanged(nameof(IsAppTitleBarVisible));
        // Defer a second notification by one dispatcher tick. Symptom
        // we are hunting: in MSIX packaged mode on Win 11 25H2 we
        // observed that AppWindow.SetPresenter returns synchronously
        // but AppWindow.Presenter.Kind doesn't actually flip until
        // the next dispatcher round-trip — so when subscribers
        // (GalleryView / ViewerView) read MainWindow.IsFullscreen
        // inside their OnMainWindowPropertyChanged handler, they see
        // the OLD value, fall into the wrong branch of the if/else,
        // and the chrome fails to collapse. Re-raising the same
        // notification on the next tick catches the eventual value.
        // Cheap (one extra OnPropertyChanged) and idempotent if the
        // platform behaviour changes in the future.
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(IsFullscreen));
            OnPropertyChanged(nameof(IsAppTitleBarVisible));
        });
    }

    /// <summary>
    /// True when the in-app <see cref="Microsoft.UI.Xaml.Controls.TitleBar"/>
    /// element should be visible. Visible in windowed mode (so the
    /// user gets the custom title bar content with icon + commit
    /// hash); collapsed in fullscreen (where the OS title bar is
    /// already hidden by the FullScreen presenter and the in-app
    /// TitleBar would only waste vertical space).
    /// </summary>
    public bool IsAppTitleBarVisible => !IsFullscreen;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    public MainWindow()
    {
        // Self-register with App so pages navigated inside this ctor
        // (RootFrame.Navigate → GalleryView ctor at line ~145) can see
        // App.MainWindow as a non-null reference. Without this the
        // App.OnLaunched assignment `_window = new MainWindow()` lands
        // AFTER this ctor body finishes — by then any pages that ran
        // inside have already evaluated `App.MainWindow is null` and
        // skipped their PropertyChanged subscription. Symptom: every
        // fullscreen toggle (F11 / Esc / button click) raises the
        // event but no subscriber exists, chrome stays as-is forever.
        // See App.SetMainWindow doc comment for the full chronology.
        if (Application.Current is App app)
        {
            app.SetMainWindow(this);
        }

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Show the build's commit hash in the title so the running version is
        // unambiguous (matters because dotnet run launches under a debug
        // package identity, so the bin path alone doesn't tell you which
        // commit you have on screen). Both AppTitleBar (visual custom title
        // bar) and AppWindow.Title (native window title — used by taskbar
        // hover, Alt+Tab, screen reader) are updated so a11y and OS shells
        // see the version too.
        var titleWithHash = $"IcedPicViewer ({BuildInfo.CommitShort})";
        AppTitleBar.Title = titleWithHash;
        AppWindow.Title = titleWithHash;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        RestoreWindowState();

        // Initialize navigation service with the root frame
        var navigationService = App.GetService<INavigationService>();
        navigationService.Initialize(RootFrame);

        RootFrame.Navigate(typeof(GalleryView));

        AppWindow.Closing += AppWindow_Closing;

        // Thread-scope WH_KEYBOARD hook for ViewerView shortcuts
        // (Left/Right/Delete/Escape). Replaces 6 earlier XAML-layer
        // attempts (AddHandler(KeyDownEvent), KeyboardAccelerator,
        // SetWindowSubclass, …) — all depended on a focused element or
        // on registering the hook on a HWND that the OS actually
        // routes keyboard input to, both of which are unreliable in
        // MSIX packaged mode on Win11 25H2. WH_KEYBOARD thread scope
        // sidesteps both: it hooks the UI thread's message queue
        // directly, so every keystroke reaches us regardless of focus
        // and HWND. WH_KEYBOARD_LL would need an injected DLL that the
        // MSIX sandbox generally blocks, so this is the right tool.
        //
        // See AGENTS.md "键盘导航实现" for the full failed-attempts
        // chronology. The DispatcherQueue.TryEnqueue is a belt-and-
        // suspenders: the ctor IS on the UI thread in practice, but
        // the dispatcher round-trip removes a class of "ctor ran on
        // the wrong thread" bugs we already hit.
        DispatcherQueue.GetForCurrentThread().TryEnqueue(InstallKeyboardHook);
    }

    private bool _hookInstalled;

    private void InstallKeyboardHook()
    {
        if (_hookInstalled) return;
        _hookProc = KeyboardHookProc;
        var threadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD, _hookProc, IntPtr.Zero, threadId);
        if (_hookHandle == IntPtr.Zero)
        {
            var gle = Marshal.GetLastWin32Error();
            // Hook install failed — keyboard navigation will be broken.
            Trace.TraceError($"InstallKeyboardHook: SetWindowsHookEx(WH_KEYBOARD) failed, Win32 error {gle}");
        }
        else
        {
            _hookInstalled = true;
        }
    }

    // ---- WH_KEYBOARD thread-scope hook ----

    private delegate IntPtr HOOKPROC(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HOOKPROC lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private HOOKPROC? _hookProc;
    private IntPtr _hookHandle;

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // WH_KEYBOARD hook invariants (each one empirically bit us while
        // iterating on the keyboard nav fix — see AGENTS.md for chronology):
        //   - nCode < 0 → pass through to CallNextHookEx with no work
        //   - Callback must return fast (slow callbacks degrade input latency)
        //   - Exceptions MUST NOT escape (unhandled throw in a hook is undefined;
        //     on Win11 25H2 + MSIX it silently crashes the process)
        //   - (int)IntPtr is unchecked truncate — never throws. IntPtr.ToInt32()
        //     does throw on .NET 6+ when value is out of int range (observed on
        //     Win11 25H2 + MSIX where wParam/lParam have garbage in the high
        //     32 bits). The C# `unchecked` keyword does NOT change BCL method
        //     behavior — only the explicit `(int)IntPtr` cast is safe here.
        // The body is try/catch-wrapped and HandleViewerKey is hopped via
        // DispatcherQueue.TryEnqueue (synchronous on the UI thread) so the VM's
        // await chain starts AFTER the hook returns, not before.
        try
        {
            if (nCode >= 0)
            {
                unchecked
                {
                    int wParam32 = (int)wParam;
                    int lParam32 = (int)lParam;
                    var vk = (Windows.System.VirtualKey)wParam32;
                    bool isKeyUp = (lParam32 & unchecked((int)0x80000000)) != 0;
                    if (!isKeyUp)
                    {
                        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                        {
                            try { HandleViewerKey(vk); }
                            catch { /* swallowed to protect hook */ }
                        });
                    }
                }
            }
        }
        catch
        {
            // Must never let exceptions escape the hook proc.
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void HandleViewerKey(Windows.System.VirtualKey key)
    {
        // F11 is the only key that fires regardless of which page is
        // on screen — it toggles the window-level fullscreen state
        // (AppWindow.Presenter), which is the same operation for the
        // gallery and the viewer. Everything else is page-specific
        // (gallery has no hotkeys) so we route through the page-type
        // check below.
        if (key == Windows.System.VirtualKey.F11)
        {
            // F11 is the conventional "fullscreen" toggle key in
            // most image viewers. Toggling here routes through the
            // same ToggleFullscreen method as the viewer's button,
            // so the presenter kind + the button's IsFullscreen-
            // derived glyph/label/tooltip stay in sync regardless
            // of which input triggered the change.
            ToggleFullscreen();
            return;
        }

        // PageUp / PageDown: scroll the masonry gallery by ~one viewport
        // (not prev/next image — those stay on ← → only).
        if (key is Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown)
        {
            if (RootFrame.Content is GalleryView gallery)
                gallery.ScrollByPage(down: key == Windows.System.VirtualKey.PageDown);
            return;
        }

        // ViewerView binds via x:Bind to a ViewModel field on the code-behind
        // (not through DataContext), so DataContext is null here — read VM from
        // the typed ViewModel property exposed on the page.
        if (RootFrame.Content is not ViewerView viewer || viewer.ViewModel is not ViewerViewModel vm)
            return;

        switch (key)
        {
            case Windows.System.VirtualKey.Left:
                if (vm.NavigatePreviousCommand.CanExecute(null))
                    vm.NavigatePreviousCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Right:
                if (vm.NavigateNextCommand.CanExecute(null))
                    vm.NavigateNextCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Delete:
                if (vm.DeleteCommand.CanExecute(null))
                    vm.DeleteCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Escape:
                vm.CloseCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Space:
                // Start playback, or toggle LibVLC pause (MF uses built-in controls).
                if (vm.IsUsingVlc && vm.IsVideoPlaying)
                    vm.ToggleVlcPlayPause();
                else if (vm.PlayCommand.CanExecute(null))
                    vm.PlayCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Number0:
            case Windows.System.VirtualKey.Number1:
            case Windows.System.VirtualKey.Number2:
            case Windows.System.VirtualKey.Number3:
            case Windows.System.VirtualKey.Number4:
            case Windows.System.VirtualKey.Number5:
            case Windows.System.VirtualKey.Number6:
            case Windows.System.VirtualKey.Number7:
            case Windows.System.VirtualKey.Number8:
            case Windows.System.VirtualKey.Number9:
                // VLC / mpv convention: 1-9 jump to 10%..90% of the
                // current video; 0 jumps to 0% (start). Only fires
                // for video items (IsVideo check inside the helper);
                // pressing a digit while viewing an image is a no-op.
                // Decoupled from Space (play/pause) so the two keys
                // don't fight over the WH_KEYBOARD hook — Space goes
                // through PlayCommand's CanExecute gate, digits are
                // an explicit seek.
                var digit = (int)key - (int)Windows.System.VirtualKey.Number0;
                var percent = digit * 10;
                HandleNumberKeySeek(vm, percent);
                break;
        }
    }

    /// <summary>
    /// Jump the current video to the given percentage of its total
    /// duration (0-100). Mirrors the VLC / mpv convention where
    /// 1-9 are 10%..90% and 0 is the start. No-op for image items
    /// (no position to seek to) and for videos whose duration hasn't
    /// loaded yet (NaturalDuration == Zero, which happens for the
    /// first ~100ms after the source opens). Pauses before seeking
    /// and resumes if the video was playing — seeking on a playing
    /// video can stutter on the native side, and the resume keeps
    /// the user's "I'm watching this, not paused" intent intact.
    /// </summary>
    private static void HandleNumberKeySeek(ViewerViewModel vm, int percent)
    {
        if (!vm.IsVideo) return;
        // Covers Media Foundation + LibVLC (VP8/WebM etc.).
        vm.SeekPlaybackToPercent(percent);
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Uninstall the thread-scope WH_KEYBOARD hook before the window
        // tears down. Leaving it installed risks a dangling HOOKPROC
        // (managed delegate) after the window is gone; UnhookWindowsHookEx
        // is the documented cleanup for SetWindowsHookEx.
        if (_hookHandle != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_hookHandle))
            {
                var gle = Marshal.GetLastWin32Error();
                Trace.TraceError($"AppWindow_Closing: UnhookWindowsHookEx failed, Win32 error {gle}");
            }
            _hookHandle = IntPtr.Zero;
            _hookInstalled = false;
        }

        SaveWindowState();
    }

    private void SaveWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            var tempFile = file + ".tmp";

            bool isFs = IsFullscreen;
            int x = AppWindow.Position.X;
            int y = AppWindow.Position.Y;
            int w = AppWindow.Size.Width;
            int h = AppWindow.Size.Height;

            if (isFs && _lastWindowedBounds.Width > 0 && _lastWindowedBounds.Height > 0)
            {
                x = _lastWindowedBounds.X;
                y = _lastWindowedBounds.Y;
                w = _lastWindowedBounds.Width;
                h = _lastWindowedBounds.Height;
            }

            var content = $"{x},{y},{w},{h},{(isFs ? 1 : 0)}";

            File.WriteAllText(tempFile, content);
            File.Move(tempFile, file, overwrite: true);

            Trace.TraceInformation($"Saved window state: {content}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"SaveWindowState error: {ex}");
        }
    }

    private void RestoreWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            if (!File.Exists(file))
            {
                Trace.TraceWarning("RestoreWindowState: file not found");
                return;
            }

            var content = File.ReadAllText(file);
            var parts = content.Split(',');
            if (parts.Length < 4)
            {
                Trace.TraceWarning($"RestoreWindowState: invalid parts count {parts.Length}");
                return;
            }

            if (!double.TryParse(parts[0], out var x) ||
                !double.TryParse(parts[1], out var y) ||
                !double.TryParse(parts[2], out var width) ||
                !double.TryParse(parts[3], out var height))
            {
                Trace.TraceWarning($"RestoreWindowState: failed to parse values");
                return;
            }

            _lastWindowedBounds = new Windows.Graphics.RectInt32((int)x, (int)y, (int)width, (int)height);
            AppWindow.MoveAndResize(_lastWindowedBounds);

            bool wasFullscreen = parts.Length >= 5 &&
                int.TryParse(parts[4], out var fs) && fs != 0;

            if (wasFullscreen)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }

            Trace.TraceInformation($"Restored window state: {content}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RestoreWindowState error: {ex}");
        }
    }
}
