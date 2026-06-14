using IcedPicViewer.Models;
using Microsoft.UI.Xaml.Media.Imaging;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

public interface IImageLoader
{
    IEnumerable<string> SupportedExtensions { get; }

    /// <summary>
    /// Opens a read stream over the image. The caller takes ownership of
    /// the returned stream and is responsible for disposing it.
    /// Returns null if the source does not exist or cannot be opened.
    /// </summary>
    Task<Stream?> LoadImageStreamAsync(ImageSource source, CancellationToken ct = default);

    Task<BitmapImage?> LoadThumbnailAsync(ImageSource source, int maxSize, CancellationToken ct = default);

    Task<(int Width, int Height)?> GetImageSizeAsync(ImageSource source, CancellationToken ct = default);

    bool IsSupportedFormat(string path);
}
