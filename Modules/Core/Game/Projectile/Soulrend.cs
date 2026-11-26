using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const ushort SoulrendBuffRow = 1266;
    const string SoulrendPulseStage = "soulrend-debuff";
    const int SoulrendRadius = 10; // Legion balance notes, shared by the normal and spiral projectile.
    static readonly RuleTable<GameArea, Dictionary<uint, AreaChaosDot>> SoulrendDots = new();

    static bool HasSoulrendDamage(GameArea area) => SoulrendDots.TryGetValue(area, out var dots) && dots.Count > 0;

    static ProjectileUse SoulrendRelease(ProjectileReleaseContext context, ProjectileUse use, ProjectileOffense shot,
        Func<uint, int> areaStat)
    {
        var player = context.Player;
        var get = context.Get;
        var own = context.Own;
        var placed = PlacedKind(SupportedSkill(player, context.SkillId));
        var owner = CharacterPlayerStats(player);
        var caster = shot.ActualCaster ?? (placed is (true, _) or (_, true) ? TotemCasterStats(owner) : owner);
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ChaosDamagePlusPct)
            + get(Stat.DamageOverTimePlusPct) + get(Stat.ChaosDamageOverTimePlusPct)
            + get(Stat.DamageOverTimePlusPctWithSpells) + get(Stat.SoulrendDamagePlusPct);
        if (own(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += get(Stat.SpellDamagePlusPct);
        if (placed.Trap || placed.Mine)
            increased += (double)get(placed.Mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct) + get(Stat.TrapOrMineDamagePlusPct);
        double more = shot.HitProfileMore;
        foreach (uint stat in EssenceDrainSupportMore) more *= Math.Max(0, 1 + own(stat) / 100d);
        if (placed.Trap || placed.Mine)
            more *= Math.Max(0, 1 + own(Stat.SupportTrapAndMineDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + own(placed.Mine ? Stat.SupportRemoteMine2DamagePlusPctFinal : Stat.SupportTrapDamagePlusPctFinal) / 100d);
        double multiplier = 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ChaosDotMultiplierPlus)
            + get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100d;
        double dps = own(Stat.BaseChaosDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d)
            * more * Math.Max(0, multiplier);
        double seconds = SkillDurationModifiers.Seconds(own(Stat.BaseSkillEffectDuration), context.PlayerStat, own);
        if (DamageStats.Prohibited(DamageType.Chaos, get)) return use;
        var damage = new SkillDamageContext(SkillHitKind.Spell, caster).OverTime(new(DamageType.Chaos, dps, seconds));
        int radius = SkillArea.Radius(SoulrendRadius, areaStat, spell: true);
        if (damage.DamagePerSecond <= 0 || seconds <= 0 || radius <= 0) return use;

        const long interval = ProjectileRuntime.TickQuanta;
        return use with
        {
            Profile = use.Profile with
            {
                Pulse = new(SoulrendPulseStage, radius, interval)
                {
                    HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Interval, interval),
                },
            },
            Payload = shot with { Soulrend = new(damage.DamagePerSecond, seconds, radius, Caster: player) },
        };
    }

    static ProjectileHitResult ApplySoulrendDamage(Player player, uint id, SpawnedMonster target,
        MonsterContagion.Cast cast, double now)
    {
        if (!target.Hostile || target.OwnerId is not null || target.Invulnerable || target.Effects[Stat.BaseCannotBeDamaged] != 0
            || cast.DamagePerSecond <= 0) return ProjectileHitResult.Miss;
        var dots = SoulrendDots.GetOrCreateValue(player.Area);
        if (!dots.TryGetValue(id, out var entry) || !ReferenceEquals(entry.Monster, target))
            dots[id] = entry = new(target, new());
        var dot = entry.Damage;
        var previous = dot.ApplicationAt(now)?.Cast;
        dot.Apply(now, cast, trackContagion: false, target.RecipientDebuffRate);
        if (dot.Buff is null || !ReferenceEquals(previous, dot.ApplicationAt(now)?.Cast))
            PresentSoulrendDamage(player, id, dot, now);
        return ProjectileHitResult.Hit;
    }

    static void PresentSoulrendDamage(Player player, uint id, MonsterContagion dot, double now)
    {
        if (dot.ApplicationAt(now) is not { } active)
        {
            EndDot(player, id, ref dot.Buff, dot.BuffSource);
            return;
        }
        // The flight refreshes the server timer every pulse and the area removes the visual at final expiry.
        uint perMinute = (uint)Math.Clamp(Math.Round(active.Cast.DamagePerSecond * 60), 0, int.MaxValue);
        ShowDot(player, id, ref dot.Buff, ref dot.BuffSource,
            new(SoulrendBuffRow, active.Cast.Caster?.Id ?? 0, Values: [perMinute]));
    }

    static void RetimeSoulrendDamage(GameArea area, uint id, SpawnedMonster monster, double now, double oldRate, double rate)
    {
        if (SoulrendDots.TryGetValue(area, out var dots) && dots.TryGetValue(id, out var state) && state.Monster == monster)
            state.Damage.RetimeExpiry(now, oldRate, rate);
    }
}
