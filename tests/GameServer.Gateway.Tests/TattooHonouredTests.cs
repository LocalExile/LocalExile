using GameServer.Core.Protocol.Instance;
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
                Assert.Contains((Stat.Of(stat), value), mapped);
        }
    }

    [Fact]
    public void The_honoured_consumers_read_their_stats()
    {
        // "You gain Onslaught for 4 seconds on Hit": the stat starts it on a hit and it never shortens.
        var player = new Player(new(1, "tattoo-honoured", 10, 10, 1, 1, null, []), 1, 1) { PlayerLife = 100 };
        Game.ConfigureKillOnslaught(player, name => name == Stat.GainOnslaughtOnHitDurationMs ? 4000 : 0);
        Assert.True(Game.OnslaughtOnHitLocked(player, 10));
        Assert.Equal(14, Game.KillOnslaughtOf(player).Until);
        // The next hit refreshes the four seconds; it never shortens a longer running one.
        Assert.True(Game.OnslaughtOnHitLocked(player, 11));
        Assert.Equal(15, Game.KillOnslaughtOf(player).Until);

        // "Gain Arcane Surge on Hit with Spells": the buff's four seconds, and the stat's roll on a spell hit.
        Game.GainArcaneSurgeLocked(player, 20);
        Assert.Equal(24, player.SupportGems.ArcaneSurgeUntil);
        Assert.Equal(20, player.SupportGems.ArcaneSurgeCastSpeedPct);
        Assert.Equal(30, player.SupportGems.ArcaneSurgeManaRegenPct);
        var chance = new Dictionary<uint, int>
        {
            [Stat.GainArcaneSurgeOnHitPctChance] = 100,
        };
        Game.ArcaneSurgeOnSpellHitLocked(player, chance, 30, new Random(1));
        Assert.Equal(34, player.SupportGems.ArcaneSurgeUntil);

        // "+1 to Level of all Strength/Dexterity/Intelligence Skill Gems": the attribute a gem colour names.
        Assert.Equal(Stat.StrengthSkillGemLevelPlus, SkillGemLevel.AttributeStat(1));
        Assert.Equal(Stat.DexteritySkillGemLevelPlus, SkillGemLevel.AttributeStat(2));
        Assert.Equal(Stat.IntelligenceSkillGemLevelPlus, SkillGemLevel.AttributeStat(3));
        Assert.Null(SkillGemLevel.AttributeStat(4));
    }

    [Fact]
    public void Minion_skill_gem_level_stat_raises_minion_active_gem_levels()
    {
        Assert.Contains((Stat.MinionSkillGemLevelPlus, "minion"), SkillGemLevel.TaggedActiveLevelStats);

        using var f = new AnimatedAdmissionTests.Fixture();
        f.Player.Pools = f.Player.Pools with { Level = 80, Strength = 300, Dexterity = 300, Intelligence = 300 };
        var items = Game.ItemsOf(f.Player);
        foreach (var p in items.Items(PlayerInventories.Weapon1)) items.Remove(PlayerInventories.Weapon1, p.Id);
        var gemType = Program.Items.FindGem("Raise Spectre")!.Value;
        var specGem = ItemCatalog.Create(gemType.Type) with
        {
            GemVariant = gemType.Variant.Index,
            GemExperience = Program.Items.Gem(gemType.Type)!.ExperienceFor(20),
        };
        Assert.True(Program.Skills.GemHasTag(Program.Items.ByHash(specGem.TypeHash)!.Row, 0, "minion"));
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        {
            Sockets = [new ItemSocket(GameServer.Core.Protocol.Instance.SocketColour.Blue, specGem)],
            Links = [1],
        });
        ushort source = Game.GemSkillEffects(f.Player).Single().Id;
        Assert.Equal(20, Game.SupportedSkill(f.Player, source).Level);

        var mod = Program.Items.Mods.ById("MinionGemLevel1h1")!;
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        {
            Rarity = ItemRarity.Rare,
            Mods = [new ItemMod(ModSection.Explicit, mod.Hash, [1])],
        });
        Assert.Equal(21, Game.SupportedSkill(f.Player, source).Level);
    }
}
