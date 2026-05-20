using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace IcedPicViewer.Services.Implementations;

public class ImageLoader : IImageLoader
{
    private static readonly HashSet<string> s_supportedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".avif", ".heic"];

    public IEnumerable<string> SupportedExtensions => s_supportedExtensions;

    public bool IsSupportedFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return s_supportedExtensions.Contains(ext);
    }

    public async Task<byte[]?> LoadImageAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream, ct);
            return memoryStream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public async Task<BitmapImage?> LoadThumbnailAsync(string path, int maxSize, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var bitmapImage = new BitmapImage();
            bitmapImage.DecodePixelWidth = maxSize;

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var randomAccessStream = new InMemoryRandomAccessStream();
            await fileStream.CopyToAsync(randomAccessStream.AsStreamForWrite(), ct);
            randomAccessStream.Seek(0);

            await bitmapImage.SetSourceAsync(randomAccessStream);
            return bitmapImage;
        }
        catch
        {
            return null;
        }
    }

    public async Task<(int Width, int Height)?> GetImageSizeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var bitmapImage = new BitmapImage();
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var randomAccessStream = new InMemoryRandomAccessStream();
            await fileStream.CopyToAsync(randomAccessStream.AsStreamForWrite(), ct);
            randomAccessStream.Seek(0);
            await bitmapImage.SetSourceAsync(randomAccessStream);

            return (bitmapImage.PixelWidth, bitmapImage.PixelHeight);
        }
        catch
        {
            return null;
        }
    }
}
