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
    // For each, S→C 0x1ab gives it its Emerge basic action in place, 0x2ba steps its Transitionable to the transitioned
    // state (on_transition: EnableTargetable), and 0x1ac at once lets the action play: the .act's Emerge clip, from the
    // paused first frame. Meanwhile it is busy (RiseLocked: the clip's length), so the AI only chases and attacks after it.
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
                    risen = Emerges.RiseLocked(conn, conn.Walk.Position(now), now, out watching);
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
    }

    internal static async Task Emerge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, (int X, int Y) at, double seconds)
    {
        if (Emerges.SkillId is not ushort skill || m.Variety is not { } v) return;
        try
        {
            int index = v.ComponentsOrBase.ToList().IndexOf("Transitionable");
            byte state = MonsterEmerge.RisenState(v);
            var messages = MonsterEmerge.RiseMessages(id, at, skill, (byte)index, state);
            await Send(remote, stream, ciphers, messages[0],
                $"0x01ab monster 0x{id:x} ({m.Label}) emerges at ({at.X}, {at.Y}): Emerge action 0x{skill:x4}, {seconds:0.00} s");
            await Send(remote, stream, ciphers, messages[1], $"0x02ba monster 0x{id:x} ({m.Label}) Transitionable → {state} (targetable)");
            // A server action on a monster waits in the Actor's pending slot (+0x1b8) until 0x1ac sets its go flag
            // (0x141e94720, retried every 0.25 s); only then does it move to +0x1b0 and play. Sent at the end of the clip,
            // the monster lay still for one clip and rose for another, and took Moves only at twice the clip (live
            // 2026-11-01, 5 of 5 rises).
            bool Still() { lock (conn.Monsters) return !conn.Closed && !m.Dead && conn.Monsters.GetValueOrDefault(id) == m; }
            await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id), $"0x01ac monster 0x{id:x} ({m.Label}) emerge action plays",
                still: Still);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }
}
