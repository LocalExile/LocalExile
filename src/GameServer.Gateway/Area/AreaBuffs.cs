using GameServer.Core.Protocol.Instance;

/// <summary>
/// A buff as the client keeps it (S→C 0x1c1, InstanceEntities.AddBuff): its BuffDefinitions row, its source object and
/// the fields the message carries. U68 is the client's f32 duration in seconds (raw bits), F6c its countdown rate. Tag is
/// the server's alone (a game enum's value): two buffs of one row from one source (the client keeps them as two instances).
/// KeepInstance (the server's too): a new duration updates the instance in place, its clock running on (Winter Orb's stages).
/// </summary>
public sealed record Buff(ushort Row, uint Source = 0, ushort SourceSkill = 0, IReadOnlyList<uint>? Values = null,
    uint? U68 = null, float? F6c = null, ushort? Visual = null, (ushort Id, ushort Action)? ActiveSkill = null,
    bool AuraSource = false, byte? DisplayState = null, ushort? Stacks = null, ushort? U82 = null, IReadOnlyList<uint>? List = null,
    ushort Tag = 0, bool KeepInstance = false)
{
    public bool Same(Buff other) => U68 == other.U68 && SameBesidesDuration(other);

    /// <summary>The same buff, its countdown aside (a re-statement that computed its remaining time again).</summary>
    public bool SameBesidesDuration(Buff other) => Row == other.Row && Source == other.Source && SourceSkill == other.SourceSkill
        && F6c == other.F6c && Visual == other.Visual && ActiveSkill == other.ActiveSkill
        && AuraSource == other.AuraSource && DisplayState == other.DisplayState && Stacks == other.Stacks && U82 == other.U82
        && Tag == other.Tag && (Values ?? []).SequenceEqual(other.Values ?? []) && (List ?? []).SequenceEqual(other.List ?? []);

    /// <summary>The client's u68: a countdown of this many seconds from the instance's creation.</summary>
    public static uint Seconds(double seconds) => BitConverter.SingleToUInt32Bits((float)Math.Max(0, seconds));

    /// <summary>The bytes of this buff for <paramref name="entity"/> on <paramref name="instance"/>. Only an IsRecovery
    /// row carries GrantedStats values and only a __col44 row carries u82: an extra one makes the client read it as the
    /// counted list's header and over-read the following messages. Callers can supply either anyway, so write only what
    /// this row's definition reads, as <see cref="BuffRows.Spec"/> does.</summary>
    public byte[] Add(uint entity, ushort instance)
    {
        var info = BuffRows.Of(Row);
        return InstanceEntities.AddBuff(entity, instance, Row, Source, SourceSkill,
            info is { IsRecovery: true } ? Values : null, U68, F6c, Visual, ActiveSkill, AuraSource, DisplayState, Stacks,
            info is { HasU82: true } ? U82 : null, List);
    }
}

/// <summary>
/// The Buffs component of the area's entities. A system says which buffs an entity has, each by the client's own key
/// (its BuffDefinitions row and its source object): <see cref="Set"/> and <see cref="Clear"/>. The component owns the
/// instance ids, and the frame's end writes what changed since the frame before, in the frame's first phase: a new buff
/// as 0x1c1, a changed one as 0x1c1 on its instance (a new duration on a new one), a cleared one as 0x1c2.
/// </summary>
public sealed class AreaBuffs(GameArea area)
{
    // The client's own key of a buff on an object: its row, its source object, and whether it is the aura's source
    // (flags bit0) or the buff it gives.
    readonly record struct Key(uint Entity, ushort Row, uint Source, bool AuraSource, ushort Tag);

    // What the area wrote, with the server clock's expiry. The client can still show the icon at zero.
    readonly Dictionary<Key, (ushort Instance, Buff Buff, double Ends)> written = [];
    readonly PriorityQueue<(Key Key, ushort Instance), double> expiries = new();
    readonly Dictionary<Key, Buff?> pending = [];
    readonly Lock gate = new();
    ushort next = 1;

