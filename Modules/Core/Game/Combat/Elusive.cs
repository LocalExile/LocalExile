using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // All consumers share the source-qualified owner, including the whole-hit avoidance roll.
    public static double ElusiveFactor(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !Elusives.TryGetValue(player, out var state) || !ReferenceEquals(state.Rules, CombatLifetime)
            || state.Epoch != player.ProjectileEpoch) return 0;
        return state.Effect(ResourceEffectsTime(player)) / 100;
    }

    /// <summary>The effect's chance to avoid damage from hits, 15% at 100% effect, for the server's avoidance roll.</summary>
    public static int ElusiveAvoidPct(Player player) => (int)Math.Floor(15 * ElusiveFactor(player) + 1e-9);

    /// <summary>The effect's movement speed, 30% at 100% effect: the pools add it while the buff runs.</summary>
    public static int ElusiveMovementPct(Player player) => (int)Math.Floor(30 * ElusiveFactor(player) + 1e-9);

    /// <summary>The percentage the buff icon shows: the opening effect less one 5-point step every quarter
    /// second (the game's buff display ticks 20 points a second in 5-point steps). The server's avoidance and
    /// movement use the continuous <see cref="ElusiveFactor"/> instead.</summary>
    public static int ElusiveDisplayPercent(Player player)
    {
        if (ElusiveFactor(player) <= 0) return 0;
        var state = ElusiveOf(player);
        double now = ResourceEffectsTime(player), quarters = state.Age(now) * 4;
        return (int)Math.Max(0, Math.Round(state.Effect(now - (quarters - Math.Floor(quarters)) / 4)));
    }
    public static void GainElusiveOnCrit(Player player)
    {
        CharacterStatSources(player);
        TryGainElusiveLocked(player, critical: true, kill: false, ResourceEffectsTime(player));
    }
    public static Task SyncElusive(Player player) =>
        SendElusive(player);
}
