// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Models;

public sealed partial class ImageItem : MediaItem
{
    public ImageItem(
        MediaRef media,
        long fileSize,
        DateTime modifiedTime,
        int originalWidth,
        int originalHeight)
        : base(media, fileSize, modifiedTime, originalWidth, originalHeight)
    {
    }

    /// <summary>
    /// Original pixel dimensions formatted as "WIDTH×HEIGHT", or "Unknown" when
    /// the size could not be determined (corrupt file, unsupported format, etc.).
    /// Avoids the misleading "0×0" you would get from binding OriginalWidth/Height
    /// directly when the metadata is missing.
    /// </summary>
    public override string OriginalSizeText
    {
        get
        {
            var size = MediaDisplay.FormatPixelSize(OriginalWidth, OriginalHeight);
            return string.IsNullOrEmpty(size) ? UiCopy.UnknownSize : size;
        }
    }
}
