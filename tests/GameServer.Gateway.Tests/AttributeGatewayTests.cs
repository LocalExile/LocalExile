using System.Reflection;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class AttributeGatewayTests : IDisposable
{
    readonly string name = "Attributes" + Guid.NewGuid().ToString("N");

    ClientState Character(int classRow = SkillCatalog.WitchRow)
    {
        TestSaves.Ensure();
        return new(new(ushort.MaxValue, "AttributeTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = classRow };
    }

    static Item Gear(string type, params (string Stat, int Value)[] stats) => ItemCatalog.Create(Program.Items.Find(type)!) with
    {
        Mods = stats.Select(s =>
        {
            var mod = Program.Items.Mods.All.Where(m => m.Stats.Any(a => a.Id == s.Stat && !a.Local))
                .OrderBy(m => m.Level).First();
            return new ItemMod(ModSection.Explicit, mod.Hash,
                mod.Stats.Select(a => a.Id == s.Stat ? s.Value : 0).ToArray());
        }).ToArray()
    };

    PlayerInventories Inventory => PlayerInventories.For(name, Program.Items);
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;
    static void Set(ClientState conn, string stat, int value) => conn.PlayerStats[Key(stat)] = value;
    static CharacterStats.Pools Rebuild(ClientState conn) => conn.Pools = Program.PoolsFor(conn, 100);
    static Dictionary<uint, int> Publish(ClientState conn) =>
        ((IEnumerable<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).ToDictionary(s => s.Item1, s => s.Item2);

    static async Task WithCommands(ClientState conn, Func<ChatCommands, Task> scenario)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var commands = (ChatCommands)typeof(Program).GetMethod("Commands", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["attribute-boundary-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn])!;
        await scenario(commands).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Complete_class_passive_and_equipment_bucket_drives_every_attribute_bonus_once()
    {
        var conn = Character();
        conn.Passives.Add(49772); // Utmost Might: +40 strength, 8% increased strength, from the extracted tree.
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", 20), ("additional_all_attributes", 30),
            ("strength_+%", 20), ("dexterity_+%", 25), ("intelligence_+%", 50), ("all_attributes_+%", 10)));
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Simple Robe"));
        var pools = Rebuild(conn);
        Assert.Equal((144, 59, 99), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(1310u, pools.Life);
        Assert.Equal(683u, pools.Mana);
        Assert.Equal(318, pools.Accuracy);
        Assert.Equal(11, pools.EvasionPct);
        Assert.Equal(16, pools.Evasion);
        Assert.Equal(19, pools.EnergyShieldPct);
        Assert.True(pools.EnergyShield > 0);
        Assert.Equal(28, pools.MeleePhysicalDamagePct);
        var shown = Program.CharacterPlayerStats(conn);
        foreach (var (stat, value) in new[] { ("strength", 144), ("dexterity", 59), ("intelligence", 99),
            ("virtual_strength_for_requirements", 144), ("virtual_dexterity_for_requirements", 59),
            ("virtual_intelligence_for_requirements", 99) })
            Assert.Equal(value, shown[Key(stat)]);
        Assert.Equal(pools, Rebuild(conn));
    }

    [Fact]
    public void Extracted_Behemoth_small_node_scales_gear_Strength_before_mana_groups_and_removes_cleanly()
    {
        var conn = Character();
        var node = Program.Passives.Find(4561)!;
        Assert.Equal("AscendancyCustomBerserker1", node.Id);
        Assert.Contains((Key("strength_+%"), 3), node.Stats);
        Assert.Contains((Key("physical_damage_reduction_rating_+%"), 10), node.Stats);
        // Pure source fixture only: this bypasses neither the real connection admission nor the Phrecia play gate.
        conn.MainAscendancy = new(Nodes: [4561, 38080]);
        Set(conn, PowerOfPurpose.RawFlag, 0); // Isolate mana per Strength from the node's independent conversion.
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", 3)));
        Assert.Equal(18, Rebuild(conn).Strength); // Complete 17 * 1.03 rounds up, rather than rounding each source.
        Assert.Equal(682u, conn.Pools.Mana); // Rounded 18 has four complete groups, not flat 17's separate sources.
        Assert.Equal(10, conn.Pools.ArmourPct);
        Assert.Equal(3, Publish(conn)[Key("strength_+%")]);
        conn.MainAscendancy = new();
        Assert.Equal(17, Rebuild(conn).Strength);
        Assert.Equal(650u, conn.Pools.Mana);
        Assert.Equal(0, conn.Pools.ArmourPct);
        Assert.Equal(0, Publish(conn)[Key("strength_+%")]);
    }

    [Fact]
    public async Task Increased_attributes_compose_with_Strength_mana_equipment_activation_and_current_weapon_snapshots()
    {
        var conn = Character();
        conn.MainAscendancy = new(Nodes: [38080]); // Raw mana-per-four-Strength source; no Phrecia admission.
        Set(conn, PowerOfPurpose.RawFlag, 0);
        var ring = Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", 1), ("strength_+%", 4)));
        var wand = Inventory.Add(PlayerInventories.Weapon1, 0, 0, Gear("Driftwood Wand", ("additional_strength", 9)));
        var chest = Inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            Gear("Chestplate", ("base_maximum_mana", 9), ("maximum_mana_+%", 25)));
        Assert.Equal(25, Program.Items.Requirements(Program.Items.Find("Chestplate")!).Str);
        Assert.Equal(14, Program.Items.Requirements(Program.Items.Find("Driftwood Wand")!).Int);

        Dictionary<uint, int> Check(int strength, int intelligence, uint mana, bool armed, bool chestActive, int flatStrength)
        {
            var pools = conn.Pools;
            Assert.Equal((strength, intelligence, mana), (pools.Strength, pools.Intelligence, pools.Mana));
            Assert.Equal(!armed, Program.InactiveItems[name].Contains(wand.Id));
            Assert.Equal(!chestActive, Program.InactiveItems[name].Contains(chest.Id));
            Assert.Equal(armed, Program.WeaponProfileFor(conn, activeSet: true).Wand);
            Assert.Equal(!armed, Program.WeaponProfileFor(conn, activeSet: true).Unarmed);
            var published = Publish(conn);
            Assert.Equal(strength, published[Key("virtual_strength_for_requirements")]);
            Assert.Equal((int)mana, published[Key("maximum_mana")]);
            Assert.Equal(flatStrength, published[Key("additional_strength")]);
            Assert.Equal(pools, Rebuild(conn)); // Neither publication nor another rebuild compounds either mechanic.
            return published;
        }

        Rebuild(conn);
        Check(25, 32, 883, true, true, 10); // round(24 * 1.04); trunc((650 + 9 + 6*8) * 1.25).
        await WithCommands(conn, async commands =>
        {
            await commands.RunAsync("/set_stat strength_+% 0");
            Check(24, 32, 698, true, false, 10); // Explicit zero replaces the item's increase, disabling its dependent.
            await commands.RunAsync("/set_stat strength_+% off");
            Check(25, 32, 883, true, true, 10);
            await commands.RunAsync("/set_stat intelligence_+% -60");
            Check(16, 13, 672, false, false, 1); // The inactive wand's +9 cannot reach pools or the outgoing snapshot.
            await commands.RunAsync("/set_stat strength 999");
            Assert.Equal(999, Program.CharacterPlayerStats(conn)[Key("strength")]);
            Assert.Equal(999, Program.CharacterPlayerStats(conn)[Key("virtual_strength_for_requirements")]);
            Assert.Equal(16, Rebuild(conn).Strength);
            Assert.Equal(672u, conn.Pools.Mana);
            Assert.Contains(chest.Id, Program.InactiveItems[name]);
            Assert.True(Program.WeaponProfileFor(conn, activeSet: true).Unarmed);
            await commands.RunAsync("/set_stat strength off");
            await commands.RunAsync("/set_stat intelligence_+% off");
            Check(25, 32, 883, true, true, 10);
            await commands.RunAsync("/set_stat X_mana_per_4_strength 0");
            Check(25, 32, 823, true, true, 10);
            await commands.RunAsync("/set_stat X_mana_per_4_strength off");
            Check(25, 32, 883, true, true, 10);
        });
        Inventory.SwapWeaponSets();
        Assert.Equal(16, Rebuild(conn).Strength);
        Assert.Equal(682u, conn.Pools.Mana);
        Assert.Contains(chest.Id, Program.InactiveItems[name]);
        Assert.True(Program.WeaponProfileFor(conn, activeSet: true).Unarmed);
        Assert.Equal(1, Publish(conn)[Key("additional_strength")]);
        Inventory.SwapWeaponSets();
        Rebuild(conn);
        Check(25, 32, 883, true, true, 10);
        Inventory.Remove(6, ring.Id);
        Rebuild(conn);
        Assert.Equal(0, Check(23, 32, 690, true, false, 9)[Key("strength_+%")]);
    }

    [Fact]
    public void Raw_overrides_replace_each_input_and_zero_or_removal_never_inherits_final_attributes()
    {
        var conn = Character();
        conn.Passives.Add(49772);
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", 10), ("strength_+%", 20)));
        Assert.Equal(82, Rebuild(conn).Strength); // 64 * 1.28.
        Set(conn, "strength_+%", 0);
        Assert.Equal(64, Rebuild(conn).Strength);
        Set(conn, "additional_strength", 0);
        Assert.Equal(54, Rebuild(conn).Strength);
        Set(conn, "base_strength", 1);
        Set(conn, "all_attributes_+%", 10);
        Assert.Equal(17, Rebuild(conn).Strength); // base_strength replaces Utmost Might's 40: (14 + 1) * 1.10.
        Set(conn, "strength", 999); // A final development display override is never a raw source.
        Assert.Equal(17, Rebuild(conn).Strength);
        conn.PlayerStats.Clear();
        Assert.Equal(82, Rebuild(conn).Strength);
        conn.Passives.Clear();
        Assert.Equal(29, Rebuild(conn).Strength); // Gear only: 24 * 1.20.
    }

    [Fact]
    public void Weapon_swap_and_source_removal_refresh_requirements_and_clear_sent_raw_stats()
    {
        var conn = Character();
        var weapon = Inventory.Add(PlayerInventories.Weapon1, 0, 0,
            Gear("Driftwood Wand", ("additional_strength", 10), ("strength_+%", 25)));
        var chest = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Plate Vest")); // Needs 12 strength.
        Set(conn, "base_strength", -4); // Ten without the active wand; twenty-five with it.
        Assert.Equal(25, Rebuild(conn).Strength);
        Assert.DoesNotContain(chest.Id, Program.InactiveItems[name]);
        Assert.Equal(25, Publish(conn)[Key("strength_+%")]);
        Inventory.SwapWeaponSets();
        Assert.Equal(10, Rebuild(conn).Strength);
        Assert.Contains(chest.Id, Program.InactiveItems[name]);
        Assert.Equal(0, Publish(conn)[Key("strength_+%")]);
        Inventory.SwapWeaponSets();
        Assert.Equal(25, Rebuild(conn).Strength);
        Assert.DoesNotContain(chest.Id, Program.InactiveItems[name]);
        Assert.Equal(25, Publish(conn)[Key("strength_+%")]);
        Inventory.Remove(PlayerInventories.Weapon1, weapon.Id);
        Assert.Equal(10, Rebuild(conn).Strength);
        Assert.Contains(chest.Id, Program.InactiveItems[name]);
        Assert.Equal(0, Publish(conn)[Key("strength_+%")]);
    }

    [Theory]
    [InlineData("additional_strength", 11, 25)]
    [InlineData("strength_+%", 100, 28)]
    public void Inactive_own_attributes_cannot_enable_an_item_but_active_own_attributes_can_retain_it(
        string stat, int amount, int retainedStrength)
    {
        var conn = Character();
        var chest = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Chestplate", (stat, amount)));
        // Chestplate needs 25 strength, so this item's own flat or percentage source is decisive.
        Program.InactiveItems[name] = [chest.Id];
        Assert.Equal(14, Rebuild(conn).Strength);
        Assert.Contains(chest.Id, Program.InactiveItems[name]);
        var ring = Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", 20)));
        Rebuild(conn);
        Assert.DoesNotContain(chest.Id, Program.InactiveItems[name]);
        Inventory.Remove(6, ring.Id);
        Assert.Equal(retainedStrength, Rebuild(conn).Strength);
        Assert.DoesNotContain(chest.Id, Program.InactiveItems[name]);
        Assert.Equal(retainedStrength, Rebuild(conn).Strength);
    }

    [Fact]
    public void Unmet_level_turns_off_an_items_percentage_and_cascades_to_dependents()
    {
        var conn = Character();
        var ring = Inventory.Add(6, 0, 0, Gear("Iron Ring", ("strength_+%", 100)));
        var chest = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Chestplate"));
        Assert.Equal(28, Rebuild(conn).Strength);
        conn.Pools = Program.PoolsFor(conn, 1);
        Assert.Contains(ring.Id, Program.InactiveItems[name]);
        Assert.Contains(chest.Id, Program.InactiveItems[name]);
        Assert.Equal(14, conn.Pools.Strength);
        Assert.Equal(28, Rebuild(conn).Strength);
        Assert.Empty(Program.InactiveItems[name]);
    }

    [Fact]
    public void Zero_final_attributes_are_published_as_zero_instead_of_falling_back_to_class_bases()
    {
        var conn = Character();
        Set(conn, "all_attributes_+%", -100);
        var pools = Rebuild(conn);
        Assert.Equal((0, 0, 0), (pools.Strength, pools.Dexterity, pools.Intelligence));
        conn.Pools = pools with { Life = pools.Life + 1 };
        var shown = Program.CharacterPlayerStats(conn);
        foreach (string stat in new[] { "strength", "dexterity", "intelligence", "virtual_strength_for_requirements",
            "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
            Assert.Equal(0, shown[Key(stat)]);
    }

    [Fact]
    public void Legacy_uninitialized_pools_keep_class_fallback_but_saturated_calculated_pools_do_not()
    {
        var conn = Character();
        conn.Pools = new(100, 100, 100, 0, 0);
        var uninitialized = Program.CharacterPlayerStats(conn);
        Assert.Equal(14, uninitialized[Key("strength")]);
        Assert.Equal(32, uninitialized[Key("virtual_intelligence_for_requirements")]);
        Set(conn, "additional_all_attributes", int.MaxValue);
        Rebuild(conn);
        var initialized = Program.CharacterPlayerStats(conn);
        foreach (string stat in new[] { "strength", "dexterity", "intelligence", "virtual_strength_for_requirements",
            "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
            Assert.Equal(int.MaxValue, initialized[Key(stat)]);
    }

    [Fact]
    public async Task Real_raw_stat_commands_rebuild_Marauder_pools_and_requirements_including_midpoints_and_zero()
    {
        var conn = Character(0);
        conn.Pools = Program.PoolsFor(conn, 1);
        Assert.Equal((32, 14, 14), (conn.Pools.Strength, conn.Pools.Dexterity, conn.Pools.Intelligence));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var commands = (ChatCommands)typeof(Program).GetMethod("Commands", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["attribute-command-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn])!;
        await commands.RunAsync("/set_stat strength_+% 3").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(33, conn.Pools.Strength);
        Assert.Equal(66u, conn.Pools.Life);
        Assert.Equal(6, conn.Pools.MeleePhysicalDamagePct);
        await commands.RunAsync("/set_stat strength_+% 0").WaitAsync(TimeSpan.FromSeconds(5));
        await commands.RunAsync("/set_stat all_attributes_+% 25").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((40, 18, 18), (conn.Pools.Strength, conn.Pools.Dexterity, conn.Pools.Intelligence));
        Assert.Equal(70u, conn.Pools.Life);
        Assert.Equal(49u, conn.Pools.Mana);
        Assert.Equal(38, conn.Pools.Accuracy);
        var midpoint = Program.CharacterPlayerStats(conn);
        Assert.Equal(18, midpoint[Key("virtual_dexterity_for_requirements")]);
        // A final display override is not a new flat input and does not change the derived pools.
        await commands.RunAsync("/set_stat strength 999").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(40, conn.Pools.Strength);
        Assert.Equal(70u, conn.Pools.Life);
        Assert.Equal(999, Program.CharacterPlayerStats(conn)[Key("strength")]);
        conn.PlayerStats.Remove(Key("strength"));
        conn.PlayerStats.Remove(Key("virtual_strength_for_requirements"));
        await commands.RunAsync("/set_stat all_attributes_+% -100").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((0, 0, 0), (conn.Pools.Strength, conn.Pools.Dexterity, conn.Pools.Intelligence));
        Assert.Equal(50u, conn.Pools.Life);
        Assert.Equal(40u, conn.Pools.Mana);
        Assert.Equal(2, conn.Pools.Accuracy);
        var zero = Program.CharacterPlayerStats(conn);
        Assert.Equal(0, zero[Key("strength")]);
        Assert.Equal(0, zero[Key("virtual_strength_for_requirements")]);
        Assert.Equal(0, zero[Key("virtual_dexterity_for_requirements")]);
        Assert.Equal(0, zero[Key("virtual_intelligence_for_requirements")]);
    }

    [Fact]
    public void Repeated_item_stat_sources_cancel_before_saturation_and_remain_signed_when_combined_with_passives()
    {
        var conn = Character();
        conn.Passives.Add(49772); // 40 base strength, 8% increased strength.
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", int.MaxValue),
            ("additional_strength", int.MaxValue), ("additional_strength", -int.MaxValue),
            ("additional_all_attributes", -int.MaxValue)));
        Assert.Equal(58, Rebuild(conn).Strength); // The large opposite flat sources cancel, leaving 54 * 1.08.
        Assert.Equal(0, conn.Pools.Dexterity);
        Assert.Equal(0, conn.Pools.Intelligence);
    }

    [Fact]
    public async Task Wide_item_flats_survive_cross_key_cancellation_reduction_and_complete_raw_key_replacement()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("additional_strength", int.MaxValue),
            ("additional_strength", int.MaxValue)));
        var chest = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Chestplate"));
        Rebuild(conn);
        await WithCommands(conn, async commands =>
        {
            await commands.RunAsync("/set_stat base_strength -2147483647");
            Assert.Equal(int.MaxValue, conn.Pools.Strength); // 14 + 2*int.MaxValue - int.MaxValue.
            Assert.DoesNotContain(chest.Id, Program.InactiveItems[name]);
            Assert.Equal(int.MaxValue, Program.CharacterPlayerStats(conn)[Key("virtual_strength_for_requirements")]);
            await commands.RunAsync("/set_stat strength_+% -50");
            Assert.Equal(1073741831, conn.Pools.Strength); // Half of the complete int.MaxValue + 14 flat bucket.
            await commands.RunAsync("/set_stat additional_strength 0");
            Assert.Equal(0, conn.Pools.Strength); // Replaces both huge item affixes, rather than their bounded display.
            Assert.Contains(chest.Id, Program.InactiveItems[name]);
            await commands.RunAsync("/set_stat base_strength 0");
            Assert.Equal(7, conn.Pools.Strength);
            await commands.RunAsync("/set_stat additional_strength 40");
            Assert.Equal(27, conn.Pools.Strength);
            Assert.DoesNotContain(chest.Id, Program.InactiveItems[name]);
            conn.PlayerStats.Remove(Key("additional_strength"));
            Assert.Equal(int.MaxValue, Rebuild(conn).Strength);
        });
    }

    [Fact]
    public async Task Wide_item_percentages_cancel_across_raw_keys_before_scaling_the_complete_flat_bucket()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("strength_+%", int.MaxValue), ("strength_+%", int.MaxValue)));
        Rebuild(conn);
        await WithCommands(conn, async commands =>
        {
            await commands.RunAsync("/set_stat all_attributes_+% -2147483647");
            Assert.Equal(300647725, conn.Pools.Strength); // 14 * (1 + int.MaxValue / 100), rounded once.
            await commands.RunAsync("/set_stat strength_+% 2147483647");
            Assert.Equal(14, conn.Pools.Strength); // Replacing the entire wide key cancels the all-attribute reduction.
            await commands.RunAsync("/set_stat strength_+% 0");
            Assert.Equal(0, conn.Pools.Strength);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saturated_dexterity_with_equipment_evasion_does_not_wedge_commands_or_Iron_Reflexes(bool ironReflexes)
    {
        var conn = Character();
        var body = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Gear("Destiny Leather"));
        Assert.True(Program.Items.Defences(Program.Items.Find("Destiny Leather")!)!.Value.EvasionMin >= 485);
        if (ironReflexes) conn.Passives.Add(10661);
        Rebuild(conn);
        await WithCommands(conn, async commands =>
        {
            await commands.RunAsync("/set_stat additional_dexterity 2147483647");
            Assert.Equal(int.MaxValue, conn.Pools.Dexterity);
            Assert.DoesNotContain(body.Id, Program.InactiveItems[name]);
            Assert.Equal(ironReflexes ? 0 : int.MaxValue, conn.Pools.Evasion);
            if (ironReflexes) Assert.True(conn.Pools.Armour >= 500);
            Assert.Equal(conn.Pools, Rebuild(conn));
            await commands.RunAsync("/set_stat additional_dexterity 0");
            Assert.Equal(14, conn.Pools.Dexterity);
            Assert.Contains(body.Id, Program.InactiveItems[name]);
            Assert.Equal(ironReflexes ? 0 : 15, conn.Pools.Evasion);
        });
    }

    [Fact]
    public async Task Increased_equipment_pools_publish_positive_maxima_and_leave_valid_aura_reservation()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Gear("Iron Ring", ("maximum_life_+%", 100), ("maximum_mana_+%", 100)));
        conn.Auras[0x103] = new(0x103, Program.Skills.EffectRow("Haste"), "Haste", "Haste", 1,
            AuraCatalog.BuffRow("Haste"), 0, 5000);
        Set(conn, "mana_regeneration_rate_+%", -100);
        Rebuild(conn);
        await WithCommands(conn, async commands =>
        {
            await commands.RunAsync("/set_stat additional_all_attributes 2147483647");
            Assert.Equal((uint)int.MaxValue, conn.Pools.Life);
            Assert.Equal((uint)int.MaxValue, conn.Pools.Mana);
            var published = Publish(conn);
            Assert.Equal(int.MaxValue, published[Key("base_maximum_life")]);
            Assert.Equal(int.MaxValue, published[Key("maximum_life")]);
            Assert.Equal(int.MaxValue, published[Key("base_maximum_mana")]);
            Assert.Equal(int.MaxValue, published[Key("maximum_mana")]);
            Assert.Equal(1073741824, AuraCatalog.ReservedMana(conn.Pools, conn.Auras.Values));
            Assert.Equal(1073741823, AuraCatalog.UnreservedMana(conn.Pools, conn.Auras.Values));
        });
    }

    public void Dispose()
    {
        PlayerInventories.Forget(name);
        Program.InactiveItems.TryRemove(name, out _);
    }
}
