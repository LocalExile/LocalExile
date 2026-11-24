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
                : ColdGroundDps(Get, Own, spell: true, shot.FromMine, trap: shot.FromTrapOrMine && !shot.FromMine);
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
