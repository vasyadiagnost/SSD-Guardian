namespace SsdGuardian.Core;

public sealed class MonitorEngine
{
    private const int MaxInventoryDisks = 128;
    private const int MaxDetachedSsdDisks = 32;
    private const int MaxRetainedPathLength = 4096;
    private const int MaxExportedTerminationPathCharacters = 100_000;
    private static readonly TimeSpan SnoozeLength = TimeSpan.FromHours(1);
    private readonly object gate = new();
    private readonly SessionLedger sessionLedger = new();
    private readonly Dictionary<int, TrackedDisk> disks = [];
    private readonly List<TrackedDisk> detachedDisks = [];
    private readonly Dictionary<ProcessDisk, TrackedProcess> processes = [];
    private readonly WriteCandidates<ProcessDisk> processCandidates = new();
    private readonly Dictionary<ProcessIdentity, DateTimeOffset> snoozes = [];
    private readonly Dictionary<string, RecentTermination> terminations = new(StringComparer.OrdinalIgnoreCase);
    private GuardianOptions options;
    private WriteTotals sessionPhysical, unmappedLogical, unmappedPhysical;
    private long lostEvents, anomalyCount, untrackedProcesses, untrackedFiles;
    private long nextDiskObservationId;
    private DateTimeOffset? lastGap;

    public MonitorEngine(GuardianOptions? options = null)
    {
        this.options = options ?? new GuardianOptions();
        this.options.Validate();
    }

    public int FileSizeSampleIntervalSeconds => Volatile.Read(ref options).FileSizeSampleIntervalSeconds;

