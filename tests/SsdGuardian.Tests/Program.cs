using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SsdGuardian.Core;

var scenarios = new (string Name, Action Test)[]
{
    ("Normal background writes", Background),
    ("One-time large copy", LargeCopy),
    ("Sequentially growing file", SequentialFile),
    ("Sustained cyclic overwrite", CyclicOverwrite),
    ("Five active intervals out of six", SustainedIntervals),
    ("Sustained low-rate background writes", LowRate),
    ("Elevated fast cyclic overwrite", FastCyclicOverwrite),
    ("Stable small file without ETW offsets", StableSmallFile),
    ("Rewrites detected across rotating files in a custom window", MultiFileRewrite),
    ("One-volume activity does not support another volume", WrongDisk),
    ("Network-only or unverified paths are excluded from SSD counters", Unmapped),
    ("HDD writes excluded", Hdd),
    ("Restart reusing PID and renewed anomaly", Restart),
    ("Termination history restores recurrence and honors expiry", RestoredRecurrence),
    ("Lost ETW events and rolling reliability recovery", EventLoss),
    ("Observation gaps reduce reliability without fictional losses", ObservationGap),
    ("NVIDIA-specific fingerprint and threshold-only suppression", Nvidia),
    ("Independent NVIDIA threshold requires real rewrite and disk evidence", NvidiaDiagnostic),
    ("Bilingual anomaly text and previous history format", LocalizedHistory),
    ("Independent 10/30/60-minute and session totals", RollingWindows),
    ("One-hour snooze", Snooze),
    ("Concurrent telemetry aggregation", ConcurrentWrites),
    ("New heavy processes and files enter saturated tracking", AdmissionRanking),
    ("Per-disk history survives detachment and verified renumbering", InventoryHistory),
    ("Bounded retained memory during long-running high-cardinality churn", BoundedMemory)
};

var failures = 0;
foreach (var (name, test) in scenarios)
{
    try
    {
        test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {name}\n{error}");
    }
}
Console.WriteLine($"{scenarios.Length - failures}/{scenarios.Length} core scenarios passed.");
return failures == 0 ? 0 : 1;

static void Background()
{
    var core = Create();
    var process = GetProcess();
    Replay(core, process, Epoch(), 30, 30, 65_536, offset: index => index * 65_536L);
    Equal(0, core.Evaluate(Epoch().AddMinutes(30)).Count);
    var stats = core.Snapshot(Epoch().AddMinutes(30));
    Equal(new WriteTotals(3_932_160, 60), stats.SessionPhysical);
    Equal(stats.SessionPhysical, stats.Processes.Single().Logical.Session);
    Equal(stats.SessionPhysical, stats.Processes.Single().Files.Single().Logical.Session);
}

static void LargeCopy()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 1, 5, 550_000_000, offset: index => index * 550_000_000L);
    Equal(0, core.Evaluate(Epoch().AddMinutes(1)).Count);
    Equal(6_600_000_000L, core.Snapshot(Epoch().AddMinutes(1)).SessionPhysical.Bytes);
}

static void SequentialFile()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 30, 30, 90_000_000,
        offset: index => index * 90_000_000L,
        length: index => (index + 1) * 90_000_000L);
    Equal(0, core.Evaluate(Epoch().AddMinutes(30)).Count);
}

static void CyclicOverwrite()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 30, 30, 24_000_000, offset: _ => 0, length: _ => 24_000_000);
    var alert = Single(core.Evaluate(Epoch().AddMinutes(30)));
    Equal(30, alert.WindowMinutes);
    Equal(false, alert.IsElevated);
    Equal(48d * alert.Logical.Bytes, alert.ProjectedDailyBytes);
    Equal(alert.Logical, alert.Physical);
    Equal(1L, core.Snapshot(alert.Timestamp).AnomalyCount);
    Equal(0, core.Evaluate(alert.Timestamp.AddSeconds(5)).Count);
}

