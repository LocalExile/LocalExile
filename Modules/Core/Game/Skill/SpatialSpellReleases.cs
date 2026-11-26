using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    internal const string ManabondEffect = "Manabond";

    static (int Reach, Func<uint, (int X, int Y), bool>? Covers) ManabondArea(
        Player player, int effect, IReadOnlyDictionary<uint, int> own, (int X, int Y) centre,
        IReadOnlyDictionary<uint, int>? proxyCaster = null)
    {
        var area = SkillAreaStats(player, own, effect, proxyCaster);
        int Radius(int @base) => SkillArea.Radius(@base, area.GetValueOrDefault, spell: true);
        int radius = Radius(SkillArea.BaseRadius(ManabondEffect, own.GetValueOrDefault));
        int unreserved = proxyCaster is null ? UnreservedMana(player) : 0;
        // Totems, traps and mines have a full, empty mana pool of their own.
        int quarters = unreserved > 0 ? (int)Math.Clamp((long)player.PlayerMana * 4 / unreserved, 0, 4) : 4;
        if (quarters == 0) return (radius, null);
        int reach = radius + 6 * quarters;
        int halfWidth = Radius(6);
        return ((int)Math.Ceiling(Math.Sqrt((double)reach * reach + (double)halfWidth * halfWidth)), (_, at) =>
        {
            int x = Math.Abs(at.X - centre.X), y = Math.Abs(at.Y - centre.Y);
            return (double)x * x + (double)y * y <= (double)radius * radius
                || Math.Max(x, y) <= reach && Math.Min(x, y) <= halfWidth;
        });
    }

    static bool SpatialSpellEffect(string? effect) =>
        IsPurifyingFlame(effect) || IsWaveOfConviction(effect) || IsGroundBladeBlast(effect) || IsBladeBlastOfUnloading(effect)
            || IsCracklingLance(effect) || effect is "LightningConduitAltX" or ManabondEffect or "VortexAltX"
            or "FlameSurge" or "FlameSurgeAltX" or ArcSurgingEffect or TrarthanBargain
            || ShockNovaEffect(effect) || GlacialCascadeEffect(effect) || effect == DivineRetributionEffect;

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

        internal void ScheduleSpatialSpell((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) from, (int X, int Y) aim, double windup, Func<bool> current, SacrificeUse sacrifice,
            SkillCriticalRoll? critical, ushort sequence = 0, int repeat = 0, double damageMore = 1,
            SpawnedMonster? brandedTarget = null, uint targetId = 0)
        {
            if (!Active || !current()) return;
            var source = SupportedSkill(Player, gem.Id);
            var intensity = CastingIntensity(Player, gem.Id);
            double beidatMore = CastingBeidatSpell(Player, gem.Id)?.More ?? 1;
            double dotMore = GhorrDotMore(Player, gem.Id);
            long released = Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player))) + ProjectileRuntime.Time(windup);
            Runtime.Schedule(released, () =>
            {
                if (!CurrentSource() || !sacrifice.Triggered && !current()) return;
                int Own(uint key) => source.Stats.GetValueOrDefault(key);
                var caster = CharacterPlayerStats(Player);
                var effect = SkillsFor(Player).EffectId(gem.Effect);
                var direction = GroundSlamWave.Direction(from, aim);
                if (direction == default) direction = (1, 0);
                int Radius(int baseRadius) => SkillRadius(Player, baseRadius, source.Stats, true, gem.Effect, caster);
                void Hit((int X, int Y) centre, int radius, double more = 1,
                    Func<uint, (int X, int Y), bool>? targetFilter = null, bool centred = false,
                    int extraShockChance = 0, double burstMore = 1, CascadeBurst cascadeBurst = CascadeBurst.None,
                    long baseChaos = 0, bool nearbyOnly = false) =>
                    Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, centred ? centre : from, centre, immediate: true, sequence: sequence,
                        repeat: repeat, damageMore: damageMore * more, radiusOverride: radius, criticalRoll: critical,
                        sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget, targetFilter: targetFilter,
                        hitAndAilmentMore: beidatMore * burstMore, extraShockChance: extraShockChance, cascadeBurst: cascadeBurst,
                        baseChaos: baseChaos, nearbyOnly: nearbyOnly));

                if (IsPurifyingFlame(effect))
                {
                    SchedulePurifyingFlame(Player, this, gem.Effect, source.Stats, caster, from, aim, CurrentSource,
                        (radius, filter, waveMore, onHit) => Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, from, aim,
                            immediate: true, sequence: sequence, repeat: repeat, damageMore: damageMore, radiusOverride: radius,
                            criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget,
                            targetFilter: filter, hitAndAilmentMore: waveMore * beidatMore, onDirectHit: onHit)), repeat);
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
                            hitAndAilmentMore: scale * beidatMore)), repeat);
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
                else if (effect == ManabondEffect)
                    Enqueue(() =>
                    {
                        if (!CurrentSource()) return Task.CompletedTask;
                        // Resolve geometry and base lightning in the same contact, after the skill's payment.
                        var area = ManabondArea(Player, gem.Effect, source.Stats, aim);
                        return AreaSkillHitForUse(null, Player, gem.Id, gem, from, aim, immediate: true, sequence: sequence,
                            repeat: repeat, damageMore: damageMore, radiusOverride: area.Reach, criticalRoll: critical,
                            sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget, targetFilter: area.Covers,
                            hitAndAilmentMore: beidatMore);
                    });
                else if (effect == "VortexAltX")
                    ReleaseVortexProjection(gem, from, aim, source.Stats, caster, critical, sacrifice, CurrentSource,
                        sequence, repeat, damageMore, brandedTarget, dotMore);
                else if (effect == TrarthanBargain)
                {
                    long chaos = GainTrarthanRuin(Player, Player.Id, gem.Id, source.Stats,
                        Player.PlayerLife, Player.PlayerMaxLife, out uint paid);
                    if (paid > 0)
                    {
                        Player.PlayerLife -= paid;
                        PresentStats(Player);
                    }
                    var centre = SpellCentre(Player, gem.Effect, from, aim);
                    Hit(centre, SkillRadius(Player, source.Stats, spell: true, gem.Effect), centred: true, baseChaos: chaos);
                }
                else if (ShockNovaEffect(effect))
                    ScheduleShockNova(gem.Effect, gem.Level, gem.Quality, SpellCentre(Player, gem.Effect, from, aim), aim, source.Stats, caster, CurrentSource,
                        pulse => Hit(pulse.Centre, pulse.Radius, pulse.DamageMore,
                            targetFilter: (_, at) => pulse.OutsideInnerRadius(at), centred: true, extraShockChance: pulse.ExtraShockChance));
                else if (effect == ArcSurgingEffect)
                {
                    int distance = Own(Stat.ArcChainDistance);
                    int splits = ArcSplitCount(source.Stats, caster);
                    if (distance > 0)
                        Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(Math.Max(0, Own(Stat.ArcAdditionalDelayMs)) / 1000d), () =>
                        {
                            if (!CurrentSource()) return;
                            double now = ProjectileWorldTime(Player);
                            var primary = (Key: 0u, At: (X: 0, Y: 0));
                            if (targetId != 0 && Player.Area.Monsters.TryGetValue(targetId, out var target)
                                && ArcProxyTarget(Player, targetId, target))
                            {
                                var at = target.Walk.Position(now);
                                if (Dist(at, from) <= distance) primary = (targetId, at);
                            }
                            if (primary.Key == 0)
                                primary = Player.Area.MonstersNear(from, distance).Where(p => ArcProxyTarget(Player, p.Key, p.Value))
                                    .Select(p => (p.Key, At: p.Value.Walk.Position(now))).Where(p => Dist(p.At, from) <= distance)
                                    .OrderBy(p => Dist(p.At, aim)).ThenBy(p => p.Key).FirstOrDefault();
                            if (primary.Key == 0) return;
                            var selected = ArcSplitTargets(Player, primary.Key, primary.At, now, distance, splits).ToHashSet();
                            selected.Add(primary.Key);
                            Hit(primary.At, distance, targetFilter: (id, _) => selected.Contains(id));
                        });
                }
                else if (effect is "FlameSurge" or "FlameSurgeAltX")
                {
                    // Both gems have the same base reach; the client data adds length with gem level.
                    int reach = Radius(30 + Own(Stat.ActiveSkillBaseAreaLengthPlus));
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
                                targetFilter: filter, hitAndAilmentMore: intensityMore * beidatMore);
                        }), intensity?.Stacks ?? 0, repeat);
                else if (effect == DivineRetributionEffect)
                    ScheduleDivineRetribution(Player.Walk.Position(ProjectileWorldTime(Player)), aim,
                        Own(Stat.DivineRetributionNumWaves), Own(Stat.DivineRetributionBlastsPerWave), Radius,
                        SkillArea.BaseRadius(effect, Own), CurrentSource,
                        (centre, radius) => Hit(centre, radius, nearbyOnly: true));
                else if (effect is not null && GlacialCascadeEffect(effect))
                    ScheduleGlacialCascade(effect, from, aim, source.Stats,
                        () => SkillRadius(Player, source.Stats, spell: true, gem.Effect), CurrentSource,
                        pulse => Hit(pulse.Centre, pulse.Radius, burstMore: pulse.DamageMore, cascadeBurst: pulse.Burst));
            });

            bool CurrentSource() => Active && !Player.Paused && Player.PlayerLife > 0 && sacrifice.Current
                && (!sacrifice.Triggered || current())
                && SameTriggerSource(source, SupportedSkill(Player, gem.Id));
        }
    }
}
