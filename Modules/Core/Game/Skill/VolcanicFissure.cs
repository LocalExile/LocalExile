using GameServer.Core.Projectiles;

// A bounded ground sweep followed by the eruption. The server approximates the strip as a straight segment.
public static class VolcanicFissureSweep
{
    public static (int X, int Y) End(AreaLayout layout, (int X, int Y) from, (int X, int Y) aim, int radius)
    {
        var direction = GroundSlamWave.Direction(from, aim);
        double distance = Math.Min(Game.Dist(from, aim), GroundSlamWave.Length(radius));
        var last = from;
        for (int i = 1; i <= Math.Ceiling(distance); i++)
        {
            double step = Math.Min(i, distance);
            var next = (X: (int)Math.Round(from.X + direction.X * step), Y: (int)Math.Round(from.Y + direction.Y * step));
            if (!layout.WalkableFor(next.X, next.Y, 0)) break;
            last = next;
        }
        return last;
    }

    public static double ContactDistance((int X, int Y) from, (int X, int Y) end, (int X, int Y) target, int radius)
    {
        var origin = new ProjectileVector(from.X, from.Y);
        var path = new ProjectileVector(end.X, end.Y) - origin;
        return ContactDistance(origin, path.Unit, 0, path.Length, target, radius);
    }

    internal static double ContactDistance(ProjectileVector origin, ProjectileVector direction, double start, double end,
        (int X, int Y) target, int radius)
    {
        var relative = new ProjectileVector(target.X, target.Y) - origin;
        double along = Math.Clamp(relative.Dot(direction), start, end);
        var offset = relative - direction * along;
        return offset.Dot(offset) <= (double)radius * radius ? along : double.PositiveInfinity;
    }
}

public static partial class Game
{
    static bool IsVolcanicFissure(int effect) => Skills.EffectId(effect) == "VolcanicFissure";

    static void ScheduleFissureEruption(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) end, double now, int epoch, SkillCriticalRoll roll,
        ushort sequence, int repeat, double more, bool cryExerted, bool seismicExerted, double radiusMore, int? rallyingDamage)
    {
        var origin = ProjectileSession.Vector(end);
        var direction = (origin - ProjectileSession.Vector(from)).Unit;
        var aim = direction.Length > 0 ? origin + direction * 20 : origin + new ProjectileVector(20, 0);
        var target = ((int)Math.Round(aim.X), (int)Math.Round(aim.Y));
        var use = CaptureGemProjectile(player, session, skillId, gem, target, roll, sequence,
            cryExerted: cryExerted, repeat: repeat, otherWarcryExertions: seismicExerted ? 1 : 0, rallyingDamage: rallyingDamage);
        var shot = (ProjectileOffense)use.Payload!;
        var profile = use.Profile with
        {
            NativeVolley = new() { Angles = RandomVolleyAngles(sequence, use.Profile.Count, player.Id, aim - origin, (uint)repeat) },
            EndArea = use.Profile.EndArea is { } area ? area with { Radius = area.Radius * radiusMore } : null
        };
        session.Schedule(ProjectileRuntime.Time(now + GroundSlamWave.AdvanceSeconds(Dist(from, end))),
            () => player.ProjectileActive(epoch) && player.PlayerLife > 0,
            () => use with
            {
                Origin = origin, Aim = aim, Profile = profile,
                Payload = shot with
                {
                    Hit = shot.Hit.Scale(more), AreaHit = shot.AreaHit?.Scale(more)
                }
            });
    }
}
