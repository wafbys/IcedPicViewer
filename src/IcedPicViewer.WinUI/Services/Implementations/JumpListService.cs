// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IcedPicViewer.Core.Text;
using IcedPicViewer.Services.Interfaces;
using Windows.UI.StartScreen;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Shell jump list (taskbar right-click) integration. Two kinds of entries:
/// a static "open folder" task (its activation argument is
/// <see cref="OpenFolderPickerArgument"/> — the launched instance shows the
/// folder picker) and the most recently opened folders, whose activation
/// argument is the folder path itself. Recents are persisted in
/// <c>AppSettings.RecentFolders</c>; this service only mirrors them into
/// the shell.
///
/// <para>
/// The WinRT JumpList API needs package identity; it is available in this
/// packaged desktop app (verified via IsSupported()/LoadCurrentAsync on
/// Windows 11 25H2). Every failure is swallowed with a Trace line — a
/// broken jump list must never affect folder opening.
/// </para>
/// </summary>
public sealed class JumpListService : IJumpListService
{
    /// <summary>Activation argument of the "open folder" task item.</summary>
    public const string OpenFolderPickerArgument = "open-folder-picker";

    private const int MaxRecentItems = 5;

    public async Task UpdateRecentFoldersAsync(IReadOnlyList<string> folders)
    {
        try
        {
            if (!JumpList.IsSupported())
            {
                return;
            }

            var list = await JumpList.LoadCurrentAsync();
            list.Items.Clear();

            var task = JumpListItem.CreateWithArguments(OpenFolderPickerArgument, UiCopy.OpenFolder);
            task.Description = UiCopy.OpenFolder;
            list.Items.Add(task);

            foreach (var folder in folders.Take(MaxRecentItems))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                if (string.IsNullOrEmpty(name)) name = trimmed; // drive root, e.g. "C:"
                var item = JumpListItem.CreateWithArguments(folder, name);
                item.Description = folder;
                item.GroupName = UiCopy.RecentFolders;
                list.Items.Add(item);
            }

            await list.SaveAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"JumpListService.UpdateRecentFoldersAsync error: {ex}");
        }
    }
}
