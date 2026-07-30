// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Avalonia.Services;

/// <summary>
/// Desktop shell helpers for Win / macOS / Linux. Trash uses platform
/// conventions; Reveal opens the native file manager.
/// </summary>
public sealed class DesktopShellService : IShellService
{
    public void RevealInFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // /select requires a path that exists; quote for spaces.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                if (File.Exists(path))
                {
                    Process.Start("open", ["-R", path]);
                }
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        Process.Start("open", [dir]);
                }
                return;
            }

            // Linux: open containing folder (select-in-folder is DE-specific).
            // Use ArgumentList with xdg-open so paths containing spaces are
            // passed as a single argument without shell interpretation.
            var folder = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { folder },
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RevealInFolder failed for {path}: {ex.Message}");
        }
    }

    public bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Network;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"DesktopShellService.IsNetworkPath probe failed for {path}: {ex.Message}");
            return false;
        }
    }

    public bool TryDelete(string path, bool preferTrash, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            errorMessage = IcedPicViewer.Core.Text.UiCopy.FileNotFound;
            return false;
        }

        try
        {
            if (preferTrash)
            {
                if (OperatingSystem.IsWindows())
                {
                    if (TryWindowsRecycle(path, out errorMessage))
                        return true;
                    // Fall through to permanent delete only if recycle failed hard.
                }
                else if (OperatingSystem.IsMacOS())
                {
                    MoveToMacTrash(path);
                    return true;
                }
                else if (OperatingSystem.IsLinux())
                {
                    MoveToXdgTrash(path);
                    return true;
                }
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            Trace.TraceError($"TryDelete failed for {path}: {ex.Message}");
            return false;
        }
    }

    private static bool TryWindowsRecycle(string path, out string? error)
    {
        error = null;
        try
        {
            // SHFileOperationW with FO_DELETE + FOF_ALLOWUNDO → Recycle Bin.
            var ops = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + '\0' + '\0', // double-null terminated list
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var result = SHFileOperation(ref ops);
            if (result != 0)
            {
                error = $"SHFileOperation error {result}";
                return false;
            }
            return !ops.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void MoveToMacTrash(string path)
    {
        // osascript: move POSIX file to trash
        var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
        Process.Start(new ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList =
            {
                "-e",
                $"tell application \"Finder\" to delete POSIX file \"{escaped}\"",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.WaitForExit(10_000);
    }

    private static void MoveToXdgTrash(string path)
    {
        // Prefer `gio trash` / `trash-put` when available; else ~/.local/share/Trash.
        if (TryRun("gio", ["trash", path]) || TryRun("trash-put", [path]))
            return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trashFiles = Path.Combine(home, ".local", "share", "Trash", "files");
        var trashInfo = Path.Combine(home, ".local", "share", "Trash", "info");
        Directory.CreateDirectory(trashFiles);
        Directory.CreateDirectory(trashInfo);

        var name = Path.GetFileName(path);
        var dest = Path.Combine(trashFiles, name);
        if (File.Exists(dest))
            dest = Path.Combine(trashFiles, $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(name)}");

        File.Move(path, dest);

        var infoPath = Path.Combine(trashInfo, Path.GetFileName(dest) + ".trashinfo");
        var deletionDate = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(infoPath,
            $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}\nDeletionDate={deletionDate}\n",
            Encoding.UTF8);
    }

    private static bool TryRun(string fileName, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"DesktopShellService.TryRun failed for {fileName}: {ex.Message}");
            return false;
        }
    }

    private const int FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

#pragma warning disable SYSLIB1054 // DllImport kept for ref SHFILEOPSTRUCT; LibraryImport needs different shape
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
#pragma warning restore SYSLIB1054
}
