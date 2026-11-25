using System.ComponentModel;
using System.Diagnostics;
using SsdGuardian.Windows;

namespace SsdGuardian.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "SSD Guardian", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }

        var store = new ApplicationStore(options.DataDirectory);
        var profile = SettingsProfile.Load(store, out var warning);
        UiText.Language = profile.Language;

        if (options.SmokeDuration is null && options.RequestElevation && !DiskWriteMonitor.IsElevated && TryElevate(options, out _))
        {
            return 0;
        }

        using var form = new MainForm(options, store, profile, warning);
        using var context = new ApplicationContext(form);
        form.Start();
        Application.Run(context);
        return form.ExitCode;
    }

    internal static bool TryElevate(LaunchOptions options, out string? failure)
    {
        var processPath = Environment.ProcessPath;
        if (processPath is null)
        {
            failure = UiText.Get("Could not determine the application path.", "Не удалось определить путь к программе.");
            return false;
        }

        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(options.DataDirectory);
        if (options.Tray)
        {
            start.ArgumentList.Add("--tray");
        }

        if (options.Demo)
        {
            start.ArgumentList.Add("--demo");
        }

        try
        {
            using var launched = Process.Start(start);
            failure = null;
            return true;
        }
        catch (Win32Exception ex)
        {
            failure = ex.NativeErrorCode == 1223
                ? UiText.Get("The UAC prompt was canceled.", "Запрос UAC отменён.")
                : UiText.Get($"Windows could not restart SSD Guardian as administrator (error {ex.NativeErrorCode}).",
                    $"Windows не удалось перезапустить SSD Guardian с правами администратора (ошибка {ex.NativeErrorCode}).");
            return false;
        }
    }
}
