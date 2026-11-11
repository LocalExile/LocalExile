using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The eleven drop-disabled Honoured rows of issue #372: their client stats, and the consumers that were added for
// them (Onslaught on hit, Arcane Surge on a spell hit and the attribute gem-level stats; the rest already had one).
public sealed class TattooHonouredTests
{
    static readonly (string Name, (string Stat, int Value)[] Stats)[] Rows =
    [
        ("Honoured Tattoo of the Barbarian", [("base_maximum_cold_damage_resistance_%", 1), ("max_frenzy_charges", 1)]),
        ("Honoured Tattoo of the Berserker", [("gain_onslaught_on_hit_duration_ms", 4000)]),
        ("Honoured Tattoo of the Flock", [("non_curse_aura_effect_+%", 1)]),
        ("Honoured Tattoo of the Flood", [("gain_arcane_surge_on_hit_%_chance", 100)]),
        ("Honoured Tattoo of the Hatungo", [("base_reservation_efficiency_+%", 1)]),
        ("Honoured Tattoo of the Hunter", [("dexterity_skill_gem_level_+", 1)]),
        ("Honoured Tattoo of the Mountain", [("base_maximum_fire_damage_resistance_%", 1), ("max_endurance_charges", 1)]),
        ("Honoured Tattoo of the Pa", [("fortify_on_hit", 1)]),
        ("Honoured Tattoo of the Storm", [("base_maximum_lightning_damage_resistance_%", 1), ("max_power_charges", 1)]),
        ("Honoured Tattoo of the Warlord", [("strength_skill_gem_level_+", 1)]),
        ("Honoured Tattoo of the Wise", [("intelligence_skill_gem_level_+", 1)]),
    ];

    [Fact]
    public void Every_honoured_tattoo_maps_its_client_stats()
    {
        foreach (var (name, stats) in Rows)
        {
            var mapped = TattooStats.For(name);
            Assert.NotNull(mapped);
            foreach (var (stat, value) in stats)
                Assert.Contains((Program.Stats.Key(stat)!.Value, value), mapped);
        }
    }

    [Fact]
    public void The_honoured_consumers_read_their_stats()
    {
        // "You gain Onslaught for 4 seconds on Hit": the stat starts it on a hit and it never shortens.
        var conn = new ClientState(new(1, "tattoo-honoured", 10, 10, 1, 1, null, []), 1, 1) { PlayerLife = 100 };
        Game.ConfigureKillOnslaught(conn, name => name == "gain_onslaught_on_hit_duration_ms" ? 4000 : 0);
        Assert.True(Game.OnslaughtOnHitLocked(conn, 10));
        Assert.Equal(14, Game.KillOnslaughtOf(conn).Until);
        // The next hit refreshes the four seconds; it never shortens a longer running one.
        Assert.True(Game.OnslaughtOnHitLocked(conn, 11));
        Assert.Equal(15, Game.KillOnslaughtOf(conn).Until);

        // "Gain Arcane Surge on Hit with Spells": the buff's four seconds, and the stat's roll on a spell hit.
        Game.GainArcaneSurgeLocked(conn, 20);
        Assert.Equal(24, conn.SupportGems.ArcaneSurgeUntil);
        Assert.Equal(20, conn.SupportGems.ArcaneSurgeCastSpeedPct);
        Assert.Equal(30, conn.SupportGems.ArcaneSurgeManaRegenPct);
        var chance = new Dictionary<uint, int>
        {
            [Program.Stats.Key("gain_arcane_surge_on_hit_%_chance")!.Value] = 100,
        };
        Game.ArcaneSurgeOnSpellHitLocked(conn, chance, 30, new Random(1));
        Assert.Equal(34, conn.SupportGems.ArcaneSurgeUntil);

        // "+1 to Level of all Strength/Dexterity/Intelligence Skill Gems": the attribute a gem colour names.
        Assert.Equal("strength_skill_gem_level_+", SkillGemLevel.AttributeStat(1));
        Assert.Equal("dexterity_skill_gem_level_+", SkillGemLevel.AttributeStat(2));
        Assert.Equal("intelligence_skill_gem_level_+", SkillGemLevel.AttributeStat(3));
        Assert.Null(SkillGemLevel.AttributeStat(4));
    }
}
