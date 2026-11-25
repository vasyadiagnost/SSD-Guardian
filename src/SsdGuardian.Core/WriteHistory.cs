namespace SsdGuardian.Core;

internal sealed class WriteHistory
{
    internal const int SlotSeconds = 5;
    internal const int SlotsPerMinute = 60 / SlotSeconds;
    internal const int SessionHistorySlots = 60 * SlotsPerMinute;
    private const int SparseSlotLimit = 32;
    private Bucket[]? buckets;
    private int usedSlots;
    private int lastWrittenIndex;
    private long newestSlot;

    internal WriteTotals Session { get; private set; }

    internal static long GetSlot(DateTimeOffset timestamp) =>
        timestamp.UtcTicks / (TimeSpan.TicksPerSecond * SlotSeconds);

    internal void Add(long slot, long bytes, long repeatedBytes = 0)
    {
        Session = Session.Add(bytes);
        newestSlot = Math.Max(slot, newestSlot);
        if (newestSlot - slot >= SessionHistorySlots)
            return;
        buckets ??= new Bucket[4];
        ref var bucket = ref GetBucket(slot);
        if (bucket.Slot != slot)
            bucket = new Bucket { Slot = slot };
        bucket.Totals = bucket.Totals.Add(bytes);
        bucket.RepeatedBytes = WriteTotals.SaturatingAdd(bucket.RepeatedBytes, repeatedBytes);
    }

    private ref Bucket GetBucket(long slot)
    {
        var storage = buckets!;
        if (storage.Length == SessionHistorySlots)
            return ref storage[slot % SessionHistorySlots];
        if (usedSlots > 0 && storage[lastWrittenIndex].Slot == slot)
            return ref storage[lastWrittenIndex];

        var expired = -1;
        for (var index = 0; index < usedSlots; index++)
        {
            var existingSlot = storage[index].Slot;
            if (existingSlot == slot)
            {
                lastWrittenIndex = index;
                return ref storage[index];
            }
            if (newestSlot - existingSlot >= SessionHistorySlots)
                expired = index;
        }

        if (expired >= 0)
        {
            lastWrittenIndex = expired;
            return ref storage[expired];
        }
        if (usedSlots < storage.Length)
        {
            lastWrittenIndex = usedSlots++;
            return ref storage[lastWrittenIndex];
        }
        if (storage.Length < SparseSlotLimit)
        {
            Array.Resize(ref storage, storage.Length * 2);
            buckets = storage;
            lastWrittenIndex = usedSlots++;
            return ref storage[lastWrittenIndex];
        }

        // Dense histories use one slot per bucket. Brief, one-off paths stay compact.
        var expanded = new Bucket[SessionHistorySlots];
        foreach (var item in storage)
            expanded[item.Slot % SessionHistorySlots] = item;
        buckets = expanded;
        return ref expanded[slot % SessionHistorySlots];
    }

    internal WriteWindows ReadWindows(long lastSlot)
    {
        if (buckets is null)
            return new WriteWindows(default, default, default, Session);

        WriteTotals last10 = default, last30 = default, last60 = default;
        var length = buckets.Length == SessionHistorySlots ? SessionHistorySlots : usedSlots;
        for (var index = 0; index < length; index++)
        {
            ref readonly var bucket = ref buckets[index];
            var offset = lastSlot - bucket.Slot;
            if (offset is < 0 or >= SessionHistorySlots || bucket.Totals.Operations == 0)
                continue;
            last60 = last60.Add(bucket.Totals.Bytes, bucket.Totals.Operations);
            if (offset < 30 * SlotsPerMinute)
                last30 = last30.Add(bucket.Totals.Bytes, bucket.Totals.Operations);
            if (offset < 10 * SlotsPerMinute)
                last10 = last10.Add(bucket.Totals.Bytes, bucket.Totals.Operations);
        }

        return new WriteWindows(last10, last30, last60, Session);
    }

    internal Period ReadPeriod(long firstSlot, long lastSlot)
    {
        if (buckets is null)
            return default;

        WriteTotals totals = default;
        long firstActiveSlot = long.MaxValue, lastActiveSlot = 0, repeatedBytes = 0;
        if (buckets.Length == SessionHistorySlots)
        {
            for (var slot = Math.Max(firstSlot, 0); slot <= lastSlot; slot++)
            {
                ref readonly var bucket = ref buckets[slot % SessionHistorySlots];
                if (bucket.Slot == slot && bucket.Totals.Operations != 0)
                    Include(bucket);
            }
        }
        else
        {
            for (var index = 0; index < usedSlots; index++)
            {
                ref readonly var bucket = ref buckets[index];
                if (bucket.Slot >= firstSlot && bucket.Slot <= lastSlot)
                    Include(bucket);
            }
        }
        return totals.Operations == 0 ? default : new Period(totals, repeatedBytes, firstActiveSlot, lastActiveSlot);

        void Include(in Bucket bucket)
        {
            firstActiveSlot = Math.Min(firstActiveSlot, bucket.Slot);
            lastActiveSlot = Math.Max(lastActiveSlot, bucket.Slot);
            totals = totals.Add(bucket.Totals.Bytes, bucket.Totals.Operations);
            repeatedBytes = WriteTotals.SaturatingAdd(repeatedBytes, bucket.RepeatedBytes);
        }
    }

    internal readonly record struct Period(
        WriteTotals Totals, long RepeatedBytes, long FirstActiveSlot, long LastActiveSlot)
    {
        internal long ActiveSeconds => (LastActiveSlot - FirstActiveSlot) * SlotSeconds;
    }

    private struct Bucket
    {
        internal long Slot;
        internal WriteTotals Totals;
        internal long RepeatedBytes;
    }
}
