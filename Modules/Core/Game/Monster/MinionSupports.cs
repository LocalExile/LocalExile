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
        return MinionDamageScale(current, scaling.HitMore, scaling.AilmentMore, stats, accepted.RepeatCriticalRoll);
    }

    static readonly RuleTable<GolemState.Golem, Dictionary<ushort, int>> MinionRuthlessCounts = new();
    /// <summary>Fills this type's game data at startup, after the supports (the world loader, before the Gateway listens).</summary>
    public static void LoadMinionSupportsData() => MinionSupportRules = LoadMinionSupportRules();

    static IReadOnlyDictionary<string, GemSupports.Effect> MinionSupportRules = default!;
    static IReadOnlyDictionary<string, GemSupports.Effect> LoadMinionSupportRules() =>
        Supports.Effects.Where(e => e.Id is "SupportMultistrike" or "SupportMeleeSplash" or "SupportRuthless" or "SupportSpellEcho")
            .ToDictionary(e => e.Id);

    static bool MinionSupportMatches(int effectRow, string id)
    {
        if (Supports.Of(effectRow) is not { } effect || !MinionSupportRules.TryGetValue(id, out var rule)) return false;
        var types = effect.Types.ToHashSet();
        return GemSupports.Matches(rule.Allowed, types, true) && !GemSupports.Matches(rule.Excluded, types, false);
    }

    static bool MinionMelee(Player player, GolemState.Ability a) =>
        !a.Attack.Spell && a.Projectile is null && SkillsFor(player).IsMelee(a.Effect);

    static int MinionSpellEchoRepeats(Player player, int effect, Func<uint, int> own) =>
        MinionSupportMatches(effect, "SupportSpellEcho") ? SpellRepeats(player, effect, own) : 0;

    static GolemState.Ability MinionDamageScale(GolemState.Ability original, double hitMore, double ailmentMore,
        IReadOnlyDictionary<uint, int>? stats = null, int? criticalRoll = null)
    {
        var scaled = original with { Attack = original.Attack with { Damage = original.Attack.Damage.Scale(hitMore) },
            Stats = stats ?? original.Stats, RepeatCriticalRoll = criticalRoll ?? original.RepeatCriticalRoll };
        if (MinionHitProfiles.TryGetValue(original, out var profile))
        {
            MonsterIgnites.Settings Scale(MonsterIgnites.Settings s) => s with
            { Min = s.Min * ailmentMore, Max = s.Max * ailmentMore,
                CriticalMin = s.CriticalMin * ailmentMore, CriticalMax = s.CriticalMax * ailmentMore };
            var p = profile.Poison;
            MinionHitProfiles.Add(scaled, profile with { Ignite = Scale(profile.Ignite), Bleed = Scale(profile.Bleed),
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

    static GolemState.Ability MinionSupportAction(Player player, GolemState.Golem fighter, GolemState.Action action)
    {
        var original = action.Original ?? action.Ability;
        int Own(uint n) => GolemStat(fighter.Own, n);
        if (original.Attack.Spell)
        {
            if (action.RepeatIndex == 0 && MinionSpellEchoRepeats(player, original.Effect, Own) is > 0 and int repeats)
            {
                action.Original = original;
                action.RepeatsRemaining = repeats;
                action.RepeatCriticalRoll = AreaRng(player).Next(10_000);
            }
            return action.RepeatCriticalRoll is int roll ? MinionDamageScale(original, 1, 1, criticalRoll: roll) : original;
        }
        if (!MinionMelee(player, original)) return original;
        if (action.RepeatIndex == 0)
        {
            action.Original = original;
            action.RepeatsRemaining = Own(Stat.DisableAttackRepeats) != 0 || Own(Stat.DisableSkillRepeats) != 0
                || !MinionSupportMatches(original.Effect, "SupportMultistrike") ? 0 : Math.Clamp(Own(Stat.BaseMeleeAttackRepeatCount), 0, 3);
            action.UseDamageMore = MinionSupportMatches(original.Effect, "SupportRuthless")
                ? RuthlessBlowMore(MinionRuthlessCounts.GetValue(fighter, _ => []), original.Skill, Own) : 1;
            action.UseAilmentMore = action.UseDamageMore == 1 ? 1
                : Math.Max(0, 1 + Own(Stat.SupportRuthlessBlowAilmentDamageFromMeleeHitsPlusPctFinal) / 100d);
        }
        double more = action.RepeatIndex == 0 ? 1 : Math.Max(0, 1 + Own(action.RepeatIndex switch
        {
            1 => Stat.MultistrikeDamagePlusPctFinalOnFirstRepeat,
            2 => Stat.MultistrikeDamagePlusPctFinalOnSecondRepeat,
            _ => Stat.MultistrikeDamagePlusPctFinalOnThirdRepeat
        }) / 100d);
        IReadOnlyDictionary<uint, int> stats = original.Stats;
        if (action.UseDamageMore != 1 && Stat.SupportRuthlessBigHitStunBaseDurationOverrideMs is var stun)
            stats = new Dictionary<uint, int>(stats) { [stun] = Own(Stat.SupportRuthlessBigHitStunBaseDurationOverrideMs) };
        return MinionDamageScale(original, action.UseDamageMore * more, action.UseAilmentMore * more, stats);
    }

    static bool RepeatMinionActionLocked(Player player, GolemState.Golem fighter, double now)
    {
        if (fighter.Repeat is not { } repeat) return false;
        fighter.Repeat = null;
        int Own(uint n) => GolemStat(fighter.Own, n);
        if (repeat.Generation != fighter.Monster.ActionGeneration || repeat.RepeatsRemaining <= 0 || repeat.Original is not { } a
            || Own(Stat.DisableSkillRepeats) != 0) return false;
        if (a.Attack.Spell ? MinionSpellEchoRepeats(player, a.Effect, Own) <= 0
            : Own(Stat.BaseMeleeAttackRepeatCount) <= 0 || Own(Stat.DisableAttackRepeats) != 0) return false;
        var at = fighter.Monster.Walk.Position(now);
        var target = player.Area.MonstersNear(at, a.Attack.Range).Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0
                && !p.Value.Invulnerable && !p.Value.Asleep && !MonsterAsleep(player, p.Key)
                && Dist(at, p.Value.Walk.Position(now)) <= a.Attack.Range + LivingCollision.Size(p.Value))
            .OrderBy(p => p.Key == repeat.Target ? 0 : 1).ThenBy(p => Dist(at, p.Value.Walk.Position(now))).FirstOrDefault();
        if (target.Value is null && !a.Attack.Spell) return false;
        GolemReserveLocked(player, fighter, a, target.Value is null ? repeat.Target : target.Key,
            target.Value ?? repeat.Victim, target.Value?.Walk.Position(now) ?? repeat.Aim, now);
        fighter.Pending!.Original = a;
        fighter.Pending.RepeatIndex = repeat.RepeatIndex + 1;
        fighter.Pending.RepeatsRemaining = repeat.RepeatsRemaining - 1;
        fighter.Pending.RepeatCriticalRoll = repeat.RepeatCriticalRoll;
        fighter.Pending.UseDamageMore = repeat.UseDamageMore;
        fighter.Pending.UseAilmentMore = repeat.UseAilmentMore;
        return true;
    }
}
