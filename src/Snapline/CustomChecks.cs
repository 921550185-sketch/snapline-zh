using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapline.Core;
using Snapline.Interop;
using Snapline.UI;

namespace Snapline;

/// <summary>Regression checks run only against generated fixtures and isolated settings.</summary>
public static partial class CustomChecks
{
    internal static bool IsRunning { get; private set; }
    public static int VerifyRestored(string output)
    {
        try
        {
            var savedSpots = new Dictionary<string, double>(Settings.Current.Spots, StringComparer.OrdinalIgnoreCase);
            var line = new Line();
            var clearedPaths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(output, "cleared-paths.json")))!;
            bool clearedImagesStayGone = clearedPaths.All(p => !File.Exists(p) && line.Items.All(i => i.Path != p));
            bool positionsRestored = savedSpots.Count > 0 && savedSpots.All(pair => line.Items.Any(i =>
                string.Equals(i.Path, pair.Key, StringComparison.OrdinalIgnoreCase) && i.Spot == pair.Value))
                && line.Items.Any(i => i.Spot == 0.22);
            bool restored = line.LiveCount == 16 && line.Items.All(i => File.Exists(i.Path))
                && Settings.Current.InboxFolder == Path.Combine(output, "自定义图片保存路径")
                && Settings.Current.CaptureHotKey == "F1" && Settings.Current.HotKey == "F2" && Settings.Current.ClipboardHangKey == "F3" && Settings.Current.Language == "zh" && positionsRestored && clearedImagesStayGone;
            File.WriteAllText(Path.Combine(output, "restart-check.json"), JsonSerializer.Serialize(new { success = restored, restoredImages = line.LiveCount, settingsRestored = restored, positionsRestored, clearedImagesStayGone }));
            return restored ? 0 : 1;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(output, "restart-check.json"), e.ToString()); return 1; }
    }
    public static int Run(string output, bool desktopPixels = true)
    {
        IsRunning = true;
        Directory.CreateDirectory(output);
        var passed = new List<string>();
        try
        {
            Theme.Override(false); Strings.Refresh();
            void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed.Add(name); }
            ClearAllChecks.Run(output, Check);
            Check(Settings.Current.HotKey == "F2" && Settings.Current.CaptureHotKey == "F1" && Settings.Current.ClipboardHangKey == "F3", "默认 F1 截图、F2 显隐、F3 手动导入剪贴板图片");
            var legacyShortcuts = new Settings { HotKey = "F3", PinPreviewKey = "F3", CustomSchemaVersion = 1 };
            legacyShortcuts.UpgradeClipboardShortcut();
            Check(legacyShortcuts.HotKey == "F2" && legacyShortcuts.PinPreviewKey == "F3" && legacyShortcuts.ClipboardHangKey == "F3", "旧版 F3 显隐迁移为 F2，保留 F3 钉图共用设置");
            var legacyCaptureKey = new Settings { CaptureHotKey = "F3", CustomSchemaVersion = 1 };
            legacyCaptureKey.UpgradeClipboardShortcut();
            Check(legacyCaptureKey.CaptureHotKey == "F1" && legacyCaptureKey.ClipboardHangKey == "F3", "旧截图键与新增上杆键冲突时选择空闲键");
            Check(Strings.Language == "zh" && Strings.Copy == "复制图片", "默认简体中文");
            for (int i = 1; i <= 4; i++) Check(HotKey.Parse(i.ToString(), out _, out var vk) && vk == 0x30 + i, $"数字键 {i} 解析");
            Check(HotKey.Parse("Ctrl+Alt+F9", out var mods, out var key) && mods == (Native.MOD_CONTROL | Native.MOD_ALT) && key == 0x78, "自定义组合快捷键解析");

            var pixels = new byte[120 * 80 * 4];
            for (int n = 0; n < pixels.Length; n += 4) { pixels[n] = 40; pixels[n + 1] = 160; pixels[n + 2] = 240; }
            var dib = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 120 * 4);
            var clipboard = new DataObject(); clipboard.SetImage(dib); clipboard.SetText("第三方截图同时携带文字");
            var normalized = ClipboardWatcher.ReadImage(clipboard);
            var visible = new byte[pixels.Length]; normalized!.CopyPixels(visible, 120 * 4, 0);
            Check(visible[3] == 255 && visible[2] == 240, "第三方 DIB 零透明度修复，兼容图片与文字混合格式");
            using var rawDib = new MemoryStream();
            using (var writer = new BinaryWriter(rawDib, System.Text.Encoding.UTF8, true))
            {
                writer.Write(40); writer.Write(120); writer.Write(80); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(pixels);
            }
            var rawData = new DataObject(); rawData.SetData(DataFormats.Dib, new MemoryStream(rawDib.ToArray()));
            var rawImage = ClipboardWatcher.ReadImage(rawData);
            Check(rawImage is { PixelWidth: 120, PixelHeight: 80 }, "直接读取仅含 DIB 的第三方截图");

            var fixture = new DrawingVisual();
            using (var dc = fixture.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 400));
                dc.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 300, 200));
                dc.DrawRectangle(Brushes.OrangeRed, null, new Rect(300, 0, 300, 200));
                dc.DrawRectangle(Brushes.MediumSeaGreen, null, new Rect(0, 200, 300, 200));
                dc.DrawRectangle(Brushes.Gold, null, new Rect(300, 200, 300, 200));
                dc.DrawText(new FormattedText("Snapline 中文贴图测试\n600 × 400 原始像素", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 24, Brushes.Black, 1), new Point(45, 165));
            }
            var full = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32); full.Render(fixture); full.Freeze();
            var snapFixture = new DrawingVisual();
            using (var drawing = snapFixture.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 650));
                drawing.DrawImage(full, new Rect(120, 100, 600, 400));
            }
            var snapScreen = new RenderTargetBitmap(900, 650, 96, 96, PixelFormats.Pbgra32); snapScreen.Render(snapFixture); snapScreen.Freeze();
            var edgeRegions = SmartSnap.ImageEdges(snapScreen, new Point(0, 0));
            Check(edgeRegions.Any(r => r == new Rect(120, 100, 600, 400)), "像素边缘识别完整图片矩形");
            var snapped = SmartSnap.Best(new Point(200, 180), new Rect(0, 0, 900, 650), new SnapWindow(IntPtr.Zero, new Rect(20, 20, 850, 600)), edgeRegions, null);
            Check(snapped?.Bounds == new Rect(120, 100, 600, 400), "图片边缘优先于整个窗口");
            Check(SmartSnap.WindowAt(new[] { new SnapWindow(new IntPtr(1), new Rect(0, 0, 100, 100)), new SnapWindow(new IntPtr(2), new Rect(0, 0, 200, 200)) }, new Point(50, 50))?.Handle == new IntPtr(1), "重叠窗口吸附按前后层级选择");
            var negativeSnap = SmartSnap.Best(new Point(-300, 200), new Rect(-900, 0, 900, 650), new SnapWindow(IntPtr.Zero, new Rect(-850, 20, 800, 600)), new[] { new Rect(-780, 100, 600, 400) }, null);
            Check(negativeSnap?.Bounds == new Rect(-780, 100, 600, 400), "负坐标显示器保持物理像素坐标");
            var accessibleImage = SmartSnap.Best(new Point(200, 180), new Rect(0, 0, 900, 650), new SnapWindow(IntPtr.Zero, new Rect(20, 20, 850, 600)), edgeRegions, new SnapRegion(new Rect(130, 110, 550, 350), "图片"));
            Check(accessibleImage?.Bounds == new Rect(130, 110, 550, 350), "系统图片控件边界优先于推测边缘");
            var outside = Path.Combine(output, "外部原图.png"); Thumbnails.SavePng(full, outside);
            var pngData = new DataObject(); var pngStream = new MemoryStream(Thumbnails.PngBytes(outside)!); pngData.SetData("PNG", pngStream);
            Check(ClipboardWatcher.ReadImage(pngData)?.PixelWidth == 600, "PNG 剪贴板读取");
            var fileData = new DataObject(); fileData.SetFileDropList(new System.Collections.Specialized.StringCollection { outside });
            Check(ClipboardWatcher.ReadImage(fileData)?.PixelHeight == 400, "图片文件剪贴板读取");
            Check(Compare(full, ClipboardWatcher.ReadImage(new FaultedClipboard(pngData, "image/png"))!), "有效 PNG 图片保持原始像素");
            Check(ClipboardWatcher.ReadImage(new FaultedClipboard(clipboard, "PNG"))?.PixelWidth == 120,
                "浏览器 PNG 格式读取异常时继续读取 Bitmap");
            Check(ClipboardWatcher.ReadImage(new FaultedClipboard(fileData, "PNG", DataFormats.Bitmap))?.PixelWidth == 600,
                "图片文件的 PNG 和 Bitmap 不可用时继续读取文件路径");

            var line = new Line();
            var id = line.Hang(outside)!.Value; var item = line.Find(id)!;
            Check(item.Path != outside && Library.Owns(item.Path) && File.Exists(item.Path), "导入后生成独立持久副本");
            File.Delete(outside);
            Check(Thumbnails.Load(item.Path)?.PixelWidth == 600, "第三方临时原图消失后仍可读取");
            var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(Settings.Folder, "settings.json")));
            Check(persisted.RootElement.GetProperty("pegged").GetArrayLength() == 1, "图片清单立即保存到磁盘");
            Check(new Line().LiveCount == 1, "重新构建会话后恢复所有图片");
            var reused = Path.Combine(output, "第三方重复使用的临时路径.png");
            Thumbnails.SavePng(full, reused);
            var firstCopy = Library.Import(reused);
            Thumbnails.SavePng(normalized!, reused);
            var nextCopy = Library.Import(reused);
            Check(firstCopy != nextCopy && Thumbnails.Load(nextCopy)?.PixelWidth == 120, "第三方覆盖同一临时文件名时接收新的图片内容");
            Library.Forget(firstCopy); Library.Forget(nextCopy); File.Delete(firstCopy); File.Delete(nextCopy); Settings.Current.Save();

            using (var png = File.OpenRead(item.Path)) { Check(png.Length > 0, "保存文件完整"); }
            var picture = new PictureWindow(line, item, true);
            Check(picture.DisplayImage.PixelWidth == 600 && picture.DisplayImage.PixelHeight == 400, "悬停／贴图读取完整原始分辨率");
            picture.ApplyTransform(0);
            Check(picture.DisplayImage.PixelWidth == 400 && picture.DisplayImage.PixelHeight == 600, "顺时针旋转 90 度");
            picture.ApplyTransform(1);
            Check(Compare(full, picture.DisplayImage), "逆时针旋转恢复像素");
            picture.ApplyTransform(2); picture.ApplyTransform(2);
            Check(Compare(full, picture.DisplayImage), "左右镜像两次恢复像素");
            picture.ApplyTransform(3); picture.ApplyTransform(3);
            Check(Compare(full, picture.DisplayImage), "上下镜像两次恢复像素");
            picture.ApplyTransform(2); picture.ApplyTransform(0);
            var expected = new TransformedBitmap(new TransformedBitmap(full, new ScaleTransform(-1, 1)), new RotateTransform(90));
            Check(Compare(expected, picture.DisplayImage), "镜像与旋转依照操作顺序组合");
            picture.ChangeScale(0.5);
            Check(picture.Zoom == 0.5, "贴图等比例缩放");
            picture.ChangeOpacity(-120);
            Check(Math.Abs(picture.Opacity - 0.95) < 0.001, "滚轮向下增加透明度");
            picture.ChangeOpacity(120);
            Check(picture.Opacity == 1, "滚轮向上恢复不透明度");
            picture.Opacity = 0.25; double left = picture.Left, top = picture.Top;
            picture.ResetSizeAndOpacity();
            Check(picture.Opacity == 1 && picture.Zoom == 1 && picture.Left == left && picture.Top == top, "中键恢复原始透明度与大小且位置不变");
            picture.Close();
            var secondPicture = new PictureWindow(line, item, true);
            var thirdPicture = new PictureWindow(line, item, true);
            Check(secondPicture.IsPinned && thirdPicture.IsPinned, "支持同时钉住多张图片");
            secondPicture.Close(); thirdPicture.Close();

            var before = item.Path;
            var folder = Path.Combine(output, "自定义图片保存路径");
            Library.ChangeFolder(folder, line);
            Check(item.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(item.Path) && !File.Exists(before), "更换保存路径并迁移图片");
            Check(new Line().LiveCount == 1, "迁移路径后重建会话恢复成功");
            using (var locked = new FileStream(Path.Combine(Settings.Folder, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string safePath = item.Path;
                bool rejected = false;
                try { Library.ChangeFolder(Path.Combine(output, "模拟保存失败"), line); } catch (IOException) { rejected = true; }
                Check(rejected && item.Path == safePath && File.Exists(safePath) && Settings.Current.InboxFolder == folder, "路径迁移时清单保存失败会保留旧路径及图片");
            }
            Settings.Current.Save();

            for (int i = 0; i < 16; i++) line.Hang(Library.Store(full));
            Check(line.LiveCount == 17 && new Line().LiveCount == 17, "多张图片持久保存，不自动淘汰旧图");
            var canvas = new LineCanvas(line) { Width = 1000, Height = Layout.PanelHeight };
            var root = new Grid { Width = 1000, Height = Layout.PanelHeight + 30, Background = Brushes.LavenderBlush };
            root.Children.Add(canvas);
            root.Measure(new Size(1000, Layout.PanelHeight + 30)); root.Arrange(new Rect(0, 0, 1000, Layout.PanelHeight + 30)); root.UpdateLayout();
            canvas.Revealed = true; canvas.Advance(1.5); root.UpdateLayout();
            Check(canvas.PageCount > 1, "图片过多时可翻页");
            Render(root, Path.Combine(output, "晾衣绳预览.png"), 1000, (int)Layout.PanelHeight + 30);
            canvas.ChangePage(-1); canvas.Advance(1.5);
            Check(canvas.Cards.Any(c => c.Visibility == Visibility.Visible && c.Opacity > 0.9), "缩略图卡片正常可见");
            var freelyMoved = canvas.Cards.First(c => c.Visibility == Visibility.Visible);
            canvas.BeginReorder(freelyMoved); canvas.ReorderTo(freelyMoved, 220); canvas.EndReorder(freelyMoved); canvas.Advance(1.5);
            Check(Math.Abs(freelyMoved.CenterX - 220) < 0.1 && freelyMoved.Item.Spot == 0.22, "分页晾衣绳自由拖动松手后保持位置");
            canvas.ChangePage(1); canvas.ChangePage(-1); canvas.Advance(1.5);
            Check(Math.Abs(freelyMoved.CenterX - 220) < 0.1, "切换分页后仍保留手动位置");
            canvas.RelayoutNow(); canvas.Advance(1);
            Check(new Line().Items.First(i => i.Path == freelyMoved.Item.Path).Spot == 0.22, "自由拖动位置写入清单并能重新读取");
            Settings.Current.AutoArrange = true; canvas.RelayoutNow();
            var pageCards = canvas.Cards.Where(c => c.Visibility == Visibility.Visible).ToList();
            var reordered = pageCards[0]; var oldOrder = line.Items.Where(i => !i.Falling).Select(i => i.Id).ToArray();
            int pageStart = Array.IndexOf(oldOrder, reordered.Item.Id);
            canvas.BeginReorder(reordered); canvas.ReorderTo(reordered, Layout.X(1, pageCards.Count, 1000)); canvas.EndReorder(reordered);
            var afterOrder = line.Items.Where(i => !i.Falling).Select(i => i.Id).ToArray();
            Check(afterOrder[pageStart + 1] == reordered.Item.Id && oldOrder.Take(pageStart).SequenceEqual(afterOrder.Take(pageStart)),
                "自动排列时在当前分页内移动，不打乱其他页");
            Settings.Current.AutoArrange = false; canvas.RelayoutNow(); canvas.Advance(1.5);
            Check(Math.Abs(freelyMoved.CenterX - 220) < 0.1, "关闭自动排列后恢复手动位置");
            Render(SettingsWindow.ForDemo(() => { }), Path.Combine(output, "中文设置预览.png"), 560, 1600);

            var destroyItem = line.Items.First(i => !i.Falling); var destroyedPath = destroyItem.Path;
            var originalGuard = Path.Combine(folder, "用户自己放入的图片.png"); Thumbnails.SavePng(full, originalGuard);
            line.Destroy(destroyItem.Id);
            Check(!File.Exists(destroyedPath) && !Settings.Current.ManagedPaths.Contains(destroyedPath), "双击销毁移除软件副本与持久记录");
            Check(File.Exists(originalGuard), "销毁不删除保存目录中用户自己的文件");
            Check(new Line().LiveCount == 16, "已销毁图片重启后不恢复");
            using (var controller = new AppController(isolatedTest: true))
            {
                Check(!Settings.Current.CatchClipboard, "仅截图入库，停止自动接收剪贴板图片");
                var panel = Application.Current.Windows.OfType<LineWindow>().Last();
                Pump(TimeSpan.FromSeconds(4));
                Check(panel.IsVisible && panel.Canvas.Revealed, "晾衣绳经过四秒仍持续显示");
                controller.Toggle(); Pump(TimeSpan.FromMilliseconds(300));
                Check(!Native.IsWindowVisible(panel.Handle) && !panel.Canvas.Revealed, "显示隐藏快捷键逻辑可隐藏晾衣绳");
                controller.Toggle(); Pump(TimeSpan.FromMilliseconds(400));
                Check(Native.IsWindowVisible(panel.Handle) && panel.Canvas.Revealed, "再次切换可恢复晾衣绳");
                var testMonitor = Displays.UnderPointer();
                var awayCursor = new Native.POINT(testMonitor.Work.Right - 15, testMonitor.Work.Bottom - 15);
                Displays.VerificationCursor = awayCursor;
                controller.PollPointer();
                controller.Model.PressedId = controller.Model.Items[0].Id; controller.PollPointer();
                Check(panel.HoldMouse && !panel.ClickThrough, "鼠标不动时按住图片仍更新拖拽命中状态");
                controller.Model.PressedId = null; controller.PollPointer();
                File.WriteAllText(Path.Combine(output, "release-pointer.json"), JsonSerializer.Serialize(new
                {
                    panel.HoldMouse, panel.ClickThrough, controller.Model.PressedId, controller.Model.DraggingId,
                    controller.Model.MenuOpen, panel.Canvas.RopeDragging, panel.Canvas.AnyReordering,
                    cursorX = awayCursor.X, cursorY = awayCursor.Y, overPhoto = panel.IsOverPhoto(awayCursor),
                }));
                Check(!panel.HoldMouse && panel.ClickThrough, "鼠标不动时松开图片恢复穿透");
                controller.Model.MenuOpen = true; controller.PollPointer();
                Check(!panel.ClickThrough, "鼠标不动时打开菜单仍更新穿透状态");
                controller.Model.MenuOpen = false; controller.PollPointer();
                Check(panel.ClickThrough, "关闭菜单立即恢复穿透状态");
                var hoverCard = panel.Canvas.HitRects().First();
                var hoverPoint = panel.Canvas.PointToScreen(new Point(hoverCard.rect.X + hoverCard.rect.Width / 2, hoverCard.rect.Y + hoverCard.rect.Height / 2));
                Displays.VerificationCursor = new Native.POINT((int)hoverPoint.X, (int)hoverPoint.Y);
                controller.PollPointer(); panel.Canvas.Advance(2); controller.PollPointer();
                Pump(TimeSpan.FromMilliseconds(130));
                Check(Preview.CurrentWindow is null, "悬停预览保留原有进入等待时间");
                Pump(TimeSpan.FromMilliseconds(250));
                if (Preview.CurrentWindow is null) Pump(TimeSpan.FromMilliseconds(500));
                File.WriteAllText(Path.Combine(output, "hover-timing.json"), JsonSerializer.Serialize(new { hovered = panel.Canvas.Hovered?.Id, expected = hoverCard.card.Item.Id,
                    cursor = Displays.VerificationCursor?.ToString(), previewDue = Preview.NeedsTimedUpdate, ticks = controller.TickInvocations, effective = controller.EffectiveTicks }));
                Check(Preview.CurrentWindow is { IsVisible: true }, "鼠标静止且画面稳定时仍按时打开大图预览");
                Displays.VerificationCursor = awayCursor; controller.PollPointer();
                Check(Preview.CurrentWindow is null, "优化后鼠标移开仍立即收起预览");
                int themeReads = Theme.RegistryReads, themeApplies = Theme.ResourceApplications;
                Task.Run(() => { for (int n = 0; n < 8; n++) controller.QueueThemeRefresh(); }).GetAwaiter().GetResult();
                Pump(TimeSpan.FromMilliseconds(80));
                Check(Theme.RegistryReads - themeReads <= 1 && Theme.ResourceApplications == themeApplies, "后台主题通知合并到界面线程，同主题不重建资源");
                Displays.VerificationCursor = null;
                var remaining = line.Items.First(i => !i.Falling);
                PictureWindow.Pin(line, remaining);
                var pin = Application.Current.Windows.OfType<PictureWindow>().Last();
                Pump(TimeSpan.FromMilliseconds(150)); controller.Toggle();
                Check(pin.IsVisible && !Native.IsWindowVisible(panel.Handle), "隐藏晾衣绳不影响桌面贴图");
                Native.GetWindowRect(pin.PresentationHandle, out var shortcutBounds);
                Displays.VerificationCursor = new Native.POINT(shortcutBounds.Left + 80, shortcutBounds.Top + 70);
                controller.PollPointer(); controller.PollPointer();
                Check(PictureWindow.ShortcutTargetId == pin.ItemId, "晾衣绳隐藏后仍识别鼠标下的贴图快捷键目标");
                long effectiveBefore = controller.EffectiveTicks; controller.PollPointer(); controller.PollPointer();
                Check(controller.EffectiveTicks == effectiveBefore, "鼠标与界面不变时跳过重复托管处理");
                var cover = new Window { Width = 150, Height = 130, Left = (shortcutBounds.Left + 30) / testMonitor.Scale,
                    Top = (shortcutBounds.Top + 20) / testMonitor.Scale, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, Background = Brushes.White };
                cover.Show(); controller.PollPointer();
                Check(PictureWindow.ShortcutTargetId is null, "鼠标不动但其他窗口盖住图片时释放数字快捷键");
                cover.Close(); controller.PollPointer();
                Check(PictureWindow.ShortcutTargetId == pin.ItemId, "遮挡窗口关闭后恢复同一贴图快捷键");
                pin.Hide(); controller.PollPointer();
                Check(PictureWindow.ShortcutTargetId is null, "目标贴图隐藏后清理快捷键");
                pin.Show(); controller.PollPointer();
                Check(PictureWindow.ShortcutTargetId == pin.ItemId, "鼠标不动时重新显示贴图恢复快捷键");
                Displays.VerificationCursor = null; PictureWindow.ClearShortcutTarget();
                Thumbnails.SavePng(pin.Snapshot(), Path.Combine(output, "桌面贴图预览.png"));
                Check(pin.HasImageOnlyContent && FloatingToolbar.VisibleCount == 0, "常驻贴图无信息栏或工具条");
                // Start away from the preview's clamped position so a real cursor at the
                // right edge of the monitor cannot accidentally be inside the preview.
                Displays.VerificationCursor = new Native.POINT(testMonitor.Work.Left + 5, testMonitor.Work.Top + 5);
                Preview.Close(); Preview.Update(remaining, line); Pump(TimeSpan.FromMilliseconds(300)); Preview.Update(remaining, line);
                var hoverPreview = Preview.CurrentWindow!;
                Check(hoverPreview is { IsVisible: true, IsPinned: false } && Settings.Current.PinPreviewKey == "Q" && Preview.PinShortcutRegistered, "悬停大图预览期间注册 Q 钉图快捷键");
                Preview.Update(null, line);
                Check(Preview.CurrentWindow is null && !hoverPreview.IsVisible && !Preview.PinShortcutRegistered, "鼠标离开立即关闭临时预览，不再等待 350 毫秒");
                Preview.Update(remaining, line); Pump(TimeSpan.FromMilliseconds(300)); Preview.Update(remaining, line);
                hoverPreview = Preview.CurrentWindow!;
                var beforePinBounds = hoverPreview.PictureBounds;
                Check(Preview.PinCurrent() && hoverPreview.IsPinned && hoverPreview.IsVisible && hoverPreview.PictureBounds == beforePinBounds, "Q 将当前预览直接钉住，位置及大小不变");
                Preview.Update(null, line);
                Check(hoverPreview.IsVisible && !Preview.PinShortcutRegistered && !Preview.PinCurrent(), "钉住后移开鼠标仍显示，Q 及时释放且不能重复钉空预览");
                hoverPreview.Close();
                Displays.VerificationCursor = null;
                foreach (double scale in new[] { 0.5, 0.25, 0.05 })
                {
                    pin.ChangeScale(scale); Pump(TimeSpan.FromMilliseconds(100));
                    var scaledView = pin.Snapshot();
                    Thumbnails.SavePng(scaledView, Path.Combine(output, $"整图缩放-{scale:0.00}.png"));
                    Check(HasFixtureCorners(scaledView), $"缩小到 {scale:P0} 四个角均显示原图对应内容");
                }
                pin.ResetSizeAndOpacity(); Pump(TimeSpan.FromMilliseconds(100));
                Native.GetWindowRect(pin.PresentationHandle, out var beforeNativeDrag);
                SendTestMessage(pin.PresentationHandle, 0x201, new IntPtr(1), PackPoint(120, 90));
                SendTestMessage(pin.PresentationHandle, 0x200, new IntPtr(1), PackPoint(150, 110));
                SendTestMessage(pin.PresentationHandle, 0x202, IntPtr.Zero, PackPoint(120, 90));
                Native.GetWindowRect(pin.PresentationHandle, out var afterNativeDrag);
                Check(afterNativeDrag.Left == beforeNativeDrag.Left + 30 && afterNativeDrag.Top == beforeNativeDrag.Top + 20, "实际贴图窗口鼠标消息可直接拖动图片");
                var nativeWheelPoint = new Point(afterNativeDrag.Left + 100, afterNativeDrag.Top + 80);
                SendTestMessage(pin.PresentationHandle, 0x20a, new IntPtr(120 << 16), PackPoint((int)nativeWheelPoint.X, (int)nativeWheelPoint.Y));
                Pump(TimeSpan.FromMilliseconds(180));
                Check(Math.Abs(pin.Zoom - 1.12) < 0.002, "实际贴图窗口接收普通滚轮并缩放");
                SendTestMessage(pin.PresentationHandle, 0x20a, new IntPtr((-120 << 16) | 8), PackPoint((int)nativeWheelPoint.X, (int)nativeWheelPoint.Y));
                Check(Math.Abs(pin.Opacity - 0.95) < 0.002, "实际贴图窗口 Ctrl 加滚轮调整透明度");
                SendTestMessage(pin.PresentationHandle, 0x207, IntPtr.Zero, PackPoint(100, 80));
                Check(pin.Zoom == 1 && pin.Opacity == 1, "实际贴图窗口中键恢复原始大小和透明度");
                var dpi = VisualTreeHelper.GetDpi(pin);
                var anchor = new Point(100, 80);
                var fixedScreen = pin.PointToScreen(anchor);
                int nativeBefore = pin.NativeGeometryUpdates, framesBefore = pin.ZoomFrames;
                var zoomSamples = new List<double>();
                int lastSample = framesBefore;
                EventHandler sampleZoom = (_, _) =>
                {
                    if (pin.ZoomFrames == lastSample) return;
                    lastSample = pin.ZoomFrames;
                    zoomSamples.Add(pin.Zoom);
                };
                pin.ZoomAt(120, anchor);
                CompositionTarget.Rendering += sampleZoom;
                try { Pump(TimeSpan.FromMilliseconds(195)); }
                finally { CompositionTarget.Rendering -= sampleZoom; }
                File.WriteAllText(Path.Combine(output, "wheel-frames.json"), JsonSerializer.Serialize(zoomSamples));
                Check(zoomSamples.Any(zoom => zoom > 1 && zoom < pin.TargetZoom), "滚轮缩放实际绘制出中间帧");
                Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(pin).Handle, out var zoomedFrame);
                Check(Math.Abs(pin.Zoom - 1.12) < 0.002, "滚轮向上放大到预期比例");
                Check(Math.Abs(pin.Zoom - pin.TargetZoom) < 1e-8, "滚轮缩放在 120 毫秒内跟随完成，不拖长尾动画");
                Check(pin.ZoomFrames - framesBefore >= 2 && pin.NativeGeometryUpdates - nativeBefore <= 2, "缩放动画多帧绘制但只在开始和结束调整窗口，减少分配开销");
                Check(Math.Abs(zoomedFrame.Left + anchor.X * dpi.DpiScaleX * pin.Zoom - fixedScreen.X) < 2
                    && Math.Abs(zoomedFrame.Top + anchor.Y * dpi.DpiScaleY * pin.Zoom - fixedScreen.Y) < 2, "缩放时鼠标对应的图像位置保持稳定");
                int beforeBurst = pin.NativeGeometryUpdates;
                for (int step = 0; step < 5; step++) pin.ZoomAt(120, anchor);
                pin.StopZoom(true);
                Check(Math.Abs(pin.Zoom - Math.Pow(1.12, 6)) < 0.002, "快速连续滚轮不会丢失缩放步进");
                Check(pin.NativeGeometryUpdates - beforeBurst <= 1, "同帧连续滚轮合并窗口调整，不逐个重建窗口");
                double enlarged = pin.Zoom;
                pin.ZoomAt(-120, anchor); pin.StopZoom(true);
                Check(pin.Zoom < enlarged, "滚轮向下缩小图片");
                double beforeOpacity = pin.Zoom;
                pin.ChangeOpacity(-120);
                Check(pin.Zoom == beforeOpacity && pin.Opacity < 1, "透明度操作不会改变图片比例");
                pin.ResetSizeAndOpacity(); Pump(TimeSpan.FromMilliseconds(100));

                // Capture only an area covered by our synthetic fixture and a black test backdrop.
                var monitor = Displays.UnderPointer(); int testX = monitor.Work.Left + 100, testY = monitor.Work.Top + 80;
                if (desktopPixels)
                {
                var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Width = 640 / monitor.Scale, Height = 440 / monitor.Scale, Background = Brushes.Black };
                backdrop.Show();
                Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(backdrop).Handle, Native.HWND_TOPMOST, testX, testY, 640, 440, Native.SWP_NOACTIVATE);
                Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(pin).Handle, Native.HWND_TOPMOST, testX + 10, testY + 10, 600, 400, Native.SWP_NOACTIVATE);
                pin.Opacity = 0.5; Pump(TimeSpan.FromMilliseconds(250));
                PictureWindow.PrepareForCapture();
                Check(pin.IsVisible, "开始截图时不隐藏钉住的图片");
                var captured = CaptureWindow.ReadRegion(new Native.RECT(testX + 40, testY + 40, testX + 72, testY + 72));
                var sample = new byte[4]; new FormatConvertedBitmap(captured, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(10, 10, 1, 1), sample, 4, 0);
                Check(sample[0] > 70 && sample[1] > 40 && sample[2] > 25 && sample[2] < 140, "截图实际包含透明桌面贴图的合成画面");
                Thumbnails.SavePng(captured, Path.Combine(output, "截图包含贴图验证.png"));
                PictureWindow.FinishCapture(); pin.Opacity = 1; backdrop.Hide();

                // Verify actual window pixels, not just the scale value or source bitmap.
                backdrop.Show();
                Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(backdrop).Handle, Native.HWND_TOPMOST, testX, testY, 640, 440, Native.SWP_NOACTIVATE);
                Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(pin).Handle, Native.HWND_TOPMOST, testX + 10, testY + 10, 600, 400, Native.SWP_NOACTIVATE);
                foreach (double scale in new[] { 0.5, 0.25, 0.05 })
                {
                    pin.ChangeScale(scale); Pump(TimeSpan.FromMilliseconds(120));
                    Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(pin).Handle, out var actual);
                    var desktopBitmap = CaptureWindow.ReadRegion(actual);
                    Check(actual.Width == (int)Math.Round(600 * scale) && actual.Height == (int)Math.Round(400 * scale)
                        && HasFixtureCorners(desktopBitmap), $"桌面实际像素验证 {scale:P0} 完整显示整张图且无多余窗口");
                    Thumbnails.SavePng(desktopBitmap, Path.Combine(output, $"桌面整图-{scale:0.00}.png"));
                }
                pin.ResetSizeAndOpacity(); Pump(TimeSpan.FromMilliseconds(100));
                // Sample a fixed image pixel through the desktop compositor on every
                // animation frame, including expansion and final buffer-size changes.
                var continuityPoint = pin.PointToScreen(new Point(100 / monitor.Scale, 80 / monitor.Scale));
                var continuitySamples = new List<byte[]>();
                EventHandler checkContinuity = (_, _) =>
                {
                    var frame = CaptureWindow.ReadRegion(new Native.RECT((int)continuityPoint.X, (int)continuityPoint.Y, (int)continuityPoint.X + 2, (int)continuityPoint.Y + 2));
                    var pixel = new byte[4]; new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                    continuitySamples.Add(pixel);
                };
                for (int wheel = 0; wheel < 6; wheel++)
                {
                    pin.ZoomAt(wheel % 2 == 0 ? 120 : -120, pin.PointFromScreen(continuityPoint));
                    CompositionTarget.Rendering += checkContinuity;
                    Pump(TimeSpan.FromMilliseconds(170));
                    CompositionTarget.Rendering -= checkContinuity;
                }
                Check(continuitySamples.Count >= 18 && continuitySamples.All(p => Math.Abs(p[2] - 100) <= 8 && Math.Abs(p[1] - 149) <= 8 && Math.Abs(p[0] - 237) <= 8), "逐帧实际桌面像素验证连续放大缩小没有空白、变黑或闪白");
                File.WriteAllText(Path.Combine(output, "zoom-continuity.json"), JsonSerializer.Serialize(new { samples = continuitySamples.Count, allStable = true, continuitySamples }));
                pin.ZoomAt(-600, new Point(200, 130)); Pump(TimeSpan.FromMilliseconds(70));
                var currentImage = pin.PictureBounds;
                var livePixels = CaptureWindow.ReadRegion(new Native.RECT((int)Math.Ceiling(currentImage.Left + 2), (int)Math.Ceiling(currentImage.Top + 2), (int)Math.Floor(currentImage.Right - 2), (int)Math.Floor(currentImage.Bottom - 2)));
                Thumbnails.SavePng(livePixels, Path.Combine(output, "滚轮缩小四角.png"));
                Check(HasFixtureCorners(livePixels), "滚轮缩小画面四角完整可见，不发生临时裁切");
                pin.ZoomAt(360, pin.PointFromScreen(new Point(testX + 210, testY + 140)));
                Pump(TimeSpan.FromMilliseconds(450));
                Check(HasFixtureCorners(CaptureWindow.ReadRegion(new Native.RECT((int)pin.PictureBounds.Left, (int)pin.PictureBounds.Top, (int)pin.PictureBounds.Right, (int)pin.PictureBounds.Bottom))), "缩放中反向滚轮仍完整显示图片");
                pin.ResetSizeAndOpacity(); backdrop.Close();
                }

                byte[] originalBytes = File.ReadAllBytes(remaining.Path);
                Check(!MarkupWindow.AnyEditing, "拖动与滚轮操作后不误打开编辑条");
                int visibilityChanges = 0;
                DependencyPropertyChangedEventHandler observeVisibility = (_, _) => visibilityChanges++;
                pin.IsVisibleChanged += observeVisibility;
                var clickHandle = pin.PresentationHandle;
                int framesAtClick = pin.PresentedFrames;
                var clickClock = System.Diagnostics.Stopwatch.StartNew();
                SendTestMessage(pin.PresentationHandle, 0x201, new IntPtr(1), PackPoint(100, 80));
                SendTestMessage(pin.PresentationHandle, 0x202, IntPtr.Zero, PackPoint(100, 80));
                double firstClickMilliseconds = clickClock.Elapsed.TotalMilliseconds;
                var editor = pin.Editor ?? throw new Exception("单击松开时应已创建工具条");
                Check(!editor.IsVisible && pin.IsVisible && FloatingToolbar.VisibleCount == 1,
                    "单击松开同步打开工具条，不等待双击计时且不隐藏原贴图");
                Check(pin.PresentationHandle == clickHandle && visibilityChanges == 0, "工具条出现时复用原贴图窗口，无隐藏与显示切换");
                Check(pin.PresentedFrames == framesAtClick, "单击打开工具条不重新绘制整张图片");
                Pump(TimeSpan.FromMilliseconds(100));
                Render(editor.ToolbarSurface!, Path.Combine(output, "悬浮编辑条.png"), (int)Math.Ceiling(editor.ToolbarSurface!.ActualWidth), (int)Math.Ceiling(editor.ToolbarSurface.ActualHeight));
                var editPixels = new List<byte[]>();
                if (desktopPixels)
                {
                    var point = pin.PictureBounds.TopLeft + new Vector(20, 20);
                    EventHandler sampleEditPixel = (_, _) =>
                    {
                        var frame = CaptureWindow.ReadRegion(new Native.RECT((int)point.X, (int)point.Y, (int)point.X + 1, (int)point.Y + 1));
                        var pixel = new byte[4]; new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixel, 4, 0); editPixels.Add(pixel);
                    };
                    CompositionTarget.Rendering += sampleEditPixel;
                    Pump(TimeSpan.FromMilliseconds(180));
                    for (int cycle = 0; cycle < 4; cycle++)
                    {
                        pin.Editor!.Close(); Pump(TimeSpan.FromMilliseconds(40));
                        SendTestMessage(pin.PresentationHandle, 0x201, new IntPtr(1), PackPoint(100, 80));
                        SendTestMessage(pin.PresentationHandle, 0x202, IntPtr.Zero, PackPoint(100, 80));
                        Pump(TimeSpan.FromMilliseconds(40));
                    }
                    CompositionTarget.Rendering -= sampleEditPixel;
                    Check(editPixels.Count > 0 && editPixels.All(p => Math.Abs(p[2] - 100) <= 8 && Math.Abs(p[1] - 149) <= 8 && Math.Abs(p[0] - 237) <= 8),
                        "连续开关编辑条的桌面实际像素稳定，不露出空白或背景");
                    editor = pin.Editor!;
                }
                var shapeTool = ((Panel)((Border)editor.ToolbarSurface!).Child).Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().First();
                shapeTool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(editor.IsVisible && pin.IsVisible && visibilityChanges == 0, "选择工具后启用透明标注层，原图仍持续显示");
                Pump(TimeSpan.FromMilliseconds(100));
                var editPoint = editor.PointToScreen(new Point(65, 55));
                var editorHandle = new System.Windows.Interop.WindowInteropHelper(editor).Handle;
                var inputHandle = TestWindowFromPoint(new Native.POINT((int)editPoint.X, (int)editPoint.Y));
                File.WriteAllText(Path.Combine(output, "editor-input.json"), JsonSerializer.Serialize(new { editorHandle = editorHandle.ToInt64(), inputHandle = inputHandle.ToInt64(), pictureHandle = pin.PresentationHandle.ToInt64(), opacity = pin.Opacity }));
                Check(inputHandle == editorHandle, "编辑区域的系统鼠标命中标注层，不穿透到桌面贴图");
                Check(editor.ToolReceivesInput(new Point(65, 55)), "方框工具的实际控件可命中编辑输入");
                var toolButtons = ((Panel)((Border)editor.ToolbarSurface!).Child).Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().ToArray();
                foreach (int toolIndex in new[] { 1, 2, 3, 4, 5 })
                {
                    toolButtons[toolIndex].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Check(editor.ToolReceivesInput(new Point(65, 55)), $"编辑工具 {toolIndex} 切换后可接收画布输入");
                }
                Thumbnails.SavePng(editor.RenderComposite(), Path.Combine(output, "编辑图片本体.png"));
                editor.AddVerificationMark(); Pump(TimeSpan.FromMilliseconds(650));
                Check(File.ReadAllBytes(remaining.Path).SequenceEqual(originalBytes), "编辑期间不提前覆盖原图片");
                editor.Close(); Pump(TimeSpan.FromMilliseconds(100));
                Check(pin.IsVisible && FloatingToolbar.VisibleCount == 0 && File.ReadAllBytes(remaining.Path).SequenceEqual(originalBytes), "取消编辑丢弃本次标注并收起工具条");
                Check(visibilityChanges == 0, "进入与取消标注全程不切换贴图可见性");
                File.WriteAllText(Path.Combine(output, "editor-opening.json"), JsonSerializer.Serialize(new { firstClickMilliseconds, visibilityChanges, nativeWindowUnchanged = pin.PresentationHandle == clickHandle, desktopSamples = editPixels.Count, allStable = true }));
                pin.IsVisibleChanged -= observeVisibility;
                pin.Opacity = 0.1;
                pin.BeginEdit(); editor = pin.Editor!;
                ((Panel)((Border)editor.ToolbarSurface!).Child).Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().ElementAt(3)
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(TimeSpan.FromMilliseconds(100));
                var fadedPoint = editor.PointToScreen(new Point(65, 55));
                Check(TestWindowFromPoint(new Native.POINT((int)fadedPoint.X, (int)fadedPoint.Y)) == new System.Windows.Interop.WindowInteropHelper(editor).Handle
                    && editor.ToolReceivesInput(new Point(65, 55)), "贴图仅 10% 不透明时，画笔输入仍命中标注层");
                editor.Close(); pin.Opacity = 1;
                pin.BeginEdit(); Pump(TimeSpan.FromMilliseconds(100));
                editor = pin.Editor!;
                editor.AddVerificationMark(); editor.Confirm(false); Pump(TimeSpan.FromMilliseconds(100));
                Check(pin.IsVisible && FloatingToolbar.VisibleCount == 0 && !File.ReadAllBytes(remaining.Path).SequenceEqual(originalBytes), "完成编辑保存标注并收起工具条");
                int imagesBeforeClose = line.LiveCount;
                var savedBeforeClose = File.ReadAllBytes(remaining.Path);
                var doubleHandle = pin.PresentationHandle;
                SendTestMessage(doubleHandle, 0x201, new IntPtr(1), PackPoint(100, 80));
                SendTestMessage(doubleHandle, 0x202, IntPtr.Zero, PackPoint(100, 80));
                Pump(TimeSpan.FromMilliseconds(80));
                SendTestMessage(doubleHandle, 0x203, new IntPtr(1), PackPoint(100, 80));
                Pump(TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 100));
                Check(!pin.IsVisible && line.LiveCount == imagesBeforeClose
                    && File.ReadAllBytes(remaining.Path).SequenceEqual(savedBeforeClose) && !MarkupWindow.AnyEditing && FloatingToolbar.VisibleCount == 0, "单击工具条已打开时仍可双击关闭贴图并清理工具条");
                PictureWindow.Pin(line, remaining); Pump(TimeSpan.FromMilliseconds(60));
                pin = Application.Current.Windows.OfType<PictureWindow>().Last(w => w.IsVisible);
                Check(pin.IsVisible && pin.ItemId == remaining.Id, "双击关闭后仍可从晾衣绳重新钉图");

                var largeFixture = new TransformedBitmap(full, new ScaleTransform(6.4, 6.4)); largeFixture.Freeze();
                string largePath = Path.Combine(output, "4K缩放样例.png"); Thumbnails.SavePng(largeFixture, largePath);
                var largePin = new PictureWindow(new Line(persist: false), new Pegged(largePath, Thumbnails.Load(largePath)!.Value), true);
                largePin.ChangeScale(0.2); largePin.Show(); Pump(TimeSpan.FromMilliseconds(150));
                int initialUpdates = largePin.NativeGeometryUpdates, initialFrames = largePin.ZoomFrames, initialDrawings = largePin.DrawingPasses;
                var frameTimes = new List<double>(); var clock = System.Diagnostics.Stopwatch.StartNew();
                double lastTime = 0; int lastCount = initialFrames;
                EventHandler recordFrame = (_, _) =>
                {
                    if (largePin.ZoomFrames == lastCount) return;
                    double time = clock.Elapsed.TotalMilliseconds;
                    if (lastTime > 0) frameTimes.Add(time - lastTime);
                    lastTime = time; lastCount = largePin.ZoomFrames;
                };
                CompositionTarget.Rendering += recordFrame;
                largePin.ZoomAt(840, new Point(200, 130)); Pump(TimeSpan.FromMilliseconds(700));
                CompositionTarget.Rendering -= recordFrame;
                int drawFrames = largePin.ZoomFrames - initialFrames, windowUpdates = largePin.NativeGeometryUpdates - initialUpdates;
                Check(drawFrames >= 3 && windowUpdates <= 2 && Math.Abs(largePin.Zoom - largePin.TargetZoom) < 0.001, "3840×2560 大图缩放复用窗口缓冲区，不逐帧调整窗口");
                Check(largePin.DrawingPasses - initialDrawings <= 2, "缩放只更新保留绘图的变换，不逐帧重建绘制指令");
                largePin.ChangeScale(0.2); largePin.ZoomAt(120, new Point(100, 80));
                Check(largePin.InteractiveSourceWidth < largePin.DisplayImage.PixelWidth, "大图缩放使用预先生成的适当分辨率缓存");
                largePin.StopZoom(true);
                Check(largePin.InteractiveSourceWidth >= largePin.DisplayImage.PixelWidth * largePin.Zoom, "缩放结束使用足够分辨率的高清缓存，避免反复切换全尺寸原图");
                var largeView = largePin.Snapshot();
                Check(HasFixtureCorners(largeView), "4K 大图缩放仍完整显示四角");
                int drawingPasses = largePin.DrawingPasses - initialDrawings;
                largePin.ChangeScale(0.2); Pump(TimeSpan.FromMilliseconds(50));
                int streamUpdates = largePin.NativeGeometryUpdates;
                var streamAnchor = largePin.PointToScreen(new Point(100, 80));
                for (int wheel = 0; wheel < 6; wheel++)
                {
                    largePin.ZoomAt(120, largePin.PointFromScreen(streamAnchor)); Pump(TimeSpan.FromMilliseconds(20));
                }
                Pump(TimeSpan.FromMilliseconds(180));
                Check(Math.Abs(largePin.Zoom - 0.2 * Math.Pow(1.12, 6)) < 0.001, "连续跨帧滚轮输入正确累计并快速跟随");
                File.WriteAllText(Path.Combine(output, "zoom-stream-diagnostics.json"), JsonSerializer.Serialize(new { updates = largePin.NativeGeometryUpdates - streamUpdates, maxPaintMilliseconds = largePin.MaximumPaintMilliseconds, frames = largePin.ZoomFrames }));
                Check(HasFixtureCorners(largePin.Snapshot()), "跨帧连续滚轮结束后整张图片完整显示，无局部裁切");
                File.WriteAllText(Path.Combine(output, "zoom-performance.json"), JsonSerializer.Serialize(new
                {
                    sourceWidth = 3840, sourceHeight = 2560, animationFrames = drawFrames, nativeGeometryUpdates = windowUpdates,
                    rebuiltDrawingPasses = drawingPasses,
                    continuousWheelNativeUpdates = largePin.NativeGeometryUpdates - streamUpdates,
                    averageFrameMilliseconds = frameTimes.Count == 0 ? 0 : Math.Round(frameTimes.Average(), 2),
                    maxFrameMilliseconds = frameTimes.Count == 0 ? 0 : Math.Round(frameTimes.Max(), 2), frameTimes,
                }, new JsonSerializerOptions { WriteIndented = true }));
                // Exercise the actual HWND dimensions beyond each monitor edge. Corner colors
                // alone cannot detect a complete bitmap squeezed into the wrong aspect ratio.
                var oversizedCases = new List<object>();
                foreach (bool portrait in new[] { false, true })
                {
                    if (portrait) largePin.ApplyTransform(0);
                    double beyond = portrait ? monitor.Bounds.Height + 320 : monitor.Bounds.Width + 320;
                    double oversizedZoom = beyond / (portrait ? largePin.DisplayImage.PixelHeight : largePin.DisplayImage.PixelWidth);
                    largePin.ChangeScale(oversizedZoom); Pump(TimeSpan.FromMilliseconds(100));
                    var oversized = largePin.Snapshot();
                    Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(largePin).Handle, out var hostBounds);
                    int expectedWidth = (int)Math.Round(largePin.DisplayImage.PixelWidth * largePin.Zoom);
                    int expectedHeight = (int)Math.Round(largePin.DisplayImage.PixelHeight * largePin.Zoom);
                    oversizedCases.Add(new { portrait, expectedWidth, expectedHeight, actualWidth = oversized.PixelWidth, actualHeight = oversized.PixelHeight, hostWidth = hostBounds.Width, hostHeight = hostBounds.Height });
                    File.WriteAllText(Path.Combine(output, "oversized-geometry.json"), JsonSerializer.Serialize(oversizedCases));
                    Check(oversized.PixelWidth == expectedWidth && oversized.PixelHeight == expectedHeight
                        && hostBounds.Width == expectedWidth && hostBounds.Height == expectedHeight && HasFixtureCorners(oversized, portrait),
                        portrait ? "图片超过屏幕高度仍按原始比例完整绘制" : "图片超过屏幕宽度仍按原始比例完整绘制");
                    largePin.BeginEdit();
                    var oversizedEditor = largePin.Editor!; oversizedEditor.ActivateCanvas(); Pump(TimeSpan.FromMilliseconds(100));
                    Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(oversizedEditor).Handle, out var editorBounds);
                    File.WriteAllText(Path.Combine(output, $"oversized-editor-{portrait}.json"), JsonSerializer.Serialize(new { expectedWidth, expectedHeight, actualWidth = editorBounds.Width, actualHeight = editorBounds.Height, receivesInput = oversizedEditor.ToolReceivesInput(new Point(65, 55)) }));
                    Check(editorBounds.Width == expectedWidth && editorBounds.Height == expectedHeight
                        && oversizedEditor.ToolReceivesInput(new Point(65, 55)), "超屏图片编辑层尺寸与原图一致且接收输入");
                    oversizedEditor.Close();
                    largePin.ZoomAt(120, new Point(100, 80)); largePin.StopZoom(true);
                    var settledOversized = largePin.Snapshot();
                    Check(settledOversized.PixelWidth == (int)Math.Round(largePin.DisplayImage.PixelWidth * largePin.Zoom)
                        && settledOversized.PixelHeight == (int)Math.Round(largePin.DisplayImage.PixelHeight * largePin.Zoom), "超屏图片滚轮结束后保持等比尺寸");
                    largePin.ChangeScale(0.2); Pump(TimeSpan.FromMilliseconds(60));
                    Check(largePin.Snapshot().PixelWidth == (int)Math.Round(largePin.DisplayImage.PixelWidth * 0.2)
                        && HasFixtureCorners(largePin.Snapshot(), portrait), "超屏图片缩回后整图完整显示");
                }
                largePin.Close();

                // Exercise the native overlay and confirm the selected region through its keyboard handler.
                var snapMonitor = new Display(monitor.Handle, new Native.RECT(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Left + 900, monitor.Bounds.Top + 650), monitor.Work, monitor.Dpi, monitor.Primary);
                var snapWindows = new[] { new SnapWindow(IntPtr.Zero, new Rect(monitor.Bounds.Left + 20, monitor.Bounds.Top + 20, 850, 600)) };
                int beforeSnap = line.LiveCount; bool snapFinished = false;
                var overlay = CaptureWindow.ShowVerification(line, snapMonitor, snapScreen, snapWindows, () => snapFinished = true);
                Pump(TimeSpan.FromMilliseconds(300));
                var snapPoint = new Point(monitor.Bounds.Left + 300, monitor.Bounds.Top + 200);
                overlay.HoverAt(snapPoint);
                Check(overlay.HoverBounds == new Rect(monitor.Bounds.Left + 120, monitor.Bounds.Top + 100, 600, 400), "截图悬停自动高亮整张图片范围");
                overlay.HoverAt(snapPoint, manual: true);
                Check(overlay.HoverBounds is null, "按住 Alt 暂停智能吸附");
                overlay.HoverAt(snapPoint);
                var snapLocal = overlay.PointFromScreen(snapPoint);
                overlay.BeginSelection(snapLocal); overlay.CompleteSelection(snapLocal);
                Check(CaptureWindow.IsCapturing && overlay.CaptureToolbar.IsVisible && !CaptureResult.HasPending,
                    "单击吸附范围后保留选框并显示截图工具条，等待确认");
                CaptureKey(overlay, System.Windows.Input.Key.Enter);
                Pump(TimeSpan.FromMilliseconds(100));
                var snappedPixels = CaptureResult.Image!;
                Thumbnails.SavePng(snappedPixels, Path.Combine(output, "智能吸附裁切.png"));
                File.WriteAllText(Path.Combine(output, "snap-region.json"), JsonSerializer.Serialize(new { width = snappedPixels.PixelWidth, height = snappedPixels.PixelHeight, hover = overlay.HoverBounds?.ToString(), expected = "120,100,600,400" }));
                Check(Compare(new CroppedBitmap(snapScreen, new Int32Rect(120, 100, 600, 400)), snappedPixels), "吸附截图确认后裁切内容与截图选区像素一致");
                Check(snapFinished && line.LiveCount == beforeSnap && !CaptureWindow.IsCapturing && !MarkupWindow.AnyEditing && FloatingToolbar.VisibleCount == 0,
                    "确认后关闭截图遮罩和工具条且不上杆");
                Check(Compare(snappedPixels, ClipboardWatcher.ReadImage(CaptureResult.ClipboardData(snappedPixels))!), "默认复制的数据包含完整截图像素");
                Check(Preview.PinShortcutRegistered, "截图完成后没有编辑窗口仍注册 Q 钉图键");
                overlay = CaptureWindow.ShowVerification(line, snapMonitor, snapScreen, snapWindows);
                Pump(TimeSpan.FromMilliseconds(100));
                var dragStart = overlay.PointFromScreen(new Point(monitor.Bounds.Left + 150, monitor.Bounds.Top + 140));
                var dragEnd = overlay.PointFromScreen(new Point(monitor.Bounds.Left + 310, monitor.Bounds.Top + 230));
                overlay.HoverAt(snapPoint); overlay.BeginSelection(dragStart); overlay.MoveSelection(dragEnd); overlay.CompleteSelection(dragEnd);
                CaptureKey(overlay, System.Windows.Input.Key.Enter);
                Pump(TimeSpan.FromMilliseconds(60));
                var manualImage = CaptureResult.Image!;
                Check(manualImage.PixelWidth == 160 && manualImage.PixelHeight == 90, "拖动框选覆盖自动吸附范围，按实际拖动区域截图");
                Check(!MarkupWindow.AnyEditing && line.LiveCount == beforeSnap, "下一张截图替换待钉结果，不产生旧图或编辑窗口");
                CaptureResult.Clear();
                VerifyCaptureTools(output, line, snapMonitor, snapScreen, snapWindows, desktopPixels, Check);
                if (desktopPixels) VerifyCaptureStartup(output, controller, Check);

                var accessibleCanvas = new Canvas { Background = Brushes.White };
                var accessiblePhoto = new Image { Source = full, Width = 360 / monitor.Scale, Height = 240 / monitor.Scale, Stretch = Stretch.Fill };
                System.Windows.Automation.AutomationProperties.SetName(accessiblePhoto, "Snapline 吸附回归图片");
                Canvas.SetLeft(accessiblePhoto, 80 / monitor.Scale); Canvas.SetTop(accessiblePhoto, 70 / monitor.Scale); accessibleCanvas.Children.Add(accessiblePhoto);
                var accessibleWindow = new Window { Content = accessibleCanvas, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Topmost = true, Width = 700 / monitor.Scale, Height = 500 / monitor.Scale };
                accessibleWindow.Show();
                var accessibleHandle = new System.Windows.Interop.WindowInteropHelper(accessibleWindow).Handle;
                Native.SetWindowPos(accessibleHandle, Native.HWND_TOPMOST, testX, testY, 700, 500, Native.SWP_NOACTIVATE);
                Pump(TimeSpan.FromMilliseconds(100));
                var photoPoint = accessiblePhoto.PointToScreen(new Point(20, 20));
                var accessibleQuery = Task.Run(() => SmartSnap.AccessibleAt(new SnapWindow(accessibleHandle, new Rect(testX, testY, 700, 500)), photoPoint));
                var queryDeadline = System.Diagnostics.Stopwatch.StartNew();
                while (!accessibleQuery.IsCompleted && queryDeadline.ElapsedMilliseconds < 2500) Pump(TimeSpan.FromMilliseconds(25));
                Check(accessibleQuery.IsCompletedSuccessfully && accessibleQuery.Result is { Kind: "图片" } foundPhoto
                    && Math.Abs(foundPhoto.Bounds.Width - 360) < 2 && Math.Abs(foundPhoto.Bounds.Height - 240) < 2, "真实系统辅助功能接口识别图片控件的完整边界");
                var snapWindowsSnapshot = SmartSnap.SnapshotWindows();
                Check(SmartSnap.WindowAt(snapWindowsSnapshot, photoPoint)?.Handle == accessibleHandle, "截图前记录窗口层级，避免吸附到截图遮罩自身");
                accessibleWindow.Close();

                int beforeCapture = line.LiveCount; int managedBefore = Settings.Current.ManagedPaths.Count;
                CaptureResult.Complete(full, line); controller.PollPointer();
                Check(CaptureResult.HasPending && line.LiveCount == beforeCapture && Settings.Current.ManagedPaths.Count == managedBefore,
                    "截图默认只复制和保留待钉结果，不创建持久图片");
                Check(!MarkupWindow.AnyEditing && FloatingToolbar.VisibleCount == 0 && Preview.PinShortcutRegistered,
                    "无编辑窗口时仍可使用钉图快捷键");
                SettingsWindow.Open(controller.ApplySettings, controller.Model); Pump(TimeSpan.FromMilliseconds(100)); controller.PollPointer();
                Check(SettingsWindow.AnyOpen && !Preview.PinShortcutRegistered && !CaptureResult.PinCurrent(), "设置快捷键时暂停待钉截图的 Q，避免误钉图");
                Application.Current.Windows.OfType<SettingsWindow>().Last(w => w.IsVisible).Close();
                Pump(TimeSpan.FromMilliseconds(100)); controller.PollPointer();
                Check(!SettingsWindow.AnyOpen && Preview.PinShortcutRegistered, "设置关闭后恢复待钉截图快捷键");
                pin.BeginEdit(); Pump(TimeSpan.FromMilliseconds(100)); controller.PollPointer();
                Check(!Preview.PinShortcutRegistered && !CaptureResult.PinCurrent(), "标注输入期间暂停截图钉图键");
                pin.Editor!.Close();
                Pump(TimeSpan.FromMilliseconds(100)); controller.PollPointer();
                Check(Preview.PinShortcutRegistered, "退出编辑后恢复 Q 钉图");
                Settings.Current.PinPreviewKey = "Ctrl+Alt+F9"; controller.ApplySettings(); controller.PollPointer();
                var shortcutField = typeof(Preview).GetField("_pinKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                Check(((HotKey)shortcutField.GetValue(null)!).Text == "Ctrl+Alt+F9", "待钉截图使用用户自定义的钉图键");
                Settings.Current.PinPreviewKey = "Q"; controller.ApplySettings(); controller.PollPointer();
                var shortcut = (HotKey)shortcutField.GetValue(null)!;
                var shortcutWindow = (MessageWindow)typeof(Preview).GetField("_pinMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
                int shortcutId = (int)typeof(HotKey).GetField("_id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(shortcut)!;
                SendTestMessage(shortcutWindow.Handle, Native.WM_HOTKEY, new IntPtr(shortcutId), IntPtr.Zero);
                Pump(TimeSpan.FromMilliseconds(100));
                var newCapture = line.Items.Last(i => !i.Falling);
                var capturedPin = Application.Current.Windows.OfType<PictureWindow>().Last(w => w.ItemId == newCapture.Id && w.IsPinned && w.IsVisible);
                Check(line.LiveCount == beforeCapture + 1 && File.Exists(newCapture.Path) && capturedPin.IsVisible, "真实 Q 快捷键消息将截图钉到桌面并挂到晾衣绳");
                Check(!CaptureResult.HasPending && !CaptureResult.PinCurrent() && !Preview.PinShortcutRegistered,
                    "钉图后清理待钉结果并释放 Q，不重复添加图片");
                Check(!MarkupWindow.AnyEditing && FloatingToolbar.VisibleCount == 0, "刚钉到桌面时不自动出现编辑条");
                SendTestMessage(capturedPin.PresentationHandle, 0x201, new IntPtr(1), PackPoint(100, 80));
                SendTestMessage(capturedPin.PresentationHandle, 0x202, IntPtr.Zero, PackPoint(100, 80));
                editor = capturedPin.Editor ?? throw new Exception("新截图单击后应已创建工具条");
                Check(FloatingToolbar.VisibleCount == 1 && capturedPin.IsVisible, "新截图钉住后单击立即出现工具条且保持图片可见");
                editor.AddVerificationMark(); editor.Confirm(false); Pump(TimeSpan.FromMilliseconds(100));
                Check(capturedPin.IsVisible && line.LiveCount == beforeCapture + 1 && FloatingToolbar.VisibleCount == 0,
                    "单击打开编辑后可保存标注，原贴图恢复且不重复上杆");
                line.Destroy(newCapture.Id); PictureWindow.CloseFor(newCapture.Id);
                int clipboardBefore = controller.Model.LiveCount;
                var externalClipboard = new DataObject(); externalClipboard.SetImage(full); externalClipboard.SetText("复制的图片与文字");
                controller.VerificationClipboard = externalClipboard;
                Pump(TimeSpan.FromMilliseconds(100));
                Check(controller.Model.LiveCount == clipboardBefore && !Settings.Current.CatchClipboard, "复制外部图片不自动上杆，等待手动快捷键");
                void FireClipboardKey(bool wait = true)
                {
                    controller.PollPointer();
                    var hotkey = (HotKey?)typeof(AppController).GetField("_clipboardHotKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller);
                    Check(hotkey?.Registered == true, "剪贴板上杆快捷键已全局注册");
                    var window = (MessageWindow)typeof(HotKey).GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hotkey)!;
                    int keyId = (int)typeof(HotKey).GetField("_id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hotkey)!;
                    SendTestMessage(window.Handle, Native.WM_HOTKEY, new IntPtr(keyId), IntPtr.Zero);
                    if (wait) Pump(TimeSpan.FromMilliseconds(100));
                }
                void RemoveImported(Guid imported)
                {
                    if (Native.IsWindowVisible(panel.Handle)) controller.Toggle();
                    controller.Model.Destroy(imported); PictureWindow.CloseFor(imported);
                }
                FireClipboardKey();
                var clipboardItem = controller.Model.Items.Last(i => !i.Falling);
                Check(controller.Model.LiveCount == clipboardBefore + 1 && Compare(Thumbnails.Load(clipboardItem.Path, int.MaxValue)!.Value.Image, full)
                    && Native.IsWindowVisible(panel.Handle), "真实 F3 消息将剪贴板图片保存上杆并显示晾衣绳");
                Check(!Application.Current.Windows.OfType<PictureWindow>().Any(w => w.ItemId == clipboardItem.Id && w.IsPinned), "复制图片上杆不会自动钉到桌面");
                void VerifyNewImportHover(Pegged imported, BitmapSource expectedImage)
                {
                    bool previousArrange = Settings.Current.AutoArrange;
                    var previousCursor = Displays.VerificationCursor;
                    var pointerTimer = (System.Windows.Threading.DispatcherTimer)typeof(AppController).GetField("_mouseTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller)!;
                    try
                    {
                        Settings.Current.AutoArrange = true; panel.Canvas.RelayoutNow(); panel.Canvas.Advance(2);
                        var importedCard = panel.Canvas.Cards.Single(c => c.Item.Id == imported.Id);
                        var importHover = panel.Canvas.PointToScreen(new Point(importedCard.CardRect.X + importedCard.CardRect.Width / 2,
                            importedCard.CardRect.Y + importedCard.CardRect.Height / 2));
                        Displays.VerificationCursor = new Native.POINT((int)importHover.X, (int)importHover.Y);
                        controller.PollPointer(); pointerTimer.Stop();
                        Pump(TimeSpan.FromMilliseconds(450));
                        var observed = Preview.CurrentWindow;
                        File.WriteAllText(Path.Combine(output, $"import-hover-{expectedImage.PixelWidth}x{expectedImage.PixelHeight}.json"), JsonSerializer.Serialize(new
                        {
                            imported = imported.Id, hovered = panel.Canvas.Hovered?.Id, candidate = typeof(Preview).GetField("_candidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null),
                            preview = observed?.ItemId, visible = observed?.IsVisible, nativeVisible = observed is not null && Native.IsWindowVisible(observed.PresentationHandle),
                            loaded = observed?.IsLoaded, frames = observed?.PresentedFrames,
                            ownerVisible = observed is not null && Native.IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(observed).Handle),
                            zoom = observed?.Zoom, width = observed?.DisplayImage.PixelWidth, height = observed?.DisplayImage.PixelHeight,
                            samePixels = observed is not null && Compare(observed.DisplayImage, expectedImage),
                            pressed = controller.Model.PressedId, menu = controller.Model.MenuOpen, cursorX = importHover.X, cursorY = importHover.Y,
                        }));
                        Check(Preview.CurrentWindow is { IsVisible: true } importedHoverPreview && importedHoverPreview.ItemId == imported.Id
                            && importedHoverPreview.Zoom == 1 && Native.IsWindowVisible(importedHoverPreview.PresentationHandle)
                            && Compare(importedHoverPreview.DisplayImage, expectedImage), "F3 新导入图片首次悬停显示原尺寸大图，不依赖后续鼠标轮询");
                        Displays.VerificationCursor = new Native.POINT(testMonitor.Work.Left - 10, testMonitor.Work.Top - 10); controller.PollPointer();
                        Check(Preview.CurrentWindow is null, "新导入图片移开鼠标立即关闭大图");
                        Pump(TimeSpan.FromMilliseconds(320));
                        Check(Preview.CurrentWindow is null, "移开后悬停计时不会重新弹出旧图片");
                    }
                    finally
                    {
                        Displays.VerificationCursor = previousCursor; pointerTimer.Start();
                        Settings.Current.AutoArrange = previousArrange; panel.Canvas.RelayoutNow();
                    }
                }
                VerifyNewImportHover(clipboardItem, full);
                RemoveImported(clipboardItem.Id);
                var browserPng = new DataObject(); browserPng.SetData("PNG", new MemoryStream(Thumbnails.PngBytes(remaining.Path)!));
                controller.VerificationClipboard = browserPng; FireClipboardKey();
                var pngImport = controller.Model.Items.Last(i => !i.Falling);
                Check(controller.Model.LiveCount == clipboardBefore + 1 && Compare(Thumbnails.Load(pngImport.Path, int.MaxValue)!.Value.Image,
                    Thumbnails.Load(remaining.Path, int.MaxValue)!.Value.Image), "浏览器复制的 PNG 经 F3 保存上杆");
                VerifyNewImportHover(pngImport, Thumbnails.Load(remaining.Path, int.MaxValue)!.Value.Image);
                RemoveImported(pngImport.Id);
                var explorerFiles = new DataObject(); explorerFiles.SetFileDropList(new System.Collections.Specialized.StringCollection { remaining.Path });
                controller.VerificationClipboard = new FaultedClipboard(explorerFiles, "PNG", DataFormats.Bitmap); FireClipboardKey();
                var fileImport = controller.Model.Items.Last(i => !i.Falling);
                Check(controller.Model.LiveCount == clipboardBefore + 1 && File.Exists(remaining.Path) && fileImport.Path != remaining.Path,
                    "文件夹 Ctrl+C 后 F3 上杆独立副本，其他格式异常不影响文件导入");
                RemoveImported(fileImport.Id);
                var tallImage = new TransformedBitmap(full, new ScaleTransform(1600.0 / full.PixelWidth, 2400.0 / full.PixelHeight)); tallImage.Freeze();
                string tallPath = Path.Combine(output, "1600x2400-复制悬停样例.png"); Thumbnails.SavePng(tallImage, tallPath);
                var tallFiles = new DataObject(); tallFiles.SetFileDropList(new System.Collections.Specialized.StringCollection { tallPath });
                controller.VerificationClipboard = tallFiles; FireClipboardKey();
                var tallImported = controller.Model.Items.Last(i => !i.Falling);
                VerifyNewImportHover(tallImported, tallImage); RemoveImported(tallImported.Id);
                int readAttempts = 0;
                controller.VerificationClipboardReader = () => ++readAttempts < 3
                    ? throw new System.Runtime.InteropServices.COMException("Clipboard busy", unchecked((int)0x800401D0)) : browserPng;
                // Deliver the duplicate before pumping the dispatcher: a late second
                // press after a slow frame could legitimately start a new import.
                FireClipboardKey(wait: false); FireClipboardKey(wait: false); Pump(TimeSpan.FromMilliseconds(400));
                var retriedImport = controller.Model.Items.Last(i => !i.Falling);
                Check(readAttempts == 3 && controller.Model.LiveCount == clipboardBefore + 1,
                    "剪贴板短暂占用自动重试成功，等待期间重复 F3 不重复入库");
                RemoveImported(retriedImport.Id); controller.VerificationClipboardReader = null;
                controller.VerificationClipboard = new DataObject(DataFormats.Text, "只有文字"); FireClipboardKey();
                Check(controller.Model.LiveCount == clipboardBefore, "剪贴板只有文字时不生成空白图片");

                Settings.Current.PinPreviewKey = "F3"; controller.ApplySettings();
                CaptureResult.Complete(full, controller.Model);
                controller.VerificationClipboard = CaptureResult.ClipboardData(full);
                FireClipboardKey();
                var sharedPin = controller.Model.Items.Last(i => !i.Falling);
                Check(controller.Model.LiveCount == clipboardBefore + 1 && !CaptureResult.HasPending
                    && Application.Current.Windows.OfType<PictureWindow>().Any(w => w.ItemId == sharedPin.Id && w.IsPinned && w.IsVisible), "F3 共用时仍将本软件框选结果直接钉住并上杆");
                RemoveImported(sharedPin.Id);
                CaptureResult.Complete(full, controller.Model);
                var newerClipboard = new DataObject(); newerClipboard.SetImage(normalized!);
                controller.VerificationClipboard = newerClipboard; FireClipboardKey();
                var newerItem = controller.Model.Items.Last(i => !i.Falling);
                Check(!CaptureResult.HasPending && Thumbnails.Load(newerItem.Path, int.MaxValue) is { PixelWidth: 120, PixelHeight: 80 }
                    && !Application.Current.Windows.OfType<PictureWindow>().Any(w => w.ItemId == newerItem.Id && w.IsPinned), "共用 F3 时优先上杆新复制的外部图片，不误钉旧截图");
                RemoveImported(newerItem.Id);
                pin.BeginEdit();
                controller.VerificationClipboard = externalClipboard; FireClipboardKey();
                var toolbarImport = controller.Model.Items.Last(i => !i.Falling);
                Check(controller.Model.LiveCount == clipboardBefore + 1 && pin.Editor is not null,
                    "已有贴图工具条打开时，F3 仍可导入外部图片且不关闭原编辑会话");
                pin.Editor!.Close(); RemoveImported(toolbarImport.Id);
                Settings.Current.PinPreviewKey = "Q";
                Settings.Current.ClipboardHangKey = "Ctrl+Alt+F8"; controller.ApplySettings();
                controller.VerificationClipboard = externalClipboard; FireClipboardKey();
                var customClipboardItem = controller.Model.Items.Last(i => !i.Falling);
                Check(controller.Model.LiveCount == clipboardBefore + 1, "自定义剪贴板上杆快捷键可生效");
                RemoveImported(customClipboardItem.Id);
                Settings.Current.ClipboardHangKey = "Ctrl+Alt+F7";
                using (var blockerMessages = new MessageWindow())
                {
                    var blocker = new HotKey(blockerMessages, Settings.Current.ClipboardHangKey, () => { });
                    Check(blocker.Registered, "测试占用快捷键成功");
                    controller.ApplySettings();
                    var keyField = typeof(AppController).GetField("_clipboardHotKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    Check(((HotKey)keyField.GetValue(controller)!).Registered == false, "上杆键被占用时可检测注册失败");
                    blocker.Dispose(); Pump(TimeSpan.FromMilliseconds(2200)); controller.PollPointer();
                    Check(((HotKey)keyField.GetValue(controller)!).Registered, "其他程序释放按键后上杆键自动恢复，无需重启");
                    controller.VerificationClipboard = externalClipboard; FireClipboardKey();
                    var recoveredImport = controller.Model.Items.Last(i => !i.Falling);
                    Check(controller.Model.LiveCount == clipboardBefore + 1, "恢复的快捷键可正常导入图片");
                    RemoveImported(recoveredImport.Id);
                }
                Settings.Current.ClipboardHangKey = "F3"; controller.VerificationClipboard = null;
                controller.ApplySettings(); Settings.Current.Save();
                controller.Toggle(); Pump(TimeSpan.FromMilliseconds(400));
                var fallingId = controller.Model.Hang(Library.Store(full))!.Value;
                Pump(TimeSpan.FromMilliseconds(450));
                controller.Model.Destroy(fallingId);
                var fallingWindow = Application.Current.Windows.OfType<FlightWindow>().LastOrDefault();
                Check(fallingWindow is { IsVisible: true }, "双击销毁流程重新播放独立掉落动画窗口");
                var fallingVisual = ((Panel)fallingWindow!.Content).Children.OfType<Grid>().First();
                double fallTop = Canvas.GetTop(fallingVisual);
                Pump(TimeSpan.FromMilliseconds(250));
                Check(Canvas.GetTop(fallingVisual) > fallTop + 10 && fallingVisual.Opacity < 1, "销毁图片向下掉落并逐渐淡出");
                Pump(TimeSpan.FromMilliseconds(450));
                Check(!fallingWindow.IsVisible && controller.Model.Find(fallingId) is null && new Line().LiveCount == 16, "掉落结束清理动画，销毁图片重启后不恢复");
                pin.Close(); panel.Close();
            }
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { success = true, desktopPixelChecks = desktopPixels, count = passed.Count, passed }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { success = false, passed, error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { Displays.VerificationCursor = null; IsRunning = false; }
    }

    private sealed class FaultedClipboard : IDataObject
    {
        private readonly IDataObject _inner;
        private readonly HashSet<string> _faults;
        public FaultedClipboard(IDataObject inner, params string[] faults) { _inner = inner; _faults = new(faults); }
        public object GetData(string format, bool autoConvert) => _faults.Contains(format)
            ? throw new System.Runtime.InteropServices.COMException("Unavailable clipboard format", unchecked((int)0x80040064)) : _inner.GetData(format, autoConvert);
        public object GetData(string format) => GetData(format, true);
        public object GetData(Type format) => _inner.GetData(format);
        public bool GetDataPresent(string format, bool autoConvert) => _faults.Contains(format) || _inner.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => _inner.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert) => _inner.GetFormats(autoConvert);
        public string[] GetFormats() => _inner.GetFormats();
        public void SetData(string format, object data, bool autoConvert) => _inner.SetData(format, data, autoConvert);
        public void SetData(string format, object data) => _inner.SetData(format, data);
        public void SetData(Type format, object data) => _inner.SetData(format, data);
        public void SetData(object data) => _inner.SetData(data);
    }

    private static bool HasFixtureCorners(BitmapSource image, bool clockwise = false)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        Color[] expected = clockwise
            ? new[] { Colors.MediumSeaGreen, Colors.CornflowerBlue, Colors.Gold, Colors.OrangeRed }
            : new[] { Colors.CornflowerBlue, Colors.OrangeRed, Colors.MediumSeaGreen, Colors.Gold };
        for (int corner = 0; corner < 4; corner++)
        {
            int x = (int)(bitmap.PixelWidth * ((corner & 1) == 0 ? 0.1 : 0.9));
            int y = (int)(bitmap.PixelHeight * (corner < 2 ? 0.1 : 0.9));
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            var color = expected[corner];
            if (pixel[3] < 250 || Math.Abs(pixel[2] - color.R) > 8 || Math.Abs(pixel[1] - color.G) > 8 || Math.Abs(pixel[0] - color.B) > 8) return false;
        }
        return true;
    }
    private static IntPtr PackPoint(int x, int y) => new((x & 65535) | (y << 16));
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr TestWindowFromPoint(Native.POINT point);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendTestMessage(IntPtr window, int message, IntPtr w, IntPtr l);
    private static bool Compare(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        var aa = new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0); var bb = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var x = new byte[a.PixelWidth * a.PixelHeight * 4]; var y = new byte[x.Length]; aa.CopyPixels(x, a.PixelWidth * 4, 0); bb.CopyPixels(y, b.PixelWidth * 4, 0);
        return x.SequenceEqual(y);
    }
    private static void Pump(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(element); Thumbnails.SavePng(image, path);
    }
}
