using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static InventoryItem EquipDurationSources(GatewayCharacter game)
    {
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        // Exact native duration pair only. Nevalius' independent effect modifiers are a separate operator gap.
        return inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "BeltIncreasedFlaskDurationUnique__4", "flask_duration_+%", 150),
            LevelPanelMod(catalog, "FlaskDurationPerLevelUnique__1", "flask_duration_+%_per_level", -2)));
    }

    [Theory]
    [InlineData(10, 13.8)]
    [InlineData(20, 12.6)]
    [InlineData(100, 3.0)]
    public async Task Actual_utility_use_passes_current_player_level_with_real_duration_modifiers(int level, double expected)
    {
        using var game = LevelPanelCharacter(level);
        EquipDurationSources(game);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find("Granite Flask")!;
        var utility = catalog.UtilityFlaskOf(type)!;
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Quality = 0, Charges = utility.MaxCharges });
        // Reserve the existing ticker ownership: the fixture drives effect state and ends it before its wire closes.
        // This exercises the actual use/charge/buff path without an unowned background sender surviving Exchange.
        Assert.NotNull(game.State.UtilityFlaskEffects.Start(new(uint.MaxValue, 4, "fixture-owned ticker", utility,
            ushort.MaxValue, null, [], 0, double.PositiveInfinity)));
        try
        {
            var result = await game.UseModifierFlask();
            Assert.Null(result.Error);
            Assert.NotEmpty(result.Bytes);
            var active = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.NotNull(active);
            Assert.Equal(expected, active.Ends - active.Started, 8);
            Assert.Equal(utility.MaxCharges - utility.ChargesPerUse,
                inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
        }
        finally { game.State.UtilityFlaskEffects.End(game.State.Clock.Elapsed.TotalSeconds, true, out _); }
    }

    [Theory]
    [InlineData(10, 161, 6.9)]
    [InlineData(20, 147, 6.3)]
    [InlineData(100, 35, 1.5)]
    public async Task Actual_recovery_use_passes_current_player_level_and_schedules_the_fixed_amount(        int level, double expectedLife, double expectedSeconds)
    {
        using var game = LevelPanelCharacter(level);
        EquipDurationSources(game);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find("Small Life Flask")!;
        var native = catalog.FlaskOf(type)!;
        inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Quality = 0, Charges = native.MaxCharges });
        game.State.PlayerLife = game.State.Pools.Life / 2;
        try
        {
            var result = await game.UseModifierFlask();
            Assert.Null(result.Error);
            var parts = game.State.FlaskEffects.End(_ => true);
            var effect = Assert.Single(parts);
            Assert.Equal(expectedSeconds, effect.Ends - effect.Started, 8);
            Assert.Equal(expectedLife, effect.LifePerSecond * (effect.Ends - effect.Started), 8);
            Assert.Equal(0, effect.ManaPerSecond);
        }
        finally { game.State.FlaskEffects.End(_ => true); }
    }

    [Fact]
    public async Task Utility_local_duration_multiplies_current_global_duration_after_level_change_and_source_removal()
    {
        using var game = LevelPanelCharacter(10);
        var source = EquipDurationSources(game);
        Assert.Null((await game.SetLevel(20)).Error);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var type = catalog.Find("Granite Flask")!;
        var utility = catalog.UtilityFlaskOf(type)!;
        var mod = catalog.Mods.ById("FlaskIncreasedDuration4")!;
        Assert.Equal(2, mod.Domain);
        Assert.Equal("local_flask_duration_+%", Assert.Single(mod.Stats).Id);
        Assert.InRange(30, mod.Stats[0].Min, mod.Stats[0].Max);
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with
            { Quality = 0, Charges = utility.MaxCharges, Mods = [new(ModSection.Explicit, mod.Hash, [30])] });
        Assert.NotNull(game.State.UtilityFlaskEffects.Start(new(uint.MaxValue, 4, "fixture-owned ticker", utility,
            ushort.MaxValue, null, [], 0, double.PositiveInfinity)));
        try
        {
            Assert.Null((await game.UseModifierFlask()).Error);
            var first = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.Equal(16.38, first.Ends - first.Started, 8);
            game.State.UtilityFlaskEffects.End(0, true, out _);
            Assert.NotNull(inventory.Remove(6, source.Id));
            Assert.NotNull(inventory.Change(PlayerInventories.Flask, flask.Id, item => item with { Charges = utility.MaxCharges }));
            Assert.NotNull(game.State.UtilityFlaskEffects.Start(new(uint.MaxValue, 4, "fixture-owned ticker", utility,
                ushort.MaxValue, null, [], 0, double.PositiveInfinity)));
            Assert.Null((await game.UseModifierFlask()).Error);
            var plainGlobal = game.State.UtilityFlaskEffects.Of(flask.Id)!;
            Assert.Equal(7.8, plainGlobal.Ends - plainGlobal.Started, 8);
        }
        finally { game.State.UtilityFlaskEffects.End(0, true, out _); }
    }
}
