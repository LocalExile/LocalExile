using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string ExplosiveArrowExplosion = "Metadata/Effects/Spells/explosivearrow_2/explosion/explosion.ao";

    public sealed record ExplosiveFuseSpec(long Duration, int Limit, int RadiusPerFuse, int RadiusCap,
        double RadiusScale, int HitPerFuse, int AilmentPerFuse, string Art, ProjectileOffenseAlternate Damage);

    static ExplosiveFuseSpec CaptureExplosiveFuse(ProjectileReleaseContext context, SupportedHitInputs inputs,
        string hand, IReadOnlyDictionary<uint, int>? local)
    {
        var raw = inputs.Raw.Scale(Math.Max(0, 1 + context.Get(Stat.ExplosiveArrowExplosionBaseDamagePlusPermyriad) / 10000d));
        raw = raw.Add(DamageType.Fire, new(context.Get(Stat.ExplosiveArrowExplosionMinimumAddedFireDamage),
            context.Get(Stat.ExplosiveArrowExplosionMaximumAddedFireDamage)));
        var explosion = inputs with { Raw = raw };
        var rows = SupportedAilmentRowsFrom(explosion, hand);
        var player = context.Player;
        var skill = context.SkillId;
        var stats = SkillAreaStats(player, SupportedSkill(player, skill).Stats, context.Gem.Effect);
        int Area(uint key) => stats.GetValueOrDefault(key) + (key == Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal
            ? context.Get(Stat.ExplosiveArrowRadiusPlusPct) : 0);
        var damage = new ProjectileOffenseAlternate(SupportedHitFrom(explosion, suppress: false), null,
            WithOverheat(player, skill, MonsterIgnites.Settings.From(rows, hand, context.Get)),
            WithOverheat(player, skill, BleedSettings(rows, hand, context.Get, local, melee: false, bow: true, projectileAttack: true)),
            WithOverheat(player, skill, MonsterPoisons.Settings.From(rows, hand, context.Get, local, projectileAttack: true)));
        double seconds = SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration), context.PlayerStat,
            key => context.Own(key) + (key == Stat.SkillEffectDurationPlusPct ? context.Get(Stat.ExplosiveArrowDurationPlusPct) : 0));
        return new(Math.Max(1, ProjectileRuntime.Time(seconds)), Math.Clamp(context.Get(Stat.ExplosiveArrowStackLimit), 1, 256),
            context.Get(Stat.FuseArrowExplosionRadiusPlusPerFuseArrowOrb), context.Get(Stat.ExplosiveArrowMaximumBonusExplosionRadius),
            1 + SkillArea.RadiusPercent(Area, spell: false) / 100d,
            context.Get(Stat.ExplosiveArrowHitDamagePlusPctFinalPerStack),
            context.Get(Stat.ExplosiveArrowAilmentDamagePlusPctFinalPerStack),
            Art.AnimatedInPlaceOf(ExplosiveArrowExplosion, context.Variation), damage);
    }

    sealed class ExplosiveFuses(ProjectileSession session, ProjectileUse first, ExplosiveFuseSpec spec,
        ProjectileVector position, SpawnedMonster? target, uint targetId)
    {
        public ProjectileSession Session { get; } = session;
        public ProjectileUse First { get; } = first;
        public ExplosiveFuseSpec Spec { get; } = spec;
        public ProjectileVector Position { get; } = position;
        public SpawnedMonster? Target { get; } = target;
        public uint TargetId { get; } = targetId;
        public DamagePacket Hit { get; private set; } = DamagePacket.Empty;
        public double IgniteMin, IgniteMax, IgniteCritMin, IgniteCritMax;
        public double BleedMin, BleedMax, BleedCritMin, BleedCritMax;
        public double PoisonMin, PoisonMax, PoisonCritMin, PoisonCritMax;
        public int Count { get; private set; }
        public bool Detonated;

        public void Add(ProjectileOffense shot, ExplosiveFuseSpec fuse)
        {
            Count++;
            var damage = fuse.Damage;
            Hit = Hit.Add(damage.Hit.Scale(shot.HitProfileMore));
            double more = shot.AilmentProfileMore;
            IgniteMin += damage.Ignite.Min * more;
            IgniteMax += damage.Ignite.Max * more;
            IgniteCritMin += damage.Ignite.CriticalMin * more;
            IgniteCritMax += damage.Ignite.CriticalMax * more;
            BleedMin += damage.Bleed.Min * more;
            BleedMax += damage.Bleed.Max * more;
            BleedCritMin += damage.Bleed.CriticalMin * more;
            BleedCritMax += damage.Bleed.CriticalMax * more;
            PoisonMin += damage.Poison.Min * more;
            PoisonMax += damage.Poison.Max * more;
            PoisonCritMin += damage.Poison.CriticalMin * more;
            PoisonCritMax += damage.Poison.CriticalMax * more;
        }
    }

    static readonly RuleTable<SpawnedMonster, ExplosiveFuses> FusedEnemies = new();

    public sealed partial class ProjectileSession
    {
        void ExplosiveArrowAtEnd(ProjectileEvent value)
        {
            if (value.Projectile.Use.Payload is not ProjectileOffense { ExplosiveFuse: { } fuse } shot) return;
            SpawnedMonster? target = null;
            uint id = 0;
            if (value.HitTarget is { } key && Player.Area.Monsters.TryGetValue(key.Id, out var enemy)
                && Key(key.Id, enemy) == key)
            {
                target = enemy;
                id = key.Id;
            }
            else if (value.Reason != ProjectileEndReason.Terrain) return;
            if (target is { Invulnerable: true }) return;

            if (target is not null && FusedEnemies.TryGetValue(target, out var existing) && existing.Session.Active)
            {
                existing.Add(shot, fuse);
                if (existing.Count >= existing.Spec.Limit)
                {
                    FusedEnemies.Remove(target);
                    var runtime = existing.Session.Runtime;
                    runtime.Schedule(Math.Max(runtime.Now, value.At),
                        () => existing.Session.DetonateExplosiveArrow(existing, runtime.Now));
                }
                return;
            }

            var group = new ExplosiveFuses(this, value.Projectile.Use, fuse, value.Position, target, id);
            group.Add(shot, fuse);
            if (target is not null) FusedEnemies.AddOrUpdate(target, group);
            // Even a cap reached by this impact runs once the flight's contact resolver has unwound.
            Runtime.Schedule(value.At + (group.Count >= fuse.Limit || target is { Dead: true } ? 0 : fuse.Duration),
                () => DetonateExplosiveArrow(group, Runtime.Now));
        }

        void DetonateExplosiveArrow(ExplosiveFuses group, long at)
        {
            if (group.Detonated || !Active) return;
            group.Detonated = true;
            var target = group.Target;
            var centre = group.Position;
            if (target is not null)
            {
                if (FusedEnemies.TryGetValue(target, out var currentGroup) && ReferenceEquals(currentGroup, group))
                    FusedEnemies.Remove(target);
                if (Player.Area.Monsters.TryGetValue(group.TargetId, out var current) && ReferenceEquals(current, target))
                    centre = Vector(target.Walk.Position(at / (double)ProjectileRuntime.QuantaPerSecond));
            }

            var spec = group.Spec;
            var first = (ProjectileOffense)group.First.Payload!;
            double ailments = Math.Max(0, 1 + group.Count * spec.AilmentPerFuse / 100d);
            var blast = first with
            {
                AreaHit = group.Hit.Scale(Math.Max(0, 1 + group.Count * spec.HitPerFuse / 100d)),
                Ignite = spec.Damage.Ignite with { Min = group.IgniteMin * ailments, Max = group.IgniteMax * ailments,
                    CriticalMin = group.IgniteCritMin * ailments, CriticalMax = group.IgniteCritMax * ailments },
                Bleed = spec.Damage.Bleed with { Min = group.BleedMin * ailments, Max = group.BleedMax * ailments,
                    CriticalMin = group.BleedCritMin * ailments, CriticalMax = group.BleedCritMax * ailments },
                Poison = spec.Damage.Poison with { Min = group.PoisonMin * ailments, Max = group.PoisonMax * ailments,
                    CriticalMin = group.PoisonCritMin * ailments, CriticalMax = group.PoisonCritMax * ailments },
                ExplosiveFuse = null, Spider = null, SpiderArea = null, VsBlinded = null,
                HitPacket = false, MirageSource = null
            };
            double radius = Math.Max(1, Math.Floor((15 + Math.Min(spec.RadiusCap, group.Count * spec.RadiusPerFuse)) * spec.RadiusScale));
            Effect((int)Math.Round(centre.X), (int)Math.Round(centre.Y), spec.Art, ProjectileRuntime.Time(1), radius);
            Runtime.Impact(group.First with { Payload = blast }, centre, new("explosive-arrow", radius));
        }
    }
}
