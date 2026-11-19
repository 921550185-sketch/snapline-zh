using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snapline.Core;
using Snapline.Interop;
using Line = Snapline.Core.Line;
using Tool = Snapline.UI.MarkupWindow.Tool;

namespace Snapline.UI;

/// <summary>Capture each monitor before showing overlays; crop in physical pixels.</summary>
public sealed partial class CaptureWindow : Window
{
    private enum Gesture { None, Select, Move, Draw }

    private static readonly List<CaptureWindow> Active = new();
    private static Action? _finished;
    private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(137, 0, 0, 0)));
    private static readonly Pen Outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0, 204, 137))), 1.5));
    private static readonly Brush SizeBackground = Frozen(new SolidColorBrush(Color.FromArgb(205, 25, 28, 31)));
    private readonly Display _monitor;
    private readonly BitmapSource _screen;
    private readonly Line _line;
    private readonly Canvas _canvas;
    private readonly CaptureDrawingSurface _frame = new();
    private readonly CaptureDrawingSurface _marks = new();
    private readonly CaptureMarkup _markup;
    private readonly TranslateTransform _marksOffset = new();
    private Int32Rect _region, _gestureRegion, _displayedRegion;
    private Point _start, _pointer, _startPixel;
    private Gesture _gesture;
    private bool _dragging, _closed, _square, _frameDrawn;
    private readonly IReadOnlyList<SnapWindow> _windows;
    private IReadOnlyList<Rect> _imageRegions = Array.Empty<Rect>();
    private readonly DispatcherTimer _hoverTimer;
    private Task? _controlTask;
    private SnapRegion? _control, _hover, _pressedRegion;
    private IntPtr _controlWindow;
    private Point _controlPoint;
    private long _lastControlQuery;
    private FormattedText? _sizeLabel;
    private DpiScale _dpi;
    internal Rect? HoverBounds => _hover?.Bounds;
    internal Int32Rect SelectionPixels => _region;
    internal string? DisplayedSize => _sizeLabel?.Text;
    public static bool IsCapturing => Active.Count > 0;

    internal CaptureWindow(Line line, Display monitor, BitmapSource screen, IReadOnlyList<SnapWindow> windows)
    {
        _line = line; _monitor = monitor; _screen = screen; _windows = windows;
        _markup = new CaptureMarkup(screen);
        _dpi = new DpiScale(monitor.Scale, monitor.Scale);
        Title = "Snapline 截图";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.Left / monitor.Scale; Top = monitor.Bounds.Top / monitor.Scale;
        Width = monitor.Bounds.Width / monitor.Scale; Height = monitor.Bounds.Height / monitor.Scale;
        Cursor = Cursors.Cross; Background = Brushes.Black;
        _canvas = new Canvas { Background = Brushes.Transparent, Focusable = true };
        var backdrop = new Image { Source = screen, Stretch = Stretch.Fill, IsHitTestVisible = false };
        SizeChanged += (_, _) => { backdrop.Width = ActualWidth; backdrop.Height = ActualHeight; };
        _canvas.Children.Add(backdrop);
        _marks.Drawing.Transform = _marksOffset;
        RenderOptions.SetBitmapScalingMode(_marks.Drawing, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(_marks);
        _canvas.Children.Add(_frame);
        _toolbar = BuildToolbar();
        _canvas.Children.Add(_toolbar);
        Content = _canvas;
        SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST,
            monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height, Native.SWP_SHOWWINDOW);
        PreviewKeyDown += CaptureKeyDown;
        PreviewKeyUp += (_, e) => UpdateSquareKey(e);
        MouseRightButtonDown += (_, _) => Finish();
        MouseLeftButtonDown += (_, e) =>
        {
            if (_toolbar.IsMouseOver) return;
            var modifiers = Keyboard.Modifiers;
            BeginSelection(e.GetPosition(_canvas), modifiers.HasFlag(ModifierKeys.Alt), modifiers.HasFlag(ModifierKeys.Shift));
            if (_gesture != Gesture.None) CaptureMouse();
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            var point = e.GetPosition(_canvas);
            if (_gesture != Gesture.None) MoveSelection(point);
            else if (_region.IsEmpty) RefreshHover();
            else UpdateCursor(point);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_gesture == Gesture.None) return;
            CompleteSelection(e.GetPosition(_canvas));
            ReleaseMouseCapture();
            e.Handled = true;
        };
        LostMouseCapture += (_, _) =>
        {
            if (_gesture == Gesture.None || IsMouseCaptured) return;
            _gesture = Gesture.None;
            _markup.CancelStroke();
            DrawMarks();
            ShowFrame(_region);
        };
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _hoverTimer.Tick += (_, _) => RefreshHover();
        Loaded += (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this);
            ShowFrame(_region);
            _hoverTimer.Start();
            RefreshHover();
        };
        Closed += (_, _) => { _closed = true; _hoverTimer.Stop(); };
        _ = Task.Run(() => SmartSnap.ImageEdges(screen, new Point(monitor.Bounds.Left, monitor.Bounds.Top))).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Dispatcher.BeginInvoke(() => { if (!_closed) { _imageRegions = task.Result; RefreshHover(); } });
        }, TaskScheduler.Default);
    }

    private bool CanHover => !_closed && _gesture == Gesture.None && _region.IsEmpty
        && !Active.Any(window => window != this && (!window._region.IsEmpty || window._gesture != Gesture.None));

    private void RefreshHover()
    {
        if (!CanHover) return;
        var cursor = Displays.Cursor(); var point = new Point(cursor.X, cursor.Y);
        bool manual = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        HoverAt(point, manual);
        if (manual || _hover is null) return;
        var window = SmartSnap.WindowAt(_windows, point);
        if (window is null || _controlTask is { IsCompleted: false }) return;
        if (_controlWindow == window.Value.Handle && (point - _controlPoint).Length < 3) return;
        if (Environment.TickCount64 - _lastControlQuery < 80) return;
        var requested = window.Value;
        _control = null; _controlPoint = point; _controlWindow = requested.Handle; _lastControlQuery = Environment.TickCount64;
        _controlTask = Task.Run(() => SmartSnap.AccessibleAt(requested, point)).ContinueWith(task =>
        {
            var result = task.IsCompletedSuccessfully ? task.Result : null;
            Dispatcher.BeginInvoke(() =>
            {
                if (!CanHover) return;
                var latest = Displays.Cursor();
                if (SmartSnap.WindowAt(_windows, new Point(latest.X, latest.Y))?.Handle != requested.Handle || (new Point(latest.X, latest.Y) - point).Length > 3) return;
                _control = result; HoverAt(point, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            });
        }, TaskScheduler.Default);
    }

    internal void HoverAt(Point point, bool manual = false)
    {
        if (!CanHover) return;
        var screen = new Rect(_monitor.Bounds.Left, _monitor.Bounds.Top, _monitor.Bounds.Width, _monitor.Bounds.Height);
        if (manual || !screen.Contains(point))
        {
            _hover = null;
            ShowFrame(Int32Rect.Empty);
            return;
        }
        var window = SmartSnap.WindowAt(_windows, point);
        var control = window?.Handle == _controlWindow && (point - _controlPoint).Length < 3 ? _control : null;
        _hover = SmartSnap.Best(point, screen, window, _imageRegions, control);
        ShowFrame(_hover is { } region ? SnapPixels(region.Bounds) : Int32Rect.Empty);
    }

    internal void BeginSelection(Point local, bool manual = false, bool square = false)
    {
        CommitText();
        _canvas.Focus();
        _start = _pointer = local;
        _startPixel = ToPixel(local);
        if (!_region.IsEmpty && ToLocal(_region).Contains(local))
        {
            _gestureRegion = _region;
            if (_tool == Tool.Text)
            {
                BeginText(local);
                return;
            }
            _gesture = _tool is null ? Gesture.Move : Gesture.Draw;
            if (_tool is { } tool) _markup.Begin(tool, MarkPoint(local));
            return;
        }

        foreach (var window in Active)
            if (window != this) window.ClearSelection();
        bool replaceSelection = !_region.IsEmpty;
        if (replaceSelection) ClearSelection();
        HoverAt(PointToScreen(local), manual || replaceSelection);
        _pressedRegion = _hover;
        _gesture = Gesture.Select;
        _dragging = false;
        _square = square;
    }

    internal void MoveSelection(Point local)
    {
        _pointer = local;
        switch (_gesture)
        {
            case Gesture.Select:
                _dragging |= (local - _start).LengthSquared >= 9;
                if (_dragging) ShowFrame(DraggedPixels(local));
                break;
            case Gesture.Move:
                var delta = ToPixel(local) - _startPixel;
                var region = _gestureRegion;
                region.X = Math.Clamp(region.X + (int)delta.X, 0, _screen.PixelWidth - region.Width);
                region.Y = Math.Clamp(region.Y + (int)delta.Y, 0, _screen.PixelHeight - region.Height);
                if (region == _region) break;
                _region = region;
                UpdateMarkOffset();
                if (_markup.HasMosaic) DrawMarks();
                ShowFrame(region);
                PlaceToolbar(region);
                break;
            case Gesture.Draw:
                _markup.Move(MarkPoint(local));
                DrawMarks();
                break;
        }
    }

    internal void CompleteSelection(Point local)
    {
        MoveSelection(local);
        var gesture = _gesture;
        _gesture = Gesture.None;
        if (gesture == Gesture.Draw)
        {
            _markup.End(MarkPoint(local));
            DrawMarks();
            return;
        }
        if (gesture != Gesture.Select) return;
        if (!_dragging && _pressedRegion is { } snap) _region = SnapPixels(snap.Bounds);
        else if (_dragging) _region = DraggedPixels(local);
        _pressedRegion = null;
        if (_region.IsEmpty) { ShowFrame(_region); RefreshHover(); return; }
        ShowFrame(_region);
        UpdateMarkOffset();
        ShowToolbar(_region);
        UpdateCursor(local);
    }

    internal void SetSquare(bool square)
    {
        if (_gesture != Gesture.Select || _square == square) return;
        _square = square;
        if (_dragging) ShowFrame(DraggedPixels(_pointer));
    }

    private void UpdateSquareKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.LeftShift or Key.RightShift)) return;
        SetSquare(e.RoutedEvent == Keyboard.PreviewKeyDownEvent
            || Keyboard.IsKeyDown(key == Key.LeftShift ? Key.RightShift : Key.LeftShift));
    }

    private Int32Rect DraggedPixels(Point local)
    {
        var end = ToPixel(local);
        if (_square)
        {
            var delta = end - _startPixel;
            int signX = delta.X < 0 ? -1 : delta.X > 0 || _startPixel.X < _screen.PixelWidth / 2.0 ? 1 : -1;
            int signY = delta.Y < 0 ? -1 : delta.Y > 0 || _startPixel.Y < _screen.PixelHeight / 2.0 ? 1 : -1;
            double availableX = signX > 0 ? _screen.PixelWidth - _startPixel.X : _startPixel.X;
            double availableY = signY > 0 ? _screen.PixelHeight - _startPixel.Y : _startPixel.Y;
            double side = Math.Min(Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)), Math.Min(availableX, availableY));
            end = _startPixel + new Vector(side * signX, side * signY);
        }
        return PixelBounds(_startPixel, end);
    }

    private Int32Rect SnapPixels(Rect rect)
    {
        var origin = new Vector(_monitor.Bounds.Left, _monitor.Bounds.Top);
        return PixelBounds(rect.TopLeft - origin, rect.BottomRight - origin);
    }

    private Int32Rect PixelBounds(Point a, Point b)
    {
        int x = Math.Clamp((int)Math.Round(Math.Min(a.X, b.X)), 0, _screen.PixelWidth);
        int y = Math.Clamp((int)Math.Round(Math.Min(a.Y, b.Y)), 0, _screen.PixelHeight);
        int right = Math.Clamp((int)Math.Round(Math.Max(a.X, b.X)), 0, _screen.PixelWidth);
        int bottom = Math.Clamp((int)Math.Round(Math.Max(a.Y, b.Y)), 0, _screen.PixelHeight);
        return right - x < 2 || bottom - y < 2 ? Int32Rect.Empty : new Int32Rect(x, y, right - x, bottom - y);
    }

    private Point ToPixel(Point local) => new(
        Math.Round(local.X * _dpi.DpiScaleX), Math.Round(local.Y * _dpi.DpiScaleY));

    private Rect ToLocal(Int32Rect rect) => new(rect.X / _dpi.DpiScaleX, rect.Y / _dpi.DpiScaleY,
        rect.Width / _dpi.DpiScaleX, rect.Height / _dpi.DpiScaleY);

    private Point MarkPoint(Point local)
    {
        var bounds = ToLocal(_region);
        return new Point(Math.Clamp(local.X - bounds.X, 0, bounds.Width), Math.Clamp(local.Y - bounds.Y, 0, bounds.Height));
    }

    private void ShowFrame(Int32Rect region)
    {
        if (_frameDrawn && region == _displayedRegion) return;
        _frameDrawn = true;
        double width = _screen.PixelWidth / _dpi.DpiScaleX, height = _screen.PixelHeight / _dpi.DpiScaleY;
        using var dc = _frame.Drawing.RenderOpen();
        if (region.IsEmpty)
        {
            dc.DrawRectangle(Shade, null, new Rect(0, 0, width, height));
            _sizeLabel = null;
        }
        else
        {
            var rect = ToLocal(region);
            dc.DrawRectangle(Shade, null, new Rect(0, 0, width, rect.Top));
            dc.DrawRectangle(Shade, null, new Rect(0, rect.Bottom, width, Math.Max(0, height - rect.Bottom)));
            dc.DrawRectangle(Shade, null, new Rect(0, rect.Top, rect.Left, rect.Height));
            dc.DrawRectangle(Shade, null, new Rect(rect.Right, rect.Top, Math.Max(0, width - rect.Right), rect.Height));
            dc.DrawRectangle(null, Outline, rect);

            if (_sizeLabel is null || region.Width != _displayedRegion.Width || region.Height != _displayedRegion.Height)
                _sizeLabel = new FormattedText($"{region.Width} × {region.Height}", System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, _dpi.PixelsPerDip);
            double x = Math.Clamp(rect.Left + 3, 0, Math.Max(0, width - _sizeLabel.Width - 10));
            double y = rect.Top >= 24 ? rect.Top - 24 : rect.Top + 3;
            y = Math.Min(y, Math.Max(0, height - _sizeLabel.Height - 4));
            dc.DrawRoundedRectangle(SizeBackground, null, new Rect(x, y, _sizeLabel.Width + 10, _sizeLabel.Height + 4), 3, 3);
            dc.DrawText(_sizeLabel, new Point(x + 5, y + 2));
        }
        _displayedRegion = region;
    }

    private void ClearSelection()
    {
        _region = Int32Rect.Empty;
        _hover = _pressedRegion = null;
        _toolbar.Visibility = Visibility.Collapsed;
        DropText();
        _markup.Clear();
        PickTool(null);
        DrawMarks();
        ShowFrame(_region);
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    public static void Begin(Line line, Action? finished = null)
    {
        if (Active.Count > 0) return;
        CaptureResult.Clear();
        _finished = finished;
        try
        {
            // Every monitor is read before any capture overlay is made visible.
            var windows = SmartSnap.SnapshotWindows();
            var captures = Displays.All().Select(m => (monitor: m, image: ReadScreen(m))).ToList();
            foreach (var (monitor, image) in captures) Active.Add(new CaptureWindow(line, monitor, image, windows));
            foreach (var overlay in Active) overlay.Show();
            Active.FirstOrDefault(w => w._monitor.Equals(Displays.UnderPointer()))?.Activate();
        }
        catch { Finish(); throw; }
    }

    internal static CaptureWindow ShowVerification(Line line, Display monitor, BitmapSource screen, IReadOnlyList<SnapWindow> windows, Action? finished = null)
    {
        _finished = finished;
        var overlay = new CaptureWindow(line, monitor, screen, windows); Active.Add(overlay); overlay.Show(); return overlay;
    }

    internal static BitmapSource ReadScreen(Display monitor) => ReadRegion(monitor.Bounds);
    internal static BitmapSource ReadRegion(Native.RECT region)
    {
        using var bitmap = new System.Drawing.Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var destination = graphics.GetHdc(); var desktop = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(destination, 0, 0, region.Width, region.Height, desktop, region.Left, region.Top, 0x40CC0020))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            finally { ReleaseDC(IntPtr.Zero, desktop); graphics.ReleaseHdc(destination); }
        }
        var handle = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally { Native.DeleteObject(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);

    private static Action? DetachFinish()
    {
        foreach (var window in Active.ToList()) window.Close();
        Active.Clear(); var finished = _finished; _finished = null; return finished;
    }
    private static void Finish() => DetachFinish()?.Invoke();
}
