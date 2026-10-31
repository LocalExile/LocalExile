using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    const string FrenzyCombinedSpeed = "attack_and_cast_speed_+%_per_frenzy_charge";
    const string FrenzyAttackSpeed = "attack_speed_+%_per_frenzy_charge";
    const string FrenzyCastSpeed = "cast_speed_+%_per_frenzy_charge";
    const string FrenzyCombinedMod = "AttackAndCastSpeedPerFrenzyChargeUniqueBootsDex4";
    static uint FrenzyKey(string stat) => Program.Stats.Key(stat)!.Value;

    static (PlayerInventories Inventory, Item Boots) FrenzyGear(GatewayCharacter game)
    {
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        // A single real weapon avoids the separate inherent dual-wield speed multiplier of an empty hand.
        inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(catalog.Find("Driftwood Wand")!));
        var attributes = catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == "additional_all_attributes"));
        inventory.Add(6, 0, 0, ItemCatalog.Create(catalog.Find("Iron Ring")!) with
        {
            Mods = [new(ModSection.Explicit, attributes.Hash,
                attributes.Stats.Select(s => s.Id == "additional_all_attributes" ? 200 : 0).ToArray())],
        });
        var combined = catalog.Mods.All.Single(m => m.Id == FrenzyCombinedMod);
        Assert.Equal(1, combined.Domain);
        var stat = Assert.Single(combined.Stats);
        Assert.Equal(FrenzyCombinedSpeed, stat.Id);
        Assert.False(stat.Local);
        Assert.Equal((-4, -4), (stat.Min, stat.Max));
        // Isolate the actual native Blood Dance modifier on its real base. Attribute gear satisfies its requirements.
        var boots = ItemCatalog.Create(catalog.Find("Sharkskin Boots")!) with
            { Mods = [new(ModSection.Explicit, combined.Hash, [-4])] };
        return (inventory, boots);
    }

    static Dictionary<uint, int> FrenzyRefresh(ClientState state, int held)
    {
        var charges = ProgramValue<ChargeSession>("ChargesOf", state);
        lock (state.Monsters)
            charges.State.Set(ChargeType.Frenzy, held, state.Clock.Elapsed.TotalSeconds, new(3, 0, Charges.BaseDurationMs));
        state.Pools = Program.PoolsFor(state, 100);
        return ProgramValue<List<(uint, int)>>("PlayerStatsWithCleared", state).ToDictionary(x => x.Item1, x => x.Item2);
    }

    static void AssertFrenzySpeed(ClientState state, IReadOnlyDictionary<uint, int> published,
        int attackPerCharge, int castPerCharge, int held)
    {
        Assert.Equal(attackPerCharge, published.GetValueOrDefault(FrenzyKey(FrenzyAttackSpeed)));
        Assert.Equal(castPerCharge, published.GetValueOrDefault(FrenzyKey(FrenzyCastSpeed)));
        Assert.Equal(attackPerCharge * held, state.Pools.AttackSpeedPct);
        Assert.Equal(attackPerCharge * held, published[FrenzyKey("main_hand_attack_speed_+%")]);
        Assert.Equal(castPerCharge * held, published.GetValueOrDefault(FrenzyKey("cast_speed_+%")));
        var weapon = ProgramValue<SkillCatalog.Unarmed>("WeaponFor", state);
        double baseline = weapon.AttackMs / 1000.0;
        Assert.Equal(baseline / (1 + attackPerCharge * held / 100.0), weapon.AttackSeconds(published), 9);
    }

    [Fact]
    public void Native_combined_Frenzy_reduction_clears_both_published_coefficients_and_runtime_speed()
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Elementalist",
            classRow: SkillCatalog.WitchRow, baseClass: 3);
        var (inventory, boots) = FrenzyGear(game);
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 4, 4, 3);
        var placed = inventory.Add(PlayerInventories.Boots, 0, 0, boots);

        var cancelled = FrenzyRefresh(game.State, 3);
        AssertFrenzySpeed(game.State, cancelled, 0, 0, 3);
        Assert.Equal(0, cancelled[FrenzyKey(FrenzyAttackSpeed)]); // Clear values sent by the previous snapshot.
        Assert.Equal(0, cancelled[FrenzyKey(FrenzyCastSpeed)]);
        Assert.DoesNotContain(placed.Id, Program.InactiveItems[game.Character]);
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 0, 0, 3); // No alias feedback on refresh.

        Assert.NotNull(inventory.Remove(PlayerInventories.Boots, placed.Id));
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 4, 4, 3);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(-7)]
    public void Combined_Frenzy_source_replacements_share_one_coefficient_with_actual_held_count_speed(int value)
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Elementalist",
            classRow: SkillCatalog.WitchRow, baseClass: 3);
        var (inventory, boots) = FrenzyGear(game);
        inventory.Add(PlayerInventories.Boots, 0, 0, boots);
        game.State.PlayerStats[FrenzyKey(FrenzyCombinedSpeed)] = value; // Raw replacement, including explicit zero.
        foreach (int held in new[] { 0, 2, 3 })
            AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, held), 4 + value, 4 + value, held);
        game.State.PlayerStats.Remove(FrenzyKey(FrenzyCombinedSpeed));
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 0, 0, 3);
    }

    [Fact]
    public void A_typed_Frenzy_attack_source_and_combined_reduction_do_not_change_the_cast_coefficient()
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Elementalist",
            classRow: SkillCatalog.WitchRow, baseClass: 3);
        var (inventory, boots) = FrenzyGear(game);
        var catalog = Program.Items;
        var attack = catalog.Mods.All.Single(m => m.Id == "AttackSpeedPerFrenzyChargeUniqueGlovesDexInt5");
        inventory.Add(PlayerInventories.Boots, 0, 0, boots with
            { Mods = [.. boots.Mods, new(ModSection.Explicit, attack.Hash, [2])] });
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 2, 0, 3);
    }

    [Fact]
    public void Explicit_final_Frenzy_panel_zero_remains_a_presentation_override()
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Elementalist",
            classRow: SkillCatalog.WitchRow, baseClass: 3);
        FrenzyGear(game);
        game.State.PlayerStats[FrenzyKey(FrenzyCombinedSpeed)] = 3;
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 7, 7, 3);
        game.State.PlayerStats[FrenzyKey(FrenzyAttackSpeed)] = 0;
        game.State.PlayerStats[FrenzyKey(FrenzyCastSpeed)] = 0;
        var zero = FrenzyRefresh(game.State, 3);
        Assert.Equal(0, zero[FrenzyKey(FrenzyAttackSpeed)]);
        Assert.Equal(0, zero[FrenzyKey(FrenzyCastSpeed)]);
        Assert.Equal(21, game.State.Pools.AttackSpeedPct);
        Assert.Equal(21, zero[FrenzyKey("cast_speed_+%")]);
        game.State.PlayerStats.Remove(FrenzyKey(FrenzyAttackSpeed));
        game.State.PlayerStats.Remove(FrenzyKey(FrenzyCastSpeed));
        AssertFrenzySpeed(game.State, FrenzyRefresh(game.State, 3), 7, 7, 3);
    }

    [Fact]
    public void Native_Frenzy_speed_rows_read_the_composed_percentage_keys()
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes("data/tables/CharacterPanelStats.json"));
        foreach (string stat in new[] { FrenzyAttackSpeed, FrenzyCastSpeed })
        {
            int index = (int)FrenzyKey(stat) - 1;
            var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("StatsKeys1")
                .EnumerateArray().Any(k => k.GetInt32() == index));
            Assert.Equal(7, row.GetProperty("CharacterPanelDescriptionModesKey").GetInt32());
        }
    }
}
