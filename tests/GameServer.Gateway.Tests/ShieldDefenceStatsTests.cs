using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ShieldDefenceStatsTests : IDisposable
{
    const string ShieldDefences = "shield_armour_+%"; // Native name: this increases all shield defences.
    const string FullConversion = "gain_maximum_life_instead_of_maximum_es_from_armour";
    const string PercentConversion = "gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value";
    readonly string _name = "ShieldDefence" + Guid.NewGuid().ToString("N");

    public ShieldDefenceStatsTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "ShieldDefences", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Ring(params (string Stat, int Value)[] mods) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    // Give the native fixture item exact local totals, independently of its ordinary base rating.
    static Item Armour(string name, int armour, int evasion, int es, byte quality = 0,
        params (string Stat, int Value)[] extra)
    {
        var type = Program.Items.Find(name)!;
        var basis = Program.Items.Defences(type)!.Value.AtRoll(0);
        return ItemCatalog.Create(type, defenceBaseRoll: 0) with
        {
            Quality = quality,
            Mods = new[]
            {
                Mod("local_base_physical_damage_reduction_rating", armour - basis.Armour),
                Mod("local_base_evasion_rating", evasion - basis.Evasion),
                Mod("local_energy_shield", es - basis.EnergyShield),
            }.Concat(extra.Select(m => Mod(m.Stat, m.Value))).ToArray(),
        };
    }

    static Item Shield(int armour = 100, int evasion = 100, int es = 100) =>
        Armour("Splintered Tower Shield", armour, evasion, es);

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    [Fact]
    public void Native_shield_armour_stat_increases_all_three_shield_defences_only()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield());
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 50)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(250, pools.Armour); // Body 100 + shield 100 * 1.50.
        Assert.Equal(254, pools.Evasion); // (15 base + 85 body) * 1.02 + shield 100 * 1.52.
        Assert.Equal(256u, pools.EnergyShield); // Body 100 * 1.03 + shield 100 * 1.53.
    }

    [Fact]
    public void Shield_and_global_increases_add_instead_of_multiplying()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield());
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 50), ("global_defences_+%", 50)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(350, pools.Armour);
        Assert.Equal(354, pools.Evasion);
        Assert.Equal(356u, pools.EnergyShield);
    }

    [Fact]
    public void The_real_Shield_Defences_passive_applies_to_all_three_ratings()
    {
        var conn = Character();
        conn.Passives.Add(2355); // Shield Defences: native shield_armour_+% = 50.
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield());

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(250, pools.Armour);
        Assert.Equal(254, pools.Evasion);
        Assert.Equal(256u, pools.EnergyShield);
    }

    [Fact]
    public void The_real_shield_ES_passive_affects_only_shield_ES()
    {
        var conn = Character();
        conn.Passives.Add(12246); // Energy Shield: native shield_maximum_energy_shield_+% = 30.
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield());

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(200, pools.Armour);
        Assert.Equal(204, pools.Evasion);
        Assert.Equal(236u, pools.EnergyShield); // Body 103 + shield 133.
    }

    [Fact]
    public void Typed_and_combined_shield_increases_apply_only_to_their_own_rating()
    {
        // These native typed AR/EV inputs have no current passive or item-mod source.
        var bonus = new CharacterStats.Bonus(Armour: 200, Evasion: 185, EnergyShield: 200,
            ArmourPct: 50, EvasionPct: 50, EnergyShieldPct: 50,
            ShieldArmour: 100, ShieldEvasion: 100, ShieldEnergyShield: 100,
            ShieldArmourPct: 20, ShieldEvasionPct: 30, ShieldEnergyShieldPct: 40,
            SharedShieldDefencesPct: 50);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(370, pools.Armour);
        Assert.Equal(384, pools.Evasion);
        Assert.Equal(396u, pools.EnergyShield);
    }

    [Fact]
    public void All_three_final_defences_round_halfway_up()
    {
        var bonus = new CharacterStats.Bonus(Armour: 1, Evasion: 1, EnergyShield: 1,
            EvasionPct: 48, ShieldArmour: 1, ShieldEvasion: 1, ShieldEnergyShield: 1,
            ShieldArmourPct: 50, ShieldEvasionPct: 50, ShieldEnergyShieldPct: 47);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(2, pools.Armour); // Shield 1 * 1.5.
        Assert.Equal(25, pools.Evasion); // Character 15 * 1.5 + shield 1 * 2 = 24.5.
        Assert.Equal(2u, pools.EnergyShield); // Shield 1 * (1 + .03 Intelligence + .47 synthetic shield INC) = 1.5.
    }

    [Fact]
    public void Shield_and_other_source_fractions_survive_until_the_final_total_round()
    {
        var bonus = new CharacterStats.Bonus(Armour: 2, Evasion: 5, EnergyShield: 2,
            ArmourPct: 40, EvasionPct: 2, EnergyShieldPct: 34,
            ShieldArmour: 1, ShieldEvasion: 10, ShieldEnergyShield: 1);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(3, pools.Armour); // 1.4 + 1.4 = 2.8; truncating individual sources would give 2.
        Assert.Equal(21, pools.Evasion); // 10.4 + 10.4 = 20.8; separate source truncation would give 20.
        Assert.Equal(3u, pools.EnergyShield); // Two 1.37 contributions sum to 2.74 before rounding.
    }

    [Fact]
    public void Local_quality_and_mods_finish_before_shield_and_global_increases()
    {
        var conn = Character();
        var shield = Armour("Splintered Tower Shield", 100, 100, 100, 20,
            ("local_armour_and_evasion_and_energy_shield_+%", 50));
        var local = ItemDefences.Calculate(shield,
            Program.Items.Defences(Program.Items.ByHash(shield.TypeHash)!)!.Value, Program.Items.Mods);
        Assert.Equal((180, 180, 180), local); // 100 * 1.5 local * 1.2 quality.
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, shield);
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 50), ("global_defences_+%", 50)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(360, pools.Armour);
        Assert.Equal(386, pools.Evasion);
        Assert.Equal(365u, pools.EnergyShield); // 180 local ES * (1 + .03 Intelligence + .50 shield + .50 global).
        Assert.Equal((180, 180, 180), ItemDefences.Calculate(shield,
            Program.Items.Defences(Program.Items.ByHash(shield.TypeHash)!)!.Value, Program.Items.Mods));
    }

    [Fact]
    public void Iron_Reflexes_counts_combined_shield_increases_once_and_uses_both_typed_increases()
    {
        var bonus = new CharacterStats.Bonus(Armour: 200, Evasion: 185,
            ArmourPct: 50, EvasionPct: 50, SharedDefencesPct: 50, IronReflexes: true,
            ArmourMorePct: 10, EvasionMorePct: 20,
            ShieldArmour: 100, ShieldEvasion: 100,
            ShieldArmourPct: 20, ShieldEvasionPct: 30, SharedShieldDefencesPct: 50);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        // Original armour: (200 * 1.5 + 100 * .7) * 1.1 = 407.
        // Converted evasion: (200 * 1.5 + 100 * (20 + 30 + 50)/100) * 1.2 * 1.1 = 528.
        Assert.Equal(935, pools.Armour);
        Assert.Equal(0, pools.Evasion);
    }

    [Fact]
    public void Iron_Reflexes_rounds_after_adding_armour_and_converted_evasion()
    {
        var bonus = new CharacterStats.Bonus(Armour: 1, ArmourPct: 50, IronReflexes: true, ShieldArmour: 1);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(24, pools.Armour); // Armour 1.5 + converted character evasion 22.5 = 24, not 2 + 23.
        Assert.Equal(0, pools.Evasion);
    }

    [Fact]
    public void Native_no_shield_defences_removes_the_shield_but_keeps_unrelated_flat_defences()
    {
        // This stat currently has no Mods.dat or passive source; exercise the final calculation boundary.
        var bonus = new CharacterStats.Bonus(Armour: 200, Evasion: 185, EnergyShield: 200,
            ShieldArmour: 100, ShieldEvasion: 100, ShieldEnergyShield: 100,
            SharedShieldDefencesPct: 200, NoShieldDefences: true);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(100, pools.Armour);
        Assert.Equal(102, pools.Evasion);
        Assert.Equal(103u, pools.EnergyShield);
    }

    [Fact]
    public void Necromantic_Aegis_removes_player_shield_defences_without_removing_other_items()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield());
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 50), ("minions_get_shield_stats_instead_of_you", 1)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(100, pools.Armour);
        Assert.Equal(102, pools.Evasion);
        Assert.Equal(103u, pools.EnergyShield);
    }

    [Theory]
    [InlineData(true, 0, 100)]
    [InlineData(false, 50, 50)]
    public void ES_to_life_uses_local_shield_ES_before_shield_and_global_increases(bool full, int percent, int life)
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield());
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 100), (FullConversion, full ? 1 : 0),
            (PercentConversion, percent)));
        Inventory.Add(7, 0, 0, Ring(("base_maximum_energy_shield", 100), ("maximum_energy_shield_+%", 100)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(bare.Life + (uint)life, pools.Life);
        Assert.Equal(203u, pools.EnergyShield); // Independent global ES stays ES; shield increases cannot raise it.
        Assert.Equal(200, pools.Armour); // Conversion affects ES only.
    }

    [Fact]
    public void Only_the_active_shield_receives_the_shield_increase_after_weapon_swap()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield(100, 0, 0));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Shield(200, 0, 0));
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 50)));

        Assert.Equal(150, Program.PoolsFor(conn, 100).Armour);
        Inventory.SwapWeaponSets();
        Assert.Equal(300, Program.PoolsFor(conn, 100).Armour);
        Inventory.SwapWeaponSets();
        Assert.Equal(150, Program.PoolsFor(conn, 100).Armour);
    }

    [Fact]
    public void Backpack_shields_and_backpack_shield_increases_do_not_contribute()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        Inventory.Add(PlayerInventories.MainInventory, 0, 0, Shield());
        Inventory.Add(PlayerInventories.MainInventory, 2, 0, Ring((ShieldDefences, 500)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(100, pools.Armour);
        Assert.Equal(102, pools.Evasion);
        Assert.Equal(103u, pools.EnergyShield);
    }

    [Fact]
    public void Requirement_disabled_shields_add_neither_local_defences_nor_their_global_shield_increase()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 85, 100));
        var shield = Inventory.Add(PlayerInventories.Offhand1, 0, 0,
            Armour("Pinnacle Tower Shield", 1000, 1000, 1000, 0, (ShieldDefences, 500)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Contains(shield.Id, Program.InactiveItems[_name]);
        Assert.Equal(100, pools.Armour);
        Assert.Equal(102, pools.Evasion);
        Assert.Equal(103u, pools.EnergyShield);
    }

    [Fact]
    public void Large_shield_increases_and_more_multipliers_saturate_without_wrapping()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: int.MaxValue, Evasion: int.MaxValue, EnergyShield: int.MaxValue,
                ShieldArmour: int.MaxValue, ShieldEvasion: int.MaxValue, ShieldEnergyShield: int.MaxValue,
                ShieldArmourPct: int.MaxValue, ShieldEvasionPct: int.MaxValue, ShieldEnergyShieldPct: int.MaxValue,
                SharedShieldDefencesPct: int.MaxValue, ArmourMorePct: int.MaxValue, EvasionMorePct: int.MaxValue,
                IronReflexes: true));

        Assert.Equal(int.MaxValue, pools.Armour);
        Assert.Equal(0, pools.Evasion);
        Assert.Equal((uint)int.MaxValue, pools.EnergyShield);
    }

    [Fact]
    public void Reduced_defences_cannot_make_another_source_negative()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: 200, ArmourPct: -150, ShieldArmour: 100, ShieldArmourPct: 200));

        // Other armour clamps at zero; the shield has -150% global +200% shield = +50%.
        Assert.Equal(150, pools.Armour);
    }

    [Fact]
    public void Removing_the_shield_clears_its_scaled_pool_and_published_panel_values()
    {
        var conn = Character();
        var shield = Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield(100, 0, 100));
        Inventory.Add(6, 0, 0, Ring((ShieldDefences, 50)));
        conn.Pools = Program.PoolsFor(conn, 100);
        var before = Published(conn);
        Assert.Equal(150, before[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(153, before[Program.Stats.Key("maximum_energy_shield")!.Value]);

        Assert.NotNull(Inventory.Remove(PlayerInventories.Offhand1, shield.Id));
        conn.Pools = Program.PoolsFor(conn, 100);
        var after = Published(conn);

        Assert.Equal(0, after[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(0, after[Program.Stats.Key("maximum_energy_shield")!.Value]);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
