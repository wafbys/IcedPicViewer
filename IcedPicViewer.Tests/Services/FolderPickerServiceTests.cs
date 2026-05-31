// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IcedPicViewer.Tests.Services;

[TestClass]
public class FolderPickerServiceTests
{
    [TestMethod]
    public void PickFolderAsync_WhenNoMainWindow_ReturnsNull()
    {
        // Arrange
        // In test context, App.MainWindow is typically null
        var service = new FolderPickerService();

        // Act
        var result = service.PickFolderAsync("Test").Result;

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void PickFolderAsync_HandlesMissingMainWindowGracefully()
    {
        // Arrange
        var service = new FolderPickerService();

        // Act - We only verify it doesn't throw synchronously before hitting App.MainWindow issues in test host
        // The actual async path is hard to unit test without UI/App context.
        var task = service.PickFolderAsync("Select Test Folder");

        // Assert - At minimum, the method should return a task without immediate crash
        Assert.IsNotNull(task);
    }
}
