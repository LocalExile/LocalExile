using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const int TurtleBoon = 6861;
    const ushort TurtleBoonBuff = 2376;
    sealed record SpectreBoon(double Until);
    static readonly RuleTable<GolemState.Golem, SpectreBoon> SpectreBoons = new();

    static void AddSpectreOwnerBenefits(Player player, Dictionary<uint, int> stats, GolemState.Golem spectre)
    {
        // The owner effects are visible in the high ItemisedCorpse descriptions and the corresponding native buff templates.
        (uint key, int value) = spectre.Monster.Variety?.Row switch
        {
            10207 => (Stat.MaimEffectPlusPct, 10),
            10210 => (Stat.PhysicalDamagePctToAddAsFire, 5),
            10248 => (Stat.CombinedAttackPhysicalDamagePlusPctFinal, 5),
            10260 => (Stat.WitheredEffectPlusPct, 10),
            10265 => (Stat.BaseAdditionalPhysicalDamageReductionPct, 3),
            10266 => (Stat.BaseAdditionalPhysicalDamageReductionPct, 5),
            _ => (0u, 0),
        };
        if (key != 0) stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
        if (!SpectreBoons.TryGetValue(spectre, out var boon)) return;
        double remaining = boon.Until - AreaTime(player);
        if (remaining <= 0) return;
        stats[Stat.BaseCannotBeDamaged] = 1;
        StatsChangeIn(remaining);
    }

    static bool TryReserveTurtleBoonLocked(Player player, GolemState.Golem spectre, (int X, int Y) here,
        (int X, int Y) owner, double now)
    {
        if (player.PlayerLife >= player.PlayerUnreservedLife || player.PlayerLife == 0
            || spectre.Abilities.FirstOrDefault(a => a.Effect == TurtleBoon) is not { } boon
            || Dist(here, owner) > boon.Attack.Range || !GolemReady(spectre, boon, now)) return false;
        GolemReserveLocked(player, spectre, boon, player.Id, null, owner, now);
        return true;
    }

    static void GrantTurtleBoonLocked(Player player, GolemState.Golem spectre, GolemState.Ability boon, double now)
    {
        int ms = GolemStat(boon.Stats, Stat.BaseSkillEffectDuration);
        if (player.PlayerLife == 0 || ms <= 0 || !GolemCurrent(player, spectre)
            || Dist(spectre.Monster.Walk.Position(now), player.Walk.Position(now)) > boon.Attack.Range) return;
        double seconds = ms / 1000d;
        var grant = new SpectreBoon(now + seconds);
        SpectreBoons.AddOrUpdate(spectre, grant);
        var area = player.Area;
        area.Buffs.Set(player.Id, new(TurtleBoonBuff, spectre.Id, U68: Buff.Seconds(seconds)));
        MarkPlayerStats(player, PlayerStatStep.Sources);
        PresentStats(player);
        area.Timers.After(area.Frame, (long)Math.Ceiling(seconds / FrameSeconds), _ =>
        {
            if (ReferenceEquals(area, player.Area) && SpectreBoons.TryGetValue(spectre, out var current) && current == grant)
                ClearTurtleBoonLocked(player, spectre);
        }, source: CombatLifetime);
    }

    static void ClearTurtleBoonLocked(Player player, GolemState.Golem spectre)
    {
        if (!SpectreBoons.Remove(spectre)) return;
        player.Area.Buffs.Clear(player.Id, TurtleBoonBuff, spectre.Id);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        PresentStats(player);
    }
}
