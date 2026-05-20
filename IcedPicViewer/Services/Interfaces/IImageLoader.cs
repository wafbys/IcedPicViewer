using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.Services.Interfaces;

public interface IImageLoader
{
    IEnumerable<string> SupportedExtensions { get; }

    Task<byte[]?> LoadImageAsync(string path, CancellationToken ct = default);

    Task<BitmapImage?> LoadThumbnailAsync(string path, int maxSize, CancellationToken ct = default);

    Task<(int Width, int Height)?> GetImageSizeAsync(string path, CancellationToken ct = default);

    bool IsSupportedFormat(string path);
}
