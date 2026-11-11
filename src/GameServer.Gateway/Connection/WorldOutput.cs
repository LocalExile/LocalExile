using System.Buffers.Binary;

/// <summary>The native common header's scheduling fields, independent of its update target.</summary>
public readonly record struct WorldOrder(uint Key, ushort Checkpoint = 0) : IComparable<WorldOrder>
{
    public static readonly WorldOrder First = new(0), Last = new(uint.MaxValue);
    // 142183ce0 consumes the first and last phases without comparing checkpoints; 142184680 compares them unsigned.
    public int CompareTo(WorldOrder other)
    {
        int key = Key.CompareTo(other.Key);
        return key != 0 || Key is 0 or uint.MaxValue ? key : Checkpoint.CompareTo(other.Checkpoint);
    }
}

/// <summary>One object's incarnation, never resolved again by numeric ID. The producer retires it with the object.</summary>
public sealed class WorldObject
{
    public enum BirthState { Reserved, Admitted, Ready, Retired }
    public readonly WorldOutput Owner;
    public BirthState State;
    public bool RemovalRequested;
    public readonly TaskCompletionSource<bool> Invalidated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<bool> Removal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<bool> Birth = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WorldObject(WorldOutput owner, uint id, bool ready)
    {
        Owner = owner; Id = id; State = ready ? BirthState.Ready : BirthState.Reserved;
        if (ready) Birth.SetResult(true);
    }
    public uint Id { get; }
    public long? CreationFrame { get; set; }
    public bool IsReady => Owner.IsReady(this);
    /// <summary>Cancel an unadmitted reservation. Published/admitted objects require an explicit Remove record.</summary>
    public void Retire() => Owner.Retire(this);
    /// <summary>Successful removal delimiter. Await outside gameplay gates before reusing this numeric ID.</summary>
    public Task<bool> Removed => Removal.Task;
}

/// <summary>One explicitly framed record. Construction owns a copy; transmission never mutates the producer's bytes.</summary>
public sealed class OutputRecord
{
    public enum RecordKind { Immediate, World, Create, AfterBirth, Remove }
    public RecordKind Kind { get; }
    public WorldObject? Object { get; }
    public WorldOrder Order { get; }
    public string? Log { get; }
    public bool SharedView { get; }
    readonly byte[] bytes;
    OutputRecord(RecordKind kind, byte[] bytes, string? log, WorldObject? target = null, WorldOrder order = default, bool sharedView = false)
    {
        Kind = kind; this.bytes = bytes.ToArray(); Log = log; Object = target; Order = order; SharedView = sharedView;
        if (kind == RecordKind.Immediate) return;
        // Only a declared SINGLE record is checked. This is not a parser or a classifier for opaque Send(byte[]).
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) != target!.Id
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)) != order.Key
            || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10)) != order.Checkpoint)
            throw new ArgumentException("The single World record does not match its declared target/order.", nameof(bytes));
        if ((BinaryPrimitives.ReadUInt16BigEndian(bytes) == 0x2bb) != (kind == RecordKind.Create))
            throw new ArgumentException("Creates require an explicit birth record.", nameof(bytes));
        if ((BinaryPrimitives.ReadUInt16BigEndian(bytes) == 0x2bd) != (kind == RecordKind.Remove))
            throw new ArgumentException("Removals require an explicit lifecycle record.", nameof(bytes));
    }
    public byte[] CopyBytes() => bytes.ToArray();
    /// <summary>The life this record gives its object's life pool (S→C 0x1b1 with pool 0); null for any other record.
    /// 0 is the object's death on a client whose copy lived.</summary>
    public uint? LifeWritten => Kind is RecordKind.World or RecordKind.AfterBirth && bytes.Length >= 21
        && BinaryPrimitives.ReadUInt16BigEndian(bytes) == 0x1b1 && bytes[20] == 0
        ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) : null;
    public OutputRecord AsSharedView() => new(Kind, bytes, Log, Object, Order, sharedView: true);
    public static OutputRecord Immediate(byte[] bytes, string? log = null) => new(RecordKind.Immediate, bytes, log);
    public static OutputRecord World(WorldObject target, WorldOrder order, byte[] bytes, string? log = null) =>
        new(RecordKind.World, bytes, log, target, order);
    public static OutputRecord Create(WorldObject target, byte[] bytes, string? log = null) =>
        Create(target, WorldOrder.Last, bytes, log);
    // Bootstrap/predictive retain their producer's actual header; active lockstep validates the native tail phase.
    public static OutputRecord Create(WorldObject target, WorldOrder order, byte[] bytes, string? log = null) =>
        new(RecordKind.Create, bytes, log, target, order);
    /// <summary>Already committed presentation only: no action/position factory may run early and use this as a delay.</summary>
    public static OutputRecord AfterBirth(WorldObject target, WorldOrder order, byte[] bytes, string? log = null) =>
        new(RecordKind.AfterBirth, bytes, log, target, order);
    /// <summary>Invalidate future action factories now; preserve admitted output and publish removal after birth.</summary>
    public static OutputRecord Remove(WorldObject target, WorldOrder order, byte[] bytes, string? log = null) =>
        new(RecordKind.Remove, bytes, log, target, order);
}

