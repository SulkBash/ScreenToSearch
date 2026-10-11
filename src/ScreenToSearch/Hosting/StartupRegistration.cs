using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace screen_to_search;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Keep the existing Run value so upgrades can replace the old executable path.
    private const string ValueName = "CircleToSearchForWindows";

    public static void RefreshPathIfEnabled()
    {
        try
        {
            if (IsEnabled()) SetEnabled(true);
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var assembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(assembly))
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot find app location.");
            key.SetValue(ValueName, $"\"{processPath}\"", RegistryValueKind.String);
            return;
        }
        var exe = Path.ChangeExtension(assembly, ".exe");
        var command = File.Exists(exe)
            ? $"\"{exe}\""
            : $"\"{Environment.ProcessPath}\" \"{assembly}\"";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
