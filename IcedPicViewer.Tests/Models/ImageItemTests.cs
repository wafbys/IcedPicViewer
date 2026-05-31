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
}