    /// <summary>The next buff instance of the area, for every entity in it. It wraps back to 1 below the monster ailments'
    /// fixed instances (MonsterElementalAilments.FirstInstance) and never hands out 0.</summary>
    public ushort Next
    {
        get { lock (gate) return next; }
        set { lock (gate) next = value is 0 or >= MonsterElementalAilments.FirstInstance ? (ushort)1 : value; }
    }

    ushort Take()
    {
        ushort taken = next;
        next = (ushort)(next + 1) is 0 or >= MonsterElementalAilments.FirstInstance ? (ushort)1 : (ushort)(next + 1);
        return taken;
    }

    // The buff the entity has under that key: as set this frame, or as written while its countdown runs. Under the gate.
    Buff? Current(Key key) => pending.TryGetValue(key, out var set) ? set
        : written.TryGetValue(key, out var was) && was.Ends > area.Time ? was.Buff : null;

    public void Set(uint entity, Buff buff) { lock (gate) pending[KeyOf(entity, buff)] = buff; }

    static Key KeyOf(uint entity, Buff buff) => new(entity, buff.Row, buff.Source, buff.AuraSource, buff.Tag);

    /// <summary>The entity has this buff: unless it has it already, its countdown aside (a system that states its buffs
    /// every pass, with the remaining time it computes then). An end that moved is a <see cref="Set"/>.</summary>
    public void Keep(uint entity, Buff buff)
    {
        var key = KeyOf(entity, buff);
        lock (gate)
            if (Current(key) is not { } current || !current.SameBesidesDuration(buff)) pending[key] = buff;
    }

    public void Clear(uint entity, ushort row, uint source = 0, bool auraSource = false, ushort tag = 0)
    {
        lock (gate) pending[new(entity, row, source, auraSource, tag)] = null;
    }

    /// <summary>Whether the entity has that buff now (as set this frame, or written and not run out).</summary>
    public bool Has(uint entity, ushort row, uint source = 0, bool auraSource = false, ushort tag = 0)
    {
        lock (gate) return Current(new(entity, row, source, auraSource, tag)) is not null;
    }

    /// <summary>Whether the entity has a buff of that row now, from any source.</summary>
    public bool HasRow(uint entity, ushort row)
    {
        lock (gate)
            return pending.Any(p => p.Key.Entity == entity && p.Key.Row == row && p.Value is not null)
                || written.Any(w => w.Key.Entity == entity && w.Key.Row == row && w.Value.Ends > area.Time && !pending.ContainsKey(w.Key));
    }

    /// <summary>The live buff on <paramref name="entity"/> published under wire <paramref name="instance"/>, or null
    /// when no active buff matches (expired or already cleared).</summary>
    public Buff? Find(uint entity, ushort instance)
    {
        if (instance == 0) return null;
        lock (gate)
        {
            double now = area.Time;
            foreach (var (key, entry) in written)
                if (key.Entity == entity && entry.Instance == instance && entry.Ends > now)
                    return pending.TryGetValue(key, out var next) ? next : entry.Buff;
        }
        return null;
    }

    /// <summary>A client that joins the area is written every buff the area's entities have, with their instances.</summary>
    public void Join(Player player)
    {
        area.AssertOwner();
        lock (gate)
        {
            double now = area.Time;
            foreach (var (key, (instance, buff, ends)) in written)
            {
                if (ends <= now) continue;
                var current = double.IsFinite(ends) ? buff with { U68 = Buff.Seconds((ends - now) * CountdownRate(buff)) } : buff;
                area.Delta.Add(key.Entity, current.Add(key.Entity, instance), null, only: player);
            }
        }
    }

    /// <summary>The entity's buffs of one kind (<paramref name="group"/>: a player's own auras, say) are these now, kept as
    /// <see cref="Keep"/> keeps one; the others of that kind end.</summary>
    public void Replace(uint entity, Func<Buff, bool> group, IEnumerable<Buff> buffs)
    {
        var now = buffs.ToDictionary(b => KeyOf(entity, b));
        lock (gate)
        {
            foreach (var (key, was) in written)
                if (key.Entity == entity && group(was.Buff) && !now.ContainsKey(key)) pending[key] = null;
            foreach (var (key, buff) in pending.ToList())
                if (key.Entity == entity && buff is not null && group(buff) && !now.ContainsKey(key)) pending[key] = null;
            foreach (var (key, buff) in now)
                if (Current(key) is not { } current || !current.SameBesidesDuration(buff)) pending[key] = buff;
        }
    }

