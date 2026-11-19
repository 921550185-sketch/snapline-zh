using System.Windows;
using System.Windows.Media.Imaging;
using Snapline.Core;

namespace Snapline.UI;

/// <summary>The latest completed capture stays in memory until pinned or replaced.
/// It has no window and creates no library item before the user pins it.</summary>
internal static class CaptureResult
{
    private static BitmapSource? _image;
    private static Line? _line;
    private static Guid? _storedId;
    private static Point? _screenPosition;
    internal static BitmapSource? Image => _image;
    internal static bool HasPending => _image is not null;
    internal static long Revision { get; private set; }

    internal static DataObject ClipboardData(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image); data.SetData(ClipboardWatcher.OwnFormat, true);
        return data;
    }

    internal static void Complete(BitmapSource image, Line line, Point? screenPosition = null)
    {
        Clear();
        _image = image; _line = line; _screenPosition = screenPosition; Revision++;
        try { if (!CustomChecks.IsRunning) Clipboard.SetDataObject(ClipboardData(image), true); }
        finally { Preview.RefreshPinShortcut(); }
    }

    internal static bool PinCurrent()
    {
        if (_image is null || _line is null || PictureWindow.CaptureActive || CaptureWindow.IsCapturing
            || MarkupWindow.AnyEditing || SettingsWindow.AnyOpen) return false;
        try
        {
            _storedId ??= _line.Hang(Library.Store(_image));
            if (_storedId is not { } id || _line.Find(id) is not { } item)
                throw new System.IO.IOException("无法保存截图，请检查保存路径及磁盘空间。");
            PictureWindow.Pin(_line, item, _screenPosition);
            Clear();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show($"钉图失败：{e.Message}", "Snapline", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    internal static void Clear()
    {
        _image = null; _line = null; _storedId = null; _screenPosition = null; Revision++;
        Preview.RefreshPinShortcut();
    }
}
