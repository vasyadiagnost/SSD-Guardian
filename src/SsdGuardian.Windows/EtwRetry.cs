namespace SsdGuardian.Windows;

internal static class EtwRetry
{
    internal static void Run(Action attempt, Action<Exception?> failed, Func<TimeSpan, bool> waitForStop,
        CancellationToken cancellation, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        int delaySeconds = 1;
        while (!cancellation.IsCancellationRequested)
        {
            long started = time.GetTimestamp();
            Exception? failure = null;
            try
            {
                attempt();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (cancellation.IsCancellationRequested)
                return;
            failed(failure);

            if (time.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                delaySeconds = 1;
            if (waitForStop(TimeSpan.FromSeconds(delaySeconds)))
                return;
            delaySeconds = Math.Min(delaySeconds * 2, 30);
        }
    }
}
