using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    sealed record LifeManaPoolSource(string Stat, string Mod, int Value, int Row, ushort Hash, uint Key);
    static readonly LifeManaPoolSource[] LifeManaPoolNative =
    [
        new("life_per_level", "LifePerLevelUnique__1", 2, 14152, 35467, 369),
        new("mana_per_level", "ManaPerLevelUnique__1", 2, 14153, 14072, 370),
        new("maximum_life_per_10_dexterity", "MaximumLifePer10DexterityUnique__1", 2, 13772, 59294, 5473),
        new("maximum_life_per_10_intelligence", "LifePer10IntelligenceUnique__1", 2, 21199, 3738, 9749),
        new("X_life_per_4_dexterity", "LifePerDexterityUnique__1", 1, 13591, 37678, 4995),
        new("x_to_maximum_life_per_2_intelligence", "IncreasedLifePerIntelligenceUnique__1", 1, 16634, 64327, 7963),
        new("mana_per_level", "MutatedUniqueRing26ManaPerLevel", 2, 39378, 1337, 370),
    ];

    static uint LifeManaPoolKey(string stat) => Program.Stats.Key(stat)!.Value;
    static PlayerInventories LifeManaPoolInventory(GatewayCharacter game) => ProgramValue<PlayerInventories>("ItemsOf", game.State);
    static ItemMod LifeManaPoolNativeMod(ItemCatalog catalog, LifeManaPoolSource source)
    {
        var row = catalog.Mods.ById(source.Mod)!;
        Assert.Equal(source.Row, row.Row);
        Assert.Equal(source.Hash, row.Hash);
        Assert.Equal(source.Key, LifeManaPoolKey(source.Stat));
        return LevelPanelMod(catalog, source.Mod, source.Stat, source.Value);
    }

    // Source-isolation carriers use actual native modifiers and in-range values. They do not claim that these
    // unique/implicit combinations can be acquired on an Iron Ring or other carrier in normal play.
    static ItemMod LifeManaPoolAttributeMod(ItemCatalog catalog, string stat, int value)
    {
        var row = catalog.Mods.All.Where(m => m.Domain == 1 && m.Stats.Count == 1
            && m.Stats[0].Id == stat && !m.Stats[0].Local && m.Stats[0].Min <= value && m.Stats[0].Max >= value
            && catalog.Mods.ByHash(m.Hash)?.Id == m.Id).OrderBy(m => m.Row).First();
        return LevelPanelMod(catalog, row.Id, stat, value);
    }

    static Dictionary<uint, int> LifeManaPoolRefresh(GatewayCharacter game)
    {
        game.State.Pools = Program.PoolsFor(game.State, game.State.Pools.Level);
        return LevelPanelPublished(game);
    }

    static void LifeManaPoolAssert(GatewayCharacter game, uint life, uint mana)
    {
        for (int i = 0; i < 2; i++)
        {
            var published = LifeManaPoolRefresh(game);
            Assert.Equal(life, game.State.Pools.Life);
            Assert.Equal(mana, game.State.Pools.Mana);
            Assert.Equal((int)life, published[LifeManaPoolKey("maximum_life")]);
            Assert.Equal((int)mana, published[LifeManaPoolKey("maximum_mana")]);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Life_mana_pool_real_per_level_items_add_both_mana_sources_and_follow_actual_level_and_removal(
        int level)
    {
        using var game = LevelPanelCharacter(level);
        var catalog = Program.Items;
        var items = LifeManaPoolInventory(game);
        uint BaseLife(int current) => (uint)(45 + 12 * current); // Witch: base38 + floor(Strength14/2).
        uint BaseMana(int current) => (uint)(50 + 6 * current); // Base34 + floor(Intelligence32/2).
        LifeManaPoolAssert(game, BaseLife(level), BaseMana(level));
        var first = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LifeManaPoolNativeMod(catalog, LifeManaPoolNative[0]), LifeManaPoolNativeMod(catalog, LifeManaPoolNative[1])));
        var second = items.Add(7, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LifeManaPoolNativeMod(catalog, LifeManaPoolNative[6])));
        void Supported(int current)
        {
            LifeManaPoolAssert(game, BaseLife(current) + (uint)(2 * current), BaseMana(current) + (uint)(4 * current));
            var raw = Program.CharacterPlayerStats(game.State);
            Assert.Equal(2, raw[369]);
            Assert.Equal(4, raw[370]);
        }
        Supported(level);
        int otherLevel = level == 10 ? 100 : 10;
        Assert.Null((await game.SetLevel(otherLevel)).Error);
        Assert.Equal(otherLevel, game.State.Pools.Level);
        Supported(otherLevel);
        Assert.Null((await game.SetLevel(level)).Error);
        Supported(level);
        Assert.NotNull(items.Remove(6, first.Id));
        LifeManaPoolAssert(game, BaseLife(level), BaseMana(level) + (uint)(2 * level));
        Assert.NotNull(items.Remove(7, second.Id));
        LifeManaPoolAssert(game, BaseLife(level), BaseMana(level));
        Assert.DoesNotContain(369u, game.State.PlayerStats.Keys);
        Assert.DoesNotContain(370u, game.State.PlayerStats.Keys);
    }

    [Theory]
    [InlineData(9, 6, 1501, 638)]
    [InlineData(10, 11, 1507, 639)]
    [InlineData(19, 17, 1514, 643)]
    [InlineData(20, 23, 1521, 644)]
    public void Life_mana_pool_attribute_groups_use_completed_final_attributes_before_native_life_increase(
        int attribute, int flatLife, int expectedLife, int expectedMana)
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        var items = LifeManaPoolInventory(game);
        // Native -100% plus two distinct native +10% inputs leaves 20%. Flat sources make each pre-percent
        // attribute exactly five times the desired boundary, so these controls do not depend on fractional rounding.
        var attributes = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LifeManaPoolAttributeMod(catalog, "additional_dexterity", attribute * 5 - 14),
            LifeManaPoolAttributeMod(catalog, "additional_intelligence", attribute * 5 - 32),
            LevelPanelMod(catalog, "PercentageDexterityUnique__1", "dexterity_+%", -100),
            LevelPanelMod(catalog, "TalismanIncreasedDexterity", "dexterity_+%", 10),
            LevelPanelMod(catalog, "PercentageDexterityUber2", "dexterity_+%", 10),
            LevelPanelMod(catalog, "PercentageIntelligenceUnique__1", "intelligence_+%", -100),
            LevelPanelMod(catalog, "TalismanIncreasedIntelligence", "intelligence_+%", 10),
            LevelPanelMod(catalog, "PercentageIntelligenceUber2", "intelligence_+%", 10),
            LevelPanelMod(catalog, "MaximumLifeUnique__5", "maximum_life_+%", 20)));
        var coefficients = items.Add(7, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LifeManaPoolNative[2..6].Select(s => LifeManaPoolNativeMod(catalog, s)).ToArray()));
        var published = LifeManaPoolRefresh(game);
        // The two native Uber modifiers have Level83 and therefore require character level66.
        Assert.Equal(66, ItemModStats.LevelRequirement(attributes.Item,
            catalog.ByHash(attributes.Item.TypeHash)!.DropLevel, catalog.Mods));
        foreach (var carrier in new[] { attributes, coefficients })
        {
            Assert.DoesNotContain(carrier.Id, Program.InactiveItems[game.Character]);
            Assert.InRange(ItemModStats.LevelRequirement(carrier.Item,
                catalog.ByHash(carrier.Item.TypeHash)!.DropLevel, catalog.Mods), 0, game.State.Pools.Level);
        }
        Assert.Equal((14, attribute, attribute),
            (game.State.Pools.Strength, game.State.Pools.Dexterity, game.State.Pools.Intelligence));
        Assert.Equal(attribute, published[LifeManaPoolKey("dexterity")]);
        Assert.Equal(attribute, published[LifeManaPoolKey("intelligence")]);
        Assert.Equal(flatLife, 2 * (attribute / 10) + 2 * (attribute / 10) + attribute / 4 + attribute / 2);
        LifeManaPoolAssert(game, (uint)expectedLife, (uint)expectedMana);
        Assert.NotNull(items.Remove(7, coefficients.Id));
        LifeManaPoolAssert(game, 1494, (uint)expectedMana); // floor((38 + 1200 + 7) * 1.20).
    }

    [Fact]
    public void Life_mana_pool_item_sources_require_the_active_weapon_set_and_a_qualified_host()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        var items = LifeManaPoolInventory(game);
        var mods = LifeManaPoolNative.Select(s => LifeManaPoolNativeMod(catalog, s)).ToArray();
        items.Add(PlayerInventories.Weapon2, 0, 0, LevelPanelCarrier(catalog, "Rusted Sword", mods));
        LifeManaPoolAssert(game, 1245, 650);
        items.SwapWeaponSets();
        LifeManaPoolAssert(game, 1472, 1050);
        items.SwapWeaponSets();
        LifeManaPoolAssert(game, 1245, 650);

        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, LevelPanelCarrier(catalog, "Chestplate", mods));
        LifeManaPoolAssert(game, 1245, 650);
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);
        items.Add(5, 0, 0, LevelPanelCarrier(catalog, "Coral Amulet",
            LevelPanelMod(catalog, "AllAttributesUniqueAmulet8", "additional_all_attributes", 100)));
        LifeManaPoolAssert(game, 1637, 1100);
        Assert.DoesNotContain(body.Id, Program.InactiveItems[game.Character]);
        Assert.NotNull(items.Remove(PlayerInventories.BodyArmour, body.Id));
        LifeManaPoolAssert(game, 1295, 700);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Life_mana_pool_actual_set_stat_zero_rebuilds_and_clamps_resources_with_reservation(
        int sourceIndex)
    {
        using var game = LevelPanelCharacter();
        var source = LifeManaPoolNative[sourceIndex];
        var catalog = Program.Items;
        var items = LifeManaPoolInventory(game);
        bool manaSource = sourceIndex == 1;
        var mods = new List<ItemMod> { LifeManaPoolNativeMod(catalog, source) };
        if (manaSource) mods.Add(LifeManaPoolNativeMod(catalog, LifeManaPoolNative[6]));
        items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", mods.ToArray()));
        // Controlled active reservation state, as in StrengthManaTests; no aura activation/acquisition claim.
        var skills = Program.Skills;
        game.State.Auras[0x103] = new(0x103, skills.EffectRow("Haste"), "Haste", "Haste",
            1, AuraCatalog.BuffRow("Haste"), 0, 5000);
        int Groups() => sourceIndex switch { 0 or 1 => 10, 2 => 1, 3 => 3, 4 => 3, 5 => 16, _ => 0 };
        int original = manaSource ? 4 : source.Value;
        uint Life(int coefficient) => (uint)(165 + (manaSource ? 0 : coefficient * Groups()));
        uint Mana(int coefficient) => (uint)(110 + (manaSource ? coefficient * Groups() : 0));
        LifeManaPoolAssert(game, Life(original), Mana(original));
        game.State.PlayerLife = game.State.Pools.Life;
        game.State.PlayerMana = (uint)ProgramValue<int>("UnreservedMana", game.State);
        var cleared = await game.Command($"/set_stat {source.Stat} 0");
        Assert.Null(cleared.Error);
        Assert.Equal(0, game.State.PlayerStats[source.Key]);
        Assert.Equal(165u, game.State.Pools.Life);
        Assert.Equal(110u, game.State.Pools.Mana);
        Assert.Equal(165u, game.State.PlayerLife);
        Assert.Equal(55u, game.State.PlayerMana);
        Assert.Equal(55, ProgramValue<int>("ReservedMana", game.State));
        LifeManaPoolAssert(game, 165, 110);
        Assert.Equal(0, LevelPanelPublished(game)[source.Key]);
        var expectedPacket = manaSource
            ? InstanceProtocol.LifeUpdate(Program.PlayerId, 55, 55, pool: Program.ManaPool)
            : InstanceProtocol.LifeUpdate(Program.PlayerId, 165);
        LifeManaPoolPacket(cleared.Bytes, expectedPacket);

        Assert.Null((await game.Command($"/set_stat {source.Stat} 1")).Error);
        LifeManaPoolAssert(game, Life(1), Mana(1));
        Assert.Equal(165u, game.State.PlayerLife); // Raising a maximum does not heal the current resource.
        Assert.Equal(55u, game.State.PlayerMana);
        if (sourceIndex is 0 or 1)
        {
            Assert.Null((await game.Command($"/set_stat {source.Stat} {(manaSource ? -6 : -12)}")).Error);
            Assert.Equal(manaSource ? 165u : 45u, game.State.Pools.Life);
            Assert.Equal(manaSource ? 50u : 110u, game.State.Pools.Mana);
            Assert.Equal(manaSource ? 165u : 45u, game.State.PlayerLife);
            Assert.Equal(manaSource ? 25u : 55u, game.State.PlayerMana);
        }
        Assert.True(game.State.PlayerStats.Remove(source.Key));
        LifeManaPoolAssert(game, Life(original), Mana(original));
        Assert.Equal(original, LevelPanelPublished(game)[source.Key]);
    }

    static void LifeManaPoolPacket(byte[] actual, byte[] expected) =>
        Assert.True(actual.AsSpan().IndexOf(expected) >= 0, "Expected the command to publish the clamped resource packet.");
}
