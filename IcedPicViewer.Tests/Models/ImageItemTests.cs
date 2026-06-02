using IcedPicViewer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IcedPicViewer.Tests.Models;

[TestClass]
public class ImageItemTests
{
    [TestMethod]
    public void FileSizeText_WhenLessThan1KB_ReturnsBytes()
    {
        // Arrange
        var item = new ImageItem(
            id: "test",
            name: "test.jpg",
            path: @"C:\test.jpg",
            fileSize: 512,
            modifiedTime: DateTime.Now,
            originalWidth: 100,
            originalHeight: 100);

        // Act & Assert
        Assert.AreEqual("512 B", item.FileSizeText);
    }

    [TestMethod]
    public void FileSizeText_WhenBetween1KBAnd1MB_ReturnsKB()
    {
        // Arrange
        var item = new ImageItem(
            id: "test",
            name: "test.jpg",
            path: @"C:\test.jpg",
            fileSize: 1536, // 1.5 KB
            modifiedTime: DateTime.Now,
            originalWidth: 100,
            originalHeight: 100);

        // Act & Assert
        Assert.AreEqual("1.5 KB", item.FileSizeText);
    }

    [TestMethod]
    public void FileSizeText_WhenOver1MB_ReturnsMB()
    {
        // Arrange
        var item = new ImageItem(
            id: "test",
            name: "test.jpg",
            path: @"C:\test.jpg",
            fileSize: 2_621_440, // 2.5 MB
            modifiedTime: DateTime.Now,
            originalWidth: 100,
            originalHeight: 100);

        // Act & Assert
        Assert.AreEqual("2.5 MB", item.FileSizeText);
    }

    [TestMethod]
    public void OriginalSizeText_WhenBothDimensionsValid_ReturnsFormatted()
    {
        var item = new ImageItem(
            id: "test", name: "test.jpg", path: @"C:\test.jpg",
            fileSize: 1024, modifiedTime: DateTime.Now,
            originalWidth: 1920, originalHeight: 1080);

        Assert.AreEqual("1920×1080", item.OriginalSizeText);
    }

    [TestMethod]
    public void OriginalSizeText_WhenWidthZero_ReturnsUnknown()
    {
        // Simulates a corrupt or unsupported file where GetImageSizeAsync returns null.
        var item = new ImageItem(
            id: "test", name: "test.jpg", path: @"C:\test.jpg",
            fileSize: 1024, modifiedTime: DateTime.Now,
            originalWidth: 0, originalHeight: 1080);

        Assert.AreEqual("Unknown", item.OriginalSizeText);
    }

    [TestMethod]
    public void OriginalSizeText_WhenHeightZero_ReturnsUnknown()
    {
        var item = new ImageItem(
            id: "test", name: "test.jpg", path: @"C:\test.jpg",
            fileSize: 1024, modifiedTime: DateTime.Now,
            originalWidth: 1920, originalHeight: 0);

        Assert.AreEqual("Unknown", item.OriginalSizeText);
    }

    [TestMethod]
    public void OriginalSizeText_WhenBothZero_ReturnsUnknown()
    {
        var item = new ImageItem(
            id: "test", name: "test.jpg", path: @"C:\test.jpg",
            fileSize: 1024, modifiedTime: DateTime.Now,
            originalWidth: 0, originalHeight: 0);

        Assert.AreEqual("Unknown", item.OriginalSizeText);
    }
}
