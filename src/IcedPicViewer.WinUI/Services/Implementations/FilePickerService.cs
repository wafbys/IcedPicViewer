// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace IcedPicViewer.Services.Implementations;

public sealed class FilePickerService : IFilePickerService
{
    public async Task<string?> PickFileAsync(string commitButtonText, IReadOnlyList<string> extensions)
    {
        // Get the main window to associate the picker with (required for WinUI 3 desktop).
        Window? mainWindow = null;
        try
        {
            mainWindow = App.MainWindow;
        }
        catch
        {
            // In unit test or headless environments, accessing App.MainWindow can throw.
        }

        if (mainWindow == null)
            return null;

        try
        {
            var picker = new FileOpenPicker(mainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = commitButtonText,
                ViewMode = PickerViewMode.List,
            };

            foreach (var extension in extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var result = await picker.PickSingleFileAsync();
            return result?.Path;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"FilePickerService error: {ex}");
            return null;
        }
    }
}
