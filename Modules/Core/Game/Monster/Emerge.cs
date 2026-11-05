using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Monsters that rise from the ground (MonsterEmerge.cs); loaded with the first monster spawned.
    internal static readonly MonsterEmerge Emerges = new();

    // Starts the connection's emerge watch for a monster just created waiting to rise (SpawnMonster), unless it already
    // runs.
    internal static void WatchBuried(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        bool start;
        lock (conn.Monsters) start = MonsterEmerge.StartWatch(conn);
        if (start) _ = RunEmergeWatch(remote, stream, ciphers, conn);
    }

    // Every 250 ms: the monsters waiting near the player, and lurking ones that were hit, rise (MonsterEmerge.RiseLocked).
    // For each, the initial hidden buff is removed, 0x1ab/0x1ac starts Emerge, then 0x2ba transitions to targetable.
    // It plays the .act's Emerge clip from the paused first frame. Meanwhile it is busy (RiseLocked: the clip's length),
    // so the AI only chases and attacks after it.
    // Rising does not wait for the AI (/ai off still lets a /spawn_mob emerge monster rise next to the player); it waits
    // while the game is paused or the player is dead.
    internal static async Task RunEmergeWatch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
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

    internal static async Task Emerge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, (int X, int Y) at, double seconds)
    {
        if (Emerges.SkillId is not ushort skill || m.Variety is not { } v) return;
        try
        {
            int index = v.ComponentsOrBase.ToList().IndexOf("Transitionable");
            byte state = MonsterEmerge.RisenState(v);
            await Send(remote, stream, ciphers, [],
                $"0x01ab monster 0x{id:x} ({m.Label}) emerges at ({at.X}, {at.Y}): Emerge action 0x{skill:x4}, {seconds:0.00} s",
                atGate: () =>
                {
                    using (ProjectileMutation(conn))
                    {
                        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || m.Dead || conn.Monsters.GetValueOrDefault(id) != m)
                            return null;
                        var messages = MonsterEmerge.RiseMessages(id, at, skill, m.NextSequence(), (byte)index, state, m.HiddenBuffInstance);
                        byte[] bytes = [.. messages.SelectMany(p => p)];
                        ToOthers(conn, id, bytes);
                        return bytes;
                    }
                });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }
}
