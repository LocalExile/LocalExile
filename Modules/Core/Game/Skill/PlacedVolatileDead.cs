using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        const string VolatileOrbArt = "Metadata/Effects/Spells/volatile_dead/fire_orb_playerskill.ao";
        const string VolatileBlastArt = "Metadata/Effects/Spells/volatile_dead/second_explode_playerskill.ao";
        const int VolatileOrbSearch = 90, VolatileOrbContact = 5;
        const double VolatileOrbStep = .1;
        readonly LinkedList<uint> placedVolatileOrbs = new();

        internal void SchedulePlacedVolatileDead(PlacedAreaHit placement, Func<bool> current)
        {
            var device = placement.Device;
            Runtime.Schedule(Runtime.Now, () =>
            {
                if (Active && current())
                    ReleaseVolatileDead(device.Skill, device.At, ProjectileWorldTime(Player), current,
                        (at, radius, now) => placement.Apply(Player, at, radius, now), placement);
            });
        }

        void ReleaseVolatileDead(ushort skill, (int X, int Y) aim, double now, Func<bool> current,
            Action<(int X, int Y), int, double> hit, PlacedAreaHit? placement = null, double more = 1)
        {
            var supported = SupportedSkill(Player, skill);
            var caster = CharacterPlayerStats(Player);
            var own = placement?.Device.Own ?? supported.Stats;
            int Own(uint key) => own.GetValueOrDefault(key);
            int Source(uint key) => caster.GetValueOrDefault(key);
            int corpses = Math.Clamp(Own(Stat.VolatileDeadBaseNumberOfCorpsesToConsume)
                + Own(Stat.VolatileDeadConsumeAdditionalCorpse) + Source(Stat.VolatileDeadConsumeAdditionalCorpse), 0, 60);
            int limit = Own(Stat.VolatileDeadMaxCoresAllowed);
            if (corpses == 0 || limit <= 0) return;
            var radiusStats = SkillAreaStats(Player, own, supported.Effect, caster);
            int corpseRadius = SkillArea.Radius(Own(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius),
                radiusStats.GetValueOrDefault, spell: true);
            int orbRadius = SkillArea.Radius(Own(Stat.ActiveSkillBaseAreaOfEffectRadius)
                + Own(Stat.VolatileDeadCoreExplosionRadiusPlus), radiusStats.GetValueOrDefault, spell: true);
            double speed = 35 * Math.Max(0, 1 + (Own(Stat.VolatileDeadCoreMovementSpeedPlusPct)
                + Source(Stat.VolatileDeadCoreMovementSpeedPlusPct)) / 100d);
            double? fuse = SkillsFor(Player).EffectId(supported.Effect) == "VolatileDeadAltX"
                ? SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Source, Own) : null;
            foreach (var (id, corpse) in PlacedSpellCorpses(aim, PlacedCorpseSearchRadius, now).Take(corpses).ToArray())
            {
                var at = corpse.Walk.Position(now);
                ConsumeSpellCorpse(id, corpse, now, playerConsumes: placement is null);
                Effect(at.X, at.Y, DetonateDeadArt, ProjectileRuntime.Time(.5), corpseRadius);
                ApplyCorpseExplosion(skill, corpse.MaxLife, at, corpseRadius, now, placement, more);
                ReleaseVolatileOrb(at, orbRadius, limit, speed, current, hit, fuse);
            }
        }

        void ReleaseVolatileOrb((int X, int Y) start, int radius, int limit, double speed, Func<bool> current,
            Action<(int X, int Y), int, double> hit, double? fuse)
        {
            while (placedVolatileOrbs.Count >= limit)
                RemovePlacedVolatileOrb(placedVolatileOrbs.First!);
            uint id = Player.TakeObjectId();
            var orb = placedVolatileOrbs.AddLast(id);
            var effect = new EffectObject(start.X, start.Y, VolatileOrbArt);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()), wake: true);

            void Explode((int X, int Y) centre, double now)
            {
                RemovePlacedVolatileOrb(orb);
                Effect(centre.X, centre.Y, VolatileBlastArt, ProjectileRuntime.Time(.5), radius);
                hit(centre, radius, now);
            }

            if (fuse is double duration)
            {
                Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(duration), () =>
                {
                    if (orb.List is null) return;
                    if (Active && current()) Explode(start, ProjectileWorldTime(Player));
                    else RemovePlacedVolatileOrb(orb);
                });
                return;
            }

            var at = Vector(start);
            long end = Runtime.Now + ProjectileRuntime.Time(4.5);
            long step = ProjectileRuntime.Time(VolatileOrbStep);
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.25), Advance);

            void Advance()
            {
                if (orb.List is null) return;
                if (!Active || !current() || Runtime.Now >= end) { RemovePlacedVolatileOrb(orb); return; }
                double now = ProjectileWorldTime(Player);
                var centre = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
                var nearest = Player.Area.MonstersNear(centre, VolatileOrbSearch)
                    .Where(entry => ArcProxyTarget(Player, entry.Key, entry.Value))
                    .Select(entry => Vector(entry.Value.Walk.Position(now)) - at)
                    .Where(offset => offset.Length <= VolatileOrbSearch)
                    .OrderBy(offset => offset.Length).Select(offset => (ProjectileVector?)offset).FirstOrDefault();
                if (nearest is { } offset)
                {
                    if (offset.Length <= VolatileOrbContact)
                    {
                        Explode(centre, now);
                        return;
                    }
                    var next = at + offset.Unit * Math.Min(speed * VolatileOrbStep, offset.Length);
                    var cell = ((int)Math.Round(next.X), (int)Math.Round(next.Y));
                    if (Player.Layout.WalkableFor(cell.Item1, cell.Item2, 0) && !Player.Barrier.Contains(cell))
                    {
                        at = next;
                        if (cell != centre) World(id, InstanceProtocol.Teleport(id, cell.Item1, cell.Item2));
                    }
                }
                Runtime.Schedule(Runtime.Now + step, Advance);
            }
        }

        void RemovePlacedVolatileOrb(LinkedListNode<uint> orb)
        {
            uint id = orb.Value;
            placedVolatileOrbs.Remove(orb);
            Player.UntrackProjectileObject(id);
            WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
        }

        void ClearPlacedVolatileOrbs()
        {
            foreach (uint id in placedVolatileOrbs) RetireSkillEffectObject(id);
            placedVolatileOrbs.Clear();
        }
    }
}
