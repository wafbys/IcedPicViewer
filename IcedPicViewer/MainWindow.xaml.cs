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
        // ImageViewerView binds via x:Bind to a ViewModel field on the code-behind
        // (not through DataContext), so DataContext is null here — read VM from
        // the typed ViewModel property exposed on the page.
        if (RootFrame.Content is not ImageViewerView viewer || viewer.ViewModel is not ImageViewModel vm)
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
                // Space is the keyboard shortcut to start video playback
                // from the static first-frame state (matches the gallery
                // card's click-to-play affordance). The PlayCommand's
                // CanExecute returns false once IsVideoPlaying is true, so
                // we don't fight the MediaPlayerElement's built-in
                // transport controls for Space-to-pause once the player
                // surface is up. See ImageViewModel.PlayAsync + the
                // CanPlay guard for the gate that makes this safe.
                if (vm.PlayCommand.CanExecute(null))
                    vm.PlayCommand.Execute(null);
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
