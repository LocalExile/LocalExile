using GameServer.Core;

/// <summary>
/// The mods a monster's own row carries for good (MonsterVarieties <c>ModsKeys</c> and <c>Special_ModsKeys</c> → Mods),
/// limited to the ones that are behaviour the server can express without the systems it lacks: a boss that is immune to
/// knockback, a boss that cannot be stunned again for a stated time, a boss that slows no further than half speed, and a
/// stage or a soul of a boss that drops nothing (Agents/docs/game/campaign/*.md, the act bosses). The rolled mods of rare
/// and magic monsters are a different table and are not read here.
/// </summary>
public static class BossMods
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadBossModsData()
    {
        Rows = Load();
    }

    /// <summary>BossHealOnPlayerDeath is not here: its stat (<c>heal_aura_owner_%_max_life_on_death</c>) is 0 in the
    /// extracted table for both it and its Minor form, so how much life comes back is not something the data says.</summary>
    public const string ImmuneToKnockback = "ImmuneToKnockback";

    /// <summary>The monster gives no items and no experience (a boss's first stage: Piety's escape, Malachai, Shakari).</summary>
    public const string NoDropsOrExperience = "MonsterNoDropsOrExperience";

    /// <summary>The monster gives no items, and its experience is still its own (Abberath, Shavronne's tower, Maligaro's).</summary>
    public const string NoDrops = "MonsterNoDrops";

    /// <summary>A boss slows no further than this: its own row states the floor (half speed for the Elder, Atziri, the
    /// Shaper and the campaign bosses that carry it).</summary>
    public const string SlowFloor = "CannotBeSlowedBelowValueBosses";

    /// <summary>An act boss cannot be stunned again for this long after a stun ends (2000 ms in its row).</summary>
    const uint StunImmunityStat = Stat.CannotBeStunnedForMsAfterStunFinished;

    /// <summary>The action-speed floor a row states, as a signed percentage (the bosses' -50%) — how far slows may take it.</summary>
    const uint SlowFloorStat = Stat.HellscapeBootsActionSpeedPlusPctMinimumValue;

    /// <summary>What a monster's own rows say about it: the mod ids, the repeat-stun immunity the mods' stats give, and the
    /// floor they put under its action speed (0 when the rows state none).</summary>
    public sealed record Row(HashSet<string> Mods, int StunImmunityMs, int SlowFloorPct = 0);

    static Dictionary<string, Row> Rows = default!;

    /// <summary>Whether the variety's row names this mod in either of its mod lists.</summary>
    public static bool Has(MonsterCatalog.Variety? variety, string mod) =>
        variety is not null && Rows.GetValueOrDefault(variety.Id)?.Mods.Contains(mod) == true;

    /// <summary>Whether nothing may shove the monster: the client's ImmuneToKnockback component or its mod.</summary>
    public static bool KnockbackImmune(MonsterCatalog.Variety? variety) =>
        variety?.ComponentsOrBase.Contains(ImmuneToKnockback) == true || Has(variety, ImmuneToKnockback);

    /// <summary>How long after a stun ends the monster cannot be stunned again, when its row says (an act boss's 2000 ms);
    /// null when the row does not, and the caller falls back to the wiki's rule for uniques.</summary>
    public static int? StunImmunityMilliseconds(MonsterCatalog.Variety? variety) =>
        variety is not null && Rows.GetValueOrDefault(variety.Id) is { StunImmunityMs: > 0 } row ? row.StunImmunityMs : null;

    /// <summary>Whether the monster's death gives no items and no experience.</summary>
    public static bool NoRewards(MonsterCatalog.Variety? variety) => Has(variety, NoDropsOrExperience);

    /// <summary>How far slows may take the monster's action speed, as a factor of its own: the floor its row states
    /// (<c>CannotBeSlowedBelowValueBosses</c>, -50% → half speed), else the .25 every monster has had.</summary>
    public static double SlowestActionFactor(MonsterCatalog.Variety? variety) =>
        variety is not null && Rows.GetValueOrDefault(variety.Id) is { SlowFloorPct: < 0 } row
            ? Math.Max(.01, 1 + row.SlowFloorPct / 100d)
            : .25;

    /// <summary>Whether the monster's death gives no items (its experience may still be rolled).</summary>
    public static bool GivesNoItems(MonsterCatalog.Variety? variety) => Has(variety, NoDropsOrExperience) || Has(variety, NoDrops);

    static Dictionary<string, Row> Load()
    {
        if (!ClientTables.Has("MonsterVarieties") || !ClientTables.Has("Mods")) return [];
        try
        {
            return ClientTables.Has("Stats")
                ? Parse(ClientTables.Table("MonsterVarieties"), ClientTables.Table("Mods"), ClientTables.Table("Stats"))
                : Parse(ClientTables.Table("MonsterVarieties"), ClientTables.Table("Mods"), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidCastException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] monster mods: {ex.Message}");
            return [];
        }
    }

    static readonly string[] StatsKeyColumns = ["StatsKey1", "StatsKey2", "StatsKey3", "StatsKey4", "StatsKey5", "StatsKey6", "StatsKey7", "StatsKey8"];
    static readonly string[] StatMinColumns = ["Stat1Min", "Stat2Min", "Stat3Min", "Stat4Min", "Stat5Min", "Stat6Min", "Stat7Min", "Stat8Min"];
    static readonly string[] VarietyModColumns = ["ModsKeys", "Special_ModsKeys"];

    /// <summary>From the MonsterVarieties, Mods and (for the stat values) Stats client tables: each variety's mod ids, how
    /// long it cannot be stunned after a stun, and the floor under its action speed, both its own keys into Mods.</summary>
    public static Dictionary<string, Row> Parse(ClientTable varieties, ClientTable mods, ClientTable? stats)
    {
        int statCount = stats?.Count ?? 0;
        // Only inspect the ~hundreds of Mods rows actually referenced by MonsterVarieties instead of all 35k+ rows.
        var modInfo = new Dictionary<int, (string Id, int StunMs, int SlowFloor)>();
        (string Id, int StunMs, int SlowFloor) ReadMod(int row)
        {
            if (modInfo.TryGetValue(row, out var cached)) return cached;
            var mod = mods[row];
            string id = mod.TextOrNull("Id") ?? "";
            int stunMs = 0, slowFloor = 0;
            if (id.Length > 0 && statCount > 0)
            {
                for (int slot = 0; slot < StatsKeyColumns.Length; slot++)
                {
                    if (mod.IntOrNull(StatsKeyColumns[slot]) is not int key || (uint)key >= (uint)statCount) continue;
                    uint statId = (uint)key + 1;
                    if (statId == StunImmunityStat)
                    {
                        int ms = Math.Max(0, mod.IntOrNull(StatMinColumns[slot]) ?? 0);
                        if (ms > stunMs) stunMs = ms;
                    }
                    else if (statId == SlowFloorStat)
                    {
                        int pct = Math.Min(0, mod.IntOrNull(StatMinColumns[slot]) ?? 0);
                        if (pct < slowFloor) slowFloor = pct;
                    }
                }
            }
            return modInfo[row] = (id, stunMs, slowFloor);
        }
        var result = new Dictionary<string, Row>(StringComparer.Ordinal);
        int modCount = mods.Count;
        foreach (var variety in varieties)
        {
            string id = variety.TextOrNull("Id") ?? "";
            if (id.Length == 0) continue;
            HashSet<string>? own = null;
            int stunMs = 0;
            int slowFloor = 0;
            for (int c = 0; c < VarietyModColumns.Length; c++)
                foreach (int row in variety.Ints(VarietyModColumns[c]))
                {
                    if ((uint)row >= (uint)modCount) continue;
                    var info = ReadMod(row);
                    if (info.Id.Length == 0) continue;
                    (own ??= new HashSet<string>(StringComparer.Ordinal)).Add(info.Id);
                    if (info.StunMs > stunMs) stunMs = info.StunMs;
                    if (info.SlowFloor < slowFloor) slowFloor = info.SlowFloor;
                }
            if (own is not null) result[id] = new Row(own, stunMs, slowFloor);
        }
        return result;
    }
}
