using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapline.Core;
using Snapline.Interop;

namespace Snapline.UI;

public sealed class PictureWindow : Window
{
    private static readonly List<PictureWindow> PinnedWindows = new();
    private static readonly List<HotKey> Keys = new();
    private static MessageWindow? _messages;
    private static PictureWindow? _target;
    private static bool _captureActive;
    internal static bool CaptureActive => _captureActive;
    public static long Revision { get; private set; }
    internal static Guid? ShortcutTargetId => _target?.ItemId;
    private readonly Pegged _item;
    private readonly Line _line;
    private readonly Point? _initialScreenPosition;
    private bool _positioned;
    private BitmapSource _original;
    private readonly PictureSurface _image;
    private readonly Grid _root;
    private FloatingToolbar? _previewBar;
    private double _zoom = 1, _targetZoom = 1;
    private int _angle;
    private Matrix _transform = Matrix.Identity;
    private Point? _resizeStart;
    private Point? _moveStart;
    private Point? _clickStart;
    private Point _clickWindowStart;
    private bool _clickMoved;
    private MarkupWindow? _editor;
    internal MarkupWindow? Editor => _editor;
    private double _moveLeft, _moveTop;
    private double _resizeZoom, _resizeLeft, _resizeTop, _resizeWidth, _resizeHeight;
    private int _resizeX, _resizeY;
    private bool _pinned, _zooming, _editing, _closed, _zoomSurfaceDirty;
    private IntPtr _handle;
    private PicturePresenter? _presenter;
    private bool _syncingGeometry;
    private BitmapSource _display = null!;
    private Point _zoomScreenAnchor, _zoomPixelAnchor;
    private readonly Stopwatch _zoomClock = new();
    private double _zoomFrom;
    private BitmapSource[] _levels = Array.Empty<BitmapSource>();
    private CancellationTokenSource? _levelWork;
    private bool _interactiveQuality;
    internal int NativeGeometryUpdates { get; private set; }
    internal int ZoomFrames { get; private set; }
    internal int DrawingPasses => _image.DrawingPasses;
    internal int PresentedFrames => _presenter?.Frames ?? 0;
    internal int InteractiveSourceWidth => _image.Source?.PixelWidth ?? 0;
    internal IntPtr PresentationHandle => _presenter?.Handle ?? _handle;
    internal BitmapSource Snapshot() => _presenter?.Snapshot() ?? _display;
    internal double MaximumPaintMilliseconds => _presenter?.MaximumPaintMilliseconds ?? 0;
    internal Rect PictureBounds => _zooming ? BoundsAtZoom(_zoom) : WindowBounds();
    public Guid ItemId => _item.Id;
    public bool IsPinned => _pinned;
    public double Zoom => _zoom;
    public double TargetZoom => _targetZoom;
    public int Angle => _angle;
    public BitmapSource DisplayImage => _display;
    public bool IsInteracting => _editing || PointerOnPicture || _previewBar?.IsMouseOver == true || ContextMenu?.IsOpen == true;
    private bool PointerOnPicture => IsVisible && _presenter is not null && WindowFromPoint(Displays.Cursor()) == _presenter.Handle;
    internal bool HasImageOnlyContent => ReferenceEquals(Content, _root) && _root.Children.Count == 1 && _root.Children[0] == _image;

    public PictureWindow(Line line, Pegged item, bool pinned = false)
        : this(line, item, pinned, null)
    {
    }

