using System.Buffers.Binary;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // Producers retain packet boundaries while committing a multi-object projection at the send gate.
    // Presentation is already committed data; stateful actions must await the target before entering this scope.
    sealed class WorldRecordBuilder(ClientState conn, WorldAdmission? admission)
    {
        readonly List<OutputRecord> records = [];
        bool UsesWorld => conn.LockstepClient && admission is not null;
        public int Count => records.Count;
        public byte[] Bytes() => [.. records.SelectMany(r => r.CopyBytes())];
        public void Add(OutputBatch? batch) { if (batch is not null) records.AddRange(batch.Records); }
        public void Immediate(byte[] bytes, string? log = null)
        {
            if (bytes.Length > 0) records.Add(OutputRecord.Immediate(bytes, log));
        }
        public void World(uint id, byte[] bytes, string? log = null)
        {
            if (bytes.Length == 0) return;
            if (!UsesWorld) { Immediate(bytes, log); return; }
            var target = conn.WorldOutput.PublishedObject(id);
            records.Add(OutputRecord.World(target, RecordOrder(bytes, id), bytes, log));
        }
        public void Presentation(uint id, byte[] bytes, string? log = null)
        {
            if (bytes.Length == 0) return;
            if (!UsesWorld) { Immediate(bytes, log); return; }
            var target = conn.WorldOutput.PublishedObject(id);
            records.Add(target.IsReady ? OutputRecord.World(target, RecordOrder(bytes, id), bytes, log)
                : OutputRecord.AfterBirth(target, RecordOrder(bytes, id), bytes, log));
        }
        public void Create(uint id, byte[] bytes, string? log = null)
        {
            if (!UsesWorld) { Immediate(bytes, log); return; }
            var target = admission!.NewObject(id);
            bytes = bytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 0);
            records.Add(OutputRecord.Create(target, bytes, log));
        }
        public void Remove(uint id, string? log = null)
        {
            var bytes = InstanceProtocol.RemoveObject(id);
            if (!UsesWorld) { Immediate(bytes, log); return; }
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            records.Add(OutputRecord.Remove(conn.WorldOutput.PublishedObject(id), WorldOrder.Last, bytes, log));
        }
        public OutputBatch Build() => records.Count == 0 ? OutputBatch.Empty : UsesWorld ? new([.. records])
            : new(OutputRecord.Immediate([.. records.SelectMany(r => r.CopyBytes())]));
    }

    public static Task<bool> SendWorldCreate(IPlayer player, uint id, byte[] bytes, string? log = null) =>
        SendWorldCreate(ConnOf(player), id, () => bytes, log);

    public static Task SendWorldRecord(IPlayer player, uint id, byte[] bytes, string? log = null)
    {
        var conn = ConnOf(player); var wire = conn.Wire;
        return SendWorldRecord(wire.Remote, wire.Stream, wire.Ciphers, conn, id, bytes, log);
    }

    public static Task SendWorldRemoval(IPlayer player, uint id, string? log = null)
    {
        var conn = ConnOf(player); var wire = conn.Wire;
        return SendWorldRemoval(wire.Remote, wire.Stream, wire.Ciphers, conn, id, log);
    }

    public static async Task SendWorldReceipt(ClientState conn, PlayerMinions.Output output,
        (string Remote, NetworkStream Stream, ConnectionCiphers Ciphers) wire)
    {
        if (output.CreatesObject)
            await SendWorldCreate(wire.Remote, wire.Stream, wire.Ciphers, conn, output.WorldId!.Value, output.Packet, output.Log);
        else if (output.RemovesObject)
            await SendWorldRemoval(wire.Remote, wire.Stream, wire.Ciphers, conn, output.WorldId!.Value, output.Log);
        else if (output.WorldId is { } id)
            await SendWorldRecord(wire.Remote, wire.Stream, wire.Ciphers, conn, id, output.Packet, output.Log);
        else await Send(wire.Remote, wire.Stream, wire.Ciphers, output.Packet, output.Log);
    }

    // A declared single create; its common header is not inferred from an opaque concatenated send.
    public static async Task<bool> SendWorldCreate(ClientState conn, uint id, Func<byte[]?> create,
        string? log = null, Func<bool>? still = null, bool wake = false,
        (string Remote, NetworkStream Stream, ConnectionCiphers Ciphers)? transport = null,
        IReadOnlyList<byte[]>? afterBirth = null)
    {
        var wire = transport ?? conn.Wire;
        if (!conn.LockstepClient)
        {
            bool sent = false;
            await Send(wire.Remote, wire.Stream, wire.Ciphers, [], log, still, atGate: () =>
            {
                if (create() is not { } bytes) return null;
                sent = true;
                return [.. bytes, .. wake ? InstanceProtocol.EnterWakeZone(id) : [],
                    .. (afterBirth ?? []).SelectMany(p => p)];
            });
            return sent;
        }
        // Reserve before any mutation exposes the actor to concurrent action producers.
        WorldObject? birth = QuestKillCommitOutput is null ? conn.WorldOutput.NewObject(id) : null;
        WorldObject[] reservations = birth is null ? [] : [birth];
        bool admitted = await SendWorldBatch(conn, [], admission =>
        {
            if (create() is not { } bytes) return null;
            if (bytes.Length < 18 || BinaryPrimitives.ReadUInt16BigEndian(bytes) != InstanceEntities.CreateObjectId
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) != id)
                throw new ArgumentException("Expected one create for the declared object.");
            bytes = bytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 0);
            birth ??= admission.NewObject(id);
            OutputRecord born = OutputRecord.Create(birth, bytes, log);
            var records = new List<OutputRecord> { born };
            if (wake) records.Add(OutputRecord.AfterBirth(birth, WorldOrder.First, InstanceProtocol.EnterWakeZone(id)));
            foreach (var packet in afterBirth ?? [])
                records.Add(OutputRecord.AfterBirth(birth, RecordOrder(packet, id), packet));
            return new([.. records]);
        }, still, transport: transport, creates: reservations);
        // A producer may only start an action after the create's delimiter. No gameplay lock is held here.
        return admitted && (QuestKillCommitOutput is not null || await conn.WorldOutput.AwaitBirths([birth!]));
    }

    public static Task<bool> SendWorldCreate(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, byte[] bytes, string? log = null, Func<bool>? still = null) =>
        SendWorldCreate(conn, id, () => bytes, log, still, transport: (remote, stream, ciphers));

    // World output already batches at the frame boundary. Preserve the tick's projection order when it mixes
    // World and immediate records; predictive retains its existing one-write send batch.
    static Task<bool> SendPlayerTickBatch(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Func<Task<bool>> body) => conn.LockstepClient ? body()
            : Program.SendBatched(remote, stream, ciphers, body);

    // The caller declares one complete World record and its target. Opaque sends retain their original path.
    public static Task SendWorldRecord(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, byte[] bytes, string? log, Func<bool>? still = null,
        Func<byte[]?>? atGate = null, string? sync = null)
    {
        if (!conn.LockstepClient) return Send(remote, stream, ciphers, bytes, log, still, atGate, sync);
        if (sync is not null && !MetricsFor(ciphers).SyncDue(sync, SyncPublishMilliseconds)) return Task.CompletedTask;
        var target = conn.WorldOutput.PublishedObject(id);
        return SendWorldBatch(conn, QuestKillCommitOutput is not null && atGate is null ? [] : [target], _ =>
        {
            var current = atGate is null ? bytes : atGate();
            if (current is null || current.Length == 0) return null;
            return new(PresentedRecord(target, current, log));
        }, still, transport: (remote, stream, ciphers));
    }

    public static Task SendWorldRecords(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, Func<byte[][]?> records, string? log = null, Func<bool>? still = null, string? sync = null)
    {
        if (!conn.LockstepClient)
            return Send(remote, stream, ciphers, [], log, still, atGate: () =>
                records() is { } batch ? [.. batch.SelectMany(p => p)] : null, sync: sync);
        if (sync is not null && !MetricsFor(ciphers).SyncDue(sync, SyncPublishMilliseconds)) return Task.CompletedTask;
        var target = conn.WorldOutput.PublishedObject(id);
        return SendWorldBatch(conn, [target], _ => records() is { } batch
            ? new(batch.Where(p => p.Length > 0).Select(p => PresentedRecord(target, p, log)).ToArray()) : null,
            still, transport: (remote, stream, ciphers));
    }

    public static async Task SendWorldRemoval(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint id, string? log = null, Func<bool>? still = null, Func<byte[]?>? atGate = null)
    {
        if (!conn.LockstepClient) { await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), log, still, atGate); return; }
        var target = conn.WorldOutput.PublishedObject(id);
        bool admitted = await SendWorldBatch(conn, QuestKillCommitOutput is not null ? [] : [target], _ =>
        {
            var bytes = atGate is null ? InstanceProtocol.RemoveObject(id) : atGate();
            if (bytes is null) return null;
            bytes = bytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            return new(OutputRecord.Remove(target, WorldOrder.Last, bytes, log));
        }, still, transport: (remote, stream, ciphers));
        if (admitted && QuestKillCommitOutput is null) await target.Removed;
    }

    static OutputRecord PresentedRecord(WorldObject target, byte[] bytes, string? log = null) =>
        QuestKillCommitOutput is not null && !target.IsReady
            ? OutputRecord.AfterBirth(target, RecordOrder(bytes, target.Id), bytes, log)
            : OutputRecord.World(target, RecordOrder(bytes, target.Id), bytes, log);

    static WorldOrder RecordOrder(byte[] bytes, uint id)
    {
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) != id)
            throw new ArgumentException("Expected one record for the declared object.");
        return new(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10)));
    }

    // Explicit convenience used by producers that already own their connection's transport.
    public static Task<bool> SendWorldRecord(ClientState conn, uint id, Func<byte[]?> record,
        string? log = null, Func<bool>? still = null)
    {
        if (!conn.LockstepClient) return Predictive();
        var target = conn.WorldOutput.PublishedObject(id);
        return SendWorldBatch(conn, [target], _ =>
        {
            if (record() is not { } bytes) return null;
            if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) != id)
                throw new ArgumentException("Expected one record for the declared object.");
            var order = new WorldOrder(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)),
                BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10)));
            return new(PresentedRecord(target, bytes, log));
        }, still);
        async Task<bool> Predictive()
        {
            var wire = conn.Wire;
            bool sent = false;
            await Send(wire.Remote, wire.Stream, wire.Ciphers, [], log, still, atGate: () =>
            {
                var bytes = record(); sent = bytes is not null; return bytes;
            });
            return sent;
        }
    }
}
