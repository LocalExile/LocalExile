using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class PathOfBuildingItemsTests
{
    static PathOfBuildingBuild Build(string slot, string text, params PathOfBuildingSkillGroup[] skills) =>
        new(CharacterClass.Witch, 20, new(3, 0, "3_29", [], new Dictionary<ushort, ushort>(), [], false),
            [new(1, text, null)], [new(slot, 1)], skills, null);

    static PathOfBuildingGem Gem(string name = "Fireball", int level = 1, int quality = 0, bool enabled = true) =>
        new(name, null, null, null, level, quality, enabled);

    const string Wand = "Rarity: NORMAL\nPractice Wand\nSockets: R-G-B\nImplicits: 0";

    [Fact]
    public void Exact_mod_values_and_sections_round_trip_to_real_saved_items()
    {
        using var data = new Fixture();
        var build = Build("Ring 1", "Rarity: RARE\nDread Coil\nPractice Ring\nItem Level: 50\nImplicits: 0\n+17 to maximum Life\n+10% to Fire Resistance");
        var result = PathOfBuildingItems.Materialize(build, data.Items, data.Folder);
        var saved = Assert.Single(result.Inventories[6]).Item;
        Assert.Equal(ItemRarity.Rare, saved.Rarity);
        Assert.Equal(["Life", "FireResistance"], saved.Mods!.Select(m => m.Id));
        Assert.Equal([17, 10], saved.Mods!.SelectMany(m => m.Values));
        Assert.Equal([11u, 12u], saved.Name);
        var restored = CharacterSaves.FromSaved(saved, data.Items);
        Assert.Equal([17, 10], restored.Mods.SelectMany(m => m.Values));
        Assert.NotEmpty(restored.Payload());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Ranged_mod_lines_use_the_exported_roll_and_positive_half_rounding()
    {
        using var data = new Fixture();
        var build = Build("Ring 1", "Rarity: NORMAL\nPractice Ring\nItem Level: 50\nImplicits: 0\n{range:0.25}+(10-24) to maximum Life");
        var item = Assert.Single(PathOfBuildingItems.Materialize(build, data.Items, data.Folder).Inventories[6]).Item;
        Assert.Equal(14, Assert.Single(Assert.Single(item.Mods!).Values));
    }

    [Fact]
    public void Direct_affix_ids_preserve_tiers_and_do_not_duplicate_their_derived_text()
    {
        using var data = new Fixture();
        var build = Build("Ring 1", "Rarity: RARE\nDread Coil\nPractice Ring\nItem Level: 50\nCrafted: true\nPrefix: {fractured}{range:0.5}Life\nImplicits: 0\n{prefix}{fractured}+17 to maximum Life");
        var saved = Assert.Single(PathOfBuildingItems.Materialize(build, data.Items, data.Folder).Inventories[6]).Item;
        Assert.Equal("Life", Assert.Single(saved.Mods!).Id);
        Assert.Equal(17, Assert.Single(saved.Mods![0].Values));
        Assert.Equal(1, saved.Fractured);
    }

    [Fact]
    public void Inactive_variant_lines_do_not_become_modifiers()
    {
        using var data = new Fixture();
        var build = Build("Ring 1", "Rarity: NORMAL\nPractice Ring\nItem Level: 50\nVariant: Low\nVariant: High\nSelected Variant: 2\nImplicits: 0\n{variant:1}+11 to maximum Life\n{variant:2}+23 to maximum Life");
        var saved = Assert.Single(PathOfBuildingItems.Materialize(build, data.Items, data.Folder).Inventories[6]).Item;
        Assert.Equal(23, Assert.Single(Assert.Single(saved.Mods!).Values));
    }

    [Theory]
    [InlineData("+99999 to maximum Life", "no native modifier")]
    [InlineData("Gain a random superpower", "unsupported explicit modifier")]
    [InlineData("{mystery}17% increased Spell Damage", "unsupported modifier flags")]
    public void Unsupported_mods_fail_instead_of_creating_substitute_items(string mod, string reason)
    {
        using var data = new Fixture();
        var build = Build("Ring 1", "Rarity: NORMAL\nPractice Ring\nItem Level: 50\nImplicits: 0\n" + mod);
        var error = Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(build, data.Items, data.Folder));
        Assert.Contains(reason, error.Message);
        Assert.Contains("Ring 1", error.Message);
    }

    [Fact]
    public void Import_reports_all_invalid_equipment_slots_together()
    {
        using var data = new Fixture();
        var build = Build("Belt", Wand) with { Slots = [new("Belt", 1), new("Ring 2", 1)] };
        var error = Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(build, data.Items, data.Folder));
        Assert.Contains("Belt:", error.Message);
        Assert.Contains("Ring 2:", error.Message);
    }

    [Fact]
    public void Socket_order_colours_links_and_disabled_gem_offsets_are_preserved()
    {
        using var data = new Fixture();
        var group = new PathOfBuildingSkillGroup("Weapon 1", true, [Gem(enabled: false), Gem()], null);
        var result = PathOfBuildingItems.Materialize(Build("Weapon 1", Wand, group), data.Items, data.Folder);
        var saved = Assert.Single(result.Inventories[2]).Item;
        Assert.Equal([SocketColour.Red, SocketColour.Green, SocketColour.Blue], saved.Sockets!.Select(s => s.Colour));
        Assert.Null(saved.Sockets![0].Gem);
        Assert.NotNull(saved.Sockets[1].Gem); // Intentionally green, not reassigned to its matching blue socket.
        Assert.Null(saved.Sockets[2].Gem);
        Assert.Equal([3], saved.Links!.Select(n => (int)n));
        Assert.Equal("Fireball", result.SkillBar[1]);
        Assert.Contains(result.Warnings, w => w.Contains("Disabled gems"));
    }

    [Fact]
    public void Separate_groups_cannot_accidentally_share_a_native_support_link()
    {
        using var data = new Fixture();
        var group = new PathOfBuildingSkillGroup("Weapon 1", true, [Gem()], null);
        var error = Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(Build("Weapon 1", Wand, group, group), data.Items, data.Folder));
        Assert.Contains("share one native linked group", error.Message);
    }

    [Fact]
    public void Separate_link_groups_on_one_item_follow_the_exported_linear_offsets()
    {
        using var data = new Fixture();
        var group = new PathOfBuildingSkillGroup("Weapon 1", true, [Gem()], null);
        var result = PathOfBuildingItems.Materialize(Build("Weapon 1", Wand.Replace("R-G-B", "R G B"), group, group), data.Items, data.Folder);
        var sockets = Assert.Single(result.Inventories[2]).Item.Sockets!;
        Assert.NotNull(sockets[0].Gem);
        Assert.NotNull(sockets[1].Gem);
        Assert.Null(sockets[2].Gem);
    }

    [Fact]
    public void Gem_ids_and_variants_are_exact_and_corrupted_caps_are_preserved()
    {
        using var data = new Fixture();
        var gem = Gem("Fireball of Testing", 4, 23) with
        { GemId = "Metadata/Items/Gems/Fireball", VariantId = "FireballVariant", SkillId = "FireballAlt" };
        var item = PathOfBuildingItems.CreateGem(gem, data.Items, data.Folder);
        Assert.Equal((1, -1, (byte)23, true), (item.GemVariant, item.GemExperience, item.Quality, item.Corrupted));
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.CreateGem(gem with { VariantId = "Fireball" }, data.Items, data.Folder));
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.CreateGem(gem with { SkillId = "DifferentSkill" }, data.Items, data.Folder));
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.CreateGem(gem with { GemId = "Metadata/Items/Gems/Missing" }, data.Items, data.Folder));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(1, 24)]
    public void Invalid_gem_levels_and_quality_are_refused(int level, int quality)
    {
        using var data = new Fixture();
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.CreateGem(Gem(level: level, quality: quality), data.Items, data.Folder));
    }

    [Fact]
    public void Flasks_keep_their_numbered_bar_cells_and_native_charges()
    {
        using var data = new Fixture();
        var result = PathOfBuildingItems.Materialize(Build("Flask 5", "Rarity: NORMAL\nPractice Flask\nImplicits: 0"), data.Items, data.Folder);
        var flask = Assert.Single(result.Inventories[11]);
        Assert.Equal((byte)4, flask.X);
        Assert.Equal(21, flask.Item.Charges);
    }

    [Fact]
    public void Flask_charge_recovery_resolves_its_native_local_stat()
    {
        using var data = new Fixture();
        var result = PathOfBuildingItems.Materialize(Build("Flask 1",
            "Rarity: MAGIC\nPractice Flask\nItem Level: 50\nImplicits: 0\n30% increased Charge Recovery"), data.Items, data.Folder);
        var mod = Assert.Single(Assert.Single(result.Inventories[11]).Item.Mods!);
        Assert.Equal("ChargeRecovery", mod.Id);
        Assert.Equal(30, Assert.Single(mod.Values));
    }

    [Fact]
    public void Vaal_gems_are_corrupted_at_ordinary_levels_and_quality()
    {
        using var data = new Fixture();
        var gem = PathOfBuildingItems.CreateGem(Gem("Vaal Fireball", 1, 0), data.Items, data.Folder);
        Assert.True(gem.Corrupted);
        Assert.Equal(1, data.Items.GemLevel(gem));
    }

    [Fact]
    public void Exported_requirement_overrides_cannot_silently_change_native_requirements()
    {
        using var data = new Fixture();
        var build = Build("Weapon 1", Wand + "\nLevelReq: 1");
        Assert.Single(PathOfBuildingItems.Materialize(build, data.Items, data.Folder).Inventories[2]);
        var invalid = build with { Items = [new(1, Wand + "\nLevelReq: 20", null)] };
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(invalid, data.Items, data.Folder));
    }

    [Fact]
    public void Second_weapon_set_is_materialized_into_the_active_native_hand_rows()
    {
        using var data = new Fixture();
        var build = Build("Weapon 1 Swap", Wand) with { UseSecondWeaponSet = true };
        var result = PathOfBuildingItems.Materialize(build, data.Items, data.Folder);
        Assert.Single(result.Inventories[2]);
        Assert.False(result.Inventories.ContainsKey(14));
    }

    [Fact]
    public void Armour_final_values_select_a_native_base_roll_and_impossible_values_fail()
    {
        using var data = new Fixture();
        var build = Build("Body Armour", "Rarity: NORMAL\nPractice Vest\nArmour: 23\nImplicits: 0");
        var item = Assert.Single(PathOfBuildingItems.Materialize(build, data.Items, data.Folder).Inventories[1]).Item;
        Assert.Equal(23, data.Items.Defences(data.Items.ByHash(item.Type)!)!.Value.AtRoll(item.DefenceBaseRoll).Armour);
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(build with
        { Items = [new(1, build.Items[0].Text.Replace("Armour: 23", "Armour: 999"), null)] }, data.Items, data.Folder));
    }

    [Fact]
    public void Ambiguous_bases_do_not_use_the_first_fuzzy_match()
    {
        using var data = new Fixture(duplicateRing: true);
        var error = Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(Build("Ring 1", "Rarity: NORMAL\nPractice Ring\nImplicits: 0"), data.Items, data.Folder));
        Assert.Contains("ambiguous", error.Message);
    }

    [Fact]
    public void Royale_duplicates_do_not_make_ordinary_bases_ambiguous()
    {
        using var data = new Fixture(royaleRing: true);
        var result = PathOfBuildingItems.Materialize(Build("Ring 1", "Rarity: NORMAL\nPractice Ring\nImplicits: 0"), data.Items, data.Folder);
        Assert.Equal(1u, Assert.Single(result.Inventories[6]).Item.Type);
    }

    [Theory]
    [InlineData("W--W")]
    [InlineData("-W")]
    [InlineData("W-")]
    public void Malformed_socket_links_are_refused(string layout)
    {
        using var data = new Fixture();
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(Build("Weapon 1", Wand.Replace("R-G-B", layout)), data.Items, data.Folder));
    }

    [Fact]
    public void Undefined_rarity_and_unsupported_percentiles_are_refused()
    {
        using var data = new Fixture();
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(Build("Weapon 1", Wand.Replace("NORMAL", "99")), data.Items, data.Folder));
        Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(Build("Body Armour",
            "Rarity: NORMAL\nPractice Vest\nWardBasePercentile: 0.5\nImplicits: 0"), data.Items, data.Folder));
    }

    [Fact]
    public void An_explicit_mod_does_not_hide_missing_intrinsic_implicit_values()
    {
        using var data = new Fixture(implicitRing: true);
        var error = Assert.Throws<ArgumentException>(() => PathOfBuildingItems.Materialize(Build("Ring 1",
            "Rarity: NORMAL\nPractice Ring\nItem Level: 50\n+17 to maximum Life"), data.Items, data.Folder));
        Assert.Contains("omits its implicit", error.Message);
    }

    [Fact]
    public void Inactive_direct_affix_variants_do_not_become_modifiers()
    {
        using var data = new Fixture();
        var result = PathOfBuildingItems.Materialize(Build("Ring 1", "Rarity: NORMAL\nPractice Ring\nSelected Variant: 1\nPrefix: {variant:2}{range:0.5}Life\nImplicits: 0"), data.Items, data.Folder);
        Assert.Empty(Assert.Single(result.Inventories[6]).Item.Mods!);
    }

    sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "pob-items-" + Guid.NewGuid().ToString("N"));
        public ItemCatalog Items { get; }
        void Write(string file, object data) => File.WriteAllText(Path.Combine(Folder, file), JsonSerializer.Serialize(data));

        public Fixture(bool duplicateRing = false, bool royaleRing = false, bool implicitRing = false)
        {
            Directory.CreateDirectory(Path.Combine(Folder, "tables"));
            var names = new List<(string Id, string Name, string Class, string[] Components, int Width, int Height)>
            {
                ("Metadata/Items/Rings/Practice", "Practice Ring", "Ring", ["Base", "Mods"], 1, 1),
                ("Metadata/Items/Weapons/Practice", "Practice Wand", "Wand", ["Base", "Mods", "Weapon", "Sockets"], 1, 3),
                ("Metadata/Items/Gems/Fireball", "Fireball", "Active Skill Gem", ["Base", "Quality", "SkillGem"], 1, 1),
                ("Metadata/Items/Flasks/Practice", "Practice Flask", "LifeFlask", ["Base", "Mods", "Charges", "Flask"], 1, 2),
                ("Metadata/Items/Armours/Practice", "Practice Vest", "Body Armour", ["Base", "Mods", "Armour", "Quality", "Sockets"], 2, 3),
                ("Metadata/Items/Gems/VaalFireball", "Vaal Fireball", "Active Skill Gem", ["Base", "Quality", "SkillGem"], 1, 1),
            };
            if (duplicateRing) names.Add(("Metadata/Items/Rings/Practice2", "Practice Ring", "Ring", ["Base", "Mods"], 1, 1));
            if (royaleRing) names.Add(("Metadata/Items/Rings/PracticeRoyale", "Practice Ring", "Ring", ["Base", "Mods"], 1, 1));
            Write("tables/ItemClasses.json", names.Select(t => new { Id = t.Class, ItemStance = 0 }));
            Write("tables/ItemVisualIdentity.json", new[] { new { HASH16 = 1 } });
            Write("tables/BaseItemTypes.json", names.Select((t, i) => new Dictionary<string, object?>
            {
                ["Id"] = t.Id, ["Name"] = t.Name, ["HASH32"] = (uint)(i + 1), ["Width"] = t.Width, ["Height"] = t.Height,
                ["ItemVisualIdentity"] = 0, ["ItemClassesKey"] = i, ["ItemStance"] = 0, ["DropLevel"] = 1,
                ["Implicit_ModsKeys"] = implicitRing && i == 0 ? new[] { 0 } : Array.Empty<int>(), ["TagsKeys"] = new[] { 0 }, ["ModDomain"] = 1,
            }));
            Write("item-types.json", new { types = names.Select(t => new { type = t.Id, components = t.Components, read = t.Components.Length }) });
            Write("tables/Stats.json", new[] { "base_maximum_life", "base_fire_damage_resistance_%", "local_charges_added_+%" }
                .Select(id => new { Id = id, IsLocal = id.StartsWith("local_", StringComparison.Ordinal) }));
            Write("tables/Tags.json", new[] { new { Id = "default" } });
            object Mod(string id, int hash, int stat, int min, int max, int generation)
            {
                var r = new Dictionary<string, object?>
                {
                    ["Id"] = id, ["Name"] = id, ["HASH16"] = hash, ["Domain"] = 1, ["GenerationType"] = generation,
                    ["Level"] = 1, ["Families"] = new[] { hash }, ["SpawnWeight_TagsKeys"] = new[] { 0 },
                    ["SpawnWeight_Values"] = new[] { 1000 }, ["GenerationWeight_TagsKeys"] = Array.Empty<int>(),
                    ["GenerationWeight_Values"] = Array.Empty<int>(), ["IsEssenceOnlyModifier"] = false,
                };
                for (int i = 1; i <= 8; i++)
                { r[$"StatsKey{i}"] = i == 1 ? stat : null; r[$"Stat{i}Min"] = min; r[$"Stat{i}Max"] = max; }
                return r;
            }
            Write("tables/Mods.json", new[] { Mod("Life", 10, 0, 10, 24, 1), Mod("FireResistance", 11, 1, 6, 11, 2),
                Mod("ChargeRecovery", 12, 2, 20, 40, 1) });
            Write("tables/Words.json", new[] { new { Text = "Dread", HASH32 = 11u, Wordlist = 1 }, new { Text = " Coil", HASH32 = 12u, Wordlist = 2 } });
            Write("tables/ArmourTypes.json", new[] { new { BaseItemTypesKey = 4, ArmourMin = 19, ArmourMax = 27,
                EvasionMin = 0, EvasionMax = 0, EnergyShieldMin = 0, EnergyShieldMax = 0 } });
            Write("tables/ComponentCharges.json", new[] { new { BaseItemTypesKey = names[3].Id, MaxCharges = 21 } });
            Write("tables/ItemExperienceTypes.json", new[] { new { Id = "GemProgression1" } });
            Write("tables/ItemExperiencePerLevel.json", Enumerable.Range(0, 3).Select(i => new { ItemExperienceType = 0, ItemCurrentLevel = i + 1, Experience = i * 100 }));
            Write("tables/SkillGems.json", new[] { new { BaseItemTypesKey = 2, IsSupport = false, IsVaalVariant = false,
                RegularVariant = (int?)null, ItemExperienceType = 0, GemVariants = new[] { 0, 1 }, GemColour = 3,
                StrengthRequirementPercent = 0, DexterityRequirementPercent = 0, IntelligenceRequirementPercent = 100 },
                new { BaseItemTypesKey = 5, IsSupport = false, IsVaalVariant = true,
                RegularVariant = (int?)null, ItemExperienceType = 0, GemVariants = new[] { 2 }, GemColour = 3,
                StrengthRequirementPercent = 0, DexterityRequirementPercent = 0, IntelligenceRequirementPercent = 100 } });
            Write("tables/GemEffects.json", new[]
            {
                new { Id = "Fireball", Name = "Fireball", GrantedEffect = 0, GrantedEffect2 = (int?)null },
                new { Id = "FireballVariant", Name = "Fireball of Testing", GrantedEffect = 1, GrantedEffect2 = (int?)null },
                new { Id = "VaalFireball", Name = "Vaal Fireball", GrantedEffect = 2, GrantedEffect2 = (int?)0 },
            });
            Write("tables/GrantedEffects.json", new[] { new { Id = "Fireball" }, new { Id = "FireballAlt" }, new { Id = "VaalFireball" } });
            Items = new(Folder);
        }

        public void Dispose() => Directory.Delete(Folder, true);
    }
}
