using System.Collections.Concurrent;

/// <summary>Transient Harbinger summons and their own active clock, shared across area leases. The clock advances
/// through loading, freezes at exact pause boundaries, and cannot be advanced by the departed connection.</summary>
sealed class HarbingerRoster
{
    static readonly ConcurrentDictionary<string, HarbingerRoster> Characters = new(StringComparer.OrdinalIgnoreCase);
    public static HarbingerRoster For(string character) => Characters.GetOrAdd(character, _ => new());
    public sealed class Member(long id, HarbingerCompanionCatalog.Companion source, HarbingerCycles cycles)
    {
        public long Id { get; } = id;
        public HarbingerCompanionCatalog.Companion Source { get; } = source;
        public HarbingerCycles Cycles { get; } = cycles;
    }
    public sealed record Active(Member Owner, HarbingerCycles.Buff Buff)
    { public double RemainingBase { get; init; } }
    public sealed record Update(Member Owner, HarbingerCycles.Change Change);
    readonly object gate = new();
    readonly Dictionary<ushort, Member> members = [];
    readonly Dictionary<(long Member, int Effect), (Member Owner, HarbingerCycles.Buff Buff, EffectClock Clock)> recipients = [];
    double recipientRate = 1;
    Guid lease;

    public void SetRecipientRate(Guid owner, DateTimeOffset now, double rate)
    {
        lock (gate)
        {
            if (lease != owner) return;
            AdvanceTime(now);
            if (recipientRate == rate) return;
            foreach (var recipient in recipients.Values) recipient.Clock.ChangeRate(elapsed, rate);
            recipientRate = rate;
        }
    }

    void PruneRecipients()
    {
        foreach (var (key, value) in recipients.ToArray())
        {
            value.Clock.Advance(elapsed);
            if (value.Clock.Remaining <= 1e-9 || !members.Values.Any(m => m.Id == key.Member)) recipients.Remove(key);
        }
    }
    long nextId;
    long elapsedTicks;
    double elapsed => elapsedTicks / (double)TimeSpan.TicksPerSecond;
    DateTimeOffset? sampled;
    bool paused;

    void AdvanceTime(DateTimeOffset now)
    {
        if (sampled is { } previous && now < previous) return;
        if (sampled is { } last && !paused) elapsedTicks += Math.Max(0, (now - last).Ticks);
        sampled = now;
    }

    public void Attach(Guid owner, DateTimeOffset now)
    {
        lock (gate) { AdvanceTime(now); lease = owner; paused = false; }
    }

    public void Pause(Guid owner, DateTimeOffset now, bool pause)
    {
        lock (gate)
            if (lease == owner) { AdvanceTime(now); paused = pause; }
    }

    public bool Activate(Guid owner, HarbingerCompanionCatalog.Companion source, int ownRecovery, DateTimeOffset now)
    {
        lock (gate)
        {
            if (lease != owner || paused || source.Limit < 1) return false;
            AdvanceTime(now);
            if (members.Remove(source.Node, out var old)) old.Cycles.Clear();
            members[source.Node] = new(++nextId, source, new(source.Buffs, elapsed, ownRecovery));
            return true;
        }
    }

    public Member[] Read(Guid owner, IEnumerable<ushort> allowed, DateTimeOffset now)
    {
        lock (gate)
        {
            if (lease != owner) return [];
            AdvanceTime(now);
            var valid = allowed.ToHashSet();
            foreach (var (node, member) in members.ToArray())
                if (!valid.Contains(node)) { member.Cycles.Clear(); members.Remove(node); }
            return [.. members.Values];
        }
    }

    public Update[] Step(Guid owner, DateTimeOffset now, bool allowCast, bool channelled, Func<Member, bool>? canStart = null)
    {
        lock (gate)
        {
            if (lease != owner || paused) return [];
            AdvanceTime(now);
            var updates = members.Values.Select(m => new Update(m,
                m.Cycles.Advance(elapsed, allowCast && (!m.Source.RequiresChannelling || channelled)
                    && (canStart?.Invoke(m) ?? true)))).ToArray();
            foreach (var update in updates)
                foreach (var buff in update.Change.Completed)
                    recipients[(update.Owner.Id, buff.Skill.Effect)] = (update.Owner, buff,
                        new EffectClock(buff.Skill.DurationMs / 1000.0, buff.StartedAt, recipientRate));
            PruneRecipients();
            return updates;
        }
    }

    public Active[] Buffs(Guid owner, DateTimeOffset now)
    {
        lock (gate)
        {
            if (lease != owner) return [];
            AdvanceTime(now);
            PruneRecipients();
            return [.. recipients.Values.Select(r => new Active(r.Owner, r.Buff with { EndsAt = r.Clock.Deadline })
                { RemainingBase = r.Clock.Remaining })];
        }
    }

    public double Time(Guid owner, DateTimeOffset now)
    {
        lock (gate) { if (lease != owner) return elapsed; AdvanceTime(now); return elapsed; }
    }

    public bool Contains(Guid owner, long id)
    {
        lock (gate) return lease == owner && members.Values.Any(m => m.Id == id);
    }

    public void Clear(Guid owner)
    {
        lock (gate)
            if (lease == owner) { foreach (var m in members.Values) m.Cycles.Clear(); members.Clear(); recipients.Clear(); }
    }

    public void Release(Guid owner, bool transfer, DateTimeOffset now)
    {
        lock (gate)
        {
            if (lease != owner) return;
            AdvanceTime(now);
            if (!transfer) { foreach (var m in members.Values) m.Cycles.Clear(); members.Clear(); recipients.Clear(); }
            paused = false;
            lease = Guid.Empty;
        }
    }
}
