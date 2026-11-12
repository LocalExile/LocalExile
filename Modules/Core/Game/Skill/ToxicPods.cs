using System.Runtime.CompilerServices;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    // Monsters are shared by the instance. Count the vines of every caster when updating movement.
    sealed class ToxicPod(int slow, int cap)
    {
        public readonly int Slow = slow, Cap = cap;
    }
    static readonly ConditionalWeakTable<SpawnedMonster, HashSet<ToxicPod>> ToxicVines = new();

    internal static double ToxicWalkFactor(SpawnedMonster m)
    {
        if (!ToxicVines.TryGetValue(m, out var pods)) return 1;
        lock (pods)
        {
            int sum = 0, cap = 0;
            foreach (var pod in pods)
            {
                sum += pod.Slow;
                cap = Math.Max(cap, pod.Cap);
            }
            return 1 - Math.Clamp(Math.Min(sum, cap), 0, 99) / 100d;
        }
    }

    public sealed partial class ProjectileSession
    {
        readonly Dictionary<ToxicPod, HashSet<SpawnedMonster>> podVictims = new(ReferenceEqualityComparer.Instance);
        readonly ConditionalWeakTable<SpawnedMonster, PodDamage> dotRemainders = new();
        sealed class PodDamage { public double Fraction; }

        internal void RainArt(ProjectileVector at, string? ao, double duration, double? radius = null)
        {
            if (ao is not null) Effect((int)Math.Round(at.X), (int)Math.Round(at.Y), ao, ProjectileRuntime.Time(duration), radius);
        }

        void LeavePod(ToxicPod pod, SpawnedMonster m)
        {
            if (ToxicVines.TryGetValue(m, out var pods)) lock (pods) pods.Remove(pod);
        }

        void ClearPods()
        {
            foreach (var (pod, victims) in podVictims)
                foreach (var m in victims) LeavePod(pod, m);
            podVictims.Clear();
        }

        internal void PlaceToxicPod(ProjectileUse use, ProjectileVector centre, int radius, double duration,
            double dps, int slow, int cap, string? burst)
        {
            var pod = new ToxicPod(slow, cap);
            var victims = podVictims[pod] = [];
            long previous = Runtime.Now, end = previous + Math.Max(1, ProjectileRuntime.Time(duration));
            void Tick()
            {
                long now = Runtime.Now;
                double at = now / (double)ProjectileRuntime.QuantaPerSecond;
                double seconds = (now - previous) / (double)ProjectileRuntime.QuantaPerSecond;
                previous = now;
                foreach (var (id, monster) in Conn.Monsters.ToArray())
                {
                    bool inside = monster.Hostile && !monster.Dead && monster.Life > 0
                        && (Vector(monster.Walk.Position(at)) - centre).Length <= radius;
                    if (!inside)
                    {
                        if (victims.Remove(monster)) LeavePod(pod, monster);
                        continue;
                    }
                    if (victims.Add(monster))
                    {
                        var vines = ToxicVines.GetValue(monster, _ => new(ReferenceEqualityComparer.Instance));
                        lock (vines) vines.Add(pod);
                    }
                    var carry = dotRemainders.GetValue(monster, _ => new PodDamage());
                    double total = carry.Fraction + seconds * dps * MonsterDotTaken(monster, DamageType.Chaos, at);
                    int damage = (int)Math.Clamp(Math.Floor(total), 0, int.MaxValue);
                    carry.Fraction = total - Math.Floor(total);
                    if (damage == 0) continue;
                    var (life, stage) = TakeOwnerDamageLocked(Conn, monster, damage);
                    monster.Life = life;
                    monster.Aggro = life > 0;
                    if (stage) CommitProjectileBossStage(this, id, monster, now);
                    World(id, InstanceProtocol.LifeUpdate(id, life));
                    if (life == 0)
                    {
                        monster.Dead = true;
                        monster.LastHitBy = Conn;
                        CollectVaalSoulAtDeathLocked(Conn, monster);
                        Enqueue(() => DotKilled(remote, stream, ciphers, Conn, id, monster));
                    }
                }
                if (now < end) Runtime.Schedule(Math.Min(end, now + ProjectileRuntime.QuantaPerSecond / 10), Tick);
                else
                {
                    foreach (var m in victims) LeavePod(pod, m);
                    podVictims.Remove(pod);
                    RainArt(centre, burst, 1, radius);
                    Runtime.Impact(use with { Payload = ((ProjectileOffense)use.Payload!) with { MirageSource = null } }, centre, new("ToxicRainBurst", radius));
                }
            }
            Tick();
        }
    }
}

