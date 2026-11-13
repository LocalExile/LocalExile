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
        double effect = Math.Max(0.01, 1 + (Stats.Key("elusive_effect_+%") is uint effectKey
            ? stats.GetValueOrDefault(effectKey) : 0) / 100.0);
        lock (conn.Monsters)
        {
            conn.ElusiveStart = now;
            conn.ElusiveEffectMultiplier = effect;
            conn.ElusiveUntil = now + 5 * effect;
            conn.StatSourcesCache = null;
        }
        Console.WriteLine($"[gateway] elusive on crit: {chance}% -> {5 * effect:0.###} seconds ({effect * 100:0.#}% effect)");
    }

    /// <summary>The Elusive buff on the client: added when it starts, removed when its effect reaches zero or the
    /// player dies. Its stat contribution decays through <see cref="ElusiveFactor"/>.</summary>
    public static async Task SyncElusive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        // The critical hit only flags the roll: build the character stats here, outside the combat lock.
        if (conn.ElusiveCritPending)
        {
            conn.ElusiveCritPending = false;
            GainElusiveOnCrit(conn);
        }
        byte[]? bytes = null;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool active = conn.PlayerLife > 0 && conn.ElusiveUntil > now;
            if (active && conn.ElusiveBuff == 0)
            {
                conn.ElusiveBuff = conn.NextBuff++;
                bytes = InstanceEntities.AddBuff(PlayerId, conn.ElusiveBuff, ElusiveBuffRow.Value, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)(conn.ElusiveUntil - now)));
                Console.WriteLine($"[gateway] elusive buff {conn.ElusiveBuff} for {conn.ElusiveUntil - now:0.###} s (row {ElusiveBuffRow.Value})");
            }
            else if (!active && conn.ElusiveBuff != 0)
            {
                bytes = InstanceEntities.RemoveBuff(PlayerId, conn.ElusiveBuff);
                Console.WriteLine($"[gateway] elusive buff {conn.ElusiveBuff} ended");
                conn.ElusiveBuff = 0;
                conn.StatSourcesCache = null;
            }
        }
        if (bytes is not null) await Send(remote, stream, ciphers, bytes, "elusive buff");
    }
}
