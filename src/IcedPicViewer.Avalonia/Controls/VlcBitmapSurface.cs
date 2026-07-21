// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace IcedPicViewer.Avalonia.Controls;

/// <summary>
/// Software-rendered video surface for Avalonia 12+. Avoids
/// <c>LibVLCSharp.Avalonia.VideoView</c>, which crashes on Avalonia 12
/// (<c>Visual.get_VisualRoot()</c> MissingMethodException).
/// </summary>
public sealed class VlcBitmapSurface : Control, IDisposable
{
    public static readonly StyledProperty<MediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<VlcBitmapSurface, MediaPlayer?>(nameof(MediaPlayer));

    private MediaPlayer? _player;
    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;
    private GCHandle _bufferHandle;
    private int _width;
    private int _height;
    private int _pitch;
    private bool _callbacksSet;
    private bool _disposed;
    private readonly object _frameLock = new();

    static VlcBitmapSurface()
    {
        AffectsRender<VlcBitmapSurface>(MediaPlayerProperty);
        MediaPlayerProperty.Changed.AddClassHandler<VlcBitmapSurface>((s, e) => s.OnPlayerChanged(e));
    }

    public MediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private void OnPlayerChanged(AvaloniaPropertyChangedEventArgs e)
    {
        DetachCallbacks();
        _player = e.NewValue as MediaPlayer;
        AttachCallbacks();
        InvalidateVisual();
    }

    private void AttachCallbacks()
    {
        if (_player is null || _callbacksSet) return;
        try
        {
            _player.SetVideoFormatCallbacks(VideoFormat, Cleanup);
            _player.SetVideoCallbacks(Lock, null, Display);
            _callbacksSet = true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcBitmapSurface.AttachCallbacks: {ex.Message}");
        }
    }

    private void DetachCallbacks()
    {
        if (_player is null || !_callbacksSet) return;
        try
        {
            // Passing null clears callbacks on LibVLCSharp MediaPlayer.
            _player.SetVideoCallbacks(null!, null, null);
            _player.SetVideoFormatCallbacks(null!, null);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcBitmapSurface.DetachCallbacks: {ex.Message}");
        }
        _callbacksSet = false;
        FreeBuffer();
    }

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 = BGRA/RGBA 32-bit packed
        var fourcc = "RV32"u8;
        Marshal.Copy(fourcc.ToArray(), 0, chroma, 4);

        _width = (int)width;
        _height = (int)height;
        _pitch = _width * 4;
        pitches = (uint)_pitch;
        lines = (uint)_height;

        FreeBuffer();
        _buffer = new byte[_pitch * _height];
        _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _bitmap = new WriteableBitmap(
                    new PixelSize(_width, _height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                InvalidateVisual();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VlcBitmapSurface bitmap create: {ex.Message}");
            }
        });

        return 1; // one picture buffer
    }

    private void Cleanup(ref IntPtr opaque) => FreeBuffer();

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        if (!_bufferHandle.IsAllocated)
            return IntPtr.Zero;
        Marshal.WriteIntPtr(planes, _bufferHandle.AddrOfPinnedObject());
        return IntPtr.Zero;
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        byte[]? snapshot;
        int w, h, pitch;
        lock (_frameLock)
        {
            if (_buffer is null || _width <= 0 || _height <= 0) return;
            // Copy so UI thread can read without racing the next Lock.
            snapshot = new byte[_buffer.Length];
            Buffer.BlockCopy(_buffer, 0, snapshot, 0, _buffer.Length);
            w = _width;
            h = _height;
            pitch = _pitch;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_bitmap is null
                    || _bitmap.PixelSize.Width != w
                    || _bitmap.PixelSize.Height != h)
                {
                    _bitmap = new WriteableBitmap(
                        new PixelSize(w, h),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Opaque);
                }

                using (var fb = _bitmap.Lock())
                {
                    var dstPitch = fb.RowBytes;
                    if (dstPitch == pitch)
                    {
                        Marshal.Copy(snapshot, 0, fb.Address, Math.Min(snapshot.Length, dstPitch * h));
                    }
                    else
                    {
                        for (var y = 0; y < h; y++)
                        {
                            Marshal.Copy(snapshot, y * pitch, fb.Address + y * dstPitch, pitch);
                        }
                    }
                }

                InvalidateVisual();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VlcBitmapSurface.Display: {ex.Message}");
            }
        });
    }

    private void FreeBuffer()
    {
        lock (_frameLock)
        {
            if (_bufferHandle.IsAllocated)
                _bufferHandle.Free();
            _buffer = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bmp = _bitmap;
        if (bmp is null) return;

        var dest = Bounds;
        if (dest.Width <= 0 || dest.Height <= 0) return;

        // Letterbox to preserve aspect ratio.
        var scale = Math.Min(dest.Width / bmp.PixelSize.Width, dest.Height / bmp.PixelSize.Height);
        var w = bmp.PixelSize.Width * scale;
        var h = bmp.PixelSize.Height * scale;
        var x = dest.X + (dest.Width - w) / 2;
        var y = dest.Y + (dest.Height - h) / 2;
        context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), new Rect(x, y, w, h));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachCallbacks();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachCallbacks();
        _bitmap = null;
    }
}
