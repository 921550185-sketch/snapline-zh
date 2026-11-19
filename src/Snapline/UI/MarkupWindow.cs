using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Snapline.Core;
using Snapline.Interop;
using Line = Snapline.Core.Line;
using Path = System.Windows.Shapes.Path;

namespace Snapline.UI;

/// <summary>
/// Image-only editing window with a separate floating toolbar. Changes remain
/// in memory until confirmation; cancellation never overwrites the source image.
/// </summary>
public sealed class MarkupWindow : Window
{
    private static readonly Dictionary<string, MarkupWindow> Open = new(StringComparer.OrdinalIgnoreCase);

    public static void Show(string path, Line line)
    {
        if (Open.TryGetValue(path, out var existing)) { existing.Activate(); return; }
        var full = Thumbnails.Load(path, int.MaxValue, 4);
        if (full is null) { System.Media.SystemSounds.Beep.Play(); return; }
        var w = new MarkupWindow(path, full.Value, line);
        Open[path] = w;
        w.Closed += (_, _) => Open.Remove(path);
        w.Show();
        w.Activate();
    }

    public static bool AnyEditing => Open.Count > 0;
    internal static bool IsTypingText
    {
        get
        {
            foreach (var window in Open.Values)
                if (window._textEntry?.IsKeyboardFocusWithin == true) return true;
            return false;
        }
    }
    public static void CloseFor(Guid id) { foreach (var w in Open.Values.Where(w => w._itemId == id).ToList()) w.Close(); }
    public static void CloseAll() { foreach (var w in Open.Values.ToList()) w.Close(); }
    public static MarkupWindow? ShowForPicture(string path, Line line, BitmapSource image, Rect bounds, double opacity, Action<bool> finished, PictureWindow? host = null)
    {
        if (Open.TryGetValue(path, out var old)) { old.ActivateCanvas(); finished(false); return null; }
        var window = new MarkupWindow(path, new Thumbnail(image, image.PixelWidth, image.PixelHeight), line, bounds, false, finished, opacity, host);
        window._dirty = true;
        Track(window, path, show: host is null);
        try { if (host is not null) window._floating!.ShowBar(); }
        catch { window.Close(); throw; }
        return window;
    }
    public static void ShowCapture(BitmapSource image, Line line, Rect screenBounds, Action? finished, BitmapSource? captureSource = null, Int32Rect? sourceRect = null, Point? captureOrigin = null)
    {
        var drafts = System.IO.Path.Combine(Settings.Folder, "Drafts");
        Directory.CreateDirectory(drafts);
        var path = System.IO.Path.Combine(drafts, $"截图-{Guid.NewGuid():N}.png");
        Thumbnails.SavePng(image, path);
        var monitor = Displays.Nearest(new Native.POINT((int)screenBounds.X, (int)screenBounds.Y));
        var bounds = new Rect(screenBounds.X / monitor.Scale, screenBounds.Y / monitor.Scale, screenBounds.Width / monitor.Scale, screenBounds.Height / monitor.Scale);
        var window = new MarkupWindow(path, new Thumbnail(image, image.PixelWidth, image.PixelHeight), line, bounds, true, _ => finished?.Invoke());
        if (captureSource is not null && sourceRect is { } region) window.SetCaptureSource(captureSource, region, captureOrigin ?? new Point());
        Track(window, path);
    }
    private static void Track(MarkupWindow window, string key, bool show = true)
    {
        Open[key] = window;
        window.Closed += (_, _) => Open.Remove(key);
        if (show) { window.Show(); window.Activate(); }
    }

    internal enum Tool { Pen, Highlighter, Circle, Box, Arrow, Text, Blur }

