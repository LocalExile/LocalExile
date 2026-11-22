using GameServer.Modules.Atlas;
using GameServer.Modding;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The Atlas's completed maps (21-hideouts.md §4): the nodes from AtlasNode.dat and the S→C 0x1a3 / 0x1a4 messages.</summary>
public sealed class AtlasProgressTests
{
    static AtlasProgress.Node Chateau => AtlasProgress.Find("Chateau")!;

    [Fact]
    public void NormalMapBonusUsesTheNodesTierAndTheActivatedItemsRarity()
    {
        var cells = AtlasProgress.Find("Cells")!;
        Assert.Equal(1, cells.BonusTier);
        MapDevice.Run Run(int tier, ItemRarity rarity, bool corrupted = false) =>
            new("Tester", new(cells.Hash16, cells.Id, cells.Name, tier),
                new(MapMods.None.Stats) { MapRarity = rarity, Corrupted = corrupted });

        Assert.False(AtlasProgress.QualifiesForPoint(cells, null));
        Assert.False(AtlasProgress.QualifiesForPoint(cells, Run(1, ItemRarity.Normal)));
        Assert.True(AtlasProgress.QualifiesForPoint(cells, Run(7, ItemRarity.Magic)));
        var yellow = cells with { BonusTier = 6 };
        Assert.False(AtlasProgress.QualifiesForPoint(yellow, Run(7, ItemRarity.Magic)));
        Assert.False(AtlasProgress.QualifiesForPoint(yellow, Run(5, ItemRarity.Rare)));
        Assert.True(AtlasProgress.QualifiesForPoint(yellow, Run(6, ItemRarity.Rare)));
        var red = cells with { BonusTier = 11 };
        Assert.False(AtlasProgress.QualifiesForPoint(red, Run(11, ItemRarity.Rare)));
        Assert.True(AtlasProgress.QualifiesForPoint(red, Run(11, ItemRarity.Rare, corrupted: true)));
    }

    [Fact]
    public void ClearedMapsCanEarnTheirPointLaterAndOldCreditDoesNotAwardTwice()
    {
        var cells = AtlasProgress.Find("Cells")!;
        var data = new SavedData();
        Assert.True(AtlasProgress.Complete(data, [Chateau]));
        Assert.Equal(1, AtlasProgress.RewardedCount(data));

        Assert.Equal((true, 0), AtlasProgress.RecordClear(data, [cells], qualifies: false));
        Assert.Equal(1, AtlasProgress.RewardedCount(data));
        Assert.Equal((false, 1), AtlasProgress.RecordClear(data, [Chateau, cells], qualifies: true));
        Assert.Equal((false, 0), AtlasProgress.RecordClear(data, [cells], qualifies: true));
        Assert.Equal(2, AtlasProgress.RewardedCount(data));
    }

    sealed class SavedData : ISavedData
    {
        readonly Dictionary<string, string> _values = new();

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public bool Change(string key, Func<string?, string?> change)
        {
            if (change(Get(key)) is not { } value) return false;
            _values[key] = value;
            return true;
        }
    }

    [Fact]
    public void MessagesCarryTheNodesRowMasksAndHash()
    {
        byte[] full = AtlasProgress.FullMessage(new HashSet<string> { "MapWorldsChateau" });
        Assert.Equal(7 + 2 * 22, full.Length);
        Assert.Equal(new byte[] { 0x01, 0xa3, 0, 0, 0, 0x03, 0 }, full[..7]);
        // The completed map's row is set in both masks. Its byte also carries the neighbouring on-Atlas rows, so the
        // bit itself is what is checked, not the whole byte.
        Assert.NotEqual(0, full[7 + 97 / 8] & (1 << (97 & 7)));
        Assert.NotEqual(0, full[7 + 22 + 97 / 8] & (1 << (97 & 7)));
        // Every on-Atlas row goes into both masks (the hidden rows and the voidstone slots included), so the
        // completed mask is as full as the revealed one and the bottom citadels stay connected.
        Assert.Equal(full[7..29], full[29..]);
        Assert.Equal(new byte[] { 0x01, 0xa4, 0x9a, 0x23, 0, 0, 0x02 }, AtlasProgress.NodeMessage(Chateau));
        byte[] empty = AtlasProgress.EmptyMessage();
        Assert.Equal(full.Length, empty.Length);
        Assert.Equal(full[..7], empty[..7]);
        Assert.All(empty[7..], b => Assert.Equal(0, b));
    }
}
