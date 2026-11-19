using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tool = Snapline.UI.MarkupWindow.Tool;
using ShapeMark = Snapline.UI.MarkupWindow.ShapeMark;

namespace Snapline.UI;

// Marks stay in selection coordinates. Moving the selection translates the drawing
// without rebuilding it, except for mosaics that need to sample the new background.
internal sealed class CaptureMarkup(BitmapSource screen)
{
    private const double Thickness = 3;
    private static readonly Color InkColor = Color.FromRgb(238, 54, 62);
    private readonly List<object> _marks = new();
    private ShapeMark? _shape;
    private Stroke? _stroke;
    private BitmapSource? _pixelated;

    internal bool CanUndo => _marks.Count > 0;
    internal bool HasMosaic => _marks.Any(mark => mark is ShapeMark { Kind: Tool.Blur });

    internal void Begin(Tool tool, Point at)
    {
        if (tool == Tool.Pen)
        {
            _stroke = new Stroke(new StylusPointCollection { new StylusPoint(at.X, at.Y) },
                new DrawingAttributes { Color = InkColor, Width = Thickness, Height = Thickness });
        }
        else _shape = new ShapeMark(tool, at, at, InkColor, Thickness);
    }

    internal void Move(Point at)
    {
        if (_shape is { } shape) _shape = shape with { To = at };
        if (_stroke is { } stroke)
        {
            var last = stroke.StylusPoints[^1];
            if (last.X != at.X || last.Y != at.Y) stroke.StylusPoints.Add(new StylusPoint(at.X, at.Y));
        }
    }

    internal void End(Point at)
    {
        Move(at);
        if (_shape is { } shape && (shape.To - shape.From).Length >= 2) _marks.Add(shape);
        if (_stroke is { } stroke) _marks.Add(stroke);
        CancelStroke();
    }

    internal void AddText(Point at, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _marks.Add(new ShapeMark(Tool.Text, at, at, InkColor, Thickness, text));
    }

    internal void CancelStroke() { _stroke = null; _shape = null; }

    internal void Undo()
    {
        if (CanUndo) _marks.RemoveAt(_marks.Count - 1);
    }

    internal void Clear()
    {
        CancelStroke();
        _marks.Clear();
    }

    internal void Draw(DrawingContext dc, Int32Rect region, DpiScale dpi)
    {
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, region.Width / sx, region.Height / sy)));
        var background = new Rect(-region.X / sx, -region.Y / sy, screen.PixelWidth / sx, screen.PixelHeight / sy);
        foreach (var mark in _marks)
            if (mark is ShapeMark { Kind: Tool.Blur } mosaic) DrawMosaic(mosaic);
        if (_shape is { Kind: Tool.Blur } liveMosaic) DrawMosaic(liveMosaic);

        foreach (var mark in _marks)
        {
            if (mark is ShapeMark { Kind: not Tool.Blur } shape) MarkupWindow.DrawMark(dc, shape, null, background);
            if (mark is Stroke stroke) stroke.Draw(dc);
        }
        if (_shape is { Kind: not Tool.Blur } liveShape) MarkupWindow.DrawMark(dc, liveShape, null, background);
        _stroke?.Draw(dc);
        dc.Pop();

        void DrawMosaic(ShapeMark mosaic)
        {
            _pixelated ??= MarkupWindow.Pixelate(screen);
            MarkupWindow.DrawMark(dc, mosaic, _pixelated, background);
        }
    }

    internal BitmapSource Render(Int32Rect region, DpiScale dpi)
    {
        var crop = new CroppedBitmap(screen, region);
        crop.Freeze();
        if (!CanUndo) return crop;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, region.Width, region.Height));
            dc.PushTransform(new ScaleTransform(dpi.DpiScaleX, dpi.DpiScaleY));
            Draw(dc, region, dpi);
            dc.Pop();
        }
        var result = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}

internal sealed class CaptureDrawingSurface : FrameworkElement
{
    internal DrawingVisual Drawing { get; } = new();

    internal CaptureDrawingSurface()
    {
        IsHitTestVisible = false;
        AddVisualChild(Drawing);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? Drawing : throw new ArgumentOutOfRangeException(nameof(index));
}
