using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Snapline.Core;
using Snapline.Interop;
using Tool = Snapline.UI.MarkupWindow.Tool;

namespace Snapline.UI;

public sealed partial class CaptureWindow
{
    private const double ToolbarGap = 9;
    private static readonly Brush ActiveToolBrush = Frozen(new SolidColorBrush(Color.FromRgb(210, 242, 230)));
    private readonly List<(Tool? Tool, Button Button)> _tools = new();
    private readonly Border _toolbar;
    private readonly TranslateTransform _toolbarOffset = new();
    private Button _undoButton = null!;
    private Tool? _tool;
    private TextBox? _text;
    private Point _textPoint;
    private Size _toolbarSize;
    internal FrameworkElement CaptureToolbar => _toolbar;

    private Border BuildToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5) };

        Button Add(string icon, string label, Action action, Brush? stroke = null)
        {
            var button = PictureWindow.ToolButton(icon, label, action);
            button.Focusable = false;
            if (stroke is not null) ((System.Windows.Shapes.Path)button.Content).Stroke = stroke;
            bar.Children.Add(button);
            return button;
        }

        void AddTool(Tool? tool, string icon, string label)
        {
            _tools.Add((tool, Add(icon, label, () => PickTool(_tool == tool ? null : tool))));
        }

        void Divide()
        {
            bar.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(5, 0, 5, 0),
                Background = Frozen(new SolidColorBrush(Color.FromRgb(222, 225, 228))) });
        }

        AddTool(null, "M9,1 V17 M1,9 H17 M6,4 L9,1 L12,4 M6,14 L9,17 L12,14 M4,6 L1,9 L4,12 M14,6 L17,9 L14,12", "移动选框");
        AddTool(Tool.Box, "M3,3 H15 V15 H3 Z", "矩形");
        AddTool(Tool.Circle, "M9,2 A7,7 0 1 1 8.9,2", "椭圆");
        AddTool(Tool.Arrow, "M3,15 L15,3 M8,3 H15 V10", "箭头");
        AddTool(Tool.Pen, "M3,13 L11,5 L13,7 L5,15 Z M2,16 L5,15", "画笔");
        AddTool(Tool.Blur, "M3,3 H15 V15 H3 Z M3,7 H15 M3,11 H15 M7,3 V15 M11,3 V15", "马赛克");
        AddTool(Tool.Text, "M3,4 H15 M9,4 V15 M6,15 H12", "文字");
        _tools[0].Button.Background = ActiveToolBrush;
        Divide();
        _undoButton = Add("M6,4 L2,8 L6,12 M2,8 H11 A4,4 0 0 1 11,16 H8", "撤销（Ctrl+Z）", UndoMark);
        _undoButton.IsEnabled = false;
        Add("M9,2 V12 M5,8 L9,12 L13,8 M3,11 V16 H15 V11", "保存为 PNG", SaveCapture);
        var settings = Settings.Current;
        string pinKeys = HotKey.Same(settings.PinPreviewKey, settings.ClipboardHangKey) ? settings.PinPreviewKey
            : $"{settings.ClipboardHangKey} 或 {settings.PinPreviewKey}";
        Add("M6,2 H12 L11,7 L14,10 H10 V16 H8 V10 H4 L7,7 Z", $"钉到桌面并上晾衣绳（{pinKeys}）", () => AcceptCapture(pin: true));
        Divide();
        Add("M3,3 L15,15 M15,3 L3,15", "取消截图（Esc）", Finish, Brushes.IndianRed);
        Add("M2,9 L7,14 L16,3", "复制截图（Enter）", () => AcceptCapture(), Frozen(new SolidColorBrush(Color.FromRgb(0, 151, 103))));
        return new Border
        {
            Background = Frozen(new SolidColorBrush(Color.FromRgb(250, 250, 250))),
            BorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(224, 225, 228))),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = bar, RenderTransform = _toolbarOffset, Visibility = Visibility.Collapsed
        };
    }

    private void ShowToolbar(Int32Rect region)
    {
        _toolbar.Visibility = Visibility.Visible;
        var screen = ToLocal(new Int32Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
        _toolbar.LayoutTransform = Transform.Identity;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _toolbarSize = _toolbar.DesiredSize;
        if (_toolbarSize.Width > screen.Width)
        {
            double scale = screen.Width / _toolbarSize.Width;
            _toolbar.LayoutTransform = new ScaleTransform(scale, scale);
            _toolbarSize = new Size(screen.Width, _toolbarSize.Height * scale);
        }
        PlaceToolbar(region);
    }

    private void PlaceToolbar(Int32Rect region)
    {
        var rect = ToLocal(region);
        double width = _screen.PixelWidth / _dpi.DpiScaleX, height = _screen.PixelHeight / _dpi.DpiScaleY;
        _toolbarOffset.X = Math.Clamp(rect.Right - _toolbarSize.Width, 0, Math.Max(0, width - _toolbarSize.Width));
        double below = rect.Bottom + ToolbarGap;
        double above = rect.Top - ToolbarGap - _toolbarSize.Height;
        double y = below + _toolbarSize.Height <= height ? below : above >= 0 ? above
            : rect.Top > height - rect.Bottom ? 0 : height - _toolbarSize.Height;
        _toolbarOffset.Y = Math.Clamp(y, 0, Math.Max(0, height - _toolbarSize.Height));
    }

    private void CaptureKeyDown(object sender, KeyEventArgs e)
    {
        UpdateSquareKey(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            e.Handled = true;
            Finish();
        }
        else if (key == Key.Enter && _text is not null)
        {
            e.Handled = true;
            CommitText();
        }
        else if (_text is null && !_region.IsEmpty && _gesture == Gesture.None)
        {
            var modifiers = Keyboard.Modifiers;
            if (key == Key.Enter && modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                AcceptCapture();
            }
            else if (key == Key.Z && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                UndoMark();
            }
            else if (IsShortcut(Settings.Current.ClipboardHangKey, key, modifiers)
                || IsShortcut(Settings.Current.PinPreviewKey, key, modifiers))
            {
                e.Handled = true;
                AcceptCapture(pin: true);
            }
        }
    }

    private static bool IsShortcut(string shortcut, Key key, ModifierKeys modifiers) =>
        HotKey.Parse(shortcut, out uint mods, out uint vk)
        && mods == (uint)modifiers && vk == KeyInterop.VirtualKeyFromKey(key);

    private void PickTool(Tool? tool)
    {
        CommitText();
        _tool = tool;
        foreach (var (kind, button) in _tools)
            button.Background = kind == tool ? ActiveToolBrush : Brushes.Transparent;
        UpdateCursor(Mouse.GetPosition(_canvas));
    }

    private void UpdateCursor(Point local)
    {
        var cursor = !_region.IsEmpty && ToLocal(_region).Contains(local) ? _tool switch
        {
            null => Cursors.SizeAll,
            Tool.Text => Cursors.IBeam,
            _ => Cursors.Cross
        } : Cursors.Cross;
        if (Cursor != cursor) Cursor = cursor;
    }

    private void UpdateMarkOffset()
    {
        var bounds = ToLocal(_region);
        _marksOffset.X = bounds.X;
        _marksOffset.Y = bounds.Y;
    }

    private void DrawMarks()
    {
        _undoButton.IsEnabled = _markup.CanUndo;
        using var dc = _marks.Drawing.RenderOpen();
        if (!_region.IsEmpty) _markup.Draw(dc, _region, _dpi);
    }

    private void UndoMark()
    {
        CommitText();
        _markup.Undo();
        DrawMarks();
    }

    private void BeginText(Point local)
    {
        var bounds = ToLocal(_region);
        double fontSize = MarkupWindow.FontSizeFor(3);
        double x = Math.Clamp(local.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - 60));
        double y = Math.Clamp(local.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - fontSize - 8));
        _textPoint = MarkPoint(new Point(x + 4, y + 3 + fontSize * 0.7));
        _text = new TextBox
        {
            FontSize = fontSize, Foreground = Brushes.IndianRed, Background = Brushes.White,
            Padding = new Thickness(3, 2, 3, 2), Width = Math.Max(40, Math.Min(230, bounds.Right - x)),
            MaxLength = 240
        };
        Canvas.SetLeft(_text, x);
        Canvas.SetTop(_text, y);
        _canvas.Children.Add(_text);
        _text.Focus();
    }

    private void CommitText()
    {
        if (_text is null) return;
        _markup.AddText(_textPoint, _text.Text);
        DropText();
        DrawMarks();
        _canvas.Focus();
    }

    private void DropText()
    {
        if (_text is not { } box) return;
        _text = null;
        _canvas.Children.Remove(box);
    }

    private void SaveCapture()
    {
        if (_region.IsEmpty || _gesture != Gesture.None || _closed) return;
        CommitText();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图", Filter = "PNG 图片|*.png", DefaultExt = ".png",
            FileName = $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) == true) AcceptCapture(savePath: dialog.FileName);
    }

    internal void AcceptCapture(bool pin = false, string? savePath = null)
    {
        if (_region.IsEmpty || _gesture != Gesture.None || _closed) return;
        CommitText();
        try
        {
            var image = _markup.Render(_region, _dpi);
            if (savePath is not null) Thumbnails.SavePng(image, savePath);
            var screenPosition = new Point(_monitor.Bounds.Left + _region.X, _monitor.Bounds.Top + _region.Y);
            var finished = DetachFinish();
            Exception? clipboardError = null;
            try { CaptureResult.Complete(image, _line, screenPosition); }
            catch (Exception ex) { clipboardError = ex; }
            finally { finished?.Invoke(); Preview.RefreshPinShortcut(); }
            if (pin) CaptureResult.PinCurrent();
            if (clipboardError is not null)
                MessageBox.Show($"截图已完成，但复制失败：{clipboardError.Message}", "Snapline");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法完成截图：{ex.Message}", "Snapline", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
