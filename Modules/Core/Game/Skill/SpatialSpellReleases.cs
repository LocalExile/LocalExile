using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool SpatialSpellEffect(string? effect) =>
        effect is "CracklingLanceAltX" or "CracklingLanceAltY" or "GlacialCascade" or "GlacialCascadeAltX"
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
        // The gem rows provide the branch angle modifier and the cascade count and damage. Beam lengths, spike spacing
        // and spike travel time are local collision estimates in the absence of a captured client collision trace.
        const int LanceLength = 60;
        const double LanceBranchAngle = 24;
        const int CascadeBaseSpacing = 12;
        const double CascadeInterval = .045;

        internal void ScheduleSpatialSpell((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) from, (int X, int Y) aim, double windup, Func<bool> current, SacrificeUse sacrifice,
            SkillCriticalRoll? critical, ushort sequence = 0, int repeat = 0, double damageMore = 1,
            SpawnedMonster? brandedTarget = null)
        {
            if (!Active || !current()) return;
            var source = SupportedSkill(Player, gem.Id);
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

                if (effect == "LightningConduitAltX")
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
                else if (effect is "CracklingLanceAltX" or "CracklingLanceAltY")
                {
                    int width = Radius(effect == "CracklingLanceAltX" ? 4 : 3);
                    double length = LanceLength * Math.Max(.25, Radius(12) / 12d);
                    if (width > 0)
                        Hit(from, (int)Math.Ceiling(length + width), targetFilter: (_, at) => LanceCovers(at));

                    bool LanceCovers((int X, int Y) at)
                    {
                        double x = at.X - from.X, y = at.Y - from.Y;
                        double along = x * direction.X + y * direction.Y;
                        double lateral = y * direction.X - x * direction.Y;
                        if (along < 0 || along > length) return false;
                        if (Math.Abs(lateral) <= width) return true;
                        if (effect != "CracklingLanceAltX") return false;
                        double radians = Math.Clamp(LanceBranchAngle * (1 + Own(Stat.DisintegrateSecondaryBeamAnglePlusPct) / 100d), 0, 80)
                            * Math.PI / 180d;
                        double forward = Math.Cos(radians), side = Math.Sin(radians);
                        return BranchAt(.34) || BranchAt(.60);

                        bool BranchAt(double fraction)
                        {
                            double a = along - length * fraction, b = Math.Abs(lateral);
                            double branchAlong = a * forward + b * side;
                            return branchAlong >= 0 && branchAlong <= length * .56
                                && Math.Abs(b * forward - a * side) <= width;
                        }
                    }
                }
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
