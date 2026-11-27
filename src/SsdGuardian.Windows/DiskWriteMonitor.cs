using System.ComponentModel;
using System.Globalization;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using SsdGuardian.Core;

namespace SsdGuardian.Windows;

public enum MonitorState { Starting, Running, Degraded, Unavailable, Stopped }

public sealed record MonitorStatus(MonitorState State, string Message, long EventsLost = 0)
{
    public string? MessageRussian { get; init; }

    public string GetMessage(UiLanguage language) =>
        language == UiLanguage.Russian ? MessageRussian ?? Message : Message;
}

public sealed class DiskWriteMonitor : IDisposable
{
    internal const string ProductionSessionName = "SSD Guardian Kernel Monitor";
    internal static readonly Guid ProductionSessionId = new("4ac856b1-3fca-423b-90af-23d0224e8d76");
    private readonly MonitorEngine engine;
    private readonly Action<MonitorStatus> statusChanged;
    private readonly string sessionName;
    private readonly Guid sessionId;
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource stopping = new();
    private StorageInventory? inventory;
    private OwnedEtwSession? session;
    private Task? traceWorker;
    private bool disposed;
    private long losses;
    private int lossEpoch;
    private int observationGap;
    private bool wasConnected;

    public DiskWriteMonitor(MonitorEngine engine, Action<MonitorStatus> statusChanged)
        : this(engine, statusChanged, ProductionSessionName, ProductionSessionId) { }

    internal DiskWriteMonitor(MonitorEngine engine, Action<MonitorStatus> statusChanged, string sessionName, Guid sessionId)
    {
        this.engine = engine;
        this.statusChanged = statusChanged;
        this.sessionName = sessionName;
        this.sessionId = sessionId;
    }

    public IReadOnlyList<DiskInfo> Disks => Volatile.Read(ref inventory)?.Disks ?? [];

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static MonitorStatus? GetPrivilegeFailure(bool elevated) => elevated ? null : new(
        MonitorState.Unavailable, "Run SSD Guardian as administrator to receive kernel events.")
    {
        MessageRussian = "Для получения событий ядра запустите SSD Guardian от имени администратора."
    };

    public void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (traceWorker is not null)
                return;

            traceWorker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (lifecycle)
        {
            if (disposed)
                return;

            disposed = true;
            stopping.Cancel();
            worker = traceWorker;
            session?.Dispose();
        }