public sealed class OutputBatch(params OutputRecord[] records)
{
    public IReadOnlyList<OutputRecord> Records { get; } = Array.AsReadOnly(records.ToArray());
    public static readonly OutputBatch Empty = new();
}

/// <summary>Synchronous admission scope. Tracks even dynamic birth reservations if its factory fails or is suppressed.</summary>
public sealed class WorldAdmission
{
    readonly WorldOutput owner;
    readonly List<WorldObject> births;
    bool finished;
    public WorldAdmission(WorldOutput owner, IReadOnlyList<WorldObject> creates)
    {
        this.owner = owner; births = [.. creates];
    }
    public WorldObject NewObject(uint id)
    {
        if (finished) throw new InvalidOperationException("World admission has ended.");
        var result = owner.NewObject(id); births.Add(result); return result;
    }
    // A synchronous nested producer owns only its new reservations until it returns an accepted batch.
    // Suppressing it cancels those reservations without cancelling earlier work in the outer transaction.
    public OutputBatch? Capture(Func<WorldAdmission, OutputBatch?> atGate)
    {
        if (finished) throw new InvalidOperationException("World admission has ended.");
        var child = new WorldAdmission(owner, []);
        bool accepted = false;
        try
        {
            if (atGate(child) is not { } batch) return null;
            births.AddRange(child.births);
            child.Finish(true); accepted = true;
            return batch;
        }
        finally { if (!accepted) child.Finish(false); }
    }
    public IReadOnlyList<WorldObject> Births => births;
    public void Finish(bool accepted)
    {
        finished = true;
        if (!accepted) foreach (var birth in births) owner.CancelReservation(birth);
    }
}

/// <summary>Connection-owned output and birth lifetimes. Queued state contains bytes/metadata, never module delegates.</summary>
public sealed class WorldOutput
{
    public sealed record Packet(byte[] Bytes, string? Log, WorldOrder Order, WorldObject? Creates = null, WorldObject? Removes = null, bool IsWorld = true, bool SharedView = false);
    public sealed record Frame(IReadOnlyList<Packet> Packets, IReadOnlyList<WorldObject> Births, IReadOnlyList<WorldObject> Removals);
    readonly object sync = new();
    readonly Dictionary<uint, WorldObject> objects = [];
    readonly TaskCompletionSource<bool> closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<Packet> frame = [];
    readonly List<OutputRecord> prepared = [];
    bool closed, started;
    long number;
    public bool Started { get { lock (sync) return started; } }
    public bool Closed { get { lock (sync) return closed; } }
    public Task ClosedTask => closedSignal.Task;

