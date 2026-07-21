// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Service for letting the user pick a folder using the modern Windows App SDK picker.
/// </summary>
public interface IFolderPickerService
{
    /// <summary>
    /// Shows the folder picker dialog.
    /// </summary>
    /// <param name="title">Optional title / commit button text for the picker.</param>
    /// <returns>The full path of the selected folder, or null if the user cancelled.</returns>
    Task<string?> PickFolderAsync(string title = "Select a folder");
}
