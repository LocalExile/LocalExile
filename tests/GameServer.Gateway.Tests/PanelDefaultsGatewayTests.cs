using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    const string PanelDefaultsBrandLimit = "base_total_number_of_sigils_allowed";
    const ushort PanelDefaultsBrandEquity = 64355;
    // Exact Character.ot defaults, independently fixed by the native-input evidence in the G16 plan.
    static readonly (string Stat, int Value)[] PanelDefaultsNative =
    [
        (PanelDefaultsBrandLimit, 3),
        ("chance_to_deal_triple_damage_%_per_brutal_charge", 3),
        ("stun_threshold_+%_per_brutal_charge", 10),
        ("ailment_damage_+%_final_per_affliction_charge", 8),
        ("non_damaging_ailment_effect_+%_final_per_affliction_charge", 8),
        ("elemental_damage_taken_goes_to_energy_shield_over_4_seconds_%_per_absorption_charge", 12),
    ];
    static readonly (string Standard, string Special, string Mod, string Flag, int Maximum)[] PanelDefaultsMirrors =
    [
        ("endurance", "brutal", "MaximumBrutalChargesEqualsEnduranceUnique__1__",
            "maximum_brutal_charges_is_equal_to_maximum_endurance_charges", 4),
        ("frenzy", "affliction", "MaximumAfflictionChargesEqualsFrenzyUnique__1",
            "maximum_affliction_charges_is_equal_to_maximum_frenzy_charges", 5),
        ("power", "absorption", "MaximumAbsorptionChargesEqualsPowerUnique__1_",
            "maximum_absorption_charges_is_equal_to_maximum_power_charges", 6),
    ];
    static uint PanelDefaultsKey(string name) => Program.Stats.Key(name)!.Value;

    static void PanelDefaultsAddBrandEquity(GatewayCharacter game)
    {
        var tree = Program.Passives;
        Assert.Equal("Brand Equity", tree.Find(PanelDefaultsBrandEquity)!.Name);
        Assert.Equal(2, tree.Stats([PanelDefaultsBrandEquity])[PanelDefaultsKey(PanelDefaultsBrandLimit)]);
        // Isolate the real node's contribution; this is not a disconnected-tree admission or acquisition claim.
        game.State.Passives.Add(PanelDefaultsBrandEquity);
    }

    static void PanelDefaultsAssertNative(IReadOnlyDictionary<uint, int> values, int brand = 3)
    {
        foreach (var (stat, expected) in PanelDefaultsNative)
            Assert.Equal(stat == PanelDefaultsBrandLimit ? brand : expected, values[PanelDefaultsKey(stat)]);
    }

    [Fact]
    public void Panel_defaults_native_Brand_item_and_Brand_Equity_add_once_and_final_replacements_recover()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        PanelDefaultsAssertNative(LevelPanelPublished(game));
        Assert.Equal(0, Program.CharacterPlayerStats(game.State).GetValueOrDefault(PanelDefaultsKey("skill_is_brand_skill")));
        foreach (var mirror in PanelDefaultsMirrors)
            Assert.Equal(0, LevelPanelPublished(game)[PanelDefaultsKey($"virtual_maximum_{mirror.Special}_charges")]);

        // Native single-stat modifiers on isolated carriers do not assert that these are legal unique combinations.
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "AdditionalBrandUnique__1", PanelDefaultsBrandLimit, 1)));
        PanelDefaultsAssertNative(LevelPanelPublished(game), 4);
        PanelDefaultsAddBrandEquity(game);
        PanelDefaultsAssertNative(LevelPanelPublished(game), 6);
        PanelDefaultsAssertNative(LevelPanelPublished(game), 6);

        foreach (var (stat, _) in PanelDefaultsNative)
        {
            uint key = PanelDefaultsKey(stat);
            Assert.DoesNotContain(key, game.State.PlayerStats.Keys);
            foreach (int replacement in new[] { 777, 0 })
            {
                game.State.PlayerStats[key] = replacement;
                Assert.Equal(replacement, LevelPanelPublished(game)[key]);
                Assert.Equal(replacement, LevelPanelPublished(game)[key]);
            }
            Assert.True(game.State.PlayerStats.Remove(key));
            PanelDefaultsAssertNative(LevelPanelPublished(game), 6);
        }

        Assert.NotNull(inventory.Remove(6, ring.Id));
        PanelDefaultsAssertNative(LevelPanelPublished(game), 5);
        Assert.True(game.State.Passives.Remove(PanelDefaultsBrandEquity));
        PanelDefaultsAssertNative(LevelPanelPublished(game));
        PanelDefaultsAssertNative(LevelPanelPublished(game));
    }

    [Theory]
    [InlineData("StormBrand")]
    [InlineData("ArmageddonBrand")]
    public void Panel_defaults_real_linked_Foulgrasp_adds_two_to_selected_Brand_and_removes_without_stale_values(
        string effect)
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        var skills = Program.Skills;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        inventory.Add(5, 0, 0, LevelPanelCarrier(catalog, "Gold Amulet",
            PercentageAttributeMod(catalog, "additional_all_attributes", 200)));
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "AdditionalBrandUnique__1", PanelDefaultsBrandLimit, 1)));
        PanelDefaultsAddBrandEquity(game);
        Item Gem(string name)
        {
            var type = catalog.Find("Metadata/Items/Gems/" + name)!;
            Assert.NotNull(type);
            return ItemCatalog.Create(type) with { GemExperience = catalog.Gem(type)!.ExperienceFor(1), Quality = 0 };
        }
        var brand = Gem("SkillGem" + effect);
        var support = Gem("SupportGemFoulgrasp");
        var fireball = Gem("SkillGemFireball");
        var body = inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            ItemCatalog.Create(catalog.Find("Plate Vest")!) with
            {
                Sockets = [new(SocketColour.White, brand), new(SocketColour.White, support), new(SocketColour.White, fireball)],
                Links = [3],
            });
        game.State.Pools = Program.PoolsFor(game.State, 100);
        foreach (var gem in new[] { brand, support, fireball })
            Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", game.State, gem));

        void Check(int actorLimit, int selectedLimit, bool supported)
        {
            PanelDefaultsAssertNative(LevelPanelPublished(game), actorLimit);
            var gems = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)typeof(Program)
                .GetMethod("GemSkillEffects", BindingFlags.Static | BindingFlags.NonPublic, binder: null,
                    types: [typeof(ClientState)], modifiers: null)!.Invoke(null, [game.State])!).ToArray();
            var all = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State);
            var selected = Assert.Single(all, s => s.Id == Assert.Single(gems, g => g.Effect == skills.EffectRow(effect)).Id);
            var other = Assert.Single(all, s => s.Id == Assert.Single(gems, g => g.Effect == skills.EffectRow("Fireball")).Id);
            var selectedStats = selected.Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(selectedLimit, selectedStats[PanelDefaultsKey(PanelDefaultsBrandLimit)]);
            Assert.Equal(1, selectedStats[PanelDefaultsKey("skill_is_brand_skill")]);
            Assert.Equal(supported ? 1 : 0, selected.Supports.Count);
            Assert.Empty(other.Supports);
            Assert.Equal(0, other.Stats!.Single(s => s.Key == PanelDefaultsKey("skill_is_brand_skill")).Value);
            var attack = Assert.Single(all, s => s.Id == SkillCatalog.FirstSkillId);
            Assert.Equal(0, attack.Stats!.ToDictionary(s => s.Key, s => s.Value)
                .GetValueOrDefault(PanelDefaultsKey("skill_is_brand_skill")));
        }

        Check(6, 8, true);
        Check(6, 8, true);
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 1, 1] }));
        Check(6, 6, false);
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [3] }));
        Check(6, 8, true);
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        {
            Sockets = [new(SocketColour.White, brand), new(SocketColour.White, fireball)], Links = [2],
        }));
        Check(6, 6, false);
        Assert.NotNull(inventory.Remove(6, ring.Id));
        Check(5, 5, false);
        Assert.True(game.State.Passives.Remove(PanelDefaultsBrandEquity));
        Check(3, 3, false);
    }

    [Fact]
    public void Panel_defaults_real_mirror_modifiers_read_completed_unequal_maxima_and_clear_removed_sources()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        // Actual native values, isolated from unique acquisition: base3 plus E1, F1+1 and P2+1 gives4/5/6.
        var maxima = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            LevelPanelMod(catalog, "MaximumEnduranceChargeUniqueRing2", "max_endurance_charges", 1),
            LevelPanelMod(catalog, "MaximumFrenzyChargesUnique__1", "max_frenzy_charges", 1),
            LevelPanelMod(catalog, "MaxFrenzyChargesCorrupted", "max_frenzy_charges", 1),
            LevelPanelMod(catalog, "IncreasedMaximumPowerChargesUnique__3", "max_power_charges", 2),
            LevelPanelMod(catalog, "IncreasedMaximumPowerChargesUniqueWand3", "max_power_charges", 1)));
        var mods = PanelDefaultsMirrors.Select(m => LevelPanelMod(catalog, m.Mod, m.Flag, 1)).ToArray();
        var mirrors = inventory.Add(5, 0, 0, LevelPanelCarrier(catalog, "Gold Amulet", mods));

        void Check(int endurance, int frenzy, int power, int brutal, int affliction, int absorption)
        {
            var values = LevelPanelPublished(game);
            foreach (var (name, expected) in new[]
            {
                ("endurance", endurance), ("frenzy", frenzy), ("power", power),
                ("brutal", brutal), ("affliction", affliction), ("absorption", absorption),
            }) Assert.Equal(expected, values[PanelDefaultsKey($"virtual_maximum_{name}_charges")]);
            PanelDefaultsAssertNative(values); // The five special coefficients stay per charge, regardless of maxima.
        }
        Check(4, 5, 6, 4, 5, 6);
        Check(4, 5, 6, 4, 5, 6);
        foreach (var mirror in PanelDefaultsMirrors)
        {
            uint standard = PanelDefaultsKey($"virtual_maximum_{mirror.Standard}_charges");
            uint special = PanelDefaultsKey($"virtual_maximum_{mirror.Special}_charges");
            game.State.PlayerStats[standard] = 0;
            var zeroStandard = LevelPanelPublished(game);
            Assert.Equal(0, zeroStandard[standard]);
            Assert.Equal(0, zeroStandard[special]);
            foreach (var other in PanelDefaultsMirrors.Where(m => m.Standard != mirror.Standard))
                Assert.Equal(other.Maximum, zeroStandard[PanelDefaultsKey($"virtual_maximum_{other.Special}_charges")]);
            Assert.True(game.State.PlayerStats.Remove(standard));
            Check(4, 5, 6, 4, 5, 6);
            foreach (int replacement in new[] { 777, 0 })
            {
                game.State.PlayerStats[special] = replacement;
                var replaced = LevelPanelPublished(game);
                Assert.Equal(replacement, replaced[special]);
                Assert.Equal(mirror.Maximum, replaced[standard]);
            }
            Assert.True(game.State.PlayerStats.Remove(special));
            Check(4, 5, 6, 4, 5, 6);

            Assert.NotNull(inventory.Change(5, mirrors.Id, item => item with
                { Mods = mods.Where(m => m.Hash != catalog.Mods.ById(mirror.Mod)!.Hash).ToArray() }));
            var cleared = LevelPanelPublished(game);
            Assert.Equal(0, cleared[special]);
            Assert.Equal(0, cleared[PanelDefaultsKey(mirror.Flag)]);
            Assert.DoesNotContain(PanelDefaultsKey(mirror.Flag), game.State.SentStatKeys);
            Assert.DoesNotContain(PanelDefaultsKey(mirror.Flag), LevelPanelPublished(game).Keys);
            foreach (var other in PanelDefaultsMirrors.Where(m => m.Standard != mirror.Standard))
                Assert.Equal(other.Maximum, cleared[PanelDefaultsKey($"virtual_maximum_{other.Special}_charges")]);
            Assert.NotNull(inventory.Change(5, mirrors.Id, item => item with { Mods = mods }));
            Check(4, 5, 6, 4, 5, 6);
        }
        Assert.NotNull(inventory.Remove(6, maxima.Id));
        Check(3, 3, 3, 3, 3, 3);
        Assert.NotNull(inventory.Remove(5, mirrors.Id));
        Check(3, 3, 3, 0, 0, 0);
        Check(3, 3, 3, 0, 0, 0);
    }
}
