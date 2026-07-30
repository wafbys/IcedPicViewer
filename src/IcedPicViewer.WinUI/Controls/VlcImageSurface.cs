// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace IcedPicViewer.Controls;

/// <summary>
/// Software-rendered LibVLC video surface for WinUI (Image + WriteableBitmap).
/// Same approach as Avalonia <c>VlcBitmapSurface</c> — no HWND VideoView.
/// </summary>
public sealed class VlcImageSurface : UserControl, IDisposable
{
    public static readonly DependencyProperty MediaPlayerProperty =
        DependencyProperty.Register(
            nameof(MediaPlayer),
            typeof(MediaPlayer),
            typeof(VlcImageSurface),
            new PropertyMetadata(null, OnMediaPlayerChanged));

    private readonly Image _image = new()
    {
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
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

    public VlcImageSurface()
    {
        Content = _image;
    }

    public MediaPlayer? MediaPlayer
    {
        get => (MediaPlayer?)GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    private static void OnMediaPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not VlcImageSurface surface) return;
        surface.DetachCallbacks();
        surface._player = e.NewValue as MediaPlayer;
        surface.AttachCallbacks();
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
            Trace.TraceError($"VlcImageSurface.AttachCallbacks: {ex.Message}");
        }
    }

    private void DetachCallbacks()
    {
        if (_player is null || !_callbacksSet) return;
        try
        {
            _player.SetVideoCallbacks(null!, null, null);
            _player.SetVideoFormatCallbacks(null!, null);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcImageSurface.DetachCallbacks: {ex.Message}");
        }
        _callbacksSet = false;
        FreeBuffer();
    }

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var fourcc = "RV32"u8;
        Marshal.Copy(fourcc.ToArray(), 0, chroma, 4);

        _width = (int)width;
        _height = (int)height;
        _pitch = _width * 4;
        pitches = (uint)_pitch;
        lines = (uint)_height;

        lock (_frameLock)
        {
            FreeBufferCore();
            _buffer = new byte[_pitch * _height];
            _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        }

        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                _bitmap = new WriteableBitmap(_width, _height);
                _image.Source = _bitmap;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VlcImageSurface bitmap create: {ex.Message}");
            }
        });

        return 1;
    }

    private void Cleanup(ref IntPtr opaque) => FreeBuffer();

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        lock (_frameLock)
        {
            if (!_bufferHandle.IsAllocated || _buffer is null)
                return IntPtr.Zero;
            Marshal.WriteIntPtr(planes, _bufferHandle.AddrOfPinnedObject());
            return IntPtr.Zero;
        }
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        byte[]? snapshot;
        int w, h, pitch;
        lock (_frameLock)
        {
            if (_buffer is null || _width <= 0 || _height <= 0) return;
            snapshot = new byte[_buffer.Length];
            System.Buffer.BlockCopy(_buffer, 0, snapshot, 0, _buffer.Length);
            w = _width;
            h = _height;
            pitch = _pitch;
        }

        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                {
                    _bitmap = new WriteableBitmap(w, h);
                    _image.Source = _bitmap;
                }

                using (var stream = _bitmap.PixelBuffer.AsStream())
                {
                    // WriteableBitmap expects BGRA; RV32 is typically BGRA on Windows.
                    if (pitch == w * 4)
                    {
                        stream.Write(snapshot, 0, Math.Min(snapshot.Length, pitch * h));
                    }
                    else
                    {
                        for (var y = 0; y < h; y++)
                            stream.Write(snapshot, y * pitch, w * 4);
                    }
                }

                _bitmap.Invalidate();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VlcImageSurface.Display: {ex.Message}");
            }
        });
    }

    private void FreeBuffer()
    {
        lock (_frameLock)
        {
            FreeBufferCore();
        }
    }

    private void FreeBufferCore()
    {
        if (_bufferHandle.IsAllocated)
            _bufferHandle.Free();
        _buffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachCallbacks();
        FreeBuffer();
        _image.Source = null;
        _bitmap = null;
    }
}
