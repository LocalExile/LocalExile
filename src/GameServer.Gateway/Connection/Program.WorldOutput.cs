using System.Buffers.Binary;
using System.Diagnostics;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

public partial class Program
{
    // Birth waits precede gameplay/send gates. Projection runs once at the existing gate; only neutral records wait
    // for a frame. The existing module lifetime check is retained, without introducing a new reload contract.
    public static Task<bool> SendRecords(Player player, IReadOnlyList<WorldObject> requires,
        Func<WorldAdmission, OutputBatch?> atGate, Func<bool>? still = null, IReadOnlyList<WorldObject>? creates = null,
        IWorldAdmissionRules? rules = null, bool quest = false)
    {
        var output = player.Connection.WorldOutput;
        WorldObject[] dependencies = [.. requires], reservations = creates is null ? [] : [.. creates];
        foreach (var target in dependencies.Concat(reservations)) output.CheckOwner(target);
        if (!quest && player.Area.Owned && output.Started)
            return ShareInFrame(player, dependencies, reservations, atGate, still, rules) ? Admitted : NotAdmitted;
        return SendRecordsAsync(player, dependencies, reservations, atGate, still, rules, quest);
    }

    static async Task<bool> SendRecordsAsync(Player player, WorldObject[] dependencies, WorldObject[] reservations,
        Func<WorldAdmission, OutputBatch?> atGate, Func<bool>? still, IWorldAdmissionRules? rules, bool quest)
    {
        var output = player.Connection.WorldOutput;
        var admission = new WorldAdmission(output, reservations);
        bool admitted = false;
        try
        {
            if (dependencies.Intersect(reservations).Any())
                throw new ArgumentException("An output batch cannot wait on its own unadmitted birth.");
            if (!await output.AwaitBirths(dependencies)) return false;
            var wire = player.Connection.Session;
            var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            if (quest) await player.Character.QuestGate.WaitAsync();
            try
            {
                await gate.WaitAsync();
                try
                {
                    if (player.Closed || player.LeavingArea || output.Closed) return false;
                    if (dependencies.Any(t => !output.IsReady(t))) return false;
                    if (rules is not null && (!ReferenceEquals(GameLogicOrNull, rules) || !rules.WorldAdmissionActive)) return false;
                    if (ProjectileOutputGuard.Value is { } guard && !guard()) return false;
                    if (still is not null && !still()) return false;
                    if (atGate(admission) is not { } batch) return false;
                    var immediate = output.Admit(batch, admission, output.Started && player.Connection.LockstepClient);
                    admitted = true;
                    admission.Finish(true);
                    // No module callback is retained in the frame queue or invoked while flushing it.
                    atGate = null!; still = null; rules = null;
                    foreach (var packet in immediate)
                        await WritePacket(wire.Remote, wire.Stream, wire.Ciphers, packet.Bytes, packet.Log, packet).ConfigureAwait(false);
                    output.Written(immediate.Select(p => p.Creates).OfType<WorldObject>().ToArray(),
                        immediate.Select(p => p.Removes).OfType<WorldObject>().ToArray(), frameEnded: false);
                    return true;
                }
                catch { if (admitted) FailWorldOutput(player); throw; }
                finally { gate.Release(); }
            }
            finally { if (quest) player.Character.QuestGate.Release(); }
        }
        finally { if (!admitted) admission.Finish(false); }
    }

    static readonly Task<bool> Admitted = Task.FromResult(true), NotAdmitted = Task.FromResult(false);

    /// <summary>In the area's frame, before a connection's first frame ends: the game puts in the area's frame what that
    /// client is missing of the area (AreaDelta.Record.Only) and its player for the others (Except). A framework
    /// hand-over; the game sets it.</summary>
    public static Action<Player>? Joining;

    // In the area's frame a connection's world records are the area's: they go to its frame (AreaDelta), which every
    // connection in the area is written at the frame's end, in the client's frame order. Nothing waits and no socket is
    // shared. A message in the batch that is not about one object (S→C 0x20) goes first in that frame.
    /// <summary>A player's own view of the area is being made (their quest objects, NPCs and blockers): what is created now is
    /// written to them alone, and so is every later record about it (AreaDelta's private entities).</summary>
    public static readonly AsyncLocal<Player?> ViewOf = new();

