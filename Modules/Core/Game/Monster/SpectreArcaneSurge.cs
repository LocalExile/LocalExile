using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed record SpectreSurge(double Until, double ShownUntil);
    static readonly RuleTable<GolemState.Golem, SpectreSurge> SpectreSurges = new();

    static void GrantSpectreArcaneSurgeLocked(Player player, double now)
    {
        if (player.Summons.Fighters.Count == 0 || StatSourceValues(player).GetValueOrDefault(Stat.SpectresGainArcaneSurgeWhenYouDo) == 0)
            return;
        double until = now + ArcaneSurgeSeconds;
        foreach (var fighter in player.Summons.Fighters.Values)
        {
            if (fighter.Definition.Kind != "Spectre" || fighter.Monster.SummonerId is not null
                || fighter.Monster.Dead || fighter.Monster.Life == 0) continue;
            SpectreSurges.TryGetValue(fighter, out var previous);
            if (previous is not null && previous.Until >= until) continue;
            double shown = previous?.ShownUntil ?? 0;
            if (previous is null || previous.Until <= now || until - shown >= ArcaneSurgeRepresentSeconds)
            {
                player.Area.Buffs.Set(fighter.Id, new(ArcaneSurgeBuffRow, player.Id, U68: Buff.Seconds(ArcaneSurgeSeconds)));
                shown = until;
            }
            SpectreSurges.AddOrUpdate(fighter, new(until, shown));
            player.Summons.RefreshAt = 0;
        }
    }

    static bool SpectreArcaneSurgeActiveLocked(Player player, GolemState.Golem fighter, double now)
    {
        if (!SpectreSurges.TryGetValue(fighter, out var surge)) return false;
        if (now < surge.Until) return true;
        ClearSpectreArcaneSurgeLocked(player, fighter);
        return false;
    }

    static void ClearSpectreArcaneSurgeLocked(Player player, GolemState.Golem fighter)
    {
        if (SpectreSurges.Remove(fighter)) player.Area.Buffs.Clear(fighter.Id, ArcaneSurgeBuffRow, player.Id);
    }
}
