// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using IcedPicViewer.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IcedPicViewer;

public sealed partial class MainWindow : Window
{
    private const string SettingsFile = "window_settings.txt";
    private const string AppDataFolder = "IcedPicViewer";

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

    public MainWindow()
    {
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

        // Thread-scope WH_KEYBOARD hook for ImageViewerView shortcuts
        // (Left/Right/Delete/Escape). All previous XAML-layer attempts
        // (AddHandler(KeyDownEvent), KeyboardAccelerator) AND the prior
        // SetWindowSubclass approach failed in MSIX packaged mode:
        //
        //   - XAML routed events need a focused element; focus is unreliable
        //     after Frame.Navigate.
        //   - SetWindowSubclass registered successfully on the XAML island's
        //     inner HWND (verified via diagnostic log: "subclass registered on
        //     hwnd=0x1A0978") but WM_KEYDOWN was never delivered to that HWND
        //     — the OS routes keyboard input to a higher-level window in the
        //     XAML island / ApplicationFrame hierarchy.
        //
        // WH_KEYBOARD with thread scope sidesteps all of this: it hooks the
        // current thread's message queue (the WinUI 3 UI thread's dispatch
        // loop), so every keystroke that reaches the app goes through us,
        // regardless of which HWND would have received it. No focus
        // dependency, no HWND dependency.
        //
        // WH_KEYBOARD_LL would be a tempting alternative but it requires a
        // separate DLL to be injected into the target thread; MSIX sandbox
        // generally blocks that. WH_KEYBOARD is thread-internal and
        // DLL-free.
        //
        // Defer the install via the DispatcherQueue so GetCurrentThreadId
        // captures the UI thread, not whatever thread happened to be
        // running the ctor (which is the UI thread in practice, but the
        // dispatcher round-trip is cheap and removes a class of "ctor ran
        // on the wrong thread" bugs that already bit us with the ctor-time
        // SetWindowSubclass attempt).
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
            LogKbd($"SetWindowsHookEx FAILED threadId={threadId} gle={gle}");
        }
        else
        {
            _hookInstalled = true;
            LogKbd($"WH_KEYBOARD hook installed threadId={threadId} hhk=0x{_hookHandle.ToInt64():X}");
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
        // CRITICAL invariants for WH_KEYBOARD hooks:
        //   1. nCode < 0 → pass to CallNextHookEx with no work.
        //   2. Callback must return quickly — slow callbacks degrade input
        //      latency across the whole thread.
        //   3. Exceptions MUST NOT escape the callback — an uncaught throw
        //      in a hook is undefined behavior and on Win11 25H2 + MSIX
        //      manifests as a silent process crash (this is exactly what
        //      bit us in the previous attempt: vm.NavigateNextCommand.Execute
        //      kicks off an async chain via await ShowCurrentImageAsync, and
        //      anything thrown on the continuation crashes the process).
        //
        // To honor all three, the hook body is wrapped in try/catch and the
        // actual VM dispatch is hopped to the DispatcherQueue so the await
        // chain starts AFTER the hook returns. The hop is a synchronous
        // TryEnqueue (we're already on the UI thread, the dispatcher pulls
        // the work item immediately) — so log line ordering is preserved.
        try
        {
            if (nCode >= 0)
            {
                // Wrap every cast/convert in unchecked to make sure no checked
                // context slips in (e.g. if a future maintainer wraps this
                // method body in `checked { ... }` for some reason). The
                // bitwise-AND in the isKeyUp test and the int→enum cast for
                // VirtualKey have no logical reason to throw, but on Win11
                // 25H2 + MSIX we keep seeing hook callback threw:
                // OverflowException — so we trace with full stack and
                // explicitly mark the casts unchecked.
                unchecked
                {
                    var vk = (Windows.System.VirtualKey)wParam.ToInt32();
                    bool isKeyUp = (lParam.ToInt32() & int.MinValue) != 0;
                    if (!isKeyUp)
                    {
                        LogKbd($"WH_KEYBOARD vk={vk} page={RootFrame.Content?.GetType().Name ?? "<null>"}");
                        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                        {
                            try { HandleViewerKey(vk); }
                            catch (Exception ex) { LogKbd($"HandleViewerKey threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Last-resort: never let a hook exception reach the OS.
            // Capture full stack so the next log reading can pin down the
            // exact offending line without further trial-and-error builds.
            LogKbd($"hook callback threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // ---- Diagnostic log for keyboard hook verification ----
    // Written to %LOCALAPPDATA%\IcedPicViewer\kbd.log so the user can cat it
    // and see whether the subclass is firing at all. Delete the file to
    // start fresh; the path is fixed and deterministic.

    private const string KbdLogPath = "kbd.log";

    private static readonly object _logLock = new();
    private static void LogKbd(string msg)
    {
        lock (_logLock)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "IcedPicViewer");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, KbdLogPath),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
            catch
            {
                // Logging must never throw out of the WndProc — would crash the app.
            }
        }
    }

    private void HandleViewerKey(Windows.System.VirtualKey key)
    {
        if (RootFrame.Content is not ImageViewerView viewer || viewer.DataContext is not ImageViewModel vm)
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
        }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        SaveWindowState();
    }

    private void SaveWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            var tempFile = file + ".tmp";
            var content = $"{AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}";

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
            if (parts.Length != 4)
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

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)x, (int)y, (int)width, (int)height));
            Trace.TraceInformation($"Restored window state: {content}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RestoreWindowState error: {ex}");
        }
    }
}
