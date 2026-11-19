using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snapline.UI;

/// <summary>Draw the complete bitmap directly into its destination, without layout clipping
/// an original-size Image before applying a render transform.</summary>
internal sealed class PictureSurface : FrameworkElement
{
    private readonly ImageDrawing _bitmap = new() { Rect = new Rect(0, 0, 1, 1) };
    private readonly MatrixTransform _placement = new();
    private readonly DrawingGroup _drawing = new();
    public int DrawingPasses { get; private set; }
    public bool NativePresentation { get; set; }
    public BitmapSource? Source { get => _bitmap.ImageSource as BitmapSource; set => _bitmap.ImageSource = value; }

    public PictureSurface()
    {
        _drawing.Children.Add(_bitmap);
        _drawing.Transform = _placement;
    }

    public void SetBounds(Rect bounds)
    {
        // Retained drawing: update only the composition transform, without rebuilding
        // drawing commands or invalidating the element on each wheel animation frame.
        _placement.Matrix = new Matrix(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingPasses++;
        if (!NativePresentation) drawingContext.DrawDrawing(_drawing);
    }
}
