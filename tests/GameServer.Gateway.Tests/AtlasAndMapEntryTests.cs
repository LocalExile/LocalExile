using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class AtlasAndMapEntryTests
{
    [Fact]
    public void AreaInstance_and_Atlas_protocol_packets_encode_exact_client_wire_layouts()
    {
        byte[] plain = new AreaInstance(1, 0x235c, 7, 2).Encode();
        byte[] hideout = new AreaInstance(1, 0x235c, 7, 2, HideoutPermissions: true).Encode();
        Assert.Equal(plain.Length, hideout.Length);
        // Header before flags: 2 id + 4 instance + 2 area + 2 empty WStr + 4 seed + 4 bytes = 18; flags at [18..20].
        Assert.Equal(0x80, hideout[19] ^ plain[19]);

        byte[] delve = new AreaInstance(1, InstanceDelve.MineArea, 7, 68, ReturnAreaHash: 0x8b9b).Encode();
        Assert.Equal(plain.Length + 2, delve.Length);
        Assert.Equal(0x08, delve[18] & 0x08);
        // Level [21], B1d [22], B28 [23], then u16 BE ReturnAreaHash at [24..25].
        Assert.Equal(0x8b, delve[24]);
        Assert.Equal(0x9b, delve[25]);

        byte[] names = InstanceEntities.AtlasTreeNames("Boss", "Delve", "");
        Assert.Equal("003C" + "0004" + "426F7373" + "0005" + "44656C7665" + "0000", Convert.ToHexString(names));

        byte[] renameWire = new LoginWriter().U16(InstanceEntities.AtlasTreeRenameId).U8(1).WStr("Harvest").ToArray();
        Assert.Equal((1, "Harvest"), InstanceEntities.ReadAtlasTreeRename(renameWire));
        Assert.Equal([0x00, 0x91, 0x00], InstanceEntities.AtlasWindowReply());

        byte[] noReset = InstanceEntities.CharacterPassives(5, 3, [0x1234]);
        byte[] withReset = InstanceEntities.CharacterPassives(5, 3, [0x1234], freeReset: true);
        Assert.Equal(0x00, noReset[11]);
        Assert.Equal(0x01, withReset[11]);

        Assert.Equal(2, InstanceMapDevice.ReadActivateMemoryLine([0x02, 0x97, 0x02]));
        Assert.Equal(5, InstanceMapDevice.ReadActivateMavenInvitation([0x02, 0x98, 0x05]));
        Assert.Equal(2, ClientPackets.Find(InstanceEntities.ResetAllPassivesId)?.FixedLength);
        Assert.Equal(2, ClientPackets.Find(InstanceEntities.AtlasWindowRequestId)?.FixedLength);
        Assert.Equal(2, ClientPackets.Find(InstanceDelve.ReturnToWorldId)?.FixedLength);
        Assert.Equal(3, ClientPackets.Find(InstanceMapDevice.ActivateMemoryLineId)?.FixedLength);
        Assert.Equal(3, ClientPackets.Find(InstanceMapDevice.ActivateMavenInvitationId)?.FixedLength);
        Assert.Equal(renameWire.Length, ClientPackets.Length(InstanceEntities.AtlasTreeRenameId, renameWire));

        var leagues = LeagueCatalog.FromEnvironment();
        Assert.Equal((byte)59, leagues.Find("Allflame")!.Entry().C1);
        Assert.Equal((byte)0, leagues.Find("Standard")!.Entry().C1);
    }

    [Fact]
    public void Atlas_boss_option_tree_names_and_free_resets_round_trip()
    {
        var area = new MapDevice.MapArea(0x1234, "MapWorldsBeach", "Beach", 16) { Level = 83 };
        string owner = "atlas-batch-test-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var open = MapDevice.Activate(owner, area, MapMods.None, bossOption: 2);
            var run = MapDevice.Run.Of(owner, open);
            Assert.Equal((byte)2, open.BossOption);
            Assert.Equal((byte)2, run.BossOption);

            var withBoss = Game.ComposeMapAreaStats(MapMods.None, 16, new Dictionary<uint, int>(), new HashSet<string>(), 0, deepwater: true, bossOption: 2);
            var withoutBoss = Game.ComposeMapAreaStats(MapMods.None, 16, new Dictionary<uint, int>(), new HashSet<string>(), 0, bossOption: 0);
            Assert.Equal(1, withBoss[Stat.MapDeepwaterLeague]);
            Assert.Equal(2, withBoss[Stat.MapAtlasPrimordialBossInfluence]);
            Assert.False(withoutBoss.ContainsKey(Stat.MapAtlasPrimordialBossInfluence));

            var player = new Player(new(1, "1_1_town", 100, 100, 10, 10, null, []), 1, 1) { Character = { Name = owner } };
            Game.SetAtlasTreeName(player, 0, "Mapping");
            Game.SetAtlasTreeName(player, 2, "Bosses");
            Assert.Equal(["Mapping", "", "Bosses"], Game.AtlasTreeNames(player));
            Assert.Equal(InstanceEntities.AtlasTreeNames("Mapping", "", "Bosses"), Game.AtlasTreeNamesMessage(player));

            Assert.Equal(0, Game.AtlasSlots(player, 0)[6] & InstanceEntities.PassiveFreeResetFlag);
            Game.SetAtlasFreeReset(player, true);
            Assert.Equal(InstanceEntities.PassiveFreeResetFlag, Game.AtlasSlots(player, 0)[6] & InstanceEntities.PassiveFreeResetFlag);
            Game.SetAtlasFreeReset(player, false);
            Assert.Equal(0, Game.AtlasSlots(player, 0)[6] & InstanceEntities.PassiveFreeResetFlag);

            Assert.Equal(0, Game.PassivesMessage(player).Bytes[11] & InstanceEntities.PassiveFreeResetFlag);
            Game.SetPassiveFreeReset(player, true);
            Assert.Equal(InstanceEntities.PassiveFreeResetFlag, Game.PassivesMessage(player).Bytes[11] & InstanceEntities.PassiveFreeResetFlag);
            Game.SetPassiveFreeReset(player, false);
            Assert.Equal(0, Game.PassivesMessage(player).Bytes[11] & InstanceEntities.PassiveFreeResetFlag);
        }
        finally
        {
            MapDevice.Close(owner);
        }
    }
}
