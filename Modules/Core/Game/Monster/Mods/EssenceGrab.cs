using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Envy's grasping-hand grab (<c>EssenceDaemonGrab</c>): the demon-grasp circle and hand appear where the target
/// stood; after the windup a player still inside the circle is pulled to the rare — stepping out evades it (the wiki). Kept
/// apart from the other essence spells so a change here cannot disturb them.</summary>
public static partial class MapEssences
{
    const string GrabCircleArt = "Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp_circle.ao";
    const string GrabHandArt = "Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp.ao";
    const string GrabEndArt = "Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp_end.ao";

    /// <summary>The grab: the demon-grasp circle appeared where the target stood at the cast; after its windup, a target
    /// still inside it is pulled to the rare, one who stepped out is not (the wiki). The teleport is the gate's own (0x01b7).</summary>
    static async Task Grab(Player conn, SpawnedMonster monster, Player target, (int X, int Y) trapAt, double trapRadius)
    {
        if (target.Immune || InGracePeriod(target)) return;
        double now = AreaTime(target);
        if (Dist(target.Walk.Position(now), trapAt) > trapRadius) return;
        var at = monster.Walk.Position(now);
        using (ProjectileMutation(target))
            target.Walk.Teleport(at.X, at.Y, ProjectileWorldTime(target));
        await SendWorldRecord(target, target.Id, InstanceProtocol.Teleport(target.Id, at.X, at.Y),
            $"essence grab to ({at.X}, {at.Y})");
    }
}