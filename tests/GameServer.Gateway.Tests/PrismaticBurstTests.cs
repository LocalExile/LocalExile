namespace GameServer.Gateway.Tests;

public sealed class PrismaticBurstTests
{
    [Fact]
    public void Element_selection_uses_squared_attributes_and_honours_combat_focus()
    {
        var player = new Dictionary<uint, int>
        {
            [Stat.Strength] = 20,
            [Stat.Dexterity] = 40,
            [Stat.Intelligence] = 40,
        };
        var skill = new Dictionary<uint, int>();
        var counts = new int[3];
        const int rolls = 4096;
        for (ushort sequence = 0; sequence < rolls; sequence++)
            counts[Game.PrismaticBurstElementIndex(51, sequence, player, skill)]++;
        Assert.InRange(counts[0] / (double)rolls, .09, .14);
        Assert.InRange(counts[1] / (double)rolls, .41, .48);
        Assert.InRange(counts[2] / (double)rolls, .41, .48);
        var mixed = new Dictionary<uint, int> { [Stat.Strength] = 116, [Stat.Dexterity] = 65, [Stat.Intelligence] = 108 };
        Assert.Equal(0, Game.PrismaticBurstElementIndex(536870914, 5, mixed, skill));

        player[Stat.ElementalHitCannotRollFireDamage] = 1;
        skill[Stat.ElementalHitCannotRollColdDamage] = 1;
        player[Stat.Intelligence] = 0;
        Assert.Equal(2, Game.PrismaticBurstElementIndex(51, 17, player, skill));
        skill[Stat.ElementalHitCannotRollLightningDamage] = 1;
        Assert.Equal(-1, Game.PrismaticBurstElementIndex(51, 17, player, skill));
    }
}
