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

                var childPid = await RunNetworkProcess();
                await Task.Delay(TimeSpan.FromSeconds(2));
                var snapshot = engine.Snapshot(DateTimeOffset.UtcNow);
                if (snapshot.Processes.Any(row => row.Process.Identity.Pid == childPid && row.Logical.Session.Bytes != 0))
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

    private static async Task<int> RunNetworkProcess()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(LiveChecks).Assembly.Location);
        start.ArgumentList.Add("--network-probe");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start network probe.");
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
