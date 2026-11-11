using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    /// <summary>Releases a player minion's catalogue projectile at its rig contact toward the hostile target captured
    /// by the accepted swing. The shared runtime owns flight, walls and moving-target contact; this payload only names
    /// the source-owned minion, and the existing minion damage/outcome pipeline owns every contact.</summary>
    internal static bool ReleaseMinionProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, PlayerMinions.Attack attack, SkillCatalog.MonsterProjectileInfo projectile, double now)
    {
        if (!MinionProjectileAttackCurrent(conn, attack)) return false;
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        ProjectileUse? released = null;
        session.Schedule(session.Runtime.Now, () => MinionProjectileAttackCurrent(conn, attack), () =>
        {
            released = MinionProjectileUse(session, attack, projectile, now);
            conn.Minions.Attacks.Remove(attack.Id);
            return released;
        });
        session.Advance();
        return released is not null;
    }

    static bool MinionProjectileAttackCurrent(ClientState conn, PlayerMinions.Attack attack)
    {
        var (id, m, _, targetId, targetMonster, _, _) = attack;
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
            || !conn.Minions.Fights(id) || !conn.Minions.Owns(id, m)
            || !conn.Minions.Attacks.TryGetValue(id, out var accepted) || !ReferenceEquals(accepted, attack)
            || m.Dead || m.Life == 0 || !conn.Monsters.TryGetValue(id, out var held) || !ReferenceEquals(held, m)
            || targetMonster.Dead || targetMonster.Life == 0 || !targetMonster.Hostile
            || !conn.Monsters.TryGetValue(targetId, out var target) || !ReferenceEquals(target, targetMonster)
            || conn.Beasts.Active is not { } active || active.Id != id || !ReferenceEquals(active.Monster, m)
            || !ReferenceEquals(active.Area, conn.Instance) || !ReferenceEquals(active.Lifetime, CombatLifetime))
            return false;
        ReconcileBeastCompanionsLocked(conn);
        return conn.Beasts.Active is { } current && ReferenceEquals(current, active)
            && BeastSourceGenerationCurrentLocked(conn, current.Source);
    }

    static ProjectileUse MinionProjectileUse(ProjectileSession session, PlayerMinions.Attack attack,
        SkillCatalog.MonsterProjectileInfo projectile, double now)
    {
        var (id, m, _, _, targetMonster, _, _) = attack;
        var conn = session.Conn;
        var from = ProjectileSession.Vector(m.Walk.Position(now));
        var aim = ProjectileSession.Vector(targetMonster.Walk.Position(now));
        double speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, _ => 0, _ => 0,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        double distance = Math.Max(1, (aim - from).Length);
        var toward = (aim - from).Unit;
        var release = m.Attack!.Release;
        double ahead = release?.Point is { } point ? point.ForwardCells * (release.FacesAway ? -1 : 1)
            : OrdinaryRangedAttack.ReleaseAheadCells;
        ahead = Math.Min(ahead, distance / 2);
        var origin = from + toward * ahead;
        var profile = new ProjectileProfile(projectile.Metadata)
        {
            Speed = speed,
            Radius = projectile.CollisionRadius,
            ContactCells = Math.Max(1, (int)Math.Ceiling(projectile.CollisionRadius)),
            Count = Math.Max(1, projectile.Count),
            SpreadRadians = projectile.SpreadRadians,
            Range = distance * 1.5 + 5,
        };
        var owner = session.ActorKey(id, m);
        double flightCells = (aim - origin).Length;
        var presentation = MonsterProjectilePresentation(SkillsFor(conn).Clips(projectile.Metadata), flightCells / speed);
        if (release?.Point is { } launch)
            presentation = (presentation ?? new ProjectilePresentation()) with { LaunchHeight = launch.HeightSteps };
        int accuracy = m.Mods.Effects.Accuracy(Monsters.BaseAccuracy(m.Level) ?? 0);
        return new ProjectileUse(profile, owner, owner, 1, origin, aim)
        {
            Payload = new MinionProjectileOffense(id, m, m.Attack!, accuracy, $"{m.Label} {projectile.Metadata}")
                { Presentation = presentation },
        };
    }

    internal static ProjectileHitResult MinionProjectileHit(ProjectileSession session, ProjectileDamage contact,
        MinionProjectileOffense shot)
    {
        var conn = session.Conn;
        if (!HasSourceValidBeastMinion(conn) || conn.Beasts.Active is not { } active
            || active.Id != shot.MinionId || !ReferenceEquals(active.Monster, shot.Monster)
            || !conn.Monsters.TryGetValue(contact.Target.Id, out var target))
            return ProjectileHitResult.Miss;
        if (session.ActorKey(contact.Target.Id, target) != contact.Target) return ProjectileHitResult.Miss;
        double now = Math.Max(contact.At / (double)ProjectileRuntime.QuantaPerSecond,
            conn.Clock.Elapsed.TotalSeconds);
        var landed = MinionHitTargetLocked(conn, shot.MinionId, shot.Monster, contact.Target.Id, target, now,
            contact.Projectile.RandomSource, checkReach: false, shot.Attack, shot.Accuracy);
        if (landed is not { } accepted) return ProjectileHitResult.Miss;
        var hit = accepted.Hit;
        if (hit.StageStarts)
        {
            CommitBossStage(session.remote, session.stream, session.ciphers, conn, contact.Target.Id, target, now);
            hit = hit with { StageStarts = false };
        }
        var to = target.Walk.Position(now);
        int fromX = (int)Math.Round(contact.Position.X), fromY = (int)Math.Round(contact.Position.Y);
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(shot.MinionId, 0xffff, contact.Target.Id,
                flags: 0x4601, kind: 0x02, u34: 450, angle: HitAngle(to.X - fromX, to.Y - fromY)),
            $"0x01af minion 0x{shot.MinionId:x} projectile hit on monster 0x{contact.Target.Id:x}",
            contact.Target.Id, target));
        CommitMinionHitLocked(conn, contact.Target.Id, target, hit, accepted.Damage, now,
            $"minion 0x{shot.MinionId:x} ({shot.Monster.Label})");
        session.Enqueue(() => PublishMinionOutcomesFor(session.remote, session.stream, session.ciphers, conn,
            CombatLifetime));
        return ProjectileHitResult.Hit;
    }
}

/// <summary>A released player-minion projectile. It keeps the accepted monster identity but no action token: a later
/// swing cannot cancel a flight already in the shared runtime.</summary>
sealed record MinionProjectileOffense(uint MinionId, SpawnedMonster Monster, MonsterAttack Attack, int Accuracy,
    string Label)
{
    public Game.ProjectilePresentation? Presentation { get; init; }
}
