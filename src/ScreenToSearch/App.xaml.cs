using System.IO;
using System.Threading;
using System.Windows;

namespace screen_to_search;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private ResidentHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--stage0-selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = e.Args.Length > 1 ? e.Args[1]
                : Path.Combine(AppContext.BaseDirectory, "stage0-report.json");
            var code = await StageZeroSelfTest.RunAsync(report);
            Shutdown(code);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--stage2-selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = e.Args.Length > 1 ? e.Args[1]
                : Path.Combine(AppContext.BaseDirectory, "stage2-report.json");
            var code = await StageTwoSelfTest.RunAsync(report);
            Shutdown(code);
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--ocr-image-report")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await OcrQualityProbe.RunAsync(e.Args[1], e.Args[2]);
            Shutdown(code);
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--ocr-model-compare")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(await OcrModelComparison.RunAsync(e.Args[1], e.Args[2])); }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[2], ex.ToString());
                Shutdown(1);
            }
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _instanceMutex = new Mutex(false, @"Local\CircleToSearchForWindows.Singleton.5f238b73");
        if (!_instanceMutex.WaitOne(0))
        {
            await ResidentHost.NotifyFirstInstanceAsync();
            Shutdown();
            return;
        }
        try
        {
            _host = new ResidentHost(this, AppSettings.Load());
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Screen to Search could not start: {ex.Message}", "Screen to Search",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