    public WorldObject NewObject(uint id)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (objects.ContainsKey(id)) throw new InvalidOperationException("World object ID is still reserved or published; await its removal before replacement.");
            var birth = new WorldObject(this, id, false); objects.Add(id, birth); return birth;
        }
    }
    /// <summary>Explicit bootstrap/previously established object; never use this to bypass a pending creation.</summary>
    public WorldObject ExistingObject(uint id)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (objects.ContainsKey(id)) throw new InvalidOperationException("World object ID already has an incarnation.");
            var established = new WorldObject(this, id, true); objects.Add(id, established); return established;
        }
    }
    // The caller identifies an already published object. A pending birth is returned as pending, never bypassed.
    public WorldObject PublishedObject(uint id)
    {
        lock (sync)
        {
            if (objects.TryGetValue(id, out var value)) return value;
            return ExistingObject(id);
        }
    }

    public bool IsReady(WorldObject target)
    {
        lock (sync) return Published(target) && !target.RemovalRequested;
    }
    bool Published(WorldObject target) => !closed && ReferenceEquals(target.Owner, this)
        && target.State == WorldObject.BirthState.Ready;

    public async Task<bool> AwaitBirths(IReadOnlyList<WorldObject> targets)
    {
        var pending = targets.Select(t => t.Birth.Task).Where(t => !t.IsCompleted).Distinct().ToList();
        bool Invalid() { lock (sync) return closed || targets.Any(t => t.State == WorldObject.BirthState.Retired || t.RemovalRequested); }
        if (Invalid()) return false;
        if (pending.Count == 0) return targets.All(IsReady);
        // A later prerequisite's retirement must cancel this wait even if the first birth never completes.
        Task invalidated = Task.WhenAny(targets.Select(t => (Task)t.Invalidated.Task).Append(closedSignal.Task));
        while (pending.Count > 0)
        {
            await Task.WhenAny(pending.Cast<Task>().Append(invalidated));
            if (Invalid()) return false;
            foreach (var ready in pending.Where(t => t.IsCompleted).ToArray())
            {
                if (!await ready) return false;
                pending.Remove(ready);
            }
        }
        return targets.All(IsReady);
    }
    public void CheckOwner(WorldObject target)
    {
        if (!ReferenceEquals(target.Owner, this)) throw new ArgumentException("A World incarnation belongs to another connection.");
    }
    public void CancelReservation(WorldObject target)
    {
        lock (sync)
        {
            CheckOwner(target);
            if (target.State == WorldObject.BirthState.Reserved) Retire(target);
        }
    }
    public void Retire(WorldObject target)
    {
        lock (sync)
        {
            CheckOwner(target);
            if (target.State is WorldObject.BirthState.Admitted or WorldObject.BirthState.Ready)
                throw new InvalidOperationException("An admitted object needs an explicit Remove record; accepted creation/presentation cannot be cancelled.");
            target.State = WorldObject.BirthState.Retired;
            if (objects.GetValueOrDefault(target.Id) == target) objects.Remove(target.Id);
            target.Invalidated.TrySetResult(false);
            target.Birth.TrySetResult(false);
            target.Removal.TrySetResult(false);
        }
    }
    public void Close()
    {
        lock (sync)
        {
            if (closed) return;
            closed = true;
            foreach (var target in objects.Values)
            {
                target.State = WorldObject.BirthState.Retired; target.Birth.TrySetResult(false);
                target.Invalidated.TrySetResult(false); target.Removal.TrySetResult(false);
            }
            closedSignal.TrySetResult(false);
            objects.Clear(); frame.Clear(); prepared.Clear();
        }
    }
    public void Begin(long frameNumber)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (started) throw new InvalidOperationException("World frames already started.");
            number = frameNumber; started = true;
        }
    }

    // Called under SendGate. Validate the complete batch before admitting any of it.
    public IReadOnlyList<Packet> Admit(OutputBatch batch, WorldAdmission admission, bool framed)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var births = admission.Births;
            if (births.Distinct().Count() != births.Count) throw new ArgumentException("Duplicate birth declaration.");
            foreach (var birth in births)
            {
                CheckOwner(birth);
                if (birth.State != WorldObject.BirthState.Reserved
                    || batch.Records.Count(r => r.Kind == OutputRecord.RecordKind.Create && ReferenceEquals(r.Object, birth)) != 1)
                    throw new ArgumentException("Every reserved birth needs exactly one create in its admitted batch.");
            }
            // Two producers can each notice one object leave (its taker, and a view's synchronisation of what is left).
            // A Remove of an incarnation whose removal is already admitted or written repeats that removal and adds
            // nothing; the rest of its batch is admitted. Any other record for a leaving incarnation is still refused.
            var repeated = new HashSet<WorldObject>();
            var records = batch.Records.Where(r => r.Kind != OutputRecord.RecordKind.Remove || r.Object is not { } leaving
                || !(leaving.State == WorldObject.BirthState.Retired || leaving.RemovalRequested || !repeated.Add(leaving))).ToArray();
            var precedingCreates = new HashSet<WorldObject>();
            var removing = new HashSet<WorldObject>();
            foreach (var record in records)
            {
                if (record.Object is not { } target) continue;
                CheckOwner(target);
                if (target.State == WorldObject.BirthState.Retired || target.RemovalRequested || removing.Contains(target))
                    throw new ArgumentException("Retired or removing World incarnation.");
                if (record.Kind == OutputRecord.RecordKind.Create)
                {
                    if (!births.Contains(target)) throw new ArgumentException("Undeclared World birth.");
                    if (framed && record.Order != WorldOrder.Last)
                        throw new ArgumentException("An active lockstep create must carry ffffffff/0000; output never rekeys bytes.");
                    precedingCreates.Add(target);
                }
                else if (record.Kind == OutputRecord.RecordKind.World && !IsReady(target))
                    throw new ArgumentException("A stateful World update must await its birth before admission.");
                else if (record.Kind is OutputRecord.RecordKind.AfterBirth or OutputRecord.RecordKind.Remove)
                {
                    if (!Published(target) && !precedingCreates.Contains(target) && target.State != WorldObject.BirthState.Admitted)
                        throw new ArgumentException("Prepared output needs an admitted or preceding birth.");
                    if (record.Kind == OutputRecord.RecordKind.Remove)
                    {
                        // 142183ce0's tail follows object ticks. All 5,153 coastw1/ret2 removals use it;
                        // checkpoints include 0, 4 and 8, which this whole phase ignores.
                        if (framed && record.Order.Key != uint.MaxValue)
                            throw new ArgumentException("An active lockstep removal must carry key ffffffff; output never rekeys bytes.");
                        removing.Add(target);
                    }
                }
            }
            foreach (var birth in births) { birth.State = WorldObject.BirthState.Admitted; birth.CreationFrame = framed ? number : null; }
            foreach (var target in removing) { target.RemovalRequested = true; target.Invalidated.TrySetResult(false); }
            List<Packet> immediate = [];
            foreach (var record in records)
            {
                if (framed && (record.Kind is OutputRecord.RecordKind.AfterBirth or OutputRecord.RecordKind.Remove)
                    && !Published(record.Object!))
                { prepared.Add(record); continue; }
                var packet = PacketOf(record);
                if (framed && record.Kind != OutputRecord.RecordKind.Immediate) frame.Add(packet);
                else immediate.Add(packet);
            }
            return immediate;
        }
    }
    static Packet PacketOf(OutputRecord record) => new(record.CopyBytes(), record.Log, record.Order,
        record.Kind == OutputRecord.RecordKind.Create ? record.Object : null,
        record.Kind == OutputRecord.RecordKind.Remove ? record.Object : null,
        record.Kind != OutputRecord.RecordKind.Immediate, record.SharedView);

    public Frame TakeFrame()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            // LINQ OrderBy is stable: checkpoints ignored by a native whole phase retain their admission order too.
            var packets = frame.OrderBy(p => p.Order).ToArray(); frame.Clear();
            return new(packets, packets.Select(p => p.Creates).OfType<WorldObject>().ToArray(),
                packets.Select(p => p.Removes).OfType<WorldObject>().ToArray());
        }
    }
    public void Written(IReadOnlyList<WorldObject> births, IReadOnlyList<WorldObject> removals, bool frameEnded)
    {
        lock (sync)
        {
            if (closed) return;
            if (frameEnded) number++;
            foreach (var birth in births)
                if (birth.State == WorldObject.BirthState.Admitted)
                    birth.State = WorldObject.BirthState.Ready;
            foreach (var target in removals)
            {
                target.State = WorldObject.BirthState.Retired;
                if (objects.GetValueOrDefault(target.Id) == target) objects.Remove(target.Id);
            }
            // Accepted presentations survive logical retirement and precede the removal in N+1.
            foreach (var record in prepared.Where(r => Published(r.Object!))) frame.Add(PacketOf(record));
            prepared.RemoveAll(r => Published(r.Object!) || r.Object!.State == WorldObject.BirthState.Retired);
            // Populate the next queue before any continuation can admit an action or a same-ID replacement.
            foreach (var birth in births) birth.Birth.TrySetResult(true);
            foreach (var target in removals) target.Removal.TrySetResult(true);
        }
    }
}
