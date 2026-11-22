using GameServer.Core.World;

public static partial class Game
{
    sealed class InfernalLegionField
    {
        public double LastAt;
        public readonly HashSet<SpawnedMonster> Burning = [];
        public readonly Dictionary<uint, (SpawnedMonster Target, double Remainder)> Carry = new();
    }

    sealed class InfernalLegionSelfBurn { public double Carry; }

    static readonly RuleTable<ClientState, InfernalLegionField> InfernalLegionFields = new();
    static readonly RuleTable<GolemState.Golem, InfernalLegionSelfBurn> InfernalLegionSelfBurns = new();
    static readonly HashSet<int> MinionFireArea = [7, 28];

    static double MinionFireDamage(GolemState.Golem minion, double basis, bool damageOverTime)
    {
        var effects = minion.Monster.Effects;
        long increased = (long)effects["damage_+%"] + effects["fire_damage_+%"]
            + effects["elemental_damage_+%"] + effects["area_damage_+%"];
        if (damageOverTime) increased += (long)effects["damage_over_time_+%"] + effects["burn_damage_+%"];
        int Own(string stat) => GolemStat(minion.Own, stat);
        if (DamageStats.Prohibited(DamageType.Fire, Own)) return 0;
        var support = minion.Own.Select(p => (Stats.Name(p.Key) ?? "", p.Value));
        var history = DamageTypeHistory.Fire;
        var (general, typed) = GemSupportDamage.Multipliers(history, support, MinionFireArea);
        double factor = Math.Max(0, 1 + increased / 100d) * general * typed
            * Math.Max(0, 1 + Own("active_skill_minion_damage_+%_final") / 100d)
            * Math.Max(0, 1 + Own("support_minion_damage_+%_final") / 100d);
        if (damageOverTime)
            factor *= Math.Max(0, 1 + (effects["dot_multiplier_+"] + effects["fire_dot_multiplier_+"]) / 100d);
        return basis * factor;
    }

    static double MinionFireRadius(GolemState.Golem minion, double basis) =>
        basis * Math.Sqrt(Math.Max(0, 1 + minion.Monster.Effects["base_skill_area_of_effect_+%"] / 100d));

    internal static int InfernalLegionSelfDamage(GolemState.Golem minion, double elapsed, double taken)
    {
        int coefficient = GolemStat(minion.Own, "minion_fire_damage_%_of_maximum_life_taken_per_minute");
        if (coefficient <= 0 || elapsed <= 0 || taken <= 0) return 0;
        var state = InfernalLegionSelfBurns.GetOrCreateValue(minion);
        double total = state.Carry + minion.Monster.MaxLife * (coefficient / 6000d) * elapsed * taken
            * Math.Max(0, 1 + GolemStat(minion.Own, "support_minion_defensive_stance_minion_damage_taken_+%_final") / 100d);
        int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
        state.Carry = total - Math.Floor(total);
        return damage;
    }

