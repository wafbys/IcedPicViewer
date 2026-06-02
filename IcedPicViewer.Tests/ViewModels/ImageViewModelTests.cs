// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Globalization;
using System.Threading.Tasks;

namespace IcedPicViewer.Tests.ViewModels;

[TestClass]
public class ImageViewModelTests
{
    private static GalleryViewModel CreateGallery(out Mock<IImageLoader> sharedLoader)
    {
        var mockScanner = new Mock<IDirectoryScanner>();
        sharedLoader = new Mock<IImageLoader>();
        var mockPicker = new Mock<IFolderPickerService>();
        return new GalleryViewModel(mockScanner.Object, sharedLoader.Object, mockPicker.Object);
    }

    private static ImageItem MakeItem(int i) => new(
        id: i.ToString(CultureInfo.InvariantCulture),
        name: $"img{i.ToString(CultureInfo.InvariantCulture)}.jpg",
        path: $@"C:\fake\img{i.ToString(CultureInfo.InvariantCulture)}.jpg",
        fileSize: 1024,
        modifiedTime: System.DateTime.Now,
        originalWidth: 800,
        originalHeight: 600);

    [TestMethod]
    public void ShowImageAsync_SetsCurrentImageAndIndex()
    {
        // Arrange
        var gallery = CreateGallery(out var loader);
        var nav = new Mock<INavigationService>();
        loader.Setup(l => l.LoadImageStreamAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
              .ReturnsAsync((System.IO.Stream?)null);

        gallery.Images.Add(MakeItem(1));
        gallery.Images.Add(MakeItem(2));
        gallery.Images.Add(MakeItem(3));

        var vm = new ImageViewModel(gallery, loader.Object, nav.Object);

        // Act
        vm.ShowImageAsync(gallery.Images[1]).GetAwaiter().GetResult();

        // Assert
        Assert.AreSame(gallery.Images[1], vm.CurrentImage);
        Assert.AreEqual(1, vm.CurrentIndex);
        Assert.AreEqual(3, vm.TotalCount);
        Assert.AreEqual(1, gallery.LastViewedIndex);
    }

    [TestMethod]
    public void NavigateNextAsync_AdvancesIndex()
    {
        var gallery = CreateGallery(out var loader);
        var nav = new Mock<INavigationService>();
        loader.Setup(l => l.LoadImageStreamAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
              .ReturnsAsync((System.IO.Stream?)null);

        gallery.Images.Add(MakeItem(1));
        gallery.Images.Add(MakeItem(2));
        gallery.Images.Add(MakeItem(3));

        var vm = new ImageViewModel(gallery, loader.Object, nav.Object);
        vm.ShowImageAsync(gallery.Images[0]).GetAwaiter().GetResult();

        // Act
        vm.NavigateNextCommand.Execute(null);

        // Assert
        Assert.AreEqual(1, vm.CurrentIndex);
        Assert.AreSame(gallery.Images[1], vm.CurrentImage);
    }

    [TestMethod]
    public void NavigatePreviousAsync_AtFirst_DoesNothing()
    {
        var gallery = CreateGallery(out var loader);
        var nav = new Mock<INavigationService>();

        gallery.Images.Add(MakeItem(1));
        gallery.Images.Add(MakeItem(2));

        var vm = new ImageViewModel(gallery, loader.Object, nav.Object);
        vm.ShowImageAsync(gallery.Images[0]).GetAwaiter().GetResult();

        // Act
        vm.NavigatePreviousCommand.Execute(null);

        // Assert
        Assert.AreEqual(0, vm.CurrentIndex);
        Assert.AreSame(gallery.Images[0], vm.CurrentImage);
    }

    [TestMethod]
    public void NavigatePreviousAsync_DecrementsIndex()
    {
        var gallery = CreateGallery(out var loader);
        var nav = new Mock<INavigationService>();
        loader.Setup(l => l.LoadImageStreamAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
              .ReturnsAsync((System.IO.Stream?)null);

        gallery.Images.Add(MakeItem(1));
        gallery.Images.Add(MakeItem(2));
        gallery.Images.Add(MakeItem(3));

        var vm = new ImageViewModel(gallery, loader.Object, nav.Object);
        vm.ShowImageAsync(gallery.Images[2]).GetAwaiter().GetResult();

        // Act
        vm.NavigatePreviousCommand.Execute(null);

        // Assert
        Assert.AreEqual(1, vm.CurrentIndex);
        Assert.AreSame(gallery.Images[1], vm.CurrentImage);
    }

    [TestMethod]
    public void CloseCommand_CallsNavigationGoBack()
    {
        var gallery = CreateGallery(out var loader);
        var nav = new Mock<INavigationService>();

        gallery.Images.Add(MakeItem(1));
        var vm = new ImageViewModel(gallery, loader.Object, nav.Object);
        vm.ShowImageAsync(gallery.Images[0]).GetAwaiter().GetResult();

        // Act
        vm.CloseCommand.Execute(null);

        // Assert
        nav.Verify(n => n.GoBack(), Times.Once);
        Assert.IsNull(vm.CurrentImage);
        Assert.IsNull(vm.DisplayImage);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesAndKeepsIndex()
    {
        var gallery = CreateGallery(out var loader);
        var nav = new Mock<INavigationService>();
        loader.Setup(l => l.LoadImageStreamAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
              .ReturnsAsync((System.IO.Stream?)null);
        // System.IO.File.Exists returns false for the fake path; DeleteImageAsync early-exits.
        // For testing logic, we manually remove via gallery and exercise the index-adjustment branch
        // by re-invoking DeleteAsync on a real existing file in temp dir.
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid() + ".jpg");
        System.IO.File.WriteAllBytes(tempFile, new byte[] { 0 });

        var item = new ImageItem("real", "real.jpg", tempFile, 1, System.DateTime.Now, 10, 10);
        gallery.Images.Add(MakeItem(1));
        gallery.Images.Add(item);
        gallery.Images.Add(MakeItem(3));
        gallery.TotalCount = 3;

        var vm = new ImageViewModel(gallery, loader.Object, nav.Object);
        vm.ShowImageAsync(item).GetAwaiter().GetResult();

        try
        {
            // Act
            await vm.DeleteCommand.ExecuteAsync(null);

            // Assert: item removed, index points to a remaining image
            Assert.IsFalse(gallery.Images.Contains(item));
            Assert.IsTrue(vm.CurrentIndex >= 0 && vm.CurrentIndex < gallery.Images.Count);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }
}
