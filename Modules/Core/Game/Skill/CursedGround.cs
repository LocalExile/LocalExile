using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string HextoadEffect = "TriggeredSupportHextoad";
    const string HextoadGroundArt = "Metadata/Effects/Spells/Supports/Hextoad/swamp_ground.ao";

    static bool TryPlaceCursedGroundLocked(Player player, ushort skill, SupportedGem hex, (int X, int Y) aim,
        double now, IReadOnlyDictionary<uint, int> playerStats)
    {
        int baseMs = hex.Stats.GetValueOrDefault(Stat.BaseHexZoneSkillDurationMs);
        if (baseMs <= 0) return false;
        if (CaptureGemSourceLifetimeLocked(player, skill, hex.Effect) is not { } source
            || CurseCatalog.HexFamily(SkillsFor(player).EffectId(hex.Effect)) is not { } effect) return true;
        double duration = SkillDurationModifiers.Seconds(baseMs, playerStats.GetValueOrDefault, hex.Stats.GetValueOrDefault,
            DurationKinds(player, hex.Effect));
        int radius = CurseRadius(player, skill, false, hex, playerStats);
        if (duration <= 0 || radius <= 0) return true;

        var toadSupport = hex.Supports.FirstOrDefault(s => s.Effect.Id == "SupportHextoad");
        var child = toadSupport is null ? null
            : SupportCompanionSourceLocked(player, hex, toadSupport, HextoadEffect, "HungryLoopSupportedByHextoad");
        ProjectilesFor(player).PlaceCursedGround(skill, hex, effect, source, playerStats, aim, radius, now, duration, child);
        return true;
    }

    public sealed partial class ProjectileSession
    {
        sealed record CursedGround(GameArea Area, uint Id, ushort Skill, SupportedGem Hex, string Effect,
            GemSourceLifetimes.SourceLifetime Source, IReadOnlyDictionary<uint, int> Stats,
            (int X, int Y) Centre, int Radius, long Ends, PlayerSkillSource? Toad, long ToadInterval)
        {
            public readonly HashSet<SpawnedMonster> Recipients = [];
            public long NextToad;
        }

        CursedGround? cursedGround;

        internal void PlaceCursedGround(ushort skill, SupportedGem hex, string effect, GemSourceLifetimes.SourceLifetime source,
            IReadOnlyDictionary<uint, int> stats, (int X, int Y) centre, int radius, double now, double duration,
            PlayerSkillSource? toad)
        {
            if (!Active) return;
            ClearCursedGround();
            long at = ProjectileRuntime.Time(now);
            long interval = ProjectileRuntime.Time(hex.Stats.GetValueOrDefault(Stat.HexZoneTriggerHextoadEveryXMs) / 1000d);
            var ground = new CursedGround(Player.Area, Player.TakeObjectId(), skill, hex, effect, source, stats,
                centre, radius, at + ProjectileRuntime.Time(duration), toad, interval)
                { NextToad = interval > 0 ? at + interval : long.MaxValue };
            cursedGround = ground;
            if (interval > 0)
            {
                var visual = new EffectObject(centre.X, centre.Y, HextoadGroundArt) { Scale = radius / 22f };
                Player.TrackProjectileObject(ground.Id);
                Create(ground.Id, () => ReferenceEquals(cursedGround, ground)
                    ? InstanceEntities.CreateObject(ground.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), visual.Blob())
                    : null, wake: true);
            }
            StepCursedGround(ground, at);
        }

        bool CursedGroundActive(CursedGround zone)
        {
            if (!Active || !ReferenceEquals(cursedGround, zone) || Player.PlayerLife == 0
                || !GemSourceLifetimeCurrentLocked(Player, zone.Skill, zone.Hex.Effect, zone.Source)) return false;
            var hex = SupportedSkill(Player, zone.Skill);
            return ReferenceEquals(zone.Hex, hex) || SameTriggerSource(zone.Hex, hex);
        }

        void StepCursedGround(CursedGround zone, long at)
        {
            if (at >= zone.Ends || !CursedGroundActive(zone)) { ClearCursedGround(); return; }
            double now = at / (double)ProjectileRuntime.QuantaPerSecond;
            bool Inside(SpawnedMonster monster) => monster is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                && Dist(zone.Centre, monster.Walk.Position(now)) <= zone.Radius;
            zone.Recipients.RemoveWhere(monster =>
            {
                if (ReferenceEquals(zone.Area.Monsters.GetValueOrDefault(monster.ObjectId), monster) && Inside(monster))
                    return false;
                RemoveCursedGroundHex(zone, monster);
                return true;
            });
            bool toadDue = at >= zone.NextToad;
            SpawnedMonster? nearest = null;
            double nearestDistance = double.PositiveInfinity;
            foreach (var (_, monster) in zone.Area.MonstersNear(zone.Centre, zone.Radius))
            {
                if (!Inside(monster)) continue;
                if (toadDue)
                {
                    double distance = Dist(zone.Centre, monster.Walk.Position(now));
                    if (nearest is null || distance < nearestDistance
                        || distance == nearestDistance && monster.ObjectId < nearest.ObjectId)
                        (nearest, nearestDistance) = (monster, distance);
                }
                if (zone.Recipients.Contains(monster) && monster.Curses.All.ContainsKey((zone.Id, zone.Effect))) continue;
                if (ApplyCurseDataLocked(Player, monster, zone.Skill, zone.Effect, zone.Hex.Stats, now, aura: true,
                    zone.Stats, auraSource: zone.Id, buffCaster: Player.Id)) zone.Recipients.Add(monster);
            }
            if (toadDue)
            {
                zone.NextToad = at + zone.ToadInterval;
                if (zone.Toad is { } toad && nearest is not null)
                    ReleaseBurstingToad(zone, toad, nearest.Walk.Position(now));
            }
            long next = Math.Min(zone.Ends, Math.Min(zone.NextToad, at + ProjectileRuntime.QuantaPerSecond / 10));
            ScheduleContact(next, () => ReferenceEquals(cursedGround, zone), () => StepCursedGround(zone, Runtime.Now));
        }

        void ReleaseBurstingToad(CursedGround zone, PlayerSkillSource toad, (int X, int Y) aim)
        {
            if (GemSkillInstanceDisabled(Player, toad.Effect, toad.Id, triggered: true) is not null
                || !PayMana(Player, toad.Id, out uint mana, out uint life)) return;
            bool lockstep = Player.Connection.LockstepClient;
            ushort sequence = NextLockstepSequence(Player);
            if (!lockstep) sequence &= 0x7fff;
            World(Player.Id, InstanceProtocol.InstantAction(Player.Id, toad.Id, aim.X, aim.Y,
                sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                $"0x01ce Bursting Toad at {aim}");
            if (mana > 0 || life > 0) Enqueue(() => SendSkillCost(Player, life, "Bursting Toad cost"));
            var use = (toad.Id, toad.Effect, Name: "Bursting Toad", toad.Level, toad.Quality, toad.Gem);
            var hit = AreaSkillHitForUse(null, Player, toad.Id, use, zone.Centre, aim, immediate: true, sequence: sequence,
                melee: false, criticalRoll: CaptureSkillCriticalRoll(Player, toad.Id), nearbyOnly: true,
                triggeredCurrent: () => CursedGroundActive(zone), scheduledSession: this, cryExerted: false);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Bursting Toad must not yield.");
            hit.GetAwaiter().GetResult();
        }

        static void RemoveCursedGroundHex(CursedGround zone, SpawnedMonster monster)
        {
            if (!monster.HasCurses || !monster.Curses.All.ContainsKey((zone.Id, zone.Effect))) return;
            monster.Curses.Remove(zone.Id, zone.Effect);
            monster.PresentationChanged?.Invoke();
        }

        void ClearCursedGround()
        {
            if (cursedGround is not { } zone) return;
            cursedGround = null;
            if (zone.Area.Driven && !zone.Area.Owned) zone.Area.Defer(Remove);
            else Remove(zone.Area);

            void Remove(GameArea area)
            {
                foreach (var monster in zone.Recipients) RemoveCursedGroundHex(zone, monster);
                if (zone.ToadInterval <= 0) return;
                Player.UntrackProjectileObject(zone.Id);
                byte[] remove = InstanceProtocol.RemoveObject(zone.Id);
                if (area.Driven) area.Delta.RemoveSharedObject(zone.Id, remove);
                else WorldRecords(zone.Id, () => [remove], remove: true);
            }
        }
    }
}
