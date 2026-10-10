using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace screen_to_search;

internal sealed record MonitorInfo(string DeviceName, Rectangle PixelBounds, bool IsPrimary,
    uint DpiX, uint DpiY, uint RotationDegrees)
{
    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;
    public PointF PixelToLocalDip(Point physicalPixel) => new(
        (float)((physicalPixel.X - PixelBounds.X) / ScaleX),
        (float)((physicalPixel.Y - PixelBounds.Y) / ScaleY));
    public Point LocalDipToPixel(PointF localDip) => new(
        PixelBounds.X + (int)Math.Round(localDip.X * ScaleX),
        PixelBounds.Y + (int)Math.Round(localDip.Y * ScaleY));
}

internal sealed record MonitorFrame(MonitorInfo Monitor, byte[] Png, TimeSpan CaptureTime);

internal sealed class MonitorCaptureService
{
    public IReadOnlyList<MonitorInfo> GetMonitors() => Screen.AllScreens.Select(CreateInfo).ToArray();

    public MonitorFrame Capture(MonitorInfo monitor)
    {
        var timer = Stopwatch.StartNew();
        var bounds = monitor.PixelBounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new MonitorFrame(monitor, stream.ToArray(), timer.Elapsed);
    }

    private static MonitorInfo CreateInfo(Screen screen)
    {
        var bounds = screen.Bounds;
        var center = new NativePoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var handle = MonitorFromPoint(center, 2);
        var result = GetDpiForMonitor(handle, 0, out var dpiX, out var dpiY);
        if (result != 0) { dpiX = 96; dpiY = 96; }

        // DEVMODEW is 220 bytes; dmSize is at byte 68, dmDisplayOrientation at 84.
        var mode = new byte[220];
        BitConverter.GetBytes((ushort)mode.Length).CopyTo(mode, 68);
        var rotation = EnumDisplaySettings(screen.DeviceName, -1, mode)
            ? BitConverter.ToUInt32(mode, 84) * 90u : 0u;
        return new MonitorInfo(screen.DeviceName, bounds, screen.Primary, dpiX, dpiY, rotation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, [In, Out] byte[] devMode);
}
