using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static InventoryItem EquipFlaskEffectPair(GatewayCharacter game)
    {
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        return inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "BeltIncreasedFlaskEffectUnique__2", "flask_effect_+%", -60),
            LevelPanelMod(catalog, "FlaskEffectPerLevelUnique__1", "flask_effect_+%_per_level", 1)));
    }
    static void AssertRawEffectSources(GatewayCharacter game, int flat, int perLevel)
    {
        var raw = Program.CharacterPlayerStats(game.State);
        Assert.Equal(flat, raw.GetValueOrDefault(LevelPanelKey("flask_effect_+%")));
        Assert.Equal(perLevel, raw.GetValueOrDefault(LevelPanelKey("flask_effect_+%_per_level")));
        var published = LevelPanelPublished(game);
        Assert.Equal(flat, published.GetValueOrDefault(LevelPanelKey("flask_effect_+%")));
        Assert.Equal(perLevel, published.GetValueOrDefault(LevelPanelKey("flask_effect_+%_per_level")));
    }
    static void HoldEffectTestTicker(GatewayCharacter game, UtilityFlasks.Utility utility) =>
        Assert.NotNull(game.State.UtilityFlaskEffects.Start(new(uint.MaxValue, 4, "fixture-owned ticker", utility,
            ushort.MaxValue, null, [], 0, double.PositiveInfinity)));

    [Theory]
    [InlineData(10, "Quicksilver Flask", "base_movement_velocity_+%", 20, 6)]
    [InlineData(20, "Quicksilver Flask", "base_movement_velocity_+%", 24, 6)]
    [InlineData(100, "Granite Flask", "base_physical_damage_reduction_rating", 2100, 6)]
    public async Task Actual_utility_use_resolves_native_level_effect_once_without_replacing_raw_publication(        int level, string name, string stat, int expected, double seconds)
    {
        using var game = LevelPanelCharacter(level); EquipFlaskEffectPair(game);
        var catalog = Program.Items; var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find(name)!; var utility = catalog.UtilityFlaskOf(type)!;
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Quality = 0, Charges = utility.MaxCharges });
        AssertRawEffectSources(game, -60, 1); HoldEffectTestTicker(game, utility);
        try
        {
            var result = await game.UseModifierFlask(); Assert.Null(result.Error);
            var active = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.Contains(active.Stats, s => s.Stat == stat && s.Value == expected);
            Assert.Equal(seconds, active.Ends - active.Started, 8);
            AssertRawEffectSources(game, -60, 1);
        }
        finally { game.State.UtilityFlaskEffects.End(0, true, out _); }
    }

    [Theory]
    [InlineData(10, 35)]
    [InlineData(20, 42)]
    [InlineData(100, 98)]
    public async Task Actual_life_use_passes_captured_level_into_the_shared_effect_resolver(int level, double expected)
    {
        using var game = LevelPanelCharacter(level); EquipFlaskEffectPair(game);
        var catalog = Program.Items; var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find("Small Life Flask")!; var native = catalog.FlaskOf(type)!;
        inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Quality = 0, Charges = native.MaxCharges });
        game.State.PlayerLife = game.State.Pools.Life / 2;
        try
        {
            Assert.Null((await game.UseModifierFlask()).Error);
            var effect = Assert.Single(game.State.FlaskEffects.End(_ => true));
            Assert.Equal(3, effect.Ends - effect.Started, 8);
            Assert.Equal(expected, effect.LifePerSecond * (effect.Ends - effect.Started), 8);
            AssertRawEffectSources(game, -60, 1);
        }
        finally { game.State.FlaskEffects.End(_ => true); }
    }

    [Fact]
    public async Task Level_and_source_changes_affect_the_next_utility_use_while_existing_sources_remain_snapshotted()
    {
        using var game = LevelPanelCharacter(10); var source = EquipFlaskEffectPair(game);
        var catalog = Program.Items; var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find("Quicksilver Flask")!; var utility = catalog.UtilityFlaskOf(type)!;
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Quality = 0, Charges = utility.MaxCharges });
        void ResetUse()
        {
            game.State.UtilityFlaskEffects.End(0, true, out _);
            Assert.NotNull(inventory.Change(PlayerInventories.Flask, flask.Id, item => item with { Charges = utility.MaxCharges }));
            HoldEffectTestTicker(game, utility);
        }
        HoldEffectTestTicker(game, utility);
        try
        {
            Assert.Null((await game.UseModifierFlask()).Error);
            var first = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.Contains(first.Stats, s => s.Stat == "base_movement_velocity_+%" && s.Value == 20);
            Assert.Null((await game.SetLevel(20)).Error);
            Assert.Contains(first.Stats, s => s.Stat == "base_movement_velocity_+%" && s.Value == 20);
            AssertRawEffectSources(game, -60, 1);
            ResetUse(); Assert.Null((await game.UseModifierFlask()).Error);
            var second = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.Contains(second.Stats, s => s.Stat == "base_movement_velocity_+%" && s.Value == 24);
            Assert.NotNull(inventory.Remove(6, source.Id));
            Assert.Contains(second.Stats, s => s.Stat == "base_movement_velocity_+%" && s.Value == 24);
            ResetUse(); Assert.Null((await game.UseModifierFlask()).Error);
            Assert.Contains(game.State.UtilityFlaskEffects.Of(flask.Id)!.Stats,
                s => s.Stat == "base_movement_velocity_+%" && s.Value == 40);
            AssertRawEffectSources(game, 0, 0);
        }
        finally { game.State.UtilityFlaskEffects.End(0, true, out _); }
    }

    [Fact]
    public async Task Each_raw_effect_override_replaces_only_its_own_source_for_actual_utility_use()
    {
        using var game = LevelPanelCharacter(100); EquipFlaskEffectPair(game);
        var catalog = Program.Items; var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find("Granite Flask")!; var utility = catalog.UtilityFlaskOf(type)!;
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Quality = 0, Charges = utility.MaxCharges });
        try
        {
            foreach (var (flat, perLevel, expected) in new[] { (0, 1, 3000), (-60, 0, 600), (0, 0, 1500) })
            {
                game.State.PlayerStats[LevelPanelKey("flask_effect_+%")] = flat;
                game.State.PlayerStats[LevelPanelKey("flask_effect_+%_per_level")] = perLevel;
                AssertRawEffectSources(game, flat, perLevel);
                Assert.NotNull(inventory.Change(PlayerInventories.Flask, flask.Id, item => item with { Charges = utility.MaxCharges }));
                HoldEffectTestTicker(game, utility); Assert.Null((await game.UseModifierFlask()).Error);
                Assert.Contains(game.State.UtilityFlaskEffects.Of(flask.Id)!.Stats,
                    s => s.Stat == "base_physical_damage_reduction_rating" && s.Value == expected);
                game.State.UtilityFlaskEffects.End(0, true, out _);
            }
        }
        finally { game.State.UtilityFlaskEffects.End(0, true, out _); }
    }

    [Theory]
    [InlineData(4.02)]
    public async Task Actual_magic_utility_keeps_native_local_and_magic_only_buckets_separate_from_duration(double seconds)
    {
        using var game = LevelPanelCharacter(100); EquipFlaskEffectPair(game);
        var catalog = Program.Items; var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        inventory.Add(7, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "RecombinatorSpecialMagicUtilityFlaskEffect", "magic_utility_flask_effect_+%", 20)));
        var type = catalog.Find("Granite Flask")!; var utility = catalog.UtilityFlaskOf(type)!;
        var prefix = catalog.Mods.ById("FlaskEffectReducedDuration1")!;
        Assert.Equal(2, prefix.Domain); Assert.Equal(["local_flask_duration_+%", "local_flask_effect_+%"], prefix.Stats.Select(s => s.Id));
        Assert.InRange(-33, prefix.Stats[0].Min, prefix.Stats[0].Max); Assert.InRange(25, prefix.Stats[1].Min, prefix.Stats[1].Max);
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with
            { Quality = 0, ItemLevel = 100, Rarity = ItemRarity.Magic, Charges = utility.MaxCharges,
                Mods = [new(ModSection.Explicit, prefix.Hash, [-33, 25])] });
        HoldEffectTestTicker(game, utility);
        try
        {
            Assert.Null((await game.UseModifierFlask()).Error);
            var active = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.Contains(active.Stats, s => s.Stat == "base_physical_damage_reduction_rating" && s.Value == 2775);
            Assert.Equal(seconds, active.Ends - active.Started, 8);
            AssertRawEffectSources(game, -60, 1);
        }
        finally { game.State.UtilityFlaskEffects.End(0, true, out _); }
    }

    [Fact]
    public async Task Actual_instant_life_use_resolves_effect_without_admitting_magic_utility_only_source()
    {
        using var game = LevelPanelCharacter(100); EquipFlaskEffectPair(game);
        var catalog = Program.Items; var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        inventory.Add(7, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "RecombinatorSpecialMagicUtilityFlaskEffect", "magic_utility_flask_effect_+%", 20)));
        var type = catalog.Find("Small Life Flask")!; var native = catalog.FlaskOf(type)!;
        var instant = catalog.Mods.ById("FlaskFullInstantRecovery1")!;
        Assert.Equal(2, instant.Domain);
        Assert.Equal(["local_flask_recovers_instantly", "local_flask_amount_to_recover_+%"], instant.Stats.Select(s => s.Id));
        inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with
            { Quality = 20, ItemLevel = 100, Rarity = ItemRarity.Magic, Charges = native.MaxCharges,
                Mods = [new(ModSection.Explicit, instant.Hash, [1, -66])] });
        uint before = game.State.PlayerLife = game.State.Pools.Life / 2;
        Assert.Null((await game.UseModifierFlask()).Error);
        Assert.Equal(before + 39u, game.State.PlayerLife); // Floor of native70*Q1.2*amount.34*effect1.4=39.984.
        Assert.Empty(game.State.FlaskEffects.End(_ => true));
        AssertRawEffectSources(game, -60, 1);
    }
}
