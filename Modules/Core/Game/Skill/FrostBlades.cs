using GameServer.Core.Projectiles;

static partial class Game
{
    internal static bool IsFrostBlades(int effect) => Skills.EffectId(effect) == "FrostBlades";

    static int FrostBladesReach(Player player, IReadOnlyDictionary<uint, int> stats) =>
        ActorScale.Saturate((long)PlayerStatValues(player).GetValueOrDefault(Stat.MeleeWeaponRangePlus)
            + stats.GetValueOrDefault(Stat.MeleeWeaponRangePlus));

    static ProjectileUse FrostBladesVolley(Player player, ProjectileUse use, IReadOnlyDictionary<uint, int> stats,
        double now, (int X, int Y) from, (int X, int Y) aim, ProjectileStrikeContact contact, double more)
    {
        // Each accepted strike releases its blades from the enemy it hit.
        var origin = ProjectileSession.Vector(contact.At);
        var shot = (ProjectileOffense)use.Payload!;
        var fromVec = ProjectileSession.Vector(from);
        var delta = origin - fromVec;
        var forward = (delta.Length >= 1e-6 ? delta : ProjectileSession.Vector(aim) - fromVec).Unit;
        double heading = Math.Atan2(forward.Y, forward.X);
        double half = Math.Clamp(stats.GetValueOrDefault(Stat.TotalProjectileSpreadAngleOverride), 0, 360) * Math.PI / 360;
        double Offset(ProjectileVector point)
        {
            var pointDelta = point - origin;
            return Math.IEEERemainder(Math.Atan2(pointDelta.Y, pointDelta.X) - heading, Math.Tau);
        }
        var angles = player.Area.MonstersNear(contact.At, use.Profile.Range)
            .Where(pair => pair.Key != contact.Target && pair.Value is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false })
            .Select(pair => ProjectileSession.Vector(pair.Value.Walk.Position(now)))
            .Where(point => (point - origin).Length <= use.Profile.Range && Math.Abs(Offset(point)) <= half)
            .OrderBy(point => (point - origin).Length)
            .Select(Offset).Take(use.Profile.Count).ToList();
        int free = use.Profile.Count - angles.Count;
        for (int i = 0; i < free; i++)
            angles.Add(free == 1 ? 0 : -half + 2 * half * i / (free - 1));

        return use with
        {
            Origin = origin,
            Aim = origin + forward,
            Profile = use.Profile with { NativeVolley = new() { Angles = [.. angles] } },
            Payload = shot.ScaleDamage(more) with { ExcludedTarget = contact.Excluded },
        };
    }
}
