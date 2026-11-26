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
        TryTriggeredAssassinsMarkLocked(player, target, hitStats.GetValueOrDefault(Stat.CurseOnHitLevelAssassinsMark),
            now, hitStats, "AssassinsMarkTriggered");
    }

    static void TryDeathmarkedLocked(Player player, SpawnedMonster target, double now, IReadOnlyDictionary<uint, int> stats) =>
        TryTriggeredAssassinsMarkLocked(player, target,
            stats.GetValueOrDefault(Stat.LocalDisplayTriggerLevelXAssassinsMarkWhenYouCriticallyHitRareOrUniqueEnemyWithAttacks),
            now, stats, "AssassinsMarkTriggeredAssassin");

    static void TryTriggeredAssassinsMarkLocked(Player player, SpawnedMonster target, int level, double now,
        IReadOnlyDictionary<uint, int> stats, string sourceEffect)
    {
        if (level <= 0 || target is not { Hostile: true, Dead: false, Life: > 0, Rarity: MonsterRarity.Rare or MonsterRarity.Unique }
            || ActiveMarkTarget(player, now) is not null) return;
        var skills = SkillsFor(player);
        int row = skills.EffectRow(sourceEffect);
        if (row < 0 || now < player.SkillReadyAt.GetValueOrDefault(row)) return;
        var own = ItemCurseStats(skills, sourceEffect, level);
        if (own.Count == 0) return;
        var caster = FinalPlayerStats(player).ToDictionary(p => p.Item1, p => p.Item2);
        var paying = MergeSkillStats(caster, own, skills.IsChannelled(row), SkillCostMore(player, own),
            CostEfficiencyFor(skills, row));
        if (!SkillCostLocked(player, row, own, paying, out uint mana, out int life, out uint shield,
                out uint fromShield, out int rage)
            || !PayResolvedSkillCostLocked(player, paying, mana, life, shield, fromShield, rage,
                null, out uint manaPaid, out uint lifePaid)) return;
        int cooldown = skills.CooldownMs(row, level);
        if (cooldown > 0)
        {
            double rate = Math.Max(0, 1 + paying.GetValueOrDefault(Stat.VirtualCooldownSpeedPlusPct) / 100d);
            player.SkillReadyAt[row] = rate > 0 ? now + cooldown / 1000d / rate : double.PositiveInfinity;
        }
        if (lifePaid > 0) PresentStats(player);
        if (lifePaid > 0 || manaPaid > 0)
        {
            player.Frames.Area?.Life.Resend(player);
            if (player.ProjectileRuntime is { Active: true } session)
                session.Enqueue(() => SendSkillCost(player, lifePaid, "Triggered Assassin's Mark cost"));
        }
        ApplyCurseDataLocked(player, target, 0, "AssassinsMark", own, now, false, stats);
    }

    static void ApplyItemCurseLocked(Player player, SpawnedMonster target, string effect, int level, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (level <= 0) return;
        if (target.HasCurses && target.Curses.All.TryGetValue((player.Id, effect), out var held)
            && now < held.Until && now - held.Applied < 1) return;
        var curse = ItemCurseStats(SkillsFor(player), effect, level);
        if (curse.Count != 0) ApplyCurseDataLocked(player, target, 0, effect, curse, now, false, stats);
    }

    static IReadOnlyDictionary<uint, int> ItemCurseStats(SkillCatalog skills, string effect, int level) =>
        ItemCurseOnHitGemStats.GetValue(skills, static _ => new()).GetOrAdd((effect, level),
            static IReadOnlyDictionary<uint, int> (key, catalog) => catalog.EffectRow(key.Effect) is int row and >= 0
                ? catalog.EffectStats(row, key.Level).ToDictionary(p => p.Item1, p => p.Item2) : new Dictionary<uint, int>(), skills);
}
