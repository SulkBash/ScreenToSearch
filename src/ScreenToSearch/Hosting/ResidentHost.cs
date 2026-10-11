using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace screen_to_search;

internal sealed class ResidentHost : IDisposable
{
    public const string PipeName = "CircleToSearchForWindows.Singleton.5f238b73";

    private readonly App _app;
    private readonly AppSettings _settings;
    private readonly MainWindow _settingsWindow;
    private readonly KeyboardHook _hook;
    private readonly RawKeyboardListener? _rawKeyboard;
    private readonly DispatcherTimer _holdTimer;
    private readonly MonitorCaptureService _capture = new();
    private readonly CancellationTokenSource _pipeCancellation = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly List<CaptureSurface> _surfaces = [];
    private bool _openingSurface;
    private bool _closingSurfaces;
    private bool _restoreSettingsAfterSurface;
    private bool _disposed;

    public ResidentHost(App app, AppSettings settings)
    {
        _app = app;
        _settings = settings;
        _settingsWindow = new MainWindow(settings);
        _settingsWindow.Closing += (_, e) => { e.Cancel = true; _settingsWindow.Hide(); };
        _settingsWindow.SettingsChanged += ApplySettings;

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClampThreshold()) };
        _holdTimer.Tick += HoldTimerTick;
        _hook = new KeyboardHook(settings.Shortcut);
        _hook.Pressed += () =>
        {
            _holdTimer.Interval = TimeSpan.FromMilliseconds(ClampThreshold());
            _holdTimer.Start();
        };
        _hook.Released += () => _holdTimer.Stop();
        _settingsWindow.ShortcutRecordingChanged += recording =>
        {
            _holdTimer.Stop();
            _hook.SetShortcut(recording ? ShortcutBinding.Disabled : _settings.Shortcut);
        };
        try { _rawKeyboard = new RawKeyboardListener(_hook.OnRawKey); }
        catch (System.ComponentModel.Win32Exception) { /* The primary hook still works. */ }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Screen to Search", null, (_, _) => OpenSurface());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _app.Shutdown());
        _trayIcon = CreateTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Screen to Search",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        StartupRegistration.RefreshPathIfEnabled();

        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        try { _hook.Start(); }
        catch (System.ComponentModel.Win32Exception) when (_rawKeyboard is not null)
        {
            // Raw Input can still open the capture surface if a hook is unavailable.
        }
        _ = ListenForSecondLaunchAsync();
        _ = WarmUpOcrAsync();
    }

    private async Task WarmUpOcrAsync()
    {
        try
        {
            await NeuralOcrService.WarmUpAsync(_pipeCancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch { /* Recognition will report a model loading error when requested. */ }
    }

    private int ClampThreshold() => Math.Clamp(_settings.HoldMilliseconds, 200, 1000);

    private static Icon CreateTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/ScreenToSearch;component/Assets/ScreenToSearch.ico"))
            ?? throw new FileNotFoundException("The application icon is missing.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void HoldTimerTick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (!_hook.IsHeld) return;
        _hook.MarkLongHold();
        OpenSurface();
    }

    private void ApplySettings()
    {
        _holdTimer.Stop();
        _hook.SetShortcut(_settings.Shortcut);
    }

    public void ShowSettings()
    {
        if (_disposed) return;
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public async void OpenSurface()
    {
        if (_disposed || _openingSurface) return;
        if (_surfaces.Count > 0)
        {
            var pointer = Forms.Cursor.Position;
            (_surfaces.FirstOrDefault(surface => surface.Bounds.Contains(pointer)) ?? _surfaces[0]).Activate();
            return;
        }

        _openingSurface = true;
        _restoreSettingsAfterSurface = _settingsWindow.IsVisible;
        if (_restoreSettingsAfterSurface) _settingsWindow.Hide();
        try
        {
            if (_restoreSettingsAfterSurface) await Task.Delay(80);
            var cursor = Forms.Cursor.Position;
            var monitors = _capture.GetMonitors()
                .OrderByDescending(monitor => monitor.PixelBounds.Contains(cursor)).ToArray();
            var frames = monitors.Select(_capture.Capture).ToArray(); // Capture every monitor before overlays appear.
            foreach (var frame in frames)
            {
                var surface = new CaptureSurface(frame, _settings);
                surface.FormClosed += (_, _) => CloseSurfaces();
                _surfaces.Add(surface);
            }
            foreach (var surface in _surfaces) surface.Show();
            (_surfaces.FirstOrDefault(surface => surface.Bounds.Contains(cursor)) ?? _surfaces[0]).Activate();
        }
        catch (Exception ex)
        {
            CloseSurfaces();
            _tray.ShowBalloonTip(4000, "Capture unavailable", ex.Message, Forms.ToolTipIcon.Warning);
        }
        finally { _openingSurface = false; }
    }

    private void CloseSurfaces()
    {
        if (_closingSurfaces) return;
        _closingSurfaces = true;
        try
        {
            foreach (var surface in _surfaces.ToArray())
            {
                if (!surface.IsDisposed) surface.Close();
                surface.Dispose();
            }
            _surfaces.Clear();
            if (_restoreSettingsAfterSurface && !_disposed) ShowSettings();
            _restoreSettingsAfterSurface = false;
        }
        finally { _closingSurfaces = false; }
    }

    private async Task ListenForSecondLaunchAsync()
    {
        while (!_pipeCancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_pipeCancellation.Token);
                using var reader = new StreamReader(pipe);
                if (await reader.ReadLineAsync(_pipeCancellation.Token) == "SHOW_SETTINGS")
                    _ = _app.Dispatcher.BeginInvoke(ShowSettings);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (IOException) { }
        }
    }

    public static async Task NotifyFirstInstanceAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(1500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync("SHOW_SETTINGS");
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _app.Dispatcher.BeginInvoke(ResetAfterDesktopChange);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) =>
        _app.Dispatcher.BeginInvoke(ResetAfterDesktopChange);

    private void ResetAfterDesktopChange()
    {
        _holdTimer.Stop();
        _hook.Reset();
        CloseSurfaces();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _pipeCancellation.Cancel();
        _holdTimer.Stop();
        _hook.Dispose();
        _rawKeyboard?.Dispose();
        CloseSurfaces();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _trayIcon.Dispose();
        _settingsWindow.Close();
        _pipeCancellation.Dispose();
    }

}