static void SustainedIntervals()
{
    var process = GetProcess();
    var at = Epoch();
    var firstWindowSlot = at.AddSeconds(5);
    var insufficient = Create();
    Replay(insufficient, process, firstWindowSlot, 10, 30, 32_000_000, offset: _ => 0);
    Replay(insufficient, process, firstWindowSlot.AddMinutes(20), 10, 30, 32_000_000, offset: _ => 0);
    Equal(0, insufficient.Evaluate(at.AddMinutes(30)).Count);

    var sufficient = Create();
    Replay(sufficient, process, firstWindowSlot, 10, 30, 32_000_000, offset: _ => 0);
    Replay(sufficient, process, firstWindowSlot.AddMinutes(15), 15, 30, 32_000_000, offset: _ => 0);
    var alert = Single(sufficient.Evaluate(at.AddMinutes(30)));
    True(alert.GetReason(UiLanguage.English).Contains("5 of 6", StringComparison.Ordinal));
    True(alert.GetReason(UiLanguage.Russian).Contains("5 из 6", StringComparison.Ordinal));
}

static void LowRate()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 180, 30, 65_536, offset: _ => 0, length: _ => 65_536);
    Equal(0, core.Evaluate(Epoch().AddMinutes(180)).Count);
    Equal(23_592_960L, core.Snapshot(Epoch().AddMinutes(180)).SessionPhysical.Bytes);
}

static void FastCyclicOverwrite()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 10, 15, 140_000_000, offset: _ => 0);
    var alert = Single(core.Evaluate(Epoch().AddMinutes(10)));
    Equal(true, alert.IsElevated);
    Equal(10, alert.WindowMinutes);
    True(alert.Logical.Bytes >= 5_000_000_000L);
}

static void StableSmallFile()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 30, 30, 24_000_000, length: _ => 30_000_000);
    var alert = Single(core.Evaluate(Epoch().AddMinutes(30)));
    True(alert.GetReason(UiLanguage.English).Contains("small file", StringComparison.Ordinal));
    True(alert.GetReason(UiLanguage.Russian).Contains("небольшого файла", StringComparison.Ordinal));
}

static void MultiFileRewrite()
{
    var options = new GuardianOptions
    {
        LongWindowMinutes = 20, LongMinimumActivitySeconds = 900, ActivityIntervalMinutes = 2,
        RequiredActiveFiveMinuteIntervals = 8
    };
    var core = Create(options);
    var process = GetProcess();
    var at = Epoch();
    for (var index = 0; index < 4; index++)
        Replay(core, process, at.AddMinutes(index * 5), 5, 30, 30_000_000,
            offset: _ => 0, path: $@"C:\log-{index}.dat");

    var alert = Single(core.Evaluate(at.AddMinutes(20)));
    Equal(20, alert.WindowMinutes);
    Equal(4, alert.Files.Count);
    True(alert.GetReason(UiLanguage.English).Contains("4 files", StringComparison.Ordinal));
    True(alert.Files.All(file => file.Window is { Bytes: > 0 }));
    Equal(alert.Logical.Bytes, alert.Files.Sum(file => file.Window!.Value.Bytes));
}

static void LocalizedHistory()
{
    var alert = Anomaly.CreateDemo(Epoch());
    var saved = JsonSerializer.SerializeToNode(alert)!.AsObject();
    var restored = saved.Deserialize<Anomaly>()!;
    Equal(alert.GetReason(UiLanguage.English), restored.GetReason(UiLanguage.English));
    Equal(alert.GetReason(UiLanguage.Russian), restored.GetReason(UiLanguage.Russian));
    True(restored.GetReason(UiLanguage.English) != restored.GetReason(UiLanguage.Russian));

    const string oldReason = "Длительная запись в файл";
    saved.Remove(nameof(Anomaly.ReasonRussian));
    saved[nameof(Anomaly.Reason)] = oldReason;
    var legacy = saved.Deserialize<Anomaly>()!;
    Equal(oldReason, legacy.GetReason(UiLanguage.English));
    Equal(oldReason, legacy.GetReason(UiLanguage.Russian));
}

static void WrongDisk()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 30, 30, 40_000_000, offset: _ => 0, physicalDisk: 2);
    var now = Epoch().AddMinutes(30);
    Equal(0, core.Evaluate(now).Count);
    var stats = core.Snapshot(now);
    Equal(0L, stats.Disks.Single(item => item.Disk.Number == 0).Physical.Session.Bytes);
    True(stats.Disks.Single(item => item.Disk.Number == 2).Physical.Session.Bytes > 0);
}

