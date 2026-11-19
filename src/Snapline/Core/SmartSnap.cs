using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapline.Interop;

namespace Snapline.Core;

internal readonly record struct SnapWindow(IntPtr Handle, Rect Bounds);
internal readonly record struct SnapRegion(Rect Bounds, string Kind);

/// <summary>All coordinates are physical desktop pixels. Snapshot windows before overlays
/// appear; query accessibility only on a worker so a slow provider cannot block selection.</summary>
internal static class SmartSnap
{
    public static List<SnapWindow> SnapshotWindows()
    {
        var windows = new List<SnapWindow>();
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || Native.IsIconic(handle) || !Native.GetWindowRect(handle, out var bounds) || bounds.Width < 16 || bounds.Height < 16) return true;
            int cloaked = 0;
            Native.DwmGetWindowAttribute(handle, Native.DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (cloaked == 0) windows.Add(new SnapWindow(handle, new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static SnapWindow? WindowAt(IEnumerable<SnapWindow> windows, Point point)
    {
        foreach (var window in windows) if (window.Bounds.Contains(point)) return window;
        return null;
    }

    public static SnapRegion? Best(Point point, Rect screen, SnapWindow? window, IReadOnlyList<Rect> images, SnapRegion? control)
    {
        Rect limit = Rect.Intersect(screen, window?.Bounds ?? screen);
        if (limit.IsEmpty || !limit.Contains(point)) return null;
        if (control is { Kind: "图片" } image && Valid(image.Bounds, point, limit)) return image with { Bounds = Rect.Intersect(image.Bounds, limit) };
        // Prefer an outer image boundary over rectangles within the image itself.
        Rect? largest = null;
        double bestArea = 0, maxArea = limit.Width * limit.Height * 0.94;
        foreach (var candidate in images)
        {
            double area = candidate.Width * candidate.Height;
            if (area <= bestArea || area >= maxArea || !Valid(candidate, point, limit) || !limit.Contains(candidate)) continue;
            bestArea = area;
            largest = candidate;
        }
        if (largest is { } edge) return new SnapRegion(edge, "图片边缘");
        if (control is { } child && Valid(child.Bounds, point, limit)) return child with { Bounds = Rect.Intersect(child.Bounds, limit) };
        return new SnapRegion(limit, window is null ? "屏幕" : "窗口");
    }

    private static bool Valid(Rect rect, Point point, Rect limit) => !rect.IsEmpty && rect.Width >= 12 && rect.Height >= 12 && rect.Contains(point) && rect.IntersectsWith(limit);

    public static SnapRegion? AccessibleAt(SnapWindow window, Point point)
    {
        try
        {
            var current = AutomationElement.FromHandle(window.Handle);
            var walker = TreeWalker.ControlViewWalker; var clock = Stopwatch.StartNew();
            SnapRegion? selected = null;
            for (int depth = 0; depth < 16 && clock.ElapsedMilliseconds < 100; depth++)
            {
                AutomationElement? next = null;
                var child = walker.GetFirstChild(current);
                for (int count = 0; child is not null && count < 100 && clock.ElapsedMilliseconds < 100; count++, child = walker.GetNextSibling(child))
                {
                    var info = child.Current; var rect = info.BoundingRectangle;
                    if (info.IsOffscreen || rect.IsEmpty || !rect.Contains(point) || rect.Width < 12 || rect.Height < 12) continue;
                    next = child;
                    if (info.ControlType == ControlType.Image) return new SnapRegion(rect, "图片");
                    if (info.ControlType != ControlType.Text && info.ControlType != ControlType.Document && rect.Width * rect.Height < window.Bounds.Width * window.Bounds.Height * 0.97)
                        selected = new SnapRegion(rect, "控件");
                    break;
                }
                if (next is null) break;
                current = next;
            }
            return selected;
        }
        catch { return null; }
    }

    public static IReadOnlyList<Rect> ImageEdges(BitmapSource screen, Point origin)
    {
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(screen.PixelWidth, screen.PixelHeight) / 1800.0));
        BitmapSource bitmap = step == 1 ? screen : new TransformedBitmap(screen, new ScaleTransform(1.0 / step, 1.0 / step));
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height]; converted.CopyPixels(pixels, stride, 0);
        bool Edge(int x, int y, int dx, int dy)
        {
            int a = y * stride + x * 4, b = (y + dy) * stride + (x + dx) * 4;
            return Math.Abs(pixels[a] - pixels[b]) + Math.Abs(pixels[a + 1] - pixels[b + 1]) + Math.Abs(pixels[a + 2] - pixels[b + 2]) >= 75;
        }
        var lines = new List<(int Y, int Left, int Right)>();
        for (int y = 1; y < height; y++)
        {
            int start = -1, last = -1;
            for (int x = 1; x < width; x++)
            {
                if (Edge(x, y, 0, -1)) { if (start < 0) start = x; last = x; }
                if (start >= 0 && (x - last > 3 || x == width - 1))
                {
                    if (last - start >= 28) lines.Add((y, start, last + 1));
                    start = -1;
                }
            }
        }
        bool Side(int x, int top, int bottom)
        {
            if (x <= 0 || x >= width) return false;
            int hits = 0, gap = 0, maxGap = 0;
            for (int y = top; y < bottom; y++)
            {
                bool edge = Edge(x, y, -1, 0) || (x + 1 < width && Edge(x + 1, y, -1, 0)) || (x > 1 && Edge(x - 1, y, -1, 0));
                if (edge) { hits++; gap = 0; } else { gap++; maxGap = Math.Max(gap, maxGap); }
            }
            return hits >= (bottom - top) * 0.65 && maxGap <= Math.Max(5, (bottom - top) / 14);
        }
        var regions = new List<Rect>(); var budget = Stopwatch.StartNew();
        // Quantized extents tolerate a few antialiased border pixels.
        foreach (var group in lines.GroupBy(l => (l.Left / 4, l.Right / 4)))
        {
            var edges = group.OrderBy(l => l.Y).ToArray();
            for (int first = 0; first < edges.Length - 1 && regions.Count < 256; first++)
            {
                var top = edges[first];
                for (int last = edges.Length - 1, tried = 0; last > first && tried < 32; last--, tried++)
                {
                    var bottom = edges[last];
                    int w = top.Right - top.Left, h = bottom.Y - top.Y;
                    if (h < 24 || w * h < 1200 || w > h * 18 || h > w * 18) continue;
                    if (Math.Abs(top.Left - bottom.Left) > 3 || Math.Abs(top.Right - bottom.Right) > 3) continue;
                    if (!Side(top.Left, top.Y, bottom.Y) || !Side(top.Right, top.Y, bottom.Y)) continue;
                    regions.Add(new Rect(origin.X + top.Left * step, origin.Y + top.Y * step, w * step, h * step)); break;
                }
                if (budget.ElapsedMilliseconds > 160) break;
            }
            if (budget.ElapsedMilliseconds > 160 || regions.Count >= 256) break;
        }
        return regions.Distinct().OrderByDescending(r => r.Width * r.Height).ToArray();
    }
}
