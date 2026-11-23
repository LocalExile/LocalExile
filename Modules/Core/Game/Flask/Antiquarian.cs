using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    public static bool PlayerAlwaysHits(Player player) => Stat.GlobalAlwaysHit is var key
        && CharacterPlayerStats(player).GetValueOrDefault(key) != 0;

    public static Dictionary<uint, int> WithAntiquarian(Player player, Dictionary<uint, int> sources)
    {
        int Get(uint key) => sources.GetValueOrDefault(key);
        double now = AreaTime(player);
        if (Get(AntiquarianEffects.AdrenalineSource) > 0 && player.PlayerLife > 0 && player.Antiquarian.Adrenaline(now)
            && StatsChangeInAnd(player.Antiquarian.AdrenalineUntil - now))
            foreach (var (key, value) in AntiquarianEffects.AdrenalineStats)
                sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return sources;
    }

    // The sources as the stats give them (PlayerStatsUpdated): a lost source or death ends its effect.
    static void ObserveAntiquarian(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        player.Antiquarian.Observe(AreaTime(player), Get(AntiquarianEffects.AdrenalineSource) > 0,
            Get(AntiquarianEffects.StunRecoverySource) > 0, player.PlayerLife > 0 && !player.RageRetired);
    }

    public static void EnsureAntiquarianPools(Player player)
    {
        bool active = player.PlayerLife > 0 && !player.RageRetired
            && player.Antiquarian.Adrenaline(AreaTime(player));
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

    public static void ClearAntiquarianOnDeath(Player player)
    {
        if (player.PlayerLife != 0) return;
        player.Antiquarian.Clear(); player.EnduringCryRecovery.Clear(); player.Ward.SetMaximum(0);
    }

    public static byte[]? AntiquarianPresentation(Player player)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired) return null;
        EnsureAntiquarianPools(player);
        player.Ward.Advance(AreaTime(player));
        var ward = (player.Ward.Maximum, player.Ward.Broken);
        double adrenaline = player.PlayerLife > 0 && player.Antiquarian.Adrenaline(AreaTime(player))
            ? player.Antiquarian.AdrenalineUntil : double.NegativeInfinity;
        double recovery = player.Antiquarian.RecoveryUntil(AreaTime(player));
        double cry = player.EnduringCryRecovery.Rate(AreaTime(player)) > 0
            ? player.EnduringCryRecovery.Until : double.NegativeInfinity;
        if (player.SentWard == ward && player.SentAdrenalineUntil == adrenaline
            && player.SentStunRecoveryUntil == recovery && player.SentEnduringCryUntil == cry)
            return null;
        // First area tick with no Ward, Adrenaline, Stun Recovery or Enduring Cry active: baseline already sent at area entry.
        if (player.SentWard is null && ward == (0u, false)
            && !double.IsFinite(adrenaline) && !double.IsFinite(recovery) && !double.IsFinite(cry))
        {
            player.SentWard = ward; player.SentAdrenalineUntil = adrenaline;
            player.SentStunRecoveryUntil = recovery; player.SentEnduringCryUntil = cry;
            return null;
        }
        var bytes = new List<byte>();
        var buffs = player.Area.Buffs;
        if (ward.Maximum > 0 && ward.Broken) buffs.Set(player.Id, new(AntiquarianEffects.WardBrokenDefinition, player.Id));
        else buffs.Clear(player.Id, AntiquarianEffects.WardBrokenDefinition, player.Id);
        if (double.IsFinite(adrenaline))
            buffs.Set(player.Id, new(AntiquarianEffects.AdrenalineDefinition, player.Id, U68: Buff.Seconds(adrenaline - AreaTime(player))));
        else buffs.Clear(player.Id, AntiquarianEffects.AdrenalineDefinition, player.Id);
        if (double.IsFinite(cry)) buffs.Set(player.Id, new(1541, player.Id, U68: Buff.Seconds(cry - AreaTime(player))));
        else buffs.Clear(player.Id, 1541, player.Id);
        bytes.AddRange(PlayerStatsOffFrames(player));
        // The native client predicts regeneration between corrections. Removing the rate alone leaves
        // its predicted overshoot in place when the server has no further recovery tick to publish.
        player.Frames.Area?.Life.Resend(player);
        bytes.AddRange(PlayerSkillsIfChanged(player, PlayerSkills(player)));
        player.SentWard = ward; player.SentAdrenalineUntil = adrenaline;
        player.SentStunRecoveryUntil = recovery;
        player.SentEnduringCryUntil = cry;
        Console.WriteLine($"[gateway] Antiquarian Ward {player.Ward.Current}/{player.Ward.Maximum}, "
            + $"broken={player.Ward.Broken}, Adrenaline={double.IsFinite(adrenaline)}");
        return [.. bytes];
    }

    public static Task SendAntiquarian(Player player) =>
        Send(player, [], null, atGate: () => AntiquarianPresentation(player));

    public sealed record AntiquarianTransfer(string Character, DateTime At, AntiquarianCombatState.Carried Buffs,
        bool Broken, double WardSeconds, TimedLifeRecovery.Carried Cry);
    public static readonly ConcurrentDictionary<uint, AntiquarianTransfer> CarriedAntiquarian = new();

    public static void CarryAntiquarian(Player player, uint ticket)
    {
        if (player.Closed || player.RageRetired) return;
        if (player.PlayerLife == 0) ClearAntiquarianOnDeath(player);
        double now = AreaTime(player);
        player.Ward.Advance(now);
        foreach (var (old, entry) in CarriedAntiquarian)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedAntiquarian.TryRemove(old, out _);
        CarriedAntiquarian.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow,
            player.Antiquarian.Carry(now), player.Ward.Broken, Math.Max(0, player.Ward.RestoresAt - now),
            player.EnduringCryRecovery.Carry(now)));
    }

    public static void RestoreAntiquarian(Player player)
    {
        if (!CarriedAntiquarian.TryGetValue(player.Ticket, out var carry)
            || carry.Character != CharacterName(player)
            || !CarriedAntiquarian.TryRemove(new KeyValuePair<uint, AntiquarianTransfer>(player.Ticket, carry))) return;
        if (player.PlayerLife == 0 || DateTime.UtcNow - carry.At > TimeSpan.FromMinutes(5)) return;
        double now = AreaTime(player);
        player.Antiquarian.Restore(carry.Buffs, now);
        player.EnduringCryRecovery.Restore(carry.Cry, now);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.Ward.RestoreCarried(carry.Broken, carry.WardSeconds, now);
        player.ApplyMovementSpeed();
    }
}
