using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Development fallback for lobbed skills without a radius stat, in cells. The Sand Spitter's native straight
    // projectile uses direct contact and never this fallback.
    internal const double MonsterMortarRadiusCells = 10;

    internal static double MonsterMortarRadius() =>
        double.TryParse(Environment.GetEnvironmentVariable("Game_MONSTER_MORTAR_RADIUS"), out double radius) && radius > 0
            ? radius : MonsterMortarRadiusCells;

    /// <summary>The use for a monster's ranged attack released now: native straight mortars retain their ordered aim;
    /// other shots target the player's current cell. A
    /// mortar lands there and damages the area; a straight shot flies on past it. Team 2, so only the player (and friendly
    /// actors) are in its way, never the other monsters. A monster's action releases a projectile of the client's own only
    /// with a count on its skill record (the mortar attack's release, 0x141ba7c30): a <c>monster_mortar_attack</c>
    /// record carries one and the client draws it (MonsterProjectileProfile); any other shot's, a spell mortar's included,
    /// carries none, and the server's object is the projectile the player sees.</summary>
    internal static ProjectileUse MonsterProjectileUse(ProjectileSession session, uint id, SpawnedMonster m, MonsterAttack a,
        SkillCatalog.MonsterProjectileInfo projectile, int action, uint? aimedActor = null,
        (int X, int Y)? orderedAim = null, ushort sequence = MonsterActionSequence)
    {
        var conn = session.Conn;
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var from = ProjectileSession.Vector(m.Walk.Position(now));
        var aim = ProjectileSession.Vector(orderedAim ?? (aimedActor is uint targetId && OwnedFighter(conn, targetId) is { } golem
            ? golem.Monster.Walk.Position(now) : conn.Walk.Position(now)));
        double speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, _ => 0, _ => 0,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        double distance = Math.Max(1, (aim - from).Length);
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
        ProjectileVector[]? nativeTargets = null;
        if (projectile is { Variation: > 0, Lobbed: false })
        {
            // The contact offset is rotated with the facing model, then quantized to Positioned cells.
            // maintain_projectile_direction_when_using_contact_position translates the aim by the same offset.
            double lateral = release?.Point is { } contact ? contact.X / WorldUnits.PerCell * (release.FacesAway ? 1 : -1) : 0;
            origin += new ProjectileVector(toward.Y, -toward.X) * lateral;
            origin = new(Math.Floor(origin.X + .5), Math.Floor(origin.Y + .5));
            var translated = aim + origin - from;
            nativeTargets = Enumerable.Range(0, projectile.Count).Select(i => MonsterMortarTarget(from, origin, translated,
                projectile.MinimumRange, projectile.SpreadRadius, sequence, id, i)).ToArray();
        }
        // Retain the legacy landing model for server-created lobbed skills. Native straight mortar attacks use
        // their cell-based minimum range and scattered destinations above, not this untraced lob model.
        double landing = Math.Max(0.5, Math.Max((aim - origin).Length, projectile.MinimumRange / WorldUnits.PerCell - ahead));
        var target = projectile.Lobbed ? origin + toward * landing : aim;
        var profile = MonsterProjectileProfile(projectile, speed, landing, Math.Max(distance, a.Range) * 1.5 + 5);
        if (nativeTargets is not null)
            profile = profile with { ContactCells = SkillsFor(conn).ProjectileContactCells(projectile.Metadata) };
        var owner = session.ActorKey(id, m);
        double flightCells = projectile.Lobbed ? landing : (aim - origin).Length;
        var presentation = MonsterProjectilePresentation(SkillsFor(conn).Clips(projectile.Metadata), flightCells / speed);
        if (release?.Point is { } launch) presentation = (presentation ?? new ProjectilePresentation()) with { LaunchHeight = launch.HeightSteps };
        Console.WriteLine($"[gateway] {session.remote} monster 0x{id:x} ({m.Label}) releases {projectile.Metadata} from {origin}"
            + $" ({ahead:0.0} cells toward the aim, {(release?.Point is { } up ? up.HeightSteps.ToString() : "default")} steps up"
            + $"{(release is null ? "" : $", clip {release.Clip} at {release.Fraction:0.000} of the attack{(release.FacesAway ? ", facing away" : "")}")})"
            + $" aim={aim}{(projectile.Lobbed ? $" lands at {target} ({landing:0.0} cells)" : "")}");
        return new ProjectileUse(profile, owner, owner, 2, origin, target)
            { ProjectileTargets = nativeTargets,
                Payload = new HostileProjectileOffense(id, m, a, action, $"{m.Label} {projectile.Metadata}") { Presentation = presentation } };
    }

    /// <summary>The flight the server simulates for a monster's shot. A lobbed shot's range is its landing distance; a
    /// straight one flies <paramref name="straightRange"/> cells. A mortar attack the client draws (its record carries the
    /// row and count, MonsterProjectileSkillStats) gets no object, so the player sees one mortar, as from GGG, which creates
    /// none; the flight still lands its hit.</summary>
    internal static ProjectileProfile MonsterProjectileProfile(SkillCatalog.MonsterProjectileInfo projectile, double speed,
        double landing, double straightRange) => new(projectile.Metadata)
    {
        Speed = speed, Radius = projectile.CollisionRadius, Count = Math.Max(1, projectile.Count), Lobbed = projectile.Lobbed,
        SpreadRadians = projectile.SpreadRadians,
        Range = projectile.Lobbed ? landing : straightRange,
        DirectDamage = !projectile.Lobbed,
        EndArea = projectile.Lobbed ? new ProjectileAreaRecipe("monster mortar", projectile.AreaRadius > 0 ? projectile.AreaRadius : MonsterMortarRadius()) : null,
        EndAreaReasons = projectile.Lobbed
            ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        HideVisual = projectile.Variation > 0,
    };

    /// <summary>A server-created projectile object plays nothing by itself. Start the rig's flight clip at spawn even
    /// when the .ao declares the same default animation: the explicit 0x1b2 is what guarantees the server object is
    /// drawn and animated (live 2026-10-31: the King's default-animation projectiles were static/invisible). A once-only
    /// clip is stretched over the flight (the mortar's 1 s <c>proj</c> over its arc:
    /// <c>mortar_projectile_scale_animation_speed_instead_of_projectile_speed</c>), and the impact clip plays where it
    /// stops, keeping the object for the clip's length. Null without clip data: the object then flies as its .ao
    /// presents it by itself.</summary>
    internal static ProjectilePresentation? MonsterProjectilePresentation(SkillCatalog.ProjectileClips? clips, double flightSeconds)
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
    internal static void LaunchMonsterProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, MonsterAttack a, SkillCatalog.MonsterProjectileInfo projectile, int action,
        (int X, int Y)? orderedAim = null, double? releaseAt = null, ushort sequence = MonsterActionSequence)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        session.Schedule(releaseAt is { } at ? ProjectileRuntime.Time(at) : session.Runtime.Now,
            () => MonsterActionCurrent(conn, id, m, action) && !InGracePeriod(conn),
            () => MonsterProjectileUse(session, id, m, a, projectile, action, orderedAim: orderedAim, sequence: sequence));
    }

    // Called under the XP -> Monsters lock by the runtime. Only the player takes a monster's projectile; a friendly monster in
    // its way is passed through. The monster's damage pipeline (CommitMonsterHitLocked) owns evasion, block, armour,
    // ailments, recovery and the hit packet; the presentation follows through the session's ordered outbox.
    internal static ProjectileHitResult HostileHit(ProjectileSession session, ProjectileDamage contact, HostileProjectileOffense shot)
    {
        var conn = session.Conn;
        if (OwnedFighter(conn, contact.Target.Id) is { } golem)
        {
            bool landed = GolemTakeHitLocked(conn, golem, shot.MonsterId, shot.Monster, shot.Attack,
                Math.Max(AreaTime(conn), contact.At / (double)ProjectileRuntime.QuantaPerSecond), area: shot.Attack.Projectile?.Lobbed == true);
            session.Enqueue(() => PublishMinionOutcomes(session.remote, session.stream, session.ciphers, conn));
            return landed ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        }
        if (contact.Target.Id != PlayerId || conn.PlayerLife == 0 || InGracePeriod(conn)) return ProjectileHitResult.Miss;
        // The hit pipeline's timers (fortification, recent hits, ailments, evasion entropy) run on the connection clock and
        // refuse to go backwards. The contact's simulated instant lies a few milliseconds behind that clock, which other
        // reads (a stats refresh, a melee swing) may already have advanced, so the hit lands at whichever is later. The
        // runtime's fixtures run their simulation ahead of the clock; there the contact time wins.
        double now = Math.Max(contact.At / (double)ProjectileRuntime.QuantaPerSecond, conn.Clock.Elapsed.TotalSeconds);
        var here = conn.Walk.Position(now);
        var from = ((int)Math.Round(contact.Position.X), (int)Math.Round(contact.Position.Y));
        var outcome = CommitMonsterHitLocked(session.remote, conn, shot.MonsterId, shot.Monster, shot.Attack, now, here, from, checkRange: false);
        if (outcome is null) return ProjectileHitResult.Miss;
        session.Packet(outcome.HitPacket, $"0x01af monster 0x{shot.MonsterId:x} projectile hit on the player ({shot.Label})");
        session.Enqueue(() => PresentMonsterHit(session.remote, session.stream, session.ciphers, conn, shot.MonsterId, outcome));
        return outcome.Blocked ? ProjectileHitResult.Blocked : ProjectileHitResult.Hit;
    }
}

