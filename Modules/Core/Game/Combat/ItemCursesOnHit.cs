using System.Collections.Concurrent;

public static partial class Game
{
    // "Curse Enemies with <Hex> on Hit" (the influenced glove and ring suffixes): the stat's value is the level of the curse
    // gem it applies, through the same owner a cast curse uses (curse limit, hexproof). The marks' "Trigger Level # Mark
    // when you Hit a Rare or Unique Enemy" stats are local to their item and need a triggered skill instead.
    static readonly (uint Stat, string Effect)[] ItemCurseOnHitStats =
    [
        (Stat.CurseOnHitLevelDespair, "Despair"), (Stat.CurseOnHitLevelElementalWeakness, "ElementalWeakness"),
        (Stat.CurseOnHitLevelFlammability, "Flammability"), (Stat.CurseOnHitLevelFrostbite, "Frostbite"),
        (Stat.CurseOnHitLevelConductivity, "Conductivity"), (Stat.CurseOnHitLevelVulnerability, "Vulnerability"),
    ];

    static readonly RuleTable<SkillCatalog, ConcurrentDictionary<(string Effect, int Level), IReadOnlyDictionary<uint, int>>>
        ItemCurseOnHitGemStats = new();

    // In the area's frame, with the other on-hit curses. Each hit reapplies a hex for its full duration, at most once a
    // second so a fast attacker does not re-send the buff on every hit.
    internal static void ItemCursesOnHitLocked(Player player, SpawnedMonster target, IReadOnlyDictionary<uint, int> hitStats,
        double now)
    {
        foreach (var (stat, effect) in ItemCurseOnHitStats)
        {
            if (hitStats.GetValueOrDefault(stat) is not (int level and > 0)) continue;
            // An effect rule that is live for the stat applies the curse (EffectRules.LiveFor).
            if (EffectRules.LiveFor(player, stat)) continue;
            CurseOnHitLocked(player, target, effect, level, hitStats, now, 1);
        }
    }

    /// <summary>Whether a curse (a hex, not a mark) is running on the monster: "against Cursed Enemies".</summary>
    public static bool CursedNow(SpawnedMonster m, double now) => m.Curses.All.Count != 0 && m.Curses.All.Values.Any(c => !c.Mark && c.Until > now);

    static readonly string[] RandomHexes = [.. CurseCatalog.All.Where(p => !p.Value.Mark).Select(p => p.Key).Order(StringComparer.Ordinal)];

    /// <summary>The hex an action names, or one of all hexes for "random".</summary>
    internal static string HexNamed(string? curse, Random random) =>
        curse == "random" ? RandomHexes[random.Next(RandomHexes.Length)] : curse ?? "";

    /// <summary>A hit curses its monster with a hex at a gem level, as a cast of it would (curse limit, hexproof): what
    /// "Curse Enemies with X on Hit" does, and the effect rules' "curse". <paramref name="resend"/>: a hit within this
    /// many seconds of the last application leaves the running curse alone (0: every hit applies it again).</summary>
    internal static void CurseOnHitLocked(Player player, SpawnedMonster target, string effect, int level,
        IReadOnlyDictionary<uint, int> hitStats, double now, double resend, bool ignoreCurseLimit = false)
    {
        if (level <= 0 || target.Dead || target.Life == 0 || player.Reservations.Secondary) return;
        if (resend > 0 && target.Curses.All.TryGetValue((player.Id, effect), out var held) && now < held.Until && now - held.Applied < resend) return;
        var curse = ItemCurseOnHitGemStats.GetValue(SkillsFor(player), static _ => new()).GetOrAdd((effect, level),
            static IReadOnlyDictionary<uint, int> (key, catalog) => catalog.EffectRow(key.Effect) is int row and >= 0
                ? catalog.EffectStats(row, key.Level).ToDictionary(p => p.Item1, p => p.Item2) : new Dictionary<uint, int>(),
            SkillsFor(player));
        // ignoreCurseLimit: the hex takes a place of its own beyond the caster's limit (the capacity a curse can carry).
        if (curse.Count != 0) ApplyCurseDataLocked(player, target, 0, effect, curse, now, false, hitStats,
            additionalCapacity: ignoreCurseLimit ? EffectLimits.Unlimited : 0);
    }
}
