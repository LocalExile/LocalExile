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

    /// <summary>From the MonsterVarieties, Mods and (for the stat values) Stats client tables: each variety's mod ids, how
    /// long it cannot be stunned after a stun, and the floor under its action speed, both its own keys into Mods.</summary>
    public static Dictionary<string, Row> Parse(ClientTable varieties, ClientTable mods, ClientTable? stats)
    {
        var modIds = mods.Select(r => r.TextOrNull("Id") ?? "").ToList();
        var statNames = stats?.Select(r => r.TextOrNull("Id") ?? "").ToList();
        // Per mod: the repeat-stun immunity and the action-speed floor its stats give, if it has those stats at all.
        var modStunMs = new Dictionary<string, int>();
        var modSlowFloor = new Dictionary<string, int>();
        var modNoKnockback = new HashSet<string>();
        if (statNames is not null)
            foreach (var mod in mods)
            {
                string id = mod.TextOrNull("Id") ?? "";
                if (id.Length == 0) continue;
                for (int slot = 1; slot <= 8; slot++)
                {
                    if (mod.IntOrNull($"StatsKey{slot}") is not int key || key < 0 || key >= statNames.Count) continue;
                    if ((uint)key + 1 == StunImmunityStat)
                    {
                        int ms = Math.Max(0, mod.IntOrNull($"Stat{slot}Min") ?? 0);
                        if (ms > 0) modStunMs[id] = Math.Max(ms, modStunMs.GetValueOrDefault(id));
                    }
                    else if ((uint)key + 1 == SlowFloorStat)
                    {
                        int pct = Math.Min(0, mod.IntOrNull($"Stat{slot}Min") ?? 0);
                        modSlowFloor[id] = Math.Min(pct, modSlowFloor.GetValueOrDefault(id));
                    }
                    // A flag stat: the rows that carry it state 1 in both bounds.
                    else if ((uint)key + 1 == KnockbackImmuneStat && (mod.IntOrNull($"Stat{slot}Max") ?? 0) > 0)
                        modNoKnockback.Add(id);
                }
            }
        var result = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var variety in varieties)
        {
            string id = variety.TextOrNull("Id") ?? "";
            if (id.Length == 0) continue;
            var own = new HashSet<string>(StringComparer.Ordinal);
            int stunMs = 0;
            int slowFloor = 0;
            bool noKnockback = false;
            foreach (string keys in (string[])["ModsKeys", "Special_ModsKeys"])
                foreach (int row in variety.Ints(keys))
                {
                    if (row < 0 || row >= modIds.Count || modIds[row].Length == 0) continue;
                    own.Add(modIds[row]);
                    stunMs = Math.Max(stunMs, modStunMs.GetValueOrDefault(modIds[row]));
                    slowFloor = Math.Min(slowFloor, modSlowFloor.GetValueOrDefault(modIds[row]));
                    noKnockback |= modNoKnockback.Contains(modIds[row]);
                }
            if (own.Count > 0) result[id] = new Row(own, stunMs, slowFloor, noKnockback);
        }
        return result;
    }
}
