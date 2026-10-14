// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Shell taskbar progress via <c>ITaskbarList3</c> — the classic Win32
/// taskbar API; the Windows App SDK has no managed equivalent. The
/// interface declares only the vtable slots up to the two methods actually
/// called: COM dispatch is by slot index, so everything before them must
/// keep its order (ITaskbarList: 5 methods, ITaskbarList2:
/// MarkFullscreenWindow, then SetProgressValue / SetProgressState).
///
/// <para>Every failure degrades to a no-op — a missing taskbar button
/// (e.g. some remote sessions) must never break scanning.</para>
/// </summary>
public sealed class TaskbarProgressService : ITaskbarProgressService
{
    private enum TBPFLAG
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarListCoClass
    {
    }

    // SYSLIB1096 suggests [GeneratedComInterface] (source-generated COM).
    // For this classic Win32 taskbar interface the source generator would
    // drag in ComWrappers-based activation for zero benefit here — the
    // hand-declared vtable is the established, battle-tested pattern for
    // ITaskbarList3 and needs no marshalling code beyond the two calls.
#pragma warning disable SYSLIB1096
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3 — only the two slots this app uses; the remainder of
        // the interface is intentionally not declared (never called past here).
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TBPFLAG flags);
    }
#pragma warning restore SYSLIB1096

    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;

    public void Attach(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarListCoClass();
            taskbar.HrInit();
            _taskbar = taskbar;
        }
        catch (Exception ex)
        {
            // No taskbar button in this session — progress is cosmetic,
            // everything else keeps working.
            Trace.TraceInformation($"TaskbarProgressService: unavailable ({ex.Message})");
            _taskbar = null;
        }
    }

    public void SetIndeterminate() => SetState(TBPFLAG.Indeterminate);

    public void Clear() => SetState(TBPFLAG.NoProgress);

    private void SetState(TBPFLAG flags)
    {
        if (_taskbar is null || _hwnd == IntPtr.Zero) return;
        try
        {
            _taskbar.SetProgressState(_hwnd, flags);
        }
        catch (Exception ex)
        {
            // One strike and out: a broken shell integration should not
            // throw on every scan-state change.
            Trace.TraceError($"TaskbarProgressService.SetProgressState failed: {ex.Message}");
            _taskbar = null;
        }
    }
}
