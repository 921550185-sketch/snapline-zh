using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Snapline.Interop;

namespace Snapline.Core;

/// <summary>Clipboard image decoding and format marker; no monitoring or automatic import.</summary>
public static class ClipboardWatcher
{
    public const string OwnFormat = "Snapline.ManagedImage";
    public static BitmapSource? ReadImage(IDataObject data)
    {
        foreach (var format in new[] { "PNG", "image/png" })
        {
            try
            {
                if (!data.GetDataPresent(format, false)) continue;
                var value = data.GetData(format, false);
                using var stream = value switch
                {
                    byte[] bytes => new MemoryStream(bytes),
                    Stream incoming => CopyStream(incoming),
                    _ => null,
                };
                if (stream is null) continue;
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var png = decoder.Frames[0]; png.Freeze(); return png;
            }
            catch (Exception e) when (!IsClipboardBusy(e)) { }
        }
        foreach (var format in new[] { DataFormats.Dib, "DeviceIndependentBitmapV5", "DIBV5", "Format17" })
        {
            try
            {
                if (!data.GetDataPresent(format, false)) continue;
                var raw = data.GetData(format, false);
                using var stream = raw is Stream incoming ? CopyStream(incoming) : raw is byte[] bytes ? new MemoryStream(bytes) : null;
                if (stream is null || stream.Length < 40 || stream.Length > 256 * 1024 * 1024) continue;
                var dib = stream.ToArray();
                int header = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16);
                if (header < 40 || header > dib.Length) continue;
                int colours = BitConverter.ToInt32(dib, 32);
                if (colours == 0 && bits <= 8) colours = 1 << bits;
                int masks = header == 40 && (compression == 3 || compression == 6) ? (compression == 6 ? 16 : 12) : 0;
                using var bmp = new MemoryStream();
                using (var writer = new BinaryWriter(bmp, System.Text.Encoding.UTF8, true))
                {
                    writer.Write((ushort)0x4D42); writer.Write(dib.Length + 14); writer.Write(0);
                    writer.Write(checked(14 + header + masks + colours * 4)); writer.Write(dib);
                }
                bmp.Position = 0;
                var image = new BmpBitmapDecoder(bmp, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                return Thumbnails.NormalizeClipboard(image);
            }
            catch (Exception e) when (!IsClipboardBusy(e)) { }
        }
        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap, true))
            {
                var value = data.GetData(DataFormats.Bitmap, true);
                if (value is BitmapSource bitmap) return Thumbnails.NormalizeClipboard(bitmap);
                if (value is System.Drawing.Bitmap drawing)
                {
                    using var ms = new MemoryStream();
                    drawing.Save(ms, System.Drawing.Imaging.ImageFormat.Png); ms.Position = 0;
                    var decoded = new PngBitmapDecoder(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                    return Thumbnails.NormalizeClipboard(decoded);
                }
            }
        }
        catch (Exception e) when (!IsClipboardBusy(e)) { }
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var file in files.Where(Thumbnails.IsImage))
                    if (Thumbnails.Load(file, int.MaxValue) is { } image) return image.Image;
        }
        catch (Exception e) when (!IsClipboardBusy(e)) { }
        return null;
    }

    internal static bool IsClipboardBusy(Exception e) => e.HResult is unchecked((int)0x800401D0)
        or unchecked((int)0x80010001) or unchecked((int)0x8001010A);

    // Delayed clipboard rendering can race the key press. Retry on the UI thread
    // without sleeping it, keeping file writes outside the retry to avoid duplicates.
    internal static async Task<T> ReadWithRetry<T>(Func<T> read)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return read(); }
            catch (Exception e) when (attempt < 3 && IsClipboardBusy(e))
            { await Task.Delay(40 * (attempt + 1)); }
        }
    }

    private static MemoryStream CopyStream(Stream input)
    {
        var copy = new MemoryStream();
        long position = input.CanSeek ? input.Position : 0;
        try
        {
            if (input.CanSeek) input.Position = 0;
            input.CopyTo(copy); copy.Position = 0; return copy;
        }
        catch { copy.Dispose(); throw; }
        finally { if (input.CanSeek) input.Position = position; }
    }

}
