namespace SsdGuardian.Core;

public sealed record GuardianOptions
{
    public long LongThresholdBytes { get; init; } = 1_000_000_000;
    public long FastThresholdBytes { get; init; } = 5_000_000_000;
    public int RefreshSeconds { get; init; } = 5;
    public int LongWindowMinutes { get; init; } = 30;
    public int FastWindowMinutes { get; init; } = 10;
    public int ActivityIntervalMinutes { get; init; } = 5;
    public int RequiredActiveFiveMinuteIntervals { get; init; } = 5;
    public int FastRequiredActiveFiveMinuteIntervals { get; init; } = 2;
    public int LongMinimumActivitySeconds { get; init; } = 1200;
    public int FastMinimumActivitySeconds { get; init; } = 120;
    public long MinimumActiveIntervalBytes { get; init; } = 1L << 20;
    public long MinimumPhysicalIntervalBytes { get; init; } = 128L << 10;
    public int MinimumCorrelatedIntervals { get; init; } = 2;
    public double MinimumPhysicalToLogicalRatio { get; init; } = 0.02;
    public double MinimumDominantFileShare { get; init; } = 0.5;
    public double MinimumRepeatedRangeShare { get; init; } = 0.5;
    public long SmallFileMaximumBytes { get; init; } = 64L << 20;
    public double MinimumOverwriteMultiple { get; init; } = 16;
    public double MaximumFileGrowthShare { get; init; } = 0.1;
    public int MinimumSizeObservationSeconds { get; init; } = 120;
    public int FileSizeSampleIntervalSeconds { get; init; } = 20;
    public int RecentRangeCount { get; init; } = 8;
    public int RecentRangeAgeSeconds { get; init; } = 60;
    public int NotificationCooldownMinutes { get; init; } = 30;
    public bool EnableNvidiaSignature { get; init; } = true;
    public string NvidiaProcessName { get; init; } = "NVDisplay.Container.exe";
    public string NvidiaFileName { get; init; } = "nvlog.nvlgstg";
    public long NvidiaDiagnosticThresholdBytes { get; init; } = 16L << 20;
    public int RecurrenceRetentionDays { get; init; } = 7;
    public int MaxTrackedProcesses { get; init; } = 96;
    public int MaxFilesPerProcess { get; init; } = 8;
    public int TrackingIdleSeconds { get; init; } = 60;
    public double TrackingPromotionRatio { get; init; } = 1.25;

    public void Validate(UiLanguage language = UiLanguage.English)
    {
        Check(LongThresholdBytes > 0, nameof(LongThresholdBytes));
        Check(FastThresholdBytes > 0, nameof(FastThresholdBytes));
        Check(RefreshSeconds is >= 1 and <= 60, nameof(RefreshSeconds));
        Check(IsWindow(LongWindowMinutes), nameof(LongWindowMinutes));
        Check(IsWindow(FastWindowMinutes) && FastWindowMinutes <= LongWindowMinutes, nameof(FastWindowMinutes));
        Check(ActivityIntervalMinutes >= 1 && ActivityIntervalMinutes <= FastWindowMinutes &&
            LongWindowMinutes % ActivityIntervalMinutes == 0 && FastWindowMinutes % ActivityIntervalMinutes == 0,
            nameof(ActivityIntervalMinutes));
        Check(RequiredActiveFiveMinuteIntervals >= 1 &&
            RequiredActiveFiveMinuteIntervals <= LongWindowMinutes / ActivityIntervalMinutes, nameof(RequiredActiveFiveMinuteIntervals));
        Check(FastRequiredActiveFiveMinuteIntervals >= 1 &&
            FastRequiredActiveFiveMinuteIntervals <= FastWindowMinutes / ActivityIntervalMinutes, nameof(FastRequiredActiveFiveMinuteIntervals));
        Check(LongMinimumActivitySeconds >= 0 &&
            LongMinimumActivitySeconds <= LongWindowMinutes * 60 - 5, nameof(LongMinimumActivitySeconds));
        Check(FastMinimumActivitySeconds >= 0 &&
            FastMinimumActivitySeconds <= FastWindowMinutes * 60 - 5, nameof(FastMinimumActivitySeconds));
        Check(MinimumActiveIntervalBytes > 0, nameof(MinimumActiveIntervalBytes));
        Check(MinimumPhysicalIntervalBytes > 0, nameof(MinimumPhysicalIntervalBytes));
        Check(MinimumCorrelatedIntervals >= 1 &&
            MinimumCorrelatedIntervals <= FastWindowMinutes / ActivityIntervalMinutes, nameof(MinimumCorrelatedIntervals));
        Check(IsShare(MinimumPhysicalToLogicalRatio, allowZero: true), nameof(MinimumPhysicalToLogicalRatio));
        Check(IsShare(MinimumDominantFileShare), nameof(MinimumDominantFileShare));
        Check(IsShare(MinimumRepeatedRangeShare), nameof(MinimumRepeatedRangeShare));
        Check(SmallFileMaximumBytes > 0, nameof(SmallFileMaximumBytes));
        Check(double.IsFinite(MinimumOverwriteMultiple) && MinimumOverwriteMultiple > 1, nameof(MinimumOverwriteMultiple));
        Check(IsShare(MaximumFileGrowthShare, allowZero: true), nameof(MaximumFileGrowthShare));
        Check(MinimumSizeObservationSeconds >= 5 &&
            MinimumSizeObservationSeconds <= FastWindowMinutes * 60 - 5, nameof(MinimumSizeObservationSeconds));
        Check(FileSizeSampleIntervalSeconds is >= 1 and <= 300, nameof(FileSizeSampleIntervalSeconds));
        Check(RecentRangeCount is >= 1 and <= 32, nameof(RecentRangeCount));
        Check(RecentRangeAgeSeconds is >= 5 and <= 3600, nameof(RecentRangeAgeSeconds));
        Check(NotificationCooldownMinutes is >= 1 and <= 1440, nameof(NotificationCooldownMinutes));
        Check(IsFileName(NvidiaProcessName), nameof(NvidiaProcessName));
        Check(IsFileName(NvidiaFileName), nameof(NvidiaFileName));
        Check(NvidiaDiagnosticThresholdBytes > 0, nameof(NvidiaDiagnosticThresholdBytes));
        Check(RecurrenceRetentionDays is >= 1 and <= 30, nameof(RecurrenceRetentionDays));
        Check(MaxTrackedProcesses is >= 1 and <= 256, nameof(MaxTrackedProcesses));
        Check(MaxFilesPerProcess is >= 1 and <= 16 && MaxTrackedProcesses * MaxFilesPerProcess <= 1024,
            nameof(MaxFilesPerProcess));
        Check(TrackingIdleSeconds is >= 5 and <= 3600, nameof(TrackingIdleSeconds));
        Check(double.IsFinite(TrackingPromotionRatio) && TrackingPromotionRatio is >= 1 and <= 10, nameof(TrackingPromotionRatio));

        void Check(bool condition, string name)
        {
            if (!condition)
                throw new ArgumentOutOfRangeException(name, language == UiLanguage.Russian
                    ? "Значение вне допустимого диапазона."
                    : "Value is outside the allowed range.");
        }
    }

    private static bool IsWindow(int value) => value is >= 5 and <= 60 && value % 5 == 0;

    private static bool IsFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && value.IndexOfAny(['\\', '/']) < 0;

    private static bool IsShare(double value, bool allowZero = false) =>
        value <= 1 && (allowZero ? value >= 0 : value > 0);
}
