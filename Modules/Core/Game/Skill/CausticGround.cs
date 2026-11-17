using System.Runtime.CompilerServices;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    // The stretch of time a monster has already taken caustic ground damage for, and the fraction carried to its next tick.
    sealed class CausticCover
    {
        public long Until;
        public double Fraction;
    }

    public sealed partial class ProjectileSession
    {
        // GroundEffects' caustic arrow row (CausticCloud, variant 2): five pieces of art of 120 world units.
        static readonly string[] CausticArrowGround = [.. Enumerable.Range(1, 5).Select(i =>
            $"Metadata/Effects/Spells/ground_effects_v2/caustic_arrow_ground/caustic_arrow_0{i}.ao")];
        const int CausticArrowGroundArtRadius = 120;

        readonly ConditionalWeakTable<SpawnedMonster, CausticCover> causticCover = new();

        /// <summary>Caustic Arrow leaves caustic ground where its arrow stops: on an enemy, on terrain or at the end of its
        /// flight (base_skill_effect_duration, 2 s; base_chaos_damage_to_deal_per_minute). The radius is 20 cells (PoE
        /// Wiki "Caustic Arrow") plus the gem's active_skill_base_radius_+. A skill without that damage (Caustic Arrow of
        /// Poison) leaves none. The art is one piece of the GroundEffects row scaled to the radius: the client's own
        /// ground effect, which lays several pieces, is not sent.</summary>
        void CausticGroundAtEnd(ProjectileEvent value)
        {
            var p = value.Projectile;
            if (value.Kind != ProjectileEventKind.End || p.Use.Payload is not ProjectileOffense shot
                || value.Reason is not (ProjectileEndReason.Enemy or ProjectileEndReason.Terrain
                    or ProjectileEndReason.Distance or ProjectileEndReason.Lifetime or ProjectileEndReason.Landing)
                || SkillsFor(Conn).EffectId(shot.Effect)?.StartsWith("CausticArrow", StringComparison.Ordinal) != true) return;
            var own = shot.SkillStats;
            var player = CharacterPlayerStats(Conn);
            int Own(string key) => Stats.Key(key) is uint stat ? own.GetValueOrDefault(stat) : 0;
            int Player(string key) => Stats.Key(key) is uint stat ? player.GetValueOrDefault(stat) : 0;
            int Get(string key) => Own(key) + Player(key);
            if (Own("base_chaos_damage_to_deal_per_minute") <= 0) return;
            double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
            if (duration <= 0) return;
            double dps = new SkillDamageContext(SkillHitKind.Attack, shot.ActualCaster ?? player).Suppressed
                ? 0 : ChaosGroundDps(Get, Own, null);
            PlaceCausticGround(value.Position, SkillArea.Radius(20, Get, false), duration, dps,
                CausticArrowGround[Random.Shared.Next(CausticArrowGround.Length)]);
        }

        /// <summary>A caustic ground: chaos damage over time to the hostile monsters standing on it. Caustic grounds do
        /// not stack, so a monster on several takes each stretch of time once (<see cref="CausticCover"/>).</summary>
        internal void PlaceCausticGround(ProjectileVector centre, double radius, double duration, double dps, string? ao)
        {
            if (ao is not null)
                Effect((int)Math.Round(centre.X), (int)Math.Round(centre.Y), ao, ProjectileRuntime.Time(duration), radius,
                    artRadius: CausticArrowGroundArtRadius);
            long previous = Runtime.Now, end = previous + Math.Max(1, ProjectileRuntime.Time(duration));
            void Tick()
            {
                long now = Runtime.Now;
                double at = now / (double)ProjectileRuntime.QuantaPerSecond;
                foreach (var (id, monster) in Conn.Monsters.ToArray())
                {
                    if (!monster.Hostile || monster.Dead || monster.Life == 0
                        || (Vector(monster.Walk.Position(at)) - centre).Length > radius) continue;
                    var cover = causticCover.GetValue(monster, _ => new CausticCover());
                    long from = Math.Max(previous, cover.Until);
                    if (now <= from) continue;
                    cover.Until = now;
                    double total = cover.Fraction + (now - from) / (double)ProjectileRuntime.QuantaPerSecond * dps
                        * MonsterDotTaken(monster, DamageType.Chaos, at);
                    int damage = (int)Math.Clamp(Math.Floor(total), 0, int.MaxValue);
                    cover.Fraction = total - Math.Floor(total);
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
                previous = now;
                if (now < end) Runtime.Schedule(Math.Min(end, now + ProjectileRuntime.QuantaPerSecond / 10), Tick);
            }
            Tick();
        }
    }
}
