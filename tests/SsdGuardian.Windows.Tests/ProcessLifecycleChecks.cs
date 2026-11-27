using System.Diagnostics;
using SsdGuardian.Core;
using SsdGuardian.Windows;

internal static class ProcessLifecycleChecks
{
    internal static IEnumerable<(string Name, Action Run)> Scenarios =>
    [
        ("A process stop needs a matching creation time and confirmed exit", ConfirmNaturalExit)
    ];

    private static void ConfirmNaturalExit()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p SSD_GUARDIAN_TEST_EXIT= & exit 259");

        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start the test process");
        using var input = child.StandardInput;
        using var cache = new ProcessCache(new MonitorEngine());
        var observed = cache.Find(child.Id, DateTimeOffset.UtcNow) ??
            throw new InvalidOperationException("could not verify the running test process");
        var created = observed.Info.Identity.CreatedAt;

        cache.OnStop(child.Id, DateTimeOffset.UtcNow);
        Require(observed.ExitedAt is null, "an event alone ended the live process");
        Require(ReferenceEquals(observed, cache.Find(child.Id, DateTimeOffset.UtcNow.AddMinutes(1))),
            "the verified process was lost after the old lookup interval");
        Require(cache.Find(child.Id, created.AddTicks(-1)) is null, "a write predating the process was attributed to it");

        input.WriteLine("exit");
        Require(child.WaitForExit(5_000) && child.ExitCode == 259, "the test process did not exit naturally with code 259");
        Require(NativeMethods.ReadExitTime(child.SafeHandle, true, created.AddTicks(1)) is null,
            "a reused creation time was accepted");
        var exited = NativeMethods.ReadExitTime(child.SafeHandle, true, created);
        Require(exited is not null && exited >= created, "the native exit time was unavailable");

        cache.OnStop(child.Id, created.AddTicks(-1));
        Require(observed.ExitedAt is null, "a stop from before process creation ended this identity");
        cache.Sweep(DateTimeOffset.UtcNow);
        Require(observed.ExitedAt == exited, "the sweep did not publish the confirmed exit to pending writers");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