    private string _path;
    private readonly Guid? _itemId;
    private readonly bool _captureDraft;
    private readonly string? _draftPath;
    private readonly Action<bool>? _finished;
    private bool _accepted;
    private FloatingToolbar? _floating;
    internal FrameworkElement? ToolbarSurface => _floating?.Surface;
    internal bool IsCaptureDraft => _captureDraft;
    internal bool CaptureSelectionVisible => _captureHandles?.Visibility == Visibility.Visible;
    internal bool ToolReceivesInput(Point point)
    {
        var target = _tool is Tool.Pen or Tool.Highlighter ? (DependencyObject)_ink : _shapes;
        var hit = _page.InputHitTest(point) as DependencyObject;
        while (hit is not null)
        {
            if (ReferenceEquals(hit, target)) return true;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return false;
    }
    private readonly Line _line;
    private readonly PictureWindow? _pictureHost;
    private BitmapSource _image;
    private BitmapSource? _captureSource;
    private Int32Rect _captureRect, _cropDragStart;
    private Point _captureOrigin, _cropDragMouse;
    private Grid? _captureHandles;
    private readonly InkCanvas _ink;
    private readonly ShapeLayer _shapes;
    private readonly Grid _page;
    private readonly double _scale;      // display units per image pixel
    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock _status = new();
    private readonly List<(Tool tool, ToggleButton button)> _toolButtons = new();
    private readonly List<Border> _swatches = new();
    private readonly List<ToggleButton> _sizeButtons = new();
    private Tool _tool = Tool.Pen;
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);
    private int _size = 1; // 0 thin, 1 medium, 2 thick
    private bool _dirty;
    private Button _undoButton = null!, _redoButton = null!;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFF, 0xC1, 0x07), Color.FromRgb(0x1E, 0x88, 0xE5),
        Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0x12, 0x12, 0x12), Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private MarkupWindow(string path, Thumbnail full, Line line, Rect? bounds = null, bool captureDraft = false, Action<bool>? finished = null, double opacity = 1, PictureWindow? pictureHost = null)
    {
        _path = path;
        _line = line;
        _pictureHost = pictureHost;
        _itemId = line.Items.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Id;
        _captureDraft = captureDraft; _draftPath = captureDraft ? path : null; _finished = finished;
        // Keep the hosted input window fully opaque as a *window*. Applying the
        // picture's opacity here can round its hit-test background back to alpha 0.
        Opacity = pictureHost is null ? opacity : 1;
        _image = full.Image;

        Title = $"{System.IO.Path.GetFileName(path)} · {Strings.MarkUp}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Icon = Brand.Icon;
        Topmost = true;
        FlowDirection = Strings.Flow;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;

        var display = Displays.UnderPointer();
        double availW = display.Work.Width / display.Scale * 0.86;
        double availH = display.Work.Height / display.Scale * 0.82 - 70;
        _scale = bounds is { } anchor ? anchor.Width / full.PixelWidth
            : Math.Min(1 / display.Scale, Math.Min(availW / full.PixelWidth, availH / full.PixelHeight));
        double imgW = full.PixelWidth * _scale, imgH = full.PixelHeight * _scale;
        // USER32 passes clicks through alpha-zero pixels of layered windows.
        // This one-alpha background catches input without redrawing the picture.
        Background = pictureHost is null ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Foreground = new SolidColorBrush(Color.FromRgb(64, 69, 78));
        _page = new Grid { Width = imgW, Height = imgH, ClipToBounds = true };
        _page.Children.Add(new Border { Child = Fast(new Image { Source = _image, Stretch = Stretch.Fill }), BorderThickness = new Thickness(0),
            Visibility = pictureHost is null ? Visibility.Visible : Visibility.Hidden });
        _ink = new InkCanvas { Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.Ink, Cursor = Cursors.Pen };
        _ink.StrokeCollected += (_, e) => Commit(e.Stroke); _page.Children.Add(_ink);
        _shapes = new ShapeLayer(this) { IsHitTestVisible = false }; _page.Children.Add(_shapes);
        if (pictureHost is not null) { _ink.Opacity = opacity; _shapes.Opacity = opacity; }
        Content = _page;
        Width = imgW; Height = imgH; WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds?.Left ?? (display.Work.Left + (display.Work.Width - Width * display.Scale) / 2) / display.Scale;
        Top = bounds?.Top ?? (display.Work.Top + (display.Work.Height - Height * display.Scale) / 2) / display.Scale;
        _floating = new FloatingToolbar(pictureHost ?? (Window)this, BuildToolbar(false));
        Loaded += (_, _) => _floating.ShowBar();
        KeyDown += OnKey;
        if (_pictureHost is not null) _pictureHost.KeyDown += OnKey;
        Closed += (_, _) =>
        {
            if (_pictureHost is not null) _pictureHost.KeyDown -= OnKey;
            _saveTimer.Stop(); _floating?.Close();
            if (_draftPath is not null) try { File.Delete(_draftPath); } catch { }
            _finished?.Invoke(_accepted);
        };

        ApplyTool();
    }

    // The first click shows only controls. Keep the native picture HWND in place so
    // Windows can deliver the second click and the desktop never loses its image.
    internal void ActivateCanvas()
    {
        if (_pictureHost is { } host)
        {
            Left = host.Left; Top = host.Top; Owner ??= host;
        }
        if (!IsVisible) Show();
        // The first native window creation occurs before the size-limit hook is attached.
        // Reapply the picture's physical rectangle after Show so oversized canvases align.
        if (_pictureHost is { } picture && Native.GetWindowRect(new WindowInteropHelper(picture).Handle, out var pictureBounds))
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST,
                pictureBounds.Left, pictureBounds.Top, pictureBounds.Width, pictureBounds.Height, Native.SWP_NOACTIVATE);
        Activate(); ApplyTool();
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _floating?.ShowBar();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        FullScreen.Own.Add(hwnd);
        if (_pictureHost is not null)
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr handle, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (Native.AllowOversizedPicture(message, l)) handled = true;
                return IntPtr.Zero;
            });
        // Keep image pixels square; the separate toolbar supplies its own rounded capsule.
        int round = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private static Image Fast(Image image)
    {
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        RenderOptions.SetCachingHint(image, CachingHint.Cache);
        return image;
    }

    // MARK: Toolbar

    private UIElement BuildToolbar(bool dark)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void AddTool(Tool t, string glyph, string label)
        {
            var b = ToolButton(glyph, label, false);
            b.Click += (_, _) => { _tool = t; ApplyTool(); ActivateCanvas(); };
            _toolButtons.Add((t, b)); bar.Children.Add(b);
        }
        AddTool(Tool.Box, "M3,3 H15 V15 H3 Z", Strings.Box);
        AddTool(Tool.Circle, "M9,2 A7,7 0 1 1 8.9,2", Strings.Circle);
        AddTool(Tool.Arrow, "M3,15 L15,3 M8,3 H15 V10", Strings.Arrow);
        AddTool(Tool.Pen, "M3,13 L11,5 L13,7 L5,15 Z M2,16 L5,15", Strings.Pen);
        AddTool(Tool.Blur, "M3,3 H15 V15 H3 Z M3,7 H15 M3,11 H15 M7,3 V15 M11,3 V15", "马赛克／模糊");
        AddTool(Tool.Text, "M3,4 H15 M9,4 V15 M6,15 H12", Strings.TextTool);
        if (_captureDraft)
            bar.Children.Add(PictureWindow.ToolButton("M2,6 V2 H6 M12,2 H16 V6 M16,12 V16 H12 M6,16 H2 V12", "显示／隐藏截图范围调整框", ToggleCaptureSelection));

        var palette = new ContextMenu();
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var c in Palette)
        {
            var swatch = new Border { Width = 22, Height = 22, Margin = new Thickness(4), CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            swatch.MouseLeftButtonDown += (_, e) => { e.Handled = true; _color = c; ApplyTool(); palette.IsOpen = false; };
            _swatches.Add(swatch); colors.Children.Add(swatch);
        }
        palette.Items.Add(new MenuItem { Header = colors, StaysOpenOnClick = true });
        for (int i = 0; i < 3; i++)
        {
            int size = i;
            var item = new MenuItem { Header = new[] { "细线", "中等线条", "粗线" }[i] };
            item.Click += (_, _) => { _size = size; ApplyTool(); }; palette.Items.Add(item);
        }
        var highlighter = new MenuItem { Header = Strings.Highlighter }; highlighter.Click += (_, _) => { _tool = Tool.Highlighter; ApplyTool(); ActivateCanvas(); }; palette.Items.Add(highlighter);
        var colour = PictureWindow.ToolButton("M9,2 A7,7 0 1 1 8.9,2 M5,9 H13 M9,5 V13", "颜色和线条粗细", () => palette.IsOpen = true);
        colour.ContextMenu = palette; palette.PlacementTarget = colour; bar.Children.Add(colour);
        bar.Children.Add(Gap());
        _undoButton = ActionButton("M6,4 L2,8 L6,12 M2,8 H11 A4,4 0 0 1 11,16 H8", Strings.Undo + " (Ctrl+Z)", false);
        _undoButton.Click += (_, _) => Undo(); bar.Children.Add(_undoButton);
        _redoButton = ActionButton("M12,4 L16,8 L12,12 M16,8 H7 A4,4 0 0 0 7,16 H10", Strings.Redo + " (Ctrl+Y)", false);
        _redoButton.Click += (_, _) => Redo(); bar.Children.Add(_redoButton);
        bar.Children.Add(PictureWindow.ToolButton("M3,3 H15 V16 H3 Z M6,3 V8 H12 V3 M6,12 H12 V16", "保存到文件…", SaveAs));
        bar.Children.Add(PictureWindow.ToolButton("M6,2 H12 L11,7 L14,10 H10 V16 H8 V10 H4 L7,7 Z", _captureDraft ? $"完成并钉到桌面（{Settings.Current.PinPreviewKey}）" : "完成并钉到桌面", () => Confirm(true)));
        bar.Children.Add(Gap());
        var cancel = PictureWindow.ToolButton("M3,3 L15,15 M15,3 L3,15", "取消编辑（Esc）", Close);
        ((System.Windows.Shapes.Path)cancel.Content).Stroke = Brushes.IndianRed; bar.Children.Add(cancel);
        var done = PictureWindow.ToolButton("M2,9 L7,14 L16,3", "完成并复制（Enter）", () => Confirm(false));
        ((System.Windows.Shapes.Path)done.Content).Stroke = new SolidColorBrush(Color.FromRgb(0, 185, 115)); bar.Children.Add(done);
        return bar;
    }

    private static FrameworkElement Gap() => new Rectangle { Width = 1, Height = 20, Fill = new SolidColorBrush(Theme.Gray(0.5, 0.35)), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    private Style ToolStyle(bool dark)
    {
        var style = new Style(typeof(ToggleButton));
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Theme.Gray(dark ? 1 : 0, 0.08)), "Bd"));
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(dark ? Color.FromArgb(0x50, 0x4A, 0x8C, 0xFF) : Color.FromArgb(0x30, 0x1E, 0x66, 0xE5)), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(on);
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(ForegroundProperty, Foreground));
        return style;
    }

    private ToggleButton ToolButton(string glyph, string label, bool dark)
    {
        return new ToggleButton
        {
            Content = Glyph(glyph), Width = 32, Height = 30, Margin = new Thickness(1, 0, 1, 0), ToolTip = label, Style = ToolStyle(dark),
        };
    }

    private Button ActionButton(string glyph, string label, bool dark)
    {
        var b = new Button { Content = Glyph(glyph), Width = 32, Height = 30, Margin = new Thickness(1, 0, 1, 0), ToolTip = label };
        b.Style = ButtonStyle(dark, accent: false);
        return b;
    }

    private Button TextButton(string text, bool dark, bool accent = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(3, 0, 3, 0), FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal };
        b.Style = ButtonStyle(dark, accent);
        return b;
    }

    private Style ButtonStyle(bool dark, bool accent)
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.SetValue(Border.BackgroundProperty, accent
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x66, 0xE5))
            : Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, accent ? new SolidColorBrush(Color.FromRgb(0x2A, 0x74, 0xF0)) : new SolidColorBrush(Theme.Gray(dark ? 1 : 0, 0.08)), "Bd"));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.35));
        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(ForegroundProperty, accent ? Brushes.White : Foreground));
        return style;
    }

    private Path Glyph(string data) => new()
    {
        Data = Geometry.Parse(data), Stroke = Foreground, StrokeThickness = 1.6, Width = 18, Height = 18, Stretch = Stretch.None,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    // MARK: Tools

    private double Thickness => (_size switch { 0 => 2.2, 1 => 4, _ => 7 }) * Math.Max(0.6, Math.Min(1.6, 1 / _scale));

    private void ApplyTool()
    {
        if (_captureHandles is not null) _captureHandles.IsEnabled = _undo.Count == 0 && _textEntry is null;
        foreach (var (tool, button) in _toolButtons) button.IsChecked = _tool == tool && (_pictureHost is null || IsVisible);
        for (int i = 0; i < _sizeButtons.Count; i++) _sizeButtons[i].IsChecked = _size == i;
        foreach (var s in _swatches)
        {
            bool on = ((SolidColorBrush)s.Background).Color == _color;
            s.BorderThickness = new Thickness(on ? 2.5 : 1);
            s.BorderBrush = on ? new SolidColorBrush(Theme.AppsDark ? Colors.White : Color.FromRgb(0x1E, 0x66, 0xE5)) : new SolidColorBrush(Theme.Gray(Theme.AppsDark ? 1 : 0, 0.25));
        }

        bool inking = _tool is Tool.Pen or Tool.Highlighter;
        _ink.EditingMode = inking ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        _shapes.IsHitTestVisible = !inking;
        _shapes.Cursor = _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        if (_tool != Tool.Text) CommitText();
        bool hi = _tool == Tool.Highlighter;
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = hi ? Color.FromArgb(0x70, _color.R, _color.G, _color.B) : _color,
            Width = hi ? Thickness * 4 : Thickness,
            Height = hi ? Thickness * 4 : Thickness,
            IsHighlighter = hi,
            StylusTip = hi ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = true,
            IgnorePressure = true,
        };
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_textEntry is not null && _textEntry.IsKeyboardFocused) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_captureDraft && HotKey.Parse(Settings.Current.PinPreviewKey, out var pinModifiers, out var pinVirtualKey)
            && (uint)Keyboard.Modifiers == pinModifiers && (uint)KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key) == pinVirtualKey)
        { Confirm(true); e.Handled = true; return; }
        if (e.Key == Key.Enter) { Confirm(false); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { Undo(); return; }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))) { Redo(); return; }
        if (ctrl && e.Key == Key.C) { CopyComposite(); return; }
        if (ctrl) return;
        switch (e.Key)
        {
            case Key.P: _tool = Tool.Pen; break;
            case Key.H: _tool = Tool.Highlighter; break;
            case Key.C: _tool = Tool.Circle; break;
            case Key.B: case Key.R: _tool = Tool.Box; break;
            case Key.A: _tool = Tool.Arrow; break;
            case Key.T: _tool = Tool.Text; break;
            case Key.X: _tool = Tool.Blur; break;
            case Key.D1: _size = 0; break;
            case Key.D2: _size = 1; break;
            case Key.D3: _size = 2; break;
            default: return;
        }
        ApplyTool(); ActivateCanvas();
    }

    // MARK: Marks, undo, redo

    private void Commit(object mark)
    {
        _undo.Push(mark);
        _redo.Clear();
        _dirty = true;
        ApplyTool();
        _status.Text = "";
        _saveTimer.Stop();
        
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var mark = _undo.Pop();
        if (mark is Stroke s) _ink.Strokes.Remove(s); else if (mark is ShapeMark m) _shapes.Remove(m);
        _redo.Push(mark);
        _dirty = true;
        ApplyTool();
        _saveTimer.Stop(); 
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var mark = _redo.Pop();
        if (mark is Stroke s) _ink.Strokes.Add(s); else if (mark is ShapeMark m) _shapes.Add(m);
        _undo.Push(mark);
        _dirty = true;
        ApplyTool();
        _saveTimer.Stop(); 
    }

    // MARK: Saving

    private void SetCaptureSource(BitmapSource source, Int32Rect region, Point origin)
    {
        _captureSource = source; _captureRect = region; _captureOrigin = origin;
        _captureHandles = new Grid { Visibility = Visibility.Collapsed };
        _captureHandles.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0, 190, 125)), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        foreach (var (hx, hy) in new[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) })
        {
            var handle = new Thumb
            {
                Width = 8, Height = 8, Background = new SolidColorBrush(Color.FromRgb(0, 190, 125)), BorderThickness = new Thickness(0),
                HorizontalAlignment = hx < 0 ? HorizontalAlignment.Left : hx > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                VerticalAlignment = hy < 0 ? VerticalAlignment.Top : hy > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                Cursor = hx == 0 ? Cursors.SizeNS : hy == 0 ? Cursors.SizeWE : hx == hy ? Cursors.SizeNWSE : Cursors.SizeNESW,
            };
            handle.DragStarted += (_, _) => { _cropDragStart = _captureRect; var p = Displays.Cursor(); _cropDragMouse = new Point(p.X, p.Y); };
            handle.DragDelta += (_, e) =>
            {
                var mouse = Displays.Cursor(); int dx = (int)Math.Round(mouse.X - _cropDragMouse.X), dy = (int)Math.Round(mouse.Y - _cropDragMouse.Y);
                int left = _cropDragStart.X, top = _cropDragStart.Y, right = left + _cropDragStart.Width, bottom = top + _cropDragStart.Height;
                if (hx < 0) left = Math.Clamp(left + dx, 0, right - 4);
                if (hx > 0) right = Math.Clamp(right + dx, left + 4, source.PixelWidth);
                if (hy < 0) top = Math.Clamp(top + dy, 0, bottom - 4);
                if (hy > 0) bottom = Math.Clamp(bottom + dy, top + 4, source.PixelHeight);
                ResizeCapture(new Int32Rect(left, top, right - left, bottom - top)); e.Handled = true;
            };
            _captureHandles.Children.Add(handle);
        }
        _page.Children.Add(_captureHandles);
    }

    internal void ToggleCaptureSelection()
    {
        if (_captureHandles is null) return;
        _captureHandles.Visibility = CaptureSelectionVisible ? Visibility.Collapsed : Visibility.Visible;
        Activate();
    }

    internal void ResizeCapture(Int32Rect region)
    {
        if (_captureSource is null || _undo.Count > 0) return;
        if (region.Width < 4 || region.Height < 4 || region.X < 0 || region.Y < 0 || region.X + region.Width > _captureSource.PixelWidth || region.Y + region.Height > _captureSource.PixelHeight) return;
        _captureRect = region;
        _image = new CroppedBitmap(_captureSource, region); _image.Freeze();
        ((Image)((Border)_page.Children[0]).Child).Source = _image;
        _page.Width = Width = region.Width * _scale; _page.Height = Height = region.Height * _scale;
        var monitor = Displays.Nearest(new Native.POINT((int)_captureOrigin.X, (int)_captureOrigin.Y));
        Left = (_captureOrigin.X + region.X) / monitor.Scale; Top = (_captureOrigin.Y + region.Y) / monitor.Scale;
    }

    internal BitmapSource RenderComposite()
    {
        CommitText();
        int pw = _image.PixelWidth, ph = _image.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_image, new Rect(0, 0, pw, ph));
            dc.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));
            _ink.Strokes.Draw(dc); _shapes.Draw(dc); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private bool Save()
    {
        CommitText();
        if (!_dirty) return true;
        try
        {
            if (_itemId is { } id && _line.Find(id) is { } current) _path = current.Path;
            if (!_captureDraft) Originals.Keep(_path);
            var image = RenderComposite(); var tmp = _path + ".tmp";
            Thumbnails.SavePng(image, tmp); File.Move(tmp, _path, true);
            _dirty = false; _line.ReloadThumbnail(_path); return true;
        }
        catch (Exception e) { Log.Error($"编辑保存失败: {e}"); return false; }
    }

    internal void Confirm(bool pin)
    {
        try
        {
            Guid? id = _itemId;
            if (_captureDraft)
            {
                // A draft becomes a persistent clothesline item only when pinned.
                if (!pin)
                {
                    if (!CustomChecks.IsRunning) CopyComposite();
                    _accepted = true;
                    Close();
                    return;
                }
                _path = Library.Store(RenderComposite());
                id = _line.Hang(_path);
                if (id is null) throw new IOException("截图已保存，但无法挂到晾衣绳，请重新打开软件恢复。");
            }
            else if (!Save()) throw new IOException("保存编辑结果失败，请检查文件夹和磁盘空间。");
            _accepted = true;
            Close();
            if (id is { } itemId && _line.Find(itemId) is { } item)
            {
                if (!CustomChecks.IsRunning) _line.Copy(itemId);
                if (pin) PictureWindow.Pin(_line, item);
            }
        }
        catch (Exception e) { MessageBox.Show($"无法完成编辑：{e.Message}", "Snapline", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CopyComposite()
    {
        var image = RenderComposite(); var data = new DataObject();
        data.SetImage(image); data.SetData(ClipboardWatcher.OwnFormat, true); Clipboard.SetDataObject(data, true);
    }
    internal void AddVerificationMark()
    {
        var mark = new ShapeMark(Tool.Box, new Point(30 * _scale, 30 * _scale), new Point(180 * _scale, 120 * _scale), Colors.Red, 4 * _scale);
        _shapes.Add(mark); Commit(mark);
    }
    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存编辑后的图片", Filter = "PNG 图片|*.png", DefaultExt = ".png", FileName = $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(_pictureHost ?? (Window)this) != true) return;
        try { Thumbnails.SavePng(RenderComposite(), dialog.FileName); Confirm(false); }
        catch (Exception e) { MessageBox.Show($"保存失败：{e.Message}", "Snapline"); }
    }
    private void Revert()
    {
        _saveTimer.Stop(); _ink.Strokes.Clear(); _shapes.Clear(); _undo.Clear(); _redo.Clear();
        _dirty = false; ApplyTool();
    }

    /// <summary>Draws a stroke and a circle by code, saves, undoes one, saves again. For checking the pipeline without a desktop.</summary>
    public static bool SelfTest(string path, Line line)
    {
        Show(path, line);
        if (!Open.TryGetValue(path, out var w)) return false;
        try
        {
            long before = new FileInfo(path).Length;
            var points = new StylusPointCollection();
            for (int i = 0; i < 40; i++) points.Add(new StylusPoint(20 + i * 4, 30 + Math.Sin(i / 4.0) * 12));
            var stroke = new Stroke(points, w._ink.DefaultDrawingAttributes);
            w._ink.Strokes.Add(stroke);
            w.Commit(stroke);
            var circle = new ShapeMark(Tool.Circle, new Point(60, 40), new Point(160, 110), w._color, w.Thickness);
            w._shapes.Add(circle);
            w.Commit(circle);
            w._saveTimer.Stop();
            w.Save();
            bool savedOnce = !w._dirty && Originals.Exists(w._path);
            w.Undo();
            w._saveTimer.Stop();
            w.Save();
            bool undone = w._undo.Count == 1 && w._redo.Count == 1;
            var reread = Thumbnails.Load(path, int.MaxValue, 2);
            bool sameSize = reread is not null && reread.Value.PixelWidth == w._image.PixelWidth && reread.Value.PixelHeight == w._image.PixelHeight;
            Log.Notice($"Markup self test: savedOnce={savedOnce} undone={undone} sameSize={sameSize} bytes {before}->{new FileInfo(path).Length}");
            return savedOnce && undone && sameSize;
        }
        catch (Exception e)
        {
            Log.Error($"Markup self test failed: {e}");
            return false;
        }
        finally { w.Close(); }
    }

    /// <summary>The editor as a plain element, driven by code: for the demo film.</summary>
    public sealed class DemoHandle
    {
        private readonly MarkupWindow _w;
        internal DemoHandle(MarkupWindow w) { _w = w; }
        public FrameworkElement Content { get; internal set; } = null!;
        public double Width => _w.Width;
        public double Height => _w.Height;
        public double Scale => _w._scale;
        public Point PagePoint(double imageX, double imageY) => new(imageX * _w._scale, imageY * _w._scale);
        public void Tool(string name) { _w._tool = Enum.Parse<MarkupWindow.Tool>(name, true); _w.ApplyTool(); }
        public void Colour(int index) { _w._color = Palette[index]; _w.ApplyTool(); }
        public void Size(int size) { _w._size = size; _w.ApplyTool(); }
        /// <summary>A pen stroke through these page points, as if drawn now.</summary>
        public void Stroke(IEnumerable<Point> points)
        {
            var pts = new StylusPointCollection(points.Select(p => new StylusPoint(p.X, p.Y)));
            var s = new Stroke(pts, _w._ink.DefaultDrawingAttributes.Clone());
            _w._ink.Strokes.Add(s);
            _w.Commit(s);
        }
        /// <summary>A partial stroke shown while the pointer moves; replaced by the next call.</summary>
        private Stroke? _liveStroke;
        public void LiveStroke(IEnumerable<Point> points)
        {
            if (_liveStroke is not null) _w._ink.Strokes.Remove(_liveStroke);
            var list = points.ToList();
            if (list.Count < 2) { _liveStroke = null; return; }
            _liveStroke = new Stroke(new StylusPointCollection(list.Select(p => new StylusPoint(p.X, p.Y))), _w._ink.DefaultDrawingAttributes.Clone());
            _w._ink.Strokes.Add(_liveStroke);
        }
        public void EndLiveStroke() { if (_liveStroke is not null) { _w.Commit(_liveStroke); _liveStroke = null; } }
        public void LiveShape(Point from, Point to) { _w._shapes.ShowLive(new ShapeMark(_w._tool, from, to, _w._color, _w.Thickness)); }
        public void Shape(Point from, Point to)
        {
            _w._shapes.ShowLive(null);
            var m = new ShapeMark(_w._tool, from, to, _w._color, _w.Thickness);
            _w._shapes.Add(m);
            _w.Commit(m);
        }
        public void Text(Point at, string text)
        {
            var m = new ShapeMark(MarkupWindow.Tool.Text, at, at, _w._color, _w.Thickness, text);
            _w._shapes.Add(m);
            _w.Commit(m);
        }
        public void Undo() => _w.Undo();
        public void Save() { _w._saveTimer.Stop(); _w.Save(); }
    }

    public static DemoHandle ForDemo(string path, Line line)
    {
        var full = Thumbnails.Load(path, int.MaxValue, 4) ?? throw new FileNotFoundException(path);
        var w = new MarkupWindow(path, full, line);
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        return new DemoHandle(w) { Content = content };
    }

    // MARK: Shapes

    internal sealed record ShapeMark(Tool Kind, Point From, Point To, Color Color, double Thickness, string? Text = null);

    internal static double FontSizeFor(double thickness) => thickness * 4.5 + 6;

    // MARK: Text

    private TextBox? _textEntry;

    /// <summary>A click with the text tool: type where you clicked, Enter to keep it, Esc to drop it.</summary>
    private void BeginText(Point at)
    {
        CommitText();
        var box = new TextBox
        {
            MinWidth = 60, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x1E, 0x66, 0xE5)), BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(_color), FontSize = FontSizeFor(Thickness), FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(at.X - 3, at.Y - FontSizeFor(Thickness) * 0.7, 0, 0),
            Tag = at,
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; DropText(); }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; CommitText(); }
        };
        box.LostKeyboardFocus += (_, _) => CommitText();
        _textEntry = box;
        _page.Children.Add(box);
        box.Focus();
    }

    private void CommitText()
    {
        var box = _textEntry;
        if (box is null) return;
        _textEntry = null;
        _page.Children.Remove(box);
        var text = box.Text.TrimEnd();
        if (text.Length == 0) return;
        var at = (Point)box.Tag;
        var mark = new ShapeMark(Tool.Text, at, at, ((SolidColorBrush)box.Foreground).Color, (box.FontSize - 6) / 4.5, text);
        _shapes.Add(mark);
        Commit(mark);
    }

    private void DropText()
    {
        var box = _textEntry;
        if (box is null) return;
        _textEntry = null;
        _page.Children.Remove(box);
    }

    // MARK: Blur

    private BitmapSource? _pixelated;

    /// <summary>The picture in coarse blocks, for hiding what should not be shared.</summary>
    private BitmapSource Pixelated() => _pixelated ??= Pixelate(_image);

    internal static BitmapSource Pixelate(BitmapSource image)
    {
        int block = Math.Clamp(image.PixelWidth / 96, 8, 40);
        var small = new TransformedBitmap(image, new ScaleTransform(1.0 / block, 1.0 / block));
        small.Freeze();
        return small;
    }

    /// <summary>Circles, boxes, arrows, text and blurs. They live here, above the ink.</summary>
    private sealed class ShapeLayer : FrameworkElement
    {
        private readonly MarkupWindow _owner;
        private readonly List<ShapeMark> _marks = new();
        private ShapeMark? _live;
        private Point _start;

        public ShapeLayer(MarkupWindow owner)
        {
            _owner = owner;
            // Blurs are drawn as enlarged blocks: no smoothing.
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        public void Add(ShapeMark m) { _marks.Add(m); InvalidateVisual(); }
        public void ShowLive(ShapeMark? m) { _live = m; InvalidateVisual(); }
        public void Remove(ShapeMark m) { _marks.Remove(m); InvalidateVisual(); }
        public void Clear() { _marks.Clear(); InvalidateVisual(); }

        protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _start = e.GetPosition(this);
            if (_owner._tool == Tool.Text)
            {
                _owner.BeginText(_start);
                e.Handled = true;
                return;
            }
            _live = new ShapeMark(_owner._tool, _start, _start, _owner._color, _owner.Thickness);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_live is null) return;
            _live = _live with { To = e.GetPosition(this) };
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_live is null) return;
            ReleaseMouseCapture();
            var mark = _live with { To = e.GetPosition(this) };
            _live = null;
            if ((mark.To - mark.From).Length >= 4) { _marks.Add(mark); _owner.Commit(mark); }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            Draw(dc);
        }

        public void Draw(DrawingContext dc)
        {
            // Blurs go under everything else, so a circle around a hidden bit stays visible.
            foreach (var m in _marks) if (m.Kind == Tool.Blur) DrawMark(dc, m);
            if (_live is { Kind: Tool.Blur }) DrawMark(dc, _live);
            foreach (var m in _marks) if (m.Kind != Tool.Blur) DrawMark(dc, m);
            if (_live is { } live && live.Kind != Tool.Blur) DrawMark(dc, live);
        }

        private void DrawMark(DrawingContext dc, ShapeMark m) => MarkupWindow.DrawMark(dc, m,
            m.Kind == Tool.Blur ? _owner.Pixelated() : null, new Rect(0, 0, _owner._page.Width, _owner._page.Height));
    }

    internal static void DrawMark(DrawingContext dc, ShapeMark m, BitmapSource? pixelated, Rect imageBounds)
    {
        var pen = new Pen(new SolidColorBrush(m.Color), m.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var rect = new Rect(m.From, m.To);
        switch (m.Kind)
        {
            case Tool.Text:
            {
                var ft = new FormattedText(m.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture,
                    Strings.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    FontSizeFor(m.Thickness), new SolidColorBrush(m.Color), 1.0);
                var origin = new Point(m.From.X, m.From.Y - FontSizeFor(m.Thickness) * 0.7);
                // A thin halo in the opposite tone keeps the words readable over anything.
                var halo = m.Color.R + m.Color.G + m.Color.B > 380 ? Color.FromArgb(0xA0, 0, 0, 0) : Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF);
                var geometry = ft.BuildGeometry(origin);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(halo), Math.Max(1.5, m.Thickness * 0.5)) { LineJoin = PenLineJoin.Round }, geometry);
                dc.DrawText(ft, origin);
                break;
            }
            case Tool.Blur:
            {
                if (rect.Width < 1 || rect.Height < 1 || pixelated is null) break;
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawImage(pixelated, imageBounds);
                dc.Pop();
                break;
            }
            case Tool.Circle:
                dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                break;
            case Tool.Box:
                dc.DrawRoundedRectangle(null, pen, rect, m.Thickness, m.Thickness);
                break;
            case Tool.Arrow:
            {
                dc.DrawLine(pen, m.From, m.To);
                var dir = m.To - m.From;
                if (dir.Length < 1) break;
                dir.Normalize();
                double head = Math.Max(10, m.Thickness * 4);
                var left = m.To - dir * head + new Vector(-dir.Y, dir.X) * head * 0.55;
                var right = m.To - dir * head - new Vector(-dir.Y, dir.X) * head * 0.55;
                dc.DrawLine(pen, m.To, left);
                dc.DrawLine(pen, m.To, right);
                break;
            }
        }
    }
}
