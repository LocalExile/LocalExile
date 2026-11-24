using System.Runtime.CompilerServices;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static double CausticGroundDps(Func<uint, int> get, Func<uint, int> own)
    {
        if (DamageStats.Prohibited(DamageType.Chaos, get)) return 0;
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ChaosDamagePlusPct) + get(Stat.AreaDamagePlusPct)
            + get(Stat.ProjectileDamagePlusPct) + get(Stat.DamageOverTimePlusPct) + get(Stat.ChaosDamageOverTimePlusPct)
            + get(Stat.CausticArrowDamagePlusPct) + get(Stat.CausticArrowDamageOverTimePlusPct);
        double more = 1;
        foreach (var stat in new[]
        {
            Stat.SupportVoidManipulationChaosDamagePlusPctFinal,
            Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
            Stat.SupportEfficacyDamageOverTimePlusPctFinal,
            Stat.SupportPhysChaosProjectileChaosDamageOverTimePlusPctFinal,
            Stat.SupportAreaConcentrateAreaDamagePlusPctFinal,
            Stat.CrueltyDamageOverTimePlusPctFinal,
            Stat.SupportManaforgedArrowsDamagePlusPctFinal,
        }) more *= Math.Max(0, 1 + own(stat) / 100d);
        return Math.Max(0, get(Stat.BaseChaosDamageToDealPerMinute) / 60d) * more * Math.Max(0, 1 + increased / 100)
            * Math.Max(0, 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ChaosDotMultiplierPlus)
                + get(Stat.DotMultiplierPlusWithBowSkills)) / 100);
    }

    public sealed partial class ProjectileSession
    {
        sealed record DamageGround(ProjectileSession Source, ProjectileVector Centre, double Radius, long Start, long End,
            DamageType Type, double Dps, Visual? Visual, uint ChillSource, IReadOnlyDictionary<uint, int>? ChillStats)
        {
            public readonly HashSet<SpawnedMonster> Chilled = [];
            public ProjectileVector CurrentCentre = Centre;
            public GroundMovement? Movement { get; init; }
        }

        sealed class GroundDamageCarry { public double Fire, Cold, Chaos; }

        sealed class AreaDamageGrounds
        {
            public readonly List<DamageGround> Grounds = [];
            public readonly ConditionalWeakTable<SpawnedMonster, GroundDamageCarry> Remainders = new();
            public long Previous, Next;
        }

        static readonly RuleTable<GameArea, AreaDamageGrounds> DamageGroundsByArea = new();
        static readonly long DamageGroundInterval = ProjectileRuntime.QuantaPerSecond / 10;
        GameArea? damageGroundArea;
        bool groundFallbackScheduled;

        void ClearDamageGrounds()
        {
            if (damageGroundArea is not { } area) return;
            damageGroundArea = null;
            groundFallbackScheduled = false;
            if (area.Driven && !area.Owned) area.Defer(RemoveDamageGrounds);
            else RemoveDamageGrounds(area);
        }

        void RemoveDamageGrounds(GameArea area)
        {
            if (!DamageGroundsByArea.TryGetValue(area, out var state)) return;
            for (int i = state.Grounds.Count - 1; i >= 0; i--)
            {
                var ground = state.Grounds[i];
                if (!ReferenceEquals(ground.Source, this)) continue;
                state.Grounds.RemoveAt(i);
                RemoveDamageGround(area, ground);
            }
            if (state.Grounds.Count == 0) state.Remainders.Clear();
        }

        static void RemoveDamageGround(GameArea area, DamageGround ground)
        {
            foreach (var monster in ground.Chilled)
                monster.Ailments.RemoveAura(MonsterElementalAilments.Kind.Chill, ground.ChillSource);
            if (ground.Visual is not { } visual) return;
            visual.Ended = true;
            ground.Source.Player.ProjectileObjects.Remove(visual.Id);
            if (!visual.Created) return;
            if (area.Driven) area.Delta.RemoveSharedObject(visual.Id, InstanceProtocol.RemoveObject(visual.Id));
            else ground.Source.WorldRecords(visual.Id, () => [InstanceProtocol.RemoveObject(visual.Id)], remove: true);
        }

        internal void PlaceCausticCloud(ProjectileVector centre, ProjectileOffense.CausticCloudSpec spec) =>
            PlaceDamageGround(centre, spec.Radius, spec.Duration, DamageType.Chaos, spec.Dps, Runtime.Now, spec.ArtVariation);

        internal void PlaceDamageGround(ProjectileVector centre, double radius, double seconds, DamageType type,
            double dps, long start, int artVariation = 0, IReadOnlyDictionary<uint, int>? chillStats = null)
        {
            if (radius <= 0 || seconds <= 0 || dps <= 0 && chillStats is null) return;
            Visual? visual = null;
            var kind = GroundKindOf(type switch
            {
                DamageType.Fire => "Fire",
                DamageType.Cold => "Maelstorm",
                _ => "CausticCloud",
            }, artVariation);
            if (kind is { Art.Length: > 0 })
            {
                uint id = Player.TakeObjectId();
                visual = new Visual(id, default);
                Player.ProjectileObjects.Add(id);
                float scale = kind.ArtCells > 0 ? (float)(radius / kind.ArtCells) : 1;
                var ground = new GroundEffectObject((int)Math.Round(centre.X), (int)Math.Round(centre.Y),
                    kind.ArtRow, kind.Art, (float)seconds) { Scale = scale, Team = 0 };
                Create(id, () =>
                {
                    if (visual.Ended) return null;
                    visual.Created = true;
                    return InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(GroundEffectObject.TypePath), ground.Blob());
                }, wake: true);
            }
            AddDamageGround(centre, radius, seconds, type, dps, start, visual, chillStats);
        }

        void AddDamageGround(ProjectileVector centre, double radius, double seconds, DamageType type,
            double dps, long start, Visual? visual = null, IReadOnlyDictionary<uint, int>? chillStats = null,
            GroundMovement? movement = null)
        {
            var area = Player.Area;
            var state = DamageGroundsByArea.GetOrCreateValue(area);
            damageGroundArea = area;
            start = Math.Max(start, Math.Max(Runtime.Now, state.Grounds.Count == 0 ? 0 : state.Previous));
            long end = start + Math.Max(1, ProjectileRuntime.Time(seconds));
            uint chillSource = chillStats is null ? 0 : visual?.Id ?? Player.TakeObjectId();
            if (state.Grounds.Count == 0)
            {
                state.Previous = start;
                state.Next = start + DamageGroundInterval;
            }
            if (movement is { Limit: > 0 })
            {
                var matching = state.Grounds.Where(g => ReferenceEquals(g.Source, this)
                    && g.Movement?.Effect == movement.Effect).ToArray();
                foreach (var oldest in matching.Take(matching.Length - movement.Limit + 1))
                {
                    state.Grounds.Remove(oldest);
                    RemoveDamageGround(area, oldest);
                }
            }
            state.Grounds.Add(new(this, centre, radius, start, end, type, dps, visual, chillSource, chillStats)
                { Movement = movement });
            state.Next = Math.Min(state.Next, end);
            if (AreaRunsGame(Player) || groundFallbackScheduled) return;
            groundFallbackScheduled = true;
            Runtime.Schedule(Math.Max(Runtime.Now + 1, state.Next), TickFallbackDamageGrounds);
        }

        void TickFallbackDamageGrounds()
        {
            if (damageGroundArea is not { } area || !DamageGroundsByArea.TryGetValue(area, out var state)
                || AreaRunsGame(Player))
            {
                groundFallbackScheduled = false;
                return;
            }
            AdvanceDamageGrounds(area, Runtime.Now);
            groundFallbackScheduled = state.Grounds.Any(ground => ReferenceEquals(ground.Source, this));
            if (groundFallbackScheduled) Runtime.Schedule(Math.Max(Runtime.Now + 1, state.Next), TickFallbackDamageGrounds);
        }

        internal static void AdvanceAreaDamageGrounds(GameArea area) => AdvanceDamageGrounds(area, ProjectileRuntime.Time(area.Time));

        static void AdvanceDamageGrounds(GameArea area, long now)
        {
            if (!DamageGroundsByArea.TryGetValue(area, out var state) || state.Grounds.Count == 0 || now < state.Next) return;
            long previous = state.Previous;
            var boundaries = new SortedSet<long> { now };
            foreach (var ground in state.Grounds)
            {
                if (previous < ground.Start && ground.Start < now) boundaries.Add(ground.Start);
                if (previous < ground.End && ground.End < now) boundaries.Add(ground.End);
            }
            foreach (long through in boundaries)
            {
                if (through > previous) ApplyDamageGrounds(area, state, previous, through);
                previous = through;
            }
            state.Previous = now;
            state.Next = now + DamageGroundInterval;
            for (int i = state.Grounds.Count - 1; i >= 0; i--)
            {
                var ground = state.Grounds[i];
                if (now < ground.End && ground.Source.Active)
                {
                    state.Next = Math.Min(state.Next, ground.End);
                    continue;
                }
                state.Grounds.RemoveAt(i);
                RemoveDamageGround(area, ground);
            }
            if (state.Grounds.Count == 0) state.Remainders.Clear();
        }

        sealed class GroundExposure(SpawnedMonster monster)
        {
            public readonly SpawnedMonster Monster = monster;
            public DamageGround? Fire, Cold, Chaos;

            public void Consider(DamageGround ground)
            {
                switch (ground.Type)
                {
                    case DamageType.Fire:
                        if (ground.Dps > (Fire?.Dps ?? 0)) Fire = ground;
                        break;
                    case DamageType.Cold:
                        if (ground.Dps > (Cold?.Dps ?? 0)) Cold = ground;
                        break;
                    case DamageType.Chaos:
                        if (ground.Dps > (Chaos?.Dps ?? 0)) Chaos = ground;
                        break;
                }
            }
        }

        static bool DamageGroundTarget(SpawnedMonster monster) =>
            monster is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
            && monster.Effects[Stat.BaseCannotBeDamaged] == 0;

        static void ApplyDamageGrounds(GameArea area, AreaDamageGrounds state, long from, long to)
        {
            double at = to / (double)ProjectileRuntime.QuantaPerSecond;
            double seconds = (to - from) / (double)ProjectileRuntime.QuantaPerSecond;
            var exposures = new SortedDictionary<uint, GroundExposure>();
            foreach (var ground in state.Grounds)
            {
                if (!ground.Source.Active || from < ground.Start || from >= ground.End) continue;
                if (ground.Movement is not null) MoveColdGround(area, ground, at, seconds);
                double chillLeft = Math.Min(.2, (ground.End - to) / (double)ProjectileRuntime.QuantaPerSecond);
                bool Covers(SpawnedMonster monster) => DamageGroundTarget(monster)
                    && (Vector(monster.Walk.Position(at)) - ground.CurrentCentre).Length <= ground.Radius;
                if (ground.ChillStats is not null)
                    ground.Chilled.RemoveWhere(monster =>
                    {
                        if (chillLeft > 0 && Covers(monster)) return false;
                        monster.Ailments.RemoveAura(MonsterElementalAilments.Kind.Chill, ground.ChillSource);
                        return true;
                    });
                foreach (var (id, monster) in area.MonstersNear((ground.CurrentCentre.X, ground.CurrentCentre.Y), ground.Radius))
                {
                    if (!Covers(monster)) continue;
                    if (ground.ChillStats is { } chill && chillLeft > 0)
                    {
                        monster.Ailments.ApplyAura(MonsterElementalAilments.Kind.Chill, ground.ChillSource, chillLeft, at,
                            key => chill.GetValueOrDefault(key), key => monster.Effects[key],
                            chill.GetValueOrDefault(Stat.ChilledGroundBaseMagnitudeOverride));
                        ground.Chilled.Add(monster);
                    }
                    if (ground.Dps <= 0) continue;
                    if (!exposures.TryGetValue(id, out var exposure)) exposures.Add(id, exposure = new(monster));
                    exposure.Consider(ground);
                }
            }

            foreach (var (id, exposure) in exposures)
            {
                var monster = exposure.Monster;
                var carry = state.Remainders.GetOrCreateValue(monster);
                int Accrue(DamageGround? ground, ref double fraction)
                {
                    if (ground is null) return 0;
                    double total = fraction + seconds * ground.Dps * MonsterDotTaken(monster, ground.Type, at);
                    double whole = Math.Floor(total);
                    fraction = total - whole;
                    return (int)Math.Clamp(whole, 0, int.MaxValue);
                }
                int fire = Accrue(exposure.Fire, ref carry.Fire);
                int cold = Accrue(exposure.Cold, ref carry.Cold);
                int chaos = Accrue(exposure.Chaos, ref carry.Chaos);
                int damage = ActorScale.Saturate((long)fire + cold + chaos);
                if (damage == 0) continue;
                int lifeDamage = AbsorbMonsterEnergyShieldLocked(area, monster, damage, chaos, at);
                if (lifeDamage <= 0) continue;
                var source = (fire >= cold && fire >= chaos ? exposure.Fire : cold >= chaos ? exposure.Cold : exposure.Chaos)!.Source;
                var player = source.Player;
                var (life, stage) = TakeOwnerDamageLocked(player, monster, lifeDamage);
                monster.Life = life;
                monster.Aggro = life > 0;
                if (stage) CommitProjectileBossStage(source, id, monster, to);
                if (life == 0)
                {
                    SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
                    if (DotDeathKind(monster, life) is var deathKind && deathKind != InstanceProtocol.DeathKindNormal)
                        source.World(id, InstanceProtocol.LifeUpdate(id, life, u2c: deathKind,
                            orderKey: player.Connection.LockstepClient ? uint.MaxValue : 0u));
                    monster.Dead = true;
                    monster.LastHitBy = player;
                    CleanupDeadMonsterLocked(area, id, monster);
                    CollectVaalSoulAtDeathLocked(player, monster);
                    source.Enqueue(() => DotKilled(player, id, monster));
                }
                else if (!player.Connection.LockstepClient)
                    source.World(id, InstanceProtocol.LifeUpdate(id, life));
            }
        }
    }
}
