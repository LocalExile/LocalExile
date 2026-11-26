using System.Collections.Concurrent;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string FuseArrowProjectile = "Metadata/Projectiles/FuseArrow";
    const string ExplosiveArrowExplosion = "Metadata/Effects/Spells/explosivearrow_2/explosion/explosion.ao";

    static bool IsExplosiveArrow(string? effect) =>
        effect is "ExplosiveArrow" or "ExplosiveArrowAltX" or "ExplosiveArrowAltY" or "ExplosiveArrowRoyale";

    public sealed record ExplosiveFuseSpec(long Duration, int Limit, int BaseRadius, int RadiusPerFuse, int RadiusCap,
        double RadiusScale, int HitPerFuse, int AilmentPerFuse, string Art, ProjectileOffenseAlternate Damage);

    static ExplosiveFuseSpec CaptureExplosiveFuse(ProjectileReleaseContext context, SupportedHitInputs inputs,
        IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int> own,
        string hand, IReadOnlyDictionary<uint, int>? local)
    {
        var raw = inputs.Raw.Scale(Math.Max(0, 1 + context.Get(Stat.ExplosiveArrowExplosionBaseDamagePlusPermyriad) / 10000d));
        raw = raw.Add(DamageType.Fire, new(context.Get(Stat.ExplosiveArrowExplosionMinimumAddedFireDamage),
            context.Get(Stat.ExplosiveArrowExplosionMaximumAddedFireDamage)));
        var explosion = inputs with { Raw = raw };
        var rows = SupportedAilmentRowsFrom(explosion, hand);
        var player = context.Player;
        var skill = context.SkillId;
        var stats = SkillAreaStats(player, own, context.Gem.Effect, playerStats);
        int Area(uint key) => key == SkillArea.BaseIncreased
            ? stats.GetValueOrDefault(key) + context.Get(Stat.ExplosiveArrowRadiusPlusPct)
            : stats.GetValueOrDefault(key);
        int baseRadius = Math.Max(0, 15 + stats.GetValueOrDefault(Stat.ActiveSkillBaseRadiusPlus));
        var damage = new ProjectileOffenseAlternate(SupportedHitFrom(explosion, suppress: false), null,
            WithOverheat(player, skill, NotedIgnite(player, skill, WithIgniteSpread(
                IgniteSettingsFrom(rows, hand, context.Get), playerStats, own, spell: false, hand))),
            WithOverheat(player, skill, BleedSettings(rows, hand, context.Get, local, melee: false, bow: true, projectileAttack: true)),
            WithOverheat(player, skill, PoisonSettingsFrom(rows, hand, context.Get, local, projectileAttack: true)));
        double seconds = SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration), context.PlayerStat,
            key => context.Own(key) + (key == Stat.SkillEffectDurationPlusPct ? context.Get(Stat.ExplosiveArrowDurationPlusPct) : 0));
        return new(Math.Max(1, ProjectileRuntime.Time(seconds)), Math.Clamp(context.Get(Stat.ExplosiveArrowStackLimit), 1, 256),
            baseRadius, context.Get(Stat.FuseArrowExplosionRadiusPlusPerFuseArrowOrb),
            context.Get(Stat.ExplosiveArrowMaximumBonusExplosionRadius),
            1 + SkillArea.RadiusPercent(Area, spell: false) / 100d,
            context.Get(Stat.ExplosiveArrowHitDamagePlusPctFinalPerStack),
            context.Get(Stat.ExplosiveArrowAilmentDamagePlusPctFinalPerStack),
            Art.AnimatedInPlaceOf(ExplosiveArrowExplosion, context.Variation), damage);
    }

    internal sealed class ExplosiveFuses(ProjectileSession session, ProjectileUse first, ExplosiveFuseSpec spec,
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
        int detonated;
        public bool Detonated => Volatile.Read(ref detonated) != 0;
        public bool TryDetonate() => Interlocked.Exchange(ref detonated, 1) == 0;

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

    static readonly RuleTable<SpawnedMonster, List<ExplosiveFuses>> FusedEnemies = new();

    static void DetonateExplosiveFusesOnDeath(SpawnedMonster monster, ProjectileSession? currentSession = null)
    {
        if (!FusedEnemies.TryGetValue(monster, out var fuses)) return;
        ExplosiveFuses[] pending;
        lock (fuses)
        {
            if (fuses.Count == 0) return;
            pending = [.. fuses];
        }
        foreach (var group in pending)
        {
            if (!group.Session.Active || group.Detonated) continue;
            if (ReferenceEquals(group.Session, currentSession))
            {
                // Keep group in FusedEnemies until detonation runs so a lethal 2nd+ Explosive Arrow can add its fuse first.
                var session = group.Session;
                session.Runtime.Schedule(session.Runtime.Now, () => session.DetonateExplosiveArrow(group, session.Runtime.Now));
            }
            else if (currentSession is null)
            {
                var session = group.Session;
                lock (LevelUpdatesFor(session.Player).Gate)
                {
                    if (!session.Active || group.Detonated) continue;
                    session.Runtime.Schedule(session.Runtime.Now, () => session.DetonateExplosiveArrow(group, session.Runtime.Now));
                    session.Runtime.AdvanceTo(session.Runtime.Now);
                }
            }
            else
            {
                // Never acquire another player's Gate while holding currentSession's Gate or mutate their Runtime cross-thread.
                group.Session.QueueDeathDetonation(group);
            }
        }
    }

    public sealed partial class ProjectileSession
    {
        readonly HashSet<ExplosiveFuses> activeExplosiveFuses = new(ReferenceEqualityComparer.Instance);
        readonly ConcurrentQueue<ExplosiveFuses> pendingDeathDetonations = new();

        internal void QueueDeathDetonation(ExplosiveFuses group) => pendingDeathDetonations.Enqueue(group);

        void DrainPendingExplosiveDetonations()
        {
            while (pendingDeathDetonations.TryDequeue(out var group))
                if (Active && !group.Detonated)
                    Runtime.Schedule(Runtime.Now, () => DetonateExplosiveArrow(group, Runtime.Now));
        }

        void ClearExplosiveFuses()
        {
            while (pendingDeathDetonations.TryDequeue(out _)) { }
            ExplosiveFuses[] owned;
            lock (activeExplosiveFuses)
            {
                if (activeExplosiveFuses.Count == 0) return;
                owned = [.. activeExplosiveFuses];
                activeExplosiveFuses.Clear();
            }
            foreach (var group in owned)
            {
                group.TryDetonate();
                if (group.Target is { } target && FusedEnemies.TryGetValue(target, out var fuses))
                    lock (fuses) fuses.Remove(group);
            }
        }

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

            if (target is not null)
            {
                var fuses = FusedEnemies.GetOrCreateValue(target);
                ExplosiveFuses? existing = null;
                lock (fuses)
                {
                    for (int i = fuses.Count - 1; i >= 0; i--)
                    {
                        var candidate = fuses[i];
                        if (!candidate.Session.Active || candidate.Detonated)
                        {
                            fuses.RemoveAt(i);
                            continue;
                        }
                        if (ReferenceEquals(candidate.Session, this))
                        {
                            existing = candidate;
                            break;
                        }
                    }
                    if (existing is not null)
                    {
                        existing.Add(shot, fuse);
                        if (existing.Count >= existing.Spec.Limit || target.Dead)
                            fuses.Remove(existing);
                    }
                }
                if (existing is not null)
                {
                    if (existing.Count >= existing.Spec.Limit || target.Dead)
                        Runtime.Schedule(Math.Max(Runtime.Now, value.At),
                            () => DetonateExplosiveArrow(existing, Runtime.Now));
                    return;
                }
            }

            var group = new ExplosiveFuses(this, value.Projectile.Use, fuse, value.Position, target, id);
            group.Add(shot, fuse);
            bool immediate = group.Count >= fuse.Limit || target is { Dead: true };
            if (target is not null && !immediate)
            {
                var fuses = FusedEnemies.GetOrCreateValue(target);
                lock (fuses) fuses.Add(group);
            }
            lock (activeExplosiveFuses) activeExplosiveFuses.Add(group);
            // Even a cap reached or lethal hit runs once the flight's contact resolver has unwound.
            Runtime.Schedule(value.At + (immediate ? 0 : fuse.Duration),
                () => DetonateExplosiveArrow(group, Runtime.Now));
        }

        internal void DetonateExplosiveArrow(ExplosiveFuses group, long at)
        {
            if (!Active || !group.TryDetonate()) return;
            lock (activeExplosiveFuses) activeExplosiveFuses.Remove(group);
            var target = group.Target;
            var centre = group.Position;
            if (target is not null)
            {
                if (FusedEnemies.TryGetValue(target, out var fuses))
                    lock (fuses) fuses.Remove(group);
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
                AlwaysHits = true, AccuracyProfile = null, ProjectileAttack = false,
                ExplosiveFuse = null, Spider = null, SpiderArea = null, VsBlinded = null,
                HitPacket = false, MirageSource = null
            };
            double radius = Math.Max(1, Math.Floor((spec.BaseRadius + Math.Min(spec.RadiusCap, group.Count * spec.RadiusPerFuse)) * spec.RadiusScale));
            Effect((int)Math.Round(centre.X), (int)Math.Round(centre.Y), spec.Art, ProjectileRuntime.Time(1), radius);
            Runtime.Impact(group.First with { Payload = blast }, centre, new("explosive-arrow", radius));
        }
    }
}
