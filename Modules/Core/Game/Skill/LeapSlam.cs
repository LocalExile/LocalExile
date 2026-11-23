using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>The native LeapSlam action's landing geometry and animation phases.</summary>
public static class LeapSlam
{
    public const ushort Action = 0x9779;

    public readonly record struct Phases(string Animation, double Leave, double Land, double End);
    public sealed record Use((int X, int Y) From, (int X, int Y) Landing, double At, Phases Timing,
        int ActionToken, int InterruptionGeneration);

    // 0x1412fafd0: behaviour 2 has the shorter range; 0x1419fe630 rounds each scaled component.
    public static (int X, int Y) Target((int X, int Y) from, (int X, int Y) aim, int behaviour)
    {
        float dx = (float)aim.X - from.X, dy = (float)aim.Y - from.Y;
        float distance = Math.Max(0, MathF.Sqrt(dx * dx + dy * dy) - 1);
        float range = behaviour == 2 ? 30 : 60;
        if (distance <= range) return aim;
        float scale = range / distance;
        return (from.X + (int)MathF.Floor(dx * scale + .5f), from.Y + (int)MathF.Floor(dy * scale + .5f));
    }

    public static (int X, int Y) Landing(AreaLayout layout, IReadOnlySet<(int X, int Y)> barriers,
        (int X, int Y) from, (int X, int Y) aim, int behaviour)
    {
        var target = Target(from, aim, behaviour);
        foreach (var cell in ClientPathfinder.Line(target, from))
        {
            if (cell.X < 0 || cell.Y < 0 || cell.X >= layout.GridW || cell.Y >= layout.GridH
                || !layout.WalkableFor(cell.X, cell.Y, LivingCollision.PlayerSize) || barriers.Contains(cell)) continue;
            if (layout.Sight is { } sight
                && ClientPathfinder.LineBelow(from, cell, sight, LivingCollision.PlayerSize + 1)) continue;
            return cell;
        }
        return from;
    }

    // 0x141944380 seeks to leave_ground minus 0.01 animation seconds, before playback-rate scaling.
    public static Phases Timing(string animation, int lengthMs, int leaveMs, int hitMs, double seconds, bool skipStartup)
    {
        if (lengthMs <= 0 || leaveMs < 0 || hitMs < leaveMs || hitMs > lengthMs)
            throw new ArgumentOutOfRangeException(nameof(hitMs), "Leap animation events must be ordered within the clip.");
        double seek = skipStartup ? Math.Max(0, leaveMs - 10) : 0;
        return new(animation, (leaveMs - seek) / lengthMs * seconds,
            (hitMs - seek) / lengthMs * seconds, (lengthMs - seek) / lengthMs * seconds);
    }
}

public static partial class Game
{
    public static bool IsLeapSlam(Player player, int effect) => SkillsFor(player).ActionHash(effect) == LeapSlam.Action;

    public static LeapSlam.Phases LeapSlamTiming(Player player, ushort skillId, int effect, double seconds)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var weapon = AnimationWeaponProfileFor(player);
        var leave = Animations.SkillAnimationEventBounds(ao, weapon, "leapslam", "leave_ground");
        var hit = Animations.SkillAnimationEventBounds(ao, weapon, "leapslam", "hit_ground");
        if (leave is null || hit is null || leave.Value.Name != hit.Value.Name)
            throw new InvalidOperationException($"LeapSlam animation events are missing for {ao}.");
        // Character.ot enables animation skipping. This server does not grant instant_movement_skill_delay;
        // without that buff the native action skips startup on every eligible use, not on a guessed cooldown.
        return LeapSlam.Timing(leave.Value.Name, leave.Value.LengthMs, leave.Value.EarliestMs,
            hit.Value.EarliestMs, seconds, skipStartup: SkillsFor(player).BehaviourVariation(effect) != 2);
    }

    public static LeapSlam.Use? PlanLeapSlam(Player player, ushort skillId, int effect, (int X, int Y) aim, double at)
    {
        var from = player.Walk.Position(at);
        var landing = LeapSlam.Landing(player.Layout, player.Barrier, from, aim, SkillsFor(player).BehaviourVariation(effect));
        LeapSlam.Phases timing;
        try { timing = LeapSlamTiming(player, skillId, effect, SupportedAttackSeconds(player, skillId)); }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[gateway] Leap Slam unavailable: {ex.Message}");
            return null;
        }
        return new(from, landing, at, timing, player.ActionCount + 1, player.ActionInterruptionGeneration);
    }

    public static void CommitLeapSlam(Player player, LeapSlam.Use use)
    {
        player.Walk.Teleport(use.Landing.X, use.Landing.Y, use.At);
        player.LandingUntil = Math.Max(player.LandingUntil, use.At + use.Timing.End);
    }

    public static Task ScheduleLeapSlam(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        LeapSlam.Use use, SkillCriticalRoll criticalRoll, ushort sequence, double damageMore, double radiusMore)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        int epoch = player.ProjectileEpoch;
        bool Valid() => session.Active && player.ProjectileActive(epoch) && PlayerStateAllowsAction(player)
            && player.ActionInterruptionGeneration == use.InterruptionGeneration
            && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;
        session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(use.At + use.Timing.Land)), () =>
        {
            if (!Valid()) return;
            var own = SupportedSkill(player, skillId).Stats;
            LeapSlamDisplaceOccupants(player, use.Landing,
                SkillRadius(player, own, spell: false, effect: gem.Effect) * radiusMore,
                ProjectileWorldTime(player), own, use.From);
            int count = 0;
            var hit = AreaSkillHit(player, skillId, gem, use.Landing, use.Landing,
                immediate: true, sequence: sequence, criticalRoll: criticalRoll,
                damageMore: damageMore, radiusMore: radiusMore, triggeredCurrent: Valid,
                onHit: _ => count++, scheduledSession: session);
            if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Leap Slam landing must not yield.");
            hit.GetAwaiter().GetResult();
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: landed sequence=0x{sequence:x4} "
                + $"from=({use.From.X}, {use.From.Y}) at=({use.Landing.X}, {use.Landing.Y}), hit={count}, "
                + $"animation={use.Timing.Animation}, land={use.Timing.Land:0.000000}s (t={ProjectileWorldTime(player):0.000000})");
        });
        return Task.CompletedTask;
    }
}
