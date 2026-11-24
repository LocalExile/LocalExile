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
        double effect = Math.Max(0, 1 + ((long)context.Get(Stat.EnsnaringArrowDebuffEffectPlusPct)
            + context.Get(Stat.SkillBuffEffectPlusPct)) / 100d);
        int Scale(uint key) => (int)(context.Get(key) * effect);
        int Area(uint key) => key == SkillArea.BaseIncreased
            ? area(key) + context.Get(Stat.EnsnaringArrowAreaOfEffectPlusPct) : area(key);
        return new(Math.Clamp(context.Get(Stat.TetheringArrowDisplayRopeLimit), 1, 3),
            SkillArea.Radius(SnareBaseRadius, Area, spell: false),
            Scale(Stat.TetheredMovementSpeedPlusPctFinalPerRope), Scale(Stat.TetheredMovementSpeedPlusPctFinalPerRopeVsRare),
            Scale(Stat.TetheredMovementSpeedPlusPctFinalPerRopeVsUnique),
            Scale(Stat.TetheredEnemiesTakeAttackProjectileDamageTakenPlusPct));
    }

    sealed class Snare(ProjectileSession session, SpawnedMonster target, uint targetId, ProjectileVector anchor, double radius,
        int slow, int attackTaken, uint anchorId, uint beamId, uint sourceId, ushort buffRow, ushort buffTag)
    {
        public readonly ProjectileSession Session = session;
        public readonly SpawnedMonster Target = target;
        public readonly uint TargetId = targetId;
        public readonly ProjectileVector Anchor = anchor;
        public readonly double Radius = radius;
        public readonly int Slow = slow;
        public readonly int AttackTaken = attackTaken;
        public readonly uint AnchorId = anchorId;
        public readonly uint BeamId = beamId;
        public readonly uint SourceId = sourceId;
        public readonly ushort BuffRow = buffRow;
        public readonly ushort BuffTag = buffTag;
        int ended, removed;
        public bool AnchorCreated, BeamCreated;

        public bool Ended => Volatile.Read(ref ended) != 0;
        public void End() => Interlocked.Exchange(ref ended, 1);
        public bool ClaimRemoval() => Interlocked.Exchange(ref removed, 1) == 0;

        public bool Holds(SpawnedMonster monster, ProjectileVector position) =>
            !Ended && Session.Active && !monster.Dead && monster.Life != 0 && (position - Anchor).Length <= Radius;
    }

    static readonly RuleTable<SpawnedMonster, List<Snare>> EnsnaredEnemies = new();

    public static bool IsEnsnared(SpawnedMonster monster, double now)
    {
        if (!EnsnaredEnemies.TryGetValue(monster, out var snares)) return false;
        var position = ProjectileSession.Vector(monster.Walk.Position(now));
        lock (snares)
        {
            for (int i = 0; i < snares.Count; i++)
                if (snares[i].Holds(monster, position)) return true;
        }
        return false;
    }

    public static double EnsnaredWalkFactor(SpawnedMonster monster, double now)
    {
        if (!EnsnaredEnemies.TryGetValue(monster, out var snares)) return 1;
        double factor = 1;
        var position = ProjectileSession.Vector(monster.Walk.Position(now));
        lock (snares)
        {
            for (int i = 0; i < snares.Count; i++)
                if (snares[i].Holds(monster, position)) factor *= Math.Clamp(1 + snares[i].Slow / 100d, .01, 1);
        }
        return Math.Max(.01, factor);
    }

    public static int EnsnaredProjectileAttackTaken(SpawnedMonster monster, double now)
    {
        if (!EnsnaredEnemies.TryGetValue(monster, out var snares)) return 0;
        int taken = 0;
        var position = ProjectileSession.Vector(monster.Walk.Position(now));
        lock (snares)
        {
            for (int i = 0; i < snares.Count; i++)
                if (snares[i].Holds(monster, position)) taken = Math.Max(taken, snares[i].AttackTaken);
        }
        return taken;
    }

    public sealed partial class ProjectileSession
    {
        readonly HashSet<Snare> activeSnares = new(ReferenceEqualityComparer.Instance);

        void ClearEnsnares()
        {
            Snare[] owned;
            lock (activeSnares)
            {
                if (activeSnares.Count == 0) return;
                owned = [.. activeSnares];
                activeSnares.Clear();
            }
            foreach (var rope in owned)
            {
                rope.End();
                if (EnsnaredEnemies.TryGetValue(rope.Target, out var ropes))
                    lock (ropes) ropes.Remove(rope);
                if (rope.ClaimRemoval() && rope.BuffRow != 0)
                    Player.Area.Buffs.Clear(rope.TargetId, rope.BuffRow, rope.SourceId, tag: rope.BuffTag);
                Player.UntrackProjectileObject(rope.AnchorId);
                Player.UntrackProjectileObject(rope.BeamId);
            }
        }

        void EnsnaringArrowAtEnd(ProjectileEvent value)
        {
            var projectile = value.Projectile;
            if (projectile.Use.Payload is not ProjectileOffense { Ensnare: { } spec }
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
            var rope = new Snare(this, monster, key.Id, new(x, y), spec.Radius, slow, spec.AttackTaken,
                anchorId, beamId, projectile.Use.Emitter.Id, row, tag);
            if (monster.Bleeds.Burning) SettleBleeding(monster, now);
            var ropes = EnsnaredEnemies.GetOrCreateValue(monster);
            List<Snare>? evicted = null;
            lock (ropes)
            {
                for (int i = ropes.Count - 1; i >= 0; i--)
                {
                    if (ropes[i].Holds(monster, centre)) continue;
                    var old = ropes[i];
                    old.End();
                    ropes.RemoveAt(i);
                    (evicted ??= []).Add(old);
                }
                while (ropes.Count >= spec.Limit)
                {
                    var old = ropes[0];
                    old.End();
                    ropes.RemoveAt(0);
                    (evicted ??= []).Add(old);
                }
                ropes.Add(rope);
            }
            lock (activeSnares) activeSnares.Add(rope);
            if (evicted is not null)
                foreach (var old in evicted)
                    // Only queue removal on our own session; another session's Check() cleans up its own ended rope.
                    if (ReferenceEquals(old.Session, this)) RemoveEnsnare(old);

            // The movement update already sends the combined walk modifier. The buff owns the icon; the beam follows
            // the moving target while its other end stays attached to the stationary ground arrow.
            if (row != 0) Player.Area.Buffs.Set(key.Id, BuffRows.Spec(row, rope.SourceId, null, tag: tag));
            Player.TrackProjectileObject(anchorId);
            Player.TrackProjectileObject(beamId);
            Create(anchorId, () =>
            {
                if (rope.Ended) return null;
                rope.AnchorCreated = true;
                return InstanceEntities.CreateObject(anchorId,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(x, y, demonic ? DemonicSnareArrowAo : SnareArrowAo).Blob());
            }, wake: true);
            Create(beamId, () =>
            {
                if (rope.Ended) return null;
                rope.BeamCreated = true;
                return InstanceEntities.CreateObject(beamId,
                    InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath), new BeamEffectObject(x, y, anchorId,
                        (float)centre.X * BeamEffectObject.WorldPerCell, (float)centre.Y * BeamEffectObject.WorldPerCell,
                        key.Id, demonic ? DemonicSnareBeamRow : SnareBeamRow).Blob());
            }, wake: true);

            long interval = ProjectileRuntime.Time(.1);
            Runtime.Schedule(value.At + interval, Check);
            void Check()
            {
                if (rope.Ended)
                {
                    RemoveEnsnare(rope);
                    return;
                }
                double at = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                if (!Player.Area.Monsters.TryGetValue(key.Id, out var current) || !ReferenceEquals(current, monster)
                    || !rope.Holds(monster, Vector(monster.Walk.Position(at))))
                {
                    if (monster.Bleeds.Burning) SettleBleeding(monster, at);
                    lock (ropes) ropes.Remove(rope);
                    RemoveEnsnare(rope);
                    return;
                }
                Runtime.Schedule(Runtime.Now + interval, Check);
            }
        }

        void RemoveEnsnare(Snare rope)
        {
            rope.End();
            if (!rope.ClaimRemoval()) return;
            if (rope.BuffRow != 0) Player.Area.Buffs.Clear(rope.TargetId, rope.BuffRow, rope.SourceId, tag: rope.BuffTag);
            if (!Active)
            {
                lock (activeSnares) activeSnares.Remove(rope);
                Player.UntrackProjectileObject(rope.AnchorId);
                Player.UntrackProjectileObject(rope.BeamId);
                return;
            }
            WorldRecords(rope.BeamId, () =>
            {
                Player.UntrackProjectileObject(rope.BeamId);
                return rope.BeamCreated ? [InstanceProtocol.RemoveObject(rope.BeamId)] : null;
            }, remove: true);
            WorldRecords(rope.AnchorId, () =>
            {
                lock (activeSnares) activeSnares.Remove(rope);
                Player.UntrackProjectileObject(rope.AnchorId);
                return rope.AnchorCreated ? [InstanceProtocol.RemoveObject(rope.AnchorId)] : null;
            }, remove: true);
        }
    }
}
