namespace SsdGuardian.Windows;

internal sealed class BoundedMap<TKey, TValue>(int capacity, Action<TValue>? discard = null)
    where TKey : notnull
{
    private const int RetainedNodeCount = 32;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> entries = new();
    private readonly LinkedList<(TKey Key, TValue Value)> age = new();
    private readonly Stack<LinkedListNode<(TKey Key, TValue Value)>> available = new();

    public bool Set(TKey key, TValue value)
    {
        if (entries.TryGetValue(key, out var current))
        {
            discard?.Invoke(current.Value.Value);
            age.Remove(current);
            current.Value = (key, value);
            age.AddLast(current);
            return false;
        }

        if (entries.Count < capacity)
        {
            if (!available.TryPop(out var node))
                node = new((key, value));
            else
                node.Value = (key, value);
            age.AddLast(node);
            entries.Add(key, node);
            return false;
        }

        var oldest = age.First!;
        discard?.Invoke(oldest.Value.Value);
        entries.Remove(oldest.Value.Key);
        age.RemoveFirst();
        oldest.Value = (key, value);
        age.AddLast(oldest);
        entries.Add(key, oldest);
        return true;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (entries.TryGetValue(key, out var current))
        {
            value = current.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public bool TryRemove(TKey key, out TValue value)
    {
        if (entries.Remove(key, out var current))
        {
            age.Remove(current);
            value = current.Value.Value;
            if (available.Count < RetainedNodeCount)
            {
                current.Value = default;
                available.Push(current);
            }
            return true;
        }

        value = default!;
        return false;
    }

    public void Update(TKey key, TValue value)
    {
        if (entries.TryGetValue(key, out var current))
            current.Value = (key, value);
    }

    public void RemoveWhere(Func<TValue, bool> shouldRemove)
    {
        var current = age.First;
        while (current is not null)
        {
            var next = current.Next;
            if (shouldRemove(current.Value.Value) && TryRemove(current.Value.Key, out var removed))
                discard?.Invoke(removed);
            current = next;
        }
    }

    public void Clear()
    {
        if (discard is not null)
        {
            foreach (var item in age)
                discard(item.Value);
        }

        entries.Clear();
        age.Clear();
    }
}
