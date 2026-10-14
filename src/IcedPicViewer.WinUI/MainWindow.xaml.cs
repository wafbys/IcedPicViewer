// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Settings;
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

    private Windows.Graphics.RectInt32 _lastWindowedBounds;

    // Configured in the ctor (minimum size + kept so ToggleFullscreen can
    // restore this exact instance instead of a fresh default presenter).
    private OverlappedPresenter? _overlappedPresenter;

    // Deferred window-geometry state (see RestoreWindowState / OnFirstActivated):
    // the ctor runs before the window is shown, and geometry applied there is
    // overwritten while the presenter is applied.
    private bool _pendingDefaultSize;
    private Windows.Graphics.RectInt32? _pendingGeometry;
    private bool _pendingMaximize;

    private static string GetSettingsPath()
    {
        // Window geometry lives in %LOCALAPPDATA%\IcedPicViewer (AppDataPaths).
        var dir = AppDataPaths.EnsureRoot();
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

    private OverlappedPresenter? Overlapped => AppWindow.Presenter as OverlappedPresenter;

    /// <summary>True while the window is maximized (caption-button maximize, not F11 fullscreen).</summary>
    private bool IsMaximized => Overlapped?.State == OverlappedPresenterState.Maximized;

    private bool IsMinimized => Overlapped?.State == OverlappedPresenterState.Minimized;

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
            // Restore the configured instance (not the bare enum) so the
            // minimum-size constraints survive a fullscreen roundtrip.
            if (_overlappedPresenter is not null)
            {
                AppWindow.SetPresenter(_overlappedPresenter);
            }
            else
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            }
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

    /// <summary>
    /// The gallery view-model (DI singleton). Exposed for x:Bind: the
    /// title-bar search box two-way binds to <c>SearchText</c>, so it
    /// drives the exact same filtering as the old in-header box.
    /// </summary>
    public GalleryViewModel GalleryViewModel { get; }

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

        // The gallery view-model is a DI singleton; the title-bar search
        // binds to the same SearchText the gallery uses (MainWindow.xaml).
        GalleryViewModel = App.GetService<GalleryViewModel>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Put version, build configuration and commit hash in the title so the
        // running binary is unambiguous. This matters because `dotnet run`
        // launches under a debug package identity — the bin path alone does not
        // tell you which configuration or commit is on screen, and Debug vs
        // Release behaves very differently for performance work.
        var title = $"IcedPicViewer {BuildInfo.FullLabel}";
        AppTitleBar.Title = title;
        AppWindow.Title = title;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Taskbar scan progress (best-effort; no-op when the shell
        // integration is unavailable — see TaskbarProgressService).
        App.GetService<IcedPicViewer.Services.Interfaces.ITaskbarProgressService>()
            .Attach(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // Minimum window size. Below ~680 DIP the gallery header cannot
        // fit even in its icon-only compact state (GalleryView.xaml's
        // adaptive states). Configure a dedicated OverlappedPresenter
        // instance and APPLY it: merely setting these properties on the
        // existing default presenter does not make the OS enforce them
        // for user resizing (verified by querying WM_GETMINMAXINFO — the
        // min track size still came back as 0). The instance is kept in a
        // field so ToggleFullscreen can restore it (instance-based
        // SetPresenter preserves the constraint; the kind-based overload
        // would swap in a fresh default presenter).
        _overlappedPresenter = OverlappedPresenter.Create();
        // AppWindow / presenter APIs use PHYSICAL pixels while the
        // header's adaptive breakpoints are DIP, so the floor must be
        // scaled by the window DPI: an unscaled 680 on a 150% display is
        // only 453 DIP and would let the user drag back into the
        // truncated state (verified via WM_GETMINMAXINFO + a simulated
        // edge drag: the min track was enforced, but at 680 phys).
        // XamlRoot is not attached yet in this ctor (reading
        // Content.XamlRoot.RasterizationScale silently fell back to 1.0),
        // so ask Win32 for the DPI directly. The floor is sampled once;
        // moving to a monitor with a different DPI keeps the physical
        // floor (a slightly different DIP floor) — acceptable for a
        // minimum-size guard.
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var rasterizationScale = dpi > 0 ? dpi / 96.0 : 1.0;
        _overlappedPresenter.PreferredMinimumWidth = (int)Math.Round(680 * rasterizationScale);
        _overlappedPresenter.PreferredMinimumHeight = (int)Math.Round(480 * rasterizationScale);
        AppWindow.SetPresenter(_overlappedPresenter);

        RestoreWindowState();
        Activated += OnFirstActivated;

        // Initialize navigation service with the root frame
        var navigationService = App.GetService<INavigationService>();
        navigationService.Initialize(RootFrame);

        // Title-bar back button: shown whenever the frame can go back
        // (the About page; the viewer keeps its own Close button as
        // well). The About page no longer draws its own back bar — Esc
        // also goes back via the keyboard hook (HandleViewerKey).
        AppTitleBar.BackRequested += (_, _) =>
        {
            if (RootFrame.CanGoBack)
            {
                RootFrame.GoBack();
            }
        };
        RootFrame.Navigated += (_, args) =>
        {
            AppTitleBar.IsBackButtonVisible = RootFrame.CanGoBack;
            AppTitleBar.Subtitle = args.SourcePageType == typeof(AboutPage) ? IcedPicViewer.Core.Text.UiCopy.About : "";
            // The title-bar search belongs to the gallery only; the About
            // page and the viewer collapse it (their chrome has no search).
            TitleBarSearchBox.Visibility = args.SourcePageType == typeof(GalleryView)
                ? Visibility.Visible
                : Visibility.Collapsed;
            // The About page is document-style chrome: opening it from the
            // fullscreen gallery would hide the title bar (and with it the
            // only pointer path back), so leave fullscreen first.
            if (args.SourcePageType == typeof(AboutPage) && IsFullscreen)
            {
                ToggleFullscreen();
            }
        };

        RootFrame.Navigate(typeof(GalleryView));

        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;

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
        // See AGENTS.md "键盘导航" sub-section for the full failed-attempts
        // chronology. The DispatcherQueue.TryEnqueue is a belt-and-
        // suspenders: the ctor IS on the UI thread in practice, but
        // the dispatcher round-trip removes a class of "ctor ran on
        // the wrong thread" bugs we already hit.
        //
        // The mouse X buttons (side back/forward) use the same mechanism:
        // a thread-scope WH_MOUSE hook, because XAML surfaces no X-button
        // events and the SetWindowSubclass route already proved unreliable
        // for input. X1=previous / X2=next in the viewer, X1=back on the
        // About page.
        DispatcherQueue.GetForCurrentThread().TryEnqueue(InstallInputHooks);
    }

    private bool _hookInstalled;

    private void InstallInputHooks()
    {
        if (_hookInstalled) return;
        var threadId = GetCurrentThreadId();

        _hookProc = KeyboardHookProc;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD, _hookProc, IntPtr.Zero, threadId);
        if (_hookHandle == IntPtr.Zero)
        {
            var gle = Marshal.GetLastWin32Error();
            // Hook install failed — keyboard navigation will be broken.
            Trace.TraceError($"InstallInputHooks: SetWindowsHookEx(WH_KEYBOARD) failed, Win32 error {gle}");
        }

        _mouseHookProc = MouseHookProc;
        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE, _mouseHookProc, IntPtr.Zero, threadId);
        if (_mouseHookHandle == IntPtr.Zero)
        {
            var gle = Marshal.GetLastWin32Error();
            // Hook install failed — mouse X buttons will be dead; the
            // keyboard hook is independent, so everything else works.
            Trace.TraceError($"InstallInputHooks: SetWindowsHookEx(WH_MOUSE) failed, Win32 error {gle}");
        }

        _hookInstalled = _hookHandle != IntPtr.Zero || _mouseHookHandle != IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- WH_KEYBOARD / WH_MOUSE thread-scope hooks ----

    private delegate IntPtr HOOKPROC(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD = 2;
    private const int WH_MOUSE = 7;

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
    private HOOKPROC? _mouseHookProc;
    private IntPtr _mouseHookHandle;

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

    // ---- WH_MOUSE thread-scope hook (mouse X buttons) ----

    private const int WM_XBUTTONDOWN = 0x020B;
    private const int XBUTTON1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // MOUSEHOOKSTRUCT (the documented lParam payload of WH_MOUSE callbacks;
    // extended by MOUSEHOOKSTRUCTEX whose mouseData field carries the X
    // button for WM_XBUTTON* messages).
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEHOOKSTRUCT
    {
        public POINT pt;
        public IntPtr hwnd;
        public uint wHitTestCode;
        public IntPtr dwExtraInfo;
    }

    private static readonly int MouseHookMouseDataOffset = Marshal.SizeOf<MOUSEHOOKSTRUCT>();

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Same invariants as KeyboardHookProc: fast return, no escaping
        // exceptions, unchecked (int) casts. WH_MOUSE semantics differ from
        // a window procedure: wParam is the message identifier itself, and
        // the payload lives in lParam → MOUSEHOOKSTRUCTEX.mouseData, whose
        // high word is the X button (1=back, 2=forward — the same encoding
        // WM_XBUTTONDOWN uses in a WndProc's wParam). Act on button-down
        // only, mirroring the keyboard hook. Messages without a payload
        // pointer (forged/queued by other processes) are skipped instead of
        // dereferenced.
        try
        {
            if (nCode >= 0)
            {
                unchecked
                {
                    int wParam32 = (int)wParam;
                    if (wParam32 == WM_XBUTTONDOWN)
                    {
                        if (lParam != IntPtr.Zero)
                        {
                            var mouseData = Marshal.ReadInt32(lParam, MouseHookMouseDataOffset);
                            var isBack = ((mouseData >> 16) & 0xFFFF) == XBUTTON1;
                            DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                            {
                                try { HandleMouseXButton(isBack); }
                                catch { /* swallowed to protect hook */ }
                            });
                        }
                        // else: no payload pointer — a forged/queued message
                        // rather than real input; skip it instead of reading.
                    }
                }
            }
        }
        catch
        {
            // Must never let exceptions escape the hook proc.
        }
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Mouse X buttons: X1 ("back") = previous item in the viewer / back on
    /// the About page; X2 ("forward") = next item. The gallery is a root
    /// page with no history, so both are no-ops there. Mirrors the
    /// convention image viewers use (X buttons step through items).
    /// </summary>
    private void HandleMouseXButton(bool isBack)
    {
        if (RootFrame.Content is AboutPage)
        {
            if (isBack && RootFrame.CanGoBack)
            {
                RootFrame.GoBack();
            }
            return;
        }

        if (RootFrame.Content is not ViewerView viewer || viewer.ViewModel is not ViewerViewModel vm)
            return;

        var command = isBack ? vm.NavigatePreviousCommand : vm.NavigateNextCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
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

        // F5: refresh the gallery (the header button's tooltip has
        // advertised "刷新 (F5)" all along — this makes the hint real).
        // The viewer page ignores it; there is nothing to re-scan there.
        if (key == Windows.System.VirtualKey.F5)
        {
            if (RootFrame.Content is GalleryView gallery &&
                gallery.ViewModel.RefreshCommand.CanExecute(null))
            {
                gallery.ViewModel.RefreshCommand.Execute(null);
            }
            return;
        }

        // Esc on the About page goes back. That page no longer draws its
        // own back bar — the title-bar back button is the pointer path
        // and this is the keyboard path; the viewer branch below keeps
        // owning Esc for Close.
        if (key == Windows.System.VirtualKey.Escape &&
            RootFrame.Content is AboutPage)
        {
            if (RootFrame.CanGoBack)
            {
                RootFrame.GoBack();
            }
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

        // Yield keys the focused control owns (slider ←/→, button Space).
        // The hook keeps calling CallNextHookEx, so the focused control
        // also receives the key — without this guard one press did two
        // things (slider stepped AND the image changed; button activated
        // AND playback toggled). See ViewerView.IsKeyOwnedByFocusedControl.
        if (viewer.IsKeyOwnedByFocusedControl(key))
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
            case Windows.System.VirtualKey.Home:
                if (vm.NavigateFirstCommand.CanExecute(null))
                    vm.NavigateFirstCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.End:
                if (vm.NavigateLastCommand.CanExecute(null))
                    vm.NavigateLastCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Delete:
                if (vm.DeleteCommand.CanExecute(null))
                    vm.DeleteCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Escape:
                vm.CloseCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Space:
                // Videos: start playback, or toggle LibVLC pause (MF uses
                // built-in controls). Images: Space is the slideshow's
                // play/pause — start it, or stop it while running (the
                // README has advertised this mapping all along).
                if (vm.IsUsingVlc && vm.IsVideoPlaying)
                    vm.ToggleVlcPlayPause();
                else if (vm.IsVideo)
                {
                    if (vm.PlayCommand.CanExecute(null))
                        vm.PlayCommand.Execute(null);
                }
                else if (vm.SlideshowCommand.CanExecute(null))
                {
                    vm.SlideshowCommand.Execute(null);
                }
                break;
            case Windows.System.VirtualKey.F:
                // Fit ↔ 1:1 — the same toggle the toolbar button drives
                // (README documented the shortcut; it now exists).
                vm.IsFitMode = !vm.IsFitMode;
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
        // Uninstall the thread-scope WH_KEYBOARD / WH_MOUSE hooks before the
        // window tears down. Leaving them installed risks a dangling HOOKPROC
        // (managed delegate) after the window is gone; UnhookWindowsHookEx
        // is the documented cleanup for SetWindowsHookEx.
        if (_hookHandle != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_hookHandle))
            {
                var gle = Marshal.GetLastWin32Error();
                Trace.TraceError($"AppWindow_Closing: UnhookWindowsHookEx(WH_KEYBOARD) failed, Win32 error {gle}");
            }
            _hookHandle = IntPtr.Zero;
        }
        if (_mouseHookHandle != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_mouseHookHandle))
            {
                var gle = Marshal.GetLastWin32Error();
                Trace.TraceError($"AppWindow_Closing: UnhookWindowsHookEx(WH_MOUSE) failed, Win32 error {gle}");
            }
            _mouseHookHandle = IntPtr.Zero;
        }
        _hookInstalled = false;

        SaveWindowState();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Remember the last non-maximized / non-fullscreen / non-minimized
        // bounds so a maximized (or fullscreen) session persists a sane
        // "restore to" geometry instead of the screen-sized bounds.
        if (IsFullscreen || IsMaximized || IsMinimized) return;
        var size = sender.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var pos = sender.Position;
        _lastWindowedBounds = new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);
    }

    /// <summary>
    /// First-run default geometry: 1280x800 DIP (clamped to the work area),
    /// centered on the primary display. Only used when no saved window
    /// state exists — a normally closed session restores its own geometry.
    /// </summary>
    private void ApplyDefaultWindowSize()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)Math.Round(1280 * scale), workArea.Width);
        var height = Math.Min((int)Math.Round(800 * scale), workArea.Height);
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    private void SaveWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            var tempFile = file + ".tmp";

            bool isFs = IsFullscreen;
            bool isMax = IsMaximized;
            int x = AppWindow.Position.X;
            int y = AppWindow.Position.Y;
            int w = AppWindow.Size.Width;
            int h = AppWindow.Size.Height;

            // In fullscreen / maximized, AppWindow reports the screen-sized
            // bounds; persist the last windowed bounds instead so that
            // un-maximizing after a relaunch returns to a sane size.
            if ((isFs || isMax) && _lastWindowedBounds.Width > 0 && _lastWindowedBounds.Height > 0)
            {
                x = _lastWindowedBounds.X;
                y = _lastWindowedBounds.Y;
                w = _lastWindowedBounds.Width;
                h = _lastWindowedBounds.Height;
            }

            var content = $"{x},{y},{w},{h},{(isFs ? 1 : 0)},{(isMax ? 1 : 0)}";

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
                // No saved geometry (very first run, or a session that never
                // closed normally). Apply a sensible default: a fresh launch
                // otherwise comes up at whatever the freshly applied
                // presenter defaults to (observed at the 680x480 minimum —
                // postage-stamp sized for a media viewer). Deferred to first
                // activation so the geometry is applied once the window is
                // actually shown; the deferred restore path was verified to
                // work (window matched the saved rect after activation).
                _pendingDefaultSize = true;
                Trace.TraceWarning("RestoreWindowState: file not found — default size will be applied on activation");
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
            // Geometry + maximize are deferred to first activation for the
            // same reason as the default-size path above.
            _pendingGeometry = _lastWindowedBounds;

            bool wasFullscreen = parts.Length >= 5 &&
                int.TryParse(parts[4], out var fs) && fs != 0;
            bool wasMaximized = parts.Length >= 6 &&
                int.TryParse(parts[5], out var max) && max != 0;

            if (wasFullscreen)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else if (wasMaximized)
            {
                _pendingMaximize = true;
            }

            Trace.TraceInformation($"Restored window state (deferred): {content}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RestoreWindowState error: {ex}");
        }
    }

    /// <summary>
    /// Applies the deferred window geometry on the first activation, i.e.
    /// once the window is actually shown and the presenter has been applied.
    /// </summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        try
        {
            if (_pendingDefaultSize)
            {
                ApplyDefaultWindowSize();
                _pendingDefaultSize = false;
            }

            if (_pendingGeometry is { } rect)
            {
                AppWindow.MoveAndResize(rect);
                _pendingGeometry = null;
            }

            if (_pendingMaximize && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
            _pendingMaximize = false;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"OnFirstActivated error: {ex}");
        }
    }
}