    static bool ShareInFrame(Player player, WorldObject[] dependencies, WorldObject[] reservations,
        Func<WorldAdmission, OutputBatch?> atGate, Func<bool>? still, IWorldAdmissionRules? rules)
    {
        var output = player.Connection.WorldOutput;
        if (player.Closed || player.LeavingArea || output.Closed) return false;
        foreach (var target in dependencies)
            if (target.State == WorldObject.BirthState.Retired || target.RemovalRequested) return false;
        if (rules is not null && (!ReferenceEquals(GameLogicOrNull, rules) || !rules.WorldAdmissionActive)) return false;
        if (ProjectileOutputGuard.Value is { } guard && !guard()) return false;
        if (still is not null && !still()) return false;
        var admission = new WorldAdmission(output, reservations);
        bool admitted = false;
        try
        {
            if (atGate(admission) is not { } batch) return false;
            output.Shared(batch, admission);
            var delta = player.Area.Delta;
            // A message that is not about an object (a death screen, a window) is the sender's own; what a player's view
            // makes (ViewOf) is that player's alone.
            var view = ViewOf.Value;
            foreach (var record in batch.Records)
                delta.Add(new(record.Object?.Id ?? 0, record.CopyBytes(), record.Log,
                    record.Kind == OutputRecord.RecordKind.Immediate ? WorldOrder.First : record.Order, record.Kind switch
                    {
                        OutputRecord.RecordKind.Immediate => AreaDelta.Kind.Message,
                        OutputRecord.RecordKind.Create => AreaDelta.Kind.Create,
                        OutputRecord.RecordKind.Remove => AreaDelta.Kind.Remove,
                        _ => AreaDelta.Kind.World,
                    }, Only: record.Kind == OutputRecord.RecordKind.Immediate ? player
                        : view ?? (record.Object is { } target ? delta.ViewerOf(target.Id, player) : null)));
            admitted = true;
            admission.Finish(true);
            return true;
        }
        catch { if (admitted) FailWorldOutput(player); throw; }
        finally { if (!admitted) admission.Finish(false); }
    }

