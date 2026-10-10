using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Point = System.Drawing.Point;
using Color = System.Drawing.Color;
using Form = System.Windows.Forms.Form;
using FormBorderStyle = System.Windows.Forms.FormBorderStyle;
using FormStartPosition = System.Windows.Forms.FormStartPosition;

namespace screen_to_search;

internal static class OverlayExclusionProbe
{
    private const uint WdaExcludeFromCapture = 0x11;

    public static async Task<string> RunAsync(MonitorCaptureService capture)
    {
        var probe = CreateWindow();
        try
        {
            probe.Show();
            probe.Refresh();
            var handle = probe.Handle;
            ShowWindow(handle, 5); // Ensure the probe is drawn even with a hidden process startup state.
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
            await Task.Delay(180);
            DwmFlush();
            if (!GetWindowRect(handle, out var rect))
                throw new InvalidOperationException("Could not locate overlay window.");
            var pixel = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            var monitor = capture.GetMonitors().First(m => m.PixelBounds.Contains(pixel));
            var included = await Task.Run(() => capture.Capture(monitor));
            var second = Sample(included, pixel);
            var found = FindMagenta(included);
            if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
                throw new InvalidOperationException($"WDA_EXCLUDEFROMCAPTURE failed: {Marshal.GetLastWin32Error()}");
            await Task.Delay(180);
            DwmFlush();
            var excluded = await Task.Run(() => capture.Capture(monitor));
            var first = Sample(excluded, pixel);
            var pass = !IsMagenta(first) && IsMagenta(second);
            var outcome = pass ? "PASSED" : found == "none" ? "INCONCLUSIVE" : "FAILED";
            return $"Overlay exclusion {outcome}. "
                + $"Excluded pixel: {first}; included pixel: {second}. "
                + $"Visible={IsWindowVisible(handle)}, rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}), magentaAt={found}. "
                + (pass ? "The overlay did not appear in capture."
                    : found == "none" ? "The control window was absent from capture, so this run cannot verify exclusion."
                    : "Overlay coordinate or exclusion behavior did not match the expected result.");
        }
        finally { probe.Close(); probe.Dispose(); }
    }

    private static Form CreateWindow() => new()
    {
        Width = 180,
        Height = 130,
        FormBorderStyle = FormBorderStyle.None,
        ShowInTaskbar = false,
        TopMost = true,
        StartPosition = FormStartPosition.CenterScreen,
        BackColor = Color.Magenta
    };

    private static Color Sample(MonitorFrame frame, Point screenPixel)
    {
        using var stream = new MemoryStream(frame.Png);
        using var bitmap = new Bitmap(stream);
        return bitmap.GetPixel(screenPixel.X - frame.Monitor.PixelBounds.X,
            screenPixel.Y - frame.Monitor.PixelBounds.Y);
    }

    private static bool IsMagenta(Color color) => color.R > 235 && color.G < 30 && color.B > 235;

    private static string FindMagenta(MonitorFrame frame)
    {
        using var stream = new MemoryStream(frame.Png);
        using var bitmap = new Bitmap(stream);
        for (var y = 0; y < bitmap.Height; y += 10)
            for (var x = 0; x < bitmap.Width; x += 10)
                if (IsMagenta(bitmap.GetPixel(x, y)))
                    return $"({x + frame.Monitor.PixelBounds.X},{y + frame.Monitor.PixelBounds.Y})";
        return "none";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