    internal static void InfernalLegionTickLocked(ClientState conn, double now)
    {
        List<(uint Owner, (int X, int Y) At, double Radius, double Dps)>? fields = null;
        void AddFields(ClientState owner)
        {
            if (owner.PlayerLife == 0 || InTown(owner)) return;
            foreach (var minion in UtilityFighters(owner))
            {
                int basis = GolemStat(minion.Own, "support_minion_instability_minion_base_fire_area_damage_per_minute");
                if (basis <= 0) continue;
                double dps = MinionFireDamage(minion, basis / 60d, damageOverTime: true);
                if (dps <= 0) continue;
                var at = minion.Monster.Walk.Position(now);
                double radius = MinionFireRadius(minion, 15 + GolemStat(minion.Own, "infernal_legion_minions_have_burning_effect_radius_+"));
                (fields ??= []).Add((owner.PeerId, at, radius, dps));
            }
        }
        AddFields(conn);
        if (fields is null)
        {
            InfernalLegionFields.Remove(conn);
            return;
        }
        if (!InfernalLegionFields.TryGetValue(conn, out var state))
        {
            InfernalLegionFields.GetOrCreateValue(conn).LastAt = now;
            return;
        }
        double elapsed = Math.Max(0, now - state.LastAt);
        state.LastAt = Math.Max(state.LastAt, now);
        if (elapsed == 0) return;
        state.Burning.Clear();
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
            if (peer.Current && peer.Owner is { } owner && owner.Instance == conn.Instance) AddFields(owner);
        fields.Sort((a, b) => a.Dps == b.Dps ? a.Owner.CompareTo(b.Owner) : b.Dps.CompareTo(a.Dps));
        foreach (var (id, target) in conn.Monsters.ToArray())
        {
            if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || target.Asleep) continue;
            var at = target.Walk.Position(now);
            var field = fields.FirstOrDefault(f => Dist(f.At, at) <= f.Radius + LivingCollision.Size(target));
            if (field.Dps <= 0 || field.Owner != conn.PeerId) continue;
            state.Burning.Add(target);
            double total = field.Dps * elapsed * MonsterDotTaken(target, DamageType.Fire, now);
            if (state.Carry.TryGetValue(id, out var carry) && ReferenceEquals(carry.Target, target)) total += carry.Remainder;
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
            state.Carry[id] = (target, total - Math.Floor(total));
            if (damage == 0) continue;
            (target.Life, bool stageStarts) = TakeOwnerDamageLocked(conn, target, damage);
            target.Aggro = target.Life > 0;
            if (target.Life == 0)
            {
                target.Dead = true;
                CollectVaalSoulAtDeathLocked(conn, target);
                OwnedKillLocked(target, 0);
                TriggerElementalRelicLocked(conn, target, hit: false, killed: true, field.At);
            }
            if (stageStarts)
            {
                var (remote, stream, ciphers) = conn.Wire;
                CommitBossStage(remote, stream, ciphers, conn, id, target, now);
            }
            CommitAcceptedHitLocked(conn, id, target, new(target.Life, false), damage, now,
                "Infernal Legion", playerKill: false, damageOverTime: true);
        }
        foreach (var (id, carry) in state.Carry.ToArray())
            if (carry.Target.Dead || conn.Monsters.GetValueOrDefault(id) != carry.Target) state.Carry.Remove(id);
    }

    internal static bool MinionInstabilityLocked(ClientState conn, GolemState.Golem minion, double now)
    {
        var monster = minion.Monster;
        if (monster.Dead || monster.Life == 0 || monster.Life * 2L > monster.MaxLife || GolemIgnoresDots(minion)) return false;
        int percent = GolemStat(minion.Player, "minions_explode_on_low_life_maximum_life_%_to_deal");
        if (GolemStat(minion.Player, "keystone_minion_instability") > 0) percent = Math.Max(percent, 33);
        if (percent <= 0) return false;
        var at = monster.Walk.Position(now);
        double radius = MinionFireRadius(minion, 22);
        double raw = MinionFireDamage(minion, monster.MaxLife * (percent / 100d), damageOverTime: false);
        var damagePacket = DamagePacket.Of(DamageType.Fire, raw, raw);
        GolemDieLocked(conn, minion, now);
        foreach (var (id, target) in conn.Monsters.ToArray())
        {
            if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || target.Asleep
                || Dist(target.Walk.Position(now), at) > radius + LivingCollision.Size(target)) continue;
            var mitigated = DamageModel.Mitigate(damagePacket, type => MonsterHitResistance(target, type, default, now));
            int damage = MonsterTakesMitigated(target, mitigated, spell: false, now);
            if (damage > 0) CommitMinionFireDamage(conn, id, target, damage, at, now, "Minion Instability");
        }
        return true;
    }

    static void CommitMinionFireDamage(ClientState conn, uint id, SpawnedMonster target, int damage,
        (int X, int Y) at, double now, string effect)
    {
        if (ApplyMonsterHitLocked(conn, target, damage, allyAt: at) is not { } hit) return;
        if (hit.StageStarts)
        {
            var (remote, stream, ciphers) = conn.Wire;
            CommitBossStage(remote, stream, ciphers, conn, id, target, now);
            hit = hit with { StageStarts = false };
        }
        CommitMinionHitLocked(conn, id, target, hit, damage, now, effect);
    }
}
