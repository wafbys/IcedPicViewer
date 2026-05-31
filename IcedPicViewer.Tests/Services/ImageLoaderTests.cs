// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IcedPicViewer.Tests.Services;

[TestClass]
public class ImageLoaderTests
{
    private readonly ImageLoader _imageLoader = new ImageLoader();

    [TestMethod]
    public void IsSupportedFormat_ValidExtensions_ReturnsTrue()
    {
        // Act & Assert
        Assert.IsTrue(_imageLoader.IsSupportedFormat("test.jpg"));
        Assert.IsTrue(_imageLoader.IsSupportedFormat("test.png"));
        Assert.IsTrue(_imageLoader.IsSupportedFormat("test.gif"));
        Assert.IsTrue(_imageLoader.IsSupportedFormat("test.webp"));
    }

    [TestMethod]
    public void IsSupportedFormat_InvalidExtension_ReturnsFalse()
    {
        // Act & Assert
        Assert.IsFalse(_imageLoader.IsSupportedFormat("test.txt"));
        Assert.IsFalse(_imageLoader.IsSupportedFormat("test.exe"));
        Assert.IsFalse(_imageLoader.IsSupportedFormat("test"));
    }

    [TestMethod]
    public async Task LoadThumbnailAsync_NonExistentFile_ReturnsNull()
    {
        // Arrange
        const string nonExistentPath = @"C:\this\path\does\not\exist\image.jpg";

        // Act
        var result = await _imageLoader.LoadThumbnailAsync(nonExistentPath, 400);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task LoadImageAsync_NonExistentFile_ReturnsNull()
    {
        // Arrange
        const string nonExistentPath = @"C:\this\path\does\not\exist\image.jpg";

        // Act
        var result = await _imageLoader.LoadImageAsync(nonExistentPath);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetImageSizeAsync_NonExistentFile_ReturnsNull()
    {
        // Arrange
        const string nonExistentPath = @"C:\this\path\does\not\exist\image.jpg";

        // Act
        var result = await _imageLoader.GetImageSizeAsync(nonExistentPath);

        // Assert
        Assert.IsNull(result);
    }
}
