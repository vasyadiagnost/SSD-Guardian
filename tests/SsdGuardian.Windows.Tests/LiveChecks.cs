using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SsdGuardian.Core;
using SsdGuardian.Windows;

internal static class LiveChecks
{
    private const int ChunkBytes = 256 * 1024;
    private const int ChunkCount = 16;
    private const int DatagramBytes = 4096;
    private const int DatagramCount = 512;

    internal static async Task<int> Run()
    {
        if (!DiskWriteMonitor.IsElevated)
        {
            Console.Error.WriteLine("Run the live checks as administrator.");
            return 1;
        }

        var id = Guid.NewGuid();
        var name = $"SSD Guardian Test {id:N}";
        var directory = Path.Combine(Path.GetTempPath(), $"ssdguardian-test-{id:N}");
        Directory.CreateDirectory(directory);
        try
        {
            var engine = new MonitorEngine();
            var statuses = new ConcurrentQueue<MonitorStatus>();
            using var abandoned = OwnedEtwSession.Start(name, id);
            using (var monitor = new DiskWriteMonitor(engine, statuses.Enqueue, name, id))
            {
                monitor.Start();
                await WaitFor(() => statuses.Any(item => item.State == MonitorState.Running), "ETW startup", statuses);
                Console.WriteLine("PASS Guardian reacquired a stale session with the matching name and identity");
                var beforeFile = Path.Combine(directory, "before.bin");
                WriteFile(beforeFile);
                await CheckWrite(engine, beforeFile, statuses);
                Console.WriteLine($"PASS SSD write before interruption ({ChunkBytes * ChunkCount:N0} logical bytes, {ChunkCount} operations)");

                if (OwnedEtwSession.TryStopVerified(name, Guid.NewGuid()))
                    throw new InvalidOperationException("Wrong ETW session identity was accepted.");
                if (!OwnedEtwSession.TryStopVerified(name, id))
                    throw new InvalidOperationException("Could not interrupt the dedicated ETW test session.");

                await WaitFor(() => statuses.Any(item => item.State == MonitorState.Unavailable) &&
                    statuses.Any(item => item.State == MonitorState.Degraded) && !engine.Snapshot(DateTimeOffset.UtcNow).IsReliable,
                    "automatic ETW recovery and coverage gap", statuses);
                var afterFile = Path.Combine(directory, "after.bin");
                WriteFile(afterFile);
                await CheckWrite(engine, afterFile, statuses);
                Console.WriteLine("PASS ETW reconnects after an interruption and counts new writes with a coverage warning");

                await CheckNaturalExit(engine, Path.Combine(directory, "child.bin"), statuses);

                var childPid = await RunNetworkProcess();
                await Task.Delay(TimeSpan.FromSeconds(2));
                var snapshot = engine.Snapshot(DateTimeOffset.UtcNow);
                if (snapshot.SessionLedger.Processes.Any(row => row.Process.Identity.Pid == childPid && row.Logical.Session.Bytes != 0))
                    throw new InvalidOperationException("Loopback-only network probe was attributed to an SSD.");
                Console.WriteLine($"PASS {DatagramBytes * DatagramCount:N0} loopback UDP bytes generated no attributed SSD writes");
                Console.WriteLine($"Observed physical SSD traffic including background activity: {snapshot.SessionPhysical.Bytes:N0} bytes");
                Console.WriteLine($"Reported ETW events/buffers lost: {snapshot.LostEvents:N0}");
            }

            // A second stop of the owned name would succeed only if the monitor leaked its logger.
            if (OwnedEtwSession.TryStopVerified(name, id))
                throw new InvalidOperationException("The monitor left its ETW session active after disposal.");
            Console.WriteLine("PASS Dedicated ETW session released on disposal");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            File.Delete(Path.Combine(directory, "before.bin"));
            File.Delete(Path.Combine(directory, "after.bin"));
            File.Delete(Path.Combine(directory, "child.bin"));
            Directory.Delete(directory);
        }
    }

    internal static async Task<int> SendLoopbackTraffic()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var destination = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var payload = new byte[DatagramBytes];
        for (var index = 0; index < DatagramCount; index++)
        {
            var sent = await sender.SendAsync(payload, destination, timeout.Token);
            var received = await receiver.ReceiveAsync(timeout.Token);
            if (sent != DatagramBytes || received.Buffer.Length != DatagramBytes)
                return 1;
        }

