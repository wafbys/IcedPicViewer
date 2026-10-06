// Copyright (c) IcedPicViewer. All rights reserved.

using System.Text;

namespace IcedPicViewer.Core.Media;

/// <summary>Encoding used for a persisted thumbnail payload.</summary>
public enum ThumbnailImageFormat : byte
{
    Png = 0,
    Jpeg = 1,
}

/// <summary>
/// Metadata persisted next to a thumbnail's encoded pixels. The original
/// (oriented) pixel size is stored so the viewer's clarity / reuse decisions
/// and the gallery InfoLine keep working from the cache alone, without
/// re-opening the source file.
/// </summary>
/// <param name="OriginalWidth">Oriented original width in pixels (0 when unknown).</param>
/// <param name="OriginalHeight">Oriented original height in pixels (0 when unknown).</param>
/// <param name="Duration">Video duration; null for images.</param>
/// <param name="Format">How <c>Payload</c> is encoded.</param>
public readonly record struct ThumbnailCacheHeader(
    int OriginalWidth,
    int OriginalHeight,
    TimeSpan? Duration,
    ThumbnailImageFormat Format);

/// <summary>
/// Little binary container for one persisted thumbnail:
/// <c>magic(4) | version(int) | width(int) | height(int) | durationTicks(long,
/// -1 = none) | format(byte) | payloadLength(int) | payload</c>.
///
/// <para>
/// Pure (no WIC): the WinUI disk cache owns the image encode/decode and uses
/// this type for framing, which keeps the read side guarded and unit-testable.
/// </para>
/// </summary>
public static class ThumbnailCacheFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("IPVT");
    public const int Version = 1;
    private const int MaxPayloadBytes = 256 * 1024 * 1024;

    public static void Write(Stream stream, in ThumbnailCacheHeader header, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);

        stream.Write(Magic);
        WriteInt32(stream, Version);
        WriteInt32(stream, header.OriginalWidth);
        WriteInt32(stream, header.OriginalHeight);
        WriteInt64(stream, header.Duration?.Ticks ?? -1L);
        stream.WriteByte((byte)header.Format);
        WriteInt32(stream, payload.Length);
        stream.Write(payload);
    }

    /// <summary>
    /// Reads a container. Returns false (never throws) on a bad magic/version,
    /// a truncated file or an implausible length, so the caller treats it as a
    /// cache miss.
    /// </summary>
    public static bool TryRead(Stream stream, out ThumbnailCacheHeader header, out byte[] payload)
    {
        header = default;
        payload = Array.Empty<byte>();
        if (stream is null) return false;

        try
        {
            Span<byte> magic = stackalloc byte[4];
            if (!ReadExactly(stream, magic) || !magic.SequenceEqual(Magic)) return false;
            if (ReadInt32(stream) != Version) return false;

            var width = ReadInt32(stream);
            var height = ReadInt32(stream);
            var durationTicks = ReadInt64(stream);
            var format = (ThumbnailImageFormat)stream.ReadByte();
            var length = ReadInt32(stream);

            if (length < 0 || length > MaxPayloadBytes) return false;
            if (width < 0 || height < 0) return false;
            if (format is not (ThumbnailImageFormat.Png or ThumbnailImageFormat.Jpeg)) return false;

            var bytes = new byte[length];
            if (!ReadExactly(stream, bytes)) return false;

            header = new ThumbnailCacheHeader(
                width,
                height,
                durationTicks >= 0 ? TimeSpan.FromTicks(durationTicks) : null,
                format);
            payload = bytes;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!ReadExactly(stream, buffer)) throw new EndOfStreamException();
        return BitConverter.ToInt32(buffer);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!ReadExactly(stream, buffer)) throw new EndOfStreamException();
        return BitConverter.ToInt64(buffer);
    }
}
