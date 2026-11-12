using System.Diagnostics;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

public partial class Program
{
    // Birth waits precede gameplay/send gates. Projection runs once at the existing gate; only neutral records wait
    // for a frame. The existing module lifetime check is retained, without introducing a new reload contract.
    public static async Task<bool> SendRecords(ClientState conn, IReadOnlyList<WorldObject> requires,
        Func<WorldAdmission, OutputBatch?> atGate, Func<bool>? still = null, IReadOnlyList<WorldObject>? creates = null,
        IWorldAdmissionRules? rules = null, bool quest = false,
        (string Remote, System.Net.Sockets.NetworkStream Stream, GameServer.Core.Protocol.Login.ConnectionCiphers Ciphers)? transport = null)
    {
        var output = conn.WorldOutput;
        WorldObject[] dependencies = [.. requires], reservations = creates is null ? [] : [.. creates];
        foreach (var target in dependencies.Concat(reservations)) output.CheckOwner(target);
        var admission = new WorldAdmission(output, reservations);
        bool admitted = false;
        try
        {
            if (dependencies.Intersect(reservations).Any())
                throw new ArgumentException("An output batch cannot wait on its own unadmitted birth.");
            if (!await output.AwaitBirths(dependencies)) return false;
            var wire = transport ?? conn.Wire;
            var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            if (quest) await conn.QuestGate.WaitAsync();
            try
            {
                await gate.WaitAsync();
                try
                {
                    if (conn.Closed || conn.LeavingArea || output.Closed) return false;
                    if (dependencies.Any(t => !output.IsReady(t))) return false;
                    if (rules is not null && (!ReferenceEquals(GameLogicOrNull, rules) || !rules.WorldAdmissionActive)) return false;
                    if (ProjectileOutputGuard.Value is { } guard && !guard()) return false;
                    if (still is not null && !still()) return false;
                    if (atGate(admission) is not { } batch) return false;
                    var immediate = output.Admit(batch, admission, output.Started && conn.LockstepClient);
                    admitted = true;
                    admission.Finish(true);
                    // No module callback is retained in the frame queue or invoked while flushing it.
                    atGate = null!; still = null; rules = null;
                    foreach (var packet in immediate)
                        await WritePacket(wire.Remote, wire.Stream, wire.Ciphers, packet.Bytes, packet.Log, packet);
                    output.Written(immediate.Select(p => p.Creates).OfType<WorldObject>().ToArray(),
                        immediate.Select(p => p.Removes).OfType<WorldObject>().ToArray(), frameEnded: false);
                    return true;
                }
                catch { if (admitted) FailWorldOutput(conn); throw; }
                finally { gate.Release(); }
            }
            finally { if (quest) conn.QuestGate.Release(); }
        }
        finally { if (!admitted) admission.Finish(false); }
    }

