// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Tests.Media;

public sealed class MediaQueryTests
{
    [Theory]
    [InlineData(MediaFilter.All, MediaKind.Image, true)]
    [InlineData(MediaFilter.All, MediaKind.Video, true)]
    [InlineData(MediaFilter.Image, MediaKind.Image, true)]
    [InlineData(MediaFilter.Image, MediaKind.Video, false)]
    [InlineData(MediaFilter.Video, MediaKind.Video, true)]
    [InlineData(MediaFilter.Video, MediaKind.Image, false)]
    public void MatchesFilter_Works(MediaFilter filter, MediaKind kind, bool expected)
        => Assert.Equal(expected, MediaQuery.MatchesFilter(filter, kind));

    [Theory]
    [InlineData("IMG_0012.jpg", null, true)]
    [InlineData("IMG_0012.jpg", "", true)]
    [InlineData("IMG_0012.jpg", "0012", true)]
    [InlineData("IMG_0012.jpg", "IMG", true)]
    [InlineData("IMG_0012.jpg", "img_", true)]      // case-insensitive
    [InlineData("IMG_0012.jpg", "*.jpg", true)]
    [InlineData("IMG_0012.jpg", "*.png", false)]
    [InlineData("IMG_0012.jpg", "IMG_????.jpg", true)]
    [InlineData("IMG_0012.jpg", "IMG_??.jpg", false)]
    [InlineData("IMG_0012.jpg", "x*", false)]
    public void MatchesSearch_Works(string name, string? pattern, bool expected)
        => Assert.Equal(expected, MediaQuery.MatchesSearch(name, pattern));

    [Fact]
    public void MatchesSearch_ContainsVsWildcardAreAnchored()
    {
        // No wildcard: substring, so "12" matches anywhere.
        Assert.True(MediaQuery.MatchesSearch("IMG_0012.jpg", "12"));
        // Wildcard: whole-name match, so "12*" does not match a name ending in 12.
        Assert.False(MediaQuery.MatchesSearch("IMG_0012.jpg", "12*"));
    }

    [Theory]
    [InlineData("img_2.jpg", "img_10.jpg", -1)]
    [InlineData("img_10.jpg", "img_2.jpg", 1)]
    [InlineData("img_2.jpg", "img_2.jpg", 0)]
    [InlineData("IMG_2.jpg", "img_2.jpg", 0)]        // case-insensitive
    [InlineData("a2b", "a10b", -1)]
    [InlineData("a007", "a7", 0)]                    // leading zeros ignored
    [InlineData("file", "file2", -1)]                // shorter prefix first
    public void NaturalStringComparer_Works(string a, string b, int expectedSign)
    {
        var result = NaturalStringComparer.OrdinalIgnoreCase.Compare(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void CompareName_IsNaturalAndTiesOnPath()
    {
        var two = MediaRef.FromFile(@"C:\p\img_2.jpg");
        var ten = MediaRef.FromFile(@"C:\p\img_10.jpg");

        Assert.True(MediaQuery.CompareName(two, ten) < 0);
        Assert.True(MediaQuery.CompareName(ten, two) > 0);
    }

    [Fact]
    public void GetDisplayName_And_Extension_HandleArchiveEntries()
    {
        var inArchive = MediaRef.FromArchive(@"C:\p\pack.zip", "photos/sunset.JPG", MediaKind.Image);

        Assert.Equal("sunset.JPG", MediaQuery.GetDisplayName(inArchive));
        Assert.Equal(".JPG", MediaQuery.GetExtension(inArchive));
    }

    [Fact]
    public void CompareExtension_GroupsByExtensionThenName()
    {
        var jpg = MediaRef.FromFile(@"C:\p\b.jpg");
        var png = MediaRef.FromFile(@"C:\p\a.png");

        // .jpg before .png regardless of the base name.
        Assert.True(MediaQuery.CompareExtension(jpg, png) < 0);
    }
}
