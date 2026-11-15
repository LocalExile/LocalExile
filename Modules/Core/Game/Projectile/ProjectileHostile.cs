using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Development fallback for lobbed skills without a radius stat, in cells. The Sand Spitter's native straight
    // projectile uses direct contact and never this fallback.
    public const double MonsterMortarRadiusCells = 10;

    public static double MonsterMortarRadius() =>
        double.TryParse(Environment.GetEnvironmentVariable("Game_MONSTER_MORTAR_RADIUS"), out double radius) && radius > 0
            ? radius : MonsterMortarRadiusCells;

    /// <summary>The use for a monster's ranged attack released now. A shot the client draws (its record carries the
    /// count, MonsterShots.CountStats; the release 0x141ba7c30 fires nothing without one) keeps the order's aim and flies
    /// hidden: a mortar to its scattered landing cells, any other straight shot by the client's own range, contact and
    /// volley (MonsterShots.ClientDrawnProfile). Any other shot carries no count, targets the player's cell at release,
    /// and the server's object is the projectile the player sees. A lobbed shot
    /// lands and damages the area there; a straight shot flies on past its aim. Team 2, so only the player (and friendly
    /// actors) are in its way, never the other monsters.</summary>
    public static ProjectileUse MonsterProjectileUse(ProjectileSession session, uint id, SpawnedMonster m, MonsterAttack a,
        SkillCatalog.MonsterProjectileInfo projectile, int action, uint? aimedActor = null,
        (int X, int Y)? orderedAim = null, ushort sequence = MonsterActionSequence)
    {
        var conn = session.Conn;
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var from = ProjectileSession.Vector(m.Walk.Position(now));
        var aim = ProjectileSession.Vector(orderedAim ?? (aimedActor is uint targetId && OwnedFighter(conn, targetId) is { } golem
            ? golem.Monster.Walk.Position(now) : conn.Walk.Position(now)));
        // The row's speed with the shot's speed stats (the skill's base_projectile_speed_+%: the Root Spider's spit flies
        // twice as fast; the monster's projectile_speed_+%), as the client's speed getter reads them (0x141bb38d0).
        int SkillStat(string name) => Stats.Key(name) is uint key && projectile.SkillStats is { } own ? own.GetValueOrDefault(key) : 0;
        double speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, SkillStat, SkillStat,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        double distance = Math.Max(1, (aim - from).Length);
        // A mortar's or a knife's range in cells (the range getter, 0x141ba8d60): projectile_maximum_range_override, else 150.
        double MortarRange() => SkillStat("projectile_maximum_range_override") is > 0 and var most ? most : 150;
        var toward = (aim - from).Unit;
        // The release point: the clip's contact keyframe along the line to the aim (the Spitter's lies 4.2 cells behind a
        // body that faces away from its target, so toward the player, 9 steps up), else 1.5 cells ahead at the default
        // height; never past half way to the aim, so a close target is not behind the shot. The shot still flies from the
        // monster toward the aim (maintain_projectile_direction_when_using_contact_position). Native straight mortar
        // attacks also rotate the lateral contact offset below.
        var release = a.Release;
        double ahead = release?.Point is { } point ? point.ForwardCells * (release.FacesAway ? -1 : 1) : OrdinaryRangedAttack.ReleaseAheadCells;
        ahead = Math.Min(ahead, distance / 2);
        var origin = from + toward * ahead;
        // projectile_uses_contact_direction: the shot flies from its launch along the contact's direction, as far as the
        // aim was (at least 60 units).
        var heading = toward;
        void TurnAim()
        {
            if (release?.Turn is { } turn && MonsterShots.ContactTurn(turn, toward, release.FacesAway) is { } h)
            {
                heading = h;
                aim = origin + h * Math.Max(distance, 60 / WorldUnits.PerCell);
            }
        }
        ProjectileVector[]? nativeTargets = null;
        double[]? nativeSpeeds = null;
        bool mortar = projectile.Target == SkillCatalog.MonsterShotTarget.MortarScatter;
        if (projectile is { ClientDraws: true } && (!projectile.Lobbed || mortar))
        {
            // The contact offset is rotated with the facing model, then quantized to Positioned cells.
            // maintain_projectile_direction_when_using_contact_position translates the aim by the same offset.
            double lateral = release?.Point is { } contact ? contact.X / WorldUnits.PerCell * (release.FacesAway ? 1 : -1) : 0;
            origin += new ProjectileVector(toward.Y, -toward.X) * lateral;
            origin = new(Math.Floor(origin.X + .5), Math.Floor(origin.Y + .5));
            TurnAim();
            // A table column moves the aim on along the shot (aim + N × unit(aim − release), rounded half up).
            if (!mortar && projectile.AimOffset != 0 && (aim - origin).Length > 0)
            {
                var moved = aim + (aim - origin).Unit * projectile.AimOffset;
                aim = new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
            }
            var translated = aim + origin - from;
            if (mortar)
            {
                // A spell mortar's aim moves with the release only by the stat that says so. Its volley fans about the
                // launch as any other (the shared emitter's fan 0x141bb9730 turns each target), and a row without
                // UseTargetLocation puts it at the skill's range (0x141ba99d0); each target is rounded to its cell. Then
                // each projectile's own launch moves __col9 cells toward its own target, from which its mover measures the
                // distance override and the scatter (0x141a10d50).
                ProjectileNativeVolley? fan = null;
                var start = origin;
                if (projectile.Lobbed)
                {
                    if (!projectile.AimTranslates) translated = aim;
                    // The release (0x141ba51a0) holds the aim at least 60 units (5.52 cells) from the caster's cell and, on
                    // a UseTargetLocation row with a range of its own, no farther than that range.
                    var reachFromActor = translated - from;
                    double held = reachFromActor.Length < 60 / WorldUnits.PerCell ? 60 / WorldUnits.PerCell
                        : projectile.EndsAtTarget && MortarRange() < 150 && reachFromActor.Length > MortarRange() ? MortarRange()
                        : reachFromActor.Length;
                    if (held != reachFromActor.Length)
                    {
                        var line = reachFromActor.Length > 0 ? reachFromActor.Unit : new ProjectileVector(1, 0);
                        var moved = from + line * held;
                        translated = new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
                    }
                    if (projectile.Count > 1) fan = NativeVolley(new ProjectileProfile(projectile.Metadata), SkillStat).NativeVolley;
                }
                var arm = translated - start;
                double reach = projectile.EndsAtTarget ? arm.Length : MortarRange();
                ProjectileVector TargetOf(int i)
                {
                    if (arm.Length <= 0 || fan is null && projectile.EndsAtTarget) return translated;
                    var turned = start + arm.Unit.Rotate(fan?.Angle(i, projectile.Count, arm.Length) ?? 0) * reach;
                    return new(Math.Floor(turned.X + .5), Math.Floor(turned.Y + .5));
                }
                ProjectileVector LaunchOf(ProjectileVector target)
                {
                    if (!projectile.Lobbed || projectile.StartOffset == 0) return start;
                    // The whole column, along the actor's line to the target (+x where the two meet).
                    var line = (target - from).Length > 0 ? (target - from).Unit : new ProjectileVector(1, 0);
                    var moved = start + line * projectile.StartOffset;
                    return new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
                }
                // The use flies from the unturned launch; each landing is its own projectile's.
                origin = LaunchOf(translated);
                // The minimum range grows by the start offset only where the skill has one (0x141a10d50).
                int minimum = projectile.MinimumRange != 0 ? projectile.MinimumRange + projectile.StartOffset : 0;
                var draws = new float[projectile.Count];
                nativeTargets = new ProjectileVector[projectile.Count];
                for (int i = 0; i < projectile.Count; i++)
                {
                    var turned = TargetOf(i);
                    nativeTargets[i] = MonsterMortarTarget(from, LaunchOf(turned), turned, minimum, projectile.SpreadRadius,
                        sequence, id, i, projectile.DistanceOverride, out draws[i]);
                }
                // A spell mortar lands a fixed time after its release, at any distance: each projectile at its own speed.
                if (projectile.FixedFlight)
                    nativeSpeeds = [.. nativeTargets.Select((t, i) => Math.Max(.001, (t - origin).Length) / MonsterShots.MortarFlightSeconds(projectile, draws[i]))];
            }
            else if (projectile.AimTranslates) aim = translated;
            // A water vortex ends on its diamond, whatever the aim's distance.
            if (projectile.Recipe == "WaterVortex")
                nativeTargets = [WaterVortexTarget(from, aim, (int)(128 * (100 + SkillStat("spell_maximum_action_distance_+%")) / 100.0))];
            if (projectile.Recipe == "Knives")
            {
                // Ethereal knives (0x1419f4be0) hold the aim 60 cells from the caster's cell; every knife leaves from 30
                // units ahead of the cell's centre (0x141ba7c30), not from the clip's contact, and fans about there to the
                // aim's distance. Each knife's mover then jitters and stretches its end (ShadowKnifeEnd); the counter that
                // seeds it runs on across the action's releases.
                var cell = new ProjectileVector(Math.Floor(from.X), Math.Floor(from.Y));
                var line = (aim - cell).Unit;
                var held = cell + new ProjectileVector(Math.Floor(line.X * 60 + .5), Math.Floor(line.Y * 60 + .5));
                var centre = (cell + new ProjectileVector(.5, .5)) * WorldUnits.PerCell;
                var heldWorld = (held + new ProjectileVector(.5, .5)) * WorldUnits.PerCell;
                var spawn = centre + (heldWorld - centre).Unit * 30;
                var arm = heldWorld - spawn;
                ProjectileVector Cell(ProjectileVector world) => new(Math.Floor(world.X * .092), Math.Floor(world.Y * .092));
                origin = Cell(spawn);
                var fan = NativeVolley(new ProjectileProfile(projectile.Metadata), SkillStat).NativeVolley!;
                nativeTargets = new ProjectileVector[projectile.Count];
                for (int i = 0; i < projectile.Count; i++)
                    nativeTargets[i] = ShadowKnifeEnd(Cell(spawn + arm.Rotate(fan.Angle(i, projectile.Count, arm.Length / WorldUnits.PerCell))),
                        origin, sequence, (uint)(2 + ((release?.Ordinal ?? 0) * projectile.Count + i) % 254), 0, (float)MortarRange());
            }
        }
        // The server's own object turns by its contact too.
        if (!(projectile is { ClientDraws: true } && (!projectile.Lobbed || mortar))) TurnAim();
        // A server-created lobbed shot lands at its aim, no nearer the actor than its minimum range (cells). A spell
        // mortar the server draws lands where its mover would, measured along the line to the aim: the aim (capped at
        // its range) or, without UseTargetLocation, its range from the release (0x141ba99d0); then (0x141a10d50) pushed
        // out to the minimum range plus __col9 from the actor, or its distance override from the launch __col9 cells out
        // (a Doedre nova's ring around her, not on the player). Native mortars use their scattered destinations above.
        double landing = Math.Max(0.5, Math.Max((aim - origin).Length, projectile.MinimumRange - ahead));
        if (projectile is { Lobbed: true, ClientDraws: false } && mortar)
        {
            double offset = projectile.StartOffset, most = MortarRange();
            landing = projectile.EndsAtTarget ? Math.Min((aim - origin).Length, most) : most;
            if (projectile.MinimumRange != 0) landing = Math.Max(landing, projectile.MinimumRange + offset - ahead);
            if (projectile.DistanceOverride > 0) landing = offset + projectile.DistanceOverride;
            landing = Math.Max(0.5, landing);
        }
        if (projectile.Recipe == "RollingMagma") landing = Math.Max(0.5, MonsterShots.MagmaLanding(projectile, landing + ahead) - ahead);
        var target = projectile.Lobbed ? origin + heading * landing : aim;
        // A straight shot the client draws flies by the client's own rules (MonsterShots.ClientDrawnProfile: its range,
        // contact, volley and count), hidden; a mortar keeps its scattered landings; anything else is the server's object.
        // A spark's wander starts from the monster's own cell (0x141b35a10).
        if (projectile.Recipe == "Spark") origin = from;
        // A UseTargetLocation shot ends at the aimed cell; any other straight one flies past it.
        double straightRange = projectile.EndsAtTarget ? Math.Max(.001, (aim - origin).Length) : Math.Max(distance, a.Range) * 1.5 + 5;
        var profile = projectile is { ClientDraws: true, Lobbed: false } && !mortar
            ? MonsterShots.ClientDrawnProfile(conn, projectile, attack: !a.Spell, aim - origin, sequence, id,
                thrower: () => ProjectileSession.Vector(m.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond)),
                fromActor: aim - from) with
                {
                    // A lantern reaches its aim its clip's contact after the release, at any distance.
                    Speed = projectile.FixedFlight ? Math.Max(.001, (aim - origin).Length) / projectile.ContactSeconds : speed,
                }
            : projectile.Recipe is not null ? MonsterShots.RecipeProfile(projectile, speed, landing, attack: !a.Spell)
            : MonsterProjectileProfile(projectile, speed, landing, straightRange);
        if (nativeTargets is not null)
            profile = profile with { ContactCells = SkillsFor(conn).ProjectileContactCells(projectile.Metadata) };
        var owner = session.ActorKey(id, m);
        double flightCells = projectile.Lobbed ? landing : (aim - origin).Length;
        var presentation = MonsterProjectilePresentation(SkillsFor(conn).Clips(projectile.Metadata), flightCells / speed);
        if (release?.Point is { } launch) presentation = (presentation ?? new ProjectilePresentation()) with { LaunchHeight = launch.HeightSteps };
        Console.WriteLine($"[gateway] {session.remote} monster 0x{id:x} ({m.Label}) releases {projectile.Metadata} from {origin}"
            + $" ({ahead:0.0} cells toward the aim, {(release?.Point is { } up ? up.HeightSteps.ToString() : "default")} steps up"
            + $"{(release is null ? "" : $", clip {release.Clip} at {release.Fraction:0.000} of the attack{(release.FacesAway ? ", facing away" : "")}")})"
            + $" aim={aim}{(nativeTargets is { } landings ? $" lands at {string.Join(" ", landings.Select(t => $"({t.X:0},{t.Y:0})"))}"
                : projectile.Lobbed ? $" lands at {target} ({landing:0.0} cells)" : "")}");
        return new ProjectileUse(profile, owner, owner, 2, origin, target)
            { ProjectileTargets = nativeTargets, ProjectileSpeeds = nativeSpeeds, ReturnAnchor = profile.Returns ? owner : null,
                Payload = new HostileProjectileOffense(id, m, a, action, $"{m.Label} {projectile.Metadata}")
                    { Presentation = presentation, Sequence = sequence } };
    }

    /// <summary>The flight the server simulates for a monster's shot. A lobbed shot's range is its landing distance; a
    /// straight one flies <paramref name="straightRange"/> cells. A mortar attack the client draws (its record carries the
    /// row and count, MonsterProjectileSkillStats) gets no object, so the player sees one mortar, as from GGG, which creates
    /// none; the flight still lands its hit.</summary>
    public static ProjectileProfile MonsterProjectileProfile(SkillCatalog.MonsterProjectileInfo projectile, double speed,
        double landing, double straightRange) => new(projectile.Metadata)
    {
        Speed = speed, Radius = projectile.CollisionRadius, Count = Math.Max(1, projectile.Count), Lobbed = projectile.Lobbed,
        SpreadRadians = projectile.SpreadRadians,
        // A ring at the client's nova angles, i × 2π / count from the aim (0x141bbbf90).
        NativeVolley = projectile.SpreadRadians >= 2 * Math.PI ? new() { Nova = true } : null,
        Range = projectile.Lobbed ? landing : straightRange,
        DirectDamage = !projectile.Lobbed,
        EndArea = projectile.Lobbed ? new ProjectileAreaRecipe("monster mortar", MonsterShots.AreaRadius(projectile, MonsterMortarRadius())) : null,
        EndAreaReasons = projectile.Lobbed
            ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        HideVisual = projectile.ClientDraws,
        Pierce = projectile.PiercesAll ? -1 : 0,
    };

    /// <summary>A server-created projectile object plays nothing by itself. Start the rig's flight clip at spawn even
    /// when the .ao declares the same default animation: the explicit 0x1b2 is what guarantees the server object is
    /// drawn and animated (live 2026-10-31: the King's default-animation projectiles were static/invisible). A once-only
    /// clip is stretched over the flight (the mortar's 1 s <c>proj</c> over its arc:
    /// <c>mortar_projectile_scale_animation_speed_instead_of_projectile_speed</c>), and the impact clip plays where it
    /// stops, keeping the object for the clip's length. Null without clip data: the object then flies as its .ao
    /// presents it by itself.</summary>
    public static ProjectilePresentation? MonsterProjectilePresentation(SkillCatalog.ProjectileClips? clips, double flightSeconds)
    {
        if (clips is null || clips is { Flight: null, Impact: null }) return null;
        var presentation = new ProjectilePresentation();
        if (clips.Flight is { } flight)
            presentation = presentation with
            {
                FlightClip = flight.Index,
                FlightClipSpeed = flight.Once && flightSeconds > 0 ? (float)Math.Clamp(flight.Ms / 1000.0 / flightSeconds, 0.1, 10) : 1,
            };
        if (clips.Impact is { } impact)
            presentation = presentation with
            {
                ImpactClip = impact.Index,
                ImpactLifetime = ProjectileRuntime.Time(Math.Max(0.1, impact.Ms / 1000.0)),
            };
        return presentation;
    }

    // The release at the attack's contact: the flight owns contact and damage (HostileHit).
    // The AI action must still be current when the runtime releases it.
    public static void LaunchMonsterProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, MonsterAttack a, SkillCatalog.MonsterProjectileInfo projectile, int action,
        (int X, int Y)? orderedAim = null, double? releaseAt = null, ushort sequence = MonsterActionSequence)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        // A spiral the client draws: one step at a time from the monster's cell then, each turned from the aim the
        // first step took (MonsterShots.Spiral); the steps run on once released.
        if (projectile is { ClientDraws: true } && MonsterShots.Spiral(projectile) is { } spiral)
        {
            double first = releaseAt ?? session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            ProjectileVector? line = null;
            for (int k = 0; k < spiral.Steps; k++)
            {
                int step = k;
                double turn = spiral.Offset + step * spiral.Step;
                // The first step needs the action; the rest follow it once it has flown.
                session.Schedule(ProjectileRuntime.Time(first + step * spiral.Interval),
                    () => (step == 0 ? MonsterActionCurrent(conn, id, m, action) : line is not null) && !InGracePeriod(conn),
                    () =>
                    {
                        var use = MonsterProjectileUse(session, id, m, a, projectile, action, orderedAim: orderedAim, sequence: sequence);
                        line ??= use.Aim - use.Origin;
                        IReadOnlyList<double> arms = spiral.Both ? [turn, -turn] : [turn];
                        return use with
                        {
                            Aim = use.Origin + line.Value,
                            Profile = use.Profile with { Count = arms.Count, NativeVolley = new() { Angles = arms } },
                        };
                    });
            }
            return;
        }
        session.Schedule(releaseAt is { } at ? ProjectileRuntime.Time(at) : session.Runtime.Now,
            () => MonsterActionCurrent(conn, id, m, action) && !InGracePeriod(conn),
            () => MonsterProjectileUse(session, id, m, a, projectile, action, orderedAim: orderedAim, sequence: sequence));
    }

    // Called under the XP -> Monsters lock by the runtime. Only the player takes a monster's projectile; a friendly monster in
    // its way is passed through. The monster's damage pipeline (CommitMonsterHitLocked) owns evasion, block, armour,
    // ailments, recovery and the hit packet; the presentation follows through the session's ordered outbox.
    public static ProjectileHitResult HostileHit(ProjectileSession session, ProjectileDamage contact, HostileProjectileOffense shot)
    {
        var conn = session.Conn;
        // The flight's own damage multiplier (an ice spear's second form, a burst's or a bounce's) scales the attack's hit.
        if (contact.Multiplier != 1) shot = shot with { Attack = shot.Attack with { Damage = shot.Attack.Damage.Scale(contact.Multiplier) } };
        if (OwnedFighter(conn, contact.Target.Id) is { } golem)
        {
            bool landed = GolemTakeHitLocked(conn, golem, shot.MonsterId, shot.Monster, shot.Attack,
                Math.Max(AreaTime(conn), contact.At / (double)ProjectileRuntime.QuantaPerSecond), area: shot.Attack.Projectile?.Lobbed == true);
            session.Enqueue(() => PublishMinionOutcomes(session.remote, session.stream, session.ciphers, conn));
            return landed ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        }
        if (contact.Target.Id != PlayerId || conn.PlayerLife == 0 || InGracePeriod(conn)) return ProjectileHitResult.Miss;
        var outcome = CommitHostilePlayerHitLocked(session, contact, shot);
        if (outcome is null) return ProjectileHitResult.Miss;
        session.World(shot.MonsterId, outcome.HitPacket, $"0x01af monster 0x{shot.MonsterId:x} projectile hit on the player ({shot.Label})");
        session.Enqueue(() => PresentMonsterHit(session.remote, session.stream, session.ciphers, conn, shot.MonsterId, outcome));
        return outcome.Blocked ? ProjectileHitResult.Blocked : ProjectileHitResult.Hit;
    }

    public static MonsterHitOutcome? CommitHostilePlayerHitLocked(ProjectileSession session, ProjectileDamage contact, HostileProjectileOffense shot)
    {
        var conn = session.Conn;
        // The hit pipeline's timers (fortification, recent hits, ailments, evasion entropy) run on the connection clock and
        // refuse to go backwards. The contact's simulated instant lies a few milliseconds behind that clock, which other
        // reads (a stats refresh, a melee swing) may already have advanced, so the hit lands at whichever is later. The
        // runtime's fixtures run their simulation ahead of the clock; there the contact time wins.
        double now = Math.Max(contact.At / (double)ProjectileRuntime.QuantaPerSecond, conn.Clock.Elapsed.TotalSeconds);
        var here = conn.Walk.Position(now);
        var from = ((int)Math.Round(contact.Position.X), (int)Math.Round(contact.Position.Y));
        return CommitMonsterHitLocked(session.remote, conn, shot.MonsterId, shot.Monster, shot.Attack, now, here, from,
            checkRange: false, sequence: shot.Sequence);
    }
}

