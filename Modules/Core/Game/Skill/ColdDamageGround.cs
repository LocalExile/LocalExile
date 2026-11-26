using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static bool IsCreepingFrost(string? effect) =>
        effect is "CreepingFrost" or "CreepingFrostAltX" or "CreepingFrostAltY" or "CreepingFrostRoyale";

    static readonly uint[] ColdGroundMore =
    [
        Stat.SupportAreaConcentrateAreaDamagePlusPctFinal, Stat.SupportControlledDestructionSpellDamagePlusPctFinal,
        Stat.SupportEfficacySpellDamagePlusPctFinal, Stat.SupportEfficacyDamageOverTimePlusPctFinal,
        Stat.SupportRapidDecayDamageOverTimePlusPctFinal, Stat.SupportHypothermiaColdDamageOverTimePlusPctFinal,
        Stat.CrueltyDamageOverTimePlusPctFinal, Stat.SupportTrapAndMineDamagePlusPctFinal,
        Stat.SupportTrapDamagePlusPctFinal, Stat.SupportRemoteMine2DamagePlusPctFinal,
    ];

    static double ColdGroundDps(Func<uint, int> get, Func<uint, int> own, bool spell, bool mine, bool trap)
    {
        if (DamageStats.Prohibited(DamageType.Cold, get)) return 0;
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ColdDamagePlusPct)
            + get(Stat.ElementalDamagePlusPct) + get(Stat.AreaDamagePlusPct)
            + get(Stat.DamageOverTimePlusPct) + get(Stat.ColdDamageOverTimePlusPct);
        if (spell && own(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += get(Stat.SpellDamagePlusPct);
        if (mine || trap)
            increased += (double)get(mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct) + get(Stat.TrapOrMineDamagePlusPct);
        double more = ColdGroundMore.Aggregate(1d, (value, stat) => value * Math.Max(0, 1 + own(stat) / 100d));
        double multiplier = Math.Max(0, 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ColdDotMultiplierPlus)) / 100d);
        return Math.Max(0, own(Stat.BaseColdDamageToDealPerMinute) / 60d) * Math.Max(0, 1 + increased / 100) * more * multiplier;
    }

    public sealed partial class ProjectileSession
    {
        sealed record GroundMovement(int Effect, double CellsPerSecond, int Limit);

        void CreepingFrostAtEnd(ProjectileEvent value)
        {
            if (value.Projectile.Use.Payload is not ProjectileOffense shot || !IsCreepingFrost(SkillsFor(Player).EffectId(shot.Effect))
                || value.Reason is not (ProjectileEndReason.Enemy or ProjectileEndReason.Terrain
                    or ProjectileEndReason.Distance or ProjectileEndReason.Lifetime)) return;
            var caster = shot.ActualCaster ?? CharacterPlayerStats(Player);
            var own = shot.SkillStats;
            var stats = SkillAreaStats(Player, own, shot.Effect, caster);
            int Get(uint stat) => stats.GetValueOrDefault(stat);
            int Own(uint stat) => own.GetValueOrDefault(stat);
            int Source(uint stat) => caster.GetValueOrDefault(stat);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Source,
                stat => stat == Stat.SkillEffectDurationPlusPct ? Own(stat) + Get(Stat.ArcticBreathDurationPlusPct) : Own(stat));
            int secondary = Own(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius);
            int radius = SkillArea.Radius(secondary > 4 ? secondary : 18 + secondary, Get, spell: true);
            double dps = new SkillDamageContext(SkillHitKind.Spell, caster).Suppressed ? 0
                : ColdGroundDps(Get, Own, spell: true, shot.FromMine, trap: shot.FromTrapOrMine && !shot.FromMine) * shot.GhorrDotMore;
            // Local starting speed for the chilling ground. The Floes gem's native movement modifier applies to it.
            double pace = 10 * Math.Max(0, 1 + Get(Stat.ArcticBreathChillingAreaMovementVelocityPlusPct) / 100d);
            int limit = Own(Stat.ArcticBreathMaximumNumberOfSkullsAllowed);
            PlaceCreepingGround(value.Position, radius, duration, dps, stats, new(shot.Effect, pace, limit));
        }

        internal void PlaceVortexGround(PlacedSkills.Placed placed, IReadOnlyDictionary<uint, int> caster, double damageMore)
        {
            var area = SkillAreaStats(Player, placed.Own, placed.Effect, caster);
            int Own(uint key) => placed.Own.GetValueOrDefault(key);
            int Get(uint key) => area.GetValueOrDefault(key);
            double dps = ColdGroundDps(Get, Own, spell: true, placed.Mine, trap: !placed.Mine) * damageMore;
            int radius = SkillArea.Radius(Math.Max(1, Own(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius)), Get, spell: true);
            double duration = placed.EffectDurationSeconds;
            PlaceDamageGround(Vector(placed.At), radius, duration, DamageType.Cold, dps, Runtime.Now, chillStats: area);
        }

        internal void ReleaseVortexProjection((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
            (int X, int Y) origin, (int X, int Y) aim, IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> caster,
            SkillCriticalRoll? critical, SacrificeUse sacrifice, Func<bool> current, ushort sequence, int repeat,
            double damageMore, SpawnedMonster? brandedTarget, double dotMore)
        {
            var area = SkillAreaStats(Player, own, gem.Effect, caster);
            int Get(uint key) => area.GetValueOrDefault(key);
            int Own(uint key) => own.GetValueOrDefault(key);
            int Caster(uint key) => caster.GetValueOrDefault(key);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Caster, Own);
            double dps = new SkillDamageContext(SkillHitKind.Spell, caster).OverTime(
                new(DamageType.Cold, ColdGroundDps(Get, Own, spell: true, mine: false, trap: false), duration)).DamagePerSecond;
            int radius = SkillRadius(Player, own, spell: true, gem.Effect);
            int groundRadius = SkillArea.Radius(Own(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), Get, spell: true);
            int maximum = Own(Stat.FrostBoltNovaNumberOfFrostBoltsToDetonate);
            var bolts = Runtime.Projectiles.Where(p => p.Use.Owner.Id == Player.Id
                    && p.Use.Payload is ProjectileOffense { ActualCaster: null, FromTotem: false, FromTrapOrMine: false } shot
                    && SkillsFor(Player).EffectId(shot.Effect) is "FrostBolt" or "FrostBoltAltX" or "FrostBoltAltY" or "FrostBoltRoyale")
                .Select(p => (p.Id, At: ((int)Math.Round(p.Position.X), (int)Math.Round(p.Position.Y))))
                .Where(p => Dist(aim, p.At) <= Math.Max(60, radius + 30))
                .OrderBy(p => Dist(aim, p.At)).ThenBy(p => p.Id).Take(maximum).ToArray();
            if (bolts.Length == 0) { At(origin, 1, 1); return; }
            double radiusMore = SkillArea.RadiusMultiplier(Own(Stat.ActiveSkillAreaOfEffectPlusPctFinalWhenCastOnFrostbolt));
            double boltMore = Math.Max(0, 1 + Own(Stat.ActiveSkillIfUsedThroughFrostboltDamagePlusPctFinal) / 100d);
            foreach (var bolt in bolts)
                if (Runtime.Consume(bolt.Id)) At(bolt.At, radiusMore, boltMore);

            void At((int X, int Y) centre, double areaMore, double more)
            {
                int hitRadius = (int)Math.Floor(radius * areaMore);
                int dotRadius = (int)Math.Floor(groundRadius * areaMore);
                Enqueue(() => AreaSkillHitForUse(null, Player, gem.Id, gem, centre, centre, immediate: true, sequence: sequence,
                    repeat: repeat, damageMore: damageMore * more, radiusOverride: hitRadius, criticalRoll: critical,
                    sacrifice: sacrifice, triggeredCurrent: current, brandedTarget: brandedTarget));
                PlaceDamageGround(Vector(centre), dotRadius, duration, DamageType.Cold, dps * damageMore * more * dotMore,
                    Runtime.Now, chillStats: area);
            }
        }

        void PlaceCreepingGround(ProjectileVector centre, int radius, double duration, double dps,
            IReadOnlyDictionary<uint, int> chill, GroundMovement movement)
        {
            if (duration <= 0 || radius <= 0) return;
            uint id = Player.TakeObjectId();
            var visual = new Visual(id, default);
            Player.TrackProjectileObject(id);
            var effect = new EffectObject((int)Math.Round(centre.X), (int)Math.Round(centre.Y),
                "Metadata/Effects/Spells/arctic_breath_02/aura/arctic_aura.ao")
                { Flags0 = EffectObject.FixedRotation, Scale = radius / 23f };
            Create(id, () =>
            {
                if (visual.Ended) return null;
                visual.Created = true;
                return InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob());
            }, wake: true);
            AddDamageGround(centre, radius, duration, DamageType.Cold, dps, Runtime.Now, visual, chill, movement);
        }

        static void MoveColdGround(GameArea area, DamageGround ground, double at, double seconds)
        {
            if (ground.Movement is not { CellsPerSecond: > 0 } move) return;
            var centre = ground.CurrentCentre;
            const double search = 45;
            var aim = area.MonstersNear((centre.X, centre.Y), search).Where(p => DamageGroundTarget(p.Value)
                    && p.Value is { OwnerId: null, Asleep: false })
                .Select(p => (Point: Vector(p.Value.Walk.Position(at)), p.Key))
                .Where(p => (p.Point - centre).Length <= search)
                .OrderBy(p => (p.Point - centre).Length).ThenBy(p => p.Key).FirstOrDefault();
            if (aim.Key == 0) return;
            var ray = aim.Point - centre;
            if (ray.Length < .01) return;
            var next = centre + ray.Unit * Math.Min(ray.Length, move.CellsPerSecond * seconds);
            int x = (int)Math.Round(next.X), y = (int)Math.Round(next.Y);
            var player = ground.Source.Player;
            if (!player.Layout.WalkableFor(x, y, 0) || player.Barrier.Contains((x, y))) return;
            ground.CurrentCentre = next;
            if (ground.Visual is { } visual && (x != (int)Math.Round(centre.X) || y != (int)Math.Round(centre.Y)))
                ground.Source.World(visual.Id, InstanceProtocol.MoveTo(visual.Id, x, y, (float)seconds, InstanceProtocol.FollowTerrain));
        }
    }
}
