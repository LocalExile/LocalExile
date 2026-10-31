using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static uint LevelPanelKey(string name) => Program.Stats.Key(name)!.Value;
    static GatewayCharacter LevelPanelCharacter(int level = 10) => new(level: level,
        family: AscendancyFamily.Normal, choice: "Elementalist", classRow: SkillCatalog.WitchRow, baseClass: 3);
    // Actual catalog modifiers on isolated carriers. Cross-base combinations are not legal unique acquisition claims.
    static ItemMod LevelPanelMod(ItemCatalog catalog, string id, string stat, int value)
    {
        var mod = catalog.Mods.ById(id)!;
        Assert.Equal(1, mod.Domain);
        Assert.Equal(id, catalog.Mods.ByHash(mod.Hash)!.Id);
        var source = Assert.Single(mod.Stats);
        Assert.Equal(stat, source.Id);
        Assert.False(source.Local);
        Assert.InRange(value, source.Min, source.Max);
        return new(ModSection.Explicit, mod.Hash, [value]);
    }
    static Item LevelPanelCarrier(ItemCatalog catalog, string name, params ItemMod[] mods) =>
        ItemCatalog.Create(catalog.Find(name)!) with { Mods = mods };
    static Dictionary<uint, int> LevelPanelPublished(GatewayCharacter game) =>
        ProgramValue<List<(uint, int)>>("PlayerStatsWithCleared", game.State).ToDictionary(v => v.Item1, v => v.Item2);
    static ItemMod[] LevelPanelOnKillMods(ItemCatalog catalog) =>
    [
        LevelPanelMod(catalog, "LifeGainedOnEnemyDeathPerLevelUniqueTwoHandSword7", "life_gained_on_enemy_death_per_level", 1),
        LevelPanelMod(catalog, "ManaGainedOnEnemyDeathPerLevelUniqueSceptre8", "mana_gained_on_enemy_death_per_level", 1),
        LevelPanelMod(catalog, "EnergyShieldGainedOnEnemyDeathPerLevelUniqueSceptre8", "energy_shield_gained_on_enemy_death_per_level", 1),
    ];

    [Fact]
    public async Task Real_on_kill_modifiers_follow_actual_level_changes_raw_overrides_and_source_removal()
    {
        using var game = LevelPanelCharacter();
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var mods = LevelPanelOnKillMods(catalog).Concat(new[]
        {
            LevelPanelMod(catalog, "LifeGainedFromEnemyDeath1", "base_life_gained_on_enemy_death", 7),
            LevelPanelMod(catalog, "ManaGainedFromEnemyDeath1", "base_mana_gained_on_enemy_death", 5),
            LevelPanelMod(catalog, "EnergyShieldGainedFromEnemyDeathUniqueHelmetDexInt3", "base_energy_shield_gained_on_enemy_death", 10),
        }).ToArray();
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", mods));
        void Check(int life, int mana, int es)
        {
            var first = LevelPanelPublished(game);
            var again = LevelPanelPublished(game);
            foreach (var (name, expected) in new[] { ("life", life), ("mana", mana), ("energy_shield", es) })
            {
                uint key = LevelPanelKey(name + "_gained_on_enemy_death");
                Assert.Equal(expected, first[key]);
                Assert.Equal(expected, again[key]);
            }
        }
        Check(17, 15, 20);
        Assert.Null((await game.SetLevel(20)).Error);
        Assert.Equal(20, game.State.Pools.Level);
        Check(27, 25, 30);
        var reloaded = game.Reload();
        Assert.Equal(20, reloaded.Pools.Level);
        var afterReload = Program.CharacterPlayerStats(reloaded);
        Assert.Equal(27, afterReload[LevelPanelKey("life_gained_on_enemy_death")]);
        Assert.Equal(25, afterReload[LevelPanelKey("mana_gained_on_enemy_death")]);
        Assert.Equal(30, afterReload[LevelPanelKey("energy_shield_gained_on_enemy_death")]);
        Check(27, 25, 30);
        game.State.PlayerStats[LevelPanelKey("life_gained_on_enemy_death_per_level")] = 2;
        Check(47, 25, 30);
        game.State.PlayerStats[LevelPanelKey("life_gained_on_enemy_death")] = 777;
        Check(777, 25, 30);
        game.State.PlayerStats.Remove(LevelPanelKey("life_gained_on_enemy_death"));
        game.State.PlayerStats.Remove(LevelPanelKey("life_gained_on_enemy_death_per_level"));
        Check(27, 25, 30);
        Assert.NotNull(inventory.Remove(6, ring.Id));
        var cleared = LevelPanelPublished(game);
        foreach (string name in new[] { "life", "mana", "energy_shield" })
        {
            uint key = LevelPanelKey(name + "_gained_on_enemy_death");
            Assert.Equal(0, cleared[key]);
            Assert.DoesNotContain(key, game.State.SentStatKeys);
        }
    }

    [Fact]
    public void On_kill_level_sources_require_active_equipment_and_qualified_items()
    {
        using var game = LevelPanelCharacter(20);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        inventory.Add(14, 0, 0, LevelPanelCarrier(catalog, "Rusted Sword", LevelPanelOnKillMods(catalog)));
        var body = inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            LevelPanelCarrier(catalog, "Chestplate", LevelPanelOnKillMods(catalog)));
        Assert.Equal((25, 0, 0), catalog.Requirements(catalog.Find("Chestplate")!));
        game.State.Pools = Program.PoolsFor(game.State, 20);
        var inactive = Program.CharacterPlayerStats(game.State);
        foreach (string name in new[] { "life", "mana", "energy_shield" })
            Assert.Equal(0, inactive.GetValueOrDefault(LevelPanelKey(name + "_gained_on_enemy_death")));
        inventory.Add(5, 0, 0, ItemCatalog.Create(catalog.Find("Gold Amulet")!) with
            { Mods = [PercentageAttributeMod(catalog, "additional_all_attributes", 200)] });
        game.State.Pools = Program.PoolsFor(game.State, 20);
        var qualified = Program.CharacterPlayerStats(game.State);
        foreach (string name in new[] { "life", "mana", "energy_shield" })
            Assert.Equal(20, qualified[LevelPanelKey(name + "_gained_on_enemy_death")]);
        Assert.NotNull(inventory.Remove(PlayerInventories.BodyArmour, body.Id));
        game.State.Pools = Program.PoolsFor(game.State, 20);
        foreach (string name in new[] { "life", "mana", "energy_shield" })
            Assert.Equal(0, Program.CharacterPlayerStats(game.State).GetValueOrDefault(LevelPanelKey(name + "_gained_on_enemy_death")));
    }

    [Theory]
    [InlineData(0, 72)]
    [InlineData(1, 82)]
    [InlineData(3, 102)]
    public void Real_combined_and_block_only_modifiers_plus_expanded_frenzy_recovery_count_once(int held, int expected)
    {
        using var game = LevelPanelCharacter();
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "StunRecovery1", "base_stun_recovery_+%", 12),
            LevelPanelMod(catalog, "BlockRecoveryImplicitShield1", "block_recovery_+%", 60)));
        // Native stat exists; this controlled per-charge input is expanded by the actual charge session once.
        game.State.PlayerStats[LevelPanelKey("stun_recovery_+%_per_frenzy_charge")] = 10;
        var charges = ProgramValue<ChargeSession>("ChargesOf", game.State);
        charges.State.Set(ChargeType.Frenzy, held, game.State.Clock.Elapsed.TotalSeconds, new(3, 0, Charges.BaseDurationMs));
        for (int i = 0; i < 2; i++)
        {
            var values = LevelPanelPublished(game);
            Assert.Equal(expected, values[LevelPanelKey("virtual_block_recovery_+%")]);
            Assert.Equal(expected - 60, values[LevelPanelKey("stun_recovery_+%")]);
        }
        game.State.PlayerStats[LevelPanelKey("virtual_block_recovery_+%")] = 777;
        Assert.Equal(777, LevelPanelPublished(game)[LevelPanelKey("virtual_block_recovery_+%")]);
        game.State.PlayerStats.Remove(LevelPanelKey("virtual_block_recovery_+%"));
        Assert.Equal(expected, LevelPanelPublished(game)[LevelPanelKey("virtual_block_recovery_+%")]);
        game.State.PlayerStats.Remove(LevelPanelKey("stun_recovery_+%_per_frenzy_charge"));
        Assert.NotNull(inventory.Remove(6, ring.Id));
        Assert.Equal(0, LevelPanelPublished(game)[LevelPanelKey("virtual_block_recovery_+%")]);
        Assert.DoesNotContain(LevelPanelKey("virtual_block_recovery_+%"), game.State.SentStatKeys);
    }

    [Theory]
    [InlineData(10, 130)]
    [InlineData(20, 110)]
    [InlineData(100, -50)]
    public void Real_Nevalius_duration_sources_use_character_level_and_remove_without_cached_aliases(int level, int expected)
    {
        using var game = LevelPanelCharacter(level);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var ordinary = LevelPanelMod(catalog, "BeltIncreasedFlaskDurationUnique__4", "flask_duration_+%", 150);
        var perLevel = LevelPanelMod(catalog, "FlaskDurationPerLevelUnique__1", "flask_duration_+%_per_level", -2);
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", ordinary, perLevel));
        uint final = LevelPanelKey("virtual_flask_duration_+%");
        Assert.Equal(expected, LevelPanelPublished(game)[final]);
        Assert.Equal(expected, LevelPanelPublished(game)[final]);
        game.State.PlayerStats[final] = 777;
        Assert.Equal(777, LevelPanelPublished(game)[final]);
        game.State.PlayerStats.Remove(final);
        Assert.NotNull(inventory.Change(6, ring.Id, item => item with { Mods = [ordinary] }));
        Assert.Equal(150, LevelPanelPublished(game)[final]);
        Assert.NotNull(inventory.Remove(6, ring.Id));
        Assert.Equal(0, LevelPanelPublished(game)[final]);
        Assert.DoesNotContain(final, game.State.SentStatKeys);
    }
}
