using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace IcedPicViewer.Services.Implementations;

public sealed class FolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string title = "Select a folder")
    {
        // Get the main window to associate the picker with (required for WinUI 3 desktop)
        Window? mainWindow = null;
        try
        {
            mainWindow = App.MainWindow;
        }
        catch
        {
            // In unit test or headless environments, accessing App.MainWindow can throw.
            // Treat as "no window available".
        }

        if (mainWindow == null)
            return null;

        try
        {
            var folderPicker = new FolderPicker(mainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = title,
                ViewMode = PickerViewMode.List
            };

            var result = await folderPicker.PickSingleFolderAsync();

            return result?.Path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"FolderPickerService error: {ex}");
            return null;
        }
    }
}