static void Unmapped()
{
    var core = Create();
    var now = Epoch();
    core.RecordFileWrite(new FileWrite(now, null, GetProcess(), @"\\server\share\archive.zip", 6_000_000_000));
    var stats = core.Snapshot(now);
    Equal(0L, stats.SessionPhysical.Bytes);
    Equal(0, stats.Processes.Count);
    Equal(6_000_000_000L, stats.UnmappedLogical.Bytes);
    Equal(0, core.Evaluate(now).Count);
}

static void Hdd()
{
    var core = Create();
    Replay(core, GetProcess(), Epoch(), 30, 30, 40_000_000, offset: _ => 0, disk: 1);
    var stats = core.Snapshot(Epoch().AddMinutes(30));
    Equal(0L, stats.SessionPhysical.Bytes);
    Equal(0L, stats.UnmappedLogical.Bytes);
    Equal(0L, stats.UnmappedPhysical.Bytes);
    Equal(0, stats.Processes.Count);
    Equal(0, core.Evaluate(stats.Timestamp).Count);
}

static void Restart()
{
    var core = Create();
    var first = GetProcess(315);
    Replay(core, first, Epoch(), 30, 30, 30_000_000, offset: _ => 0);
    var finish = Epoch().AddMinutes(30);
    Equal(false, Single(core.Evaluate(finish)).IsRecurrence);
    core.MarkTerminated(first.Identity, first.Path, finish);
    var restartedAt = finish.AddSeconds(5);
    var second = first with { Identity = first.Identity with { CreatedAt = restartedAt } };
    core.RecordFileWrite(new FileWrite(finish, 0, second, @"C:\activity.log", 123));
    Equal(123L, core.Snapshot(finish).Disks.First().UnattributedLogical.Session.Bytes);
    Replay(core, second, restartedAt, 30, 30, 31_000_000, offset: _ => 0);
    var secondAlert = Single(core.Evaluate(restartedAt.AddMinutes(30)));
    Equal(true, secondAlert.IsRecurrence);
    Equal(second.Identity, secondAlert.Process.Identity);
    var rows = core.Snapshot(secondAlert.Timestamp).Processes;
    Equal(2, rows.Count);
    Equal(1_800_000_000L, rows.Single(row => row.Process.Identity == first.Identity).Logical.Session.Bytes);
    Equal(1_860_000_000L, rows.Single(row => row.Process.Identity == second.Identity).Logical.Session.Bytes);
}

static void RestoredRecurrence()
{
    var options = new GuardianOptions { RecurrenceRetentionDays = 1 };
    var process = GetProcess();
    var exitedAt = Epoch().AddMinutes(5);
    var before = Create(options);
    before.MarkTerminated(process.Identity, process.Path!.ToLowerInvariant(), exitedAt);
    var stored = JsonSerializer.Serialize(before.ExportTerminations(exitedAt));
    var after = Create(options);
    after.RestoreTerminations(JsonSerializer.Deserialize<RecentTermination[]>(stored)!, exitedAt.AddSeconds(5));

    var restartedAt = exitedAt.AddSeconds(1);
    var restarted = process with { Identity = process.Identity with { CreatedAt = restartedAt } };
    Replay(after, restarted, restartedAt, 30, 30, 24_000_000, offset: _ => 0);
    Equal(true, Single(after.Evaluate(restartedAt.AddMinutes(30))).IsRecurrence);
    Equal(0, after.ExportTerminations(exitedAt.AddDays(2)).Count);
}

static void EventLoss()
{
    var core = Create();
    var at = Epoch();
    Replay(core, GetProcess(), at, 30, 30, 24_000_000, offset: _ => 0);
    var loss = at.AddMinutes(28);
    core.ReportLostEvents(25, loss);
    core.ReportLostEvents(6, loss.AddSeconds(-1));
    var damaged = core.Snapshot(at.AddMinutes(30));
    Equal(31L, damaged.LostEvents);
    Equal(false, damaged.IsReliable);
    True(damaged.SessionPhysical.Bytes > 1_000_000_000);
    Equal(0, core.Evaluate(damaged.Timestamp).Count);
    Replay(core, GetProcess(), at.AddMinutes(30), 30, 30, 24_000_000, offset: _ => 0);
    Equal(1, core.Evaluate(at.AddMinutes(60)).Count);
    var clean = core.Snapshot(loss.AddHours(1));
    Equal(31L, clean.LostEvents);
    Equal(true, clean.IsReliable);
}

