namespace GameServer.Gateway.Tests;

public sealed class AttackLifeCostConversionTests
{
    [Fact]
    public void Cannibalistic_rite_splits_attack_costs_and_declines_unpayable_life_atomically()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort attack = Assert.Single(f.Equip(("Heavy Strike", 1)));
        Assert.True(Game.SkillsFor(conn).IsAttack(Game.SupportedSkill(conn, attack).Effect));
        uint hybrid = Program.Stats.Key("base_attack_skill_cost_life_instead_of_mana_%")!.Value;
        conn.PlayerStats[Program.Stats.Key("skill_mana_cost_+")!.Value] = 30;

        Assert.True(Game.PayMana(conn, attack, out uint originalMana, out uint originalLife));
        Assert.Equal(0u, originalLife);
        Assert.True(originalMana > 30);
        conn.PlayerStats[hybrid] = 10;
        Assert.True(Game.PayMana(conn, attack, out uint mana, out uint life));
        Assert.Equal(originalMana * 90 / 100, mana);
        Assert.Equal((originalMana - 30 + 5) / 10 + 3, life);

        conn.PlayerLife = life;
        uint manaBefore = conn.PlayerMana;
        Assert.False(Game.PayMana(conn, attack, out _, out _));
        Assert.Equal(manaBefore, conn.PlayerMana);
        Assert.Equal(life, conn.PlayerLife);

        conn.PlayerLife = 1000;
        ushort spell = Assert.Single(f.Equip(("Fireball", 1)));
        Assert.True(Game.PayMana(conn, spell, out uint spellMana, out uint spellLife));
        conn.PlayerStats.Remove(hybrid);
        Assert.True(Game.PayMana(conn, spell, out uint ordinarySpellMana, out uint ordinarySpellLife));
        Assert.Equal((ordinarySpellMana, ordinarySpellLife), (spellMana, spellLife));
    }
}
