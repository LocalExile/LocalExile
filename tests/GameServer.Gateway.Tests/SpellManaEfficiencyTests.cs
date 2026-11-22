namespace GameServer.Gateway.Tests;

public sealed class SpellManaEfficiencyTests
{
    [Fact]
    public void Arcane_conservation_reduces_only_a_spells_mana_payment()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        uint efficiency = Program.Stats.Key("base_spell_mana_cost_efficiency_+%")!.Value;
        uint shieldCost = Program.Stats.Key("base_skill_gain_es_cost_%_of_mana_cost")!.Value;
        conn.PlayerStats[shieldCost] = 100;

        (uint Mana, uint Life, uint Shield) Pay(ushort skill)
        {
            conn.PlayerMana = conn.PlayerLife = 1000;
            conn.EnergyShield.SetMaximum(100, refill: true);
            Assert.True(Game.PayMana(conn, skill, out uint mana, out uint life));
            return (mana, life, 100 - conn.EnergyShield.Current);
        }

        ushort spell = Assert.Single(f.Equip(("Fireball", 1)));
        var original = Pay(spell);
        Assert.True(original.Shield > 0);
        conn.PlayerStats[efficiency] = 20;
        var discounted = Pay(spell);
        Assert.Equal(5u, original.Mana);
        Assert.Equal(4u, discounted.Mana);
        Assert.Equal(original.Life, discounted.Life);
        Assert.Equal(original.Shield, discounted.Shield);

        ushort attack = Assert.Single(f.Equip(("Heavy Strike", 1)));
        var attackPayment = Pay(attack);
        Assert.True(attackPayment.Mana > 0);
        conn.PlayerStats.Remove(efficiency);
        Assert.Equal(attackPayment, Pay(attack));
    }
}