    public static Task<bool> SendRecords(Player player, OutputBatch batch, Func<bool>? still = null) =>
        SendRecords(player, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.World).Select(r => r.Object!).Distinct().ToArray(),
            _ => batch, still, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.Create).Select(r => r.Object!).ToArray());

    /// <summary>One of the area's records (GameArea.Output) for this connection's player: a record about an object goes, on a
    /// lockstep client, into the frame once the client has the object (as SendWorld); anything else goes out at once.</summary>
    public static async Task SendAreaRecord(Player player, AreaOutput.Record record)
    {
        var wire = player.Connection.Session;
        if (record.Object is uint id && player.Connection.LockstepClient && record.Bytes.Length >= 12)
        {
            var order = new WorldOrder(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record.Bytes.AsSpan(6)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(record.Bytes.AsSpan(10)));
            await SendWorld(player, player.Connection.WorldOutput.PublishedObject(id), order, () => record.Bytes, record.Log);
        }
        else await Send(wire.Remote, wire.Stream, wire.Ciphers, record.Bytes, record.Log);
    }

    public static Task<bool> SendWorld(Player player, WorldObject target, WorldOrder order,
        Func<byte[]?> atGate, string? log = null, Func<bool>? still = null) =>
        SendRecords(player, [target], _ => atGate() is { } bytes ? new(OutputRecord.World(target, order, bytes, log)) : null, still);

    // Differs from GGG: it never sends 0x2b8 (14 236 frames) and relies on the client's default, which is not traced; ours
    // says the length it paces by. Every connection runs these frames: the client alone picks its Network Mode, and a
    // predictive one ignores 0x2b7 (07 §6). The area's driver (AreaFrames) ends them for every connection in the area at
    // once. What the game does at a frame end is asked of the rules loaded then, so after a game module reload the new
    // rules take over at the next frame. It runs in the frame, without the caller's async-local values (the game module's).
    public static void StartWorldFrames(Player player)
    {
        var (remote, stream, ciphers) = player.Connection.Session;
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = AreaFrames.Start(() => player.Frames.Join(remote, stream, ciphers, player));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = AreaFrames.Start(() => player.Frames.Join(remote, stream, ciphers, player));
    }

    public static async Task<double> BeginWorldFrames(Player player, double frameSeconds)
    {
        var gate = SendGates.GetValue(player.Connection.Session.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(player.Closed || player.Connection.WorldOutput.Closed, player.Connection.WorldOutput);
            // Frames start on the instance clock's own grid (whole frame lengths), so every connection of an instance
            // ends its frames at the same instants, as the clients of one stepped world do: what one of them is sent
            // in a frame can then reach the others exactly one frame later (Player.FrameFrom).
            double first = Math.Ceiling(player.Clock.Elapsed.TotalSeconds / frameSeconds) * frameSeconds;
            player.Connection.WorldOutput.Begin(player.Connection.ServerFrame);
            if (player.Connection.LockstepClient) player.Connection.LockstepFrame = (first, first + frameSeconds);
            return first;
        }
        finally { gate.Release(); }
    }

    /// <summary>Ends the connection's frame: its own records, the area's delta (AreaDelta: the records about what is awake on
    /// its client, in their world order with its own), then S→C 0x2b7, in one write.</summary>
    public static async Task<bool> EndWorldFrame(Player player, (double Start, double End) next, IReadOnlyList<AreaDelta.Record>? delta = null)
    {
        var wire = player.Connection.Session;
        var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        bool committed = false;
        try
        {
            WorldOutput.Frame frame;
            if (player.Closed || player.Connection.WorldOutput.Closed || player.Paused) return false;
            frame = player.Connection.WorldOutput.TakeFrame();
            player.Connection.ServerFrame++;
            if (player.Connection.LockstepClient) player.Connection.LockstepFrame = next;
            committed = true;
            player.Connection.TurnFrame();
            long writing = Stopwatch.GetTimestamp();
            int bytes = 0;
            // The frame's records and its end in one write.
            IEnumerable<WorldOutput.Packet> records = frame.Packets;
            // The area's frame: the same records for every connection (what is for this one), in the client's frame order.
            if (delta is { Count: > 0 })
            {
                var shared = new List<WorldOutput.Packet>(delta.Count);
                foreach (var record in delta)
                    if (record.For(player))
                        shared.Add(new(record.Bytes, record.Log, record.Order, IsWorld: record.Kind != AreaDelta.Kind.Message, SharedView: true));
                if (shared.Count > 0) records = frame.Packets.Concat(shared).OrderBy(p => p.Order).ToList();
            }
            var packets = new List<(byte[], string?, WorldOutput.Packet?)>(frame.Packets.Count + 1);
            foreach (var packet in records)
            {
                bytes += packet.Bytes.Length;
                packets.Add((packet.Bytes, packet.Log, packet));
            }
            packets.Add((InstanceProtocol.FrameEnd(), null, null));
            await WritePackets(wire.Remote, wire.Stream, wire.Ciphers, packets);
            player.Connection.WorldOutput.Written(frame.Births, frame.Removals, frameEnded: true);
            player.Frames.Wrote(frame.Packets.Count, bytes, Stopwatch.GetElapsedTime(writing).TotalMilliseconds);
            return true;
        }
        catch { if (committed) FailWorldOutput(player); throw; }
        finally { gate.Release(); }
    }

    /// <summary>While paused, what the server sends the player (a passive point's stats, an item's) still reaches a lockstep
    /// client, which applies world records only in the frames it runs: they go out in a frame of 0 ms (S→C 0x2b8 0, the
    /// records, 0x2b7, then 0x2b8 back to the frame length). The client runs a frame its time already covers at once, and
    /// a 0 ms frame is covered by no time at all (0x14218a2a0), so its records apply and the world does not move. Not on
    /// the frame timeline: it ends no frame of the clock's.</summary>
    public static async Task EndPausedFrame(Player player)
    {
        var wire = player.Connection.Session;
        var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        bool committed = false;
        try
        {
            WorldOutput.Frame frame;
            if (player.Closed || player.Connection.WorldOutput.Closed || !player.Paused || !player.Connection.WorldOutput.Started) return;
            frame = player.Connection.WorldOutput.TakeFrame();
            if (frame.Packets.Count == 0) return;
            committed = true;
            List<(byte[], string?, WorldOutput.Packet?)> packets = [(InstanceProtocol.FrameLength(0), "0x02b8 frame length 0 ms (paused)", null)];
            foreach (var packet in frame.Packets) packets.Add((packet.Bytes, packet.Log, packet));
            packets.Add((InstanceProtocol.FrameEnd(), null, null));
            packets.Add((InstanceProtocol.FrameLength(FrameMs), null, null));
            await WritePackets(wire.Remote, wire.Stream, wire.Ciphers, packets);
            player.Connection.WorldOutput.Written(frame.Births, frame.Removals, frameEnded: true);
        }
        catch { if (committed) FailWorldOutput(player); throw; }
        finally { gate.Release(); }
    }

    static void FailWorldOutput(Player player)
    {
        player.Connection.CloseReason ??= "World output failed; its cipher stream cannot be retried";
        player.Closed = true;
        player.Connection.Session.Stream.Close();
    }
}
