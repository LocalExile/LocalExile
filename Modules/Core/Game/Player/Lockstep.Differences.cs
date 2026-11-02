using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Where serving lockstep differs from GGG's server, each on purpose and each with its reason. Lockstep.cs holds what
// follows GGG; 07 §6 lists these. The loopback rule is LockstepLink in the Gateway.
static partial class Game
{
    // Differs from GGG: its server steps its world in frames, so a frame is a tick of the simulation. Ours walks on the
    // clock, so frames are laid over it: frame n ends n × FrameMs after the first, and a frame missed by a late timer goes
    // out at once instead of being lost, which keeps every frame start on the conn.Clock timeline the walks use (a free
    // 33 ms timer drifted 28 to 47 ms apart, with gaps to 390 ms). GGG's spacing is 30 to 36 ms. The Clock stops in the
    // Escape menu's pause, and so do the frames.
    // Differs from GGG: it never sends 0x2b6 (14 236 frames) and relies on the client's default, which is not traced; ours
    // says the length it paces by, as every connection's frames do (RunServerFrames).
    internal static async Task RunLockstepFrames(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            await Send(remote, stream, ciphers, InstanceProtocol.FrameLength(FrameMs), $"0x02b6 server frame length {FrameMs} ms");
            double frame = FrameMs / 1000d, first = conn.Clock.Elapsed.TotalSeconds;
            long ended = 0;
            // Well under a frame: a timer tick landing just before a boundary must not hold the frame for a whole period.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, FrameMs / 3)));
            while (!conn.Closed && await timer.WaitForNextTickAsync())
            {
                long due = (long)((conn.Clock.Elapsed.TotalSeconds - first) / frame);
                if (due == ended) continue;
                for (; ended < due; ended++)
                {
                    await Send(remote, stream, ciphers, InstanceProtocol.FrameEnd(), null);
                    conn.ServerFrame++;
                }
                await StartLockstepWalk(remote, stream, ciphers, conn, first + ended * frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // Differs from GGG: it keys a Move from its tick by the player (u16 0x9001, 3 286 of 3 492 Moves). Keys must reach the
    // client sorted or the rest of the frame waits for its end (0x142184680), and everything else the Gateway sends is key
    // 0 in no set order, so ours is key 0 too: applied before the objects tick, which fits a walk starting at the frame's
    // start.
    static byte[] LockstepActionStart(ClientState conn, (int X, int Y) from, (int X, int Y) target, ushort sequence) =>
        InstanceProtocol.ActionStarted(PlayerId, from.X, from.Y, target.X, target.Y, conn.LockstepHold.Skill, sequence, conn.LockstepHold.Flags);

    // Differs from GGG: it serves every skill and object use in lockstep. Only walking is served here so far (07 §6): the
    // rest is ignored, and said once per connection in the log.
    static void NotServedInLockstep(string remote, ClientState conn, ushort id)
    {
        if (conn.WarnedLockstep) return;
        conn.WarnedLockstep = true;
        Console.WriteLine($"[gateway] {remote} <= 0x{id:x4}: only walking is served in lockstep so far, the request is ignored");
    }
}
