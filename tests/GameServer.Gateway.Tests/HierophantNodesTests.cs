namespace GameServer.Gateway.Tests;

public sealed class HierophantNodesTests
{
    static Func<string, int> With(params (string Name, int Value)[] stats)
    {
        var values = stats.ToDictionary(s => s.Name, s => s.Value);
        return name => values.GetValueOrDefault(name);
    }

    static Dictionary<uint, int> Keyed(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name)!.Value, s => s.Value);

    // The Hierophant's nodes with a consumer of their own (Agents/docs/game/hierophant.md), by the values PassiveSkills
    // gives them: Divine Guidance's mana increases at 30% as damage, Sanctuary of Thought's 1% area per 50 unreserved
    // mana up to 100%, and Arcane Blessing's Arcane Surge on a spell hit with two 20% Arcane Surge Effect nodes and
    // Illuminated Devotion's "while you have Arcane Surge" stats.
    [Fact]
    public void Hierophant_nodes_follow_their_stats_and_arcane_surge()
    {
        // Divine Guidance: 30% of the summed maximum_mana_+% (30 + three 8% nodes = 54 → 16), truncated toward zero.
        Assert.Equal(16, Hierophant.DamageFromMana(With((Hierophant.ManaModifiersToDamage, 1), ("maximum_mana_+%", 54))));
        Assert.Equal(-4, Hierophant.DamageFromMana(With((Hierophant.ManaModifiersToDamage, 1), ("maximum_mana_+%", -15))));
        Assert.Equal(0, Hierophant.DamageFromMana(With(("maximum_mana_+%", 54))));

        // Sanctuary of Thought: per whole 50 unreserved maximum mana, capped at 100.
        Assert.Equal(0, Hierophant.AreaFromMana(1, 49));
        Assert.Equal(25, Hierophant.AreaFromMana(1, 1299));
        Assert.Equal(100, Hierophant.AreaFromMana(1, 9000));

        // Arcane Blessing: a spell hit on a hostile monster grants the 4 s Arcane Surge (20% cast speed, 30% mana regen).
        var conn = new ClientState(new(1, "hierophant", 1000, 1000, 100, 100, null, []), 1, 1) { PlayerLife = 100 };
        var target = new SpawnedMonster("hit", new(105, 100, 0), 0, 0, 1000);
        var caster = Keyed(("gain_arcane_surge_on_spell_hit_by_you_or_your_totems", 1));
        lock (conn.Monsters)
        {
            Game.ArcaneSurgeOnSpellHitLocked(conn, Keyed(), target);
            Assert.False(conn.SupportGems.ArcaneSurgeActiveAt(conn.Clock.Elapsed.TotalSeconds));
            Game.ArcaneSurgeOnSpellHitLocked(conn, caster, target);
        }
        double now = conn.Clock.Elapsed.TotalSeconds;
        Assert.True(conn.SupportGems.ArcaneSurgeActiveAt(now));
        Assert.False(conn.SupportGems.ArcaneSurgeActiveAt(now + 4.01));

        // With 40% increased effect the buff's values are 28 / 42 / 28% more; the "while" stats join their plain forms.
        var stats = Game.WithSupportGemBuffs(conn, Keyed(("arcane_surge_effect_+%", 40),
            ("arcane_surge_on_you_spell_damage_+%_final_from_hierophant", 20),
            ("area_of_effect_+%_while_you_have_arcane_surge", 30),
            ("life_leech_from_spell_damage_permyriad_while_you_have_arcane_surge", 50),
            ("non_damaging_ailment_effect_+%_on_self_while_you_have_arcane_surge", -50)));
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(1, Get("has_arcane_surge"));
        Assert.Equal(28, Get("arcane_surge_cast_speed_+%"));
        Assert.Equal(42, Get("mana_regeneration_rate_+%"));
        Assert.Equal(28, Get("spell_damage_+%_final_from_arcane_surge"));
        Assert.Equal(30, Get("base_skill_area_of_effect_+%"));
        Assert.Equal(50, Get("base_life_leech_from_spell_damage_permyriad"));
        Assert.Equal(-50, Get("non_damaging_ailment_effect_+%_on_self"));

        // The cast speed total counts the buff's; a totem's copy of the owner's stats loses its more Spell Damage.
        Assert.Contains("arcane_surge_cast_speed_+%",
            PanelStats.Sums.Single(s => s.Display == "cast_speed_+%").Sources.Select(s => s.Name));
        Assert.DoesNotContain(Program.Stats.Key("spell_damage_+%_final_from_arcane_surge")!.Value,
            Game.TotemCasterStats(stats).Keys);
    }
}
