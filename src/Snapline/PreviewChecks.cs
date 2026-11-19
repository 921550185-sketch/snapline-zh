using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapline.Core;
using Snapline.Interop;
using Snapline.UI;

namespace Snapline;

/// <summary>Fast isolated reproduction of first-show behavior for a tall image.</summary>
internal static class PreviewChecks
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            using var controller = new AppController(isolatedTest: true);
            var monitor = Displays.UnderPointer();
            Displays.VerificationCursor = new Native.POINT(monitor.Work.Left - 10, monitor.Work.Top - 10);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, 1600, 2400));
            var image = new RenderTargetBitmap(1600, 2400, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
            var data = new DataObject(); data.SetImage(image);
            typeof(AppController).GetMethod("HangClipboardImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, new object[] { data });
            IdleChecks.Pump(TimeSpan.FromMilliseconds(150));
            var panel = Application.Current.Windows.OfType<LineWindow>().Last();
            panel.Canvas.RelayoutNow(); panel.Canvas.Advance(2);
            var item = controller.Model.Items.Last();
            var card = panel.Canvas.Cards.Single(c => c.Item.Id == item.Id);
            var point = panel.Canvas.PointToScreen(new Point(card.CardRect.X + card.CardRect.Width / 2, card.CardRect.Y + card.CardRect.Height / 2));
            Displays.VerificationCursor = new Native.POINT((int)point.X, (int)point.Y); controller.PollPointer();
            ((System.Windows.Threading.DispatcherTimer)typeof(AppController).GetField("_mouseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!).Stop();
            IdleChecks.Pump(TimeSpan.FromMilliseconds(800));
            var preview = Preview.CurrentWindow;
            var owner = preview is null ? IntPtr.Zero : new WindowInteropHelper(preview).Handle;
            bool success = preview is { IsVisible: true } && Native.IsWindowVisible(preview.PresentationHandle);
            var result = new { success, created = preview is not null, loaded = preview?.IsLoaded, logicalVisible = preview?.IsVisible,
                owner = owner.ToInt64(), ownerVisible = Native.IsWindowVisible(owner),
                picture = preview?.PresentationHandle.ToInt64(), pictureVisible = preview is not null && Native.IsWindowVisible(preview.PresentationHandle),
                frames = preview?.PresentedFrames, width = preview?.DisplayImage.PixelWidth, height = preview?.DisplayImage.PixelHeight };
            File.WriteAllText(Path.Combine(output, "preview-check.json"), JsonSerializer.Serialize(result));
            panel.Close(); return success ? 0 : 1;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(output, "preview-check.json"), e.ToString()); return 1; }
        finally { Displays.VerificationCursor = null; }
    }
}
