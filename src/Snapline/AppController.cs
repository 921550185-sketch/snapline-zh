using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snapline.Core;
using Snapline.Interop;
using Snapline.UI;
using static Snapline.Interop.Native;

namespace Snapline;

public sealed class AppController : IDisposable
{
    private readonly Line _line = new();
    private readonly LineWindow _panel;
    private readonly MessageWindow _messages = new();
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _mouseTimer;
    private bool _disposed, _pointerDirty = true;
    private int _themeRefreshQueued;
    private PointerState? _lastPointerState;
    private Native.RECT _panelFrame;
    private readonly record struct PointerState(int X, int Y, IntPtr Window, bool Visible, bool Capturing,
        bool SourceDragging, Guid? Dragging, Guid? Pressed, bool RopeDragging, bool Reordering, bool MenuOpen,
        bool Animating, long CanvasRevision, long PictureRevision, long CaptureRevision, bool SettingsActive, bool SettingsOpen, bool Editing, bool TypingText);
    private HotKey? _hotKey, _captureHotKey, _clipboardHotKey;
    private bool _readingClipboard, _clipboardFailureNotified;
    private DateTime _clipboardRetryAt;
    private bool _visible = true, _capturing;
    private readonly bool _isolatedTest;
    internal Line Model => _line;
    internal long TickInvocations { get; private set; }
    internal long EffectiveTicks { get; private set; }
    internal long TickAllocatedBytes { get; private set; }
    internal bool MeasureTickAllocations { get; set; }
    internal IDataObject? VerificationClipboard { get; set; }
    internal Func<IDataObject?>? VerificationClipboardReader { get; set; }

    public AppController(bool isolatedTest = false)
    {
        _isolatedTest = isolatedTest;
        OleInitialize(IntPtr.Zero);
        Theme.ApplySetting(); Theme.Apply(); Strings.Refresh();
        _panel = new LineWindow(_line);
        _panel.PlaceOn(); _panel.Show(); _panel.OrderFront();
        _panel.Canvas.Revealed = true;
        _tray = new TrayIcon { MenuProvider = BuildMenu };
        _tray.LeftClick += Toggle;
        _panel.Canvas.MenuRequested += () => Menus.Show(BuildMenu());
        _panel.Canvas.CaptureRequested += Capture;
        // Clipboard images enter only through the explicit import shortcut. There
        // is no clipboard monitor and copied files never create cards automatically.
        Settings.Current.CatchClipboard = false;
        RegisterHotKeys();
        _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _mouseTimer.Tick += (_, _) => Tick(); _mouseTimer.Start();
        _line.Destroyed += PictureWindow.CloseFor;
        _line.ItemUpdated += PictureWindow.ReloadFor;
        _line.Fall += AnimateFall;
        _line.ItemsChanged += InvalidatePointerState;
        _line.StateChanged += InvalidatePointerState;
        _line.ItemUpdated += ItemChanged;
        _panel.LocationChanged += (_, _) => PanelGeometryChanged();
        _panel.SizeChanged += (_, _) => PanelGeometryChanged();
        PanelGeometryChanged();
        Microsoft.Win32.SystemEvents.SessionEnding += SessionEnding;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        Settings.Current.Welcomed = true;
        _line.Save();
    }

    private void AnimateFall(Pegged item)
    {
        if (!Native.IsWindowVisible(_panel.Handle) || _panel.Display is not { } monitor) return;
        var card = _panel.Canvas.Cards.FirstOrDefault(c => c.Item.Id == item.Id && c.Visibility == Visibility.Visible);
        if (card is null) return;
        var rect = card.CardRect;
        var topLeft = _panel.Canvas.PointToScreen(rect.TopLeft); var bottomRight = _panel.Canvas.PointToScreen(rect.BottomRight);
        FlightWindow.Fall(item.Thumb, new Native.RECT((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y)),
            item.Tilt, monitor, item.Id.GetHashCode());
    }

