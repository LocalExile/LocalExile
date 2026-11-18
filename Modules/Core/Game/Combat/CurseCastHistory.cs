public static partial class Game
{
    const int CurseCastHistorySeconds = 10;

    static string? CurseCastToken(string effect) => effect switch
    {
        "TemporalChains" => "temporal_chains", "ElementalWeakness" => "elemental_weakness",
        "Enfeeble" => "enfeeble", "Vulnerability" => "vulnerability", "Punishment" => "punishment",
        "Flammability" => "flammability", "Frostbite" => "frostbite", "Conductivity" => "conductivity",
        // The actual client-table conditional ids retain this spelling.
        "Despair" => "dispair", _ => null,
    };

    static IEnumerable<string> TrackedCurseCastEvents() => new[]
        { "TemporalChains", "ElementalWeakness", "Enfeeble", "Vulnerability", "Punishment", "Flammability",
            "Frostbite", "Conductivity", "Despair" }
        .Select(effect => StatConditions.CastEventKey(CurseCastToken(effect)!, CurseCastHistorySeconds));

    // Only the completed manual-cast executor calls this. On-hit, aura and triggered curse application do not.
    public static void RecordCompletedCurseCast(ClientState conn, string effect, double at)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null
                || conn.PlayerLife == 0 || conn.Paused || !double.IsFinite(at) || at < 0
                || CurseCastToken(effect) is not { } token) return;
            conn.RecentEvents.Record(StatConditions.CastEventKey(token, CurseCastHistorySeconds), at, CurseCastHistorySeconds);
        }
    }
}
