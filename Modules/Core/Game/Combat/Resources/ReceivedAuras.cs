using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // Several copies of one aura do not stack. External snapshots retain the caster's effect,
    // never the receiving player's aura-effect modifier; Generosity still reaches nearby allies.
    public static List<AuraCatalog.Aura> AffectingAuras(ClientState conn) => conn.Auras.Values.Where(a => a.AffectsSelf)
        .Concat(conn.ReceivedAuras.Values).ToList();

    static byte[][] ReconcileReceivedAuraRecordsLocked(ClientState conn)
    {
        var here = conn.Walk.Position(AreaTime(conn));
        var desired = conn.PlayerLife == 0 || InTown(conn) ? [] : conn.Instance.PlayersExcept(conn.PeerId)
            .Where(p => p.Current && p.Life > 0).SelectMany(p => p.Auras.Where(a => Dist(here, (p.X, p.Y)) <= a.Radius)
                .Select(a => (Key: (p.Id, a.Aura.SkillId), Aura: a.Aura with { SourceBuff = null,
                    AffectsSelf = true, ExternalEffectPct = a.Effect }))).ToDictionary(p => p.Key, p => p.Aura);
        var packets = new List<byte[]>();
        var previousAvian = conn.ReceivedAuras.Values.Select(a => a.EffectId).ToHashSet();
        bool changed = false;
        foreach (var (key, old) in conn.ReceivedAuras.ToArray())
        {
            if (desired.TryGetValue(key, out var next) && next with { Buff = old.Buff } == old) continue;
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

    public static Dictionary<string, int> MinionAuraStats(ClientState conn, GolemState.Golem minion)
    {
        var here = minion.Monster?.Walk.Position(AreaTime(conn)) ?? conn.Walk.Position(AreaTime(conn));
        var owner = conn.Walk.Position(AreaTime(conn));
        var skills = SkillsFor(conn);
        var own = conn.PlayerLife == 0 || InTown(conn) ? [] : conn.Auras.Values
            .Where(a => AuraCatalog.Shared(a.EffectId) && Dist(here, owner) <= AuraRadius.Of(a, conn.Pools.AuraAreaPct, skills, Stats))
            .Select(a => a with { AffectsSelf = true, ExternalEffectPct = conn.Pools.AuraEffectPct + a.SupportEffectPct });
        var directAvian = SharedAvianAuras(conn).Where(a => Dist(here, owner) <= a.Radius).Select(a => a.Aura);
        var auras = own.Concat(directAvian).Concat(conn.Instance.PlayersExcept(conn.PeerId).Where(p => p.Current && p.Life > 0)
                .SelectMany(p => p.Auras.Where(a => Dist(here, (p.X, p.Y)) <= a.Radius)
                    .Select(a => a.Aura with { AffectsSelf = true, ExternalEffectPct = a.Effect })))
            .ToArray();
        var values = AuraCatalog.RawStats(auras, 0, skills, Stats).ToDictionary(p => Stats.Name(p.Key)!, p => p.Value);
        foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock", "scorch", "brittle", "sap" })
            if (values.GetValueOrDefault($"base_immune_to_{ailment}") != 0) values[$"immune_to_{ailment}"] = 1;
        var bonus = AuraCatalog.Bonus(auras, 0, skills, Stats);
        void Add(string name, decimal raw)
        {
            int value = (int)Math.Clamp(raw, int.MinValue, int.MaxValue);
            if (value != 0) values[name] = values.GetValueOrDefault(name) + value;
        }
        Add("base_fire_damage_resistance_%", bonus.FireResistance);
        Add("base_cold_damage_resistance_%", bonus.ColdResistance);
        Add("base_lightning_damage_resistance_%", bonus.LightningResistance);
        Add("base_maximum_fire_damage_resistance_%", bonus.FireMaxResistance);
        Add("base_maximum_cold_damage_resistance_%", bonus.ColdMaxResistance);
        Add("base_maximum_lightning_damage_resistance_%", bonus.LightningMaxResistance);
        Add("accuracy_rating", bonus.Accuracy); Add("attack_speed_+%", bonus.AttackSpeedPct);
        Add("base_movement_velocity_+%", bonus.MovementVelocityPct);
        Add("base_physical_damage_reduction_rating", bonus.Armour);
        Add("determination_aura_armour_+%_final", bonus.ArmourMorePct);
        Add("base_evasion_rating", bonus.Evasion); Add("grace_aura_evasion_rating_+%_final", bonus.EvasionMorePct);
        return values;
    }
}
