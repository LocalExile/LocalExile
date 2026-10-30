using System.Reflection;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task A_real_charge_gain_mod_affects_actual_flask_awards_and_final_virtual_replacements()
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Slayer");
        var catalog = Program.Items;
        var inventory = PlayerInventories.For(game.Character, catalog);
        var belt = inventory.Add(10, 0, 0, FlaskModifierBelt(catalog, "BeltIncreasedFlaskChargesGained1", 100));
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0,
            ItemCatalog.Create(catalog.Find("Metadata/Items/Flasks/FlaskLife1")!) with { Charges = 0 });
        uint rawKey = Program.Stats.Key("charges_gained_+%")!.Value;
        uint finalKey = Program.Stats.Key("flask_charges_gained_+%")!.Value;

        Assert.Equal(100, Program.CharacterPlayerStats(game.State)[rawKey]);
        Assert.Equal(100, Program.CharacterPlayerStats(game.State)[finalKey]);
        await game.GiveModifierFlaskCharges(4);
        Assert.Equal(8, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);

        game.State.PlayerStats[finalKey] = 0;
        Assert.Equal(0, Program.CharacterPlayerStats(game.State)[finalKey]);
        await game.GiveModifierFlaskCharges(4);
        Assert.Equal(12, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);

        game.State.PlayerStats[finalKey] = 20;
        await game.GiveModifierFlaskCharges(4);
        Assert.Equal(16, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
        Assert.Equal(0.8, game.State.FlaskChargeCarry.Partial(flask.Id), 8);

        game.State.PlayerStats.Remove(finalKey);
        Assert.NotNull(inventory.Change(10, belt.Id, item => item with { Mods = [] }));
        Assert.Equal(0, Program.CharacterPlayerStats(game.State)[finalKey]);
        await game.GiveModifierFlaskCharges(4);
        Assert.Equal(20, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_belt_Mana_rate_changes_actual_flask_recovery_without_shortening_hybrid_Life(
        bool hybrid)
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Slayer");
        var catalog = Program.Items;
        var inventory = PlayerInventories.For(game.Character, catalog);
        inventory.Add(10, 0, 0, FlaskModifierBelt(catalog, "BeltFlaskManaRecoveryRate1_", 100));
        var type = catalog.Find(hybrid ? "Small Hybrid Flask" : "Metadata/Items/Flasks/FlaskMana1")!;
        var native = catalog.FlaskOf(type)!;
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Charges = native.MaxCharges });
        game.State.PlayerLife = game.State.Pools.Life / 2;
        game.State.PlayerMana = 0;
        ushort beforeBuff = game.State.NextBuff;

        var result = await game.UseModifierFlask();

        Assert.NotEmpty(result.Bytes);
        Assert.Equal(native.MaxCharges - native.ChargesPerUse,
            inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
        Assert.Equal((ushort)(beforeBuff + (hybrid ? 2 : 1)), game.State.NextBuff);
        var parts = game.State.FlaskEffects.End(_ => true);
        Assert.Equal(hybrid ? 2 : 1, parts.Count);
        double started = parts.Min(p => p.Started);
        Assert.Empty(game.State.FlaskEffects.KeepRunning(parts, lifeFull: false, manaFull: false, started));
        var first = game.State.FlaskEffects.Advance(started, started + native.Seconds / 2);
        Assert.Equal(native.ManaPerUse, first.Mana, 6);
        Assert.Equal(hybrid ? native.LifePerUse / 2.0 : 0, first.Life, 6);
        Assert.Single(first.Ended);
        Assert.Equal((ushort)9, first.Ended[0].BuffRow);
        Assert.Equal(hybrid, game.State.FlaskEffects.Any);
        if (hybrid)
        {
            var rest = game.State.FlaskEffects.Advance(started + native.Seconds / 2, started + native.Seconds);
            Assert.Equal(native.LifePerUse / 2.0, rest.Life, 6);
            Assert.Equal(0, rest.Mana);
            Assert.Equal((ushort)5, Assert.Single(rest.Ended).BuffRow);
        }
        Assert.False(game.State.FlaskEffects.Any);
    }

    [Fact]
    public async Task Increased_gains_apply_to_an_active_ordinary_utility_flask()
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Slayer");
        var catalog = Program.Items;
        var inventory = PlayerInventories.For(game.Character, catalog);
        inventory.Add(10, 0, 0, FlaskModifierBelt(catalog, "BeltIncreasedFlaskChargesGained1", 100));
        var type = catalog.Find("Granite Flask")!;
        var flask = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Charges = 0 });
        game.State.UtilityFlaskEffects.Start(new(flask.Id, 0, type.Name, catalog.UtilityFlaskOf(type)!, 10, null, [], 0, 1000));

        await game.GiveModifierFlaskCharges(4);

        Assert.Equal(8, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
    }

    [Fact]
    public void The_native_passive_and_belt_sources_use_raw_charge_gain_and_Mana_rate_keys()
    {
        uint charge = Program.Stats.Key("charges_gained_+%")!.Value;
        Assert.Equal(10, Program.Passives.Stats([3676])[charge]);
        var gain = Program.Items.Mods.ById("BeltIncreasedFlaskChargesGained1")!;
        Assert.Contains(gain.Stats, stat => stat.Id == "charges_gained_+%");
        var rate = Program.Items.Mods.ById("BeltFlaskManaRecoveryRate1_")!;
        Assert.Contains(rate.Stats, stat => stat.Id == "flask_mana_recovery_rate_+%");
    }

    [Fact]
    public void Recovery_buff_constants_are_zero_based_native_BuffDefinitions_rows()
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes("data/tables/BuffDefinitions.json"));
        var rows = table.RootElement.EnumerateArray().Select((row, index) => (Row: row, Index: index)).ToArray();
        Assert.Equal(5, Assert.Single(rows, r => r.Row.GetProperty("Id").GetString() == "flask_effect_life").Index);
        Assert.Equal(9, Assert.Single(rows, r => r.Row.GetProperty("Id").GetString() == "flask_effect_mana").Index);
        Assert.Equal("flask_effect_life", rows[Program.FlaskEffectLifeBuffRow].Row.GetProperty("Id").GetString());
        Assert.Equal("flask_effect_mana", rows[Program.FlaskEffectManaBuffRow].Row.GetProperty("Id").GetString());
    }

    static Item FlaskModifierBelt(ItemCatalog catalog, string modifier, int value)
    {
        var mod = catalog.Mods.ById(modifier)!;
        return ItemCatalog.Create(catalog.Find("Rustic Sash")!) with
            { Mods = [new(ModSection.Explicit, mod.Hash, mod.Stats.Select(s => value).ToArray())] };
    }

    sealed partial class GatewayCharacter
    {
        static readonly MethodInfo ModifierFlaskUse = GatewayMethod("UseFlask");
        static readonly MethodInfo ModifierFlaskGain = typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "GainFlaskCharges" && m.GetParameters().Length == 6
                && m.GetParameters()[4].ParameterType == typeof(Func<InventoryItem, IReadOnlyDictionary<string, int>, double>));

        public Task<(string? Error, byte[] Bytes)> GiveModifierFlaskCharges(double amount) => Exchange(async (stream, cipher) =>
        {
            Func<InventoryItem, IReadOnlyDictionary<string, int>, double> gain = (_, _) => amount;
            await (Task)ModifierFlaskGain.Invoke(null, ["flask-modifier-test", stream, cipher, State, gain, "modifier test"])!;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> UseModifierFlask() => Exchange(async (stream, cipher) =>
        {
            await (Task)ModifierFlaskUse.Invoke(null, ["flask-modifier-test", stream, cipher, State, 0u])!;
            return null;
        });
    }
}
