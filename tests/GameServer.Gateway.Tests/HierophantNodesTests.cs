namespace GameServer.Gateway.Tests;

public sealed class HierophantNodesTests
{
    static Func<uint, int> With(params (uint Name, int Value)[] stats)
    {
        var values = stats.ToDictionary(s => s.Name, s => s.Value);
        return name => values.GetValueOrDefault(name);
    }

    static Dictionary<uint, int> Keyed(params (uint Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    // The Hierophant's nodes with a consumer of their own (Agents/docs/game/hierophant.md), by the values PassiveSkills
    // gives them: Divine Guidance's mana increases at 30% as damage, Sanctuary of Thought's 1% area per 50 unreserved
    // mana up to 100%, and Arcane Blessing's Arcane Surge on a spell hit with two 20% Arcane Surge Effect nodes and
    // Illuminated Devotion's "while you have Arcane Surge" stats.
    [Fact]
    public void Hierophant_nodes_follow_their_stats_and_arcane_surge()
    {
        // Divine Guidance: 30% of the summed maximum_mana_+% (30 + three 8% nodes = 54 → 16), truncated toward zero.
        Assert.Equal(16, Hierophant.DamageFromMana(With((Hierophant.ManaModifiersToDamage, 1), (Stat.MaximumManaPlusPct, 54))));
        Assert.Equal(-4, Hierophant.DamageFromMana(With((Hierophant.ManaModifiersToDamage, 1), (Stat.MaximumManaPlusPct, -15))));
        Assert.Equal(0, Hierophant.DamageFromMana(With((Stat.MaximumManaPlusPct, 54))));

        // Sanctuary of Thought: per whole 50 unreserved maximum mana, capped at 100.
        Assert.Equal(0, Hierophant.AreaFromMana(1, 49));
        Assert.Equal(25, Hierophant.AreaFromMana(1, 1299));
        Assert.Equal(100, Hierophant.AreaFromMana(1, 9000));

        // Arcane Blessing: a spell hit on a hostile monster grants the 4 s Arcane Surge (20% cast speed, 30% mana regen).
        var conn = new Player(new(1, "hierophant", 1000, 1000, 100, 100, null, []), 1, 1) { PlayerLife = 100 };
        var target = new SpawnedMonster("hit", new(105, 100, 0), 0, 0, 1000);
        var caster = Keyed((Stat.GainArcaneSurgeOnSpellHitByYouOrYourTotems, 1));
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
        var stats = Game.WithSupportGemBuffs(conn, Keyed((Stat.ArcaneSurgeEffectPlusPct, 40),
            (Stat.ArcaneSurgeOnYouSpellDamagePlusPctFinalFromHierophant, 20),
            (Stat.AreaOfEffectPlusPctWhileYouHaveArcaneSurge, 30),
            (Stat.LifeLeechFromSpellDamagePermyriadWhileYouHaveArcaneSurge, 50),
            (Stat.NonDamagingAilmentEffectPlusPctOnSelfWhileYouHaveArcaneSurge, -50)));
        int Get(uint name) => stats.GetValueOrDefault(name);
        Assert.Equal(1, Get(Stat.HasArcaneSurge));
        Assert.Equal(28, Get(Stat.ArcaneSurgeCastSpeedPlusPct));
        Assert.Equal(42, Get(Stat.ManaRegenerationRatePlusPct));
        Assert.Equal(28, Get(Stat.SpellDamagePlusPctFinalFromArcaneSurge));
        Assert.Equal(30, Get(Stat.BaseSkillAreaOfEffectPlusPct));
        Assert.Equal(50, Get(Stat.BaseLifeLeechFromSpellDamagePermyriad));
        Assert.Equal(-50, Get(Stat.NonDamagingAilmentEffectPlusPctOnSelf));
        // ... which halves a chill or shock on the player, on top of their own effect-on-self stats.
        foreach (var kind in new[] { PlayerElementalAilments.Kind.Chill, PlayerElementalAilments.Kind.Shock })
        {
            var plain = new PlayerElementalAilments();
            plain.Inflict(kind, 20, 2, 0, _ => 0, new Random(1));
            var surged = new PlayerElementalAilments();
            surged.Inflict(kind, 20, 2, 0, Get, new Random(1));
            Assert.Equal(20, plain.Magnitude(kind, 0));
            Assert.Equal(10, surged.Magnitude(kind, 0));
        }

        // The cast speed total counts the buff's; a totem's copy of the owner's stats loses its more Spell Damage.
        Assert.Contains("arcane_surge_cast_speed_+%",
            PanelStats.Sums.Single(s => s.Display == Stat.CastSpeedPlusPct).Sources.Select(s => Program.Stats.Name(s.Name)));
        Assert.DoesNotContain(Stat.SpellDamagePlusPctFinalFromArcaneSurge,
            Game.TotemCasterStats(stats).Keys);
    }
}
