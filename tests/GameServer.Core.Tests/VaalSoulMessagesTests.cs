using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class VaalSoulMessagesTests
{
    [Fact]
    public void Actor20_has_seeds_then_current_and_NEW_capacity_after_skill_adds_and_removes()
    {
        var skill = new GrantedSkill(0x103, 532, 42);
        // Same literal skill record as existing records; souls follow the removed u16 ids.
        string record = "03010014020000002A00000000000000000000000000000000000000";
        string expected = "02BA00001000000000000000003909" + "20000101" + record + "0401"
            + "01" + "570000001E000000" + "01" + "57000000070000001E000000";
        Assert.Equal(expected, Convert.ToHexString(InstanceEntities.PlayerSkillsChange(0x1000, [skill], [0x104],
            [new(87, 30)], [new(87, 7, 30)])));
    }

    [Fact]
    public void Creation_blob_has_row_capacity_current_before_the_existing_cooldown_count()
    {
        var player = new PlayerObject("x", 1, 2, 3, 4, 5, Skills: [new(0x103, 532, 42)],
            Souls: [new(87, 30, 7), new(123, 80, 9)]);
        string hex = Convert.ToHexString(player.Blob());
        string Pool(string current) => current + "0000000000000000000000000000";
        string expected = "0000" + "020000000300000000000000000000020001" // base and Positioned
            + "00000000000000000000" + "0000" // Stats and comp3
            + Pool("04000000") + Pool("05000000") + Pool("00000000") + "00" + "00" // Life, Animated
            + "010000007800" + "01" + "00000000000000000000000000000000" // Player name, class, fields/flags
            + "000000000000000000" + "00000000" + "00" + "0000000000" + "00000000" + "00"
            + "0000" // Inventories
            + "000000000000000000000000000000" + "4000" // Actor body and flags
            + "01" + "03010014020000002A00000000000000000000000000000000000000" // skill record
            + "02" + "570000001E00000007000000" + "7B0000005000000009000000"
            + "00000000" + "00"; // cooldown trailer and tasks
        Assert.Equal(expected, hex);
        Assert.Equal(player.Blob().Length - 24, (player with { Souls = [] }).Blob().Length);
    }

    [Fact]
    public void Actor40_cooldown_refresh_preserves_the_soul_list_and_cooldown_trailer_order()
    {
        Assert.Equal("02BA00001000000000000000001B09" + "40000001" + "570000001E00000007000000"
            + "01000000" + "030100000000",
            Convert.ToHexString(InstanceEntities.PlayerSingleUseCooldowns(0x1000,
                [new(0x103, 0, 2, 1)], [new(87, 30, 7)])));
        // Empty soul lists are a preserve operation in the native reader, not a resource reset.
        Assert.Equal("02BA00001000000000000000000909" + "4000000000000000",
            Convert.ToHexString(InstanceEntities.PlayerSingleUseCooldowns(0x1000, [])));
        Assert.Equal("02BA00001000000000000000000709" + "200000000000",
            Convert.ToHexString(InstanceEntities.PlayerSkillsChange(0x1000, [], [])));
    }

    [Fact]
    public void Soul_list_counts_cannot_silently_wrap()
    {
        Assert.Throws<OverflowException>(() => InstanceEntities.PlayerSkillsChange(1, [], [],
            Enumerable.Repeat(new VaalSoulSeed(87, 30), 256).ToArray()));
        Assert.Throws<OverflowException>(() => new PlayerObject("x", 1, 0, 0, 1, 1,
            Souls: Enumerable.Repeat(new VaalSoulBudget(87, 30, 0), 256).ToArray()).Blob());
    }
}
