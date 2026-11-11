using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Feature state only. The common executor supplies current, range-filtered living hostiles at its
/// accepted send gate, then reconciles Ignite/taunt clocks and monster walk/swing timing before publishing changes.</summary>
internal sealed class HarbingerCurses
{
    internal sealed record Target(uint Id, object Identity, bool Rare, bool Immune, bool Hexproof, bool Reflects)
    { internal double Rate { get; init; } = 1; }
    internal sealed record Change(Target Target, HarbingerTemporalChains? Previous, HarbingerTemporalChains? Current);
    readonly Dictionary<uint, (Target Target, HarbingerTemporalChains Curse)> monsters = [];
    internal HarbingerTemporalChains? Self { get; private set; }

    internal static bool Allowed(Target target, Func<string, int> player) => !target.Immune
        && (!target.Hexproof || player("ignore_hexproof") != 0 || player("temporal_chains_ignores_hexproof") != 0);

    static HarbingerTemporalChains New(ushort instance, ushort skill, double now,
        HarbingerTemporalChains.Source source, bool aura, double rate = 1) =>
        new(instance, skill, new(aura ? double.PositiveInfinity : source.Seconds, now, rate),
            source.NormalSlow, source.RareSlow, source.OtherEffects, aura) { Snapshot = source };

    internal Change[] Apply(IEnumerable<Target> targets, ushort skill, double now,
        HarbingerTemporalChains.Source source, Func<string, int> player, Func<ushort> allocate,
        HarbingerTemporalChains.Source reflectedSource, bool reflectOwn = false)
    {
        var changes = new List<Change>();
        bool reflect = false;
        foreach (var target in targets)
        {
            if (!Allowed(target, player)) continue;
            monsters.Remove(target.Id, out var old);
            var curse = New(allocate(), skill, now, source, aura: false, target.Rate);
            monsters[target.Id] = (target, curse);
            changes.Add(new(target, ReferenceEquals(old.Target?.Identity, target.Identity) ? old.Curse : null, curse));
            reflect |= target.Reflects || reflectOwn;
        }
        if (reflect)
            Self = New(allocate(), skill, now, reflectedSource, aura: false,
                HarbingerRecipientClock.RecipientRate(5, player, temporalChains: true));
        return [.. changes];
    }

    internal Change[] Reconcile(IEnumerable<Target> living, IEnumerable<Target> insideAura, ushort? auraSkill,
        HarbingerTemporalChains.Source? source, double now, Func<string, int> player, Func<ushort> allocate)
    {
        var current = living.ToDictionary(t => t.Id);
        var inside = insideAura.Where(t => Allowed(t, player)).ToDictionary(t => t.Id);
        var changes = new List<Change>();
        foreach (var (id, previous) in monsters.ToArray())
        {
            previous.Curse.Clock.Advance(now);
            bool same = current.TryGetValue(id, out var target) && ReferenceEquals(target.Identity, previous.Target.Identity);
            bool keep = same && (previous.Curse.Aura
                ? auraSkill == previous.Curse.Skill && source == previous.Curse.Snapshot && inside.ContainsKey(id)
                : previous.Curse.Clock.Remaining > 0);
            if (keep) continue;
            monsters.Remove(id);
            // An id reused for a different actor must never receive the old actor's removal/stats packet.
            if (same) changes.Add(new(target!, previous.Curse, null));
        }
        if (auraSkill is ushort skill && source is { } snapshot)
            foreach (var target in inside.Values)
            {
                if (!current.TryGetValue(target.Id, out var live) || !ReferenceEquals(live.Identity, target.Identity)
                    || monsters.ContainsKey(target.Id)) continue;
                var curse = New(allocate(), skill, now, snapshot, aura: true);
                monsters[target.Id] = (target, curse);
                changes.Add(new(target, null, curse));
            }
        return [.. changes];
    }

    internal HarbingerTemporalChains? Active(Target target, double now) =>
        monsters.TryGetValue(target.Id, out var entry) && ReferenceEquals(target.Identity, entry.Target.Identity)
            && entry.Curse.Clock.Remaining > 0 && entry.Curse.Clock.Deadline > now ? entry.Curse : null;

    internal (int Slow, int OtherEffects) SelfStats(double now, bool alive, Func<string, int> player)
    {
        if (Self is not { } curse) return default;
        curse.Clock.ChangeRate(now, HarbingerRecipientClock.RecipientRate(5, player, temporalChains: true));
        if (curse.Clock.Remaining <= 0) { Self = null; return default; }
        // Suppression does not delete the running curse; refund can expose its remaining budget again.
        return alive && curse.Clock.Remaining > 0 && player("unaffected_by_curses") == 0
            && player("unaffected_by_temporal_chains") == 0 && player("virtual_unaffected_by_temporal_chains") == 0
            && player("immune_to_curses") == 0
            ? (curse.Slow(false), curse.OtherEffects) : default;
    }

    internal static byte[] Packet(uint target, HarbingerTemporalChains curse, double now)
    {
        curse.Clock.Advance(now);
        return InstanceEntities.AddBuff(target, curse.Instance, 69, PlayerId, curse.Skill,
            u68: curse.Aura ? null : BitConverter.SingleToUInt32Bits((float)curse.Clock.Remaining));
    }

    internal void Clear() { monsters.Clear(); Self = null; }
    internal Change[] Retire()
    {
        var changes = monsters.Values.Select(e => new Change(e.Target, e.Curse, null)).ToArray();
        Clear();
        return changes;
    }
}
