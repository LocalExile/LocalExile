using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // "Curse Enemies with <Hex> on Hit" (the influenced glove and ring suffixes): the stat's value is the level of the curse
    // gem it applies, through the same owner a cast curse uses (curse limit, hexproof).
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
            ApplyItemCurseLocked(player, target, effect, hitStats.GetValueOrDefault(stat), now, hitStats);
        TryTriggeredAssassinsMarkLocked(player, target, hitStats.GetValueOrDefault(Stat.CurseOnHitLevelAssassinsMark), now, hitStats);
    }

    static void TryTriggeredAssassinsMarkLocked(Player player, SpawnedMonster target, int level, double now,
        IReadOnlyDictionary<uint, int> stats, string sourceEffect = "AssassinsMark")
    {
        if (level <= 0 || target is not { Hostile: true, Dead: false, Life: > 0, Rarity: MonsterRarity.Rare or MonsterRarity.Unique }
            || ActiveMarkTarget(player, now) is not null) return;
        ApplyItemCurseLocked(player, target, "AssassinsMark", level, now, stats, sourceEffect);
    }

    static void ApplyItemCurseLocked(Player player, SpawnedMonster target, string effect, int level, double now,
        IReadOnlyDictionary<uint, int> stats, string? sourceEffect = null)
    {
        if (level <= 0) return;
        if (target.HasCurses && target.Curses.All.TryGetValue((player.Id, effect), out var held)
            && now < held.Until && now - held.Applied < 1) return;
        var skills = SkillsFor(player);
        var curse = ItemCurseOnHitGemStats.GetValue(skills, static _ => new()).GetOrAdd((sourceEffect ?? effect, level),
            static IReadOnlyDictionary<uint, int> (key, catalog) => catalog.EffectRow(key.Effect) is int row and >= 0
                ? catalog.EffectStats(row, key.Level).ToDictionary(p => p.Item1, p => p.Item2) : new Dictionary<uint, int>(), skills);
        if (curse.Count != 0) ApplyCurseDataLocked(player, target, 0, effect, curse, now, false, stats);
    }
}
