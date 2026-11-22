namespace GameServer.Gateway.Tests;

public sealed class CurseManaEfficiencyTests
{
    [Fact]
    public void Skittering_runes_applies_once_to_hexes_and_marks()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        uint generic = Program.Stats.Key("base_mana_cost_efficiency_+%")!.Value;
        uint curse = Program.Stats.Key("base_mana_cost_efficiency_+%_of_curse_skills")!.Value;
        uint Pay(ushort skill)
        {
            conn.PlayerMana = 1000;
            Assert.True(Game.PayMana(conn, skill, out uint mana, out _));
            return mana;
        }

        foreach (string name in new[] { "Flammability", "Assassin's Mark" })
        {
            ushort skill = Assert.Single(f.Equip((name, 1)));
            uint baseline = Pay(skill);
            conn.PlayerStats[generic] = 20;
            uint expected = Pay(skill);
            Assert.True(expected < baseline);
            conn.PlayerStats.Remove(generic);
            conn.PlayerStats[curse] = 20;
            Assert.Equal(expected, Pay(skill));
            conn.PlayerStats.Remove(curse);
        }

        ushort fireball = Assert.Single(f.Equip(("Fireball", 1)));
        uint normalPayment = Pay(fireball);
        conn.PlayerStats[curse] = 20;
        Assert.Equal(normalPayment, Pay(fireball));
    }
}
