using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Snapline.Interop;

/// <summary>Present pixels, position and size together. A WPF layered HWND resized with
/// SetWindowPos can expose the previous back buffer before its next composition frame.</summary>
internal sealed class PicturePresenter : IDisposable
{
    private const string ClassName = "Snapline.AtomicPicture";
    internal const int FramePadding = 3;
    private static readonly uint[] FramePixels =
    {
        Premultiply(28, 102, 165, 234),
        Premultiply(72, 102, 165, 234),
        Premultiply(190, 105, 170, 235)
    };
    private static readonly WindowProcedure Procedure = WndProc;
    private static readonly Dictionary<IntPtr, PicturePresenter> Instances = new();
    private static bool _registered;
    private readonly IntPtr _owner;
    private readonly Dictionary<BitmapSource, Drawing.Bitmap> _sources = new();
    private readonly IntPtr _dc;
    private IntPtr _dib, _previous, _bits;
    private Drawing.Bitmap? _buffer;
    private Drawing.Graphics? _graphics;
    private int _capacityWidth, _capacityHeight, _width, _height, _padding;
    private Native.POINT _position;
    private byte _opacity = 255;
    public IntPtr Handle { get; private set; }
    public int Frames { get; private set; }
    public Action<int, Point, bool>? Wheel;
    public Action<int, int, Point>? ButtonDown;
    public Action<int, Point>? ButtonUp;
    public Action<Point>? PointerMove;
    public Func<int>? CursorId;
    public Action? CaptureLost;
    public double MaximumPaintMilliseconds { get; private set; }
    public void Reserve(int width, int height, bool framed)
    {
        int extra = framed ? 2 * FramePadding : 0;
        EnsureBuffer(width + extra, height + extra);
    }

