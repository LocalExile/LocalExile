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
        sealed record DamageGround(ProjectileVector Centre, double Radius, long Start, long End,
            DamageType Type, double Dps, Visual? Visual, IReadOnlyDictionary<uint, int>? ChillStats);

        readonly List<DamageGround> damageGrounds = [];
        sealed class GroundDamageCarry { public double Fire, Cold, Chaos; }

        readonly ConditionalWeakTable<SpawnedMonster, GroundDamageCarry> groundRemainders = new();
        bool damageGroundTickScheduled;
        long damageGroundPreviousTick;

        void ClearDamageGrounds()
        {
            foreach (var ground in damageGrounds)
                if (ground.Visual is { } visual)
                {
                    visual.Ended = true;
                    Player.ProjectileObjects.Remove(visual.Id);
                }
            damageGrounds.Clear();
            groundRemainders.Clear();
            damageGroundTickScheduled = false;
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
            double dps, long start, Visual? visual = null, IReadOnlyDictionary<uint, int>? chillStats = null)
        {
            start = Math.Max(Runtime.Now, start);
            long end = start + Math.Max(1, ProjectileRuntime.Time(seconds));
            damageGrounds.Add(new(centre, radius, start, end, type, dps, visual, chillStats));
            if (damageGroundTickScheduled) return;
            damageGroundTickScheduled = true;
            damageGroundPreviousTick = start;
            Runtime.Schedule(Math.Min(end, start + ProjectileRuntime.QuantaPerSecond / 10), TickDamageGrounds);
        }

        void TickDamageGrounds()
        {
            if (damageGrounds.Count == 0) { damageGroundTickScheduled = false; return; }
            long now = Runtime.Now;
            long previous = damageGroundPreviousTick;
            var boundaries = new SortedSet<long> { now };
            foreach (var ground in damageGrounds)
            {
                if (previous < ground.Start && ground.Start < now) boundaries.Add(ground.Start);
                if (previous < ground.End && ground.End < now) boundaries.Add(ground.End);
            }
            var monsters = Player.Area.Monsters.ToArray();
            foreach (long through in boundaries)
            {
                if (through > previous) ApplyDamageGrounds(monsters, previous, through);
                previous = through;
            }
            damageGroundPreviousTick = now;

            for (int i = damageGrounds.Count - 1; i >= 0; i--)
            {
                var ground = damageGrounds[i];
                if (now < ground.End) continue;
                damageGrounds.RemoveAt(i);
                if (ground.Visual is not { } visual) continue;
                visual.Ended = true;
                WorldRecords(visual.Id, () =>
                {
                    Player.ProjectileObjects.Remove(visual.Id);
                    return visual.Created ? [InstanceProtocol.RemoveObject(visual.Id)] : null;
                }, remove: true);
            }
            if (damageGrounds.Count == 0)
            {
                groundRemainders.Clear();
                damageGroundTickScheduled = false;
                return;
            }
            Runtime.Schedule(Math.Min(damageGrounds.Min(ground => ground.End), now + ProjectileRuntime.QuantaPerSecond / 10),
                TickDamageGrounds);
        }

        void ApplyDamageGrounds(KeyValuePair<uint, SpawnedMonster>[] monsters, long from, long to)
        {
            double at = to / (double)ProjectileRuntime.QuantaPerSecond;
            double seconds = (to - from) / (double)ProjectileRuntime.QuantaPerSecond;
            int Accrue(double dps, ref double fraction)
            {
                double total = fraction + seconds * dps;
                double whole = Math.Floor(total);
                fraction = total - whole;
                return (int)Math.Clamp(whole, 0, int.MaxValue);
            }
            foreach (var (id, monster) in monsters)
            {
                if (!monster.Hostile || monster.Dead || monster.Life == 0) continue;
                var pos = Vector(monster.Walk.Position(at));
                double fire = 0, cold = 0, chaos = 0;
                foreach (var ground in damageGrounds)
                {
                    if (from < ground.Start || from >= ground.End) continue;
                    bool covered = (pos - ground.Centre).Length <= ground.Radius;
                    if (ground.Visual is { } visual && ground.ChillStats is { } chill)
                    {
                        double left = (ground.End - to) / (double)ProjectileRuntime.QuantaPerSecond;
                        if (covered && left > 0)
                            monster.Ailments.ApplyAura(MonsterElementalAilments.Kind.Chill, visual.Id, Math.Min(.1, left), at,
                                key => chill.GetValueOrDefault(key), key => monster.Effects[key],
                                chill.GetValueOrDefault(Stat.ChilledGroundBaseMagnitudeOverride));
                        else monster.Ailments.RemoveAura(visual.Id);
                    }
                    if (!covered) continue;
                    if (ground.Type == DamageType.Fire) fire = Math.Max(fire, ground.Dps);
                    else if (ground.Type == DamageType.Cold) cold = Math.Max(cold, ground.Dps);
                    else chaos = Math.Max(chaos, ground.Dps);
                }
                if (fire == 0 && cold == 0 && chaos == 0) continue;
                var carry = groundRemainders.GetValue(monster, _ => new GroundDamageCarry());
                int fireDamage = fire > 0 ? Accrue(fire * MonsterDotTaken(monster, DamageType.Fire, at), ref carry.Fire) : 0;
                int coldDamage = cold > 0 ? Accrue(cold * MonsterDotTaken(monster, DamageType.Cold, at), ref carry.Cold) : 0;
                int chaosDamage = chaos > 0 ? Accrue(chaos * MonsterDotTaken(monster, DamageType.Chaos, at), ref carry.Chaos) : 0;
                int damage = ActorScale.Saturate((long)fireDamage + coldDamage + chaosDamage);
                if (damage == 0) continue;
                int lifeDamage = AbsorbMonsterEnergyShieldLocked(Player.Area, monster, damage, chaosDamage, at);
                if (lifeDamage <= 0) continue;
                var (life, stage) = TakeOwnerDamageLocked(Player, monster, lifeDamage);
                monster.Life = life;
                monster.Aggro = life > 0;
                if (stage) CommitProjectileBossStage(this, id, monster, to);
                if (life == 0)
                {
                    SetStats(Player, id, [(Stat.HideMiniLifeBar, 1)]);
                    if (DotDeathKind(monster, life) is var deathKind && deathKind != InstanceProtocol.DeathKindNormal)
                        World(id, InstanceProtocol.LifeUpdate(id, life, u2c: deathKind,
                            orderKey: Player.Connection.LockstepClient ? uint.MaxValue : 0u));
                    monster.Dead = true;
                    monster.LastHitBy = Player;
                    CollectVaalSoulAtDeathLocked(Player, monster);
                    Enqueue(() => DotKilled(Player, id, monster));
                }
                else if (!Player.Connection.LockstepClient)
                    World(id, InstanceProtocol.LifeUpdate(id, life));
            }
        }
    }
}