    private PictureWindow(Line line, Pegged item, bool pinned, Point? screenPosition)
    {
        Revision++;
        _line = line; _item = item; _initialScreenPosition = screenPosition;
        _original = Thumbnails.Load(item.Path, int.MaxValue)?.Image ?? item.Thumb;
        Title = "Snapline 贴图";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _image = new PictureSurface { NativePresentation = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _root = new Grid { Background = Brushes.Transparent, ClipToBounds = true, Focusable = true };
        _root.Children.Add(_image); Content = _root;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            int square = Native.DWMWCP_DONOTROUND;
            Native.DwmSetWindowAttribute(_handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
            HwndSource.FromHwnd(_handle)?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (Native.AllowOversizedPicture(msg, l)) { handled = true; return IntPtr.Zero; }
                if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                if (msg == Native.WM_NCHITTEST && _zooming)
                {
                    long coordinates = l.ToInt64();
                    var point = new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
                    if (!PictureBounds.Contains(point)) { handled = true; return new IntPtr(Native.HTTRANSPARENT); }
                }
                return IntPtr.Zero;
            });
            RefreshSize(screenPosition, screenPosition.HasValue ? new Point() : null);
            _presenter = new PicturePresenter(_handle);
            Revision++;
            _presenter.Wheel = (delta, point, control) =>
            {
                CloseEditorForGesture();
                if (!_pinned || _editing) return;
                if (control) ChangeOpacity(delta); else ZoomAt(delta, PointFromScreen(point));
            };
            _presenter.ButtonDown = NativeButtonDown;
            _presenter.ButtonUp = NativeButtonUp;
            _presenter.PointerMove = NativePointerMove;
            _presenter.CaptureLost = () =>
            {
                if (_moveStart is null && _resizeStart is null) return;
                _clickStart = null;
                _moveStart = null; _resizeStart = null; SetQuality(false); PresentPicture();
            };
            _presenter.CursorId = () =>
            {
                var p = Displays.Cursor(); var bounds = PictureBounds;
                double edge = Math.Min(8 * VisualTreeHelper.GetDpi(this).DpiScaleX, Math.Min(bounds.Width, bounds.Height) / 5);
                bool x = p.X - bounds.Left < edge || bounds.Right - p.X < edge;
                bool y = p.Y - bounds.Top < edge || bounds.Bottom - p.Y < edge;
                return !_pinned ? 32512 : x && y ? 32642 : x ? 32644 : y ? 32645 : 32646;
            };
        };
        LocationChanged += (_, _) =>
        {
            if (_clickStart is not null && Moved(_clickWindowStart, WindowBounds().TopLeft)) { _clickMoved = true; CloseEditorForGesture(); }
            if (!_zooming && !_syncingGeometry && _presenter is not null && Native.GetWindowRect(_handle, out var moved))
                _presenter.MoveTo(moved.Left, moved.Top);
        };
        IsVisibleChanged += (_, _) =>
        {
            Revision++;
            if (!IsVisible) { _presenter?.Hide(); return; }
            if (_presenter?.Frames > 0) _presenter.Show();
            // Oversized transparent WPF surfaces may never finish their first
            // render/Loaded pass. The native image must not wait for that pass.
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || !IsVisible) return;
                // A cached level may have painted a frame before Loaded, while
                // leaving the HWND hidden. A painted frame is not proof of visibility.
                if (_presenter is not null && !Native.IsWindowVisible(_presenter.Handle)) ShowPicture();
                if (!_pinned) ShowPreviewBar();
            });
        };
        PreviewMouseDown += MouseDownOnPicture;
        PreviewMouseMove += MouseMoveOnPicture;
        PreviewMouseUp += (_, e) =>
        {
            if (_resizeStart is not null) { _resizeStart = null; ReleaseMouseCapture(); SetQuality(false); e.Handled = true; }
        };
        LostMouseCapture += (_, _) => { _resizeStart = null; SetQuality(false); };
        PreviewMouseWheel += (_, e) =>
        {
            CloseEditorForGesture();
            if (!_pinned || _editing) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ChangeOpacity(e.Delta);
            else ZoomAt(e.Delta, e.GetPosition(this));
            e.Handled = true;
        };
        MouseRightButtonUp += (_, e) => { ContextMenu = BuildMenu(); ContextMenu.IsOpen = true; e.Handled = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _editor?.Close(); _editor = null;
            Revision++;
            _levelWork?.Cancel();
            _presenter?.Dispose(); _presenter = null;
            StopZoom(false); _previewBar?.Close(); _previewBar = null;
            PinnedWindows.Remove(this); if (_target == this) ClearShortcutTarget();
        };
        RefreshImage();
        var cursor = Displays.Cursor();
        var monitor = screenPosition is { } origin ? Displays.Nearest(new Native.POINT((int)origin.X, (int)origin.Y)) : Displays.Nearest(cursor);
        Left = (screenPosition?.X ?? cursor.X + 15) / monitor.Scale;
        Top = (screenPosition?.Y ?? monitor.Work.Top + 150 * monitor.Scale) / monitor.Scale;
        Loaded += (_, _) =>
        {
            ShowPicture();
            if (!_pinned) ShowPreviewBar();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { StopZoom(true); RefreshSize(); });
        if (pinned) MakePinned();
    }

    private void ShowPicture()
    {
        if (_handle == IntPtr.Zero || _presenter is null) return;
        var position = _positioned ? null : _initialScreenPosition;
        RefreshSize(position, position.HasValue ? new Point() : null);
        if (!_positioned && position is null && Native.GetWindowRect(_handle, out var bounds))
        {
            var m = Displays.OfWindow(_handle) ?? Displays.UnderPointer();
            int x = Math.Clamp(bounds.Left, m.Work.Left, Math.Max(m.Work.Left, m.Work.Right - bounds.Width));
            int y = Math.Clamp(bounds.Top, m.Work.Top, Math.Max(m.Work.Top, m.Work.Bottom - bounds.Height));
            Native.SetWindowPos(_handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        _positioned = true;
        PresentPicture(); _presenter.Show();
    }

    private void ShowPreviewBar()
    {
        if (_pinned || _previewBar is not null) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ToolButton("M6,2 H12 L11,7 L14,10 H10 V16 H8 V10 H4 L7,7 Z", $"钉到桌面（{Settings.Current.PinPreviewKey}）", MakePinned));
        row.Children.Add(ToolButton("M3,14 L12,5 L15,8 L6,17 L2,18 Z", "编辑图片", BeginEdit));
        row.Children.Add(ToolButton("M6,6 H16 V17 H6 Z M3,13 V3 H13", "复制图片", () => _line.Copy(_item.Id)));
        row.Children.Add(ToolButton("M4,4 L14,14 M14,4 L4,14", "关闭预览", Close));
        _previewBar = new FloatingToolbar(this, row); _previewBar.ShowBar();
    }

    internal static Button ToolButton(string path, string tip, Action action)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(path), Stroke = new SolidColorBrush(Color.FromRgb(65, 70, 80)), StrokeThickness = 1.5,
            Width = 18, Height = 18, Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var button = new Button { Content = icon, Width = 36, Height = 32, Padding = new Thickness(7), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action(); return button;
    }

    public static void Pin(Line line, Pegged item, Point? screenPosition = null)
    {
        var existing = PinnedWindows.FirstOrDefault(w => w.ItemId == item.Id);
        if (existing is not null) { existing.Show(); return; }
        new PictureWindow(line, item, true, screenPosition).Show();
    }
    public void MakePinned()
    {
        if (_pinned) return;
        _pinned = true; _previewBar?.Close(); _previewBar = null;
        PinnedWindows.Add(this); _item.Used = true;
        Revision++;
        if (IsVisible) PresentPicture();
    }

    private void RefreshImage()
    {
        _presenter?.ClearSources();
        BitmapSource result = _original;
        if (!_transform.IsIdentity) result = new TransformedBitmap(result, new MatrixTransform(_transform));
        result.Freeze(); _display = result; _image.Source = result; _levels = Array.Empty<BitmapSource>(); RefreshSize();
        _levelWork?.Cancel(); _levelWork?.Dispose(); _levelWork = new CancellationTokenSource();
        var cancellation = _levelWork.Token;
        // Frozen images can be prepared away from the input/render dispatcher.
        _ = Task.Run(() =>
        {
            var levels = new List<BitmapSource>(); BitmapSource current = result;
            while (Math.Max(current.PixelWidth, current.PixelHeight) > 768)
            {
                cancellation.ThrowIfCancellationRequested();
                var scaled = new TransformedBitmap(current, new ScaleTransform(0.5, 0.5));
                var cached = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); cached.Freeze();
                levels.Add(cached); current = cached;
            }
            return levels.ToArray();
        }, cancellation).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Dispatcher.BeginInvoke(() =>
            {
                if (_closed || !ReferenceEquals(_display, result)) return;
                _levels = task.Result; SelectInteractiveSource();
                if (!_zooming) PresentPicture();
            });
        }, TaskScheduler.Default);
    }

    private double ClampZoom(double zoom) => Math.Clamp(zoom, 0.05, Math.Max(0.05, Math.Min(8, 16300.0 / Math.Max(_display.PixelWidth, _display.PixelHeight))));
    private void RefreshSize(Point? screenAnchor = null, Point? pixelAnchor = null)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(2, (int)Math.Round(_display.PixelWidth * _zoom));
        int ph = Math.Max(2, (int)Math.Round(_display.PixelHeight * _zoom));
        _image.SetBounds(new Rect(0, 0, pw / dpi.DpiScaleX, ph / dpi.DpiScaleY));
        if (_handle == IntPtr.Zero) { Width = pw / dpi.DpiScaleX; Height = ph / dpi.DpiScaleY; return; }
        if (!Native.GetWindowRect(_handle, out var bounds)) return;
        int left = bounds.Left, top = bounds.Top;
        if (screenAnchor is { } anchor && pixelAnchor is { } pixel)
        { left = (int)Math.Round(anchor.X - pixel.X * _zoom); top = (int)Math.Round(anchor.Y - pixel.Y * _zoom); }
        SetWindowBounds(new Rect(left, top, pw, ph));
        if (!_zooming) PresentPicture();
    }

    private Rect WindowBounds()
    {
        if (_handle != IntPtr.Zero && Native.GetWindowRect(_handle, out var bounds))
            return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(Left * dpi.DpiScaleX, Top * dpi.DpiScaleY, _display.PixelWidth * _zoom, _display.PixelHeight * _zoom);
    }

    private Rect BoundsAtZoom(double zoom) => new(
        _zoomScreenAnchor.X - _zoomPixelAnchor.X * zoom, _zoomScreenAnchor.Y - _zoomPixelAnchor.Y * zoom,
        _display.PixelWidth * zoom, _display.PixelHeight * zoom);

    private void SetWindowBounds(Rect rect)
    {
        int x = (int)Math.Floor(rect.Left), y = (int)Math.Floor(rect.Top);
        int width = Math.Max(2, (int)Math.Ceiling(rect.Right) - x), height = Math.Max(2, (int)Math.Ceiling(rect.Bottom) - y);
        if (!Native.GetWindowRect(_handle, out var current) || (current.Left == x && current.Top == y && current.Width == width && current.Height == height)) return;
        _syncingGeometry = true;
        try { Native.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE); }
        finally { _syncingGeometry = false; }
        NativeGeometryUpdates++;
    }

    private void PrepareZoomSurface()
    {
        _presenter?.Reserve(Math.Max(2, (int)Math.Ceiling(_display.PixelWidth * Math.Max(_zoom, _targetZoom) * 1.12)),
            Math.Max(2, (int)Math.Ceiling(_display.PixelHeight * Math.Max(_zoom, _targetZoom) * 1.12)), _pinned);
    }

    private void DrawZoomFrame()
    {
        PresentPicture();
    }

    private void PresentPicture()
    {
        if (_closed || _presenter is null || _image.Source is not { } image) return;
        _presenter.Present(image, PictureBounds, Opacity, _interactiveQuality, _pinned);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == OpacityProperty) _presenter?.SetOpacity(Opacity);
    }

    private void SetQuality(bool interactive)
    {
        _interactiveQuality = interactive;
        RenderOptions.SetBitmapScalingMode(_image, interactive ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality);
        // Use the prefiltered level for settled downscaled pictures too. Switching back
        // to a full 4K source after every wheel notch both flickers in detail and stalls.
        SelectInteractiveSource();
    }
    private void SelectInteractiveSource()
    {
        double required = Math.Max(_display.PixelWidth, _display.PixelHeight) * Math.Max(_zoom, _targetZoom);
        BitmapSource selected = _display;
        foreach (var level in _levels)
        {
            if (Math.Max(level.PixelWidth, level.PixelHeight) < required) break;
            selected = level;
        }
        if (!ReferenceEquals(_image.Source, selected)) _image.Source = selected;
    }
    public void ResetSizeAndOpacity() { StopZoom(false); _zoom = _targetZoom = 1; SetQuality(false); Opacity = 1; RefreshSize(); }
    internal void ChangeOpacity(int delta) => Opacity = Math.Clamp(Opacity + Math.Sign(delta) * 0.05, 0.1, 1);
    internal void ChangeScale(double zoom) { StopZoom(false); _zoom = _targetZoom = ClampZoom(zoom); SetQuality(false); RefreshSize(); }

    internal void ZoomAt(int delta, Point local)
    {
        if (delta == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = _handle == IntPtr.Zero ? new Point(Left * dpi.DpiScaleX + local.X * dpi.DpiScaleX, Top * dpi.DpiScaleY + local.Y * dpi.DpiScaleY) : PointToScreen(local);
        var image = PictureBounds;
        _zoomScreenAnchor = screen;
        _zoomPixelAnchor = new Point((screen.X - image.Left) / _zoom, (screen.Y - image.Top) / _zoom);
        _targetZoom = ClampZoom((_zooming ? _targetZoom : _zoom) * Math.Pow(1.12, delta / 120.0));
        if (!_zooming && Math.Abs(_targetZoom - _zoom) < 1e-10) return;
        if (!_zooming)
        {
            _zooming = true;
            CompositionTarget.Rendering += AnimateZoom;
        }
        _zoomFrom = _zoom; _zoomClock.Restart(); SetQuality(true);
        // Coalesce high-resolution wheels and bursts into at most one buffer update per frame.
        _zoomSurfaceDirty = true;
    }
    private void AnimateZoom(object? sender, EventArgs e)
    {
        if (_zoomSurfaceDirty) { _zoomSurfaceDirty = false; PrepareZoomSurface(); }
        double progress = Math.Clamp(_zoomClock.Elapsed.TotalMilliseconds / 120, 0, 1);
        _zoom = _zoomFrom + (_targetZoom - _zoomFrom) * (1 - Math.Pow(1 - progress, 3));
        bool settled = progress >= 1;
        if (settled) _zoom = _targetZoom;
        DrawZoomFrame(); ZoomFrames++;
        if (settled) StopZoom(false);
    }
    internal void StopZoom(bool complete)
    {
        bool wasZooming = _zooming;
        if (complete && wasZooming) _zoom = _targetZoom;
        CompositionTarget.Rendering -= AnimateZoom; _zooming = false; _zoomSurfaceDirty = false;
        SetQuality(false);
        if (wasZooming && !_closed) RefreshSize(_zoomScreenAnchor, _zoomPixelAnchor);
    }

    public void ApplyTransform(int action)
    {
        StopZoom(true);
        if (action == 0) { _angle = (_angle + 90) % 360; _transform.Rotate(90); }
        if (action == 1) { _angle = (_angle + 270) % 360; _transform.Rotate(-90); }
        if (action == 2) _transform.Scale(-1, 1);
        if (action == 3) _transform.Scale(1, -1);
        RefreshImage();
    }

    public void BeginEdit()
    {
        if (_editing) return;
        StopZoom(true); _editing = true; _previewBar?.Hide();
        var bounds = new Rect(Left, Top, Width, Height);
        try
        {
            Activate(); _root.Focus();
            _editor = MarkupWindow.ShowForPicture(_item.Path, _line, _display, bounds, Opacity, accepted =>
            {
                if (_closed) return;
                _editing = false; _editor = null;
                if (accepted) { _transform = Matrix.Identity; _angle = 0; _original = Thumbnails.Load(_item.Path, int.MaxValue)?.Image ?? _original; RefreshImage(); }
                if (!_pinned) _previewBar?.ShowBar();
            }, this);
        }
        catch { _editing = false; throw; }
    }

    private void CloseEditorForGesture()
    {
        if (_editor is { IsVisible: false } editor) editor.Close();
    }

    private void MouseDownOnPicture(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { ResetSizeAndOpacity(); e.Handled = true; return; }
        if (!_pinned || e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { Close(); e.Handled = true; return; }
        StopZoom(true);
        var point = e.GetPosition(this);
        double edge = Math.Min(8, Math.Min(ActualWidth, ActualHeight) / 5);
        _resizeX = point.X <= edge ? -1 : point.X >= ActualWidth - edge ? 1 : 0;
        _resizeY = point.Y <= edge ? -1 : point.Y >= ActualHeight - edge ? 1 : 0;
        e.Handled = true;
        if (_resizeX != 0 || _resizeY != 0)
        {
            _resizeStart = PointToScreen(point); _resizeZoom = _zoom;
            _resizeLeft = Left; _resizeTop = Top; _resizeWidth = ActualWidth; _resizeHeight = ActualHeight;
            CaptureMouse(); SetQuality(true);
        }
        else
        {
            var start = PointToScreen(point); var before = WindowBounds();
            _clickStart = start; _clickWindowStart = before.TopLeft; _clickMoved = false;
            try { DragMove(); OpenEditorAfterClick(start, before); } catch (InvalidOperationException) { }
            finally { _clickStart = null; }
        }
    }

    private static bool Moved(Point start, Point end) => Math.Abs(end.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(end.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void OpenEditorAfterClick(Point start, Rect before)
    {
        var cursor = Displays.Cursor(); var after = WindowBounds();
        if (!_clickMoved && !Moved(start, new Point(cursor.X, cursor.Y)) && !Moved(before.TopLeft, after.TopLeft)) OpenClickEditor();
    }

    private void OpenClickEditor()
    {
        if (_closed || _editing || !_pinned) return;
        if (!CaptureActive && !CaptureWindow.IsCapturing && !SettingsWindow.AnyOpen) BeginEdit();
    }

    private void NativeButtonDown(int button, int clicks, Point screen)
    {
        _clickStart = null;
        if (button == 1) { CloseEditorForGesture(); ResetSizeAndOpacity(); return; }
        if (button != 0 || !_pinned) return;
        if (clicks == 2) { Close(); return; }
        StopZoom(true); var bounds = WindowBounds(); var dpi = VisualTreeHelper.GetDpi(this);
        _clickStart = screen; _clickMoved = false;
        _clickWindowStart = bounds.TopLeft;
        double edge = Math.Min(8 * dpi.DpiScaleX, Math.Min(bounds.Width, bounds.Height) / 5);
        _resizeX = screen.X - bounds.Left <= edge ? -1 : bounds.Right - screen.X <= edge ? 1 : 0;
        _resizeY = screen.Y - bounds.Top <= edge ? -1 : bounds.Bottom - screen.Y <= edge ? 1 : 0;
        SetNativeCapture(PresentationHandle);
        if (_resizeX != 0 || _resizeY != 0)
        {
            _resizeStart = screen; _resizeZoom = _zoom;
            _resizeLeft = Left; _resizeTop = Top; _resizeWidth = ActualWidth; _resizeHeight = ActualHeight;
            SetQuality(true);
        }
        else
        {
            if ((Native.GetAsyncKeyState(1) & 0x8000) != 0)
            {
                ReleaseNativeCapture();
                try { DragMove(); _clickStart = null; OpenEditorAfterClick(screen, bounds); return; }
                catch (InvalidOperationException) { SetNativeCapture(PresentationHandle); }
            }
            _moveStart = screen; _moveLeft = bounds.Left; _moveTop = bounds.Top;
        }
    }
    private void NativePointerMove(Point screen)
    {
        if (_clickStart is { } pressed) _clickMoved |= Moved(pressed, screen);
        if (_clickMoved && (_moveStart is not null || _resizeStart is not null)) CloseEditorForGesture();
        if (_moveStart is { } start)
        {
            _clickMoved |= Moved(start, screen);
            if (!_clickMoved) return;
            Native.SetWindowPos(_handle, IntPtr.Zero, (int)Math.Round(_moveLeft + screen.X - start.X), (int)Math.Round(_moveTop + screen.Y - start.Y), 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
        else if (_resizeStart is { } resize)
        {
            if (!_clickMoved) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (screen.X - resize.X) / dpi.DpiScaleX, dy = (screen.Y - resize.Y) / dpi.DpiScaleY;
            double fx = _resizeX == 0 ? 1 : (_resizeWidth + _resizeX * dx) / _resizeWidth;
            double fy = _resizeY == 0 ? 1 : (_resizeHeight + _resizeY * dy) / _resizeHeight;
            double factor = _resizeX == 0 ? fy : _resizeY == 0 ? fx : Math.Abs(fx - 1) > Math.Abs(fy - 1) ? fx : fy;
            _zoom = _targetZoom = ClampZoom(_resizeZoom * factor); RefreshSize();
            if (_resizeX < 0) Left = _resizeLeft + _resizeWidth - Width;
            if (_resizeY < 0) Top = _resizeTop + _resizeHeight - Height;
        }
    }
    private void NativeButtonUp(int button, Point screen)
    {
        if (button == 2) { CloseEditorForGesture(); ContextMenu = BuildMenu(); ContextMenu.IsOpen = true; return; }
        if (button != 0) return;
        bool click = _clickStart is { } start && !_clickMoved && !Moved(start, screen);
        bool resized = _resizeStart is not null && _clickMoved;
        _clickStart = null;
        _moveStart = null; _resizeStart = null; ReleaseNativeCapture(); SetQuality(false);
        if (resized) PresentPicture();
        if (click) OpenClickEditor();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetCapture")] private static extern IntPtr SetNativeCapture(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ReleaseCapture")] private static extern bool ReleaseNativeCapture();
    private void MouseMoveOnPicture(object sender, MouseEventArgs e)
    {
        if (!_pinned) return;
        var p = e.GetPosition(this);
        if (_resizeStart is { } start)
        {
            var current = PointToScreen(p); var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (current.X - start.X) / dpi.DpiScaleX, dy = (current.Y - start.Y) / dpi.DpiScaleY;
            double fx = _resizeX == 0 ? 1 : (_resizeWidth + _resizeX * dx) / _resizeWidth;
            double fy = _resizeY == 0 ? 1 : (_resizeHeight + _resizeY * dy) / _resizeHeight;
            double factor = _resizeX == 0 ? fy : _resizeY == 0 ? fx : Math.Abs(fx - 1) > Math.Abs(fy - 1) ? fx : fy;
            _zoom = _targetZoom = ClampZoom(_resizeZoom * factor); RefreshSize();
            if (_resizeX < 0) Left = _resizeLeft + _resizeWidth - Width;
            if (_resizeY < 0) Top = _resizeTop + _resizeHeight - Height;
            e.Handled = true; return;
        }
        bool x = p.X < 8 || p.X > ActualWidth - 8, y = p.Y < 8 || p.Y > ActualHeight - 8;
        Cursor = x && y ? Cursors.SizeNWSE : x ? Cursors.SizeWE : y ? Cursors.SizeNS : Cursors.SizeAll;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        if (!_pinned) menu.Items.Add(PeggedControl.MenuItemFor("钉到桌面", MakePinned));
        menu.Items.Add(PeggedControl.MenuItemFor("编辑图片…", BeginEdit));
        menu.Items.Add(PeggedControl.MenuItemFor("复制图片", () => _line.Copy(_item.Id)));
        menu.Items.Add(PeggedControl.MenuItemFor("恢复原始大小和透明度", ResetSizeAndOpacity, "鼠标中键"));
        menu.Items.Add(PeggedControl.MenuItemFor("顺时针旋转 90°", () => ApplyTransform(0), Settings.Current.RotateClockwiseKey));
        menu.Items.Add(PeggedControl.MenuItemFor("逆时针旋转 90°", () => ApplyTransform(1), Settings.Current.RotateCounterclockwiseKey));
        menu.Items.Add(PeggedControl.MenuItemFor("左右镜像", () => ApplyTransform(2), Settings.Current.MirrorHorizontalKey));
        menu.Items.Add(PeggedControl.MenuItemFor("上下镜像", () => ApplyTransform(3), Settings.Current.MirrorVerticalKey));
        menu.Items.Add(PeggedControl.MenuItemFor("关闭贴图（保留图片）", Close)); return menu;
    }

    public static void CloseFor(Guid id)
    {
        Preview.CloseFor(id); MarkupWindow.CloseFor(id);
        foreach (var w in PinnedWindows.Where(w => w.ItemId == id).ToList()) w.Close();
    }
    public static void ReloadFor(Pegged item)
    {
        foreach (var w in PinnedWindows.Where(w => w.ItemId == item.Id && !w._editing).ToList())
        { w._original = Thumbnails.Load(item.Path, int.MaxValue)?.Image ?? item.Thumb; w.RefreshImage(); }
    }
    public static void ClosePinned() { foreach (var w in PinnedWindows.ToList()) w.Close(); }
    public static void PrepareForCapture()
    {
        _captureActive = true; ClearShortcutTarget();
        Revision++;
        foreach (var w in PinnedWindows) { w.CloseEditorForGesture(); w.StopZoom(true); }
        FloatingToolbar.HideForCapture();
    }
    public static void FinishCapture() { _captureActive = false; Revision++; FloatingToolbar.RestoreAfterCapture(); }
    public static void ClearShortcutTarget()
    {
        if (_target is null && Keys.Count == 0) return;
        foreach (var key in Keys) key.Dispose(); Keys.Clear(); _target = null; Revision++;
    }
    public static void UpdateShortcutTarget(Native.POINT cursor) => UpdateShortcutTarget(cursor, WindowFromPoint(cursor));
    internal static void UpdateShortcutTarget(Native.POINT cursor, IntPtr hwnd)
    {
        if (_captureActive || MarkupWindow.AnyEditing || SettingsWindow.AnyActive) { ClearShortcutTarget(); return; }
        if (_target is { IsVisible: true } current && current.PresentationHandle == hwnd) return;
        PictureWindow? target = null;
        for (int i = PinnedWindows.Count - 1; i >= 0; i--)
        {
            var candidate = PinnedWindows[i];
            if (candidate.IsVisible && candidate.PresentationHandle == hwnd) { target = candidate; break; }
        }
        if (target == _target) return;
        ClearShortcutTarget(); if (target is null) return;
        _target = target; _messages ??= new MessageWindow(); var s = Settings.Current;
        string[] shortcuts = { s.RotateClockwiseKey, s.RotateCounterclockwiseKey, s.MirrorHorizontalKey, s.MirrorVerticalKey };
        for (int i = 0; i < shortcuts.Length; i++) { int action = i; Keys.Add(new HotKey(_messages, shortcuts[i], () => _target?.ApplyTransform(action))); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT point);
}

public static class Preview
{
    private static PictureWindow? _window;
    private static Guid? _candidate;
    private static Guid? _pinnedOnHover;
    private static DateTime _since;
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(280);
    private static readonly System.Windows.Threading.DispatcherTimer HoverTimer = new(System.Windows.Threading.DispatcherPriority.Background);
    private static Pegged? _waitingItem;
    private static Line? _waitingLine;
    private static MessageWindow? _pinMessages;
    private static HotKey? _pinKey;
    private static HotKey? _sharedPinKey;
    static Preview()
    {
        HoverTimer.Tick += (_, _) =>
        {
            HoverTimer.Stop();
            if (_waitingItem is not { Falling: false } item || _waitingLine is not { } line
                || _candidate != item.Id || line.Find(item.Id) is null) return;
            var remaining = HoverDelay - (DateTime.UtcNow - _since);
            if (remaining > TimeSpan.Zero)
            {
                HoverTimer.Interval = remaining + TimeSpan.FromMilliseconds(1); HoverTimer.Start(); return;
            }
            Update(item, line);
        };
    }
    internal static PictureWindow? CurrentWindow => _window;
    private static bool CanPin => (_window?.IsVisible == true || CaptureResult.HasPending) && !MarkupWindow.AnyEditing
        && !SettingsWindow.AnyOpen && !PictureWindow.CaptureActive && !CaptureWindow.IsCapturing;
    internal static bool PinShortcutRegistered => _pinKey?.Registered == true || CanPin && _sharedPinKey?.Registered == true
        && HotKey.Same(_sharedPinKey.Text, Settings.Current.PinPreviewKey);
    internal static void SharePinShortcut(HotKey? shortcut) { _sharedPinKey = shortcut; RefreshPinShortcut(); }
    internal static bool NeedsTimedUpdate => _candidate is { } candidate && _pinnedOnHover != candidate
        && _window?.ItemId != candidate && DateTime.UtcNow - _since > TimeSpan.FromMilliseconds(280);
    public static void ClearPinShortcut() { _pinKey?.Dispose(); _pinKey = null; }
    internal static bool PinCurrent()
    {
        if (_window is not { IsVisible: true, IsPinned: false } picture || MarkupWindow.AnyEditing
            || SettingsWindow.AnyOpen) return false;
        picture.MakePinned(); _pinnedOnHover = picture.ItemId; _window = null; _candidate = null; RefreshPinShortcut(); return true;
    }
    public static void Update(Pegged? item, Line line)
    {
        var now = DateTime.UtcNow;
        if (_window?.IsPinned == true) { _pinnedOnHover = _window.ItemId; _window = null; ClearPinShortcut(); }
        if (item?.Id == _pinnedOnHover && item is not null) { RefreshPinShortcut(); return; }
        _pinnedOnHover = null;
        if (item is not null)
        {
            if (_candidate != item.Id)
            {
                if (_window?.ItemId != item.Id) Close();
                _candidate = item.Id; _since = now;
                _waitingItem = item; _waitingLine = line;
                HoverTimer.Stop(); HoverTimer.Interval = HoverDelay; HoverTimer.Start();
            }
            if (_window?.ItemId != item.Id && now - _since > TimeSpan.FromMilliseconds(280))
            {
                Close(); _candidate = item.Id; var created = new PictureWindow(line, item); _window = created;
                created.Closed += (_, _) => { if (_window == created) { _window = null; ClearPinShortcut(); } }; created.Show();
            }
        }
        else if (_window?.IsInteracting != true) { Close(); _candidate = null; }
        RefreshPinShortcut();
    }
    internal static void RefreshPinShortcut()
    {
        if (CanPin && _sharedPinKey?.Registered == true && HotKey.Same(_sharedPinKey.Text, Settings.Current.PinPreviewKey))
        { ClearPinShortcut(); return; }
        if (CanPin)
        {
            if (_pinKey is null || _pinKey.Text != Settings.Current.PinPreviewKey)
            {
                ClearPinShortcut(); _pinMessages ??= new MessageWindow();
                _pinKey = new HotKey(_pinMessages, Settings.Current.PinPreviewKey, () => { if (!PinCurrent()) CaptureResult.PinCurrent(); });
            }
        }
        else ClearPinShortcut();
    }
    public static void Close()
    {
        HoverTimer.Stop(); _waitingItem = null; _waitingLine = null; _candidate = null;
        if (_window?.IsPinned != true) _window?.Close(); _window = null; ClearPinShortcut();
    }
    public static void CloseFor(Guid id) { if (_window?.ItemId == id || _candidate == id) Close(); }
}
