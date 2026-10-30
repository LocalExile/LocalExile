using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static uint PercentageAttributeKey(string name) => Program.Stats.Key(name)!.Value;

    static GatewayCharacter PercentageCharacter() => new(level: 100,
        family: AscendancyFamily.Normal, choice: "Elementalist", classRow: SkillCatalog.WitchRow,
        baseClass: 3);

    static (ItemCatalog Catalog, PlayerInventories Inventory) PercentageEquipment(GatewayCharacter game) =>
        (Program.Items, ProgramValue<PlayerInventories>("ItemsOf", game.State));

    // These are controlled source-isolation values on actual native modifier rows. This helper does not claim
    // that arbitrary combinations or supplied values form a legal rolled rare/unique item.
    static ItemMod PercentageAttributeMod(ItemCatalog catalog, string stat, int value, string? exactId = null)
    {
        var row = exactId is null
            ? catalog.Mods.All.Where(m => m.Domain == 1 && m.Stats.Count == 1 && m.Stats[0].Id == stat
                && !m.Stats[0].Local && catalog.Mods.ByHash(m.Hash)?.Id == m.Id).OrderBy(m => m.Row).First()
            : catalog.Mods.ById(exactId)!;
        Assert.NotNull(row);
        Assert.Equal(1, row.Domain);
        var source = Assert.Single(row.Stats);
        Assert.Equal(stat, source.Id);
        Assert.False(source.Local);
        return new(ModSection.Explicit, row.Hash, [value]);
    }

    static Item PercentageRing(ItemCatalog catalog, params (string Stat, int Value)[] sources) =>
        ItemCatalog.Create(catalog.Find("Iron Ring")!) with
        {
            Mods = sources.Select(s => PercentageAttributeMod(catalog, s.Stat, s.Value)).ToArray(),
        };

    static Dictionary<uint, int> PercentageRefresh(GatewayCharacter game)
    {
        game.State.Pools = Program.PoolsFor(game.State, 100);
        return ProgramValue<List<(uint, int)>>("PlayerStatsWithCleared", game.State)
            .ToDictionary(s => s.Item1, s => s.Item2);
    }

    static void AssertPercentageAttributes(ClientState state, IReadOnlyDictionary<uint, int> published,
        int strength, int dexterity, int intelligence)
    {
        Assert.Equal((strength, dexterity, intelligence),
            (state.Pools.Strength, state.Pools.Dexterity, state.Pools.Intelligence));
        foreach (var (name, value) in new[]
        {
            ("strength", strength), ("dexterity", dexterity), ("intelligence", intelligence),
            ("virtual_strength_for_requirements", strength), ("virtual_dexterity_for_requirements", dexterity),
            ("virtual_intelligence_for_requirements", intelligence),
        })
            Assert.Equal(value, published[PercentageAttributeKey(name)]);
    }

    static Item PercentageGem(ItemCatalog catalog, string id, int level)
    {
        var type = catalog.Find("Metadata/Items/Gems/" + id)!;
        Assert.NotNull(type);
        var metadata = catalog.Gem(type)!;
        Assert.NotNull(metadata);
        return ItemCatalog.Create(type) with { GemExperience = metadata.ExperienceFor(level), GemFlags = 4 };
    }

    [Theory]
    [InlineData(49772, "strength_+%", 58, 14, 32)]
    [InlineData(63921, "dexterity_+%", 14, 58, 32)]
    [InlineData(55114, "intelligence_+%", 14, 14, 78)]
    public void Real_Utmost_flat_and_percentage_sources_scale_the_class_before_publication_and_derived_bonuses(
        ushort node, string percentageStat, int strength, int dexterity, int intelligence)
    {
        using var game = PercentageCharacter();
        var tree = Program.Passives;
        var source = tree.Stats([node]);
        Assert.Equal(40, source[PercentageAttributeKey("base_" + percentageStat[..^3])]);
        Assert.Equal(8, source[PercentageAttributeKey(percentageStat)]);
        // Isolate an actual node's calculation sources. Ordinary tree connectivity/admission has separate tests;
        // allocating its full travel path here would add unrelated attribute sources to the control.
        game.State.Passives.Add(node);
        var published = PercentageRefresh(game);
        AssertPercentageAttributes(game.State, published, strength, dexterity, intelligence);
        if (node == 49772)
        {
            Assert.Equal(1267u, game.State.Pools.Life); // 38 + 1200 + floor(58/2).
            Assert.Equal(11, game.State.Pools.MeleePhysicalDamagePct);
        }
        else if (node == 63921)
        {
            Assert.Equal(316, game.State.Pools.Accuracy); // 200 + 2*58.
            Assert.Equal(11, game.State.Pools.EvasionPct);
        }
        else
        {
            Assert.Equal(673u, game.State.Pools.Mana); // 34 + 600 + floor(78/2); (32+40)*1.08 rounds77.76 to78.
            Assert.Equal(7, game.State.Pools.EnergyShieldPct); // Seven completed groups of final 78 Intelligence.
        }
        game.State.Passives.Remove(node);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 32);
    }

    [Fact]
    public void Typed_and_all_attribute_item_increases_share_one_bucket_and_removal_restores_every_consumer()
    {
        using var game = PercentageCharacter();
        var (catalog, inventory) = PercentageEquipment(game);
        inventory.Add(6, 0, 0, PercentageRing(catalog, ("additional_all_attributes", 36), ("base_maximum_energy_shield", 100)));
        var percentages = PercentageRing(catalog, ("strength_+%", 20), ("dexterity_+%", 20),
            ("intelligence_+%", 20), ("all_attributes_+%", 10));
        var placed = inventory.Add(7, 0, 0, percentages);

        void Increased()
        {
            AssertPercentageAttributes(game.State, PercentageRefresh(game), 65, 65, 88);
            Assert.Equal(1270u, game.State.Pools.Life);
            Assert.Equal(678u, game.State.Pools.Mana);
            Assert.Equal(330, game.State.Pools.Accuracy);
            Assert.Equal(13, game.State.Pools.EvasionPct);
            Assert.Equal(13, game.State.Pools.MeleePhysicalDamagePct);
            Assert.Equal(8, game.State.Pools.EnergyShieldPct); // Final 88 Intelligence contributes 8%.
            Assert.Equal(108u, game.State.Pools.EnergyShield);
        }
        Increased();
        Increased(); // No feedback from published final attributes into a subsequent source calculation.
        Assert.NotNull(inventory.Remove(7, placed.Id));
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 50, 50, 68);
        Assert.Equal(1263u, game.State.Pools.Life);
        Assert.Equal(668u, game.State.Pools.Mana);
        Assert.Equal(300, game.State.Pools.Accuracy);
        Assert.Equal(106u, game.State.Pools.EnergyShield); //100 flat ES with six groups of final 68 Intelligence.
        inventory.Add(7, 0, 0, percentages);
        Increased();
    }

    [Fact]
    public void Three_native_minus_one_hundred_percent_sources_publish_legitimate_zero_attributes_without_class_fallback()
    {
        using var game = PercentageCharacter();
        var (catalog, inventory) = PercentageEquipment(game);
        var ring = ItemCatalog.Create(catalog.Find("Iron Ring")!) with
        {
            Mods = [PercentageAttributeMod(catalog, "strength_+%", -100, "PercentageStrengthUnique__1"),
                PercentageAttributeMod(catalog, "dexterity_+%", -100, "PercentageDexterityUnique__1"),
                PercentageAttributeMod(catalog, "intelligence_+%", -100, "PercentageIntelligenceUnique__1"),
                PercentageAttributeMod(catalog, "base_maximum_energy_shield", 100)],
        };
        var placed = inventory.Add(6, 0, 0, ring);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 0, 0, 0);
        Assert.DoesNotContain(placed.Id, Program.InactiveItems[game.Character]); // Iron Ring has no attribute requirement.
        Assert.Equal(1238u, game.State.Pools.Life);
        Assert.Equal(634u, game.State.Pools.Mana);
        Assert.Equal(200, game.State.Pools.Accuracy);
        Assert.Equal(0, game.State.Pools.EvasionPct);
        Assert.Equal(0, game.State.Pools.MeleePhysicalDamagePct);
        Assert.Equal(100u, game.State.Pools.EnergyShield);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 0, 0, 0);
        Assert.NotNull(inventory.Remove(6, placed.Id));
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 32);
    }

    [Fact]
    public void A_percentage_source_activates_real_equipment_and_removal_then_recovery_changes_its_actual_mods()
    {
        using var game = PercentageCharacter();
        var (catalog, inventory) = PercentageEquipment(game);
        inventory.Add(6, 0, 0, PercentageRing(catalog, ("additional_strength", 6)));
        var type = catalog.Find("Chestplate")!;
        Assert.Equal(25, catalog.Requirements(type).Str);
        var body = inventory.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(type) with
            { Mods = [PercentageAttributeMod(catalog, "base_maximum_life", 100)] });
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 20, 14, 32);
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);
        Assert.Equal(1248u, game.State.Pools.Life);

        var source = PercentageRing(catalog, ("strength_+%", 25));
        var ring = inventory.Add(7, 0, 0, source);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 25, 14, 32);
        Assert.DoesNotContain(body.Id, Program.InactiveItems[game.Character]);
        Assert.Equal(1350u, game.State.Pools.Life);
        Assert.NotNull(inventory.Remove(7, ring.Id));
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 20, 14, 32);
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);
        Assert.Equal(1248u, game.State.Pools.Life);
        inventory.Add(7, 0, 0, source);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 25, 14, 32);
        Assert.DoesNotContain(body.Id, Program.InactiveItems[game.Character]);
        Assert.Equal(1350u, game.State.Pools.Life);
    }

    [Fact]
    public void A_percentage_source_qualifies_a_real_socketed_gem_and_its_host_follows_the_gem_when_the_source_is_removed()
    {
        using var game = PercentageCharacter();
        var (catalog, inventory) = PercentageEquipment(game);
        var gem = PercentageGem(catalog, "SkillGemFireball", 9);
        int effect = catalog.Gem(catalog.ByHash(gem.TypeHash)!)!.Variant(0).Effect;
        var skills = Program.Skills;
        Assert.Equal(67, SkillCatalog.GemAttributeRequirement(100, skills.PlayerLevelReq(effect, 9), support: false));
        var body = inventory.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(catalog.Find("Plate Vest")!) with
            { Sockets = [new(SocketColour.White, gem)], Links = [1] });
        inventory.Add(6, 0, 0, PercentageRing(catalog, ("additional_intelligence", 30)));
        // The socketed gem raises the Plate Vest's requirement to its 67 Intelligence (19-gems.md): the host is inactive
        // with its gem until a percentage source carries the attribute over the threshold.
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 62);
        Assert.Contains("67 intelligence", ProgramValue<string?>("GemRequirementsUnmet", game.State, gem)!);
        Assert.NotNull(ProgramValue<string?>("GemDisabled", game.State, effect));
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);

        var source = PercentageRing(catalog, ("intelligence_+%", 10));
        var ring = inventory.Add(7, 0, 0, source);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 68);
        Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", game.State, gem));
        Assert.Null(ProgramValue<string?>("GemDisabled", game.State, effect));
        Assert.DoesNotContain(body.Id, Program.InactiveItems[game.Character]);
        Assert.NotNull(inventory.Remove(7, ring.Id));
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 62);
        Assert.NotNull(ProgramValue<string?>("GemDisabled", game.State, effect));
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);
        inventory.Add(7, 0, 0, source);
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 68);
        Assert.Null(ProgramValue<string?>("GemDisabled", game.State, effect));
        Assert.DoesNotContain(body.Id, Program.InactiveItems[game.Character]);
    }

    [Fact]
    public void A_percentage_source_on_an_inactive_weapon_only_applies_after_the_existing_weapon_set_swap()
    {
        using var game = PercentageCharacter();
        var (catalog, inventory) = PercentageEquipment(game);
        inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(catalog.Find("Rusted Sword")!));
        inventory.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(catalog.Find("Rusted Sword")!) with
        {
            Mods = [PercentageAttributeMod(catalog, "additional_strength", 100),
                PercentageAttributeMod(catalog, "strength_+%", 18, "PercentageStrengthUniqueBootsStrInt2")],
        });
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 32);
        inventory.SwapWeaponSets();
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 135, 14, 32); //114*1.18=134.52.
        Assert.Equal(1305u, game.State.Pools.Life);
        inventory.SwapWeaponSets();
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 14, 14, 32);
        Assert.Equal(1245u, game.State.Pools.Life);
    }

    [Fact]
    public async Task A_final_development_strength_override_changes_display_and_requirements_without_becoming_a_percentage_source()
    {
        using var game = PercentageCharacter();
        var (catalog, inventory) = PercentageEquipment(game);
        inventory.Add(6, 0, 0, PercentageRing(catalog, ("additional_all_attributes", 36)));
        inventory.Add(7, 0, 0, PercentageRing(catalog, ("strength_+%", 20), ("dexterity_+%", 20),
            ("intelligence_+%", 20), ("all_attributes_+%", 10)));
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 65, 65, 88);
        var gem = PercentageGem(catalog, "SkillGemAnger", 20);
        Assert.Contains("98 strength", ProgramValue<string?>("GemRequirementsUnmet", game.State, gem)!);

        await game.Command("/set_stat strength 100000");
        var published = PercentageRefresh(game);
        Assert.Equal(65, game.State.Pools.Strength);
        Assert.Equal(100000, published[PercentageAttributeKey("strength")]);
        Assert.Equal(100000, published[PercentageAttributeKey("virtual_strength_for_requirements")]);
        Assert.Equal(1270u, game.State.Pools.Life);
        Assert.Equal(13, game.State.Pools.MeleePhysicalDamagePct);
        Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", game.State, gem));

        await game.Command("/set_stat strength 0");
        published = PercentageRefresh(game);
        Assert.Equal(65, game.State.Pools.Strength);
        Assert.Equal(0, published[PercentageAttributeKey("strength")]);
        Assert.Equal(0, published[PercentageAttributeKey("virtual_strength_for_requirements")]);
        Assert.Equal(1270u, game.State.Pools.Life);
        Assert.NotNull(ProgramValue<string?>("GemRequirementsUnmet", game.State, gem));
        game.State.PlayerStats.Remove(PercentageAttributeKey("strength"));
        game.State.PlayerStats.Remove(PercentageAttributeKey("virtual_strength_for_requirements"));
        AssertPercentageAttributes(game.State, PercentageRefresh(game), 65, 65, 88);
    }
}