static void ObservationGap()
{
    var core = Create();
    var at = Epoch();
    var process = GetProcess();
    Replay(core, process, at, 30, 30, 24_000_000, offset: _ => 0);
    var gap = at.AddMinutes(29);
    core.ReportObservationGap(gap);
    var partial = core.Snapshot(at.AddMinutes(30));
    Equal(0L, partial.LostEvents);
    Equal(false, partial.IsReliable);
    Equal(0, core.Evaluate(partial.Timestamp).Count);
    Replay(core, process, at.AddMinutes(30), 30, 30, 24_000_000, offset: _ => 0);
    Equal(1, core.Evaluate(at.AddMinutes(60)).Count);
    Equal(true, core.Snapshot(gap.AddHours(1)).IsReliable);
}

static void Nvidia()
{
    var process = GetProcess() with
    {
        Name = "NVDisplay.Container.exe",
        Path = @"C:\Windows\System32\DriverStore\NVDisplay.Container.exe"
    };
    const string target = @"C:\ProgramData\NVIDIA\nvlog.nvlgstg";
    var core = Create();
    Replay(core, process, Epoch(), 30, 30, 24_000_000, offset: _ => 0, path: target);
    var now = Epoch().AddMinutes(30);
    Equal(true, Single(core.Evaluate(now)).IsNvidiaSignature);
    var disabled = Create(new GuardianOptions { EnableNvidiaSignature = false });
    Replay(disabled, process, Epoch(), 30, 30, 24_000_000, offset: _ => 0, path: target);
    Equal(false, Single(disabled.Evaluate(now)).IsNvidiaSignature);
    var burst = Create();
    Replay(burst, process, Epoch(), 1, 5, 550_000_000, offset: _ => 0, path: target);
    Equal(0, burst.Evaluate(Epoch().AddMinutes(1)).Count);
}

static void NvidiaDiagnostic()
{
    const string path = @"C:\ProgramData\NVIDIA\nvlog.nvlgstg";
    var process = GetProcess() with { Name = "NVDisplay.Container.exe", Path = @"C:\NVIDIA\NVDisplay.Container.exe" };
    var at = Epoch();
    var now = at.AddMinutes(10);
    var core = Create();
    Replay(core, process, at, 10, 15, 750_000, offset: _ => 0, path: path);
    var alert = Single(core.Evaluate(now));
    True(alert.IsNvidiaSignature && !alert.IsElevated);
    True(alert.Logical.Bytes < new GuardianOptions().LongThresholdBytes);
    True(alert.GetReason(UiLanguage.English).Contains("dedicated", StringComparison.Ordinal));

    var wrongDisk = Create();
    Replay(wrongDisk, process, at, 10, 15, 750_000, offset: _ => 0, path: path, physicalDisk: 2);
    Equal(0, wrongDisk.Evaluate(now).Count);
    var sequential = Create();
    Replay(sequential, process, at, 10, 15, 750_000, offset: index => index * 750_000L, path: path);
    Equal(0, sequential.Evaluate(now).Count);
}

static void RollingWindows()
{
    var core = Create();
    var now = Epoch().AddHours(2);
    for (var index = 0; index < 4; index++)
    {
        var age = new[] { 9, 20, 40, 61 }[index];
        var at = now.AddMinutes(-age);
        core.RecordDiskWrite(new DiskWrite(at, 0, (index + 1) * 100));
    }
    core.RecordDiskWrite(new DiskWrite(now, 31, 200));
    var stats = core.Snapshot(now);
    var physical = stats.Disks.First().Physical;
    Equal(new WriteTotals(100, 1), physical.Last10);
    Equal(new WriteTotals(300, 2), physical.Last30);
    Equal(new WriteTotals(600, 3), physical.Last60);
    Equal(new WriteTotals(1_000, 4), physical.Session);
    Equal(new WriteTotals(200, 1), stats.UnmappedPhysical);
}