    public void SetInventory(IEnumerable<DiskInfo> inventory, DateTimeOffset? observedAt = null)
    {
        var discovered = inventory.Where(disk => disk.Number >= 0 && disk.IsConnected).DistinctBy(disk => disk.Number)
            .Take(MaxInventoryDisks).ToArray();
        var uniqueIds = discovered.Where(disk => disk.PersistentId is { Length: > 0 and <= 512 })
            .GroupBy(disk => disk.PersistentId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newDisks = discovered.ToDictionary(disk => disk.Number, disk =>
            disk.PersistentId is { } id && !uniqueIds.Contains(id) ? disk with { PersistentId = null } : disk);
        lock (gate)
        {
            foreach (var existing in disks.Keys.ToArray())
            {
                var previous = disks[existing];
                if (newDisks.TryGetValue(existing, out var replacement) && SameDisk(previous.Info, replacement))
                {
                    previous.UpdateInfo(replacement);
                    newDisks.Remove(existing);
                    continue;
                }
                disks.Remove(existing);
                if (previous.Info.IsSsd)
                {
                    previous.Info = previous.Info with { IsConnected = false };
                    detachedDisks.Add(previous);
                }
                foreach (var key in processes.Keys.Where(key => key.DiskNumber == existing).ToArray())
                    processes.Remove(key);
                processCandidates.Clear();
            }

            foreach (var disk in newDisks.Values)
            {
                var index = disk.PersistentId is null ? -1 : detachedDisks.FindIndex(item => SameDisk(item.Info, disk));
                if (index < 0)
                {
                    disks.Add(disk.Number, new TrackedDisk(disk with { ObservationId = ++nextDiskObservationId }, observedAt));
                    continue;
                }
                var resumed = detachedDisks[index];
                detachedDisks.RemoveAt(index);
                resumed.UpdateInfo(disk);
                resumed.MappingSince = observedAt;
                disks.Add(disk.Number, resumed);
            }

            if (detachedDisks.Count > MaxDetachedSsdDisks)
                detachedDisks.RemoveRange(0, detachedDisks.Count - MaxDetachedSsdDisks);
        }
    }

    private static bool SameDisk(DiskInfo before, DiskInfo after) =>
        before.IsTypeKnown == after.IsTypeKnown && before.IsSsd == after.IsSsd &&
        (before.PersistentId is not null || after.PersistentId is not null
            ? StringComparer.OrdinalIgnoreCase.Equals(before.PersistentId, after.PersistentId)
            : StringComparer.OrdinalIgnoreCase.Equals(before.Name, after.Name));

    public void UpdateOptions(GuardianOptions settings)
    {
        settings.Validate();
        lock (gate)
        {
            var resetWeights = options.TrackingIdleSeconds != settings.TrackingIdleSeconds;
            Volatile.Write(ref options, settings);
            processCandidates.Clear();
            foreach (var key in processes.OrderByDescending(pair => pair.Value.Weight.LastSlot)
                         .Skip(settings.MaxTrackedProcesses).Select(pair => pair.Key).ToArray())
                processes.Remove(key);
            foreach (var process in processes.Values)
            {
                process.FileCandidates.Clear();
                foreach (var file in process.Files.OrderByDescending(pair => pair.Value.Weight.LastSlot)
                             .Skip(settings.MaxFilesPerProcess).Select(pair => pair.Key).ToArray())
                    process.Files.Remove(file);
                if (!resetWeights)
                    continue;
                process.Weight = new RecentWriteWeight(process.Weight.LastSlot, 0, 0);
                foreach (var file in process.Files.Values)
                    file.Weight = new RecentWriteWeight(file.Weight.LastSlot, 0, 0);
            }
            TrimDates(snoozes, options.MaxTrackedProcesses, date => date);
            TrimDates(terminations, options.MaxTrackedProcesses, item => item.Timestamp);
        }
    }

    public void RecordDiskWrite(DiskWrite write)
    {
        if (write.Bytes <= 0)
            return;
        lock (gate)
        {
            if (!disks.TryGetValue(write.DiskNumber, out var disk) || !disk.CanAttribute(write.Timestamp))
            {
                unmappedPhysical = unmappedPhysical.Add(write.Bytes);
                return;
            }
            if (!disk.Info.IsSsd)
                return;
            disk.Physical.Add(WriteHistory.GetSlot(write.Timestamp), write.Bytes);
            sessionPhysical = sessionPhysical.Add(write.Bytes);
        }
    }

    public void RecordFileWrite(FileWrite write)
    {
        if (write.Bytes <= 0)
            return;
        lock (gate)
        {
            if (write.DiskNumber is not int diskNumber || !disks.TryGetValue(diskNumber, out var disk) ||
                !disk.CanAttribute(write.Timestamp))
            {
                unmappedLogical = unmappedLogical.Add(write.Bytes);
                return;
            }
            if (!disk.Info.IsSsd)
                return;

            var slot = WriteHistory.GetSlot(write.Timestamp);
            disk.Logical.Add(slot, write.Bytes);
            if (write.Process is null || write.Process.Identity.Pid <= 0 ||
                write.Process.Identity.CreatedAt == default || write.Process.Identity.CreatedAt > write.Timestamp)
            {
                disk.Unattributed.Add(slot, write.Bytes);
                sessionLedger.RecordUnidentified(write.Bytes);
                return;
            }

            sessionLedger.RecordProcess(write.Process, disk.Info, write.Timestamp, write.Bytes);
            var process = FindProcess(write.Process, diskNumber, slot, write.Bytes);
            if (process is null)
            {
                untrackedProcesses = WriteTotals.SaturatingAdd(untrackedProcesses, 1);
                return;
            }
            process.Writes.Add(slot, write.Bytes);
            if (write.Path is null || write.Path.Length == 0 || write.Path.Length > MaxRetainedPathLength)
            {
                untrackedFiles = WriteTotals.SaturatingAdd(untrackedFiles, 1);
                return;
            }

            var file = FindFile(process, write.Path, slot, write.Bytes);
            if (file is null)
            {
                untrackedFiles = WriteTotals.SaturatingAdd(untrackedFiles, 1);
                return;
            }
            file.Write(slot, write.Bytes, write.Offset, options.RecentRangeCount, options.RecentRangeAgeSeconds);
        }
    }

    public void RecordFileSize(FileSizeSample sample)
    {
        if (sample.Length < 0 || string.IsNullOrEmpty(sample.Path))
            return;
        var slot = WriteHistory.GetSlot(sample.Timestamp);
        lock (gate)
        {
            if (!disks.TryGetValue(sample.DiskNumber, out var disk) || !disk.CanAttribute(sample.Timestamp))
                return;
            foreach (var (key, process) in processes)
            {
                if (key.DiskNumber == sample.DiskNumber && process.Files.TryGetValue(sample.Path, out var file))
                    file.ObserveSize(slot, sample.Length);
            }
        }
    }

    public void MarkProcessExited(ProcessIdentity identity, DateTimeOffset exitedAt)
    {
        lock (gate)
            sessionLedger.MarkExited(identity, exitedAt);
    }

    public void ReportLostEvents(long count, DateTimeOffset timestamp)
    {
        if (count <= 0)
            return;
        lock (gate)
        {
            lostEvents = WriteTotals.SaturatingAdd(lostEvents, count);
            RecordGap(timestamp);
        }
    }

    public void ReportObservationGap(DateTimeOffset timestamp)
    {
        lock (gate)
            RecordGap(timestamp);
    }

    private void RecordGap(DateTimeOffset timestamp)
    {
        if (lastGap is null || lastGap < timestamp)
            lastGap = timestamp;
    }

    public MonitorSnapshot Snapshot(DateTimeOffset now)
    {
        var slot = WriteHistory.GetSlot(now);
        lock (gate)
        {
            var diskRows = disks.Values.Concat(detachedDisks).Where(disk => disk.Info.IsSsd)
                .OrderByDescending(disk => disk.Info.IsConnected).ThenBy(disk => disk.Info.Number)
                .Select(disk => new DiskSnapshot(disk.Info, disk.Physical.ReadWindows(slot),
                    disk.Logical.ReadWindows(slot), disk.Unattributed.ReadWindows(slot))).ToArray();
            var processRows = processes.Select(pair => new ProcessSnapshot(pair.Value.Process, pair.Key.DiskNumber,
                    pair.Value.Writes.ReadWindows(slot), GetFiles(pair.Value, slot)))
                .OrderByDescending(row => row.Logical.Last30.Bytes).ThenByDescending(row => row.Logical.Session.Bytes)
                .ToArray();
            return new MonitorSnapshot(now, diskRows, processRows, sessionPhysical, unmappedLogical, unmappedPhysical,
                lostEvents, lastGap is null || now - lastGap.Value >= TimeSpan.FromHours(1),
                anomalyCount, untrackedProcesses, untrackedFiles)
            {
                HasObservationGap = lastGap is not null,
                SessionLedger = sessionLedger.Snapshot(diskRows, processRows)
            };
        }
    }

    public IReadOnlyList<Anomaly> Evaluate(DateTimeOffset now)
    {
        var lastSlot = WriteHistory.GetSlot(now);
        lock (gate)
        {
            var options = this.options;
            if (lastGap is { } loss && now - loss < TimeSpan.FromMinutes(options.LongWindowMinutes))
                return [];

            List<Anomaly> anomalies = [];
            foreach (var (key, process) in processes)
            {
                if (process.Terminated || IsSnoozed(key.Identity, now) ||
                    process.LastAlert is { } last && now - last < TimeSpan.FromMinutes(options.NotificationCooldownMinutes))
                    continue;
                if (!disks.TryGetValue(key.DiskNumber, out var disk))
                    continue;

                var nvidiaProcess = options.EnableNvidiaSignature && IsNvidiaProcess(process.Process, options);
                var activity = InspectWindow(process, disk, lastSlot, options.FastWindowMinutes,
                    options.FastThresholdBytes, options.FastRequiredActiveFiveMinuteIntervals,
                    options.FastMinimumActivitySeconds, options, nvidiaProcess);
                var elevated = activity is not null;
                activity ??= InspectWindow(process, disk, lastSlot, options.LongWindowMinutes,
                    options.LongThresholdBytes, options.RequiredActiveFiveMinuteIntervals,
                    options.LongMinimumActivitySeconds, options, nvidiaProcess);
                if (activity is null && nvidiaProcess)
                    activity = InspectWindow(process, disk, lastSlot, options.FastWindowMinutes,
                        options.NvidiaDiagnosticThresholdBytes, options.FastRequiredActiveFiveMinuteIntervals,
                        options.FastMinimumActivitySeconds, options, nvidiaProcess, nvidiaOnly: true);
                if (activity is not { } evidence)
                    continue;

                process.LastAlert = now;
                var recurrence = process.Process.Path is { } path && terminations.TryGetValue(path, out var exit) &&
                    exit.Identity != process.Process.Identity && IsRecentTermination(exit, now) &&
                    process.Process.Identity.CreatedAt >= exit.Timestamp;
                var (englishReason, russianReason) = GetReasons(evidence, recurrence, options);
                var bytesPerSecond = (double)evidence.Logical.Bytes / (evidence.Minutes * 60);
                var files = GetFiles(process, lastSlot, evidence.PreferredFile, evidence.Minutes);
                anomalies.Add(new Anomaly(now, process.Process, key.DiskNumber, evidence.Minutes, evidence.Logical,
                    evidence.Physical, bytesPerSecond, bytesPerSecond * 86_400, files, englishReason, elevated, evidence.NvidiaMatch,
                    recurrence) { ReasonRussian = russianReason });
                anomalyCount = WriteTotals.SaturatingAdd(anomalyCount, 1);
            }
            return anomalies;
        }
    }

    public void Snooze(ProcessIdentity identity, DateTimeOffset now)
    {
        lock (gate)
        {
            snoozes[identity] = now + SnoozeLength;
            TrimDates(snoozes, options.MaxTrackedProcesses, date => date);
        }
    }

    public void MarkTerminated(ProcessIdentity identity, string? path, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var (key, process) in processes)
            {
                if (key.Identity == identity)
                    process.Terminated = true;
            }
            if (string.IsNullOrWhiteSpace(path) || path.Length > MaxRetainedPathLength)
                return;
            terminations[path] = new RecentTermination(identity, path, now);
            TrimDates(terminations, options.MaxTrackedProcesses, item => item.Timestamp);
        }
    }

    public IReadOnlyList<RecentTermination> ExportTerminations(DateTimeOffset now)
    {
        lock (gate)
        {
            var records = new List<RecentTermination>();
            var remaining = MaxExportedTerminationPathCharacters;
            foreach (var item in terminations.Values.Where(item => IsRecentTermination(item, now))
                         .OrderByDescending(item => item.Timestamp))
            {
                if (item.Path.Length > remaining)
                    break;
                records.Add(item);
                remaining -= item.Path.Length;
            }
            return records;
        }
    }

    public void RestoreTerminations(IEnumerable<RecentTermination> history, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var item in history)
            {
                if (item?.Identity is not { Pid: > 0 } identity || identity.CreatedAt == default ||
                    identity.CreatedAt > item.Timestamp || string.IsNullOrWhiteSpace(item.Path) ||
                    item.Path.Length > MaxRetainedPathLength || !IsRecentTermination(item, now))
                    continue;
                if (!terminations.TryGetValue(item.Path, out var existing) || existing.Timestamp < item.Timestamp)
                    terminations[item.Path] = item;
                TrimDates(terminations, options.MaxTrackedProcesses, record => record.Timestamp);
            }
        }
    }

    private bool IsRecentTermination(RecentTermination record, DateTimeOffset now) =>
        record.Timestamp <= now && now - record.Timestamp <= TimeSpan.FromDays(options.RecurrenceRetentionDays);

    private int RankingWindowSlots => options.TrackingIdleSeconds / WriteHistory.SlotSeconds;

    private TrackedProcess? FindProcess(ProcessInfo info, int diskNumber, long slot, long bytes)
    {
        var key = new ProcessDisk(info.Identity, diskNumber);
        var window = RankingWindowSlots;
        if (processes.TryGetValue(key, out var result))
        {
            if (result.Process.Path is null && info.Path is not null)
                result.Process = info;
            result.Weight = result.Weight.Add(slot, bytes, window);
            return result;
        }
        var weight = processCandidates.Observe(key, slot, bytes, window, options.MaxTrackedProcesses * 2);
        if (processes.Count >= options.MaxTrackedProcesses)
        {
            var weakest = WriteRanking.FindWeakest(processes, slot, window, static item => item.Weight);
            if (weight.Read(slot, window) <= weakest.Weight * options.TrackingPromotionRatio)
                return null;
            processes.Remove(weakest.Key);
        }
        processCandidates.Remove(key);
        result = new TrackedProcess(info) { Weight = weight };
        processes.Add(key, result);
        return result;
    }

    private TrackedFile? FindFile(TrackedProcess process, string path, long slot, long bytes)
    {
        var window = RankingWindowSlots;
        if (process.Files.TryGetValue(path, out var result))
        {
            result.Weight = result.Weight.Add(slot, bytes, window);
            return result;
        }
        var weight = process.FileCandidates.Observe(path, slot, bytes, window, options.MaxFilesPerProcess * 2);
        if (process.Files.Count >= options.MaxFilesPerProcess)
        {
            var weakest = WriteRanking.FindWeakest(process.Files, slot, window, static item => item.Weight);
            if (weight.Read(slot, window) <= weakest.Weight * options.TrackingPromotionRatio)
                return null;
            process.Files.Remove(weakest.Key);
        }
        process.FileCandidates.Remove(path);
        result = new TrackedFile(path) { Weight = weight };
        process.Files.Add(path, result);
        return result;
    }

    private bool IsSnoozed(ProcessIdentity identity, DateTimeOffset now) =>
        snoozes.TryGetValue(identity, out var until) && now < until;

    private static FileSnapshot[] GetFiles(TrackedProcess process, long slot, string? preferredPath = null, int? minutes = null) =>
        process.Files.Values.Select(file => new FileSnapshot(file.Path, file.Writes.ReadWindows(slot))
            {
                Window = minutes is { } window ? file.Writes.ReadPeriod(slot - window * WriteHistory.SlotsPerMinute + 1, slot).Totals : null
            })
            .OrderByDescending(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, preferredPath))
            .ThenByDescending(file => file.Window?.Bytes ?? file.Logical.Last30.Bytes)
            .ThenByDescending(file => file.Logical.Session.Bytes).ToArray();

    private static WindowEvidence? InspectWindow(
        TrackedProcess process, TrackedDisk disk, long lastSlot, int minutes, long thresholdBytes,
        int minimumActiveIntervals, int minimumActiveSeconds, GuardianOptions options, bool nvidiaProcess,
        bool nvidiaOnly = false)
    {
        var firstSlot = lastSlot - minutes * WriteHistory.SlotsPerMinute + 1;
        var logical = process.Writes.ReadPeriod(firstSlot, lastSlot);
        if (logical.Totals.Bytes < thresholdBytes || logical.ActiveSeconds < minimumActiveSeconds)
            return null;

        var physical = disk.Physical.ReadPeriod(firstSlot, lastSlot).Totals;
        if (!nvidiaOnly && physical.Bytes < logical.Totals.Bytes * options.MinimumPhysicalToLogicalRatio)
            return null;

        var intervalCount = minutes / options.ActivityIntervalMinutes;
        var intervalSlots = options.ActivityIntervalMinutes * WriteHistory.SlotsPerMinute;
        Span<long> intervalWrites = stackalloc long[intervalCount];
        intervalWrites.Clear();
        long firstActiveSlot = long.MaxValue, lastActiveSlot = 0, evidenceBytes = 0, strongestBytes = 0;
        string? preferredFile = null;
        int evidenceFiles = 0;
        bool repeated = false, stable = false, nvidiaMatch = false;
        foreach (var file in process.Files.Values)
        {
            var nvidiaFile = nvidiaProcess && GetFileName(file.Path).Equals(options.NvidiaFileName, StringComparison.OrdinalIgnoreCase);
            if (nvidiaOnly && !nvidiaFile)
                continue;

            var writes = file.Writes.ReadPeriod(firstSlot, lastSlot);
            if (writes.Totals.Operations < 2)
                continue;

            var sizes = file.GetSizeEvidence(firstSlot, lastSlot);
            var observedSize = sizes.HasDuration(options.MinimumSizeObservationSeconds);
            if (observedSize && sizes.Maximum - sizes.Minimum > writes.Totals.Bytes * options.MaximumFileGrowthShare)
                continue;
            var recentSize = sizes.LastSlot >= writes.LastActiveSlot -
                options.MinimumSizeObservationSeconds / WriteHistory.SlotSeconds;
            var stableSmallFile = observedSize && recentSize && sizes.Maximum > 0 && sizes.Maximum <= options.SmallFileMaximumBytes &&
                writes.Totals.Bytes >= sizes.Maximum * options.MinimumOverwriteMultiple &&
                sizes.Maximum - sizes.Minimum <= sizes.Maximum * options.MaximumFileGrowthShare;
            var repeatedRanges = writes.RepeatedBytes >= writes.Totals.Bytes * options.MinimumRepeatedRangeShare;
            if (!stableSmallFile && !repeatedRanges)
                continue;

            evidenceFiles++;
            evidenceBytes = WriteTotals.SaturatingAdd(evidenceBytes, writes.Totals.Bytes);
            firstActiveSlot = Math.Min(firstActiveSlot, writes.FirstActiveSlot);
            lastActiveSlot = Math.Max(lastActiveSlot, writes.LastActiveSlot);
            repeated |= repeatedRanges;
            stable |= stableSmallFile;
            nvidiaMatch |= nvidiaFile;
            if (writes.Totals.Bytes > strongestBytes)
            {
                strongestBytes = writes.Totals.Bytes;
                preferredFile = file.Path;
            }
            for (var interval = 0; interval < intervalCount; interval++)
            {
                var from = firstSlot + interval * intervalSlots;
                var to = from + intervalSlots - 1;
                var fileInterval = file.Writes.ReadPeriod(from, to);
                intervalWrites[interval] = WriteTotals.SaturatingAdd(intervalWrites[interval], fileInterval.Totals.Bytes);
            }
        }

        if (preferredFile is null || (lastActiveSlot - firstActiveSlot) * WriteHistory.SlotSeconds < minimumActiveSeconds)
            return null;
        if (nvidiaOnly
            ? evidenceBytes < thresholdBytes || physical.Bytes < evidenceBytes * options.MinimumPhysicalToLogicalRatio
            : evidenceBytes < logical.Totals.Bytes * options.MinimumDominantFileShare)
            return null;

        int active = 0, correlated = 0;
        for (var interval = 0; interval < intervalCount; interval++)
        {
            if (intervalWrites[interval] < options.MinimumActiveIntervalBytes)
                continue;
            active++;
            var from = firstSlot + interval * intervalSlots;
            if (disk.Physical.ReadPeriod(from, from + intervalSlots - 1).Totals.Bytes >= options.MinimumPhysicalIntervalBytes)
                correlated++;
        }

        return active >= minimumActiveIntervals && correlated >= options.MinimumCorrelatedIntervals
            ? new WindowEvidence(preferredFile, logical.Totals, physical, minutes, active, intervalCount, correlated,
                evidenceFiles, stable, repeated, nvidiaMatch, nvidiaOnly)
            : null;
    }

    private static (string English, string Russian) GetReasons(
        WindowEvidence evidence, bool recurrence, GuardianOptions options)
    {
        var english = $"Active writes in {evidence.ActiveIntervals} of {evidence.IntervalCount} {options.ActivityIntervalMinutes}-minute intervals.";
        var russian = $"Активная запись в {evidence.ActiveIntervals} из {evidence.IntervalCount} интервалов по {options.ActivityIntervalMinutes} мин.";
        if (evidence.EvidenceFiles > 1)
        {
            english += $" Rewrite evidence spans {evidence.EvidenceFiles} files.";
            russian += $" Признаки перезаписи обнаружены в нескольких файлах ({evidence.EvidenceFiles}).";
        }
        if (evidence.RepeatedRanges)
        {
            english += " The same file ranges were overwritten repeatedly.";
            russian += " Повторная запись тех же диапазонов файла.";
        }
        if (evidence.StableSmallFile)
        {
            english += " The bytes written were many times the size of a small file that barely grew.";
            russian += " Записанный объём многократно превышает размер небольшого файла, который почти не менялся.";
        }
        english += $" Physical disk writes were observed in {evidence.CorrelatedIntervals} of the same intervals.";
        russian += $" Физическая запись на диске наблюдалась в {evidence.CorrelatedIntervals} тех же интервалах.";
        if (evidence.NvidiaMatch)
        {
            english += evidence.NvidiaOnly ? " The dedicated NVIDIA rewrite threshold was reached." : " The NVIDIA rewrite pattern matched.";
            russian += evidence.NvidiaOnly ? " Достигнут отдельный порог перезаписи NVIDIA." : " Обнаружены признаки перезаписи NVIDIA.";
            english += $" ({options.NvidiaProcessName} and {options.NvidiaFileName})";
            russian += $" ({options.NvidiaProcessName} и {options.NvidiaFileName})";
        }
        if (recurrence)
        {
            english += " The anomaly recurred after the process restarted.";
            russian += " Аномалия повторилась после перезапуска процесса.";
        }
        return (english, russian);
    }

    private static bool IsNvidiaProcess(ProcessInfo process, GuardianOptions options)
    {
        var imageName = GetFileName(process.Path ?? process.Name);
        return imageName.Equals(options.NvidiaProcessName, StringComparison.OrdinalIgnoreCase) ||
            (process.Path is null && options.NvidiaProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
             imageName.Equals(options.NvidiaProcessName[..^4], StringComparison.OrdinalIgnoreCase));
    }

    private static string GetFileName(string path) => path[(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1)..];

    private static void TrimDates<TKey, TValue>(
        Dictionary<TKey, TValue> dictionary, int capacity, Func<TValue, DateTimeOffset> getDate) where TKey : notnull
    {
        if (dictionary.Count <= capacity)
            return;
        foreach (var key in dictionary.OrderByDescending(pair => getDate(pair.Value)).Skip(capacity)
                     .Select(pair => pair.Key).ToArray())
            dictionary.Remove(key);
    }

    private sealed class TrackedDisk(DiskInfo info, DateTimeOffset? observedAt)
    {
        internal DiskInfo Info { get; set; } = info;
        internal DateTimeOffset? MappingSince { get; set; } = observedAt;
        internal WriteHistory Physical { get; } = new();
        internal WriteHistory Logical { get; } = new();
        internal WriteHistory Unattributed { get; } = new();

        internal void UpdateInfo(DiskInfo replacement) => Info = replacement with { ObservationId = Info.ObservationId };

        internal bool CanAttribute(DateTimeOffset timestamp) => Info.IsTypeKnown &&
            (MappingSince is null || timestamp >= MappingSince);
    }

    private sealed class TrackedProcess(ProcessInfo process)
    {
        internal ProcessInfo Process { get; set; } = process;
        internal WriteHistory Writes { get; } = new();
        internal Dictionary<string, TrackedFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal WriteCandidates<string> FileCandidates { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal RecentWriteWeight Weight { get; set; }
        internal DateTimeOffset? LastAlert { get; set; }
        internal bool Terminated { get; set; }
    }

    private readonly record struct ProcessDisk(ProcessIdentity Identity, int DiskNumber);

    private readonly record struct WindowEvidence(
        string PreferredFile, WriteTotals Logical, WriteTotals Physical, int Minutes, int ActiveIntervals,
        int IntervalCount, int CorrelatedIntervals, int EvidenceFiles, bool StableSmallFile, bool RepeatedRanges,
        bool NvidiaMatch, bool NvidiaOnly);
}
