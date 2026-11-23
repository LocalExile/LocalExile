using System.Collections;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Stats by id that code reads and changes in place: a plain dictionary (<see cref="PlainStatMap"/>), or a stat graph
/// rule's view, which records what it reads (StatGraph.StatView). Code written against it runs on either.
/// </summary>
public abstract class StatMap : IDictionary<uint, int>, IReadOnlyDictionary<uint, int>
{
    public abstract bool TryGetValue(uint id, [MaybeNullWhen(false)] out int value);
    protected abstract void Set(uint id, int value);
    /// <summary>Takes the id out; true when it was present.</summary>
    public abstract bool Remove(uint id);
    /// <summary>Every stat present (a graph view reads all of them).</summary>
    protected abstract List<KeyValuePair<uint, int>> Pairs();

    public int GetValueOrDefault(uint id) => TryGetValue(id, out int value) ? value : 0;
    public int GetValueOrDefault(uint id, int fallback) => TryGetValue(id, out int value) ? value : fallback;
    public bool ContainsKey(uint id) => TryGetValue(id, out _);

    public int this[uint id]
    {
        get => TryGetValue(id, out int value) ? value : throw new KeyNotFoundException($"stat {id}");
        set => Set(id, value);
    }

    public void Add(uint id, int value)
    {
        if (ContainsKey(id)) throw new ArgumentException($"stat {id} is already present");
        Set(id, value);
    }

    public bool TryAdd(uint id, int value)
    {
        if (ContainsKey(id)) return false;
        Set(id, value);
        return true;
    }

    public int Count => Pairs().Count;
    public ICollection<uint> Keys => Pairs().Select(p => p.Key).ToList();
    public ICollection<int> Values => Pairs().Select(p => p.Value).ToList();
    IEnumerable<uint> IReadOnlyDictionary<uint, int>.Keys => Keys;
    IEnumerable<int> IReadOnlyDictionary<uint, int>.Values => Values;
    public IEnumerator<KeyValuePair<uint, int>> GetEnumerator() => Pairs().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool IsReadOnly => false;
    public void Add(KeyValuePair<uint, int> item) => Add(item.Key, item.Value);
    public void Clear() { foreach (var (id, _) in Pairs()) Remove(id); }
    public bool Contains(KeyValuePair<uint, int> item) => TryGetValue(item.Key, out int v) && v == item.Value;
    public void CopyTo(KeyValuePair<uint, int>[] array, int index) { foreach (var p in Pairs()) array[index++] = p; }
    public bool Remove(KeyValuePair<uint, int> item) => Contains(item) && Remove(item.Key);
}

/// <summary>A <see cref="StatMap"/> on a plain dictionary (which it changes).</summary>
public sealed class PlainStatMap(Dictionary<uint, int> stats) : StatMap
{
    public PlainStatMap() : this([]) { }
    public Dictionary<uint, int> Stats => stats;
    public override bool TryGetValue(uint id, [MaybeNullWhen(false)] out int value) => stats.TryGetValue(id, out value);
    protected override void Set(uint id, int value) => stats[id] = value;
    public override bool Remove(uint id) => stats.Remove(id);
    protected override List<KeyValuePair<uint, int>> Pairs() => [.. stats];
}
