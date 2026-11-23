using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string SnareArrowAo = "Metadata/Effects/Spells/ensnaring_shot/arrow_tether.ao";
    const string DemonicSnareArrowAo = "Metadata/Effects/Microtransactions/Spells/ensnaring_shot/demonic/Ao/hand.ao";
    const string DemonicSnareProjectile = "Metadata/Projectiles/TetheringArrowDemonic";
    const ushort SnareBeamRow = 229, DemonicSnareBeamRow = 230;
    // MiscBeams rows 229 and 230 use a 300-world-unit tether.
    const int SnareBaseRadius = 28;

    static bool IsEnsnaringArrow(string? effect) =>
        effect is "EnsnaringArrow" or "EnsnaringArrowAltX" or "EnsnaringArrowAltY" or "EnsnaringArrowRoyale";

    public sealed record EnsnareSpec(int Limit, int Radius, int NormalSlow, int RareSlow, int UniqueSlow, int AttackTaken);

    static EnsnareSpec CaptureEnsnare(ProjectileReleaseContext context, Func<uint, int> area)
    {
        double effect = Math.Max(0, 1 + context.Get(Stat.EnsnaringArrowDebuffEffectPlusPct) / 100d);
        int Scale(uint key) => (int)(context.Get(key) * effect);
        return new(Math.Clamp(context.Get(Stat.TetheringArrowDisplayRopeLimit), 1, 3),
            SkillArea.Radius(SnareBaseRadius, area, spell: false),
            Scale(Stat.TetheredMovementSpeedPlusPctFinalPerRope), Scale(Stat.TetheredMovementSpeedPlusPctFinalPerRopeVsRare),
            Scale(Stat.TetheredMovementSpeedPlusPctFinalPerRopeVsUnique),
            Scale(Stat.TetheredEnemiesTakeAttackProjectileDamageTakenPlusPct));
    }

    sealed record Snare(ProjectileSession Session, ProjectileVector Anchor, double Radius, int Slow, int AttackTaken,
        uint AnchorId, uint BeamId, uint SourceId, ushort BuffRow, ushort BuffTag)
    {
        public bool Holds(SpawnedMonster monster, ProjectileVector position) =>
            Session.Active && !monster.Dead && monster.Life != 0 && (position - Anchor).Length <= Radius;
    }

    static readonly RuleTable<SpawnedMonster, List<Snare>> EnsnaredEnemies = new();

    public static double EnsnaredWalkFactor(SpawnedMonster monster, double now)
    {
        if (!EnsnaredEnemies.TryGetValue(monster, out var snares)) return 1;
        double factor = 1;
        var position = ProjectileSession.Vector(monster.Walk.Position(now));
        foreach (var rope in snares)
            if (rope.Holds(monster, position)) factor *= Math.Clamp(1 + rope.Slow / 100d, .01, 1);
        return Math.Max(.01, factor);
    }

    public static int EnsnaredProjectileAttackTaken(SpawnedMonster monster, double now)
    {
        if (!EnsnaredEnemies.TryGetValue(monster, out var snares)) return 0;
        int taken = 0;
        var position = ProjectileSession.Vector(monster.Walk.Position(now));
        foreach (var rope in snares)
            if (rope.Holds(monster, position)) taken = Math.Max(taken, rope.AttackTaken);
        return taken;
    }

    public sealed partial class ProjectileSession
    {
        void EnsnaringArrowAtEnd(ProjectileEvent value)
        {
            var projectile = value.Projectile;
            if (projectile.Use.Payload is not ProjectileOffense { Ensnare: { } spec } shot
                || value.HitTarget is not { } key || !Player.Area.Monsters.TryGetValue(key.Id, out var monster)
                || Key(key.Id, monster) != key || monster.Dead || monster.Life == 0 || monster.Invulnerable) return;

            double now = value.At / (double)ProjectileRuntime.QuantaPerSecond;
            var centre = Vector(monster.Walk.Position(now));
            // Place the anchor on the far side of the victim, stopping short of intervening terrain.
            var anchor = centre + projectile.Direction * Math.Min(8, spec.Radius * .5);
            if (TraceTerrain(centre, anchor) is { } wall)
                anchor = centre + (anchor - centre) * Math.Max(0, wall.Fraction - .05);
            int x = (int)Math.Round(anchor.X), y = (int)Math.Round(anchor.Y);
            ushort row = BuffRows.Find("tethered_debuff");
            ushort tag = Player.NextBuff++;
            bool demonic = projectile.Profile.Id == DemonicSnareProjectile;
            int slow = monster.Rarity switch
            {
                MonsterRarity.Unique => spec.UniqueSlow,
                MonsterRarity.Rare => spec.RareSlow,
                _ => spec.NormalSlow
            };
            uint anchorId = Player.TakeObjectId(), beamId = Player.TakeObjectId();
            var rope = new Snare(this, new(x, y), spec.Radius, slow, spec.AttackTaken, anchorId, beamId, projectile.Use.Emitter.Id, row, tag);
            var ropes = EnsnaredEnemies.GetOrCreateValue(monster);
            foreach (var old in ropes.ToArray())
                if (!old.Holds(monster, centre)) Remove(old);
            while (ropes.Count >= spec.Limit) Remove(ropes[0]);
            EnsnaredEnemies.AddOrUpdate(monster, ropes);
            ropes.Add(rope);

            // The movement update already sends the combined walk modifier. The buff owns the icon; the beam follows
            // the moving target while its other end stays attached to the stationary ground arrow.
            if (row != 0) Player.Area.Buffs.Set(key.Id, BuffRows.Spec(row, rope.SourceId, null, tag: tag));
            Player.ProjectileObjects.Add(anchorId);
            Player.ProjectileObjects.Add(beamId);
            Create(anchorId, () => ropes.Contains(rope) ? InstanceEntities.CreateObject(anchorId,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(x, y, demonic ? DemonicSnareArrowAo : SnareArrowAo).Blob()) : null, wake: true);
            Create(beamId, () => ropes.Contains(rope) ? InstanceEntities.CreateObject(beamId,
                InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath), new BeamEffectObject(x, y, anchorId,
                    (float)centre.X * BeamEffectObject.WorldPerCell, (float)centre.Y * BeamEffectObject.WorldPerCell,
                    key.Id, demonic ? DemonicSnareBeamRow : SnareBeamRow).Blob()) : null, wake: true);

            long interval = ProjectileRuntime.Time(.1);
            Runtime.Schedule(value.At + interval, Check);
            void Check()
            {
                if (!ropes.Contains(rope)) return;
                double at = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                if (!Player.Area.Monsters.TryGetValue(key.Id, out var current) || !ReferenceEquals(current, monster)
                    || !rope.Holds(monster, Vector(monster.Walk.Position(at))))
                {
                    Remove(rope);
                    return;
                }
                Runtime.Schedule(Runtime.Now + interval, Check);
            }

            void Remove(Snare old)
            {
                ropes.Remove(old);
                old.Session.RemoveEnsnare(key.Id, old);
                if (ropes.Count == 0) EnsnaredEnemies.Remove(monster);
            }
        }

        void RemoveEnsnare(uint target, Snare rope)
        {
            if (!Active) return;
            if (rope.BuffRow != 0) Player.Area.Buffs.Clear(target, rope.BuffRow, rope.SourceId, tag: rope.BuffTag);
            foreach (uint id in new[] { rope.BeamId, rope.AnchorId })
            {
                Player.ProjectileObjects.Remove(id);
                WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
            }
        }
    }
}
