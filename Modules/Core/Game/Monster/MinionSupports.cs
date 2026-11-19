using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed record MinionDamageScaling(double HitMore, double AilmentMore, IReadOnlyDictionary<uint, int> Overrides);
    static readonly RuleTable<GolemState.Ability, MinionDamageScaling> MinionDamageScalings = new();

    static GolemState.Ability CurrentMinionHit(GolemState.Ability accepted, GolemState.Ability current)
    {
        if (!MinionDamageScalings.TryGetValue(accepted, out var scaling)) return current;
        var stats = new Dictionary<uint, int>(current.Stats);
        foreach (var pair in scaling.Overrides) stats[pair.Key] = pair.Value;
        return MinionDamageScale(current, scaling.HitMore, scaling.AilmentMore, stats);
    }

    static readonly RuleTable<GolemState.Golem, Dictionary<ushort, int>> MinionRuthlessCounts = new();
    /// <summary>Fills this type's game data at startup, after the supports (the world loader, before the Gateway listens).</summary>
    public static void LoadMinionSupportsData() => MinionSupportRules = LoadMinionSupportRules();

    static IReadOnlyDictionary<string, GemSupports.Effect> MinionSupportRules = default!;
    static IReadOnlyDictionary<string, GemSupports.Effect> LoadMinionSupportRules() =>
        Supports.Effects.Where(e => e.Id is "SupportMultistrike" or "SupportMeleeSplash" or "SupportRuthless")
            .ToDictionary(e => e.Id);

    static bool MinionSupportMatches(int effectRow, string id)
    {
        if (Supports.Of(effectRow) is not { } effect || !MinionSupportRules.TryGetValue(id, out var rule)) return false;
        var types = effect.Types.ToHashSet();
        return GemSupports.Matches(rule.Allowed, types, true) && !GemSupports.Matches(rule.Excluded, types, false);
    }

    static bool MinionMelee(Player conn, GolemState.Ability a) =>
        !a.Attack.Spell && a.Projectile is null && SkillsFor(conn).IsMelee(a.Effect);

    static GolemState.Ability MinionDamageScale(GolemState.Ability original, double hitMore, double ailmentMore,
        IReadOnlyDictionary<uint, int>? stats = null)
    {
        var scaled = original with { Attack = original.Attack with { Damage = original.Attack.Damage.Scale(hitMore) },
            Stats = stats ?? original.Stats };
        if (MinionAilmentProfiles.TryGetValue(original, out var profile))
        {
            MonsterIgnites.Settings Scale(MonsterIgnites.Settings s) => s with
            { Min = s.Min * ailmentMore, Max = s.Max * ailmentMore,
                CriticalMin = s.CriticalMin * ailmentMore, CriticalMax = s.CriticalMax * ailmentMore };
            var p = profile.Poison;
            MinionAilmentProfiles.Add(scaled, profile with { Ignite = Scale(profile.Ignite), Bleed = Scale(profile.Bleed),
                Poison = p with { Min = p.Min * ailmentMore, Max = p.Max * ailmentMore,
                    CriticalMin = p.CriticalMin * ailmentMore, CriticalMax = p.CriticalMax * ailmentMore } });
        }
        MinionDamageScalings.TryGetValue(original, out var previous);
        var overrides = previous is null ? new Dictionary<uint, int>() : new(previous.Overrides);
        if (stats is not null)
            foreach (var pair in stats)
                if (!original.Stats.TryGetValue(pair.Key, out int before) || before != pair.Value)
                    overrides[pair.Key] = pair.Value;
        MinionDamageScalings.Add(scaled, new(hitMore * (previous?.HitMore ?? 1),
            ailmentMore * (previous?.AilmentMore ?? 1), overrides));
        return scaled;
    }

    static GolemState.Ability MinionSupportAction(Player conn, GolemState.Golem fighter, GolemState.Action action)
    {
        var original = action.Original ?? action.Ability;
        if (!MinionMelee(conn, original)) return original;
        int Own(string n) => GolemStat(fighter.Own, n);
        if (action.RepeatIndex == 0)
        {
            action.Original = original;
            action.RepeatsRemaining = Own("disable_attack_repeats") != 0 || Own("disable_skill_repeats") != 0
                || !MinionSupportMatches(original.Effect, "SupportMultistrike") ? 0 : Math.Clamp(Own("base_melee_attack_repeat_count"), 0, 3);
            action.UseDamageMore = MinionSupportMatches(original.Effect, "SupportRuthless")
                ? RuthlessBlowMore(MinionRuthlessCounts.GetValue(fighter, _ => []), original.Skill, Own) : 1;
            action.UseAilmentMore = action.UseDamageMore == 1 ? 1
                : Math.Max(0, 1 + Own("support_ruthless_blow_ailment_damage_from_melee_hits_+%_final") / 100d);
        }
        double more = action.RepeatIndex == 0 ? 1 : Math.Max(0, 1 + Own(action.RepeatIndex switch
        {
            1 => "multistrike_damage_+%_final_on_first_repeat",
            2 => "multistrike_damage_+%_final_on_second_repeat",
            _ => "multistrike_damage_+%_final_on_third_repeat"
        }) / 100d);
        IReadOnlyDictionary<uint, int> stats = original.Stats;
        if (action.UseDamageMore != 1 && Stats.Key("support_ruthless_big_hit_stun_base_duration_override_ms") is uint stun)
            stats = new Dictionary<uint, int>(stats) { [stun] = Own("support_ruthless_big_hit_stun_base_duration_override_ms") };
        return MinionDamageScale(original, action.UseDamageMore * more, action.UseAilmentMore * more, stats);
    }

    static bool RepeatMinionAttackLocked(Player conn, GolemState.Golem fighter, double now)
    {
        if (fighter.Repeat is not { } repeat) return false;
        fighter.Repeat = null;
        int Own(string n) => GolemStat(fighter.Own, n);
        if (repeat.Generation != fighter.Monster.ActionGeneration || repeat.RepeatsRemaining <= 0
            || Own("base_melee_attack_repeat_count") <= 0 || Own("disable_attack_repeats") != 0 || Own("disable_skill_repeats") != 0) return false;
        var a = repeat.Original!;
        var at = fighter.Monster.Walk.Position(now);
        var target = conn.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0
                && !p.Value.Invulnerable && !p.Value.Asleep && !conn.AsleepMonsters.Contains(p.Key)
                && Dist(at, p.Value.Walk.Position(now)) <= a.Attack.Range + LivingCollision.Size(p.Value))
            .OrderBy(p => p.Key == repeat.Target ? 0 : 1).ThenBy(p => Dist(at, p.Value.Walk.Position(now))).FirstOrDefault();
        if (target.Value is null) return false;
        GolemReserveLocked(conn, fighter, a, target.Key, target.Value, target.Value.Walk.Position(now), now);
        fighter.Pending!.Original = a;
        fighter.Pending.RepeatIndex = repeat.RepeatIndex + 1;
        fighter.Pending.RepeatsRemaining = repeat.RepeatsRemaining - 1;
        fighter.Pending.UseDamageMore = repeat.UseDamageMore;
        fighter.Pending.UseAilmentMore = repeat.UseAilmentMore;
        return true;
    }
}
