// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.IO;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IcedPicViewer.Tests.Services;

[TestClass]
public class DirectoryScannerTests
{
    private readonly DirectoryScanner _scanner = new DirectoryScanner();

    // CA1861: prefer static readonly over const arrays for repeated invocations.
    private static readonly string[] JpgOnly = [".jpg"];

    [TestMethod]
    public async Task ScanAsync_EmptyDirectory_ReturnsNoFiles()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            var results = new List<string>();
            await foreach (var path in _scanner.ScanAsync(tempDir, recursive: false))
            {
                results.Add(path);
            }

            // Assert
            Assert.AreEqual(0, results.Count);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public async Task ScanAsync_SkipsRecycleBinFolders()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string recycleDir = Path.Combine(tempDir, "$RECYCLE.BIN");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(recycleDir);
        File.WriteAllText(Path.Combine(recycleDir, "hidden.txt"), "should not be found");

        try
        {
            // Act
            var results = new List<string>();
            await foreach (var path in _scanner.ScanAsync(tempDir, recursive: true))
            {
                results.Add(path);
            }

            // Assert
            Assert.IsFalse(results.Any(p => p.Contains("$RECYCLE.BIN")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public async Task ScanAsync_FiltersByExtensions()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "image.jpg"), "");
        File.WriteAllText(Path.Combine(tempDir, "document.txt"), "");

        try
        {
            // Act
            var results = new List<string>();
            await foreach (var path in _scanner.ScanAsync(tempDir, recursive: false, extensions: JpgOnly))
            {
                results.Add(path);
            }

            // Assert
            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
