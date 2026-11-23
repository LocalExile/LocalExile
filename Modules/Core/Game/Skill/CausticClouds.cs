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
        sealed class CausticCloud(uint id, Visual visual, ProjectileVector centre, int radius, long end, double dps)
        {
            public readonly uint Id = id;
            public readonly Visual Visual = visual;
            public readonly ProjectileVector Centre = centre;
            public readonly int Radius = radius;
            public readonly long End = end;
            public readonly double Dps = dps;
        }

        readonly List<CausticCloud> causticClouds = [];
        readonly ConditionalWeakTable<SpawnedMonster, PodDamage> causticRemainders = new();
        bool causticTickScheduled;
        long causticPreviousTick;

        void ClearCausticClouds()
        {
            foreach (var cloud in causticClouds)
            {
                cloud.Visual.Ended = true;
                Player.ProjectileObjects.Remove(cloud.Id);
            }
            causticClouds.Clear();
            causticTickScheduled = false;
        }

        internal void PlaceCausticCloud(ProjectileVector centre, ProjectileOffense.CausticCloudSpec spec)
        {
            if (spec.Radius <= 0 || spec.Duration <= 0 || spec.Dps <= 0) return;
            var kind = GroundKindOf("CausticCloud", spec.ArtVariation);
            if (kind is null || kind.Art.Length == 0) return;
            int x = (int)Math.Round(centre.X), y = (int)Math.Round(centre.Y);
            uint id = Player.TakeObjectId();
            var visual = new Visual(id, default);
            Player.ProjectileObjects.Add(id);
            float scale = kind.ArtRadius > 0 ? (float)(spec.Radius * WorldPerCell / kind.ArtRadius) : 1;
            var ground = new GroundEffectObject(x, y, kind.ArtRow, kind.Art, (float)spec.Duration) { Scale = scale, Team = 0 };
            Create(id, () =>
            {
                if (visual.Ended) return null;
                visual.Created = true;
                return InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(GroundEffectObject.TypePath), ground.Blob());
            }, wake: true);
            long now = Runtime.Now;
            long end = now + Math.Max(1, ProjectileRuntime.Time(spec.Duration));
            causticClouds.Add(new(id, visual, centre, spec.Radius, end, spec.Dps));
            if (!causticTickScheduled)
            {
                causticTickScheduled = true;
                causticPreviousTick = now;
                Runtime.Schedule(Math.Min(end, now + ProjectileRuntime.QuantaPerSecond / 10), TickCausticClouds);
            }
        }

        void TickCausticClouds()
        {
            causticTickScheduled = false;
            if (causticClouds.Count == 0) return;
            long now = Runtime.Now;
            double at = now / (double)ProjectileRuntime.QuantaPerSecond;
            double seconds = (now - causticPreviousTick) / (double)ProjectileRuntime.QuantaPerSecond;
            causticPreviousTick = now;

            // Remove expired clouds first.
            for (int i = causticClouds.Count - 1; i >= 0; i--)
            {
                var cloud = causticClouds[i];
                if (now < cloud.End) continue;
                causticClouds.RemoveAt(i);
                cloud.Visual.Ended = true;
                WorldRecords(cloud.Id, () =>
                {
                    Player.ProjectileObjects.Remove(cloud.Id);
                    return cloud.Visual.Created ? [InstanceProtocol.RemoveObject(cloud.Id)] : null;
                }, remove: true);
            }

            if (causticClouds.Count == 0) return;

            // Caustic Ground does not stack: each enemy takes damage only from the strongest cloud it stands in.
            if (seconds > 0)
            {
                foreach (var (id, monster) in Player.Area.Monsters.ToArray())
                {
                    if (!monster.Hostile || monster.Dead || monster.Life <= 0) continue;
                    var pos = Vector(monster.Walk.Position(at));
                    double bestDps = 0;
                    for (int i = 0; i < causticClouds.Count; i++)
                    {
                        var cloud = causticClouds[i];
                        if (cloud.Dps > bestDps && (pos - cloud.Centre).Length <= cloud.Radius)
                            bestDps = cloud.Dps;
                    }
                    if (bestDps <= 0) continue;
                    var carry = causticRemainders.GetValue(monster, _ => new PodDamage());
                    double total = carry.Fraction + seconds * bestDps * MonsterDotTaken(monster, DamageType.Chaos, at);
                    int damage = (int)Math.Clamp(Math.Floor(total), 0, int.MaxValue);
                    carry.Fraction = total - Math.Floor(total);
                    if (damage == 0) continue;
                    var (life, stage) = TakeOwnerDamageLocked(Player, monster, damage);
                    monster.Life = life;
                    monster.Aggro = life > 0;
                    if (stage) CommitProjectileBossStage(this, id, monster, now);
                    if (life == 0)
                    {
                        SetStats(Player, id, [(Stat.HideMiniLifeBar, 1)]);
                        monster.Dead = true;
                        monster.LastHitBy = Player;
                        CollectVaalSoulAtDeathLocked(Player, monster);
                        Enqueue(() => DotKilled(Player, id, monster));
                    }
                }
            }

            causticTickScheduled = true;
            Runtime.Schedule(now + ProjectileRuntime.QuantaPerSecond / 10, TickCausticClouds);
        }
    }
}
