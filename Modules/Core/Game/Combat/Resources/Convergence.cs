using GameServer.Core.Protocol.Instance;

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
        if (target.Rarity != MonsterRarity.Unique || player.PlayerLife == 0) return;
        var area = player.Area;
        if (ConvergenceWindows.TryGetValue(player, out var window) && ReferenceEquals(window.Area, area)
            && now < window.NextGain) return;
        stats ??= CharacterPlayerStats(player);
        if (stats.GetValueOrDefault(Stat.GainConvergenceOnHittingUniqueEnemy) == 0) return;

        ConvergenceWindows.AddOrUpdate(player, new(area, now + ConvergenceSeconds, now + ConvergenceCooldown));
        area.Buffs.Set(player.Id, new(ConvergenceBuffRow, player.Id, Values: [ConvergenceDamageMore],
            U68: Buff.Seconds(ConvergenceSeconds)));
        MarkPlayerStats(player, PlayerStatStep.PanelDefaults);
    }

    static void ApplyConvergenceStats(Player player, StatMap stats)
    {
        int increasedArea = stats.GetValueOrDefault(Stat.AreaOfEffectPlusPctWhileYouDoNotHaveConvergence);
        if (increasedArea == 0 && stats.GetValueOrDefault(Stat.GainConvergenceOnHittingUniqueEnemy) == 0) return;
        if (player.PlayerLife != 0 && ConvergenceWindows.TryGetValue(player, out var window)
            && ReferenceEquals(window.Area, player.Area) && ProjectileWorldTime(player) < window.Until)
        {
            StatsChangeAt(window.Until, () => ProjectileWorldTime(player));
            stats[Stat.HaveConvergence] = 1;
            stats[Stat.ConvergenceElementalDamagePlusPctFinal] = ConvergenceDamageMore;
        }
        else if (increasedArea != 0)
        {
            stats[Stat.BaseSkillAreaOfEffectPlusPct] = ActorScale.Saturate(
                (long)stats.GetValueOrDefault(Stat.BaseSkillAreaOfEffectPlusPct) + increasedArea);
        }
    }

    static void ClearConvergence(Player player)
    {
        if (!ConvergenceWindows.Remove(player)) return;
        player.Area.Buffs.Clear(player.Id, ConvergenceBuffRow, player.Id);
        MarkPlayerStats(player, PlayerStatStep.PanelDefaults);
    }
}
