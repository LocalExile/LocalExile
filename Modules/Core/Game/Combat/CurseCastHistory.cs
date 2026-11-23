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
    public static void RecordCompletedCurseCast(Player player, string effect, double at)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PendingTransfer is not null
            || player.PlayerLife == 0 || player.Paused || !double.IsFinite(at) || at < 0
            || CurseCastToken(effect) is not { } token) return;
        player.RecentEvents.Record(StatConditions.CastEventKey(token, CurseCastHistorySeconds), at, CurseCastHistorySeconds);
    }
}
