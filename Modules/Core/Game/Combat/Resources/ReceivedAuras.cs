using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // The zones of the auras a player shares (one per aura skill), around them, reaching the other players and the
    // friendly monsters: an ally coming in or going out presents its auras again (a minion: its owner's stats).
    const string SharedAuraZones = "shared auras";

    static void SyncSharedAuraZones(Player conn, IReadOnlyList<SharedAura> shared) =>
        SyncZones(conn, conn.PeerId, SharedAuraZones, shared.Select(a => ((object)a.Aura.SkillId, a.Radius, default((int, int)))),
            new(ReachesAlliesBut(conn, conn.PeerId), new EntityId(conn.PeerId), id =>
            {
                if (AreaPlayer(conn, id.Value) is { } ally) PresentNearby(ally);
                else PresentStats(conn);
            }));

    // Whether a player's shared aura reaches the entity.
    static bool SharedAuraReaches(Player conn, uint owner, ushort skill, uint id) => InZone(conn, owner, SharedAuraZones, skill, id);

    // Several copies of one aura do not stack. External snapshots retain the caster's effect,
    // never the receiving player's aura-effect modifier; Generosity still reaches nearby allies.
    public static List<AuraCatalog.Aura> AffectingAuras(Player conn) => conn.Auras.Values.Where(a => a.AffectsSelf)
        .Concat(conn.ReceivedAuras.Values).ToList();

    static byte[][] ReconcileReceivedAuraRecordsLocked(Player conn)
    {
        var desired = conn.PlayerLife == 0 || InTown(conn) ? [] : conn.Instance.PlayersExcept(conn.PeerId)
            .Where(p => p.Current && p.Life > 0).SelectMany(p => p.Auras.Where(a => SharedAuraReaches(conn, p.Id, a.Aura.SkillId, conn.PeerId))
                .Select(a => (Key: (p.Id, a.Aura.SkillId), Aura: a.Aura with { SourceBuff = null,
                    AffectsSelf = true, ExternalEffectPct = a.Effect }))).ToDictionary(p => p.Key, p => p.Aura);
        var packets = new List<byte[]>();
        var previousAvian = conn.ReceivedAuras.Values.Select(a => a.EffectId).ToHashSet();
        bool changed = false;
        foreach (var (key, old) in conn.ReceivedAuras.ToArray())
        {
            // Peer snapshots can recreate the stat array without changing the received effect.
            if (desired.TryGetValue(key, out var next)
                && next with { Buff = old.Buff, SkillStats = old.SkillStats } == old
                && (ReferenceEquals(next.SkillStats, old.SkillStats)
                    || next.SkillStats is { } stats && old.SkillStats is { } oldStats && stats.SequenceEqual(oldStats)))
                continue;
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, old.Buff));
            conn.ReceivedAuras.Remove(key); changed = true;
        }
        foreach (var (key, candidate) in desired)
        {
            if (conn.ReceivedAuras.ContainsKey(key)) continue;
            var aura = candidate with { Buff = conn.NextBuff++ };
            conn.ReceivedAuras[key] = aura; changed = true;
            // A received buff has no cancellation association with an unrelated local skill id.
            packets.Add(InstanceEntities.AddBuff(PlayerId, aura.Buff, aura.BuffRow, key.Item1));
        }
        if (changed)
        {
            foreach (string gained in conn.ReceivedAuras.Values.Select(a => a.EffectId).Distinct()
                         .Where(id => id is "AvianMightShared" or "AvianFlightShared" && !previousAvian.Contains(id)))
                QueueTwisterGainLocked(conn);
            conn.AuraStatsDirty = true;
            conn.Pools = BuildPoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield);
        }
        return [.. packets];
    }

    public static Dictionary<uint, int> MinionAuraStats(Player conn, GolemState.Golem minion)
    {
        // A minion without a body stands with its owner: every aura of his reaches it.
        bool Reaches(uint owner, ushort skill) => minion.Monster is null ? owner == conn.PeerId : SharedAuraReaches(conn, owner, skill, minion.Id);
        var skills = SkillsFor(conn);
        var own = conn.PlayerLife == 0 || InTown(conn) ? [] : conn.Auras.Values
            .Where(a => AuraCatalog.Shared(a.EffectId) && Reaches(conn.PeerId, a.SkillId))
            .Select(a => a with { AffectsSelf = true, ExternalEffectPct = conn.Pools.AuraEffectPct + a.SupportEffectPct });
        var directAvian = SharedAvianAuras(conn).Where(a => Reaches(conn.PeerId, a.Aura.SkillId)).Select(a => a.Aura);
        var auras = own.Concat(directAvian).Concat(conn.Instance.PlayersExcept(conn.PeerId).Where(p => p.Current && p.Life > 0)
                .SelectMany(p => p.Auras.Where(a => Reaches(p.Id, a.Aura.SkillId))
                    .Select(a => a.Aura with { AffectsSelf = true, ExternalEffectPct = a.Effect })))
            .ToArray();
        var values = AuraCatalog.RawStats(auras, 0, skills, Stats).ToDictionary(p => p.Key, p => p.Value);
        if (MinionIntimidatingCryMovement(conn, minion.Monster) is > 0 and var cryMovement)
            values[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate((long)values.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct)
                + cryMovement);
        foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock", "scorch", "brittle", "sap" })
            if (values.GetValueOrDefault(Stat.Of($"base_immune_to_{ailment}")) != 0) values[Stat.Of($"immune_to_{ailment}")] = 1;
        var bonus = AuraCatalog.Bonus(auras, 0, skills, Stats);
        void Add(uint name, decimal raw)
        {
            int value = (int)Math.Clamp(raw, int.MinValue, int.MaxValue);
            if (value != 0) values[name] = values.GetValueOrDefault(name) + value;
        }
        Add(Stat.BaseFireDamageResistancePct, bonus.FireResistance);
        Add(Stat.BaseColdDamageResistancePct, bonus.ColdResistance);
        Add(Stat.BaseLightningDamageResistancePct, bonus.LightningResistance);
        Add(Stat.BaseMaximumFireDamageResistancePct, bonus.FireMaxResistance);
        Add(Stat.BaseMaximumColdDamageResistancePct, bonus.ColdMaxResistance);
        Add(Stat.BaseMaximumLightningDamageResistancePct, bonus.LightningMaxResistance);
        Add(Stat.AccuracyRating, bonus.Accuracy); Add(Stat.AttackSpeedPlusPct, bonus.AttackSpeedPct);
        Add(Stat.BaseMovementVelocityPlusPct, bonus.MovementVelocityPct);
        Add(Stat.BasePhysicalDamageReductionRating, bonus.Armour);
        Add(Stat.DeterminationAuraArmourPlusPctFinal, bonus.ArmourMorePct);
        Add(Stat.BaseEvasionRating, bonus.Evasion); Add(Stat.GraceAuraEvasionRatingPlusPctFinal, bonus.EvasionMorePct);
        return values;
    }
}
