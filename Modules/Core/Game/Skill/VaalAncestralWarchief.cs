using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string VaalAncestralWarchiefEffect = "VaalAncestralWarchief";

    static bool VaalWarchiefInRange(Player player, TotemState.Totem totem, double now)
    {
        if (!TotemSources.TryGetValue(totem, out var source)) return false;
        int range = GolemStat(source.Stats, Stat.AncestorTotemParentActiviationRange);
        return range > 0 && Dist(totem.At, player.Walk.Position(now)) <= range;
    }

    static void ReleaseVaalWarchiefSlam(Player player, ProjectileSession session, TotemState.Totem totem,
        (int X, int Y) aim, ushort sequence)
    {
        double now = ProjectileWorldTime(player);
        if (!VaalWarchiefInRange(player, totem, now)) return;
        var skill = SupportedSkill(player, totem.SkillId);
        int radius = SkillRadius(player, skill.Stats, spell: false, totem.Effect);
        var from = totem.At;
        var direction = GroundSlamWave.Direction(from, aim);
        double reach = GroundSlamWave.Length(radius);
        if (Dist(from, aim) > reach)
        {
            int clearance = Math.Max(2, totem.BodySize + 2);
            var landing = (X: (int)Math.Round(aim.X - direction.X * clearance),
                Y: (int)Math.Round(aim.Y - direction.Y * clearance));
            if (player.Layout.WalkableFor(landing.X, landing.Y, totem.BodySize) && !player.Barrier.Contains(landing))
            {
                totem.Relocate(landing);
                player.SetObjectBlocker(totem.Id, landing.X, landing.Y, totem.BodySize, true);
                session.World(totem.Id, InstanceProtocol.Teleport(totem.Id, landing.X, landing.Y));
            }
        }

        var centre = totem.At;
        double angle = GroundSlamWave.HalfAngle(skill.Stats.GetValueOrDefault);
        TotemNovaLocked(player, session, totem, centre, reach, sequence, covers: at =>
            Dist(centre, at) <= radius || GroundSlamWave.Covers(direction, centre, at, angle, reach));
    }
}
