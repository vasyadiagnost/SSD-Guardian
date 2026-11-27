namespace SsdGuardian.Core;

public enum UiLanguage { English, Russian }

public sealed record ProcessIdentity(int Pid, DateTimeOffset CreatedAt);

public sealed record ProcessInfo(ProcessIdentity Identity, string Name, string? Path);

public sealed record DiskInfo(int Number, string Name, bool IsSsd, IReadOnlyList<string> Volumes)
{
    public bool UsesDefaultName { get; init; }
    public string? PersistentId { get; init; }
    public bool IsConnected { get; init; } = true;
    public bool IsTypeKnown { get; init; } = true;
    public long ObservationId { get; init; }

    public string GetName(UiLanguage language) =>
        UsesDefaultName && language == UiLanguage.Russian ? $"Диск {Number}" : Name;
}

public readonly record struct DiskWrite(DateTimeOffset Timestamp, int DiskNumber, long Bytes);

public readonly record struct FileWrite(
    DateTimeOffset Timestamp, int? DiskNumber, ProcessInfo? Process, string? Path, long Bytes, long? Offset = null);

public readonly record struct FileSizeSample(DateTimeOffset Timestamp, int DiskNumber, string Path, long Length);

public readonly record struct WriteTotals(long Bytes, long Operations)
{
    internal WriteTotals Add(long bytes, long operations = 1) =>
        new(SaturatingAdd(Bytes, bytes), SaturatingAdd(Operations, operations));

    internal static long SaturatingAdd(long total, long term) =>
        total > long.MaxValue - term ? long.MaxValue : total + term;
}

public sealed record WriteWindows(WriteTotals Last10, WriteTotals Last30, WriteTotals Last60, WriteTotals Session);

public sealed record DiskSnapshot(DiskInfo Disk, WriteWindows Physical, WriteWindows Logical, WriteWindows UnattributedLogical);

public sealed record FileSnapshot(string Path, WriteWindows Logical)
{
    public WriteTotals? Window { get; init; }
}

public sealed record RecentTermination(ProcessIdentity Identity, string Path, DateTimeOffset Timestamp);

public sealed record ProcessSnapshot(
    ProcessInfo Process, int DiskNumber, WriteWindows Logical, IReadOnlyList<FileSnapshot> Files);

public sealed record ProcessSessionSnapshot(
    ProcessInfo Process, int DiskNumber, long DiskObservationId, bool IsDiskConnected, DateTimeOffset? ExitTime,
    bool IsPartial, WriteWindows Logical, bool HasRollingDetails, IReadOnlyList<FileSnapshot> Files)
{
    public bool AreDetailsPartial { get; init; }
}

public sealed record ProgramSessionSnapshot(
    string Path, string Name, int Instances, int ExitedInstances, bool IsPartial, WriteTotals Logical);

public sealed record SessionLedgerSnapshot(
    WriteTotals KnownLogical, WriteTotals ActiveLogical, WriteTotals ExitedLogical, WriteTotals OtherLogical,
    WriteTotals UnidentifiedLogical, WriteTotals UnidentifiedProgramLogical,
    IReadOnlyList<ProcessSessionSnapshot> Processes, IReadOnlyList<ProgramSessionSnapshot> Programs)
{
    internal static readonly SessionLedgerSnapshot Empty = new(default, default, default, default, default, default, [], []);
}

public sealed record MonitorSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<DiskSnapshot> Disks,
    IReadOnlyList<ProcessSnapshot> Processes,
    WriteTotals SessionPhysical,
    WriteTotals UnmappedLogical,
    WriteTotals UnmappedPhysical,
    long LostEvents,
    bool IsReliable,
    long AnomalyCount,
    long UntrackedProcesses,
    long UntrackedFiles)
{
    public bool HasObservationGap { get; init; }
    public SessionLedgerSnapshot SessionLedger { get; init; } = SessionLedgerSnapshot.Empty;
}

public sealed record Anomaly(
    DateTimeOffset Timestamp,
    ProcessInfo Process,
    int DiskNumber,
    int WindowMinutes,
    WriteTotals Logical,
    WriteTotals Physical,
    double BytesPerSecond,
    double ProjectedDailyBytes,
    IReadOnlyList<FileSnapshot> Files,
    string Reason,
    bool IsElevated,
    bool IsNvidiaSignature,
    bool IsRecurrence)
{
    public string? ReasonRussian { get; init; }

    public string GetReason(UiLanguage language) =>
        language == UiLanguage.Russian ? ReasonRussian ?? Reason : Reason;

    public static Anomaly CreateDemo(DateTimeOffset now)
    {
        var process = new ProcessInfo(new ProcessIdentity(0, now), "Example.exe", null);
        var logical = new WriteTotals(2L << 30, 32_768);
        var physical = new WriteTotals(1L << 30, 20_480);
        var file = new FileSnapshot(@"C:\Demo\example.log", new WriteWindows(logical, logical, logical, logical))
        {
            Window = logical
        };
        var bytesPerSecond = logical.Bytes / 1800d;
        return new Anomaly(now, process, 0, 30, logical, physical, bytesPerSecond,
            bytesPerSecond * 86_400, [file], "Demo of sustained cyclic writes", false, false, false)
        {
            ReasonRussian = "Демонстрация длительной циклической записи"
        };
    }
}
