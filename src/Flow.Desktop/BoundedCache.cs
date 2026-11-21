namespace Flow.Desktop;

internal sealed class BoundedCache<TKey, TValue>(int capacity, long budget) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> recency = [];
    private long retained;

    private sealed record Entry(TKey Key, TValue Value, long Weight);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!entries.TryGetValue(key, out var node))
        {
            value = default!;
            return false;
        }
        recency.Remove(node);
        recency.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Add(TKey key, TValue value, long weight)
    {
        if (weight > budget) return;
        if (entries.TryGetValue(key, out var existing)) Remove(existing);
        while (entries.Count >= capacity || retained > budget - weight) Remove(recency.Last!);
        entries.Add(key, recency.AddFirst(new Entry(key, value, weight)));
        retained += weight;
    }

    public void RemoveWhere(Func<TKey, bool> predicate)
    {
        for (var node = recency.First; node is not null;)
        {
            var next = node.Next;
            if (predicate(node.Value.Key)) Remove(node);
            node = next;
        }
    }

    public void Clear()
    {
        entries.Clear();
        recency.Clear();
        retained = 0;
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        entries.Remove(node.Value.Key);
        recency.Remove(node);
        retained -= node.Value.Weight;
    }
}
