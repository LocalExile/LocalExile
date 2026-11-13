using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // BuffDefinitions' "elusive" row: 15% chance to avoid damage from hits and 30% increased movement speed at
    // 100% effect, losing 20% of that effect per second.
    static readonly Lazy<ushort> ElusiveBuffRow = new(() =>
    {
        if (!ClientTables.Has("BuffDefinitions")) return (ushort)1374;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == "elusive") return (ushort)row.Index;
        return (ushort)1374;
    });

    /// <summary>The Elusive effect at the connection's current game time: 1 at the start, 0.2 less per second, 0
    /// when it ended. Modifiers to Elusive effect raise the starting value and the duration together.</summary>
    public static double ElusiveFactor(ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (conn.ElusiveUntil <= now) return 0;
        return Math.Max(0, conn.ElusiveEffectMultiplier - 0.2 * (now - conn.ElusiveStart));
    }

    /// <summary>The effect's chance to avoid damage from hits, 15% at 100% effect, for the server's avoidance roll.</summary>
    public static int ElusiveAvoidPct(ClientState conn) => (int)Math.Floor(15 * ElusiveFactor(conn));

    /// <summary>The effect's movement speed, 30% at 100% effect: the pools add it while the buff runs.</summary>
    public static int ElusiveMovementPct(ClientState conn) => (int)Math.Floor(30 * ElusiveFactor(conn));

    /// <summary>"X% chance to gain Elusive on Critical Strike": a player critical hit rolls it unless Elusive is
    /// already running (the buff cannot be refreshed).</summary>
    public static void GainElusiveOnCrit(ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (conn.ElusiveUntil > now) return;
        if (Stats.Key("gain_elusive_on_crit_%_chance") is not uint key) return;
        var stats = CharacterPlayerStats(conn);
        int chance = stats.GetValueOrDefault(key);
        if (chance <= 0 || Random.Shared.Next(100) >= Math.Min(100, chance)) return;
        // PoB's Elusive effect: increased Elusive Effect plus increased Buff Effect on Self (125% with both at 25%).
        int increased = Stats.Key("elusive_effect_+%") is uint effectKey ? stats.GetValueOrDefault(effectKey) : 0;
        if (Stats.Key("buff_effect_on_self_+%") is uint buffKey) increased += stats.GetValueOrDefault(buffKey);
        double effect = Math.Max(0.01, 1 + increased / 100.0);
        lock (conn.Monsters)
        {
            conn.ElusiveStart = now;
            conn.ElusiveEffectMultiplier = effect;
            conn.ElusiveUntil = now + 5 * effect;
        }
        Console.WriteLine($"[gateway] elusive on crit: {chance}% -> {5 * effect:0.###} seconds ({effect * 100:0.#}% effect)");
    }

    /// <summary>The Elusive buff on the client: added when it starts, removed when its effect reaches zero or the
    /// player dies, and re-sent with the current effect as it decays (the client draws 100% plus the row's +0x82
    /// percentage). The avoidance and movement speed come from <see cref="ElusiveFactor"/> at their consumers; the
    /// movement speed also reaches the client as one movement stat so its walk keeps up with the server's.</summary>
    public static async Task SyncElusive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        // The critical hit only flags the roll: build the character stats here, outside the combat lock.
        if (conn.ElusiveCritPending)
        {
            conn.ElusiveCritPending = false;
            GainElusiveOnCrit(conn);
        }
        byte[]? buffBytes = null;
        byte[]? statBytes = null;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool active = conn.PlayerLife > 0 && conn.ElusiveUntil > now;
            if (!active && conn.ElusiveBuff != 0)
            {
                buffBytes = InstanceEntities.RemoveBuff(PlayerId, conn.ElusiveBuff);
                Console.WriteLine($"[gateway] elusive buff {conn.ElusiveBuff} ended");
                conn.ElusiveBuff = 0;
                conn.ElusiveEffectPercent = -1;
            }
            else if (active)
            {
                // The icon shows 100% plus the +0x82 percentage, so below full effect the value is negative.
                int percent = (int)Math.Round(ElusiveFactor(conn) * 100);
                if (percent != conn.ElusiveEffectPercent)
                {
                    bool created = conn.ElusiveBuff == 0;
                    if (created) conn.ElusiveBuff = conn.NextBuff++;
                    conn.ElusiveEffectPercent = percent;
                    // Row 1374 ("elusive") has the __col44 bool (+0xff), so its 0x1c1 must carry the u16 at +0x82.
                    buffBytes = InstanceEntities.AddBuff(PlayerId, conn.ElusiveBuff, ElusiveBuffRow.Value, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)(conn.ElusiveUntil - now)),
                        u82: unchecked((ushort)(percent - 100)));
                    if (created)
                        Console.WriteLine($"[gateway] elusive buff {conn.ElusiveBuff} for {conn.ElusiveUntil - now:0.###} s at {percent}% (row {ElusiveBuffRow.Value})");
                }
            }
            // The effect loses 20% per second, so the movement speed changes a handful of times per buff: rebuild the
            // pools then, and send only that one stat rather than a full character snapshot every tick.
            int movement = ElusiveMovementPct(conn);
            if (movement != conn.ElusiveMovementPct)
            {
                conn.ElusiveMovementPct = movement;
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeedAt(now);
                statBytes = InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.MovementVelocityPct, conn.Pools.MovementVelocityPct)]);
            }
        }
        if (buffBytes is not null) await Send(remote, stream, ciphers, buffBytes, "elusive buff");
        if (statBytes is not null) await Send(remote, stream, ciphers, statBytes, "elusive movement");
    }
}
