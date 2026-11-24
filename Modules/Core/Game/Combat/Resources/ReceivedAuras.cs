using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // The zones of the auras a player shares (one per aura skill), around them, reaching the other players and the
    // friendly monsters: an ally coming in or going out presents its auras again (a minion: its owner's stats).
    const ZoneKind SharedAuraZones = ZoneKind.SharedAuras;

    static void SyncSharedAuraZones(Player player, IReadOnlyList<SharedAura> shared) =>
        SyncZones(player, player.Id, SharedAuraZones, shared.Select(a => ((uint)a.Aura.SkillId, a.Radius, default((int, int)))),
            new(ReachesAlliesBut(player, player.Id), new EntityId(player.Id), id =>
            {
                if (AreaPlayer(player, id.Value) is { } ally) PresentNearby(ally);
                else PresentStats(player);
            }));

    // Whether a player's shared aura reaches the entity.
    static bool SharedAuraReaches(Player player, uint owner, ushort skill, uint id) => InZone(player, owner, SharedAuraZones, skill, id);

    // Several copies of one aura do not stack. External snapshots retain the caster's effect,
    // never the receiving player's aura-effect modifier; Generosity still reaches nearby allies.
    public static List<AuraCatalog.Aura> AffectingAuras(Player player) => player.Auras.Values.Where(a => a.AffectsSelf)
        .Concat(player.ReceivedAuras.Values).ToList();

    static byte[][] ReconcileReceivedAuraRecordsLocked(Player player)
    {
        var desired = player.PlayerLife == 0 || InTown(player) ? [] : player.Instance.PlayersExcept(player.Id)
            .Where(p => p.PlayerLife > 0).SelectMany(p => p.SharedAuras.Where(a => SharedAuraReaches(player, p.Id, a.Aura.SkillId, player.Id))
                .Select(a => (Key: (p.Id, a.Aura.SkillId), Aura: a.Aura with { SourceBuff = null,
                    AffectsSelf = true, ExternalEffectPct = a.Effect }))).ToDictionary(p => p.Key, p => p.Aura);
        var packets = new List<byte[]>();
        var previousAvian = player.ReceivedAuras.Values.Select(a => a.EffectId).ToHashSet();
        bool changed = false;
        foreach (var (key, old) in player.ReceivedAuras.ToArray())
        {
            // Peer snapshots can recreate the stat array without changing the received effect.
            if (desired.TryGetValue(key, out var next)
                && next with { Buff = old.Buff, SkillStats = old.SkillStats } == old
                && (ReferenceEquals(next.SkillStats, old.SkillStats)
                    || next.SkillStats is { } stats && old.SkillStats is { } oldStats && stats.SequenceEqual(oldStats)))
                continue;
            player.Area.Buffs.Clear(player.Id, old.BuffRow, key.Item1);
            player.ReceivedAuras.Remove(key); changed = true;
        }
        foreach (var (key, candidate) in desired)
        {
            if (player.ReceivedAuras.ContainsKey(key)) continue;
            player.ReceivedAuras[key] = candidate; changed = true;
            // A received buff has no cancellation association with an unrelated local skill id; its source is the caster.
            player.Area.Buffs.Set(player.Id, new(candidate.BuffRow, key.Item1));
        }
        if (changed)
        {
            foreach (string gained in player.ReceivedAuras.Values.Select(a => a.EffectId).Distinct()
                         .Where(id => id is "AvianMightShared" or "AvianFlightShared" && !previousAvian.Contains(id)))
                QueueTwisterGainLocked(player);
            player.AuraStatsDirty = true;
            player.Pools = BuildPoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
            player.EnergyShield.SetMaximum(player.Pools.EnergyShield);
        }
        return [.. packets];
    }

    public static Dictionary<uint, int> MinionAuraStats(Player player, GolemState.Golem minion)
    {
        // A minion without a body stands with its owner: every aura of his reaches it.
        bool Reaches(uint owner, ushort skill) => minion.Monster is null ? owner == player.Id : SharedAuraReaches(player, owner, skill, minion.Id);
        var skills = SkillsFor(player);
        var own = player.PlayerLife == 0 || InTown(player) ? [] : player.Auras.Values
            .Where(a => SharedPlayerAura(a.EffectId) && Reaches(player.Id, a.SkillId))
            .Select(a => a with { AffectsSelf = true, ExternalEffectPct = player.Pools.AuraEffectPct + a.SupportEffectPct });
        var directAvian = SharedAvianAuras(player).Where(a => Reaches(player.Id, a.Aura.SkillId)).Select(a => a.Aura);
        var auras = own.Concat(directAvian).Concat(player.Instance.PlayersExcept(player.Id).Where(p => p.PlayerLife > 0)
                .SelectMany(p => p.SharedAuras.Where(a => Reaches(p.Id, a.Aura.SkillId))
                    .Select(a => a.Aura with { AffectsSelf = true, ExternalEffectPct = a.Effect })))
            .ToArray();
        var values = AuraCatalog.RawStats(auras, 0, skills, Stats).ToDictionary(p => p.Key, p => p.Value);
        WithTemporaryAuraStats(auras, 0, values);
        if (minion.Monster is { } monster && MinionWarcryGrants.TryGetValue(monster, out var cries))
        {
            double now = AreaTime(player);
            for (int i = cries.Count - 1; i >= 0; i--)
                if (cries[i].Until <= now || monster.Dead) cries.RemoveAt(i);
            if (cries.Count == 0) MinionWarcryGrants.Remove(monster);
            else AddWarcryStats(cries, player.Area, now, values);
        }
        if (MinionIntimidatingCryMovement(player, minion.Monster) is > 0 and var cryMovement)
            values[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate((long)values.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct)
                + cryMovement);
        foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock", "scorch", "brittle", "sap" })
            if (values.GetValueOrDefault(Stat.Of($"base_immune_to_{ailment}")) != 0) values[Stat.Of($"immune_to_{ailment}")] = 1;
        var bonus = AuraCatalog.Bonus(auras, 0, skills, Stats);
        bonus = WithTemporaryAuraBonus(auras, 0, bonus);
        void Add(uint name, decimal raw)
        {
            int value = (int)Math.Clamp(raw, int.MinValue, int.MaxValue);
            if (value != 0) values[name] = values.GetValueOrDefault(name) + value;
        }
        foreach (var aura in DivineSentinelAurasReaching(player, minion.Id))
            foreach (var (stat, value) in aura.Stats) Add(stat, value);
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
