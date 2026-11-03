using Microsoft.Win32;

namespace LiveHelpDesk.App.Services;

/// <summary>
/// "Start with Windows" for this user, through the per-user Run key: no administrator needed, and it only affects
/// this account. The app starts hidden in the tray.
/// </summary>
public sealed class AutoStart(
    string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string valueName = "LiveHelpDesk")
{
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    public void Set(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKeyPath);
        if (enabled)
        {
            key.SetValue(valueName, $"\"{executablePath}\" --minimized");
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return key?.GetValue(valueName) as string;
        }
    }
}
