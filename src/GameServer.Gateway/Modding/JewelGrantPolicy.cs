/// <summary>Rules used by the server while the registering owner is loaded.</summary>
public static class JewelGrantPolicy
{
    /// <summary>Uses these rules until the registration is disposed or its owner unloads.</summary>
    public static IDisposable Use(string owner, JewelGrantRules options) =>
        GameEventOwners.Own(owner, Use(options));

    static readonly Lock Gate = new();
    static readonly List<(object Owner, JewelGrantRules Options)> Active = [];
    static JewelGrantRules _current = new();
    /// <summary>The latest active rules, or the server defaults when no owner supplies any.</summary>
    public static JewelGrantRules Current => Volatile.Read(ref _current);

    internal static IDisposable Use(JewelGrantRules options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumPairs < 0 || options.MaximumPairs > 100) throw new ArgumentOutOfRangeException(nameof(options));
        var owner = new object();
        lock (Gate)
        {
            Active.Add((owner, options));
            Volatile.Write(ref _current, options);
        }
        return new Lease(() =>
        {
            lock (Gate)
            {
                Active.RemoveAll(entry => ReferenceEquals(entry.Owner, owner));
                Volatile.Write(ref _current, Active.Count == 0 ? new() : Active[^1].Options);
            }
        });
    }

    sealed class Lease(Action remove) : IDisposable
    {
        Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
