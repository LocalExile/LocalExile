namespace GameServer.Modding;

/// <summary>Finite synthetic liquidity supplied by a mod; quantities are in whole currency units.</summary>
public sealed record ExchangeQuote(string Want, string Give, int Wanted, int Offered);

/// <summary>One mod generation's market. Disposal withdraws its liquidity without changing player escrow.</summary>
public interface IExchangeMarket : IDisposable
{
    /// <summary>Publish a replacement snapshot. Reusing its key preserves fills across reloads/restarts.
    /// An empty league list serves all non-SSF leagues. Publication itself never performs network I/O.</summary>
    void Publish(string snapshot, IReadOnlyList<ExchangeQuote> quotes, IReadOnlyList<string> leagues);
}
