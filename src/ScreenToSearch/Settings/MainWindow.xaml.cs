using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace screen_to_search;

public partial class MainWindow : Window
{
    private const int GwlStyle = -16;
    private const int WsMaximizeBox = 0x00010000;
    private const int WmSysCommand = 0x0112;
    private const int ScMaximize = 0xF030;
    private const int WmNcLButtonDoubleClick = 0x00A3;
    private const int HtCaption = 2;
    private const uint SwpRefreshFrame = 0x0027;

    private readonly AppSettings _settings;
    private bool _initializing = true;
    private bool _recordingShortcut;
    private ShortcutBinding? _pendingShortcut;

    public event Action? SettingsChanged;
    public event Action<bool>? ShortcutRecordingChanged;

    private static readonly (string Name, string Code)[] Languages =
    [
        ("English", "en"), ("Spanish", "es"), ("French", "fr"),
        ("German", "de"), ("Portuguese", "pt"), ("Italian", "it"),
        ("Japanese", "ja"), ("Korean", "ko"), ("Chinese (Simplified)", "zh-CN"),
        ("Arabic", "ar"), ("Hindi", "hi"), ("Russian", "ru")
    ];

    internal MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var (name, code) in Languages)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        LanguageBox.SelectedItem = FindItem(LanguageBox, settings.Language) ?? LanguageBox.Items[0];
        SearchBox.SelectedItem = FindItem(SearchBox, settings.SearchEngine) ?? SearchBox.Items[0];
        UpdateShortcutDisplay();
        HoldThresholdBox.Value = Math.Clamp(settings.HoldMilliseconds, 200, 1000);
        ThresholdText.Text = $"{(int)HoldThresholdBox.Value} ms";
        StartupCheck.IsChecked = StartupRegistration.IsEnabled();
        _initializing = false;
    }

    private static ComboBoxItem? FindItem(ComboBox box, string tag) =>
        box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = 1;
        var handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        var roundedCorners = 2;
        DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
        var style = GetWindowLong(handle, GwlStyle);
        SetWindowLong(handle, GwlStyle, style & ~WsMaximizeBox);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpRefreshFrame);
        HwndSource.FromHwnd(handle)?.AddHook(BlockMaximize);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int valueSize);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    private IntPtr BlockMaximize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if (message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMaximize ||
            message == WmNcLButtonDoubleClick && wParam.ToInt64() == HtCaption)
        {
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void MinimizeSettings(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseSettings(object sender, RoutedEventArgs e) => Close();

    private void SettingsStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            Dispatcher.BeginInvoke(new Action(() => WindowState = WindowState.Normal));
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        _settings.Language = (string)item.Tag;
        _settings.Save();
    }

    private void SearchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SearchBox.SelectedItem is not ComboBoxItem item) return;
        _settings.SearchEngine = (string)item.Tag;
        _settings.Save();
    }

    private void StartShortcutRecording(object sender, RoutedEventArgs e)
    {
        if (_recordingShortcut) return;
        _recordingShortcut = true;
        _pendingShortcut = null;
        ShortcutRecordingChanged?.Invoke(true);
        ShortcutRecordButton.Content = "Press shortcut…";
        ShortcutRemoveButton.IsEnabled = false;
        ShowStatus("Press a key or key combination. Esc cancels.");
        Keyboard.Focus(ShortcutRecordButton);
    }

    private void ShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingShortcut) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            FinishShortcutRecording(null);
            return;
        }
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Windows;
        var shortcut = new ShortcutBinding((uint)virtualKey, modifiers);
        if (!shortcut.IsEnabled || !shortcut.IsValid) return;
        _pendingShortcut = shortcut;
        ShortcutRecordButton.Content = shortcut.DisplayName;
        ShowStatus("Release the key to save this shortcut.");
    }

    private void ShortcutPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recordingShortcut) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_pendingShortcut is { } shortcut &&
            KeyInterop.VirtualKeyFromKey(key) == shortcut.Key)
            FinishShortcutRecording(shortcut);
    }

    private void FinishShortcutRecording(ShortcutBinding? shortcut)
    {
        if (!_recordingShortcut) return;
        _recordingShortcut = false;
        _pendingShortcut = null;
        if (shortcut is { } selected)
        {
            _settings.Shortcut = selected;
            _settings.Save();
            SettingsChanged?.Invoke();
        }
        ShowStatus(null);
        UpdateShortcutDisplay();
        ShortcutRecordingChanged?.Invoke(false);
    }

    private void RemoveShortcut(object sender, RoutedEventArgs e)
    {
        if (_recordingShortcut) FinishShortcutRecording(null);
        _settings.Shortcut = ShortcutBinding.Disabled;
        _settings.Save();
        UpdateShortcutDisplay();
        ShowStatus(null);
        SettingsChanged?.Invoke();
    }

    private void UpdateShortcutDisplay()
    {
        ShortcutRecordButton.Content = _settings.Shortcut.IsEnabled
            ? _settings.Shortcut.DisplayName : "Set shortcut";
        ShortcutRemoveButton.IsEnabled = _settings.Shortcut.IsEnabled;
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message ?? "";
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SettingsDeactivated(object sender, EventArgs e) => FinishShortcutRecording(null);

    private void SettingsVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) FinishShortcutRecording(null);
    }

    private void ThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || HoldThresholdBox is null || ThresholdText is null) return;
        _settings.HoldMilliseconds = (int)HoldThresholdBox.Value;
        ThresholdText.Text = $"{_settings.HoldMilliseconds} ms";
        _settings.Save();
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || StartupCheck is null) return;
        try
        {
            var enabled = StartupCheck.IsChecked == true;
            StartupRegistration.SetEnabled(enabled);
            _settings.LaunchAtSignIn = enabled;
            _settings.Save();
            ShowStatus(null);
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not update sign-in setting: {ex.Message}");
            _initializing = true;
            StartupCheck.IsChecked = StartupRegistration.IsEnabled();
            _initializing = false;
        }
    }
}
