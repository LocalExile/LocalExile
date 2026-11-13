using System.Text;
using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

/// <summary>A step looks for contacts through a grid over the actors once there are many (ProjectileRuntime's
/// ContactSearch) instead of testing every flight against every actor, and a flight keeps the contact it worked out
/// while the actors are where they were (KeepContacts). Both must find the same contacts in the same order as working
/// everything out at every step: the same seeded fights are run each way and every hit request and event compared. The
/// world also changes inside notifications (an actor dies, jumps or arrives while a flight's end is published), which
/// nothing forbids a host to do: a kept contact must not outlive that.</summary>
public class ProjectileContactSearchTests
{
    [Fact]
    public void A_crowd_s_contacts_are_the_ones_the_plain_scan_of_every_actor_finds()
    {
        for (int seed = 1; seed <= 1; seed++)
        {
            string plain = Fight(seed, indexFrom: int.MaxValue, keep: false);
            int lines = plain.Count(c => c == '\n'), hits = plain.Split("\nH ").Length - 1, meddled = plain.Split("\nM ").Length - 1;
            Assert.True(lines > 2000 && hits > 400 && meddled > 40,
                $"the fight is too quiet to compare anything: {lines} lines, {hits} hit requests, {meddled} changes in notifications");
            Assert.Equal(plain, Fight(seed, indexFrom: 0, keep: false));
            Assert.Equal(plain, Fight(seed, indexFrom: 0, keep: true));
            Assert.Equal(plain, Fight(seed, indexFrom: int.MaxValue, keep: true));
        }
    }

