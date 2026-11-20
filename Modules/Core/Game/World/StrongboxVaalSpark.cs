using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string VaalSparkEffect = "ChestVaalSpark";
    const string SparkProjectile = "Metadata/Projectiles/Spark";

    public sealed record SparkCell(int X, int Y);
    public sealed record SparkTarget(uint Peer, int Epoch, int X, int Y);
    public sealed record SparkFrame(int Step, SparkTarget[] Players);
    public sealed record SparkContact(int Flight, uint Peer, int Epoch, long At, ProjectileHitResult Result);

    public sealed record VaalSpark(long Started, string Daemon, int AimX, int AimY, uint[] Objects, SparkCell[] Walls)
        : VesselCast(Started, Daemon)
    {
        // Changed player cells and accepted contacts replay at most one two-second volley after a rules reload.
        public List<SparkFrame> Frames { get; init; } = [];
        public List<SparkContact> Contacts { get; init; } = [];
        internal SparkFlight? Flight;
        internal override (int X, int Y) Target(Box box) => (AimX, AimY);
    }

    sealed record SparkSkill(VesselSkill Spell, VesselImpact Hit, ProjectileProfile Profile, long CastTicks,
        long Lifetime, ProjectilePresentation? Art)
    {
        public int Frames => checked((int)((Lifetime + ProjectileRuntime.TickQuanta - 1) / ProjectileRuntime.TickQuanta));
        public double SampleTime(VaalSpark cast, int step) => (cast.Started + CastTicks) / (double)TimeSpan.TicksPerSecond
            + step * ProjectileRuntime.TickQuanta / (double)ProjectileRuntime.QuantaPerSecond;
    }

    static SparkSkill ReadSpark(ClientState c)
    {
        var catalog = SkillsFor(c);
        var controller = ReadVesselSpell(c, "EASChestVaalSpark");
        controller.Timing!.Value.ApplyTo(controller.Stats, Stats);
        var grant = catalog.FindMonsterSkill(controller.Action, controller.Effect, controller.ActorLevel,
            [.. controller.Stats.Select(s => (s.Key, s.Value))])!;
        var spell = ReadVesselSpell(c, VaalSparkEffect) with { PrecedingGrant = grant };
        double speed = ProjectileSpeed.CellsPerSecond(catalog.Projectile(SparkProjectile)!.Value.Speed, _ => 0, spell.Get);
        // The daemon's damaging grant uses the ordinary Spark action, with the chest's own stats.
        var profile = GatewayProjectileSkillProfiles.Resolve("Spark", SparkProjectile, spell.Get, speed, attack: false);
        spell.Stats[Stats.Key("total_number_of_projectiles_to_fire")!.Value] = profile.Count;
        int ms = spell.Required("base_skill_effect_duration");
        return new(spell, spell.Impact(0), profile, TimeSpan.FromSeconds(spell.Seconds).Ticks, ProjectileRuntime.Time(ms / 1000d),
            MonsterProjectilePresentation(catalog.Clips(SparkProjectile), ms / 1000d));
    }

    static VaalSpark PrepareSpark(ClientState c, Box box, string daemon, SparkSkill skill)
    {
        var now = c.Clock.Elapsed;
        var aim = c.Walk.Position(now.TotalSeconds);
        if (aim == (box.X, box.Y)) aim = (box.X + 1, box.Y);
        lock (c.Monsters)
            return new(now.Ticks, daemon, aim.X, aim.Y, [.. Enumerable.Range(0, skill.Profile.Count).Select(_ => c.TakeObjectId())],
                [.. c.ProjectileWalls.Select(p => new SparkCell(p.Item1, p.Item2))]);
    }

    // One combat simulation for the instance. All observers refer to the same flights, even after the opener leaves.
    internal sealed class SparkFlight : IProjectileWorld
    {
        readonly ProjectileGrid.Area? grid;
        readonly AreaLayout layout;
        readonly HashSet<(int, int)> walls;
        ProjectileActor[] actors = [];
        readonly VaalSpark cast;
        Func<ProjectileDamage, ProjectileHitResult>? commitHit;
        public ProjectileRuntime Runtime { get; }
        public int[] Legs { get; }

        public SparkFlight(ClientState c, Box box, VaalSpark cast, ProjectileProfile profile, long lifetime, Func<string, int> stats)
        {
            this.cast = cast;
            grid = ProjectileGrid.Of(c.Layout.Id, c.Seed);
            layout = c.Layout;
            walls = [.. cast.Walls.Select(w => (w.X, w.Y))];
            Legs = new int[profile.Count];
            var aim = new ProjectileVector(cast.AimX, cast.AimY);
            var skill = ClientDrawnProfile(c, profile, stats, MonsterActionSequence, aim, emitter: cast.Actor) with
            {
                Legs = index => new SparkLegs(MonsterActionSequence, index, cast.Actor,
                    lifetime / (double)ProjectileRuntime.QuantaPerSecond, 0, profile.Speed),
                LegLeash = 150
            };
            var owner = new ProjectileActorKey(cast.Actor);
            Runtime = new(this);
            Runtime.Release(new(skill, owner, owner, 2, new(box.X, box.Y), aim) { Sequence = MonsterActionSequence });
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

        void Take(SparkTarget[] players) => actors = [.. players.Select(p =>
            new ProjectileActor(new(p.Peer, p.Epoch), 1, _ => new(p.X, p.Y), 0) { Size = LivingCollision.PlayerSize })];

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

    static async Task TickSparks(ClientState conn, Area area, Box box, TimeSpan now)
    {
        var skill = area.SparkSkill ??= ReadSpark(conn);
        foreach (var cast in box.Sparks.ToArray())
        {
            if (!await PublishVesselHits(conn, area, cast)) return;
            long elapsed = now.Ticks - cast.Started - skill.CastTicks;
            if (elapsed < 0)
            {
                await ShowVesselCast(conn, area, box, cast, skill.Spell);
                continue;
            }
            if (cast.Removals is null)
            {
                await ShowVesselCast(conn, area, box, cast, skill.Spell, play: false);
                if (cast.Actor == 0) return;
                lock (conn.Monsters)
                {
                    if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !CombatLifetime!.TryRunSynchronous(
                        () => AdvanceSpark(conn, area, box, cast, skill, elapsed))) return;
                }
                if (!await PublishVesselHits(conn, area, cast)) return;
                await Task.WhenAll(Members(conn).Select(p => PresentSparks(conn.Instance, p, cast, skill.Art)));
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
                if (cast.Flight!.Runtime.Projectiles.Count != 0) continue;
            }
            if (!await RemoveVesselActor(conn, area, box, cast)) return;
            box.Sparks.Remove(cast);
            Keep(conn, area);
        }
    }

    static void AdvanceSpark(ClientState conn, Area area, Box box, VaalSpark cast, SparkSkill skill, long elapsed)
    {
        var previous = ProjectileOutputGuard.Value;
        ProjectileOutputGuard.Value = null;
        try
        {
            var flight = cast.Flight ??= new(conn, box, cast, skill.Profile, skill.Lifetime, skill.Spell.Get);
            var players = Members(conn).Where(p => p.PlayerSent && ViewFor(p).Entered.HasValue).OrderBy(p => p.PeerId).ToArray();
            long quanta = Math.Min(skill.Lifetime, ProjectileRuntime.Time(elapsed / (double)TimeSpan.TicksPerSecond));
            int steps = quanta == skill.Lifetime ? skill.Frames : (int)(quanta / ProjectileRuntime.TickQuanta);
            int start = cast.Landed;
            while (cast.Landed < steps && flight.Runtime.Projectiles.Count != 0)
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
                        MonsterActionSequence, skill.Hit.AilmentBase, skill.Spell.Critical, cast.CriticalRoll);
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

    static Task PresentSparks(InstanceState instance, ClientState player, VaalSpark cast, ProjectilePresentation? art) =>
        PublishInstanceOutput(instance, player, async current =>
        {
            var view = ViewFor(player);
            var flight = cast.Flight!;
            var wire = player.Wire;
            bool needsCaster;
            lock (player.Monsters) needsCaster = player.PlayerSent && view.Entered.HasValue && !player.MonsterViews.ContainsKey(cast.Actor);
            if (needsCaster) await SyncInstanceMonsters(wire.Remote, wire.Stream, wire.Ciphers, player, reveal: false);
            bool visible;
            lock (player.Monsters)
                visible = player.PlayerSent && view.Entered.HasValue && player.MonsterViews.ContainsKey(cast.Actor)
                    && !player.AsleepMonsters.Contains(cast.Actor);
            await Task.WhenAll(Enumerable.Range(0, cast.Objects.Length).Select(Present));

            async Task Present(int index)
            {
                uint id = cast.Objects[index];
                int prior;
                lock (view.SparkLegs) prior = view.SparkLegs.GetValueOrDefault(id);
                var p = visible ? flight.Runtime.Projectiles.FirstOrDefault(s => s.VolleyIndex == index) : null;
                if (p is null)
                {
                    if (prior != 0)
                        await SendWorldRemoval(wire.Remote, wire.Stream, wire.Ciphers, player, id, "vessel spark ends", current,
                            () =>
                            {
                                lock (view.SparkLegs) return view.SparkLegs.Remove(id) ? InstanceProtocol.RemoveObject(id) : null;
                            });
                    return;
                }
                int leg = flight.Legs[index];
                if (prior == leg) return;
                var at = Cell(p.Position);
                var end = p.Position + p.Direction * p.Remaining;
                if (flight.TraceTerrain(p.Position, end) is { } wall) end = p.Position + (end - p.Position) * wall.Fraction;
                var to = Cell(end);
                var height = ProjectileHeight.Terrain;
                byte[] move = height.MoveTo(id, to.X, to.Y, (float)Math.Max(.001, (end - p.Position).Length / p.Speed));
                if (prior == 0)
                    await SendWorldCreate(player, id, () =>
                    {
                        lock (view.SparkLegs) view.SparkLegs[id] = leg;
                        return CreateProjectileObject(id, new(SparkProjectile, at.X, at.Y, (float)Math.Atan2(p.Direction.X, -p.Direction.Y), Team: 1));
                    }, "vessel spark", current, awaitBirth: false, afterCreation: () => art?.FlightClip is { } clip
                        ? [InstanceProtocol.AnimationUpdate(id, 0, clip, art.FlightClipSpeed), move] : [move]);
                else
                    await SendWorldRecords(wire.Remote, wire.Stream, wire.Ciphers, player, id, () =>
                    {
                        lock (view.SparkLegs) view.SparkLegs[id] = leg;
                        return [height.StopAt(id, at.X, at.Y), move];
                    }, still: current);
            }
        });
}
