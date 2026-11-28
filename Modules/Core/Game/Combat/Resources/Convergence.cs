using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort ConvergenceBuffRow = 1699;
    const int ConvergenceDamageMore = 30;
    const double ConvergenceSeconds = 4, ConvergenceCooldown = 8;

    sealed record ConvergenceWindow(GameArea Area, double Until, double NextGain);

    static readonly RuleTable<Player, ConvergenceWindow> ConvergenceWindows = new();

    static void GainConvergenceOnHitLocked(Player player, SpawnedMonster target, double now,
        IReadOnlyDictionary<uint, int>? stats)
    {
        if (target.Rarity != MonsterRarity.Unique || !target.Hostile || player.PlayerLife == 0
            || player.Closed || player.LeavingArea || player.Frames.Area is not { } area) return;
        if (ConvergenceWindows.TryGetValue(player, out var window) && ReferenceEquals(window.Area, area)
            && now < window.NextGain) return;
        stats ??= PlayerStatValues(player);
        if (stats.GetValueOrDefault(Stat.GainConvergenceOnHittingUniqueEnemy) == 0) return;

        ConvergenceWindows.AddOrUpdate(player, new(area, now + ConvergenceSeconds, now + ConvergenceCooldown));
        area.Buffs.Set(player.Id, BuffRows.Spec(ConvergenceBuffRow, player.Id, null, ConvergenceSeconds));
        MarkPlayerStats(player, PlayerStatStep.PanelDefaults);
    }

    static void ApplyConvergenceStats(Player player, StatMap stats)
    {
        int increasedArea = stats.GetValueOrDefault(Stat.AreaOfEffectPlusPctWhileYouDoNotHaveConvergence);
        if (increasedArea == 0 && stats.GetValueOrDefault(Stat.GainConvergenceOnHittingUniqueEnemy) == 0) return;
        if (player.PlayerLife != 0 && ConvergenceWindows.TryGetValue(player, out var window)
            && ReferenceEquals(window.Area, player.Frames.Area ?? player.Area) && ProjectileWorldTime(player) < window.Until)
        {
            StatsChangeAt(window.Until, () => ProjectileWorldTime(player));
            stats[Stat.HaveConvergence] = 1;
            stats[Stat.ConvergenceElementalDamagePlusPctFinal] = ConvergenceDamageMore;
            int combined = stats.GetValueOrDefault(Stat.CombinedElementalDamagePlusPctFinal);
            stats[Stat.CombinedElementalDamagePlusPctFinal] =
                ActorScale.Saturate((Math.Max(0, 100L + combined) * (100 + ConvergenceDamageMore)) / 100 - 100);
        }
        else if (increasedArea != 0)
        {
            stats[Stat.BaseSkillAreaOfEffectPlusPct] = ActorScale.Saturate(
                (long)stats.GetValueOrDefault(Stat.BaseSkillAreaOfEffectPlusPct) + increasedArea);
        }
    }

    const ushort RetributionConfluxBuffRow = 626;
    const double RetributionConfluxSeconds = 4;
    sealed record RetributionConfluxWindow(GameArea Area, double Until);
    static readonly RuleTable<Player, RetributionConfluxWindow> RetributionConflux = new();

    public static void GrantRetributionConflux(KillContext kill)
    {
        var player = kill.Player;
        if (!kill.PlayerKill || !kill.Monster.Hostile || player.PlayerLife == 0 || player.Closed || player.LeavingArea
            || player.Frames.Area is not { } area) return;
        int chance = PlayerStat(player, Stat.ElementalistAllDamageCausesChillShockAndIgniteFor4SecondsOnKillPct);
        if (!RandomRolls.Chance(chance, 100, AreaRng(player))) return;
        RetributionConflux.AddOrUpdate(player, new(area, ProjectileWorldTime(player) + RetributionConfluxSeconds));
        area.Buffs.Set(player.Id, BuffRows.Spec(RetributionConfluxBuffRow, player.Id, null, RetributionConfluxSeconds));
        MarkPlayerStats(player, PlayerStatStep.Sources);
    }

    static void AddRetributionConfluxStats(Player player, Dictionary<uint, int> stats)
    {
        if (!RetributionConflux.TryGetValue(player, out var window) || player.PlayerLife == 0
            || !ReferenceEquals(window.Area, player.Frames.Area) || ProjectileWorldTime(player) >= window.Until) return;
        StatsChangeAt(window.Until, () => ProjectileWorldTime(player));
        stats[Stat.AllDamageCanChillIgniteShock] = stats[Stat.AllDamageCanChill] = 1;
        stats[Stat.AllDamageCanIgnite] = stats[Stat.AllDamageCanShock] = 1;
        stats[Stat.BaseChanceToIgnitePct] = Math.Max(100, stats.GetValueOrDefault(Stat.BaseChanceToIgnitePct));
        stats[Stat.ChanceToShockPct] = Math.Max(100, stats.GetValueOrDefault(Stat.ChanceToShockPct));
    }

    static void ClearRetributionConflux(Player player)
    {
        if (!RetributionConflux.TryGetValue(player, out var window)) return;
        RetributionConflux.Remove(player);
        window.Area.Buffs.Clear(player.Id, RetributionConfluxBuffRow, player.Id);
        MarkPlayerStats(player, PlayerStatStep.Sources);
    }

    static void ClearConvergence(Player player)
    {
        if (!ConvergenceWindows.TryGetValue(player, out var window)) return;
        ConvergenceWindows.Remove(player);
        window.Area.Buffs.Clear(player.Id, ConvergenceBuffRow, player.Id);
        MarkPlayerStats(player, PlayerStatStep.PanelDefaults);
    }
}
