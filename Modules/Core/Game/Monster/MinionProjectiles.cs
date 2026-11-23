using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Releases a player minion's catalogue projectile at its rig contact toward the hostile target captured
    /// by the accepted swing. The shared runtime owns flight, walls and moving-target contact; this payload only names
    /// the source-owned minion, and the existing minion damage/outcome pipeline owns every contact.</summary>
    public static bool ReleaseMinionProjectile(Player player, PlayerMinions.Attack attack, SkillCatalog.MonsterProjectileInfo projectile, double now)
    {
        if (!MinionProjectileAttackCurrent(player, attack)) return false;
        var session = ProjectilesFor(player);
        session.Advance();
        ProjectileUse? released = null;
        session.Schedule(session.Runtime.Now, () => MinionProjectileAttackCurrent(player, attack), () =>
        {
            released = MinionProjectileUse(session, attack, projectile, now);
            player.Minions.Attacks.Remove(attack.Id);
            return released;
        });
        session.Advance();
        return released is not null;
    }

    static bool MinionProjectileAttackCurrent(Player player, PlayerMinions.Attack attack)
    {
        var (id, m, _, targetId, targetMonster, _, _) = attack;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !player.Minions.Fights(id) || !player.Minions.Owns(id, m)
            || !player.Minions.Attacks.TryGetValue(id, out var accepted) || !ReferenceEquals(accepted, attack)
            || m.Dead || m.Life == 0 || !player.Area.Monsters.TryGetValue(id, out var held) || !ReferenceEquals(held, m)
            || targetMonster.Dead || targetMonster.Life == 0 || !targetMonster.Hostile
            || !player.Area.Monsters.TryGetValue(targetId, out var target) || !ReferenceEquals(target, targetMonster)
            || player.Beasts.Active is not { } active || active.Id != id || !ReferenceEquals(active.Monster, m)
            || !ReferenceEquals(active.Area, player.Instance) || !ReferenceEquals(active.Lifetime, CombatLifetime))
            return false;
        ReconcileBeastCompanionsLocked(player);
        return player.Beasts.Active is { } current && ReferenceEquals(current, active)
            && BeastSourceGenerationCurrentLocked(player, current.Source);
    }

    static ProjectileUse MinionProjectileUse(ProjectileSession session, PlayerMinions.Attack attack,
        SkillCatalog.MonsterProjectileInfo projectile, double now)
    {
        var (id, m, _, _, targetMonster, _, _) = attack;
        var player = session.Player;
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
        var presentation = MonsterProjectilePresentation(SkillsFor(player).Clips(projectile.Metadata), flightCells / speed);
        if (release?.Point is { } launch)
            presentation = (presentation ?? new ProjectilePresentation()) with { LaunchHeight = launch.HeightSteps };
        int accuracy = m.Mods.Effects.Accuracy(Monsters.BaseAccuracy(m.Level) ?? 0);
        return new ProjectileUse(profile, owner, owner, 1, origin, aim)
        {
            Payload = new MinionProjectileOffense(id, m, m.Attack!, accuracy, $"{m.Label} {projectile.Metadata}")
                { Presentation = presentation },
        };
    }

    public static ProjectileHitResult MinionProjectileHit(ProjectileSession session, ProjectileDamage contact,
        MinionProjectileOffense shot)
    {
        var player = session.Player;
        if (!HasSourceValidBeastMinion(player) || player.Beasts.Active is not { } active
            || active.Id != shot.MinionId || !ReferenceEquals(active.Monster, shot.Monster)
            || !player.Area.Monsters.TryGetValue(contact.Target.Id, out var target))
            return ProjectileHitResult.Miss;
        if (session.ActorKey(contact.Target.Id, target) != contact.Target) return ProjectileHitResult.Miss;
        double now = Math.Max(contact.At / (double)ProjectileRuntime.QuantaPerSecond,
            AreaTime(player));
        var landed = MinionHitTargetLocked(player, shot.MinionId, shot.Monster, contact.Target.Id, target, now,
            contact.Projectile.RandomSource, checkReach: false, shot.Attack, shot.Accuracy);
        if (landed is not { } accepted) return ProjectileHitResult.Miss;
        var hit = accepted.Hit;
        if (hit.StageStarts)
        {
            CommitBossStage(player, contact.Target.Id, target, now);
            hit = hit with { StageStarts = false };
        }
        var to = target.Walk.Position(now);
        int fromX = (int)Math.Round(contact.Position.X), fromY = (int)Math.Round(contact.Position.Y);
        // Immediate kind 3 reaches the attacker's native result consumer; 0xffff never supplies a consuming action.
        player.Minions.OutputQueue.Add(new(MinionActionHit(shot.MinionId, contact.Target.Id, accepted.HitCode,
                HitAngle(to.X - fromX, to.Y - fromY)),
            $"0x01af minion 0x{shot.MinionId:x} projectile hit on monster 0x{contact.Target.Id:x}",
            contact.Target.Id, target, WorldId: shot.MinionId));
        CommitMinionHitLocked(player, contact.Target.Id, target, hit, accepted.Damage, now,
            $"minion 0x{shot.MinionId:x} ({shot.Monster.Label})", source: shot.Monster, skill: active.SkillStats);
        session.Enqueue(() => PublishMinionOutcomesFor(player,
            CombatLifetime));
        return ProjectileHitResult.Hit;
    }
}

/// <summary>A released player-minion projectile. It keeps the accepted monster identity but no action token: a later
/// swing cannot cancel a flight already in the shared runtime.</summary>
public sealed record MinionProjectileOffense(uint MinionId, SpawnedMonster Monster, MonsterAttack Attack, int Accuracy,
    string Label)
{
    public Game.ProjectilePresentation? Presentation { get; init; }
}
