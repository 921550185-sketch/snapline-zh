using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapline.Core;
using Snapline.Interop;
using Snapline.UI;

namespace Snapline;

/// <summary>Exercise the real menu command only on generated, isolated files.</summary>
internal static class ClearAllChecks
{
    private const string Header = "清空晾衣绳（删除图片）";
    private static ContextMenu Menu(AppController controller) => (ContextMenu)typeof(AppController)
        .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;

    internal static void Run(string output, Action<bool, string> check)
    {
        var previousCursor = Displays.VerificationCursor;
        var monitor = Displays.UnderPointer();
        Displays.VerificationCursor = new Native.POINT(monitor.Work.Right - 5, monitor.Work.Bottom - 5);
        try
        {
            using var controller = new AppController(isolatedTest: true);
            var panel = Application.Current.Windows.OfType<LineWindow>().Last();
            IdleChecks.Pump(TimeSpan.FromMilliseconds(100));
            var emptyMenu = Menu(controller);
            var emptyClear = emptyMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Header);
            check(!emptyClear.IsEnabled, "空晾衣绳时禁用一键清空菜单");
            check(emptyMenu.Items.IndexOf(emptyClear) == emptyMenu.Items.IndexOf(emptyMenu.Items.OfType<MenuItem>()
                .Single(i => (string?)i.Header == "关闭所有桌面贴图")) + 1, "一键清空位于关闭所有桌面贴图下方");

            byte[] pixels = new byte[120 * 80 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 60; pixels[i + 1] = 140; pixels[i + 2] = 230; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 480); image.Freeze();
            string external = Path.Combine(output, "清空验证-外部原件.png"); Thumbnails.SavePng(image, external);
            byte[] externalBytes = File.ReadAllBytes(external);
            int total = Math.Max(12, (int)((panel.Canvas.ActualWidth - 160) / Layout.Spacing) + 3);
            for (int i = 0; i < total - 1; i++) controller.Model.Hang(Library.Store(image), quietly: true);
            controller.Model.Hang(external, quietly: true);
            IdleChecks.Pump(TimeSpan.FromMilliseconds(300));
            var items = controller.Model.Items.Where(i => !i.Falling).ToArray();
            var paths = items.Select(i => i.Path).ToArray();
            File.WriteAllText(Path.Combine(output, "cleared-paths.json"), JsonSerializer.Serialize(paths));
            check(panel.Canvas.PageCount > 1, "清空验证覆盖多个分页");
            check(paths.All(p => p != external && Library.Owns(p)), "菜单清空对象均为软件独立副本");
            controller.Model.TogglePin(items[0].Id);
            Originals.Keep(paths[0]);
            Settings.Current.Notes[paths[0]] = new NoteData { Text = "生成的清空测试记录", Color = "yellow" };
            PictureWindow.Pin(controller.Model, items[0]); PictureWindow.Pin(controller.Model, items[1]);
            var pins = Application.Current.Windows.OfType<PictureWindow>().Where(p => items.Any(i => i.Id == p.ItemId)).ToArray();
            check(items[0].Pinned && pins.Length == 2 && pins.All(p => p.IsVisible), "清空验证包含保留标记和多张桌面贴图");
            pins[0].BeginEdit();
            var filled = Menu(controller).Items.OfType<MenuItem>().Single(i => (string?)i.Header == Header);
            check(filled.IsEnabled, "有图片时一键清空菜单可用");
            Preview.Update(items[^1], controller.Model);
            filled.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(controller.Model.LiveCount == 0, "单击菜单清空所有分页及保留标记图片");
            check(paths.All(p => !File.Exists(p)) && Settings.Current.ManagedPaths.Count == 0, "清空同时删除全部软件图片副本");
            check(!Originals.Exists(paths[0]) && !Settings.Current.Notes.ContainsKey(paths[0]), "清空同步移除编辑备份和便签记录");
            check(pins.All(p => !p.IsVisible) && !MarkupWindow.AnyEditing && FloatingToolbar.VisibleCount == 0,
                "清空同步关闭对应桌面贴图和编辑条");
            check(File.ReadAllBytes(external).SequenceEqual(externalBytes), "清空软件副本不影响外部导入原件");
            check(Settings.Current.Pegged.Count == 0 && Settings.Current.PinnedPaths.Count == 0 && Settings.Current.Spots.Count == 0,
                "清空立即保存空的图片及位置清单");
            IdleChecks.Pump(TimeSpan.FromMilliseconds(750));
            check(Preview.CurrentWindow is null, "清空取消等待中的悬停预览，不重新弹出已删除图片");
            check(panel.Canvas.PageCount == 1 && controller.Model.Items.Count == 0, "掉落完成后清理卡片并恢复空晾衣绳");
            check(controller.Model.DestroyAll() == 0 && !Menu(controller).Items.OfType<MenuItem>()
                .Single(i => (string?)i.Header == Header).IsEnabled, "重复清空为空操作且菜单恢复禁用");
            check(new Line().LiveCount == 0, "重新载入会话后已清空图片不恢复");
            panel.Close();
        }
        finally { Displays.VerificationCursor = previousCursor; }
    }
}
