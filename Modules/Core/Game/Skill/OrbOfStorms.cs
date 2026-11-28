using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static bool OrbOfStormsEffect(string? effect) => effect is "OrbOfStorms" or "OrbOfStormsAltX";

    public sealed partial class ProjectileSession
    {
        const string StormOrbArt = "Metadata/Effects/Spells/storm_cloud/rig.ao";
        const string StormOrbArea = "Metadata/Effects/Spells/storm_cloud/aoe_marker.ao";
        const string StormOrbTeleport = "Metadata/Effects/Spells/storm_cloud/storm_teleport_marker.ao";
        const int StormOrbBeamRow = 29;

        sealed class StormOrb(uint caster, uint id, uint areaId, (int X, int Y) at, int radius, int splitRadius,
            int splits, int remaining, long channelInterval, bool squalls, Func<bool> current,
            Action<(int X, int Y), int, uint[], Action<uint>> hit)
        {
            public readonly uint Caster = caster, Id = id, AreaId = areaId;
            public (int X, int Y) At = at;
            public readonly int Radius = radius, SplitRadius = splitRadius, Splits = splits;
            public readonly long ChannelInterval = channelInterval;
            public int Remaining = remaining;
            public readonly bool Squalls = squalls;
            public readonly Func<bool> Current = current;
            public readonly Action<(int X, int Y), int, uint[], Action<uint>> Hit = hit;
        }

        readonly Dictionary<uint, StormOrb> stormOrbs = [];
        readonly HashSet<uint> stormOrbBeams = [];

        // The orb table and projectile schedule are owned by the area's mutation gate.
        internal bool HasStormOrb(uint caster) => stormOrbs.ContainsKey(caster);

        internal void ReleaseStormOrb(uint caster, int effect, ushort skill, IReadOnlyDictionary<uint, int> own,
            (int X, int Y) at, Func<bool> current, Action<(int X, int Y), int, uint[], Action<uint>> hit)
        {
            int Own(uint key) => own.GetValueOrDefault(key);
            int budget = Own(Stat.OrbOfStormsMaximumNumberOfHits);
            if (budget <= 0) return;
            if (stormOrbs.TryGetValue(caster, out var previous)) RemoveStormOrb(previous);
            bool squalls = SkillsFor(Player).EffectId(effect) == "OrbOfStormsAltX";
            int radius = SkillRadius(Player, own, spell: true, effect);
            int splitRadius = SkillRadius(Player, Own(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), own, spell: true, effect);
            int splits = Math.Max(0, Own(Stat.ProjectileNumberToSplit));
            int channelMs = Own(Stat.OrbOfStormsBaseChannellingBoltFrequencyMs);
            double rate = 1 + (Own(Stat.OrbOfStormStrikeRateWhileChannellingPlusPct)
                + (double)PlayerStat(Player, Stat.OrbOfStormStrikeRateWhileChannellingPlusPct)) / 100;
            long channelInterval = channelMs <= 0 ? 0
                : ProjectileRuntime.Time(Math.Max(TickSeconds, channelMs / (1000 * Math.Max(.01, rate))));
            uint id = Player.TakeObjectId();
            uint areaId = Player.TakeObjectId();
            var orb = new StormOrb(caster, id, areaId, at, radius, splitRadius, splits, budget, channelInterval,
                squalls, current, hit);
            stormOrbs[caster] = orb;
            Show(id, StormOrbArt, 1);
            Show(areaId, StormOrbArea, Art.ArtRadius(StormOrbArea) is int nativeRadius and > 0
                ? (float)(radius * (250d / 23) / nativeRadius) : 1);

            void Show(uint objectId, string art, float scale)
            {
                Player.TrackProjectileObject(objectId);
                Create(objectId, () => InstanceEntities.CreateObject(objectId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(at.X, at.Y, art) { Scale = scale }.Blob()), wake: true);
            }

            bool Alive() => Active && Player.PlayerLife > 0 && current()
                && stormOrbs.GetValueOrDefault(caster) == orb;
            void Pulse()
            {
                if (!Alive()) { RemoveStormOrb(orb); return; }
                if (!squalls) StrikeStormOrb(orb);
                if (!Alive()) return;
                SchedulePulse();
            }
            void SchedulePulse()
            {
                if (squalls)
                {
                    Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(1), Pulse);
                    return;
                }
                var timing = SpellTimingFor(Player, effect, skill);
                double speed = timing is { BaseMilliseconds: > 0, DurationSeconds: > 0 } value
                    ? value.DurationSeconds.Value * 1000 / value.BaseMilliseconds : 1;
                int interval = Own(Stat.OrbOfStormsBaseBoltFrequencyMs);
                long delay = ProjectileRuntime.Time(Math.Max(TickSeconds, interval * speed / 1000d));
                Runtime.Schedule(Runtime.Now + delay, Pulse);
            }

            if (squalls) StrikeStormOrb(orb);
            if (Alive()) SchedulePulse();
        }

        void StrikeStormOrb(StormOrb orb)
        {
            if (stormOrbs.GetValueOrDefault(orb.Caster) != orb) return;
            if (Player.PlayerLife == 0 || !orb.Current()) { RemoveStormOrb(orb); return; }
            double now = ProjectileWorldTime(Player);
            IEnumerable<(uint Id, (int X, int Y) At)> Targets((int X, int Y) at, int radius) =>
                Player.Area.MonstersNear(at, radius)
                    .Where(entry => entry.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false,
                        Asleep: false, OwnerId: null, WorldHidden: false } && !MonsterAsleep(Player, entry.Key))
                    .Select(entry => (Id: entry.Key, At: entry.Value.Walk.Position(now)))
                    .Where(target => Dist(at, target.At) <= radius && MeleeArcSight(Player, at, target.At))
                    .OrderBy(target => Dist(at, target.At)).ThenBy(target => target.Id);

            var primary = Targets(orb.At, orb.Radius).FirstOrDefault();
            if (primary.Id == 0) return;
            var split = Targets(primary.At, orb.SplitRadius).Where(t => t.Id != primary.Id).Take(orb.Splits).ToArray();
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[storm-orb] actor={orb.Id:x} caster={orb.Caster:x} strike={orb.Remaining} "
                    + $"centre={orb.At.X},{orb.At.Y} primary={primary.Id:x} splits={split.Length} at={now:F3}");
            ShowStormOrbBeam(orb.At, orb.Id, primary.At, primary.Id);
            foreach (var secondary in split)
                ShowStormOrbBeam(primary.At, primary.Id, secondary.At, secondary.Id);
            var struck = new List<uint>();
            orb.Hit(orb.At, orb.Radius + orb.SplitRadius, [primary.Id, .. split.Select(t => t.Id)], struck.Add);
            if (--orb.Remaining == 0) { RemoveStormOrb(orb); return; }
            if (orb.Squalls && struck.Count != 0 && stormOrbs.GetValueOrDefault(orb.Caster) == orb
                && Player.Area.Monsters.GetValueOrDefault(struck[AreaRng(Player).Next(struck.Count)]) is { } target)
            {
                var next = target.Walk.Position(now);
                if (next == orb.At) return;
                orb.At = next;
                World(orb.Id, InstanceProtocol.Teleport(orb.Id, next.X, next.Y));
                World(orb.AreaId, InstanceProtocol.Teleport(orb.AreaId, next.X, next.Y));
                Effect(next.X, next.Y, StormOrbTeleport, ProjectileRuntime.Time(.35));
            }
        }

        internal void StormOrbOnLightningUse(int effect, double at)
        {
            if (!stormOrbs.TryGetValue(Player.Id, out var orb) || OrbOfStormsEffect(SkillsFor(Player).EffectId(effect))
                || !SkillsFor(Player).HasType(effect, "Lightning")) return;
            if (Player.PaidChannel is { } channel && channel.Effect == effect && PaidChannelCurrent(Player, channel))
            {
                if (orb.ChannelInterval > 0)
                    ScheduleChannel(Math.Max(Runtime.Now, ProjectileRuntime.Time(at)) + orb.ChannelInterval);
                return;

                void ScheduleChannel(long due) => ScheduleContact(due,
                    () => stormOrbs.GetValueOrDefault(Player.Id) == orb && Player.PaidChannel is { } latest
                        && latest.Action == channel.Action && latest.Generation == channel.Generation
                        && ChannelCurrent(Player, latest.Skill, latest.Action, latest.Generation),
                    () =>
                    {
                        // Each successful channel tick replaces its receipt. The action and generation outlive it.
                        if (Player.PaidChannel is { } latest && PaidChannelCurrent(Player, latest)) StrikeIfNear();
                        ScheduleChannel(due + orb.ChannelInterval);
                    });
            }
            ScheduleContact(ProjectileRuntime.Time(at), () => stormOrbs.GetValueOrDefault(Player.Id) == orb, StrikeIfNear);

            void StrikeIfNear()
            {
                if (Dist(Player.Walk.Position(ProjectileWorldTime(Player)), orb.At) <= orb.Radius) StrikeStormOrb(orb);
            }
        }

        void ShowStormOrbBeam((int X, int Y) from, uint source, (int X, int Y) to, uint target)
        {
            uint id = Player.TakeObjectId();
            var beam = new BeamEffectObject(from.X, from.Y, source, to.X * BeamEffectObject.WorldPerCell,
                to.Y * BeamEffectObject.WorldPerCell, target, StormOrbBeamRow);
            stormOrbBeams.Add(id);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath), beam.Blob()), wake: true);
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.3), () =>
            {
                stormOrbBeams.Remove(id);
                RemoveStormOrbObject(id);
            });
        }

        void RemoveStormOrb(StormOrb orb)
        {
            if (stormOrbs.GetValueOrDefault(orb.Caster) != orb) return;
            stormOrbs.Remove(orb.Caster);
            RemoveStormOrbObject(orb.AreaId);
            RemoveStormOrbObject(orb.Id);
        }

        void RemoveStormOrbObject(uint id) => WorldRecords(id, () =>
        {
            Player.UntrackProjectileObject(id);
            return [InstanceProtocol.RemoveObject(id)];
        }, remove: true);

        void ClearStormOrbs()
        {
            foreach (var orb in stormOrbs.Values)
            {
                RetireSkillEffectObject(orb.Id);
                RetireSkillEffectObject(orb.AreaId);
            }
            foreach (uint beam in stormOrbBeams) RetireSkillEffectObject(beam);
            stormOrbs.Clear();
            stormOrbBeams.Clear();
        }
    }
}
