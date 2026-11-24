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

    /// <summary>Retail act/quest boss heal-on-player-death marker mods. The extracted Mods table stores 0 for
    /// <c>heal_aura_owner_%_max_life_on_death</c>; retail heals nearby living bosses for +10% max life on
    /// <see cref="HealOnPlayerDeath"/> and +5% max life on <see cref="HealOnPlayerDeathMinor"/>.</summary>
    public const string HealOnPlayerDeath = "BossHealOnPlayerDeath";
    public const string HealOnPlayerDeathMinor = "BossHealOnPlayerDeathMinor";

    public const string ImmuneToKnockback = "ImmuneToKnockback";

    /// <summary>The monster gives no items and no experience (a boss's first stage: Piety's escape, Malachai, Shakari).</summary>
    public const string NoDropsOrExperience = "MonsterNoDropsOrExperience";

    /// <summary>The monster gives no items, and its experience is still its own (Abberath, Shavronne's tower, Maligaro's).</summary>
    public const string NoDrops = "MonsterNoDrops";

    /// <summary>A boss slows no further than this: its own row states the floor (half speed for the Elder, Atziri, the
    /// Shaper and the campaign bosses that carry it).</summary>
    public const string SlowFloor = "CannotBeSlowedBelowValueBosses";

    /// <summary>The marker mod that names a pinnacle boss: MonsterVarieties <c>ModsKeys</c> carries
    /// <c>PinnacleAtlasBoss</c> (Mods row 32441) on exactly 49 varieties — the Shaper, the Elder, Sirus and the
    /// Conquerors, the Maven, the Eater of Worlds, the Searing Exarch, Venarius and the Atlas Memory Incarnations,
    /// with their standalone, gauntlet and Uber forms (client.db, 3.29.3c). No other variety carries it. Read
    /// through <see cref="Has"/> like the other marker mods here.</summary>
    public const string PinnacleAtlasBoss = "PinnacleAtlasBoss";

    /// <summary>An act boss cannot be stunned again for this long after a stun ends (2000 ms in its row).</summary>
    const uint StunImmunityStat = Stat.CannotBeStunnedForMsAfterStunFinished;

    /// <summary>The action-speed floor a row states, as a signed percentage (the bosses' -50%) — how far slows may take it.</summary>
    const uint SlowFloorStat = Stat.HellscapeBootsActionSpeedPlusPctMinimumValue;

    /// <summary>Nothing may shove the monster. <see cref="ImmuneToKnockback"/> is only one of the 8 Mods rows that
    /// carry this stat: <c>MonsterCannotBeKnockedBack</c> alone is on 78 MonsterVarieties rows, 77 of them without
    /// ImmuneToKnockback (Piety's Malachai ally, the Insect Spawner parasite boss, the Talisman Wolf King's wolf
    /// form and Doedre's pillars among them), and <c>BetrayalUpgradeMonsterCannotBeStopped</c>,
    /// <c>MetamorphosisKitava</c> and <c>StanceHarvestBossVinePhase</c> carry it too, so the stat is read like the
    /// two below rather than the id alone.</summary>
    const uint KnockbackImmuneStat = Stat.CannotBeKnockedBack;

    /// <summary>What a monster's own rows say about it: the mod ids, whether the mods' stats say nothing may shove it,
    /// the repeat-stun immunity they give, and the floor they put under its action speed (0 when the rows state none).</summary>
    public sealed record Row(HashSet<string> Mods, int StunImmunityMs, int SlowFloorPct = 0, bool NoKnockback = false);

    static Dictionary<string, Row> Rows = default!;

    /// <summary>Whether the variety's row names this mod in either of its mod lists.</summary>
    public static bool Has(MonsterCatalog.Variety? variety, string mod) =>
        variety is not null && Rows.GetValueOrDefault(variety.Id)?.Mods.Contains(mod) == true;

    /// <summary>Whether nothing may shove the monster: the client's ImmuneToKnockback component, the mod of that name
    /// (the answer without the Stats table), or any mod on its own row whose stats carry
    /// <c>cannot_be_knocked_back</c> under another id.</summary>
    public static bool KnockbackImmune(MonsterCatalog.Variety? variety) =>
        variety?.ComponentsOrBase.Contains(ImmuneToKnockback) == true || Has(variety, ImmuneToKnockback)
        || variety is not null && Rows.GetValueOrDefault(variety.Id) is { NoKnockback: true };

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

    /// <summary>Whether the variety carries any <c>MonsterActBoss*</c> species mod (Acts 1–10 Act Bosses).</summary>
    public static bool IsActBoss(MonsterCatalog.Variety? variety)
    {
        if (variety is null || !Rows.TryGetValue(variety.Id, out var row)) return false;
        foreach (string mod in row.Mods)
            if (mod.StartsWith("MonsterActBoss", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Whether the variety carries any <c>MonsterQuestBoss*</c> species mod (campaign quest bosses).</summary>
    public static bool IsQuestBoss(MonsterCatalog.Variety? variety)
    {
        if (variety is null || !Rows.TryGetValue(variety.Id, out var row)) return false;
        foreach (string mod in row.Mods)
            if (mod.StartsWith("MonsterQuestBoss", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Fraction of max life restored to this monster when a nearby player dies: 10% for
    /// <see cref="HealOnPlayerDeath"/>, 5% for <see cref="HealOnPlayerDeathMinor"/>, or 0 otherwise.</summary>
    public static double PlayerDeathHealFraction(MonsterCatalog.Variety? variety) =>
        Has(variety, HealOnPlayerDeath) ? 0.10
        : Has(variety, HealOnPlayerDeathMinor) ? 0.05
        : 0;

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
    static readonly string[] StatMaxColumns = ["Stat1Max", "Stat2Max", "Stat3Max", "Stat4Max", "Stat5Max", "Stat6Max", "Stat7Max", "Stat8Max"];
    static readonly string[] VarietyModColumns = ["ModsKeys", "Special_ModsKeys", "Part1_ModsKeys", "Part2_ModsKeys", "Endgame_ModsKeys"];

    /// <summary>From the MonsterVarieties, Mods and (for the stat values) Stats client tables: each variety's mod ids, how
    /// long it cannot be stunned after a stun, and the floor under its action speed, both its own keys into Mods.</summary>
    public static Dictionary<string, Row> Parse(ClientTable varieties, ClientTable mods, ClientTable? stats)
    {
        int statCount = stats?.Count ?? 0;
        // Only inspect the ~hundreds of Mods rows actually referenced by MonsterVarieties instead of all 35k+ rows.
        var modInfo = new Dictionary<int, (string Id, int StunMs, int SlowFloor, bool NoKnockback)>();
        (string Id, int StunMs, int SlowFloor, bool NoKnockback) ReadMod(int row)
        {
            if (modInfo.TryGetValue(row, out var cached)) return cached;
            var mod = mods[row];
            string id = mod.TextOrNull("Id") ?? "";
            int stunMs = 0, slowFloor = 0;
            bool noKnockback = false;
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
                    // A flag stat: the rows that carry it state 1 in both bounds.
                    else if (statId == KnockbackImmuneStat && (mod.IntOrNull(StatMaxColumns[slot]) ?? 0) > 0)
                        noKnockback = true;
                }
            }
            return modInfo[row] = (id, stunMs, slowFloor, noKnockback);
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
            bool noKnockback = false;
            for (int c = 0; c < VarietyModColumns.Length; c++)
                foreach (int row in variety.Ints(VarietyModColumns[c]))
                {
                    if ((uint)row >= (uint)modCount) continue;
                    var info = ReadMod(row);
                    if (info.Id.Length == 0) continue;
                    (own ??= new HashSet<string>(StringComparer.Ordinal)).Add(info.Id);
                    if (info.StunMs > stunMs) stunMs = info.StunMs;
                    if (info.SlowFloor < slowFloor) slowFloor = info.SlowFloor;
                    noKnockback |= info.NoKnockback;
                }
            if (own is not null) result[id] = new Row(own, stunMs, slowFloor, noKnockback);
        }
        return result;
    }
}
