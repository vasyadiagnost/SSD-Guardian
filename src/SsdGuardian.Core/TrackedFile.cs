namespace SsdGuardian.Core;

internal sealed class TrackedFile(string path)
{
    private Range[]? ranges;
    private int nextRange;
    private SizeSample initialSize;
    private SizeSample[]? sizes;

    internal string Path { get; } = path;
    internal WriteHistory Writes { get; } = new();
    internal RecentWriteWeight Weight { get; set; }

    internal void Write(long slot, long bytes, long? offset, int rangeCount, int rangeAgeSeconds)
    {
        var repeatedBytes = 0L;
        if (offset is >= 0 && offset <= long.MaxValue - bytes)
            repeatedBytes = RecordRange(slot, offset.Value, offset.Value + bytes, rangeCount, rangeAgeSeconds);
        Writes.Add(slot, bytes, repeatedBytes);
    }

    internal void ObserveSize(long slot, long length)
    {
        var minute = slot / WriteHistory.SlotsPerMinute;
        var offset = (byte)(slot % WriteHistory.SlotsPerMinute);
        ref var sample = ref GetSizeSample(minute);
        if (sample.HasValue && sample.Minute > minute)
            return;
        if (!sample.HasValue || sample.Minute != minute)
        {
            sample = new SizeSample(minute, length, length, length, length, offset, offset, true);
            return;
        }
        if (offset < sample.FirstOffset)
        {
            sample.FirstOffset = offset;
            sample.FirstLength = length;
        }
        if (offset >= sample.LastOffset)
        {
            sample.LastOffset = offset;
            sample.LastLength = length;
        }
        sample.Minimum = Math.Min(sample.Minimum, length);
        sample.Maximum = Math.Max(sample.Maximum, length);
    }

    internal SizeEvidence GetSizeEvidence(long firstSlot, long lastSlot)
    {
        if (!initialSize.HasValue)
            return default;

        var minimum = long.MaxValue;
        var maximum = 0L;
        var firstSampleSlot = long.MaxValue;
        var lastSampleSlot = 0L;
        var firstMinute = Math.Max(firstSlot, 0) / WriteHistory.SlotsPerMinute;
        var lastMinute = lastSlot / WriteHistory.SlotsPerMinute;
        if (sizes is null)
        {
            firstMinute = Math.Max(firstMinute, initialSize.Minute);
            lastMinute = Math.Min(lastMinute, initialSize.Minute);
        }
        for (var minute = firstMinute; minute <= lastMinute; minute++)
        {
            ref readonly var sample = ref sizes is null ? ref initialSize : ref sizes[minute % sizes.Length];
            if (!sample.HasValue || sample.Minute != minute)
                continue;
            // Do not use extrema from the portion of a minute outside the analysis window.
            if (sample.FirstSlot >= firstSlot && sample.LastSlot <= lastSlot)
            {
                firstSampleSlot = Math.Min(firstSampleSlot, sample.FirstSlot);
                lastSampleSlot = Math.Max(lastSampleSlot, sample.LastSlot);
                minimum = Math.Min(minimum, sample.Minimum);
                maximum = Math.Max(maximum, sample.Maximum);
            }
            else if (sample.FirstSlot >= firstSlot && sample.FirstSlot <= lastSlot)
            {
                firstSampleSlot = Math.Min(firstSampleSlot, sample.FirstSlot);
                lastSampleSlot = Math.Max(lastSampleSlot, sample.FirstSlot);
                minimum = Math.Min(minimum, sample.FirstLength);
                maximum = Math.Max(maximum, sample.FirstLength);
            }
            else if (sample.LastSlot >= firstSlot && sample.LastSlot <= lastSlot)
            {
                firstSampleSlot = Math.Min(firstSampleSlot, sample.LastSlot);
                lastSampleSlot = Math.Max(lastSampleSlot, sample.LastSlot);
                minimum = Math.Min(minimum, sample.LastLength);
                maximum = Math.Max(maximum, sample.LastLength);
            }
        }

        return new SizeEvidence(minimum, maximum, firstSampleSlot, lastSampleSlot);
    }

    private ref SizeSample GetSizeSample(long minute)
    {
        if (sizes is null)
        {
            if (!initialSize.HasValue || initialSize.Minute == minute)
                return ref initialSize;
            sizes = new SizeSample[61];
            sizes[initialSize.Minute % sizes.Length] = initialSize;
        }
        return ref sizes[minute % sizes.Length];
    }

    private long RecordRange(long slot, long start, long end, int rangeCount, int rangeAgeSeconds)
    {
        if (ranges is null || ranges.Length != rangeCount)
        {
            ranges = new Range[rangeCount];
            nextRange = 0;
        }
        var overlap = 0L;
        var oldest = slot - rangeAgeSeconds / WriteHistory.SlotSeconds;
        for (var index = 0; index < ranges.Length; index++)
        {
            ref var range = ref ranges[index];
            if (range.End == 0 || range.Slot < oldest || range.Slot > slot)
                continue;
            if (range.Start == start && range.End == end)
            {
                range.Slot = slot;
                return end - start;
            }
            overlap = Math.Max(overlap, Math.Min(end, range.End) - Math.Max(start, range.Start));
        }

        ranges[nextRange] = new Range { Slot = slot, Start = start, End = end };
        nextRange = (nextRange + 1) % ranges.Length;
        return overlap;
    }

    internal readonly record struct SizeEvidence(long Minimum, long Maximum, long FirstSlot, long LastSlot)
    {
        internal bool HasDuration(int seconds) =>
            LastSlot > FirstSlot && (LastSlot - FirstSlot) * WriteHistory.SlotSeconds >= seconds;
    }

    private struct Range
    {
        internal long Slot;
        internal long Start;
        internal long End;
    }

    private record struct SizeSample(
        long Minute, long FirstLength, long LastLength, long Minimum, long Maximum,
        byte FirstOffset, byte LastOffset, bool HasValue)
    {
        internal readonly long FirstSlot => Minute * WriteHistory.SlotsPerMinute + FirstOffset;
        internal readonly long LastSlot => Minute * WriteHistory.SlotsPerMinute + LastOffset;
    }
}
