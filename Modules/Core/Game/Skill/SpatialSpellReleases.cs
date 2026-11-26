using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool SpatialSpellEffect(string? effect) =>
        effect is "CracklingLanceAltX" or "CracklingLanceAltY"
            or "LightningConduitAltX" or "Manabond" or "VortexAltX" or "FlameSurge" or "FlameSurgeAltX" or ArcSurgingEffect or TrarthanBargain
            || ShockNovaEffect(effect) || GlacialCascadeEffect(effect);

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
        // The gem rows provide the branch angle modifier. Beam lengths use local collision estimates.
        const int LanceLength = 60;
        const double LanceBranchAngle = 24;

        internal void ScheduleSpatialSpell((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) from, (int X, int Y) aim, double windup, Func<bool> current, SacrificeUse sacrifice,
            SkillCriticalRoll? critical, ushort sequence = 0, int repeat = 0, double damageMore = 1,
            SpawnedMonster? brandedTarget = null, uint targetId = 0)
        {
            if (!Active || !current()) return;
            var source = SupportedSkill(Player, gem.Id);
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
                    long baseChaos = 0) =>
                    Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, centred ? centre : from, centre, immediate: true, sequence: sequence,
                        repeat: repeat, damageMore: damageMore * more, radiusOverride: radius, criticalRoll: critical,
                        sacrifice: sacrifice, triggeredCurrent: CurrentSource, brandedTarget: brandedTarget, targetFilter: targetFilter,
                        hitAndAilmentMore: beidatMore * burstMore, extraShockChance: extraShockChance, cascadeBurst: cascadeBurst,
                        baseChaos: baseChaos));

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
