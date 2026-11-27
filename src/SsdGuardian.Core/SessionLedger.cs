namespace SsdGuardian.Core;

// The engine holds its gate for every ledger operation. Rows retain only session totals and process metadata.
internal sealed class SessionLedger
{
    private const int MaxRows = 4096;
    private const int MaxRecentExits = 512;
    private const int MaxImagePathLength = 2048;
    private const int MaxImageNameLength = 256;
    private readonly Dictionary<ProcessIdentity, ProcessLifetime> lifetimes = [];
    private readonly LinkedList<ProcessIdentity> completed = new();
    private readonly Dictionary<ProcessIdentity, DateTimeOffset> recentExits = [];
    private readonly Queue<ProcessIdentity> recentExitOrder = new();
    private int rowCount;
    private DateTimeOffset? latestOtherWrite;
    private WriteTotals knownLogical, unidentifiedLogical, otherLogical;

    internal void RecordUnidentified(long bytes)
    {
        knownLogical = knownLogical.Add(bytes);
        unidentifiedLogical = unidentifiedLogical.Add(bytes);
    }

    internal void RecordProcess(ProcessInfo process, DiskInfo disk, DateTimeOffset timestamp, long bytes)
    {
        knownLogical = knownLogical.Add(bytes);
        lifetimes.TryGetValue(process.Identity, out var lifetime);
        var row = lifetime?.FindDisk(disk.ObservationId);
        if (row is null)
        {
            if (rowCount >= MaxRows && !CompactCompleted(process.Identity))
            {
                AddOther(new WriteTotals(bytes, 1), timestamp);
                return;
            }

            if (lifetime is null)
            {
                lifetime = new ProcessLifetime(LimitMetadata(process));
                lifetimes.Add(process.Identity, lifetime);
                if (recentExits.TryGetValue(process.Identity, out var exitedAt))
                    EndLifetime(lifetime, exitedAt);
            }

            row = new DiskLedger(disk.ObservationId, latestOtherWrite is { } latest && process.Identity.CreatedAt <= latest);
            lifetime.Disks.Add(row);
            rowCount++;
        }

        if (lifetime!.Process.Path is null && process.Path is not null && ExecutablePath(process.Path) is { } path)
            lifetime.Process = LimitMetadata(process with { Path = path });
        row.DiskNumber = disk.Number;
        row.Totals = row.Totals.Add(bytes);
        if (timestamp > row.LastWrite)
            row.LastWrite = timestamp;
    }

    internal void MarkExited(ProcessIdentity identity, DateTimeOffset exitedAt)
    {
        if (identity.Pid <= 0 || identity.CreatedAt == default || exitedAt < identity.CreatedAt)
            return;
        if (lifetimes.TryGetValue(identity, out var lifetime))
            EndLifetime(lifetime, exitedAt);

        if (recentExits.ContainsKey(identity))
            return;
        if (recentExits.Count >= MaxRecentExits)
            recentExits.Remove(recentExitOrder.Dequeue());
        recentExits.Add(identity, exitedAt);
        recentExitOrder.Enqueue(identity);
    }

