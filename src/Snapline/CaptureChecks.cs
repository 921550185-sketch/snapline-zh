using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapline.Core;
using Snapline.Interop;
using Snapline.UI;

namespace Snapline;

public static partial class CustomChecks
{
    private static void CaptureKey(CaptureWindow overlay, Key key, bool down = true)
    {
        overlay.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay),
            Environment.TickCount, key) { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    }

    private static void VerifyCaptureTools(string output, Line line, Display monitor, BitmapSource screen,
        IReadOnlyList<SnapWindow> windows, bool desktopPixels, Action<bool, string> check)
    {
        int before = line.LiveCount;
        var overlay = CaptureWindow.ShowVerification(line, monitor, screen, windows);
        Pump(TimeSpan.FromMilliseconds(75));
        Point Local(int x, int y) => overlay.PointFromScreen(new Point(monitor.Bounds.Left + x, monitor.Bounds.Top + y));
        void Choose(string name)
        {
            var bar = (Panel)((Border)overlay.CaptureToolbar).Child;
            var button = bar.Children.OfType<Button>().Single(b => ((string)b.ToolTip).StartsWith(name, StringComparison.Ordinal));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        byte[] At(BitmapSource bitmap, int x, int y)
        {
            var result = new byte[4];
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), result, 4, 0);
            return result;
        }

        overlay.BeginSelection(Local(150, 140), manual: true);
        overlay.MoveSelection(Local(310, 230));
        check(overlay.DisplayedSize == "160 × 90", "尺寸提示跟随拖动并显示实际像素宽高");
        CaptureKey(overlay, Key.LeftShift);
        check(overlay.DisplayedSize == "160 × 160", "静止鼠标按下 Shift，选框立即锁定为正方形");
        CaptureKey(overlay, Key.LeftShift, down: false);
        check(overlay.DisplayedSize == "160 × 90", "静止鼠标松开 Shift，立即恢复自由尺寸");
        var newSelectionSamples = new double[600];
        for (int i = 0; i < newSelectionSamples.Length; i++)
        {
            var point = Local(165 + i % 615, 152 + i % 380);
            long start = Stopwatch.GetTimestamp();
            overlay.MoveSelection(point);
            newSelectionSamples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        CaptureKey(overlay, Key.RightShift);
        overlay.CompleteSelection(Local(310, 230));
        check(overlay.SelectionPixels == new Int32Rect(150, 140, 160, 160)
            && overlay.CaptureToolbar.IsVisible && !CaptureResult.HasPending, "按住 Shift 松开鼠标，保留方形选框和工具条");
        Pump(TimeSpan.FromMilliseconds(75));
        var originalToolbar = overlay.CaptureToolbar.PointToScreen(new Point());

        overlay.BeginSelection(Local(200, 195));
        overlay.MoveSelection(Local(300, 280));
        overlay.CompleteSelection(Local(300, 280));
        var movedToolbar = overlay.CaptureToolbar.PointToScreen(new Point());
        check(overlay.SelectionPixels == new Int32Rect(250, 225, 160, 160) && overlay.DisplayedSize == "160 × 160"
            && Math.Abs(movedToolbar.Y - originalToolbar.Y - 85) <= 1, "移动选框保持宽高，工具条同步移动");

        if (desktopPixels)
        {
            Pump(TimeSpan.FromMilliseconds(130));
            var rendered = CaptureWindow.ReadRegion(monitor.Bounds);
            var inside = At(rendered, 300, 270);
            var outside = At(rendered, 130, 150);
            var expected = At(screen, 130, 150);
            check(inside.Take(3).SequenceEqual(At(screen, 300, 270).Take(3))
                && outside.Take(3).Zip(expected.Take(3), (a, b) => a < b).All(darker => darker),
                "Windows 实际窗口中的选区保持原色，区域外被压暗");
            Thumbnails.SavePng(rendered, Path.Combine(output, "截图界面实测.png"));
        }

        var samples = new double[600];
        overlay.BeginSelection(Local(265, 240));
        for (int i = 0; i < samples.Length; i++)
        {
            var point = Local(230 + i % 515, 120 + i % 450);
            long start = Stopwatch.GetTimestamp();
            overlay.MoveSelection(point);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        overlay.CompleteSelection(Local(265, 240));
        Array.Sort(samples);
        Array.Sort(newSelectionSamples);
        double p95 = samples[(int)(samples.Length * 0.95) - 1];
        double newSelectionP95 = newSelectionSamples[(int)(newSelectionSamples.Length * 0.95) - 1];
        var frameTimes = new List<double>();
        if (desktopPixels)
        {
            overlay.BeginSelection(Local(265, 240));
            TimeSpan last = TimeSpan.Zero;
            EventHandler captureFrame = (_, e) =>
            {
                var current = ((RenderingEventArgs)e).RenderingTime;
                if (current == last) return;
                if (last != TimeSpan.Zero) frameTimes.Add((current - last).TotalMilliseconds);
                last = current;
                overlay.MoveSelection(Local(265 + frameTimes.Count % 60, 240 + frameTimes.Count % 45));
            };
            CompositionTarget.Rendering += captureFrame;
            try { Pump(TimeSpan.FromMilliseconds(1100)); }
            finally { CompositionTarget.Rendering -= captureFrame; }
            overlay.CompleteSelection(Local(265, 240));
            check(frameTimes.Count >= 15, "持续移动期间 Windows 截图窗口持续收到合成渲染帧");
        }
        frameTimes.Sort();
        File.WriteAllText(Path.Combine(output, "capture-drag-timing.json"), JsonSerializer.Serialize(new
        {
            moves = samples.Length, medianMs = samples[samples.Length / 2], p95Ms = p95, maxMs = samples[^1],
            newSelectionUpdates = newSelectionSamples.Length, newSelectionP95Ms = newSelectionP95,
            compositionFrames = frameTimes.Count,
            compositionP95Ms = frameTimes.Count > 0 ? (double?)frameTimes[Math.Max(0, (int)(frameTimes.Count * 0.95) - 1)] : null,
            measure = "WPF selection, mask, dimensions and toolbar update on the UI thread; excludes compositor and physical input"
        }));
        check(p95 < 16.7 && newSelectionP95 < 16.7 && overlay.SelectionPixels == new Int32Rect(250, 225, 160, 160),
            "各 600 次连续框选与移动，95% 的界面更新低于一帧，移动返回后无漂移");

        Choose("矩形");
        overlay.BeginSelection(Local(270, 245));
        overlay.CompleteSelection(Local(375, 325));
        var bar = (Panel)((Border)overlay.CaptureToolbar).Child;
        check(bar.Children.OfType<Button>().Single(b => ((string)b.ToolTip).StartsWith("撤销")).IsEnabled,
            "截图工具条的矩形操作可标注且可撤销");
        Choose("撤销");
        Choose("画笔");
        overlay.BeginSelection(Local(265, 247));
        overlay.MoveSelection(Local(280, 252));
        overlay.CompleteSelection(Local(294, 247));
        Choose("文字");
        overlay.BeginSelection(Local(281, 302));
        var text = ((Panel)overlay.Content).Children.OfType<TextBox>().Single();
        text.Text = "验收";
        CaptureKey(overlay, Key.Enter);
        check(CaptureWindow.IsCapturing && !((Panel)overlay.Content).Children.OfType<TextBox>().Any(),
            "文字输入按 Enter 先提交标注并保留截图界面");
        Choose("马赛克");
        overlay.BeginSelection(Local(396, 232));
        overlay.CompleteSelection(Local(409, 275));
        Choose("箭头");
        overlay.BeginSelection(Local(335, 338));
        overlay.CompleteSelection(Local(386, 368));
        Choose("椭圆");
        overlay.BeginSelection(Local(310, 295));
        overlay.CompleteSelection(Local(347, 326));

        int pinnedX = monitor.Bounds.Left + 250, pinnedY = monitor.Bounds.Top + 225;
        Window? frameBackdrop = null;
        if (desktopPixels)
        {
            frameBackdrop = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                ShowActivated = false, Topmost = true, Width = 208 / monitor.Scale, Height = 208 / monitor.Scale,
                Background = new SolidColorBrush(Color.FromRgb(233, 225, 214))
            };
            frameBackdrop.Show();
            Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(frameBackdrop).Handle, Native.HWND_TOPMOST,
                pinnedX - 24, pinnedY - 24, 208, 208, Native.SWP_NOACTIVATE);
        }
        CaptureKey(overlay, Key.F3);
        Pump(TimeSpan.FromMilliseconds(140));
        var item = line.Items.Last(i => !i.Falling);
        var image = line.LiveCount == before + 1 ? Thumbnails.Load(item.Path, int.MaxValue)?.Image : null;
        var pinned = Application.Current.Windows.OfType<PictureWindow>().SingleOrDefault(window => window.ItemId == item.Id && window.IsPinned && window.IsVisible);
        check(!CaptureWindow.IsCapturing && !CaptureResult.HasPending && image is { PixelWidth: 160, PixelHeight: 160 }
            && pinned is not null,
            "截图确认前按 F3 关闭选框，直接保存、上杆并显示桌面贴图");
        int pad = PicturePresenter.FramePadding;
        Native.GetWindowRect(pinned!.PresentationHandle, out var frame);
        check(pinned.PictureBounds == new Rect(pinnedX, pinnedY, 160, 160) && frame.Left == pinnedX - pad && frame.Top == pinnedY - pad
            && frame.Width == 160 + 2 * pad && frame.Height == 160 + 2 * pad, "F3 按最终选框的实际屏幕坐标和原尺寸钉图，外框向外绘制");
        var expectedCrop = new CroppedBitmap(screen, new Int32Rect(250, 225, 160, 160));
        check(!Compare(expectedCrop, image!) && At(image!, 30, 27)[2] > 180
            && At(image!, 4, 4).SequenceEqual(At(expectedCrop, 4, 4)), "钉图包含画笔等标注，普通区域保持原画面");
        Thumbnails.SavePng(image!, Path.Combine(output, "截图F3钉图结果.png"));
        if (frameBackdrop is not null)
        {
            var desktop = CaptureWindow.ReadRegion(new Native.RECT(pinnedX - 20, pinnedY - 20, pinnedX + 180, pinnedY + 180));
            var border = At(desktop, 19, 90);
            var outside = At(desktop, 13, 90);
            check(border[0] > border[2] + 35 && outside[2] > outside[0]
                && At(desktop, 35, 100).SequenceEqual(At(image!, 15, 80)), "Windows 实际桌面显示浅蓝外框，图像边缘及保存像素保留原画");
            Thumbnails.SavePng(desktop, Path.Combine(output, "原位贴图浅蓝边框.png"));
            frameBackdrop.Close();
        }
        pinned.ChangeScale(1.5);
        Native.GetWindowRect(pinned.PresentationHandle, out frame);
        check(frame.Width == 240 + 2 * pad && frame.Height == 240 + 2 * pad && pinned.Snapshot().PixelWidth == 240,
            "贴图缩放时边框跟随，边框厚度及原图导出范围各自保持");
        PictureWindow.CloseFor(item.Id);
        line.Destroy(item.Id);

        overlay = CaptureWindow.ShowVerification(line, monitor, screen, windows);
        Pump(TimeSpan.FromMilliseconds(65));
        overlay.BeginSelection(Local(880, 630), manual: true, square: true);
        overlay.CompleteSelection(Local(810, 610));
        Pump(TimeSpan.FromMilliseconds(50));
        check(overlay.SelectionPixels == new Int32Rect(810, 560, 70, 70)
            && overlay.CaptureToolbar.PointToScreen(new Point()).Y < monitor.Bounds.Top + 560,
            "反向方形框选保持 1:1，靠近屏幕底部时工具条在选框上方");
        Choose("取消截图");
        check(!CaptureWindow.IsCapturing && !CaptureResult.HasPending && line.LiveCount == before,
            "工具条取消后不会复制、保存或上杆");

        overlay = CaptureWindow.ShowVerification(line, monitor, screen, windows);
        Pump(TimeSpan.FromMilliseconds(65));
        overlay.BeginSelection(Local(220, 180), manual: true);
        overlay.CompleteSelection(Local(440, 340));
        Choose("箭头");
        overlay.BeginSelection(Local(245, 214));
        overlay.CompleteSelection(Local(410, 303));
        var saved = Path.Combine(output, "工具条保存结果.png");
        overlay.AcceptCapture(savePath: saved);
        var savedImage = Thumbnails.Load(saved, int.MaxValue)?.Image;
        check(savedImage is { PixelWidth: 220, PixelHeight: 160 } && CaptureResult.Image is { } copied
            && Compare(savedImage, copied) && !CaptureWindow.IsCapturing && line.LiveCount == before,
            "保存生成带标注的 PNG 并复制相同画面，保存本身不自动上杆");
        CaptureResult.Clear();

        overlay = CaptureWindow.ShowVerification(line, monitor, screen, windows);
        Pump(TimeSpan.FromMilliseconds(60));
        overlay.BeginSelection(Local(0, 0), manual: true);
        overlay.CompleteSelection(Local(132, 112));
        CaptureKey(overlay, Key.Enter);
        check(CaptureResult.PinCurrent(), "确认复制后仍能调用钉图");
        Pump(TimeSpan.FromMilliseconds(100));
        var confirmed = line.Items.Last(i => !i.Falling);
        var delayedPin = Application.Current.Windows.OfType<PictureWindow>().Single(picture => picture.ItemId == confirmed.Id);
        check(delayedPin.PictureBounds == new Rect(monitor.Bounds.Left, monitor.Bounds.Top, 132, 112),
            "先确认后钉仍使用原选框坐标，贴住显示器边缘时保持位置");
        PictureWindow.CloseFor(confirmed.Id);
        line.Destroy(confirmed.Id);
    }

    private static void VerifyCaptureStartup(string output, AppController controller, Action<bool, string> check)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var hotkey = (HotKey)typeof(AppController).GetField("_captureHotKey", flags)!.GetValue(controller)!;
        var messages = (MessageWindow)typeof(AppController).GetField("_messages", flags)!.GetValue(controller)!;
        int id = (int)typeof(HotKey).GetField("_id", flags)!.GetValue(hotkey)!;
        long start = Stopwatch.GetTimestamp();
        SendTestMessage(messages.Handle, Native.WM_HOTKEY, new IntPtr(id), IntPtr.Zero);
        while (!CaptureWindow.IsCapturing && Stopwatch.GetElapsedTime(start).TotalSeconds < 3)
            Pump(TimeSpan.FromMilliseconds(12));
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var active = Application.Current.Windows.OfType<CaptureWindow>().FirstOrDefault(window => window.IsVisible);
        check(hotkey.Registered && active is not null && PictureWindow.CaptureActive, "Windows F1 快捷键入口可打开真实桌面截图覆盖层");
        CaptureKey(active!, Key.Escape);
        check(!CaptureWindow.IsCapturing && !PictureWindow.CaptureActive && !CaptureResult.HasPending,
            "Esc 取消后关闭截图界面并恢复平常状态");
        File.WriteAllText(Path.Combine(output, "capture-startup-timing.json"), JsonSerializer.Serialize(new
        {
            milliseconds = elapsed, measure = "Windows WM_HOTKEY receipt to capture overlay shown; sampled on the WPF dispatcher"
        }));
    }
}
