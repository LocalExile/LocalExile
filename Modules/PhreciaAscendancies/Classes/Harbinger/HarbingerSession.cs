using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Feature-owned adapter for the shared executor. All calls, including the packet factory, must run
/// under the owner's monster lock. No task, writer, cooldown or connection lifecycle is owned here.</summary>
internal sealed class HarbingerSession(HarbingerRoster roster, Guid lease, DateTimeOffset attached)
{
    internal readonly record struct ChannelReceipt(ushort Skill, int Action, int Generation);
    internal sealed record Frame(int Epoch, bool Alive, bool Paused, bool Leaving, bool InCombat,
        IReadOnlyList<ushort> Nodes, double RecipientRate, ChannelReceipt? PaidChannel);
    internal sealed record Observation(HarbingerRoster.Member[] Members, HarbingerRoster.Update[] Changes,
        HarbingerRoster.Active[] Buffs);

    readonly Dictionary<long, uint> actors = [];
    bool retired;
    readonly bool attachedLease = Attach(roster, lease, attached);

    static bool Attach(HarbingerRoster roster, Guid lease, DateTimeOffset now)
    {
        if (lease == Guid.Empty) throw new ArgumentException("A companion session needs an owner lease", nameof(lease));
        roster.Attach(lease, now);
        return true;
    }

    internal Observation Observe(Frame frame, DateTimeOffset now, Func<ChannelReceipt, bool> channelCurrent, bool allowStart = true)
    {
        if (retired || !attachedLease || frame.Leaving) return new([], [], []);
        roster.Pause(lease, now, frame.Paused);
        if (!frame.Alive) roster.Clear(lease);
        var members = roster.Read(lease, frame.Nodes, now);
        // Complete old casts at the OLD recipient rate before applying a new source snapshot.
        roster.SetRecipientRate(lease, now, frame.RecipientRate);
        bool channel = frame.PaidChannel is { } receipt && channelCurrent(receipt);
        var changes = roster.Step(lease, now, allowStart && frame.Alive && frame.InCombat, channel,
            m => actors.ContainsKey(m.Id));
        return new(members, changes, roster.Buffs(lease, now));
    }

    /// <summary>Called only at the accepted summon completion after the shared skill executor has checked
    /// payment, cooldown, requirements and current action/epoch. Owner recovery is never a minion input.</summary>
    internal bool Activate(HarbingerCompanionCatalog.Companion source, Frame frame, DateTimeOffset now,
        Func<bool> actionCurrent, Func<string, uint?> key, double age = 0)
    {
        if (retired || !frame.Alive || frame.Paused || frame.Leaving || !frame.Nodes.Contains(source.Node)
            || !actionCurrent()) return false;
        roster.Read(lease, frame.Nodes, now);
        int recovery = key("base_cooldown_speed_+%") is uint k ? source.MinionStats.GetValueOrDefault(k) : 0;
        return roster.Activate(lease, source, recovery, now, age);
    }

    internal bool Bind(long member, uint actor)
    {
        if (retired || actor == 0 || !roster.Contains(lease, member)) return false;
        actors[member] = actor;
        return true;
    }
    internal bool Contains(long member) => !retired && roster.Contains(lease, member);

    internal void Pause(DateTimeOffset now, bool paused)
    {
        if (!retired) roster.Pause(lease, now, paused);
    }

    /// <summary>Remove these native objects through the shared minion retirement path; never save them as hostiles.</summary>
    internal uint[] PruneActors()
    {
        var removed = actors.Where(a => !roster.Contains(lease, a.Key)).ToArray();
        foreach (var entry in removed) actors.Remove(entry.Key);
        return [.. removed.Select(a => a.Value)];
    }

    /// <summary>Invoke inside the cipher writer's atGate factory with a fresh frame. A queued observation is
    /// only a token, never permission to publish. Stats precede the native remaining-BASE duration.</summary>
    internal byte[]? BuffPacket(HarbingerRoster.Active token, int epoch, Frame current, DateTimeOffset now,
        ushort instance, ushort skill, Func<uint, bool> actorCurrent, Func<byte[]> recipientStats,
        Func<(Frame Frame, DateTimeOffset Now)> refresh, Action? presented = null)
    {
        if (retired || current.Epoch != epoch || !current.Alive || current.Paused || current.Leaving
            || !current.Nodes.Contains(token.Owner.Source.Node)
            || !actors.TryGetValue(token.Owner.Id, out uint actor) || !actorCurrent(actor)) return null;
        roster.Read(lease, current.Nodes, now);
        roster.SetRecipientRate(lease, now, current.RecipientRate);
        var live = roster.Buffs(lease, now).FirstOrDefault(b => b.Owner.Id == token.Owner.Id
            && b.Buff.Skill.Effect == token.Buff.Skill.Effect && b.Buff.StartedAt == token.Buff.StartedAt);
        if (live is null || live.RemainingBase <= 0) return null;
        byte[] prefix = recipientStats();
        // Stat construction can reconcile damage, retirement or expiry. Preserve consumed zero clears even
        // when that work invalidates the add; never mark this prefix-only result as a presented buff.
        (current, now) = refresh();
        if (retired || current.Epoch != epoch || !current.Alive || current.Paused || current.Leaving
            || !current.Nodes.Contains(token.Owner.Source.Node) || !roster.Contains(lease, token.Owner.Id)
            || !actors.TryGetValue(token.Owner.Id, out uint afterActor) || afterActor != actor || !actorCurrent(actor))
            return prefix;
        roster.SetRecipientRate(lease, now, current.RecipientRate);
        live = roster.Buffs(lease, now).FirstOrDefault(b => b.Owner.Id == token.Owner.Id
            && b.Buff.Skill.Effect == token.Buff.Skill.Effect && b.Buff.StartedAt == token.Buff.StartedAt);
        if (live is null || live.RemainingBase <= 0) return prefix;
        presented?.Invoke();
        return [.. prefix, .. InstanceEntities.AddBuff(PlayerId, instance, live.Buff.Skill.Definition,
            actor, skill, u68: BitConverter.SingleToUInt32Bits((float)live.RemainingBase))];
    }

    internal IReadOnlyDictionary<uint, int> RecipientStats(Frame current, DateTimeOffset now)
    {
        var result = new Dictionary<uint, int>();
        if (retired || current.Leaving) return result;
        if (!current.Alive) roster.Clear(lease);
        roster.Read(lease, current.Nodes, now);
        roster.SetRecipientRate(lease, now, current.RecipientRate);
        foreach (var buff in roster.Buffs(lease, now))
            foreach (var (key, value) in buff.Buff.Skill.RecipientStats)
                result[key] = (int)Math.Clamp((long)result.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return result;
    }

    internal uint[] Retire(bool transfer, DateTimeOffset now)
    {
        if (retired) return [];
        retired = true;
        roster.Release(lease, transfer, now);
        uint[] removed = [.. actors.Values];
        actors.Clear();
        return removed;
    }
}
