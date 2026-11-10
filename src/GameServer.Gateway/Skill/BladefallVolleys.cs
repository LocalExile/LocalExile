using GameServer.Core;
/// <summary>Bladefall of Trarthus (BladefallAltZ): an instant toggle that rains a volley around the player every
/// <c>bladefall_volley_frequency_ms</c> while it spends mana. That stat is virtual: the client reads it but does not derive
/// it from a network stat snapshot, so without it the interval was 0 and the client hung on the first use (#584). The
/// game module runs the toggle (BladefallTrarthus.cs).</summary>
static class BladefallVolleys
{
    public const string EffectId = "BladefallAltZ";
    /// <summary>skill_cost_over_time: its GrantedStats are the spent-per-minute stats, set on the player while it lasts.</summary>
    public const string CostBuff = "skill_cost_over_time";
    /// <summary>righteous_blades: the buff icon and its BuffVisuals (the blades raining around the player).</summary>
    public const string BladesBuff = "righteous_blades";
    /// <summary>The blade a volley leaves where it fell (MiscAnimated BladefallGroundBladeAudio), as Bladefall's.</summary>
    public const string GroundBlade = "Metadata/MiscellaneousObjects/GroundBlade";
    public const string GroundBladeArt = "BladefallGroundBladeAudio";

    /// <summary>The volley interval: the gem's base interval, faster by its increase per whole 100 maximum mana (PoB:
    /// base / (1 + increase)).</summary>
    public static int FrequencyMs(int baseMs, int increasePer100Mana, long maximumMana)
    {
        if (baseMs <= 0) return 0;
        // In whole percents: 1.12 as a double made 700 / 1.12 truncate to 624.
        long percent = Math.Max(1, 100 + increasePer100Mana * Math.Max(0, maximumMana / 100));
        return (int)Math.Clamp(baseMs * 100L / percent, 1, int.MaxValue);
    }

    /// <summary>The mana it spends per minute: <c>base_mana_cost_%_per_minute</c> (CostTypes 9) of maximum mana. The
    /// original server set <c>mana_spent_per_minute</c> 2073 at 691 maximum mana for the gem's 300.</summary>
    public static int ManaPerMinute(int percentPerMinute, long maximumMana) =>
        percentPerMinute <= 0 || maximumMana <= 0 ? 0 : (int)Math.Min(int.MaxValue, maximumMana * percentPerMinute / 100);

    /// <summary>The BuffDefinitions row of <see cref="CostBuff"/> or <see cref="BladesBuff"/>, or null without the table.</summary>
    public static ushort? BuffRow(string id) => BuffRows.Value.TryGetValue(id, out ushort row) ? row : null;

    static readonly Lazy<Dictionary<string, ushort>> BuffRows = new(() =>
    {
        const string file = "data/tables/BuffDefinitions.json";
        var rows = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (!ClientTables.Exists(file)) return rows;
        using var table = GameTables.Open(file);
        int row = 0;
        foreach (var buff in table.RootElement.EnumerateArray())
        {
            if (buff.GetProperty("Id").GetString() is { } id and (CostBuff or BladesBuff)) rows.TryAdd(id, (ushort)row);
            row++;
        }
        return rows;
    });
}