        return 0;
    }

    internal static int RunFileProbe(string path)
    {
        WriteFile(path);
        // Keep the instance alive until the ETW consumer observes its writes.
        Console.ReadLine();
        return 0;
    }

    private static void WriteFile(string path)
    {
        var payload = new byte[ChunkBytes];
        new Random(872).NextBytes(payload);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            ChunkBytes, FileOptions.WriteThrough);
        for (var index = 0; index < ChunkCount; index++)
            stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CheckWrite(MonitorEngine engine, string path, ConcurrentQueue<MonitorStatus> statuses)
    {
        await WaitFor(() => engine.Snapshot(DateTimeOffset.UtcNow).Processes
                .Where(row => row.Process.Identity.Pid == Environment.ProcessId)
                .SelectMany(row => row.Files)
                .Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase) &&
                    file.Logical.Session.Bytes == ChunkBytes * ChunkCount && file.Logical.Session.Operations == ChunkCount),
            $"known write in {Path.GetFileName(path)} on a recognized SSD", statuses);
    }

    private static async Task CheckNaturalExit(MonitorEngine engine, string path, ConcurrentQueue<MonitorStatus> statuses)
    {
        var start = ChildStart("--file-probe");
        start.ArgumentList.Add(path);
        start.RedirectStandardInput = true;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start file probe.");
        var identity = new ProcessIdentity(child.Id, new DateTimeOffset(child.StartTime.ToUniversalTime()));
        try
        {
            await WaitFor(() => engine.Snapshot(DateTimeOffset.UtcNow).Processes
                    .Where(row => row.Process.Identity == identity)
                    .SelectMany(row => row.Files)
                    .Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase) &&
                        file.Logical.Session == new WriteTotals(ChunkBytes * ChunkCount, ChunkCount)),
                "file probe writes before exit", statuses);
        }
        finally
        {
            child.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await child.WaitForExitAsync(timeout.Token);
        if (child.ExitCode != 0)
            throw new InvalidOperationException("The file probe failed.");

        await WaitFor(() => engine.Snapshot(DateTimeOffset.UtcNow).SessionLedger.Processes
                .Any(row => row.Process.Identity == identity && row.ExitTime is not null &&
                    row.Logical.Session == new WriteTotals(ChunkBytes * ChunkCount, ChunkCount)),
            "confirmed exit with preserved session totals", statuses);

        var ledger = engine.Snapshot(DateTimeOffset.UtcNow).SessionLedger;
        WriteTotals[] buckets = [ledger.ActiveLogical, ledger.ExitedLogical, ledger.OtherLogical, ledger.UnidentifiedLogical];
        if (ledger.KnownLogical != new WriteTotals(buckets.Sum(item => item.Bytes), buckets.Sum(item => item.Operations)))
            throw new InvalidOperationException("Live logical session totals did not balance.");

        var childPath = ledger.Processes.First(row => row.Process.Identity == identity).Process.Path;
        var program = ledger.Programs.SingleOrDefault(item => string.Equals(item.Path, childPath, StringComparison.OrdinalIgnoreCase));
        if (program is null || program.Instances < 2 || program.ExitedInstances < 1)
            throw new InvalidOperationException("Run counts did not combine the parent and exited child under their executable.");

        Console.WriteLine("PASS Native exit marked the child, retained 4 MiB/16 writes and preserved the logical balance");
        Console.WriteLine("PASS Executable summary includes distinct current and exited instances");
    }

    private static ProcessStartInfo ChildStart(string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(LiveChecks).Assembly.Location);
        start.ArgumentList.Add(mode);
        return start;
    }

    private static async Task<int> RunNetworkProcess()
    {
        using var process = Process.Start(ChildStart("--network-probe")) ??
            throw new InvalidOperationException("Could not start network probe.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The loopback UDP probe failed.");
        return process.Id;
    }

    private static async Task WaitFor(Func<bool> ready, string operation, ConcurrentQueue<MonitorStatus> statuses)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"Timed out waiting for {operation}. Statuses: {string.Join(" | ", statuses.Select(item => item.Message))}");
            await Task.Delay(100);
        }
    }
}
