using System.Buffers.Binary;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // Producers retain packet boundaries while committing a multi-object projection at the send gate.
    // Presentation is already committed data; stateful actions must await the target before entering this scope.
    sealed class WorldRecordBuilder(Player player, WorldAdmission? admission)
    {
        readonly List<OutputRecord> records = [];
        bool UsesWorld => admission is not null && (player.Connection.LockstepClient || InAreaFrame(player));
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
            var target = player.Connection.WorldOutput.PublishedObject(id);
            records.Add(OutputRecord.World(target, RecordOrder(bytes, id), bytes, log));
        }
        public void Presentation(uint id, byte[] bytes, string? log = null)
        {
            if (bytes.Length == 0) return;
            if (!UsesWorld) { Immediate(bytes, log); return; }
            var target = player.Connection.WorldOutput.PublishedObject(id);
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
            records.Add(OutputRecord.Remove(player.Connection.WorldOutput.PublishedObject(id), WorldOrder.Last, bytes, log));
        }
        public OutputBatch Build() => records.Count == 0 ? OutputBatch.Empty : UsesWorld ? new([.. records])
            : new(OutputRecord.Immediate([.. records.SelectMany(r => r.CopyBytes())]));
    }

    public static Task<bool> SendWorldCreate(IPlayer player, uint id, byte[] bytes, string? log = null) =>
        SendWorldCreate(PlayerOf(player), id, () => bytes, log);

    public static Task SendWorldRecord(IPlayer modPlayer, uint id, byte[] bytes, string? log = null)
    {
        var player = PlayerOf(modPlayer);
        return SendWorldRecord(player, id, bytes, log);
    }

    public static Task SendWorldRemoval(IPlayer modPlayer, uint id, string? log = null)
    {
        var player = PlayerOf(modPlayer);
        return SendWorldRemoval(player, id, log);
    }

    public static async Task SendWorldReceipt(Player player, PlayerMinions.Output output)
    {
        if (output.CreatesObject)
            await SendWorldCreate(player, output.WorldId!.Value, output.Packet, output.Log);
        else if (output.RemovesObject)
            await SendWorldRemoval(player, output.WorldId!.Value, output.Log);
        else if (output.WorldId is { } id)
            await SendWorldRecord(player, id, output.Packet, output.Log);
        else await Send(player, output.Packet, output.Log);
    }

    // In the area's frame, with the connection's frames begun: a world record is the area's (Program.SendRecords puts it in
    // the frame every client of the area is written), whatever the client's network mode.
    static bool InAreaFrame(Player player) => player.Area.Owned && player.Connection.WorldOutput.Started;

    // A declared single create; its common header is not inferred from an opaque concatenated send.
    // `awaitBirth` holds the producer until the create is written. A presentation object that no later action depends
    // on (a projectile, an effect) does not need that hold: a lockstep create is written at the next frame end, so each
    // held create stalled its producer's queue for up to a frame.
    public static async Task<bool> SendWorldCreate(Player player, uint id, Func<byte[]?> create,
        string? log = null, Func<bool>? still = null, bool wake = false,  bool awaitBirth = true,
        // Records for the object that may be written with its create and read after it (a monster's wake and its rarity
        // glow): each waits for the birth otherwise, which on a lockstep client is a frame per object. An area's rare and
        // magic monsters alone held the entry's population for 3.2 s that way (live 2026-11-17).
        Func<IReadOnlyList<byte[]>?>? afterCreation = null)
    {
        if (!player.Connection.LockstepClient && !InAreaFrame(player))
        {
            bool sent = false;
            var viewer = Program.ViewOf.Value;
            await Send(player, [], log, still, atGate: () =>
            {
                if (create() is not { } bytes) return null;
                sent = true;
                // A copy: the writer encrypts its buffer in place, and a producer that hands in the same array again would
                // send ciphertext as a header (live 2026-11-17: a monster created twice sent 244 bytes of noise whose id
                // read as 0xed68, and the client crashed).
                var created = bytes.ToArray();
                byte[]? wakeBytes = wake ? InstanceProtocol.EnterWakeZone(id) : null;
                var after = afterCreation?.Invoke();
                if (viewer is null && player.Area.Delta.ViewerOf(id, player) is null)
                {
                    player.Area.Objects.RecordDirect(id, created, AreaDelta.Kind.Create, player, log);
                    if (wakeBytes is not null) player.Area.Objects.RecordDirect(id, wakeBytes, AreaDelta.Kind.World, player);
                    if (after is { Count: > 0 })
                        foreach (var record in after.Where(r => r.Length > 0))
                            player.Area.Objects.RecordDirect(id, record, AreaDelta.Kind.World, player);
                }
                bytes = created;
                if (wakeBytes is not null) bytes = [.. bytes, .. wakeBytes];
                if (after is { Count: > 0 })
                    bytes = [.. bytes, .. after.SelectMany(r => r)];
                return bytes;
            });
            return sent;
        }
        // Reserve before any mutation exposes the actor to concurrent action producers.
        WorldObject? birth = QuestKillCommitOutput is null ? player.Connection.WorldOutput.NewObject(id) : null;
        WorldObject[] reservations = birth is null ? [] : [birth];
        bool admitted = await SendWorldBatch(player, [], admission =>
        {
            if (create() is not { } bytes) return null;
            if (bytes.Length < 18 || BinaryPrimitives.ReadUInt16BigEndian(bytes) != InstanceEntities.CreateObjectId
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) != id)
                throw new ArgumentException("Expected one create for the declared object.");
            bytes = bytes.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 0);
            birth ??= admission.NewObject(id);
            List<OutputRecord> batch = [OutputRecord.Create(birth, bytes, log)];
            if (wake) batch.Add(OutputRecord.AfterBirth(birth, WorldOrder.First, InstanceProtocol.EnterWakeZone(id)));
            if (afterCreation?.Invoke() is { Count: > 0 } after)
                foreach (var record in after.Where(r => r.Length > 0))
                    batch.Add(OutputRecord.AfterBirth(birth, RecordOrder(record, id), record));
            return new([.. batch]);
        }, still, creates: reservations);
        // A producer may only start an action after the create's delimiter. No gameplay lock is held here.
        return admitted && (!awaitBirth || QuestKillCommitOutput is not null || await player.Connection.WorldOutput.AwaitBirths([birth!]));
    }

    public static Task<bool> SendWorldCreate(Player player, uint id, byte[] bytes, string? log = null, Func<bool>? still = null) =>
        SendWorldCreate(player, id, () => bytes, log, still);

    // World output already batches at the frame boundary. Preserve the tick's projection order when it mixes
    // World and immediate records; predictive retains its existing one-write send batch.
    static Task<bool> SendPlayerTickBatch(Player player, Func<Task<bool>> body) => player.Connection.LockstepClient ? body()
            : Program.SendBatched(player, body);

    // The caller declares one complete World record and its target. Opaque sends retain their original path.
    public static Task SendWorldRecord(Player player, uint id, byte[] bytes, string? log, Func<bool>? still = null,
        Func<byte[]?>? atGate = null, string? sync = null)
    {
        if (!player.Connection.LockstepClient && !InAreaFrame(player))
        {
            var viewer = Program.ViewOf.Value;
            return Send(player, bytes, log, still, atGate: () =>
            {
                var current = atGate is null ? bytes : atGate();
                if (current is { Length: > 0 } && viewer is null && player.Area.Delta.ViewerOf(id, player) is null)
                    player.Area.Objects.RecordDirect(id, current, AreaDelta.Kind.World, player, log);
                return current;
            }, sync);
        }
        if (sync is not null && !player.Connection.Performance.SyncDue(sync, SyncPublishMilliseconds)) return Task.CompletedTask;
        var target = player.Connection.WorldOutput.PublishedObject(id);
        return SendWorldBatch(player, QuestKillCommitOutput is not null && atGate is null ? [] : [target], _ =>
        {
            var current = atGate is null ? bytes : atGate();
            if (current is null || current.Length == 0) return null;
            return new(PresentedRecord(target, current, log));
        }, still);
    }

    public static Task SendWorldRecords(Player player, uint id, Func<byte[][]?> records, string? log = null, Func<bool>? still = null, string? sync = null)
    {
        if (!player.Connection.LockstepClient && !InAreaFrame(player))
        {
            var viewer = Program.ViewOf.Value;
            return Send(player, [], log, still, atGate: () =>
            {
                if (records() is not { } batch) return null;
                if (viewer is null && player.Area.Delta.ViewerOf(id, player) is null)
                    foreach (var p in batch)
                        if (p.Length > 0) player.Area.Objects.RecordDirect(id, p, AreaDelta.Kind.World, player, log);
                return [.. batch.SelectMany(p => p)];
            }, sync: sync);
        }
        if (sync is not null && !player.Connection.Performance.SyncDue(sync, SyncPublishMilliseconds)) return Task.CompletedTask;
        var target = player.Connection.WorldOutput.PublishedObject(id);
        return SendWorldBatch(player, [target], _ => records() is { } batch
            ? new(batch.Where(p => p.Length > 0).Select(p => PresentedRecord(target, p, log)).ToArray()) : null,
            still);
    }

    public static async Task SendWorldRemoval(Player player, uint id, string? log = null, Func<bool>? still = null, Func<byte[]?>? atGate = null)
    {
        if (!player.Connection.LockstepClient && !InAreaFrame(player))
        {
            var viewer = Program.ViewOf.Value;
            await Send(player, InstanceProtocol.RemoveObject(id), log, still, atGate: () =>
            {
                var current = atGate is null ? InstanceProtocol.RemoveObject(id) : atGate();
                if (current is { Length: > 0 } && viewer is null && player.Area.Delta.ViewerOf(id, player) is null)
                    player.Area.Objects.RecordDirect(id, current, AreaDelta.Kind.Remove, player, log);
                return current;
            });
        }
        else
        {
            var target = player.Connection.WorldOutput.PublishedObject(id);
            bool admitted = await SendWorldBatch(player, QuestKillCommitOutput is not null ? [] : [target], _ =>
            {
                var bytes = atGate is null ? InstanceProtocol.RemoveObject(id) : atGate();
                if (bytes is null) return null;
                bytes = bytes.ToArray();
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
                return new(OutputRecord.Remove(target, WorldOrder.Last, bytes, log));
            }, still);
            if (admitted && QuestKillCommitOutput is null) await target.Removed;
        }
        // A monster can leave the area without dying here (a minion's body, a wisp, a scripted encounter's removal; the
        // golem and minion queues send theirs through this same call): the map overlay's "X monsters remain" (S→C 0x1a0)
        // counts the living ones again. A corpse that is consumed changes nothing, and the count is only sent when it
        // really differs (MonsterRemaining.cs).
        await SendRemainingMonsters(player);
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
    public static Task<bool> SendWorldRecord(Player player, uint id, Func<byte[]?> record,
        string? log = null, Func<bool>? still = null)
    {
        if (!player.Connection.LockstepClient && !InAreaFrame(player)) return Predictive();
        var target = player.Connection.WorldOutput.PublishedObject(id);
        return SendWorldBatch(player, [target], _ =>
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
            bool sent = false;
            var viewer = Program.ViewOf.Value;
            await Send(player, [], log, still, atGate: () =>
            {
                var bytes = record();
                sent = bytes is not null;
                if (bytes is { Length: > 0 } && viewer is null && player.Area.Delta.ViewerOf(id, player) is null)
                    player.Area.Objects.RecordDirect(id, bytes, AreaDelta.Kind.World, player, log);
                return bytes;
            });
            return sent;
        }
    }
}