    private void SessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e) => _line.Save();
    private void DisplayChanged(object? sender, EventArgs e) => _panel.Dispatcher.BeginInvoke(() => _panel.PlaceOn());
    private void InvalidatePointerState() => _pointerDirty = true;
    private void ItemChanged(Pegged item) => InvalidatePointerState();
    private void PanelGeometryChanged()
    {
        if (_panel.Handle != IntPtr.Zero) Native.GetWindowRect(_panel.Handle, out _panelFrame);
        InvalidatePointerState();
    }
    private void UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color
            or Microsoft.Win32.UserPreferenceCategory.VisualStyle or Microsoft.Win32.UserPreferenceCategory.Window) QueueThemeRefresh();
    }
    internal void QueueThemeRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _themeRefreshQueued, 1) != 0) return;
        _panel.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            Interlocked.Exchange(ref _themeRefreshQueued, 0);
            if (_disposed) return;
            Theme.Refresh(); InvalidatePointerState();
        }));
    }
    internal void PollPointer() => Tick();

    private void RegisterHotKeys()
    {
        _hotKey?.Dispose(); _captureHotKey?.Dispose();
        Preview.ClearPinShortcut();
        _hotKey = new HotKey(_messages, Settings.Current.HotKey, Toggle);
        _captureHotKey = new HotKey(_messages, Settings.Current.CaptureHotKey, Capture);
        RefreshClipboardShortcut();
        Log.Notice($"快捷键注册：截图 {Settings.Current.CaptureHotKey}={_captureHotKey.Registered}；晾衣绳 {Settings.Current.HotKey}={_hotKey.Registered}");
        if (!_isolatedTest && (!_hotKey.Registered || !_captureHotKey.Registered))
            _tray.Balloon("快捷键注册失败", "快捷键可能被其他软件占用，请在设置的自定义快捷键区域修改。");
    }

    private void RefreshClipboardShortcut()
    {
        bool enabled = !_disposed && !_capturing && !SettingsWindow.AnyOpen && !MarkupWindow.IsTypingText;
        string key = Settings.Current.ClipboardHangKey;
        bool retry = _clipboardHotKey?.Registered == false && DateTime.UtcNow >= _clipboardRetryAt;
        if (!enabled || _clipboardHotKey?.Text != key || retry)
        {
            if (_clipboardHotKey?.Text != key || !enabled) _clipboardFailureNotified = false;
            _clipboardHotKey?.Dispose(); _clipboardHotKey = null;
            Preview.SharePinShortcut(null);
        }
        if (enabled && _clipboardHotKey is null)
        {
            Preview.ClearPinShortcut();
            _clipboardHotKey = new HotKey(_messages, key, ClipboardShortcut);
            _clipboardRetryAt = DateTime.UtcNow.AddSeconds(2);
            Log.Notice($"快捷键注册：复制图片上杆 {key}={_clipboardHotKey.Registered}");
            if (!_isolatedTest && !_clipboardHotKey.Registered && !_clipboardFailureNotified)
                _tray.Balloon("快捷键注册失败", $"复制图片上杆快捷键 {key} 被占用，可在设置中修改。");
            _clipboardFailureNotified = !_clipboardHotKey.Registered;
            Preview.SharePinShortcut(_clipboardHotKey);
        }
    }

    private async void ClipboardShortcut()
    {
        if (_disposed || _readingClipboard || _capturing || SettingsWindow.AnyOpen || MarkupWindow.IsTypingText) return;
        _readingClipboard = true;
        bool shared = HotKey.Same(Settings.Current.ClipboardHangKey, Settings.Current.PinPreviewKey);
        try
        {
            var clipboard = await ClipboardWatcher.ReadWithRetry(() =>
            {
                var data = _isolatedTest ? (VerificationClipboardReader is { } read ? read() : VerificationClipboard) : Clipboard.GetDataObject();
                var image = data is null ? null : ClipboardWatcher.ReadImage(data);
                return (image, own: data?.GetDataPresent(ClipboardWatcher.OwnFormat) == true);
            });
            if (_disposed || _capturing || SettingsWindow.AnyOpen || MarkupWindow.IsTypingText) return;
            // Preserve a shared F3 pin key for this app's captures/previews. A newly
            // copied external image takes precedence over any older pending capture.
            var externalImage = !clipboard.own ? clipboard.image : null;
            if (shared && externalImage is null && (Preview.PinCurrent() || CaptureResult.PinCurrent())) return;
            HangClipboardBitmap(clipboard.image);
        }
        catch (Exception e)
        {
            Log.Error($"复制图片上杆失败：{e}");
            if (!_isolatedTest) _tray.Balloon("无法加入图片", "请稍后重试，或检查图片保存位置是否可写。");
        }
        finally { _readingClipboard = false; }
    }

    internal bool HangClipboardImage(IDataObject? data)
    {
        if (_capturing || SettingsWindow.AnyOpen || MarkupWindow.IsTypingText) return false;
        var image = data is null ? null : ClipboardWatcher.ReadImage(data);
        return HangClipboardBitmap(image);
    }

    private bool HangClipboardBitmap(BitmapSource? image)
    {
        if (image is null)
        {
            if (!_isolatedTest) _tray.Balloon("剪贴板中没有图片", $"请先复制图片，再按 {Settings.Current.ClipboardHangKey} 挂上晾衣绳。");
            return false;
        }
        var id = _line.Hang(Library.Store(image));
        if (id is null) return false;
        CaptureResult.Clear();
        if (!_visible) Toggle();
        else { _panel.OrderFront(); _panel.Canvas.Revealed = true; }
        InvalidatePointerState();
        return true;
    }

    public void Toggle()
    {
        _visible = !_visible;
        InvalidatePointerState();
        if (_capturing) return;
        if (_visible)
        {
            _panel.PlaceOn(); _panel.OrderFront(); _panel.Canvas.Revealed = true;
        }
        else
        {
            Preview.Update(null, _line);
            _panel.Canvas.Revealed = false; _panel.OrderOut();
        }
    }

    private void Tick()
    {
        TickInvocations++;
        long allocatedBefore = MeasureTickAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try { TickCore(); }
        finally { if (MeasureTickAllocations) TickAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore; }
    }

    private void TickCore()
    {
        // Continue cheap native hit testing: a foreign popup can cover a picture while
        // the pointer is stationary. Skip the managed scans only when *all* state agrees.
        var mouse = Displays.Cursor(); var pointerWindow = WindowFromPoint(mouse);
        if (_clipboardHotKey?.Registered == false && DateTime.UtcNow >= _clipboardRetryAt) RefreshClipboardShortcut();
        bool sourceDragging = DragSource.IsDragging, reordering = _panel.Canvas.AnyReordering;
        var state = new PointerState(mouse.X, mouse.Y, pointerWindow, _visible, _capturing,
            sourceDragging, _line.DraggingId, _line.PressedId, _panel.Canvas.RopeDragging, reordering, _line.MenuOpen,
            _panel.Canvas.IsAnimating, _panel.Canvas.InteractionRevision, PictureWindow.Revision, CaptureResult.Revision, SettingsWindow.AnyActive, SettingsWindow.AnyOpen, MarkupWindow.AnyEditing, MarkupWindow.IsTypingText);
        bool movingHitArea = _visible && !_capturing && state.Animating && _panelFrame.Contains(mouse);
        bool previewDue = _visible && !_capturing && Preview.NeedsTimedUpdate;
        if (!_pointerDirty && _lastPointerState == state && !movingHitArea && !previewDue) return;
        _pointerDirty = false; _lastPointerState = state;
        EffectiveTicks++;
        RefreshClipboardShortcut();
        PictureWindow.UpdateShortcutTarget(mouse, pointerWindow);
        if (_capturing) Preview.ClearPinShortcut(); else Preview.RefreshPinShortcut();
        if (_capturing || !_visible) return;
        bool dragging = sourceDragging || state.Dragging is not null;
        bool holding = state.Pressed is not null || state.RopeDragging || reordering;
        _panel.HoldMouse = dragging || holding;
        _panel.ClickThrough = !(dragging || holding || _line.MenuOpen || _panel.IsOverPhoto(mouse));
        _panel.Canvas.UpdateHover(dragging || holding ? null : _panel.ToCanvas(mouse));
        var hovered = _panel.Canvas.Hovered;
        Preview.Update(holding || dragging || _line.MenuOpen ? null : hovered, _line);
    }

    public void Capture()
    {
        if (_capturing) return;
        _capturing = true;
        RefreshClipboardShortcut();
        CaptureResult.Clear();
        Preview.Close(); _panel.OrderOut(); PictureWindow.PrepareForCapture();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed) return;
            try
            {
                // Let the compositor apply the hidden toolbar and clothesline before reading the desktop.
                DwmFlush();
                CaptureWindow.Begin(_line, () =>
                {
                    _capturing = false; PictureWindow.FinishCapture();
                    if (_visible) { _panel.OrderFront(); _panel.Canvas.Revealed = true; }
                });
            }
            catch (Exception e)
            {
                _capturing = false; PictureWindow.FinishCapture();
                if (_visible) _panel.OrderFront();
                MessageBox.Show($"截图失败：{e.Message}", "Snapline", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    public void ApplySettings()
    {
        InvalidatePointerState();
        Theme.ApplySetting(); Strings.Refresh(); Pegs.RaiseLookChanged(); _tray.Repaint();
        _panel.PlaceOn(_panel.Display);
        if (_hotKey?.Text != Settings.Current.HotKey || _captureHotKey?.Text != Settings.Current.CaptureHotKey) RegisterHotKeys();
        RefreshClipboardShortcut(); Preview.RefreshPinShortcut();
        PictureWindow.ClearShortcutTarget();
        Settings.Current.CatchClipboard = false;
    }

    private void ClearLine()
    {
        int failed = _line.DestroyAll();
        InvalidatePointerState();
        if (failed > 0 && !_isolatedTest)
            _tray.Balloon("部分图片未能删除", $"有 {failed} 张图片未能删除，仍保留在晾衣绳上，请稍后重试。");
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { FlowDirection = Strings.Flow };
        menu.Opened += (_, _) => _line.MenuOpen = true;
        menu.Closed += (_, _) => _line.MenuOpen = false;
        menu.Items.Add(PeggedControl.MenuItemFor(_visible ? Strings.HideLine : Strings.ShowLine, Toggle, Settings.Current.HotKey));
        menu.Items.Add(PeggedControl.MenuItemFor(Strings.NewCapture, Capture, Settings.Current.CaptureHotKey));
        menu.Items.Add(PeggedControl.MenuItemFor("复制的图片上晾衣绳", ClipboardShortcut, Settings.Current.ClipboardHangKey));
        var arrange = new MenuItem { Header = Strings.AutoArrange, IsCheckable = true,
            IsChecked = Settings.Current.AutoArrange, ToolTip = Strings.AutoArrangeTip };
        arrange.Click += (_, _) =>
        {
            Settings.Current.AutoArrange = arrange.IsChecked;
            Settings.Current.Save();
            _panel.Canvas.RelayoutNow();
            InvalidatePointerState();
        };
        menu.Items.Add(arrange);
        if (_panel.Canvas.PageCount > 1)
        {
            menu.Items.Add(PeggedControl.MenuItemFor("上一页 · " + _panel.Canvas.PageLabel, () => _panel.Canvas.ChangePage(-1)));
            menu.Items.Add(PeggedControl.MenuItemFor("下一页", () => _panel.Canvas.ChangePage(1)));
        }
        menu.Items.Add(PeggedControl.MenuItemFor(Strings.NewNote, () => _line.NewNote()));
        menu.Items.Add(PeggedControl.MenuItemFor("关闭所有桌面贴图", PictureWindow.ClosePinned));
        var clear = PeggedControl.MenuItemFor("清空晾衣绳（删除图片）", ClearLine);
        clear.IsEnabled = _line.LiveCount > 0;
        clear.ToolTip = "删除所有分页中的图片副本，并关闭对应的桌面贴图";
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(PeggedControl.MenuItemFor("打开图片保存文件夹", () => Shell.OpenFolder(Inbox.Folder)));
        menu.Items.Add(PeggedControl.MenuItemFor(Strings.Settings, () => SettingsWindow.Open(ApplySettings, _line)));
        var startup = PeggedControl.MenuItemFor(Strings.StartWithWindows, () => Shell.StartsWithWindows = !Shell.StartsWithWindows);
        startup.IsChecked = Shell.StartsWithWindows; menu.Items.Add(startup);
        menu.Items.Add(PeggedControl.MenuItemFor(Strings.About, () => MessageBox.Show(
            $"Snapline 中文定制版 1.9.13\n\n{Settings.Current.CaptureHotKey} 智能吸附截图，{Settings.Current.HotKey} 显示／隐藏晾衣绳。\n框选时显示像素尺寸，按住 Shift 截正方形；按住 Alt 暂停吸附。松开后可以移动选区或用工具条标注，Enter 确认复制，Esc 取消。\n截图选区内按 {Settings.Current.ClipboardHangKey} 按选框原位钉到桌面并上杆。平时复制外部图片后按同一快捷键手动上杆。\n{Settings.Current.PinPreviewKey} 可钉住截图或晾衣绳上的悬停预览。单击桌面贴图打开编辑条。\n双击晾衣绳图片销毁并掉落，双击桌面贴图关闭。\n图片保存位置：\n{Inbox.Folder}", Strings.About)));
        menu.Items.Add(PeggedControl.MenuItemFor(Strings.Quit, () => Application.Current.Shutdown()));
        return menu;
    }

    public void Dispose()
    {
        _disposed = true;
        CaptureResult.Clear();
        _line.Save(); MarkupWindow.CloseAll(); Preview.Close(); PictureWindow.ClosePinned();
        _line.Fall -= AnimateFall;
        _line.ItemsChanged -= InvalidatePointerState;
        _line.StateChanged -= InvalidatePointerState;
        _line.ItemUpdated -= ItemChanged;
        _mouseTimer.Stop();
        _hotKey?.Dispose(); _captureHotKey?.Dispose();
        _clipboardHotKey?.Dispose(); _clipboardHotKey = null; Preview.SharePinShortcut(null);
        _tray.Dispose(); _messages.Dispose();
        Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT point);
}