static void Snooze()
{
    var core = Create();
    var process = GetProcess();
    core.Snooze(process.Identity, Epoch());
    Replay(core, process, Epoch(), 60, 30, 24_000_000, offset: _ => 0);
    Equal(0, core.Evaluate(Epoch().AddSeconds(3595)).Count);
    Equal(1, core.Evaluate(Epoch().AddHours(1)).Count);
}

static void ConcurrentWrites()
{
    var core = Create();
    var now = Epoch();
    Parallel.For(1, 5, pid =>
    {
        var process = GetProcess(pid);
        for (var write = 0; write < 1000; write++)
        {
            core.RecordFileWrite(new FileWrite(now, 0, process, @"C:\activity.log", 4096, 0));
            core.RecordDiskWrite(new DiskWrite(now, 0, 4096));
        }
    });
    var stats = core.Snapshot(now);
    Equal(4, stats.Processes.Count);
    Equal(new WriteTotals(4096L * 4000, 4000), stats.SessionPhysical);
    Equal(stats.SessionPhysical, stats.Disks.First().Logical.Session);
}

static void AdmissionRanking()
{
    var core = Create(new GuardianOptions { MaxTrackedProcesses = 1, MaxFilesPerProcess = 1 });
    var at = Epoch();
    var old = GetProcess(1);
    var active = GetProcess(2);
    const string brief = @"C:\brief.log", repeated = @"C:\repeated.log";

    core.RecordFileWrite(new FileWrite(at, 0, old, brief, 100));
    for (var index = 0; index < 3; index++)
        core.RecordFileWrite(new FileWrite(at, 0, active, brief, 50));
    var admitted = core.Snapshot(at);
    Equal(active.Identity, Single(admitted.Processes).Process.Identity);
    Equal(50L, Single(admitted.Processes).Logical.Session.Bytes);
    Equal(100L, admitted.Disks[0].UnattributedLogical.Session.Bytes);
    Equal(2L, admitted.UntrackedProcesses);

    for (var index = 0; index < 3; index++)
        core.RecordFileWrite(new FileWrite(at, 0, active, repeated, 25));
    var ranked = core.Snapshot(at);
    Equal(repeated, Single(Single(ranked.Processes).Files).Path);
    Equal(25L, Single(Single(ranked.Processes).Files).Logical.Session.Bytes);
    Equal(325L, ranked.Disks[0].Logical.Session.Bytes);
    Equal(2L, ranked.UntrackedFiles);
}

static void InventoryHistory()
{
    var at = Epoch();
    var core = new MonitorEngine();
    var first = new DiskInfo(0, "Example model", true, ["C:"]) { PersistentId = "unit-a" };
    var replacement = first with { PersistentId = "unit-b" };
    core.SetInventory([first], at);
    core.RecordDiskWrite(new DiskWrite(at, 0, 100));
    core.SetInventory([replacement], at.AddSeconds(10));
    core.RecordDiskWrite(new DiskWrite(at.AddSeconds(5), 0, 45));
    core.RecordDiskWrite(new DiskWrite(at.AddSeconds(15), 0, 200));
    var reused = core.Snapshot(at.AddSeconds(15));
    Equal(2, reused.Disks.Count);
    Equal(100L, Single(reused.Disks.Where(row => !row.Disk.IsConnected).ToArray()).Physical.Session.Bytes);
    Equal(200L, Single(reused.Disks.Where(row => row.Disk.IsConnected).ToArray()).Physical.Session.Bytes);
    Equal(45L, reused.UnmappedPhysical.Bytes);

    core.SetInventory([replacement, first with { Number = 2, PersistentId = "UNIT-A", Volumes = ["H:"] },
        new DiskInfo(3, "Uncertain storage", false, []) { IsTypeKnown = false }], at.AddSeconds(20));
    core.RecordDiskWrite(new DiskWrite(at.AddSeconds(25), 2, 300));
    core.RecordDiskWrite(new DiskWrite(at.AddSeconds(25), 3, 70));
    var resumed = core.Snapshot(at.AddSeconds(25));
    var moved = Single(resumed.Disks.Where(row => row.Disk.Number == 2).ToArray());
    Equal(400L, moved.Physical.Session.Bytes);
    Equal(reused.Disks[1].Disk.ObservationId, moved.Disk.ObservationId);
    Equal(600L, resumed.SessionPhysical.Bytes);
    Equal(115L, resumed.UnmappedPhysical.Bytes);

    var ambiguous = new MonitorEngine();
    var noId = first with { PersistentId = null };
    ambiguous.SetInventory([noId]);
    ambiguous.RecordDiskWrite(new DiskWrite(at, 0, 999));
    ambiguous.SetInventory([]);
    ambiguous.SetInventory([noId]);
    var unknownReroute = ambiguous.Snapshot(at);
    Equal(2, unknownReroute.Disks.Count);
    Equal(0L, unknownReroute.Disks[0].Physical.Session.Bytes);
    Equal(999L, unknownReroute.Disks[1].Physical.Session.Bytes);
    True(unknownReroute.Disks[0].Disk.ObservationId != unknownReroute.Disks[1].Disk.ObservationId);
}

