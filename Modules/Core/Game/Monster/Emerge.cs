using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Monsters that rise from the ground (MonsterEmerge.cs); loaded with the first monster spawned.
    public static readonly MonsterEmerge Emerges = new();

    // Starts the connection's emerge watch for a monster just created waiting to rise (SpawnMonster), unless it already
    // runs.
    public static void WatchBuried(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        bool start;
        lock (conn.Monsters) start = MonsterEmerge.StartWatch(conn);
        if (start) _ = RunEmergeWatch(remote, stream, ciphers, conn);
    }

    // Every 250 ms: the monsters waiting near the player, and lurking ones that were hit, rise (MonsterEmerge.RiseLocked).
    // For each, 0x2bc steps its Transitionable to the transitioned state (on_transition: EnableTargetable), S→C 0x1ab gives
    // it its Emerge basic action in place, and 0x1ac at once lets the action play: the .act's Emerge clip, from the paused
    // first frame. Meanwhile it is busy (RiseLocked: the clip's length), so the AI only chases and attacks after it.
    // Rising does not wait for the AI (/ai off still lets a /spawn_mob emerge monster rise next to the player); it waits
    // while the game is paused or the player is dead.
    public static async Task RunEmergeWatch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            while (!conn.Closed)
            {
                await Task.Delay(250);
                if (conn.Paused || conn.PlayerLife == 0) continue;
                double now = conn.Clock.Elapsed.TotalSeconds;
                List<(uint Id, SpawnedMonster Monster, (int X, int Y) At, double Seconds)> risen;
                bool watching;
                using (ProjectileMutation(conn))
                {
                    now = ProjectileWorldTime(conn);
                    // Near any player in the instance: whoever comes near wakes them.
                    risen = Emerges.RiseLocked(conn, conn.Walk.Position(now), now, out watching);
                    foreach (var other in InGame.Keys)
                        if (!ReferenceEquals(other, conn) && ReferenceEquals(other.Instance, conn.Instance) && !other.Closed
                            && !other.LeavingArea && other.PlayerLife > 0 && watching)
                            risen.AddRange(Emerges.RiseLocked(conn, other.Walk.Position(now), now, out watching));
                }
                foreach (var (id, m, at, seconds) in risen)
                    _ = Emerge(remote, stream, ciphers, conn, id, m, at, seconds);
                if (!watching) return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
        finally
        {
            // Whoever runs the instance next watches the rest (SyncInstanceMonsters).
            lock (conn.Monsters) MonsterEmerge.StopWatch(conn);
        }
    }

    public static async Task Emerge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, (int X, int Y) at, double seconds)
    {
        if (Emerges.SkillId is not ushort skill || m.Variety is not { } v) return;
        try
        {
            int index = v.ComponentsOrBase.ToList().IndexOf("Transitionable");
            byte state = MonsterEmerge.RisenState(v);
            var messages = MonsterEmerge.RiseMessages(id, at, skill, m.NextSequence(), (byte)index, state);
            await Send(remote, stream, ciphers, messages[0], $"0x02bc monster 0x{id:x} ({m.Label}) Transitionable → {state} (targetable)");
            // The others in the instance see it rise too (it is theirs to hit as well).
            ToOthers(conn, id, [.. messages[0], .. messages[1]]);
            // A server action waits in the Actor's pending slot (+0x1b8) until 0x1ac sets its go flag (0x141e9afc0, looked at
            // again every 0.25 s); only then does it move to +0x1b0 and play.
            bool Still() { lock (conn.Monsters) return !conn.Closed && !m.Dead && conn.Monsters.GetValueOrDefault(id) == m; }
            await Send(remote, stream, ciphers, messages[1],
                $"0x01ab monster 0x{id:x} ({m.Label}) emerges at ({at.X}, {at.Y}): Emerge action 0x{skill:x4}, {seconds:0.00} s",
                still: Still);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }
}
