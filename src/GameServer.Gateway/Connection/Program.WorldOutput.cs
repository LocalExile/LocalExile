using System.Diagnostics;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

partial class Program
{
    // Birth waits precede gameplay/send gates. Projection runs once at the existing gate; only neutral records wait
    // for a frame. The existing module lifetime check is retained, without introducing a new reload contract.
    internal static async Task<bool> SendRecords(ClientState conn, IReadOnlyList<WorldObject> requires,
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

    internal static Task<bool> SendRecords(ClientState conn, OutputBatch batch, Func<bool>? still = null) =>
        SendRecords(conn, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.World).Select(r => r.Object!).Distinct().ToArray(),
            _ => batch, still, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.Create).Select(r => r.Object!).ToArray());

    internal static Task<bool> SendWorld(ClientState conn, WorldObject target, WorldOrder order,
        Func<byte[]?> atGate, string? log = null, Func<bool>? still = null) =>
        SendRecords(conn, [target], _ => atGate() is { } bytes ? new(OutputRecord.World(target, order, bytes, log)) : null, still);

    internal static async Task<double> BeginWorldFrames(ClientState conn, double frameSeconds)
    {
        var gate = SendGates.GetValue(conn.Wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            lock (conn.Monsters)
            {
                ObjectDisposedException.ThrowIf(conn.Closed || conn.WorldOutput.Closed, conn.WorldOutput);
                double first = conn.Clock.Elapsed.TotalSeconds;
                conn.WorldOutput.Begin(conn.ServerFrame);
                if (conn.LockstepClient) conn.LockstepFrame = (first, first + frameSeconds);
                return first;
            }
        }
        finally { gate.Release(); }
    }

    internal static async Task<long?> EndWorldFramesThrough(ClientState conn, double first, double frameSeconds,
        long ended, double now)
    {
        if (conn.Closed || conn.WorldOutput.Closed) return null;
        for (; first + (ended + 1) * frameSeconds <= now; ended++)
            if (!await EndWorldFrame(conn, (first + (ended + 1) * frameSeconds, first + (ended + 2) * frameSeconds)))
                return conn.Closed || conn.WorldOutput.Closed ? null : ended;
        return ended;
    }

    internal static async Task<bool> EndWorldFrame(ClientState conn, (double Start, double End) next)
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
