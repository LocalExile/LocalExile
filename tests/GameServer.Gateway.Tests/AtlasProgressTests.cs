using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

/// <summary>The Atlas's completed maps (21-hideouts.md §4): the nodes from AtlasNode.dat and the S→C 0x1a3 / 0x1a4 messages.</summary>
public sealed class AtlasProgressTests
{
    static AtlasProgress.Node Chateau => AtlasProgress.Find("Chateau")!;

    static MapDevice.Run MapRun(int tier, ItemRarity rarity, bool corrupted = false) =>
        new("Tester", new MapDevice.MapArea(Chateau.Hash16, Chateau.Id, Chateau.Name, tier),
            new MapMods(new Dictionary<uint, int>()) { MapRarity = rarity, Corrupted = corrupted });

    [Fact]
    public void A_regular_completion_leaves_the_bonus_available_and_a_second_bonus_cannot_award_twice()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // T1-5: Magic+, T6-10: Rare+, T11-16: Rare+Corrupted, Unique: always, Tier 0 / T17: false.
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(1, ItemRarity.Normal)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(1, ItemRarity.Magic)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(5, ItemRarity.Normal)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(5, ItemRarity.Magic)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(5, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(6, ItemRarity.Magic)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(6, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(10, ItemRarity.Magic)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(10, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(11, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(11, ItemRarity.Magic, corrupted: true)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(11, ItemRarity.Rare, corrupted: true)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(16, ItemRarity.Rare)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(16, ItemRarity.Rare, corrupted: true)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(1, ItemRarity.Unique)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(16, ItemRarity.Unique)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(0, ItemRarity.Rare, corrupted: true)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(17, ItemRarity.Rare, corrupted: true)));

        Assert.True(AtlasProgress.Complete(data, [Chateau]));
        Assert.Contains(Chateau.Id, AtlasProgress.Completed(data));
        Assert.Empty(AtlasProgress.BonusCompleted(data));

        Assert.True(AtlasProgress.CompleteBonus(data, [Chateau]));
        Assert.False(AtlasProgress.CompleteBonus(data, [Chateau]));
        Assert.Single(AtlasProgress.BonusCompleted(data));
    }

    [Fact]
    public void Old_completion_credit_is_preserved_and_a_new_qualified_map_earns_the_next_point()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;
        var next = AtlasProgress.Find("Alleyways")!;
        data.Change("completed", _ => Chateau.Id);
        player.Character.AtlasPassivePoints = 1;

        Assert.Contains(Chateau.Id, AtlasProgress.BonusCompleted(data));
        Assert.True(AtlasProgress.Complete(data, [next]));
        Assert.Single(AtlasProgress.BonusCompleted(data));
        Assert.False(AtlasProgress.CompleteBonus(data, [Chateau]));
        Assert.True(AtlasProgress.CompleteBonus(data, [next]));
        Assert.Equal(2, AtlasProgress.BonusCompleted(data).Count);
    }

    [Fact]
    public void MessagesCarryTheNodesRowMasksAndHash()
    {
        var alleyways = AtlasProgress.Find("Alleyways")!;
        byte[] split = AtlasProgress.FullMessage(
            new HashSet<string> { Chateau.Id, alleyways.Id },
            new HashSet<string> { Chateau.Id });
        Assert.Equal(7 + 2 * 22, split.Length);
        Assert.Equal(new byte[] { 0x01, 0xa3, 0, 0, 0, 0x03, 0 }, split[..7]);
        // First mask (+0xd030) has both completed maps; second mask (+0xd070) has only bonus-completed Chateau.
        Assert.NotEqual(0, split[7 + Chateau.Row / 8] & (1 << (Chateau.Row & 7)));
        Assert.NotEqual(0, split[7 + alleyways.Row / 8] & (1 << (alleyways.Row & 7)));
        Assert.NotEqual(0, split[7 + 22 + Chateau.Row / 8] & (1 << (Chateau.Row & 7)));
        Assert.Equal(0, split[7 + 22 + alleyways.Row / 8] & (1 << (alleyways.Row & 7)));
        Assert.All(AtlasProgress.FullMessage(new HashSet<string>(), new HashSet<string>())[7..], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 0x01, 0xa4, 0x9a, 0x23, 0, 0, 0x02 }, AtlasProgress.NodeMessage(Chateau));
        Assert.Equal(new byte[] { 0x01, 0xa4, 0x9a, 0x23, 0, 0, 0x00 }, AtlasProgress.NodeMessage(Chateau, bonus: false));
        byte[] empty = AtlasProgress.EmptyMessage();
        Assert.Equal(split.Length, empty.Length);
        Assert.Equal(split[..7], empty[..7]);
        Assert.All(empty[7..], b => Assert.Equal(0, b));
    }
}
