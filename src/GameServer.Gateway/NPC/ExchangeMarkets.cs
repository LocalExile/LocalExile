using GameServer.Modding;

/// <summary>Host-owned market registrations; no mod delegates or assemblies retained after unload.</summary>
static class ExchangeMarkets
{
    internal sealed record Snapshot(string Owner, string Key, ExchangeQuote[] Quotes, string[] Leagues);
    static readonly object Gate = new();
    static readonly Dictionary<string, Market> Markets = new(StringComparer.Ordinal);
    static long _version;
    public static long Version => Interlocked.Read(ref _version);
    public static Snapshot[] Current(string league)
    {
        lock (Gate) return Markets.Values.Select(m => m.Value).OfType<Snapshot>()
            .Where(s => s.Leagues.Length == 0 || s.Leagues.Contains(league, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
    public static HashSet<string> ActiveOwners(string league) => Current(league).Select(s => s.Owner).ToHashSet(StringComparer.Ordinal);
    public static bool IsCurrent(Snapshot snapshot)
    {
        lock (Gate) return Markets.TryGetValue(snapshot.Owner, out var market) && ReferenceEquals(market.Value, snapshot);
    }
    public static IExchangeMarket Register(string owner)
    {
        lock (Gate)
        {
            if (Markets.ContainsKey(owner)) throw new InvalidOperationException("Market already registered: " + owner);
            var market = new Market(owner);
            Markets.Add(owner, market);
            return market;
        }
    }
    sealed class Market(string owner) : IExchangeMarket
    {
        public Snapshot? Value { get; private set; }
        public void Publish(string snapshot, IReadOnlyList<ExchangeQuote> quotes, IReadOnlyList<string> leagues)
        {
            if (string.IsNullOrWhiteSpace(snapshot) || quotes.Count > 5000) throw new ArgumentException("Invalid market snapshot");
            var copy = quotes.ToArray();
            if (copy.Any(q => q is null || string.IsNullOrWhiteSpace(q.Want) || string.IsNullOrWhiteSpace(q.Give)
                || q.Wanted is <= 0 or > 999999 || q.Offered is <= 0 or > 999999
                || q.Want.Equals(q.Give, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Invalid market quantity");
            lock (Gate)
            {
                if (!Markets.TryGetValue(owner, out var current) || current != this) throw new ObjectDisposedException(owner);
                Value = new(owner, snapshot, copy, leagues.ToArray());
                Interlocked.Increment(ref _version);
            }
        }
        public void Dispose() { lock (Gate) if (Markets.GetValueOrDefault(owner) == this) { Markets.Remove(owner); Interlocked.Increment(ref _version); } }
    }
}
