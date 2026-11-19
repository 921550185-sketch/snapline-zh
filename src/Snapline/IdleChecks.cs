using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snapline.Core;
using Snapline.Interop;
using Snapline.UI;

namespace Snapline;

/// <summary>Repeatable sixty-second idle workload. Only this process sees the fixed
/// pointer; the real desktop cursor, clipboard and user library are never changed.</summary>
internal static class IdleChecks
{
    internal static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var monitor = Displays.UnderPointer();
            Displays.VerificationCursor = new Native.POINT(monitor.Work.Right - 15, monitor.Work.Bottom - 15);
            var fixture = new DrawingVisual();
            using (var drawing = fixture.RenderOpen()) drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 600, 400));
            var image = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32); image.Render(fixture); image.Freeze();
            var line = new Line();
            for (int i = 0; i < 8; i++) line.Hang(Library.Store(image));
            using var controller = new AppController(isolatedTest: true);
            PictureWindow.Pin(controller.Model, controller.Model.Items[0]);
            var pin = Application.Current.Windows.OfType<PictureWindow>().Last();
            Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(pin).Handle, Native.HWND_TOPMOST,
                monitor.Work.Left + 100, monitor.Work.Top + 350, 600, 400, Native.SWP_NOACTIVATE);
            Pump(TimeSpan.FromSeconds(3));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            controller.MeasureTickAllocations = true;
            long wakeups = controller.TickInvocations, processed = controller.EffectiveTicks, allocated = controller.TickAllocatedBytes;
            int reads = Theme.RegistryReads, applications = Theme.ResourceApplications;
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
            Pump(TimeSpan.FromSeconds(60));
            double elapsed = clock.Elapsed.TotalSeconds; process.Refresh(); double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var report = new
            {
                version = typeof(App).Assembly.GetName().Version!.ToString(), seconds = Math.Round(elapsed, 3), scene = "8 visible cards, 1 pinned fixture, stationary pointer away from line",
                timerWakeups = controller.TickInvocations - wakeups, effectiveTicks = controller.EffectiveTicks - processed,
                tickAllocatedBytes = controller.TickAllocatedBytes - allocated,
                cpuMilliseconds = Math.Round(cpuMs, 2), singleCoreCpuPercent = Math.Round(cpuMs / (elapsed * 10), 4),
                machineCpuPercent = Math.Round(cpuMs / (elapsed * 10 * Environment.ProcessorCount), 4), logicalProcessors = Environment.ProcessorCount,
                registryReads = Theme.RegistryReads - reads, menuResourceApplications = Theme.ResourceApplications - applications,
                imagesPreserved = controller.Model.LiveCount == 8,
            };
            File.WriteAllText(Path.Combine(output, "idle-performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "idle-error.txt"), error.ToString()); return 1; }
        finally { Displays.VerificationCursor = null; }
    }
}
