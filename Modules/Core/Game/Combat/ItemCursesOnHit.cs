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
        double now, bool mayTriggerMark = true)
    {
        foreach (var (stat, effect) in ItemCurseOnHitStats)
        {
            if (hitStats.GetValueOrDefault(stat) is not (int level and > 0)) continue;
            // An effect rule that is live for the stat applies the curse (EffectRules.LiveFor).
            if (EffectRules.LiveFor(player, stat)) continue;
            CurseOnHitLocked(player, target, effect, level, hitStats, now, 1);
        }
        if (mayTriggerMark && !ItemEffectRules.Enabled)
            TryTriggeredAssassinsMarkLocked(player, target, hitStats.GetValueOrDefault(Stat.CurseOnHitLevelAssassinsMark),
                now, hitStats, "AssassinsMarkTriggered");
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

    static void TryDeathmarkedLocked(Player player, SpawnedMonster target, double now, IReadOnlyDictionary<uint, int> stats) =>
        TryTriggeredAssassinsMarkLocked(player, target,
            stats.GetValueOrDefault(Stat.LocalDisplayTriggerLevelXAssassinsMarkWhenYouCriticallyHitRareOrUniqueEnemyWithAttacks),
            now, stats, "AssassinsMarkTriggeredAssassin");

    static void TryTriggeredAssassinsMarkLocked(Player player, SpawnedMonster target, int level, double now,
        IReadOnlyDictionary<uint, int> stats, string sourceEffect)
    {
        if (level <= 0 || !MayPaySkillCost(player)
            || target is not { Hostile: true, Dead: false, Life: > 0, Rarity: MonsterRarity.Rare or MonsterRarity.Unique }
            || ActiveMarkTarget(player, now) is not null) return;
        var skills = SkillsFor(player);
        int row = skills.EffectRow(sourceEffect);
        if (row < 0 || now < player.SkillReadyAt.GetValueOrDefault(row)
            || GemDisabled(player, row) is not null) return;
        var own = ItemCurseStats(skills, sourceEffect, level);
        if (own.Count == 0) return;
        var triggered = new SupportedGem(row, level, 0, [], own, Granted: true);
        var caster = FinalPlayerStats(player).ToDictionary(p => p.Item1, p => p.Item2);
        var paying = MergeSkillStats(caster, own, skills.IsChannelled(row), SkillCostMore(player, own),
            SkillCostScopeFor(skills, triggered));
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
