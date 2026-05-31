// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IcedPicViewer.Tests.Services;

[TestClass]
public class NavigationServiceTests
{
    [TestMethod]
    public void CanGoBack_BeforeInitialize_ReturnsFalse()
    {
        // Arrange
        var service = new NavigationService();

        // Act & Assert
        Assert.IsFalse(service.CanGoBack);
    }

    [TestMethod]
    public void Initialize_WithNullFrame_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new NavigationService();

        // Act & Assert
        try
        {
            service.Initialize(null!);
            Assert.Fail("Expected ArgumentNullException was not thrown.");
        }
        catch (System.ArgumentNullException)
        {
            // Expected
        }
    }

    [TestMethod]
    public void GoBack_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Arrange
        var service = new NavigationService();

        // Act & Assert
        try
        {
            service.GoBack();
            Assert.Fail("Expected InvalidOperationException was not thrown.");
        }
        catch (System.InvalidOperationException)
        {
            // Expected
        }
    }

    [TestMethod]
    public void NavigateTo_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Arrange
        var service = new NavigationService();

        // Act & Assert
        try
        {
            service.NavigateTo<Page>();
            Assert.Fail("Expected InvalidOperationException was not thrown.");
        }
        catch (System.InvalidOperationException)
        {
            // Expected
        }
    }
}
