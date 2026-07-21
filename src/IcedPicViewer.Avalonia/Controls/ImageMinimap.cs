// Copyright (c) IcedPicViewer. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace IcedPicViewer.Avalonia.Controls;

/// <summary>
/// Small overview of a large image with a viewport rectangle. Click or drag
/// to pan the bound <see cref="TargetScrollViewer"/>.
/// </summary>
public class ImageMinimap : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageMinimap, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<ScrollViewer?> TargetScrollViewerProperty =
        AvaloniaProperty.Register<ImageMinimap, ScrollViewer?>(nameof(TargetScrollViewer));

    private ScrollViewer? _subscribed;
    private bool _dragging;

    static ImageMinimap()
    {
        AffectsRender<ImageMinimap>(SourceProperty);
        SourceProperty.Changed.AddClassHandler<ImageMinimap>((m, _) => m.InvalidateVisual());
        TargetScrollViewerProperty.Changed.AddClassHandler<ImageMinimap>((m, e) => m.OnTargetChanged(e));
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ScrollViewer? TargetScrollViewer
    {
        get => GetValue(TargetScrollViewerProperty);
        set => SetValue(TargetScrollViewerProperty, value);
    }

    private void OnTargetChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.ScrollChanged -= OnScrollChanged;
            _subscribed = null;
        }

        if (e.NewValue is ScrollViewer sv)
        {
            sv.ScrollChanged += OnScrollChanged;
            _subscribed = sv;
        }

        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.ScrollChanged -= OnScrollChanged;
            _subscribed = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        PanTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        PanTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void PanTo(Point p)
    {
        var bmp = Source;
        var sv = TargetScrollViewer;
        if (bmp is null || sv is null) return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var (drawRect, scale) = GetDrawRect(bmp.PixelSize, bounds.Size);
        if (drawRect.Width <= 0 || drawRect.Height <= 0) return;

        var lx = (p.X - drawRect.X) / scale;
        var ly = (p.Y - drawRect.Y) / scale;

        var imgW = bmp.PixelSize.Width;
        var imgH = bmp.PixelSize.Height;
        var viewW = sv.Viewport.Width;
        var viewH = sv.Viewport.Height;

        var ox = lx - viewW / 2;
        var oy = ly - viewH / 2;
        ox = Math.Clamp(ox, 0, Math.Max(0, imgW - viewW));
        oy = Math.Clamp(oy, 0, Math.Max(0, imgH - viewH));

        sv.Offset = new Vector(ox, oy);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bmp = Source;
        if (bmp is null) return;

        var bounds = Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1) return;

        var (drawRect, scale) = GetDrawRect(bmp.PixelSize, bounds.Size);

        context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), bounds);
        context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), drawRect);
        context.DrawRectangle(null, new Pen(Brushes.White, 1), drawRect);

        var sv = TargetScrollViewer;
        if (sv is null) return;

        var vx = sv.Offset.X * scale + drawRect.X;
        var vy = sv.Offset.Y * scale + drawRect.Y;
        var vw = Math.Max(4, sv.Viewport.Width * scale);
        var vh = Math.Max(4, sv.Viewport.Height * scale);
        var viewRect = new Rect(vx, vy, vw, vh);
        viewRect = viewRect.Intersect(drawRect);
        if (viewRect.Width > 0 && viewRect.Height > 0)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 180, 255)), viewRect);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0, 180, 255)), 1.5), viewRect);
        }
    }

    private static (Rect drawRect, double scale) GetDrawRect(PixelSize pixelSize, Size box)
    {
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0 || box.Width <= 0 || box.Height <= 0)
            return (default, 1);

        var scale = Math.Min(box.Width / pixelSize.Width, box.Height / pixelSize.Height);
        var w = pixelSize.Width * scale;
        var h = pixelSize.Height * scale;
        var x = (box.Width - w) / 2;
        var y = (box.Height - h) / 2;
        return (new Rect(x, y, w, h), scale);
    }
}
