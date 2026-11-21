using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    // Existing Spark saves and the other vessel volleys share these position and contact records.
    public sealed record SparkCell(int X, int Y);
    public sealed record SparkTarget(uint Peer, int Epoch, int X, int Y);
    public sealed record SparkFrame(int Step, SparkTarget[] Players);
    public sealed record SparkContact(int Flight, uint Peer, int Epoch, long At, ProjectileHitResult Result);

    public record VesselProjectileCast(long Started, string Daemon, int AimX, int AimY, SparkCell[] Walls)
        : VesselCast(Started, Daemon)
    {
        // Only changed player cells and accepted contacts are needed to reconstruct an active volley after reload.
        public List<SparkFrame> Frames { get; init; } = [];
        public List<SparkContact> Contacts { get; init; } = [];
        internal VesselFlight? Flight;
        internal override (int X, int Y) Target(Box box) => (AimX, AimY);
    }

    record VesselProjectileSkill(VesselSkill Action, VesselImpact Hit, ProjectileProfile Profile, long ReleaseTicks, long Lifetime)
    {
        public long LastRelease => (Profile.Count - 1) / Profile.SequentialBatchSize * Profile.SequentialDelay;
        public int Frames => checked((int)((Lifetime + ProjectileRuntime.TickQuanta - 1) / ProjectileRuntime.TickQuanta));
        public double SampleTime(VesselProjectileCast cast, int step) => (cast.Started + ReleaseTicks) / (double)TimeSpan.TicksPerSecond
            + step * ProjectileRuntime.TickQuanta / (double)ProjectileRuntime.QuantaPerSecond;
        public virtual ProjectileProfile FlightProfile(ClientState c, uint actor, ProjectileVector aim) => Profile;
    }

    static (long Started, int AimX, int AimY, SparkCell[] Walls) VesselProjectileStart(ClientState c, Box box)
    {
        var now = c.Clock.Elapsed;
        var aim = c.Walk.Position(now.TotalSeconds);
        if (aim == (box.X, box.Y)) aim = (box.X + 1, box.Y);
        lock (c.Monsters)
            return (now.Ticks, aim.X, aim.Y, [.. c.ProjectileWalls.Select(p => new SparkCell(p.Item1, p.Item2))]);
    }

    // One combat simulation for the instance. All observers refer to the same flights, even after the opener leaves.
    internal sealed class VesselFlight : IProjectileWorld
    {
        readonly ProjectileGrid.Area? grid;
        readonly AreaLayout layout;
        readonly HashSet<(int, int)> walls;
        ProjectileActor[] actors = [];
        readonly VesselProjectileCast cast;
        readonly ProjectileActor? anchor;
        readonly long lastRelease;
        public bool Active => Runtime.Now < lastRelease || Runtime.Projectiles.Count != 0;
        Func<ProjectileDamage, ProjectileHitResult>? commitHit;
        public ProjectileRuntime Runtime { get; }
        public int[] Legs { get; }

        public VesselFlight(ClientState c, Box box, VesselProjectileCast cast, ProjectileProfile profile, long lifetime, long finalRelease)
        {
            this.cast = cast;
            grid = ProjectileGrid.Of(c.Layout.Id, c.Seed);
            layout = c.Layout;
            walls = [.. cast.Walls.Select(w => (w.X, w.Y))];
            var aim = new ProjectileVector(cast.AimX, cast.AimY);
            var owner = new ProjectileActorKey(cast.Actor);
            var origin = new ProjectileVector(box.X, box.Y);
            if (profile.Returns)
            {
                anchor = new(owner, 2, _ => origin, 0);
                actors = [anchor];
            }
            Legs = new int[profile.Count];
            lastRelease = finalRelease;
            Runtime = new(this);
            Runtime.Release(new(profile, owner, owner, 2, origin, aim) { Sequence = MonsterActionSequence, ReturnAnchor = anchor?.Key });
            foreach (var frame in cast.Frames)
            {
                Runtime.AdvanceTo(frame.Step * ProjectileRuntime.TickQuanta);
                Take(frame.Players);
            }
            if (cast.Landed > 0) Runtime.AdvanceTo(Math.Min(lifetime, cast.Landed * ProjectileRuntime.TickQuanta));
        }

        public void Advance(int step, SparkTarget[] players, Func<ProjectileDamage, ProjectileHitResult> commit, long until)
        {
            if (cast.Frames.Count == 0 || !players.AsSpan().SequenceEqual(cast.Frames[^1].Players))
            {
                cast.Frames.Add(new(step, players));
                Take(players);
            }
            commitHit = commit;
            try { Runtime.AdvanceTo(until); }
            finally { commitHit = null; } // The instance must not retain a runner or its members between ticks.
            cast.Landed++;
        }

        void Take(SparkTarget[] players)
        {
            actors = new ProjectileActor[players.Length + (anchor is null ? 0 : 1)];
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                actors[i] = new(new(p.Peer, p.Epoch), 1, _ => new(p.X, p.Y), 0) { Size = LivingCollision.PlayerSize };
            }
            if (anchor is not null) actors[^1] = anchor;
        }

        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => actors;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            ProjectileGeometry.TraceGrid(from, to, (x, y) => ProjectileGrid.Blocks(layout, grid, walls, x, y));
        public ProjectileHitResult Hit(ProjectileDamage d) => commitHit is not null ? commitHit(d)
            : cast.Contacts.Find(h => h.Flight == d.Projectile.VolleyIndex && h.At == d.At
                && h.Peer == d.Target.Id && h.Epoch == d.Target.Generation)?.Result ?? ProjectileHitResult.Miss;
        public void Publish(ProjectileEvent e)
        {
            if (e.Kind is ProjectileEventKind.Spawn or ProjectileEventKind.Redirect) Legs[e.Projectile.VolleyIndex]++;
        }
    }

    static async Task TickVesselProjectiles<T>(ClientState conn, Area area, Box box, TimeSpan now, List<T> casts,
        VesselProjectileSkill skill, Func<T, Task>? present = null) where T : VesselProjectileCast
    {
        foreach (var cast in casts.ToArray())
        {
            if (!await PublishVesselHits(conn, area, cast)) return;
            long elapsed = now.Ticks - cast.Started - skill.ReleaseTicks;
            if (elapsed < 0)
            {
                await ShowVesselCast(conn, area, box, cast, skill.Action);
                continue;
            }
            if (cast.Removals is null)
            {
                await ShowVesselCast(conn, area, box, cast, skill.Action, play: false);
                if (cast.Actor == 0) return;
                lock (conn.Monsters)
                {
                    if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !CombatLifetime!.TryRunSynchronous(
                        () => AdvanceVesselProjectiles(conn, area, box, cast, skill, elapsed))) return;
                }
                if (!await PublishVesselHits(conn, area, cast)) return;
                if (present is not null) await present(cast);
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
                if (cast.Flight!.Active) continue;
            }
            if (!await RemoveVesselActor(conn, area, box, cast)) return;
            casts.Remove(cast);
            Keep(conn, area);
        }
    }

    static void AdvanceVesselProjectiles(ClientState conn, Area area, Box box, VesselProjectileCast cast, VesselProjectileSkill skill, long elapsed)
    {
        var previous = ProjectileOutputGuard.Value;
        ProjectileOutputGuard.Value = null;
        try
        {
            var flight = cast.Flight ??= new(conn, box, cast, skill.FlightProfile(conn, cast.Actor, new(cast.AimX, cast.AimY)),
                skill.Lifetime, skill.LastRelease);
            var players = Members(conn).Where(p => p.PlayerSent && ViewFor(p).Entered.HasValue).OrderBy(p => p.PeerId).ToArray();
            long quanta = Math.Min(skill.Lifetime, ProjectileRuntime.Time(elapsed / (double)TimeSpan.TicksPerSecond));
            int steps = quanta == skill.Lifetime ? skill.Frames : (int)(quanta / ProjectileRuntime.TickQuanta);
            int start = cast.Landed;
            while (cast.Landed < steps && flight.Active)
            {
                double sample = skill.SampleTime(cast, cast.Landed);
                var targets = players.Where(p => ViewFor(p).Entered!.Value / (double)TimeSpan.TicksPerSecond <= sample
                    && !p.Closed && !p.LeavingArea && !p.Paused && p.PlayerLife != 0 && !p.Immune && !InGracePeriod(p))
                    .Select(p =>
                    {
                        var cell = p.Walk.WatchedPosition((box.Id, cast.Started), sample);
                        return new SparkTarget(p.PeerId, p.IncomingHitEpoch, cell.X, cell.Y);
                    }).ToArray();
                flight.Advance(cast.Landed, targets, Hit, Math.Min(skill.Lifetime, (cast.Landed + 1) * ProjectileRuntime.TickQuanta));

                ProjectileHitResult Hit(ProjectileDamage d)
                {
                    var player = players.First(p => p.PeerId == d.Target.Id);
                    if (player.PlayerLife == 0) return ProjectileHitResult.Miss;
                    var target = targets.First(t => t.Peer == d.Target.Id);
                    var hit = CommitMonsterHitLocked(player.Wire.Remote, player, cast.Actor, conn.Monsters[cast.Actor],
                        skill.Hit.Attack, player.Clock.Elapsed.TotalSeconds, (target.X, target.Y), Cell(d.Position), false,
                        MonsterActionSequence, skill.Hit.AilmentBase, skill.Action.Critical, cast.CriticalRoll);
                    if (hit is null) return ProjectileHitResult.Miss;
                    var result = hit.Blocked ? ProjectileHitResult.Blocked : ProjectileHitResult.Hit;
                    cast.Contacts.Add(new(d.Projectile.VolleyIndex, target.Peer, target.Epoch, d.At, result));
                    cast.Pending.Add(new(target.Peer, target.Epoch, hit));
                    return result;
                }
            }
            if (start != cast.Landed) Keep(conn, area);
        }
        finally { ProjectileOutputGuard.Value = previous; }
    }

}
