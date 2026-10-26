using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Mitigation/block precede Ward; the surviving hit then reaches energy shield. A mixed hit breaks Ward once.
    static int DamagePlayerHit(ClientState conn, int nonChaos, int chaos, double now)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double taken = Math.Max(0, 1 + Get("harbinger_channel_buff_damage_taken_+%_final") / 100.0);
        nonChaos = (int)Math.Round(nonChaos * taken);
        chaos = (int)Math.Round(chaos * taken);
        int total = nonChaos + chaos;
        conn.Ward.Advance(now);
        bool brokenBefore = conn.Ward.Broken;
        int remaining = conn.Ward.Damage(total, now, Get);
        if (!brokenBefore && conn.Ward.Broken && GrantAdrenaline(conn, Get("gain_adrenaline_when_ward_breaks_ms")))
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        int absorbed = total - remaining;
        // Physical, lightning, cold and fire precede chaos in the native damage-type order. Keep the type of the
        // unabsorbed damage: distributing Ward proportionally would incorrectly let ES absorb more chaos damage.
        int eligibleNonChaos = (int)((long)nonChaos * (100 - Math.Clamp(Get("base_damage_bypass_ward_%"), 0, 100)) / 100);
        int nonChaosAbsorbed = Math.Min(eligibleNonChaos, absorbed);
        int chaosRemaining = chaos - (absorbed - nonChaosAbsorbed);
        return conn.EnergyShield.Damage(nonChaos - nonChaosAbsorbed, false, now, Get)
            + conn.EnergyShield.Damage(chaosRemaining, true, now, Get);
    }

    static async Task SendWardState(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var packets = new List<(byte[] Packet, string Label)>();
        lock (conn.Monsters)
        {
            conn.Ward.Advance(conn.Clock.Elapsed.TotalSeconds);
            var state = (conn.Ward.Maximum, conn.Ward.Broken);
            if (conn.SentWard == state) return;
            conn.SentWard = state;
            if (conn.Ward.Broken && conn.WardBuff is null)
            {
                conn.WardBuff = conn.AllocateBuff();
                packets.Add((InstanceEntities.AddBuff(PlayerId, conn.WardBuff.Value, 1878, PlayerId), "Ward broken"));
            }
            else if (!conn.Ward.Broken && conn.WardBuff is ushort buff)
            {
                conn.WardBuff = null;
                packets.Add((InstanceEntities.RemoveBuff(PlayerId, buff), "Ward restored"));
            }
            var stats = new List<(uint, int)>();
            if (Stats.Key("ward") is uint maximum) stats.Add((maximum, (int)conn.Ward.Maximum));
            if (Stats.Key("ward_broken") is uint broken) stats.Add((broken, conn.Ward.Broken ? 1 : 0));
            packets.Add((InstanceEntities.StatsUpdate(PlayerId, stats), $"Ward {conn.Ward.Current}/{conn.Ward.Maximum}"));
        }
        foreach (var (packet, label) in packets) await Send(remote, stream, ciphers, packet, label);
    }
}
