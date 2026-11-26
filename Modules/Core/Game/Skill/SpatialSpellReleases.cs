using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool SpatialSpellEffect(string? effect) =>
        IsPurifyingFlame(effect) || IsWaveOfConviction(effect) || IsGroundBladeBlast(effect) || IsBladeBlastOfUnloading(effect)
            || IsCracklingLance(effect) || effect is "GlacialCascade" or "GlacialCascadeAltX"
            or "LightningConduitAltX" or "Manabond" or "VortexAltX" or "FlameSurgeAltX" or "ShockNovaAltX" or "ArcAltX";

    static int ShockNovaProcessionRepeats(Player player, int effect, int level, int quality) =>
        SkillsFor(player).EffectId(effect) == "ShockNovaAltX"
            ? Math.Clamp(SkillsFor(player).EffectStats(effect, level, quality)
                .FirstOrDefault(s => s.Item1 == Stat.BaseSpellRepeatCount).Item2, 0, 8)
            : 0;

    // Knockback reads the direction from an origin to the target. Put that origin on the burst's axis at the target.
    static (int X, int Y) CascadeKnockbackOrigin((int X, int Y) target, (double X, double Y) direction) =>
        (target.X - (int)Math.Round(100 * direction.X), target.Y - (int)Math.Round(100 * direction.Y));

    static DamagePacket BladeBlastDaggerDamage(Player player, int gain)
    {
        static DamagePacket? DaggerDamage(SkillCatalog.Unarmed? weapon) =>
            weapon is not null && (weapon.ItemClass == "Rune Dagger"
                || WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, "Dagger")) ? weapon.LocalDamage : null;
        var main = DaggerDamage(WeaponIn(player, ActiveMainHand(player)));
        var off = DaggerDamage(WeaponIn(player, ActiveOffHand(player)));
        if (main is null && off is null) return DamagePacket.Empty;
        long multiplier = (long)gain * (main is not null && off is not null ? 60 : 100);
        var sum = (main ?? DamagePacket.Empty).Add(off ?? DamagePacket.Empty);
        var result = DamagePacket.Empty;
        foreach (var (type, range) in sum.Components)
            result = result.Add(type, new(Math.Floor(range.Min * multiplier / 10_000d), Math.Floor(range.Max * multiplier / 10_000d)));
        return result;
    }

    public sealed partial class ProjectileSession
    {
        // Spike spacing and travel time approximate the cascade in the absence of a captured collision trace.
        const int CascadeBaseSpacing = 12;
        const double CascadeInterval = .045;

        internal void ScheduleSpatialSpell((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) from, (int X, int Y) aim, double windup, Func<bool> current, SacrificeUse sacrifice,
            SkillCriticalRoll? critical, ushort sequence = 0, int repeat = 0, double damageMore = 1,
            SpawnedMonster? brandedTarget = null)
        {
            if (!Active || !current()) return;
            var source = SupportedSkill(Player, gem.Id);
            var intensity = CastingIntensity(Player, gem.Id);
            long released = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player))) + ProjectileRuntime.Time(windup);
            Runtime.Schedule(released, () =>
            {
                if (!current() || !CurrentSource()) return;
                int Own(uint key) => source.Stats.GetValueOrDefault(key);
                var caster = CharacterPlayerStats(Player);
                var effect = SkillsFor(Player).EffectId(gem.Effect);
                var direction = GroundSlamWave.Direction(from, aim);
                if (direction == default) direction = (1, 0);
                int Radius(int baseRadius) => SkillRadius(Player, baseRadius, source.Stats, true, gem.Effect, caster);
                void Hit((int X, int Y) centre, int radius, double more = 1,
                    Func<uint, (int X, int Y), bool>? targetFilter = null, bool centred = false) =>
                    Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, centred ? centre : from, centre, immediate: true, sequence: sequence,
                        repeat: repeat, damageMore: damageMore * more, radiusOverride: radius, criticalRoll: critical,
                        sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget, targetFilter: targetFilter));

                if (IsPurifyingFlame(effect))
                {
                    SchedulePurifyingFlame(Player, this, gem.Effect, source.Stats, caster, from, aim, CurrentSource,
                        (radius, filter, waveMore, onHit) => Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, from, aim,
                            immediate: true, sequence: sequence, repeat: repeat, damageMore: damageMore, radiusOverride: radius,
                            criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget,
                            targetFilter: filter, hitAndAilmentMore: waveMore, onDirectHit: onHit)), repeat);
                }
                else if (IsWaveOfConviction(effect))
                {
                    var origin = SkillsFor(Player).BehaviourVariation(gem.Effect) == 1
                        && caster.GetValueOrDefault(Stat.NovaSpellsCastAtTargetLocation) != 0 ? aim : from;
                    ReleaseConvictionWave(Player.Id, gem.Effect, source.Stats, caster, origin, aim, CurrentSource,
                        (reach, filter) => Hit(origin, reach, targetFilter: filter, centred: true), repeat);
                }
                else if (IsGroundBladeBlast(effect))
                    ReleaseBladeBlast(gem.Effect, source.Stats, caster, aim, CurrentSource,
                        (centre, radius, detonation) => Hit(centre, radius, centred: detonation), repeat);
                else if (IsBladeBlastOfUnloading(effect))
                    ReleaseBladeBlastOfUnloading(Player.Id, gem.Effect, source.Stats, caster,
                        () => SpellCentre(Player, gem.Effect,
                            brandedTarget is null ? Player.Walk.Position(ProjectileWorldTime(Player)) : from, aim),
                        CurrentSource, (centre, radius, scale) => Enqueue(() => AreaSkillHitForUse(
                            null, Player, gem.Id, gem, centre, centre, immediate: true, sequence: sequence,
                            repeat: repeat, damageMore: damageMore, radiusOverride: radius, criticalRoll: critical,
                            sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget,
                            hitAndAilmentMore: scale)), repeat);
                else if (effect == "LightningConduitAltX")
                {
                    int radius = Radius(Own(Stat.ActiveSkillBaseAreaOfEffectRadius));
                    double now = ProjectileWorldTime(Player);
                    var selected = Player.Area.MonstersNear(aim, radius)
                        .Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Invulnerable: false,
                            Asleep: false, WorldHidden: false } && !MonsterAsleep(Player, p.Key))
                        .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), aim)))
                        .Where(p => p.Distance <= radius).OrderBy(p => p.Distance).ThenBy(p => p.Id)
                        .Take(Math.Max(0, Own(Stat.LightningConduitMaxNumTargets))).Select(p => p.Id).ToHashSet();
                    if (selected.Count > 0) Hit(aim, radius, targetFilter: (id, _) => selected.Contains(id));
                }
                else if (effect == "Manabond")
                {
                    int maximum = UnreservedMana(Player);
                    int quarters = maximum > 0 ? (int)Math.Clamp((long)Player.PlayerMana * 4 / maximum, 0, 4) : 0;
                    int radius = SkillRadius(Player, source.Stats, spell: true, gem.Effect);
                    int reach = radius + 6 * quarters;
                    int halfWidth = Radius(6);
                    Hit(aim, (int)Math.Ceiling(Math.Sqrt((double)reach * reach + (double)halfWidth * halfWidth)),
                        targetFilter: (_, at) =>
                        {
                            int x = Math.Abs(at.X - aim.X), y = Math.Abs(at.Y - aim.Y);
                            return ((double)x * x + (double)y * y) <= (double)radius * radius
                                || Math.Max(x, y) <= reach && Math.Min(x, y) <= halfWidth;
                        });
                }
                else if (effect == "VortexAltX")
                    ReleaseVortexProjection(gem, from, aim, source.Stats, caster, critical, sacrifice, CurrentSource,
                        sequence, repeat, damageMore, brandedTarget);
                else if (effect == "ShockNovaAltX")
                {
                    int rings = ShockNovaProcessionRepeats(Player, gem.Effect, gem.Level, gem.Quality) + 1;
                    int radius = Radius(26), ringOuter = Radius(20), ringInner = Radius(13);
                    // The authored repeat count is independent of Spell Echo. The pulse spacing is a local estimate.
                    for (int pulse = 0; pulse < rings; pulse++)
                    {
                        double offset = pulse * ringOuter;
                        var centre = (from.X + (int)Math.Round(offset * direction.X), from.Y + (int)Math.Round(offset * direction.Y));
                        long due = Runtime.Now + ProjectileRuntime.Time(pulse * .16);
                        Runtime.Schedule(due, () =>
                        {
                            if (CurrentSource()) Hit(centre, ringOuter, targetFilter: (_, at) => Dist(at, centre) >= ringInner, centred: true);
                        });
                        Runtime.Schedule(due + ProjectileRuntime.Time(.045), () =>
                        {
                            if (CurrentSource()) Hit(centre, radius, centred: true);
                        });
                    }
                }
                else if (effect == "ArcAltX")
                {
                    int distance = Own(Stat.ArcChainDistance);
                    int splits = (int)Math.Clamp((long)Own(Stat.ProjectileNumberToSplit) + caster.GetValueOrDefault(Stat.ProjectileNumberToSplit), 0, 256);
                    if (distance > 0)
                        Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(Math.Max(0, Own(Stat.ArcAdditionalDelayMs)) / 1000d), () =>
                        {
                            if (!CurrentSource()) return;
                            double now = ProjectileWorldTime(Player);
                            var primary = Player.Area.MonstersNear(from, distance).Where(p => ArcProxyTarget(Player, p.Key, p.Value))
                                .Select(p => (p.Key, At: p.Value.Walk.Position(now))).Where(p => Dist(p.At, from) <= distance)
                                .OrderBy(p => Dist(p.At, aim)).ThenBy(p => p.Key).FirstOrDefault();
                            if (primary.Key == 0) return;
                            var selected = Player.Area.MonstersNear(primary.At, distance)
                                .Where(p => p.Key != primary.Key && ArcProxyTarget(Player, p.Key, p.Value))
                                .Select(p => (p.Key, Distance: Dist(p.Value.Walk.Position(now), primary.At)))
                                .Where(p => p.Distance <= distance).OrderBy(p => p.Distance).ThenBy(p => p.Key)
                                .Take(splits).Select(p => p.Key).ToHashSet();
                            selected.Add(primary.Key);
                            Hit(primary.At, distance, targetFilter: (id, _) => selected.Contains(id));
                        });
                }
                else if (effect == "FlameSurgeAltX")
                {
                    // The spell data describes a forward surge. The local reach and cone angle approximate its hitbox.
                    int reach = Radius(46);
                    Hit(from, reach, targetFilter: (_, at) => GroundSlamWave.Covers(direction, from, at, 25, reach));
                }
                else if (IsCracklingLance(effect))
                    ReleaseCracklingLance(gem.Effect, source.Stats, caster, from, aim,
                        (reach, filter, intensityMore) => Enqueue(() =>
                        {
                            using var scope = intensity is null ? null : new IntensityScope(intensity);
                            return AreaSkillHitForUse(null, Player, gem.Id, gem, from, from, immediate: true, sequence: sequence,
                                repeat: repeat, damageMore: damageMore, radiusOverride: reach, criticalRoll: critical,
                                sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget,
                                targetFilter: filter, hitAndAilmentMore: intensityMore);
                        }), intensity?.Stacks ?? 0, repeat);
                else if (effect is "GlacialCascade" or "GlacialCascadeAltX")
                {
                    int radius = SkillRadius(Player, source.Stats, spell: true, gem.Effect);
                    int spikes = Math.Clamp(Own(Stat.UpheavalNumberOfSpikes) + Own(Stat.GlacialCascadeNumberOfAdditionalBursts), 0, 32);
                    double spacing = CascadeBaseSpacing * Math.Max(.25, radius / 12d);
                    double interval = CascadeInterval / Math.Max(.1, 1 + Own(Stat.GlacialCascadeTravelSpeedPlusPct) / 100d);
                    for (int i = 0; i < spikes; i++)
                    {
                        double distance = (i + 1) * spacing;
                        var at = (from.X + (int)Math.Round(distance * direction.X), from.Y + (int)Math.Round(distance * direction.Y));
                        bool final = i == spikes - 1;
                        double more = final ? Math.Max(0, 1 + Own(Stat.GlacialCascadeFinalSpikeDamagePlusPctFinal) / 100d) : 1;
                        Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(i * interval), () =>
                        {
                            if (CurrentSource()) Hit(at, radius, more);
                        });
                    }
                }
            });

            bool CurrentSource() => Active && !Player.Paused && Player.PlayerLife > 0 && sacrifice.Current
                && SameTriggerSource(source, SupportedSkill(Player, gem.Id));
        }
    }
}
