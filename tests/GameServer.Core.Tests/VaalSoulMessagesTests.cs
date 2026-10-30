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

}
