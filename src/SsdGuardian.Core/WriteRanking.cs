namespace SsdGuardian.Core;

internal readonly record struct RecentWriteWeight(long LastSlot, double Bytes, double PreviousBytes)
{
    internal RecentWriteWeight Add(long slot, long bytes, int window)
    {
        var latest = Math.Max(LastSlot, slot);
        var interval = latest / window;
        var previousInterval = LastSlot / window;
        var current = interval == previousInterval ? Bytes : 0;
        var previous = interval == previousInterval ? PreviousBytes : interval == previousInterval + 1 ? Bytes : 0;
        if (slot / window == interval)
            current += bytes;
        else if (slot / window == interval - 1)
            previous += bytes;
        return new RecentWriteWeight(latest, current, previous);
    }

    internal double Read(long slot, int window)
    {
        if (slot - LastSlot >= window)
            return 0;

        var latest = Math.Max(slot, LastSlot);
        var fraction = 1d - (double)(latest % window) / window;
        return latest / window == LastSlot / window ? Bytes + PreviousBytes * fraction : Bytes * fraction;
    }
}

internal sealed class WriteCandidates<TKey>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly Dictionary<TKey, RecentWriteWeight> weights = new(comparer);

    internal RecentWriteWeight Observe(TKey key, long slot, long bytes, int window, int capacity)
    {
        if (!weights.TryGetValue(key, out var weight) && weights.Count >= capacity)
            weights.Remove(WriteRanking.FindWeakest(weights, slot, window, static item => item).Key);

        weight = weight.Add(slot, bytes, window);
        weights[key] = weight;
        return weight;
    }

    internal void Remove(TKey key) => weights.Remove(key);

    internal void Clear() => weights.Clear();
}

internal static class WriteRanking
{
    internal static (TKey Key, double Weight) FindWeakest<TKey, TEntry>(
        Dictionary<TKey, TEntry> entries, long slot, int window, Func<TEntry, RecentWriteWeight> getWeight)
        where TKey : notnull
    {
        TKey weakest = default!;
        var minimum = double.PositiveInfinity;
        foreach (var (key, entry) in entries)
        {
            var weight = getWeight(entry).Read(slot, window);
            if (weight < minimum)
            {
                weakest = key;
                minimum = weight;
            }
        }
        return (weakest, minimum);
    }
}
