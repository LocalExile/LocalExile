using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class StationaryCollisionTests
{
    [Fact]
    public void Every_supported_npc_has_metadata_and_blocking_npcs_offer_a_safe_exit()
    {
        var npcs = NpcCatalog.Load().Rows.Where(n => n.Supported).ToArray();
        Assert.NotEmpty(npcs);
        foreach (var npc in npcs)
        {
            var profile = StationaryCollision.For(npc.Id);
            var (size, hull) = StationaryCollision.Resolve(npc.Id, npc.Model);
            if (!profile.Blocking && hull is null) { Assert.Equal(-1, size); continue; }
            ClientPathfinder.Blocker blocker = new(50, 50, size) { CollisionRadius = hull };
            var exit = CollisionEscape.Find((50, 50), 150, 150, (_, _) => true, [blocker]);
            Assert.NotNull(exit);
            Assert.True(CollisionEscape.Clear(exit.Value, 150, 150, (_, _) => true, [blocker]), npc.Id);
        }
    }

}
