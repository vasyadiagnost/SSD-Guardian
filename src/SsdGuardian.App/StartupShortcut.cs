using System.Runtime.InteropServices;

namespace SsdGuardian.App;

internal static class StartupShortcut
{
    private static string FileName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify),
        "SSD Guardian.lnk");

    internal static bool IsEnabled => File.Exists(FileName);

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(FileName);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(FileName);
            try
            {
                var program = Environment.ProcessPath ?? throw new InvalidOperationException(
                    UiText.Get("Could not determine the application path.", "Не найден путь приложения."));
                link.TargetPath = program;
                link.WorkingDirectory = Path.GetDirectoryName(program);
                link.Arguments = "--tray --request-elevation";
                link.Description = "SSD Guardian";
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
