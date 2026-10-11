using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Point = System.Drawing.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace screen_to_search;

public partial class StageZeroWindow : Window
{
    private readonly MonitorCaptureService _capture = new();
    private readonly ScreenOcrPipeline _ocr = new();
    private const double PreviewWidth = 850;

    public StageZeroWindow() => InitializeComponent();

    private async void CaptureClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var monitor = _capture.GetMonitors().First(m => m.PixelBounds.Contains(System.Windows.Forms.Cursor.Position));
            Hide();
            await Task.Delay(180);
            var frame = await Task.Run(() => _capture.Capture(monitor));
            Show();
            Activate();
            await ShowOcrAsync(frame.Png, monitor);
            StatusText.Text = $"Captured {monitor.DeviceName} in {frame.CaptureTime.TotalMilliseconds:F0} ms; OCR complete.";
        }
        catch (Exception ex) { Show(); StatusText.Text = $"Capture failed: {ex.Message}"; }
    }

    private async void OpenImageClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await ShowOcrAsync(bytes, null);
            StatusText.Text = "Image OCR complete. Boxes show words found in the image pixels.";
        }
        catch (Exception ex) { StatusText.Text = $"Image OCR failed: {ex.Message}"; }
    }

    private async Task ShowOcrAsync(byte[] image, MonitorInfo? monitor)
    {
        StatusText.Text = "Recognizing image pixels…";
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(image))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        var timer = Stopwatch.StartNew();
        var doc = await _ocr.RecognizeAsync(image);
        var scale = Math.Min(1.0, PreviewWidth / bitmap.PixelWidth);
        var width = bitmap.PixelWidth * scale;
        var height = bitmap.PixelHeight * scale;
        ImageGrid.Width = SnapshotImage.Width = OcrCanvas.Width = width;
        ImageGrid.Height = SnapshotImage.Height = OcrCanvas.Height = height;
        SnapshotImage.Source = bitmap;
        OcrCanvas.Children.Clear();
        foreach (var word in doc.Lines.SelectMany(line => line.Words))
        {
            var box = new Rectangle
            {
                Width = Math.Max(1, word.Bounds.Width * scale),
                Height = Math.Max(1, word.Bounds.Height * scale),
                Stroke = Brushes.DeepPink,
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(24, 255, 20, 147)),
                ToolTip = word.Text
            };
            Canvas.SetLeft(box, word.Bounds.X * scale);
            Canvas.SetTop(box, word.Bounds.Y * scale);
            OcrCanvas.Children.Add(box);
        }
        RecognizedText.Text = doc.Text;
        MonitorDetails.Text = monitor is null
            ? $"Image: {bitmap.PixelWidth} × {bitmap.PixelHeight} px\nOCR: {timer.ElapsedMilliseconds} ms\nWords: {doc.Lines.Sum(line => line.Words.Count)}"
            : $"{monitor.DeviceName}\nBounds: {monitor.PixelBounds}\nDPI: {monitor.DpiX} × {monitor.DpiY}\nRotation: {monitor.RotationDegrees}°\nOCR: {timer.ElapsedMilliseconds} ms\nWords: {doc.Lines.Sum(line => line.Words.Count)}";
    }

    private async void ProbeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Hide();
            await Task.Delay(120);
            var result = await OverlayExclusionProbe.RunAsync(_capture);
            Show();
            Activate();
            StatusText.Text = result;
        }
        catch (Exception ex) { Show(); StatusText.Text = $"Overlay probe failed: {ex.Message}"; }
    }

}
