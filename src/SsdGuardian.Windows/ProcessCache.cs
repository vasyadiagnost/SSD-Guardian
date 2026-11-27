using Microsoft.Win32.SafeHandles;
using SsdGuardian.Core;

namespace SsdGuardian.Windows;

internal sealed class ProcessCache : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private readonly MonitorEngine engine;
    private readonly BoundedMap<int, Lookup> lookups;

    internal ProcessCache(MonitorEngine engine)
    {
        this.engine = engine;
        lookups = new(1024, Release);
    }

    internal ProcessLifetime? Find(int pid, DateTimeOffset timestamp)
    {
        if (pid <= 0)
            return null;

        lock (lookups)
        {
            if (lookups.TryGetValue(pid, out var cached))
            {
                if (cached.Process is { } previous)
                    return previous.Info.Identity.CreatedAt <= timestamp ? previous : null;
                if (timestamp - cached.Timestamp < RetryInterval)
                    return null;
            }

            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize,
                false, pid);
            bool canWait = !handle.IsInvalid;
            if (!canWait)
            {
                handle.Dispose();
                handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
            }

            if (handle.IsInvalid || !NativeMethods.TryReadRunningCreation(handle, canWait, out var creation))
            {
                handle.Dispose();
                lookups.Set(pid, new(timestamp, null));
                return null;
            }

            string? path = NativeMethods.ReadImagePath(handle);
            string name = path is null ? $"PID {pid}" : Path.GetFileName(path);
            var process = new ProcessLifetime(new(new(pid, creation), name, path), handle, canWait);
            // The retained handle identifies this process and pins the PID against reuse.
            lookups.Set(pid, new(timestamp, process));
            return creation <= timestamp ? process : null;
        }
    }

    internal void OnStart(int pid)
    {
        lock (lookups)
        {
            if (lookups.TryGetValue(pid, out var known) && known.Process is null)
                lookups.TryRemove(pid, out _);
        }
    }

    internal void OnStop(int pid, DateTimeOffset timestamp)
    {
        lock (lookups)
        {
            if (lookups.TryGetValue(pid, out var known) && known.Process is { } process &&
                process.Info.Identity.CreatedAt <= timestamp && process.TryMarkExited(engine))
            {
                lookups.TryRemove(pid, out _);
                process.Dispose();
            }
        }
    }

    internal void Sweep(DateTimeOffset now)
    {
        lock (lookups)
            lookups.RemoveWhere(lookup => lookup.Process is { } process
                ? process.TryMarkExited(engine) : now - lookup.Timestamp >= RetryInterval);
    }

    public void Dispose()
    {
        lock (lookups)
            lookups.Clear();
    }

    private void Release(Lookup lookup)
    {
        if (lookup.Process is { } process)
        {
            process.TryMarkExited(engine);
            process.Dispose();
        }
    }

    private readonly record struct Lookup(DateTimeOffset Timestamp, ProcessLifetime? Process);

    // Writes retain this reference so a completion after process exit keeps its attribution.
    internal sealed class ProcessLifetime(ProcessInfo info, SafeProcessHandle handle, bool canWait) : IDisposable
    {
        private long exitUtcTicks;

        internal ProcessInfo Info { get; } = info;
        internal DateTimeOffset? ExitedAt
        {
            get
            {
                long ticks = Volatile.Read(ref exitUtcTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        internal bool TryMarkExited(MonitorEngine engine)
        {
            if (ExitedAt is not null)
                return true;
            if (NativeMethods.ReadExitTime(handle, canWait, Info.Identity.CreatedAt) is not { } exit)
                return false;

            Volatile.Write(ref exitUtcTicks, exit.UtcTicks);
            engine.MarkProcessExited(Info.Identity, exit);
            return true;
        }

        public void Dispose() => handle.Dispose();
    }
}