    public static Task<bool> SendRecords(ClientState conn, OutputBatch batch, Func<bool>? still = null) =>
        SendRecords(conn, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.World).Select(r => r.Object!).Distinct().ToArray(),
            _ => batch, still, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.Create).Select(r => r.Object!).ToArray());

    public static Task<bool> SendWorld(ClientState conn, WorldObject target, WorldOrder order,
        Func<byte[]?> atGate, string? log = null, Func<bool>? still = null) =>
        SendRecords(conn, [target], _ => atGate() is { } bytes ? new(OutputRecord.World(target, order, bytes, log)) : null, still);

    // Differs from GGG: its server steps its world in frames, so a frame is a tick of the simulation. Ours walks on the
    // clock, so frames are laid over it: frame n ends n × FrameMs after the first, and a frame missed by a late timer goes
    // out at once instead of being lost, which keeps every frame start on the conn.Clock timeline the walks use (a free
    // 33 ms timer drifted 28 to 47 ms apart, with gaps to 390 ms). GGG's spacing is 30 to 36 ms. The Clock stops in the
    // Escape menu's pause, and so do the frames.
    // Differs from GGG: it never sends 0x2b8 (14 236 frames) and relies on the client's default, which is not traced; ours
    // says the length it paces by. Every connection runs these frames: the client alone picks its Network Mode, and a
    // predictive one ignores 0x2b7 (07 §6).
    // The frames last as long as the connection. What the game does at a frame end is asked of the rules loaded then, so
    // after a game module reload the new rules take over at the next frame and nothing here keeps the old ones: the loop
    // also runs on an execution context of its own, since the caller's async-local values (the game module's) would ride
    // along for the connection's life.
    public static void StartWorldFrames(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, ClientState conn)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(() => RunWorldFrames(remote, stream, ciphers, conn));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => RunWorldFrames(remote, stream, ciphers, conn));
    }

    static async Task RunWorldFrames(string remote, System.Net.Sockets.NetworkStream stream,
        GameServer.Core.Protocol.Login.ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            await Send(remote, stream, ciphers, InstanceProtocol.FrameLength(FrameMs), $"0x02b8 server frame length {FrameMs} ms");
            double frame = FrameMs / 1000d, first = await BeginWorldFrames(conn, frame);
            long ended = 0;
            while (!conn.Closed)
            {
                // Paused, the Clock stands and no frame falls due: it looks again a third of a frame later.
                if (conn.Paused)
                {
                    await Task.Delay(Math.Max(1, FrameMs / 3));
                    continue;
                }
                // Woken at the next frame end, on the Gateway's 1 ms timer (WindowsTimerResolution): a poll of a third of
                // a frame landed just before a boundary often enough to send frames 22 and 44 ms apart.
                double wait = first + (ended + 1) * frame - conn.Clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait * 1000)));
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (now < first + (ended + 1) * frame) continue;
                if (await EndWorldFramesThrough(conn, first, frame, ended, now) is not { } completed) return;
                ended = completed;
                if (GameLogicOrNull is { } rules) await rules.WorldFrameEnded(remote, stream, ciphers, conn, first + ended * frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    public static async Task<double> BeginWorldFrames(ClientState conn, double frameSeconds)
    {
        var gate = SendGates.GetValue(conn.Wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            lock (conn.Monsters)
            {
                ObjectDisposedException.ThrowIf(conn.Closed || conn.WorldOutput.Closed, conn.WorldOutput);
                // Frames start on the instance clock's own grid (whole frame lengths), so every connection of an instance
                // ends its frames at the same instants, as the clients of one stepped world do: what one of them is sent
                // in a frame can then reach the others exactly one frame later (ClientState.FrameFrom).
                double first = Math.Ceiling(conn.Clock.Elapsed.TotalSeconds / frameSeconds) * frameSeconds;
                conn.WorldOutput.Begin(conn.ServerFrame);
                if (conn.LockstepClient) conn.LockstepFrame = (first, first + frameSeconds);
                return first;
            }
        }
        finally { gate.Release(); }
    }

    public static async Task<long?> EndWorldFramesThrough(ClientState conn, double first, double frameSeconds,
        long ended, double now)
    {
        if (conn.Closed || conn.WorldOutput.Closed) return null;
        for (; first + (ended + 1) * frameSeconds <= now; ended++)
            if (!await EndWorldFrame(conn, (first + (ended + 1) * frameSeconds, first + (ended + 2) * frameSeconds)))
                return conn.Closed || conn.WorldOutput.Closed ? null : ended;
        return ended;
    }

    public static async Task<bool> EndWorldFrame(ClientState conn, (double Start, double End) next)
    {
        var wire = conn.Wire;
        var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        bool committed = false;
        try
        {
            WorldOutput.Frame frame;
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.WorldOutput.Closed || conn.Paused) return false;
                frame = conn.WorldOutput.TakeFrame();
                conn.ServerFrame++;
                if (conn.LockstepClient) conn.LockstepFrame = next;
                committed = true;
            }
            conn.TurnFrame();
            foreach (var packet in frame.Packets)
                await WritePacket(wire.Remote, wire.Stream, wire.Ciphers, packet.Bytes, packet.Log, packet);
            await WritePacket(wire.Remote, wire.Stream, wire.Ciphers, InstanceProtocol.FrameEnd(), null);
            conn.WorldOutput.Written(frame.Births, frame.Removals, frameEnded: true);
            return true;
        }
        catch { if (committed) FailWorldOutput(conn); throw; }
        finally { gate.Release(); }
    }

    static void FailWorldOutput(ClientState conn)
    {
        conn.CloseReason ??= "World output failed; its cipher stream cannot be retried";
        conn.Closed = true;
        conn.Wire.Stream.Close();
    }
}
