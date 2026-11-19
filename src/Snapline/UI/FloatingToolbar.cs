using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Snapline.Interop;

namespace Snapline.UI;

/// <summary>A separate owned window: editor controls never add a frame to the image.</summary>
public sealed class FloatingToolbar : Window
{
    private static readonly HashSet<FloatingToolbar> Bars = new();
    private static readonly HashSet<FloatingToolbar> CaptureHidden = new();
    private readonly Window _anchor;
    private bool _closed;
    public FrameworkElement Surface { get; }

    public FloatingToolbar(Window anchor, UIElement content)
    {
        _anchor = anchor;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var capsule = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 252)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7),
            Child = content,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.23 },
        };
        Surface = capsule;
        Content = new Border { Padding = new Thickness(8), Child = capsule };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
        };
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        _anchor.LocationChanged += AnchorMoved;
        _anchor.SizeChanged += AnchorSized;
        _anchor.Closed += AnchorClosed;
        Closed += (_, _) =>
        {
            _closed = true; Bars.Remove(this); CaptureHidden.Remove(this);
            _anchor.LocationChanged -= AnchorMoved; _anchor.SizeChanged -= AnchorSized; _anchor.Closed -= AnchorClosed;
        };
        Bars.Add(this);
    }

    public void ShowBar()
    {
        if (_closed) return;
        if (new WindowInteropHelper(_anchor).Handle == IntPtr.Zero) return;
        Owner ??= _anchor;
        if (!IsVisible)
        {
            // Place the first frame before showing: never flash at the default origin.
            var content = (FrameworkElement)Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var monitor = Displays.OfWindow(new WindowInteropHelper(_anchor).Handle) ?? Displays.UnderPointer();
            if (Native.GetWindowRect(new WindowInteropHelper(_anchor).Handle, out var anchor))
            {
                var position = Position(anchor, monitor, (int)Math.Ceiling(content.DesiredSize.Width * monitor.Scale), (int)Math.Ceiling(content.DesiredSize.Height * monitor.Scale));
                Left = position.X / monitor.Scale; Top = position.Y / monitor.Scale;
            }
            Show();
        }
        Reposition();
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
    private void AnchorMoved(object? sender, EventArgs e) => Reposition();
    private void AnchorSized(object sender, SizeChangedEventArgs e) => Reposition();
    private void AnchorClosed(object? sender, EventArgs e) { if (!_closed) Close(); }

    public void Reposition()
    {
        if (_closed || !IsVisible || !Native.GetWindowRect(new WindowInteropHelper(_anchor).Handle, out var rect)) return;
        var monitor = Displays.OfWindow(new WindowInteropHelper(_anchor).Handle) ?? Displays.UnderPointer();
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var position = Position(rect, monitor, width, height);
        var handle = new WindowInteropHelper(this).Handle;
        if (Native.GetWindowRect(handle, out var current) && current.Left == position.X && current.Top == position.Y) return;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, position.X, position.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static Native.POINT Position(Native.RECT rect, Display monitor, int width, int height)
    {
        int x = Math.Clamp(rect.Left + (rect.Width - width) / 2, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        int y = rect.Bottom + 3;
        if (y + height > monitor.Work.Bottom) y = rect.Top - height - 3;
        return new Native.POINT(x, Math.Clamp(y, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height)));
    }

    public static void HideForCapture()
    {
        foreach (var bar in Bars.Where(b => b.IsVisible).ToList()) { CaptureHidden.Add(bar); bar.Hide(); }
    }
    public static void RestoreAfterCapture()
    {
        foreach (var bar in CaptureHidden.ToList()) if (!bar._closed && bar._anchor.IsVisible) bar.ShowBar();
        CaptureHidden.Clear();
    }
    internal static int VisibleCount => Bars.Count(b => b.IsVisible);
}
