using System.Diagnostics.CodeAnalysis;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The lists a protocol record keeps. The servers keep these records (an instance's monsters as they were created,
/// a player's object, items) for longer than the module that made them stays loaded, and a list written as a collection
/// expression in a module (<c>[.. skills]</c> for an <c>IReadOnlyList</c>) is an object of a class the compiler generated
/// inside that module's assembly: kept by a record, it kept the unloaded module in memory. So a record copies a list
/// whose type would be unloaded with its assembly, and keeps any other list as it was given.</summary>
public static class Owned
{
    [return: NotNullIfNotNull(nameof(list))]
    public static IReadOnlyList<T>? List<T>(IReadOnlyList<T>? list)
    {
        if (list is null or T[] or List<T> || !list.GetType().IsCollectible) return list;
        T[] copy = [.. list];
        return copy;
    }

    [return: NotNullIfNotNull(nameof(collection))]
    public static IReadOnlyCollection<T>? Collection<T>(IReadOnlyCollection<T>? collection)
    {
        if (collection is null or T[] or List<T> or HashSet<T> || !collection.GetType().IsCollectible) return collection;
        T[] copy = [.. collection];
        return copy;
    }

    /// <summary>A list of keyed lists (a skill's additional stat sets): the outer list and each inner one.</summary>
    public static IReadOnlyList<(TKey Key, IReadOnlyList<T> Values)> Lists<TKey, T>(IReadOnlyList<(TKey Key, IReadOnlyList<T> Values)> lists)
    {
        (TKey Key, IReadOnlyList<T> Values)[]? copy = null;
        for (int i = 0; i < lists.Count; i++)
        {
            var values = List(lists[i].Values);
            if (copy is null && ReferenceEquals(values, lists[i].Values)) continue;
            copy ??= [.. lists];
            copy[i] = (lists[i].Key, values);
        }
        return copy ?? List(lists);
    }
}
