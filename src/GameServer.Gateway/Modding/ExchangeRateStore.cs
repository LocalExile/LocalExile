using GameServer.Core.Trade;

/// <summary>What a mod gives the Currency Exchange: how it trades (<see cref="Use(string, ExchangeOptions)"/>) and the rates (<see cref="Publish"/>), saved in
/// <see cref="File"/> for the module to read. The Gateway itself contacts nothing for them.</summary>
public static class ExchangeRateStore
{
    public const string File = "data/currency-exchange-rates.json";

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    static readonly List<ExchangeOptions> Asked = [];
    static readonly ExchangeOptions Defaults = new();

    /// <summary>How the exchange trades, as the mod chose; the last one asked wins, so a reloading mod's new choice stands.</summary>
    public static ExchangeOptions Options
    {
        get { lock (Asked) return Asked.Count == 0 ? Defaults : Asked[^1]; }
    }

    /// <summary>The Currency Exchange trades by these options until the handle is disposed or its owner unloads.</summary>
    public static IDisposable Use(string owner, ExchangeOptions options) => GameEventOwners.Own(owner, Use(options));

    public static IDisposable Use(ExchangeOptions options)
    {
        lock (Asked) Asked.Add(options);
        return new Undo(() =>
        {
            lock (Asked) Asked.RemoveAll(a => ReferenceEquals(a, options));
        });
    }

    /// <summary>Saves new rates. What a mod says is not trusted further than being rates: the module's market checks each trade.</summary>
    public static void Publish(string json)
    {
        ExchangeRates? rates;
        try { rates = ExchangeRates.Parse(json); }
        catch (System.Text.Json.JsonException e) { throw new FormatException($"not rates: {e.Message}", e); }
        if (rates is not { Leagues.Count: > 0 }) throw new FormatException("no leagues in the rates");
        string written = File + ".new";
        System.IO.File.WriteAllText(written, json);
        System.IO.File.Move(written, File, overwrite: true);
        Console.WriteLine($"[rates] rates of {DateTimeOffset.FromUnixTimeSeconds(rates.SourceHour):u}");
    }
}