    /// <summary>The entity's buffs of a row end, whatever their source (a buff the client keeps one of per object: an
    /// elemental ailment).</summary>
    public void ClearRow(uint entity, ushort row) => ClearWhere(k => k.Entity == entity && k.Row == row);

    /// <summary>The entity's buffs from a source end (the source left: a minion's aura on its owner).</summary>
    public void ClearSource(uint entity, uint source) => ClearWhere(k => k.Entity == entity && k.Source == source);

    /// <summary>The entity's buff that a system tagged with its own application number ends, whatever its row and source.</summary>
    public void ClearTag(uint entity, ushort tag) => ClearWhere(k => k.Entity == entity && k.Tag == tag);

    /// <summary>Every buff the entity has ends (a player's combat retired: its death, a reload); the systems state theirs
    /// again.</summary>
    public void ClearAll(uint entity) => ClearWhere(k => k.Entity == entity);

    void ClearWhere(Func<Key, bool> which)
    {
        lock (gate)
        {
            foreach (var key in written.Keys.Where(which)) pending[key] = null;
            foreach (var key in pending.Keys.Where(which).ToList()) pending[key] = null;
        }
    }

    /// <summary>An entity left the area: its buffs went with its object.</summary>
    public void Removed(uint entity)
    {
        lock (gate)
        {
            foreach (var key in written.Keys.Where(k => k.Entity == entity).ToList()) written.Remove(key);
            foreach (var key in pending.Keys.Where(k => k.Entity == entity).ToList()) pending.Remove(key);
        }
    }

    static double CountdownRate(Buff buff) => buff.F6c is > 0 and var rate ? rate : 1;

    public void Publish()
    {
        area.AssertOwner();
        lock (gate)
        {
            if (pending.Count == 0 && expiries.Count == 0) return;
            double now = area.Time;
            // A refresh or explicit removal may have superseded a queued deadline.
            while (expiries.TryPeek(out var expired, out double deadline) && deadline <= now)
            {
                expiries.Dequeue();
                if (!written.TryGetValue(expired.Key, out var current)
                    || current.Instance != expired.Instance || current.Ends != deadline) continue;
                written.Remove(expired.Key);
                area.Delta.Add(expired.Key.Entity, InstanceEntities.RemoveBuff(expired.Key.Entity, expired.Instance));
            }
            foreach (var (key, buff) in pending)
            {
                uint entity = key.Entity;
                bool had = written.TryGetValue(key, out var was);
                if (buff is null)
                {
                    if (!had) continue;
                    written.Remove(key);
                    area.Delta.Add(entity, InstanceEntities.RemoveBuff(entity, was.Instance));
                    continue;
                }
                bool timed = buff.U68 is not null;
                if (had && !timed && was.Buff.Same(buff)) continue;
                // The client runs a buff's countdown from its instance's creation: a timed refresh (even with the same
                // U68) or duration change needs a new instance unless KeepInstance updates it in place.
                if (had && (timed || was.Buff.U68 is not null) && !buff.KeepInstance)
                {
                    area.Delta.Add(entity, InstanceEntities.RemoveBuff(entity, was.Instance));
                    had = false;
                }
                ushort instance = had ? was.Instance : Take();
                double ends = buff.U68 is uint u68 ? now + BitConverter.UInt32BitsToSingle(u68) / CountdownRate(buff)
                    : double.PositiveInfinity;
                written[key] = (instance, buff, ends);
                if (double.IsFinite(ends)) expiries.Enqueue((key, instance), ends);
                area.Delta.Add(entity, buff.Add(entity, instance));
            }
            pending.Clear();
        }
    }
}
