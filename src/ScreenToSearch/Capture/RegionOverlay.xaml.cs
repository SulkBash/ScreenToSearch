using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace screen_to_search;

public partial class RegionOverlay : Window
{
    private Point? _start;
    public System.Drawing.Rectangle? SelectedPixels { get; private set; }

    public RegionOverlay()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        CaptureMouse();
        Selection.Visibility = Visibility.Visible;
        DrawSelection(_start.Value);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is not null) DrawSelection(e.GetPosition(Surface));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        var end = e.GetPosition(Surface);
        ReleaseMouseCapture();
        var a = PointToScreen(_start.Value);
        var b = PointToScreen(end);
        var x = (int)Math.Round(Math.Min(a.X, b.X));
        var y = (int)Math.Round(Math.Min(a.Y, b.Y));
        var width = (int)Math.Round(Math.Abs(a.X - b.X));
        var height = (int)Math.Round(Math.Abs(a.Y - b.Y));
        if (width >= 8 && height >= 8)
            SelectedPixels = new System.Drawing.Rectangle(x, y, width, height);
        DialogResult = SelectedPixels is not null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    private void DrawSelection(Point end)
    {
        if (_start is null) return;
        Canvas.SetLeft(Selection, Math.Min(_start.Value.X, end.X));
        Canvas.SetTop(Selection, Math.Min(_start.Value.Y, end.Y));
        Selection.Width = Math.Abs(end.X - _start.Value.X);
        Selection.Height = Math.Abs(end.Y - _start.Value.Y);
    }
}