/// <summary>A monster's projectile attack in flight: the monster that released it, its attack and the AI action it belongs
/// to. The player is its only target (<c>Game.HostileHit</c>); the monster's own damage pipeline commits the hit when
/// the flight reaches the player, or, for a mortar, when it lands on them.</summary>
sealed record HostileProjectileOffense(uint MonsterId, SpawnedMonster Monster, MonsterAttack Attack, int Action, string Label)
{
    /// <summary>The rig clips the object needs to be seen (Game.MonsterProjectilePresentation).</summary>
    public Game.ProjectilePresentation? Presentation { get; init; }
}

/// <summary>A shot of a server object that is no monster (a Trial of Ascendancy's dart trap). Its own rule commits a
/// contact, under the connection's lock as <c>Game.HostileHit</c> is; a miss lets the flight go on.</summary>
sealed record ObjectProjectileOffense(string Label, Func<Game.ProjectileSession, ProjectileDamage, ProjectileHitResult> Hit)
{
    /// <summary>The rig clips the object needs to be seen (Game.MonsterProjectilePresentation).</summary>
    public Game.ProjectilePresentation? Presentation { get; init; }
    /// <summary>No release, visual or end lines in the log: the shot's own rule logs what matters (its hits).</summary>
    public bool Quiet { get; init; }
}
