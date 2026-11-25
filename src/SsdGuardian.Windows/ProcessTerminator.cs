using System.Runtime.InteropServices;
using SsdGuardian.Core;

namespace SsdGuardian.Windows;

public sealed record TerminationResult(bool Success, string Message)
{
    public string? MessageRussian { get; init; }

    public string GetMessage(UiLanguage language) =>
        language == UiLanguage.Russian ? MessageRussian ?? Message : Message;
}

public static class ProcessTerminator
{
    private static readonly TerminationResult ReservedPid = Deny("This PID is reserved or belongs to SSD Guardian.",
        "Защищённый PID или сам SSD Guardian.");
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "secure system", "smss.exe", "csrss.exe",
        "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe",
        "dllhost.exe", "rundll32.exe", "taskhostw.exe", "taskhost.exe", "conhost.exe", "dwm.exe",
        "sihost.exe", "fontdrvhost.exe", "RuntimeBroker.exe", "backgroundTaskHost.exe", "wudfhost.exe",
        "WmiPrvSE.exe", "spoolsv.exe", "LogonUI.exe", "explorer.exe"
    };

    // The caller collects explicit confirmation for this exact identity before calling.
    public static TerminationResult Terminate(ProcessIdentity target)
    {
        int guardianPid = Environment.ProcessId;
        if (target.Pid <= 4 || target.Pid == guardianPid)
            return ReservedPid;

        using var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessTerminate, false, target.Pid);
        if (process.IsInvalid)
            return NativeFailure("Windows denied access to the process", "Windows не разрешила открыть процесс");

        // The handle pins a single process object, including during PID reuse.
        var creation = NativeMethods.ReadCreationTime(process);
        bool? critical = NativeMethods.IsProcessCritical(process, out bool criticalValue) ? criticalValue : null;
        bool? protection = NativeMethods.GetProcessInformation(process, NativeMethods.ProcessProtectionLevelInfo,
            out uint level, sizeof(uint)) ? level != NativeMethods.ProtectionLevelNone : null;
        string? path = NativeMethods.ReadImagePath(process);
        if (GetSafetyFailure(target, creation, critical, protection, path, guardianPid) is { } failure)
            return failure;

        return NativeMethods.TerminateProcess(process, 1)
            ? new(true, "Windows accepted the request to end the process.")
                { MessageRussian = "Windows приняла запрос на завершение одного процесса." }
            : NativeFailure("Windows rejected the request to end the process", "Windows отклонила завершение процесса");
    }

    internal static TerminationResult? GetSafetyFailure(ProcessIdentity selected, DateTimeOffset? currentCreation,
        bool? isCritical, bool? isProtected, string? currentPath, int guardianPid)
    {
        if (selected.Pid <= 4 || selected.Pid == guardianPid)
            return ReservedPid;
        if (currentCreation is null)
            return Deny("The process creation time is unavailable.", "Время создания процесса недоступно.");
        if (selected.CreatedAt != currentCreation.Value)
            return Deny("The PID now belongs to a different process instance.",
                "PID теперь принадлежит другому экземпляру процесса.");
        if (isCritical is true)
            return Deny("Windows marked the process as critical.", "Windows пометила процесс как критический.");
        if (isProtected is true)
            return Deny("Windows marked the process as protected.", "Windows пометила процесс как защищённый.");
        if (isCritical is null || isProtected is null || string.IsNullOrWhiteSpace(currentPath) ||
            !Path.IsPathFullyQualified(currentPath))
            return Deny("Could not verify that ending the process is safe.",
                "Не удалось подтвердить безопасность завершения процесса.");
        if (SharedHosts.Contains(Path.GetFileName(currentPath)))
            return Deny("Ending this shared system process is blocked.", "Общий системный процесс защищён от завершения.");

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (windows.Length > 0 && currentPath.StartsWith(windows + '\\', StringComparison.OrdinalIgnoreCase) &&
            !currentPath.StartsWith(windows + @"\System32\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase))
            return Deny("Ending a Windows system executable is blocked.", "Исполняемый файл Windows защищён от завершения.");

        return null;
    }

    private static TerminationResult Deny(string english, string russian) =>
        new(false, english) { MessageRussian = russian };

    private static TerminationResult NativeFailure(string englishOperation, string russianOperation)
    {
        int error = Marshal.GetLastWin32Error();
        return Deny($"{englishOperation} (Win32 {error}).", $"{russianOperation} (Win32 {error}).");
    }
}