static void BoundedMemory()
{
    var limits = new GuardianOptions();
    var core = Create(limits);
    var at = Epoch();
    using var processMetrics = Process.GetCurrentProcess();
    Churn(0, 2);
    var baseline = GC.GetTotalMemory(forceFullCollection: true);
    Churn(2, 30);
    var final = GC.GetTotalMemory(forceFullCollection: true);
    processMetrics.Refresh();
    var stats = core.Snapshot(at.AddMinutes(90));
    Equal(limits.MaxTrackedProcesses, stats.Processes.Count);
    True(stats.Processes.All(row => row.Files.Count == limits.MaxFilesPerProcess));
    True(stats.UntrackedProcesses > 0);
    True(stats.UntrackedFiles > 0);
    True(final - baseline < 4 * 1024 * 1024);
    Console.WriteLine($"      Managed heap after full GC: {baseline:N0} B → {final:N0} B (90 min simulated).");
    Console.WriteLine($"      Test runner peak working set: {processMetrics.PeakWorkingSet64:N0} B.");
    GC.KeepAlive(core);

    void Churn(int begin, int end)
    {
        for (var batch = begin; batch < end; batch++)
        {
            var now = at.AddMinutes(3 * batch);
            for (var pid = 0; pid < limits.MaxTrackedProcesses + 16; pid++)
            {
                var process = GetProcess(100 + pid + batch * (limits.MaxTrackedProcesses + 16));
                for (var file = 0; file < limits.MaxFilesPerProcess + 6; file++)
                {
                    var path = $@"C:\file-{batch}-{pid}-{file}.log";
                    core.RecordFileWrite(new FileWrite(now, 0, process, path, 4096, 0));
                    core.RecordFileSize(new FileSizeSample(now, 0, path, 4096));
                }
            }
        }
    }
}

static void Replay(
    MonitorEngine engine, ProcessInfo process, DateTimeOffset started, int minutes, int stepSeconds, long bytes,
    Func<int, long>? offset = null, Func<int, long>? length = null, int disk = 0, int? physicalDisk = null,
    string path = @"C:\activity.log")
{
    for (var index = 0; index < minutes * 60 / stepSeconds; index++)
    {
        var at = started.AddSeconds(index * stepSeconds);
        engine.RecordFileWrite(new FileWrite(at, disk, process, path, bytes, offset?.Invoke(index)));
        engine.RecordDiskWrite(new DiskWrite(at, physicalDisk ?? disk, bytes));
        if (length is not null)
            engine.RecordFileSize(new FileSizeSample(at, disk, path, length(index)));
    }
}

static MonitorEngine Create(GuardianOptions? options = null)
{
    var engine = new MonitorEngine(options);
    engine.SetInventory([
        new DiskInfo(0, "Synthetic SSD", true, [@"C:\"]),
        new DiskInfo(1, "Synthetic HDD", false, [@"D:\"]),
        new DiskInfo(2, "Second synthetic SSD", true, [@"E:\"])
    ]);
    return engine;
}

static DateTimeOffset Epoch() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

static ProcessInfo GetProcess(int pid = 10) =>
    new(new ProcessIdentity(pid, Epoch().AddHours(-1)), "Writer.exe", @"C:\Apps\Writer.exe");

static T Single<T>(IReadOnlyList<T> values)
{
    Equal(1, values.Count);
    return values[0];
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, observed {actual}.");
}

static void True(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Expected the condition to hold.");
}