        if (worker is null || Task.CurrentId != worker.Id && worker.Wait(TimeSpan.FromSeconds(4)))
            stopping.Dispose();
        else
            _ = worker.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
    }

    private void Run()
    {
        CancellationToken cancellation = stopping.Token;
        try
        {
            PublishStatus(MonitorState.Starting, "Detecting SSDs and starting the ETW session.",
                "Определение SSD и запуск сеанса ETW.");
            var privilege = GetPrivilegeFailure(IsElevated);
            if (privilege is not null)
            {
                statusChanged(privilege);
                return;
            }

            using var ownership = new Mutex(false, @"Global\" + sessionName);
            while (!TryAcquire(ownership))
            {
                PublishStatus(MonitorState.Unavailable, "Another SSD Guardian instance is monitoring. Waiting for it to exit.",
                    "Мониторинг ведёт другой экземпляр SSD Guardian. Ожидание его завершения.");
                if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    return;
            }

            try
            {
                using var processes = new ProcessCache(engine);
                EtwRetry.Run(() => RunSession(processes, cancellation), ReportRetry, cancellation.WaitHandle.WaitOne, cancellation);
            }
            finally
            {
                ownership.ReleaseMutex();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                string error = FormatError(ex);
                PublishStatus(MonitorState.Unavailable, $"Monitoring is unavailable ({error}).",
                    $"Мониторинг недоступен ({error}).", Interlocked.Read(ref losses));
            }
        }
        finally
        {
            if (cancellation.IsCancellationRequested)
                PublishStatus(MonitorState.Stopped, "Monitoring stopped.", "Мониторинг остановлен.", Interlocked.Read(ref losses));
        }
    }

    private static bool TryAcquire(Mutex ownership)
    {
        try { return ownership.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private void RunSession(ProcessCache processes, CancellationToken cancellation)
    {
        RefreshInventory();
        OwnedEtwSession active;
        lock (lifecycle)
        {
            cancellation.ThrowIfCancellationRequested();
            active = OwnedEtwSession.Start(sessionName, sessionId);
            session = active;
        }

        using var background = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var samples = Channel.CreateBounded<SampleRequest>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        Task maintenance = Task.CompletedTask;
        Task sampling = Task.CompletedTask;
        try
        {
            using var source = new ETWTraceEventSource(sessionName, TraceEventSourceType.Session);
            var parser = new KernelTraceEventParser(source, KernelTraceEventParser.ParserTrackingOptions.None)
            {
                // Defer drive mapping until a write actually references the name.
                KernelPathToUserPathMapper = static path => path
            };
            var correlation = new TraceCorrelation(this, parser, samples.Writer, processes);
            if (wasConnected)
                ReportObservationGap();
            wasConnected = true;
            maintenance = MaintainAsync(active, correlation, background.Token);
            sampling = SampleFilesAsync(samples.Reader, background.Token);
            PublishCoverage();
            source.Process();
        }
        finally
        {
            background.Cancel();
            samples.Writer.TryComplete();
            lock (lifecycle)
            {
                active.Dispose();
                session = null;
            }

            try
            {
                Task.WhenAll(maintenance, sampling).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (background.IsCancellationRequested) { }
        }
    }

    private void ReportRetry(Exception? exception)
    {
        if (wasConnected)
            ReportObservationGap();
        long lost = Interlocked.Read(ref losses);
        if (exception is Win32Exception { NativeErrorCode: 183 })
            PublishStatus(MonitorState.Unavailable,
                "The ETW session name is occupied by an unverified logger. Waiting for it to close.",
                "Имя сеанса ETW занято неподтверждённым журналом. Ожидание его завершения.", lost);
        else if (exception is not null)
        {
            string error = FormatError(exception);
            PublishStatus(MonitorState.Unavailable, $"Monitoring is unavailable ({error}). Retrying automatically.",
                $"Мониторинг недоступен ({error}). Будет выполнена автоматическая попытка восстановления.", lost);
        }
        else
            PublishStatus(MonitorState.Unavailable, "The ETW stream stopped. Retrying automatically.",
                "Поток событий ETW остановлен. Будет выполнена автоматическая попытка восстановления.", lost);
    }

    private void ReportObservationGap()
    {
        engine.ReportObservationGap(DateTimeOffset.UtcNow);
        Interlocked.Increment(ref lossEpoch);
        Interlocked.Exchange(ref observationGap, 1);
    }

    private static string FormatError(Exception exception) => exception is Win32Exception native
        ? $"Win32 {native.NativeErrorCode}"
        : $"{exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}";

    private void RefreshInventory()
    {
        var current = StorageInventory.Discover();
        engine.SetInventory(current.Disks, DateTimeOffset.UtcNow);
        Volatile.Write(ref inventory, current);
    }

    private void PublishCoverage()
    {
        long lost = Interlocked.Read(ref losses);
        if (lost > 0)
            PublishStatus(MonitorState.Degraded,
                $"ETW events or buffers were lost, or the write queue filled up. At least {lost.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} missed.",
                $"ETW потеряла события или буферы, либо очередь записи переполнена. Пропусков не менее {lost.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))}.",
                lost);
        else if (Volatile.Read(ref observationGap) != 0)
            PublishStatus(MonitorState.Degraded, "Monitoring resumed. The session totals are incomplete after an interruption.",
                "Мониторинг возобновлён. Итоги сеанса неполны из-за перерыва.");
        else if (!Disks.Any(disk => disk.IsSsd))
            PublishStatus(MonitorState.Degraded, "Windows found no drives it could confirm as SSDs.",
                "Windows не обнаружила накопителей с подтверждённым типом SSD.");
        else
            PublishStatus(MonitorState.Running, "Monitoring SSD writes.", "Мониторинг записи на SSD работает.");
    }

    private void PublishStatus(MonitorState state, string english, string russian, long eventsLost = 0) =>
        statusChanged(new MonitorStatus(state, english, eventsLost) { MessageRussian = russian });

    private async Task MaintainAsync(OwnedEtwSession active, TraceCorrelation correlation, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        long previousLosses = 0;
        int checks = 0;
        bool lossQueryFailed = false;
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
        {
            try
            {
                (long eventCount, long bufferCount) = active.GetLosses();
                // A lost real-time buffer represents at least one missed event. Counters can overlap.
                long total = Math.Max(eventCount, bufferCount) + correlation.OverflowCount;
                if (total > previousLosses)
                {
                    Interlocked.Increment(ref lossEpoch);
                    Interlocked.Add(ref losses, total - previousLosses);
                    engine.ReportLostEvents(total - previousLosses, DateTimeOffset.UtcNow);
                    previousLosses = total;
                    PublishCoverage();
                }
                if (lossQueryFailed)
                {
                    ReportObservationGap();
                    PublishCoverage();
                }

                lossQueryFailed = false;
                if (++checks == 12)
                {
                    correlation.SweepProcesses(DateTimeOffset.UtcNow);
                    bool hadSsd = Disks.Any(disk => disk.IsSsd);
                    RefreshInventory();
                    if (hadSsd != Disks.Any(disk => disk.IsSsd))
                        PublishCoverage();
                    checks = 0;
                }
            }
            catch (Win32Exception ex)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    ReportObservationGap();
                    if (!lossQueryFailed)
                        PublishStatus(MonitorState.Degraded,
                            $"ETW loss counters are unavailable (Win32 {ex.NativeErrorCode}).",
                            $"Счётчики потерь ETW недоступны (Win32 {ex.NativeErrorCode}).", Interlocked.Read(ref losses));
                }
                lossQueryFailed = true;
            }
        }
    }

    private async Task SampleFilesAsync(ChannelReader<SampleRequest> samples, CancellationToken cancellation)
    {
        await foreach (var request in samples.ReadAllAsync(cancellation).ConfigureAwait(false))
        {
            try
            {
                // Metadata only. All requests have first been resolved to a local SSD volume.
                if (Volatile.Read(ref inventory)?.Version == request.StorageVersion)
                {
                    var file = new FileInfo(request.Path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                        engine.RecordFileSize(new(DateTimeOffset.UtcNow, request.Disk, request.Path, file.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }

            await Task.Delay(65, cancellation).ConfigureAwait(false);
        }
    }

    private readonly record struct SampleRequest(int Disk, string Path, int StorageVersion);

    private sealed class TraceCorrelation
    {
        private readonly DiskWriteMonitor monitor;
        private readonly ChannelWriter<SampleRequest> samples;
        private readonly ProcessCache processes;
        private readonly BoundedMap<ulong, NamedFile> keys = new(8192);
        private readonly BoundedMap<ulong, NamedFile> objects = new(4096);
        private readonly BoundedMap<int, ThreadOwner> threads = new(8192);
        private readonly BoundedMap<ulong, PendingWrite> writes = new(8192);
        private readonly BoundedMap<(int Disk, string Path), DateTimeOffset> sampled = new(1024);
        private long overflowCount;
        private int epoch;

        public TraceCorrelation(DiskWriteMonitor monitor, KernelTraceEventParser parser, ChannelWriter<SampleRequest> samples,
            ProcessCache processes)
        {
            this.monitor = monitor;
            this.samples = samples;
            this.processes = processes;
            parser.DiskIOWrite += OnDiskWrite;
            parser.FileIOName += OnFileName;
            parser.FileIOFileCreate += OnFileName;
            parser.FileIOFileRundown += OnFileName;
            parser.FileIOFileDelete += data => keys.TryRemove(data.FileKey, out _);
            parser.FileIOCreate += data => RememberFile(objects, data.FileObject, data.FileName, data.TimeStamp);
            parser.FileIOClose += data => objects.TryRemove(data.FileObject, out _);
            parser.FileIOWrite += OnFileWrite;
            parser.FileIOOperationEnd += OnFileOperationEnd;
            parser.ProcessStart += data => processes.OnStart(data.ProcessID);
            parser.ProcessStop += data => processes.OnStop(data.ProcessID, data.TimeStamp);
            parser.ThreadStartGroup += OnThreadStart;
            parser.ThreadStop += OnThreadStop;
        }

        public long OverflowCount => Interlocked.Read(ref overflowCount);

        public void SweepProcesses(DateTimeOffset now) => processes.Sweep(now);

        private void OnDiskWrite(DiskIOTraceData data)
        {
            long bytes = unchecked((uint)data.TransferSize);
            if (bytes > 0)
                monitor.engine.RecordDiskWrite(new(data.TimeStamp, data.DiskNumber, bytes));
        }

        private void OnFileName(FileIONameTraceData data) => RememberFile(keys, data.FileKey, data.FileName, data.TimeStamp);

        private void RememberFile(BoundedMap<ulong, NamedFile> map, ulong key, string path, DateTimeOffset time)
        {
            CheckLossEpoch();
            if (key != 0 && path.Length is > 0 and <= 2048)
                map.Set(key, new(time, path));
        }

        private void OnThreadStart(ThreadTraceData data)
        {
            CheckLossEpoch();
            if (data.ThreadID > 0 && data.ProcessID > 0)
                threads.Set(data.ThreadID, new(data.TimeStamp, data.ProcessID));
        }

        private void OnThreadStop(ThreadTraceData data)
        {
            if (threads.TryGetValue(data.ThreadID, out var known) && known.Timestamp <= data.TimeStamp && known.Pid == data.ProcessID)
                threads.TryRemove(data.ThreadID, out _);
        }

        private void OnFileWrite(FileIOReadWriteTraceData data)
        {
            CheckLossEpoch();
            // Flushes of cached writes may carry the original PID. Physical disk events account for
            // those transfers. The per-process logical counter counts the submitted file write once.
            if (data.IrpPtr == 0 || data.IoSize <= 0 || ((IrpFlags)data.IoFlags & IrpFlags.PagingIo) != 0 ||
                Volatile.Read(ref monitor.inventory) is not { } storage)
                return;

            DateTimeOffset timestamp = data.TimeStamp;
            var location = ResolveFile(objects, data.FileObject, timestamp, storage) ??
                ResolveFile(keys, data.FileKey, timestamp, storage) ?? default;
            if (location.IsNetwork || location.Disk is int disk && storage.IsConfirmedNonSsd(disk))
                return;

            int pid = data.ProcessID;
            if (pid <= 0 && threads.TryGetValue(data.ThreadID, out var owner) && owner.Timestamp <= timestamp)
                pid = owner.Pid;
            var process = processes.Find(pid, timestamp);

            // A pointer can be reused. If an earlier start remains unmatched, omit both candidates.
            if (writes.TryGetValue(data.IrpPtr, out _))
            {
                writes.TryRemove(data.IrpPtr, out _);
                return;
            }

            long? offset = data.Offset >= 0 ? data.Offset : null;
            if (writes.Set(data.IrpPtr, new(timestamp, location.Disk, process, location.Path, data.IoSize, offset, storage.Version)))
                Interlocked.Increment(ref overflowCount);
        }

        private static StorageInventory.MappedFile? ResolveFile(BoundedMap<ulong, NamedFile> names, ulong key,
            DateTimeOffset timestamp, StorageInventory storage)
        {
            if (!names.TryGetValue(key, out var file) || file.Timestamp > timestamp)
                return null;
            if (file.StorageVersion == storage.Version)
                return file.Location;

            var location = storage.MapPath(file.Path);
            names.Update(key, file with { StorageVersion = storage.Version, Location = location });
            return location;
        }

        private void OnFileOperationEnd(FileIOOpEndTraceData data)
        {
            CheckLossEpoch();
            if (!writes.TryRemove(data.IrpPtr, out var pending) || data.NtStatus < 0 || data.ExtraInfo == 0 ||
                data.ExtraInfo > (ulong)pending.RequestSize)
                return;

            DateTimeOffset timestamp = data.TimeStamp;
            if (timestamp < pending.Timestamp || timestamp - pending.Timestamp > TimeSpan.FromMinutes(2))
                return;

            var storage = Volatile.Read(ref monitor.inventory);
            int? mappedDisk = storage?.Version == pending.StorageVersion ? pending.Disk : null;
            monitor.engine.RecordFileWrite(new(timestamp, mappedDisk, pending.Process?.Info, pending.Path,
                (long)data.ExtraInfo, pending.Offset));
            if (pending.Process is { ExitedAt: { } exitedAt } process)
                monitor.engine.MarkProcessExited(process.Info.Identity, exitedAt);
            if (mappedDisk is int disk && pending.Path is { } path && storage?.IsSsd(disk) == true &&
                (!sampled.TryGetValue((disk, path), out var last) ||
                    timestamp - last >= TimeSpan.FromSeconds(monitor.engine.FileSizeSampleIntervalSeconds)) &&
                samples.TryWrite(new(disk, path, pending.StorageVersion)))
                sampled.Set((disk, path), timestamp);
        }

        private void CheckLossEpoch()
        {
            int currentEpoch = Volatile.Read(ref monitor.lossEpoch);
            if (currentEpoch == epoch)
                return;

            keys.Clear();
            objects.Clear();
            threads.Clear();
            writes.Clear();
            epoch = currentEpoch;
        }

        private readonly record struct NamedFile(DateTimeOffset Timestamp, string Path,
            int StorageVersion = 0, StorageInventory.MappedFile Location = default);
        private readonly record struct ThreadOwner(DateTimeOffset Timestamp, int Pid);
        private readonly record struct PendingWrite(DateTimeOffset Timestamp, int? Disk, ProcessCache.ProcessLifetime? Process,
            string? Path, int RequestSize, long? Offset, int StorageVersion);
    }
}