    static string Fight(int seed, int indexFrom, bool keep)
    {
        var log = new StringBuilder();
        var world = new Crowd(seed, 320, log);
        var runtime = new ProjectileRuntime(world) { IndexActorsFrom = indexFrom, KeepContacts = keep };
        world.Schedule = runtime.Schedule;
        var random = new Random(seed * 7919);
        var area = new ProjectileAreaRecipe("area", 9);
        ProjectileProfile[] profiles =
        [
            new("arrow") { Speed = 1800, Range = 120, Radius = 1 },
            new("cells") { Speed = 1500, Range = 150, ContactCells = 1, Pierce = -1 },
            new("chain") { Speed = 1200, Range = 100, Radius = 2, Chain = 3, ChainRange = 40 },
            new("fork") { Speed = 1300, Range = 110, Radius = 1.5, Fork = 1 },
            new("split") { Speed = 1000, Range = 140, Radius = 1.5, Split = 2, SplitOnTerrain = true, SplitWithoutTargets = true },
            new("return") { Speed = 1400, Range = 80, ContactCells = 1, Returns = true, Pierce = -1, ReturnOvershoot = 3 },
            new("contactarea") { Speed = 1100, Range = 100, Radius = 2, ContactArea = area, Pierce = 1 },
            new("lobbed") { Speed = 500, Range = 70, Radius = 2, Lobbed = true, EndArea = area with { Stage = "land", Radius = 14 } },
            new("bounce") { Speed = 1600, Range = 150, Radius = 1, TerrainBounces = 2, Pierce = -1 },
            // Flights that can hit an actor again forget it once they have left it.
            new("repeat") { Speed = 300, Range = 60, Radius = 5, Pierce = -1, HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Interval, 20000) },
            new("window") { Speed = 350, Range = 60, ContactCells = 2, Pierce = -1, HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Window, 30000), SkipDeniedContacts = true },
            // Flights that end inside a step ask each actor where it is at that instant.
            new("shortlife") { Speed = 800, Range = 150, Radius = 2, Lifetime = 9000, Pierce = -1 },
            new("shortrange") { Speed = 2400, Range = 11.3, Radius = 1.5, Pierce = -1 },
        ];
        const int ticks = 100;
        for (int tick = 1; tick <= ticks; tick++)
        {
            if (tick < ticks - 20 && random.Next(100) < 85)
            {
                var profile = profiles[random.Next(profiles.Length)];
                var origin = world.Player.Position(runtime.Now);
                var aim = origin + new ProjectileVector(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1).Unit * (20 + random.Next(120));
                runtime.Release(new ProjectileUse(profile with { Count = 2 + random.Next(14) }, world.Player.Key, world.Player.Key, 1, origin, aim)
                    { ReturnAnchor = world.Player.Key, Seed = random.Next(1, 1 << 20) });
            }
            runtime.AdvanceTo(tick * ProjectileRuntime.TickQuanta);
        }
        runtime.AdvanceTo((ticks + 300) * ProjectileRuntime.TickQuanta);
        return log.ToString();
    }

    /// <summary>Actors that walk with bends, die, stop, jump and arrive as they are hit, between walls.</summary>
    sealed class Crowd : IProjectileWorld
    {
        sealed class Body
        {
            public ProjectileActor Actor = null!;
            public int Life;
            public long StoppedAt = long.MaxValue;
            public ProjectileVector Pushed;
        }

        readonly Dictionary<ProjectileActorKey, Body> bodies = [];
        readonly List<ProjectileActor> order = [];
        readonly StringBuilder log;
        readonly int seed;
        uint nextId = 0x8000;
        public Action<long, Action> Schedule = (_, _) => { };
        public ProjectileActor Player { get; }

        public Crowd(int seed, int count, StringBuilder log)
        {
            this.seed = seed; this.log = log;
            var random = new Random(seed);
            Player = Add(new(0x1000, seed), 1, 200, 200, 2, 0, random.Next(), int.MaxValue);
            for (int i = 0; i < count; i++)
                Add(new(nextId++, random.Next(3) == 0 ? 1 : 0), random.Next(40) == 0 ? 1 : 2, 110 + random.NextDouble() * 180,
                    110 + random.NextDouble() * 180, random.Next(-1, 4), random.NextDouble() * 2.5, random.Next(), 1 + random.Next(4));
        }

        ProjectileActor Add(ProjectileActorKey key, int team, double x, double y, int size, double radius, int path, int life)
        {
            var body = new Body { Life = life };
            // A new waypoint every 9000 quanta (a tick is 4000), so a step can cross a bend.
            ProjectileVector Waypoint(long leg)
            {
                ulong h = unchecked((ulong)path * 6364136223846793005UL + (ulong)leg * 1442695040888963407UL);
                h ^= h >> 29; h *= 0xbf58476d1ce4e5b9UL; h ^= h >> 32;
                return new(x + (h & 0xffff) / 65535.0 * 14 - 7, y + ((h >> 16) & 0xffff) / 65535.0 * 14 - 7);
            }
            ProjectileVector At(long t)
            {
                t = Math.Min(t, body.StoppedAt);
                var a = Waypoint(t / 9000);
                return a + (Waypoint(t / 9000 + 1) - a) * (t % 9000 / 9000.0) + body.Pushed;
            }
            body.Actor = new ProjectileActor(key, team, At, radius) { Size = size };
            bodies[key] = body; order.Add(body.Actor);
            return body.Actor;
        }

        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [.. order];

        static bool Wall(int x, int y) => x < 20 || y < 20 || x > 380 || y > 380
            || (x / 60 % 2 == 1 && y / 60 % 2 == 1 && x % 60 < 5 && y % 60 < 5);
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => ProjectileGeometry.TraceGrid(from, to, Wall);

        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            ulong h = unchecked((ulong)damage.Target.Id * 2654435761UL ^ (ulong)damage.At * 40503UL ^ (ulong)damage.Projectile.Id * 97UL ^ (ulong)seed);
            h ^= h >> 13; h *= 0x9e3779b97f4a7c15UL; h ^= h >> 31;
            int roll = (int)(h % 20);
            var result = roll == 0 ? ProjectileHitResult.Miss : roll == 1 ? ProjectileHitResult.Blocked : ProjectileHitResult.Hit;
            log.Append($"H {damage.Projectile.Id} {damage.Target.Id}/{damage.Target.Generation} {damage.At} {damage.Stage} {damage.Phase} "
                + $"{damage.Multiplier:R} {damage.IsArea} {damage.Position.X:R},{damage.Position.Y:R} {result}\n");
            if (result != ProjectileHitResult.Hit || !bodies.TryGetValue(damage.Target, out var body)) return result;
            if (roll is 2 or 3) body.StoppedAt = Math.Min(body.StoppedAt, damage.At);
            if (roll == 4) body.Pushed += new ProjectileVector((h >> 8 & 7) - 3.5, (h >> 12 & 7) - 3.5);
            if (--body.Life > 0) return result;
            bodies.Remove(damage.Target); order.Remove(body.Actor);
            if (roll is 5 or 6)
            {
                var at = body.Actor.Position(damage.At);
                Add(new(nextId++, 0), 2, at.X + 3, at.Y - 2, 1, 1, unchecked((int)h), 2);
            }
            return result;
        }

        // What a notification is free to do: the world changes while a flight's end is published, with no hit decided.
        void Meddle(ProjectileEvent value)
        {
            ulong h = unchecked((ulong)value.Projectile.Id * 0x9e3779b97f4a7c15UL ^ (ulong)value.At * 0xc2b2ae3d27d4eb4fUL ^ (ulong)seed);
            h ^= h >> 31; h *= 0xff51afd7ed558ccdUL; h ^= h >> 29;
            if (order.Count < 2) return;
            var body = bodies[order[1 + (int)((h >> 8) % (ulong)(order.Count - 1))].Key];
            switch ((int)(h % 12))
            {
                case 0: bodies.Remove(body.Actor.Key); order.Remove(body.Actor); log.Append($"M dies {body.Actor.Key.Id}\n"); break;
                case 1: body.Pushed += new ProjectileVector((h >> 20 & 15) - 7.5, (h >> 24 & 15) - 7.5); log.Append($"M jumps {body.Actor.Key.Id}\n"); break;
                case 2: body.StoppedAt = Math.Min(body.StoppedAt, value.At); break;
                case 3: Schedule(value.At + (long)(h >> 16 & 0xfff), () => { }); break;
                case 4: Add(new(nextId++, 0), 2, value.Position.X + 4, value.Position.Y + 1, 1, 1, unchecked((int)h), 2); log.Append("M arrives\n"); break;
            }
        }

        public void Publish(ProjectileEvent value)
        {
            if (value.Kind == ProjectileEventKind.End) Meddle(value);
            log.Append($"E {value.Kind} {value.Projectile.Id} {value.Projectile.ParentId} {value.At} {value.Position.X:R},{value.Position.Y:R} "
                + $"{value.Reason} {value.Radius:R} {value.Stage} {value.Projectile.Phase} {value.Projectile.Direction.X:R},{value.Projectile.Direction.Y:R} "
                + $"{value.Projectile.Remaining:R}\n");
        }
    }
}