    internal SessionLedgerSnapshot Snapshot(IReadOnlyList<DiskSnapshot> diskRows, IReadOnlyList<ProcessSnapshot> detailedRows)
    {
        var connected = diskRows.Where(row => row.Disk.IsConnected).Select(row => row.Disk)
            .ToDictionary(disk => disk.ObservationId);
        var details = detailedRows.ToDictionary(row => (row.Process.Identity, row.DiskNumber));
        var rows = new ProcessSessionSnapshot[rowCount];
        var rowIndex = 0;
        var programs = new Dictionary<string, ProgramTotals>(StringComparer.OrdinalIgnoreCase);
        WriteTotals active = default, ended = default, unknownProgram = default;

        foreach (var lifetime in lifetimes.Values)
        {
            WriteTotals lifetimeTotal = default;
            bool partial = false;
            foreach (var disk in lifetime.Disks)
            {
                var isConnected = connected.TryGetValue(disk.ObservationId, out var current);
                var diskNumber = current?.Number ?? disk.DiskNumber;
                ProcessSnapshot? detail = null;
                if (isConnected)
                    details.TryGetValue((lifetime.Process.Identity, diskNumber), out detail);
                var windows = detail is null
                    ? new WriteWindows(default, default, default, disk.Totals)
                    : detail.Logical with { Session = disk.Totals };
                rows[rowIndex++] = new ProcessSessionSnapshot(lifetime.Process, diskNumber, disk.ObservationId, isConnected,
                    lifetime.ExitTime, disk.IsPartial, windows, detail is not null, detail?.Files ?? [])
                {
                    AreDetailsPartial = detail is not null && (disk.IsPartial || detail.Logical.Session != disk.Totals)
                };
                partial |= disk.IsPartial;
                lifetimeTotal = lifetimeTotal.Add(disk.Totals.Bytes, disk.Totals.Operations);
            }

            if (lifetime.ExitTime is null)
                active = active.Add(lifetimeTotal.Bytes, lifetimeTotal.Operations);
            else
                ended = ended.Add(lifetimeTotal.Bytes, lifetimeTotal.Operations);

            if (lifetime.Process.Path is not { } path)
            {
                unknownProgram = unknownProgram.Add(lifetimeTotal.Bytes, lifetimeTotal.Operations);
                continue;
            }

            if (!programs.TryGetValue(path, out var program))
            {
                program = new ProgramTotals(lifetime.Process.Name);
                programs.Add(path, program);
            }
            program.Instances++;
            if (lifetime.ExitTime is not null)
                program.ExitedInstances++;
            program.Totals = program.Totals.Add(lifetimeTotal.Bytes, lifetimeTotal.Operations);
            program.IsPartial |= partial;
        }

        var programRows = programs.Select(pair => new ProgramSessionSnapshot(pair.Key, pair.Value.Name,
            pair.Value.Instances, pair.Value.ExitedInstances, pair.Value.IsPartial || otherLogical.Operations > 0,
            pair.Value.Totals)).ToArray();
        return new SessionLedgerSnapshot(knownLogical, active, ended, otherLogical, unidentifiedLogical,
            unknownProgram, rows, programRows);
    }

    private void EndLifetime(ProcessLifetime lifetime, DateTimeOffset exitedAt)
    {
        if (lifetime.ExitTime is not null)
            return;
        lifetime.ExitTime = exitedAt;
        completed.AddLast(lifetime.Process.Identity);
    }

    private bool CompactCompleted(ProcessIdentity incoming)
    {
        var node = completed.First;
        if (node is not null && node.Value == incoming)
            node = node.Next;
        if (node is null)
            return false;

        var lifetime = lifetimes[node.Value];
        lifetimes.Remove(node.Value);
        completed.Remove(node);
        rowCount -= lifetime.Disks.Count;
        foreach (var disk in lifetime.Disks)
            AddOther(disk.Totals, disk.LastWrite);
        return true;
    }

    private void AddOther(WriteTotals totals, DateTimeOffset timestamp)
    {
        otherLogical = otherLogical.Add(totals.Bytes, totals.Operations);
        if (latestOtherWrite is null || timestamp > latestOtherWrite)
            latestOtherWrite = timestamp;
    }

    private static ProcessInfo LimitMetadata(ProcessInfo process)
    {
        var path = ExecutablePath(process.Path);
        var name = process.Name.Length <= MaxImageNameLength ? process.Name : process.Name[..MaxImageNameLength];
        return ReferenceEquals(name, process.Name) && ReferenceEquals(path, process.Path)
            ? process : process with { Name = name, Path = path };
    }

    private static string? ExecutablePath(string? path)
    {
        if (path is null || path.Length > MaxImagePathLength || !Path.IsPathFullyQualified(path))
            return null;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':')
            return path[4..];
        return path;
    }

    private sealed class ProcessLifetime(ProcessInfo process)
    {
        internal ProcessInfo Process { get; set; } = process;
        internal DateTimeOffset? ExitTime { get; set; }
        internal List<DiskLedger> Disks { get; } = new(1);

        internal DiskLedger? FindDisk(long observationId)
        {
            foreach (var disk in Disks)
            {
                if (disk.ObservationId == observationId)
                    return disk;
            }
            return null;
        }
    }

    private sealed class DiskLedger(long observationId, bool isPartial)
    {
        internal long ObservationId { get; } = observationId;
        internal bool IsPartial { get; } = isPartial;
        internal int DiskNumber { get; set; }
        internal WriteTotals Totals { get; set; }
        internal DateTimeOffset LastWrite { get; set; }
    }

    private sealed class ProgramTotals(string name)
    {
        internal string Name { get; } = name;
        internal int Instances { get; set; }
        internal int ExitedInstances { get; set; }
        internal bool IsPartial { get; set; }
        internal WriteTotals Totals { get; set; }
    }
}