/// <summary>A monster's projectile attack in flight: the monster that released it, its attack and the AI action it belongs
/// to. The player is its only target (<c>Game.HostileHit</c>); the monster's own damage pipeline commits the hit when
/// the flight reaches the player, or, for a mortar, when it lands on them.</summary>
public sealed record HostileProjectileOffense(uint MonsterId, SpawnedMonster Monster, MonsterAttack Attack, int Action, string Label)
{
    /// <summary>The action that released this shot, retained for the client's hit lookup (0x141e977f0).</summary>
    public ushort Sequence { get; init; } = Game.MonsterActionSequence;
    /// <summary>The rig clips the object needs to be seen (Game.MonsterProjectilePresentation).</summary>
    public Game.ProjectilePresentation? Presentation { get; init; }
}

/// <summary>A shot of a server object that is no monster (a Trial of Ascendancy's dart trap). Its own rule commits a
/// contact, under the connection's lock as <c>Game.HostileHit</c> is; a miss lets the flight go on.</summary>
public sealed record ObjectProjectileOffense(string Label, Func<Game.ProjectileSession, ProjectileDamage, ProjectileHitResult> Hit)
{
    /// <summary>The rig clips the object needs to be seen (Game.MonsterProjectilePresentation).</summary>
    public Game.ProjectilePresentation? Presentation { get; init; }
    /// <summary>No release, visual or end lines in the log: the shot's own rule logs what matters (its hits).</summary>
    public bool Quiet { get; init; }
}
