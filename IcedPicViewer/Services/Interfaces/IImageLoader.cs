using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

public interface IImageLoader
{
    IEnumerable<string> SupportedExtensions { get; }

    /// <summary>
    /// Opens a read stream over the image file. The caller takes ownership of
    /// the returned stream and is responsible for disposing it.
    /// Returns null if the file does not exist or cannot be opened.
    /// </summary>
    Task<Stream?> LoadImageStreamAsync(string path, CancellationToken ct = default);

    Task<BitmapImage?> LoadThumbnailAsync(string path, int maxSize, CancellationToken ct = default);

    Task<(int Width, int Height)?> GetImageSizeAsync(string path, CancellationToken ct = default);

    bool IsSupportedFormat(string path);
}
