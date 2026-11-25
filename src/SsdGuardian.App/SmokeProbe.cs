using System.Diagnostics;
using SsdGuardian.Core;
using SsdGuardian.Windows;

namespace SsdGuardian.App;

internal sealed record SmokeStatus(double AtSeconds, MonitorStatus Status);
internal sealed record SmokeCheckpoint(double AtSeconds, long WorkingSetBytes, long PrivateBytes, long ProfileWrittenBytes);

internal sealed record SmokeReport(DateTimeOffset StartedAt, DateTimeOffset EndedAt, double WallSeconds,
    double CpuSeconds, double CpuPercentOfSystem, double CpuPercentOfOneCore, int LogicalProcessors,
    int Samples, long FirstWorkingSetBytes, long LastWorkingSetBytes, long PeakWorkingSetBytes,
    long FirstPrivateBytes, long LastPrivateBytes, long PeakPrivateBytes, long WindowsPeakWorkingSetBytes,
    long ProfileWriteOperations, long ProfileWrittenBytes, bool Elevated, UiLanguage Language, string StatusText,
    IReadOnlyList<SmokeStatus> Statuses, IReadOnlyList<SmokeCheckpoint> Checkpoints, MonitorSnapshot Snapshot);

internal sealed class SmokeProbe : IDisposable
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(5);
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly ApplicationStore _store;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly TimeSpan _cpuAtStart;
    private readonly List<SmokeStatus> _statuses = [];
    private readonly List<SmokeCheckpoint> _checkpoints = [];
    private TimeSpan _nextCheckpoint;
    private long _firstWorkingSet;
    private long _lastWorkingSet;
    private long _peakWorkingSet;
    private long _firstPrivate;
    private long _lastPrivate;
    private long _peakPrivate;
    private int _samples;

    internal SmokeProbe(MonitorStatus status, ApplicationStore store)
    {
        _store = store;
        _cpuAtStart = _process.TotalProcessorTime;
        Sample(status);
    }

    internal TimeSpan Elapsed => _elapsed.Elapsed;

    internal void Sample(MonitorStatus status)
    {
        _process.Refresh();
        _lastWorkingSet = _process.WorkingSet64;
        _lastPrivate = _process.PrivateMemorySize64;
        if (_samples++ == 0)
        {
            _firstWorkingSet = _lastWorkingSet;
            _firstPrivate = _lastPrivate;
        }

        _peakWorkingSet = Math.Max(_peakWorkingSet, _lastWorkingSet);
        _peakPrivate = Math.Max(_peakPrivate, _lastPrivate);
        var elapsed = _elapsed.Elapsed;
        if (elapsed >= _nextCheckpoint)
        {
            AddCheckpoint(elapsed);
            _nextCheckpoint = elapsed + CheckpointInterval;
        }

        if (_statuses.Count == 0 || _statuses[^1].Status != status)
        {
            if (_statuses.Count >= ApplicationStore.RetainedEntries)
            {
                _statuses.RemoveAt(0);
            }

            _statuses.Add(new SmokeStatus(elapsed.TotalSeconds, status));
        }
    }

    internal SmokeReport Finish(MonitorSnapshot snapshot, MonitorStatus status)
    {
        Sample(status);
        var elapsed = _elapsed.Elapsed;
        AddCheckpoint(elapsed);
        var seconds = elapsed.TotalSeconds;
        var cpuSeconds = (_process.TotalProcessorTime - _cpuAtStart).TotalSeconds;
        var corePercent = 100 * cpuSeconds / seconds;
        return new SmokeReport(_startedAt, DateTimeOffset.UtcNow, seconds, cpuSeconds,
            corePercent / Environment.ProcessorCount, corePercent, Environment.ProcessorCount,
            _samples, _firstWorkingSet, _lastWorkingSet, _peakWorkingSet, _firstPrivate, _lastPrivate, _peakPrivate,
            _process.PeakWorkingSet64, _store.WriteOperations, _store.WrittenBytes, DiskWriteMonitor.IsElevated,
            UiText.Language, status.GetMessage(UiText.Language), _statuses.ToArray(), _checkpoints.ToArray(), snapshot);
    }

    private void AddCheckpoint(TimeSpan elapsed) =>
        _checkpoints.Add(new SmokeCheckpoint(elapsed.TotalSeconds, _lastWorkingSet, _lastPrivate, _store.WrittenBytes));

    public void Dispose() => _process.Dispose();
}
