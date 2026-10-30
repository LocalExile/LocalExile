namespace GameServer.Gateway.Tests;

// The skill id of each socketed gem stays the same while the gem stays socketed: the client binds skill bar keys by skill
// id, so a gem leaving or arriving must not move another gem's skill (live 2026-10-20: taking Haste out of a wand moved
// Righteous Fire's keys).
public class GemSkillIdsTests
{
    static readonly ushort[] Pool = [0x101, 0x102, 0x103, 0x104, 0x105];
    const int Haste = 10, RighteousFire = 20, Fireball = 30;

    static GemSkillIds.Gem G(uint host, int socket, int effect) => new(host, socket, effect);

    [Fact]
    public void Gems_get_the_lowest_free_ids_in_order()
    {
        var ids = new GemSkillIds();

        Assert.Equal([(ushort)0x101, 0x102], ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool));
    }

}