    public PicturePresenter(IntPtr owner)
    {
        _owner = owner;
        if (!_registered)
        {
            var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 8,
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandle(null), Name = ClassName };
            if (RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        Handle = CreateWindowEx(0x00080000 | 0x08000000 | 0x80, ClassName, "Snapline 图片", 0x80000000,
            0, 0, 1, 1, owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Instances[Handle] = this;
        _dc = CreateCompatibleDC(IntPtr.Zero);
        if (_dc == IntPtr.Zero) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    public void Present(BitmapSource image, Rect bounds, double opacity, bool interactive, bool framed)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int width = Math.Max(2, (int)Math.Round(bounds.Width)), height = Math.Max(2, (int)Math.Round(bounds.Height));
        int padding = framed ? FramePadding : 0;
        int surfaceWidth = width + 2 * padding, surfaceHeight = height + 2 * padding;
        EnsureBuffer(surfaceWidth, surfaceHeight);
        if (!_sources.TryGetValue(image, out var source))
        {
            source = new Drawing.Bitmap(image.PixelWidth, image.PixelHeight, Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var locked = source.LockBits(new Drawing.Rectangle(0, 0, source.Width, source.Height), Drawing.Imaging.ImageLockMode.WriteOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
                pixels.CopyPixels(Int32Rect.Empty, locked.Scan0, locked.Stride * locked.Height, locked.Stride);
            }
            finally { source.UnlockBits(locked); }
            _sources.Add(image, source);
        }
        _graphics!.CompositingMode = Drawing.Drawing2D.CompositingMode.SourceCopy;
        _graphics.InterpolationMode = interactive ? Drawing.Drawing2D.InterpolationMode.Bilinear : Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        _graphics.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
        using var edges = new Drawing.Imaging.ImageAttributes();
        edges.SetWrapMode(Drawing.Drawing2D.WrapMode.TileFlipXY);
        _graphics.DrawImage(source, new Drawing.Rectangle(padding, padding, width, height), 0, 0, source.Width, source.Height, Drawing.GraphicsUnit.Pixel, edges);
        _graphics.Flush(Drawing.Drawing2D.FlushIntention.Sync);
        if (framed) PaintFrame(surfaceWidth, surfaceHeight);
        _width = width; _height = height; _padding = padding;
        _position = new Native.POINT((int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top));
        _opacity = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        Commit(); Frames++;
        MaximumPaintMilliseconds = Math.Max(MaximumPaintMilliseconds, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static uint Premultiply(uint a, uint r, uint g, uint b) =>
        (a << 24) | ((r * a + 127) / 255 << 16) | ((g * a + 127) / 255 << 8) | (b * a + 127) / 255;

    private unsafe void PaintFrame(int width, int height)
    {
        // Only the outline pixels are touched. Submit them with the image in the same
        // layered surface, without another rendering pass or window.
        var pixels = new Span<uint>(_bits.ToPointer(), _capacityWidth * _capacityHeight);
        for (int i = 0; i < FramePadding; i++)
        {
            uint color = FramePixels[i];
            int right = width - i - 1, bottom = height - i - 1;
            pixels.Slice(i * _capacityWidth + i, width - 2 * i).Fill(color);
            pixels.Slice(bottom * _capacityWidth + i, width - 2 * i).Fill(color);
            for (int row = i + 1; row < bottom; row++)
            {
                int offset = row * _capacityWidth;
                pixels[offset + i] = color;
                pixels[offset + right] = color;
            }
        }
    }

    public void SetOpacity(double opacity)
    {
        _opacity = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        if (_width > 0) Commit();
    }
    public void MoveTo(int x, int y) { _position = new Native.POINT(x, y); if (_width > 0) Commit(); }
    public void Show() => Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    public void Hide() => Native.ShowWindow(Handle, Native.SW_HIDE);

    private void Commit()
    {
        var size = new Native.SIZE { cx = _width + 2 * _padding, cy = _height + 2 * _padding };
        var origin = new Native.POINT();
        var position = new Native.POINT(_position.X - _padding, _position.Y - _padding);
        var blend = new Blend { Operation = 0, Alpha = _opacity, Format = 1 };
        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref position, ref size, _dc, ref origin, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void EnsureBuffer(int width, int height)
    {
        if (_buffer is not null && width <= _capacityWidth && height <= _capacityHeight) return;
        ReleaseBuffer();
        _capacityWidth = (width + 127) / 128 * 128; _capacityHeight = (height + 127) / 128 * 128;
        var info = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = _capacityWidth,
            biHeight = -_capacityHeight, biPlanes = 1, biBitCount = 32 };
        _dib = Native.CreateDIBSection(_dc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _previous = SelectObject(_dc, _dib);
        _buffer = new Drawing.Bitmap(_capacityWidth, _capacityHeight, _capacityWidth * 4, Drawing.Imaging.PixelFormat.Format32bppPArgb, _bits);
        _graphics = Drawing.Graphics.FromImage(_buffer);
    }

    public BitmapSource Snapshot()
    {
        int stride = _capacityWidth * 4;
        int offset = _padding * (stride + 4);
        var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Pbgra32, null,
            IntPtr.Add(_bits, offset), stride * _capacityHeight - offset, stride);
        image.Freeze(); return image;
    }
    public void ClearSources() { foreach (var bitmap in _sources.Values) bitmap.Dispose(); _sources.Clear(); }
    private void ReleaseBuffer()
    {
        _graphics?.Dispose(); _graphics = null; _buffer?.Dispose(); _buffer = null;
        if (_dib != IntPtr.Zero) { SelectObject(_dc, _previous); Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
    }
    public void Dispose()
    {
        if (Handle != IntPtr.Zero) { Instances.Remove(Handle); DestroyWindow(Handle); Handle = IntPtr.Zero; }
        ClearSources(); ReleaseBuffer(); if (_dc != IntPtr.Zero) DeleteDC(_dc);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        if (!Instances.TryGetValue(hwnd, out var picture)) return DefWindowProc(hwnd, message, w, l);
        if (message == Native.WM_MOUSEACTIVATE) return new IntPtr(Native.MA_NOACTIVATE);
        if (message == 0x14) return new IntPtr(1); // Never erase a displayed frame.
        if (message == 0x215) { picture.CaptureLost?.Invoke(); return IntPtr.Zero; }
        if (message == 0x20) { Native.SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(picture.CursorId?.Invoke() ?? 32646))); return new IntPtr(1); }
        if (message is >= 0x200 and <= 0x20e)
        {
            long coordinates = l.ToInt64();
            var position = new Native.POINT((short)(coordinates & 65535), (short)((coordinates >> 16) & 65535));
            if (message is not 0x20a and not 0x20e) ClientToScreen(hwnd, ref position);
            var point = new Point(position.X, position.Y);
            if (message == 0x20a) picture.Wheel?.Invoke((short)((w.ToInt64() >> 16) & 65535), point, (w.ToInt64() & 8) != 0);
            else if (message == 0x200) picture.PointerMove?.Invoke(point);
            else if (message is 0x201 or 0x203) picture.ButtonDown?.Invoke(0, message == 0x203 ? 2 : 1, point);
            else if (message is 0x207 or 0x209) picture.ButtonDown?.Invoke(1, 1, point);
            else if (message == 0x202) picture.ButtonUp?.Invoke(0, point);
            else if (message == 0x205) picture.ButtonUp?.Invoke(2, point);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, w, l);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string name, string title, uint style, int x, int y, int w, int h, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr target, ref Native.POINT point, ref Native.SIZE size, IntPtr source, ref Native.POINT origin, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Native.POINT p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Native.POINT p);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr id);
}
