using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    public static bool PlayerAlwaysHits(Player player) => Stat.GlobalAlwaysHit is var key
        && CharacterPlayerStats(player).GetValueOrDefault(key) != 0;

    public static Dictionary<uint, int> WithWardBreak(Player player, Dictionary<uint, int> sources,
        IReadOnlyList<(byte Row, InventoryItem Item)>? equipment = null, IReadOnlySet<uint>? inactive = null)
    {
        bool flameTouched = FlameTouchedActive(player, equipment, inactive);
        if (flameTouched) sources[Stat.AffectedByTouchOfFire] = 1;
        var state = player.WardBreak;
        double now = AreaTime(player);
        bool timedAdrenaline = sources.GetValueOrDefault(AdrenalineSource(state)) > 0
            && now < state.TimedAdrenalineUntil && StatsChangeInAnd(state.TimedAdrenalineUntil - now);
        if (player.PlayerLife > 0 && (timedAdrenaline || flameTouched && state.SustainedAdrenaline))
            foreach (var (key, value) in WardBreakEffects.AdrenalineStats)
                sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return sources;
    }

    static uint AdrenalineSource(WardBreakState state) => state.TimedAdrenalineSource is > 0 and var source
        ? source : WardBreakEffects.AdrenalineSource;

    // The sources as the stats give them (PlayerStatsUpdated): a lost source or death ends its effect.
    static void ObserveWardBreak(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        player.WardBreak.Observe(AreaTime(player), Get(AdrenalineSource(player.WardBreak)) > 0,
            Get(WardBreakEffects.StunRecoverySource) > 0, player.PlayerLife > 0 && !player.RageRetired);
    }

    public static void EnsureWardBreakPools(Player player)
    {
        ReconcileFlameTouched(player, AreaTime(player));
        bool active = player.PlayerLife > 0 && !player.RageRetired
            && player.WardBreak.Adrenaline(AreaTime(player));
        if (player.AppliedAdrenaline != active)
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
        }
    }

    // Called under the combat lock after block/mitigation. Ward absorbs once, before ES, and never absorbs DoT; mana takes
    // its share of what is left before Life. types: the hit's damage by type (only its proportions count).
    public static int DamagePlayerHit(Player player, int nonChaos, int chaos, double now,
        IReadOnlyDictionary<uint, int> stats, DamagePacket types) => DamagePlayerHitTyped(player, 0, nonChaos, chaos, now, stats, types);

    public static void ClearWardBreakOnDeath(Player player)
    {
        if (player.PlayerLife != 0) return;
        if (player.ResourceEffectsState is ResourceEffectsState resources) RetireFlameTouched(player, resources);
        player.WardBreak.Clear(); player.Ward.SetMaximum(0);
    }

    public static byte[]? WardBreakPresentation(Player player)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired) return null;
        EnsureWardBreakPools(player);
        player.Ward.Advance(AreaTime(player));
        var ward = (player.Ward.Maximum, player.Ward.Broken);
        double adrenaline = player.PlayerLife > 0 && player.WardBreak.Adrenaline(AreaTime(player))
            ? player.WardBreak.AdrenalineUntil : double.NegativeInfinity;
        double recovery = player.WardBreak.RecoveryUntil(AreaTime(player));
        if (player.SentWard == ward && player.SentAdrenalineUntil == adrenaline
            && player.SentStunRecoveryUntil == recovery)
            return null;
        // The first area tick can reuse the entry baseline when Ward, Adrenaline and Stun Recovery are inactive.
        if (player.SentWard is null && ward == (0u, false)
            && double.IsNegativeInfinity(adrenaline) && double.IsNegativeInfinity(recovery))
        {
            player.SentWard = ward; player.SentAdrenalineUntil = adrenaline;
            player.SentStunRecoveryUntil = recovery;
            return null;
        }
        var bytes = new List<byte>();
        var buffs = player.Area.Buffs;
        if (ward.Maximum > 0 && ward.Broken) buffs.Set(player.Id, new(WardBreakEffects.WardBrokenDefinition, player.Id));
        else buffs.Clear(player.Id, WardBreakEffects.WardBrokenDefinition, player.Id);
        if (adrenaline > double.NegativeInfinity)
            buffs.Set(player.Id, new(WardBreakEffects.AdrenalineDefinition, player.Id,
                U68: double.IsFinite(adrenaline) ? Buff.Seconds(adrenaline - AreaTime(player)) : null));
        else buffs.Clear(player.Id, WardBreakEffects.AdrenalineDefinition, player.Id);
        bytes.AddRange(PlayerStatsOffFrames(player));
        // The native client predicts regeneration between corrections. Removing the rate alone leaves
        // its predicted overshoot in place when the server has no further recovery tick to publish.
        player.Frames.Area?.Life.Resend(player);
        bytes.AddRange(PlayerSkillsIfChanged(player, PlayerSkills(player)));
        player.SentWard = ward; player.SentAdrenalineUntil = adrenaline;
        player.SentStunRecoveryUntil = recovery;
        Console.WriteLine($"[gateway] Ward {player.Ward.Current}/{player.Ward.Maximum}, "
            + $"broken={player.Ward.Broken}, Adrenaline={adrenaline > double.NegativeInfinity}");
        return [.. bytes];
    }

    public static Task SendWardBreak(Player player) =>
        Send(player, [], null, atGate: () => WardBreakPresentation(player));

    public sealed record WardBreakTransfer(string Character, DateTime At, WardBreakState.Carried Buffs,
        bool Broken, double WardSeconds, (double Remaining, int Regeneration) Cry);
    public static readonly ConcurrentDictionary<uint, WardBreakTransfer> CarriedWardBreak = new();

    public static void CarryWardBreak(Player player, uint ticket)
    {
        if (player.Closed || player.RageRetired) return;
        if (player.PlayerLife == 0) ClearWardBreakOnDeath(player);
        double now = AreaTime(player);
        player.Ward.Advance(now);
        foreach (var (old, entry) in CarriedWardBreak)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedWardBreak.TryRemove(old, out _);
        CarriedWardBreak.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow,
            player.WardBreak.Carry(now), player.Ward.Broken, Math.Max(0, player.Ward.RestoresAt - now),
            CarryEnduringCry(player, now)));
    }

    public static void RestoreWardBreak(Player player)
    {
        if (!CarriedWardBreak.TryGetValue(player.Ticket, out var carry)
            || carry.Character != CharacterName(player)
            || !CarriedWardBreak.TryRemove(new KeyValuePair<uint, WardBreakTransfer>(player.Ticket, carry))) return;
        if (player.PlayerLife == 0 || DateTime.UtcNow - carry.At > TimeSpan.FromMinutes(5)) return;
        double now = AreaTime(player);
        player.WardBreak.Restore(carry.Buffs, now);
        RestoreEnduringCry(player, now, carry.Cry);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.Ward.RestoreCarried(carry.Broken, carry.WardSeconds, now);
        player.ApplyMovementSpeed();
    }
}
