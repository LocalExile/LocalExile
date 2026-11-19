using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // BuffDefinitions' "phasing_buff" row: the icon and "You have Phasing, allowing you to move through enemies."
    // (raider's row 337 is the same icon without Removable; the general buff is the one the client shows).
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadPhasingData()
    {
        PhasingBuffRow = LoadPhasingBuffRow();
        PhasingVisualRow = LoadPhasingVisualRow();
    }

    static ushort PhasingBuffRow, PhasingVisualRow;

    static ushort LoadPhasingBuffRow()
    {
        if (!ClientTables.Has("BuffDefinitions")) return (ushort)336;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == "phasing_buff") return (ushort)row.Index;
        return (ushort)336;
    }

    // The buff's own BuffVisuals row 340 has only the icon; raider_phasing_buff carries the client's phase.epk
    // (the blue model effect), so the buff is added with that visual override.
    static ushort LoadPhasingVisualRow()
    {
        if (!ClientTables.Has("BuffVisuals")) return (ushort)813;
        foreach (var row in ClientTables.Table("BuffVisuals"))
            if (row.Text("Id") == "raider_phasing_buff") return (ushort)row.Index;
        return (ushort)813;
    }

    /// <summary>The charm's "X% chance to gain Phasing for 4 seconds on Kill": one roll per kill, resetting the
    /// connection's timed phasing (<see cref="Player.IsPhasing"/>). A stat presentation publishes the stat the
    /// client's pathfinder reads and the Phasing Buff icon.</summary>
    public static void GainPhasingOnKill(Player conn)
    {
        uint key = Stat.PhasingFor4SecondsOnKillPct;
        int chance = CharacterStatSources(conn).Stats.GetValueOrDefault(key);
        if (chance <= 0 || Random.Shared.Next(100) >= Math.Min(100, chance)) return;
        // The game clock: projectile contacts carry their own quanta origin.
        double at = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
            conn.PhasingUntil = Math.Max(conn.PhasingUntil, at + 4);
        PresentStats(conn);
        Console.WriteLine($"[gateway] phasing on kill: {chance}% -> 4 seconds (until {conn.PhasingUntil:0.###})");
    }

    /// <summary>The Phasing Buff (BuffDefinitions "phasing_buff") and the virtual stat the client's pathfinder
    /// reads. The server's own blockers already follow <see cref="Player.IsPhasing"/>; without the stat the
    /// client walks around them and the estimates drift apart.</summary>
    public static async Task SyncPhasing(Player conn)
    {
        byte[]? buffBytes = null;
        byte[]? statBytes = null;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool active = conn.PlayerLife > 0 && conn.IsPhasing;
            bool timed = active && conn.PhasingUntil > now;
            if (conn.PhasingUntil > now) PresentIn(conn, conn.PhasingUntil - now);
            double until = timed ? conn.PhasingUntil : 0;
            if (active)
            {
                bool created = conn.PhasingBuff == 0;
                if (created)
                {
                    conn.PhasingBuff = conn.NextBuff++;
                    conn.PhasingSentUntil = until;
                    buffBytes = InstanceEntities.AddBuff(PlayerId, conn.PhasingBuff, PhasingBuffRow, PlayerId,
                        u68: timed ? BitConverter.SingleToUInt32Bits((float)(until - now)) : null,
                        visual: PhasingVisualRow);
                    Console.WriteLine($"[gateway] phasing buff {conn.PhasingBuff} for {(timed ? $"{until - now:0.###} s" : "the flask effect")} (row {PhasingBuffRow})");
                    statBytes = PhaseStat(conn.IsPhasing);
                }
                else if (TimedChanged(conn, timed, until))
                {
                    conn.PhasingSentUntil = until;
                    buffBytes = InstanceEntities.AddBuff(PlayerId, conn.PhasingBuff, PhasingBuffRow, PlayerId,
                        u68: timed ? BitConverter.SingleToUInt32Bits((float)(until - now)) : null,
                        visual: PhasingVisualRow);
                }
            }
            else if (conn.PhasingBuff != 0)
            {
                buffBytes = InstanceEntities.RemoveBuff(PlayerId, conn.PhasingBuff);
                Console.WriteLine($"[gateway] phasing buff {conn.PhasingBuff} ended");
                conn.PhasingBuff = 0;
                conn.PhasingSentUntil = 0;
                statBytes = PhaseStat(false);
            }
        }
        if (buffBytes is not null) await Send(conn, buffBytes, "phasing buff");
        if (statBytes is not null) await Send(conn, statBytes, "phasing stat");
    }

    // The timed remainder is re-sent when a kill extends it; a timed buff that fell back to a flask-only source
    // loses its countdown by omitting u68.
    static bool TimedChanged(Player conn, bool timed, double until) =>
        timed ? Math.Abs(until - conn.PhasingSentUntil) > 0.001 : conn.PhasingSentUntil != 0;

    static byte[] PhaseStat(bool on)
    {
        var stats = new List<(uint Key, int Value)>();
        if (Stat.VirtualPhaseThroughObjects is var virtualKey) stats.Add((virtualKey, on ? 1 : 0));
        if (Stat.PhaseThroughObjects is var phaseKey) stats.Add((phaseKey, on ? 1 : 0));
        return InstanceEntities.StatsUpdate(PlayerId, stats);
    }
}
