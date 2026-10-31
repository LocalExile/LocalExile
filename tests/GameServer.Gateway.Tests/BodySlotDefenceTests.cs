using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class BodySlotDefenceTests : IDisposable
{
    const string BodyES = "maximum_energy_shield_from_body_armour_+%";
    const string DoubleBody = "damage_reduction_rating_from_body_armour_doubled";
    const string NoBodyAR = "gain_no_armour_from_body_armour";
    const string Full = "gain_maximum_life_instead_of_maximum_es_from_armour";
    const string Half = "gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value";
    const ushort DoubleNode = 53816;
    readonly string _name = "BodyDefence" + Guid.NewGuid().ToString("N");

    public BodySlotDefenceTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "BodyDefences", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.FirstOrDefault(m => m.Stats.Any(s => s.Id == stat && value >= s.Min && value <= s.Max))
            ?? Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Ring(params (string Stat, int Value)[] mods) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static Item Armour(string name, int armour, int evasion, int es, bool removeRequirements = true,
        params (string Stat, int Value)[] extra)
    {
        var type = Program.Items.Find(name)!;
        var basis = Program.Items.Defences(type)!.Value.AtRoll(0);
        List<ItemMod> mods =
        [
            Mod("local_base_physical_damage_reduction_rating", armour - basis.Armour),
            Mod("local_base_evasion_rating", evasion - basis.Evasion),
            Mod("local_energy_shield", es - basis.EnergyShield),
        ];
        if (removeRequirements) mods.Add(Mod("local_no_attribute_requirements", 1));
        mods.AddRange(extra.Select(m => Mod(m.Stat, m.Value)));
        return ItemCatalog.Create(type, defenceBaseRoll: 0) with { Quality = 0, Mods = mods };
    }

    static (int Armour, int Evasion, int EnergyShield) Local(Item item) =>
        ItemDefences.Calculate(item, Program.Items.Defences(Program.Items.ByHash(item.TypeHash)!)!.Value, Program.Items.Mods);

    static void Discipline(ClientState conn) => conn.Auras[0x103] = new AuraCatalog.Aura(0x103,
        Program.Skills.EffectRow("Discipline"), "Discipline", "Discipline", 1, 148, 0, 3500, SourceBuff: 2);

    InventoryItem Equip121ES()
    {
        var body = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 100, 100, 101));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Wool Gloves", 25, 13, 13));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Twig Spirit Shield", 30, 20, 7));
        return body;
    }

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).ToDictionary(s => s.Item1, s => s.Item2);

    [Fact]
    public void Body_ES_increase_adds_to_global_increases_only_on_the_body_contribution()
    {
        var bonus = new CharacterStats.Bonus(EnergyShield: 200, BodyEnergyShield: 100,
            EnergyShieldPct: 50, BodyEnergyShieldPct: 50);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(356u, pools.EnergyShield); // Body 100 * 2.03 + other 100 * 1.53; not 100 * 1.5 * 1.53.
    }

    [Theory]
    [InlineData(false, 811)]
    [InlineData(true, 558)]
    public void Body_shield_and_aura_sources_keep_their_own_increases_and_exclusions(bool excludeShield, uint es)
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 0, 0, 100));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Twig Spirit Shield", 0, 0, 100));
        Inventory.Add(6, 0, 0, Ring((BodyES, 30), ("shield_armour_+%", 50),
            ("base_maximum_energy_shield", 100), ("maximum_energy_shield_+%", 100),
            ("minions_get_shield_stats_instead_of_you", excludeShield ? 1 : 0)));
        Discipline(conn);

        var pools = Program.PoolsFor(conn, 100);

        // Body 233 + shield 253 (or excluded) + (100 independent + 60 Discipline) * 2.03 = 810.8/557.8.
        Assert.Equal(es, pools.EnergyShield);
    }

    [Fact]
    public void Doubling_body_armour_keeps_other_armour_and_unconverted_evasion_and_ES_unchanged()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: 200, Evasion: 185, EnergyShield: 200,
                BodyArmour: 100, BodyEvasion: 100, BodyEnergyShield: 100, DoubleBodyArmour: true));

        Assert.Equal(300, pools.Armour);
        Assert.Equal(204, pools.Evasion); // (15 character + 185 flat) * 1.02; body evasion is not doubled yet.
        Assert.Equal(206u, pools.EnergyShield);
    }

    [Fact]
    public void The_native_Unbreakable_passive_doubles_only_body_armour()
    {
        var conn = Character();
        Equip121ES();
        conn.Passives.Add(DoubleNode);
        Assert.Equal("Unbreakable", Program.Passives.Find(DoubleNode)?.Name);
        Assert.Contains((Key(DoubleBody), 1), Program.Passives.Find(DoubleNode)!.Stats);

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(255, pools.Armour); // 200 doubled body + 25 gloves + 30 shield.
        Assert.Equal(151, pools.Evasion);
        Assert.Equal(125u, pools.EnergyShield);
    }

    [Fact]
    public void Native_no_body_armour_removes_body_AR_but_keeps_ordinary_body_evasion_and_ES()
    {
        var conn = Character();
        Equip121ES();
        Inventory.Add(6, 0, 0, Ring((NoBodyAR, 1)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(55, pools.Armour);
        Assert.Equal(151, pools.Evasion);
        Assert.Equal(125u, pools.EnergyShield);
    }

    [Theory]
    [InlineData(false, 100, 204)]
    [InlineData(true, 200, 0)]
    public void No_body_armour_wins_over_doubling_and_also_excludes_IR_converted_body_evasion(
        bool ironReflexes, int armour, int evasion)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: 200, Evasion: 185, EnergyShield: 200,
                BodyArmour: 100, BodyEvasion: 100, BodyEnergyShield: 100,
                DoubleBodyArmour: true, NoBodyArmour: true, IronReflexes: ironReflexes));

        Assert.Equal(armour, pools.Armour);
        Assert.Equal(evasion, pools.Evasion);
        Assert.Equal(206u, pools.EnergyShield);
    }

    [Fact]
    public void Iron_Reflexes_doubles_converted_body_evasion_without_adding_Dexterity_evasion()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: 200, Evasion: 185, BodyArmour: 100, BodyEvasion: 100,
                DoubleBodyArmour: true, IronReflexes: true));

        Assert.Equal(600, pools.Armour); // 100 other AR + 200 body AR + 100 other EV + 200 body EV.
        Assert.Equal(0, pools.Evasion);
    }

    [Fact]
    public void Body_doubling_composes_with_distinct_shield_increases_global_shared_increases_and_both_mores()
    {
        var bonus = new CharacterStats.Bonus(Armour: 200, Evasion: 200,
            ArmourPct: 50, EvasionPct: 50, SharedDefencesPct: 50, IronReflexes: true,
            ArmourMorePct: 10m, EvasionMorePct: 20m,
            BodyArmour: 100, BodyEvasion: 100, DoubleBodyArmour: true,
            ShieldArmour: 100, ShieldEvasion: 100, ShieldArmourPct: 20,
            ShieldEvasionPct: 30, SharedShieldDefencesPct: 50);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        // Original: (100 body * 1.5 * 2 + 100 shield * 2.2) * 1.1 = 572.
        // Converted EV: (15 character * 1.5 + 100 body * 1.5 * 2 + 100 shield * 2.5) * 1.2 * 1.1 = 755.7.
        Assert.Equal(1328, pools.Armour);
        Assert.Equal(0, pools.Evasion);
    }

    [Fact]
    public void Rolled_base_local_mods_and_quality_finish_before_body_ES_increase()
    {
        var conn = Character();
        var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!, defenceBaseRoll: 16) with
        {
            Quality = 20,
            Mods = [Mod("local_energy_shield", 10), Mod("local_energy_shield_+%", 50)],
        };
        Assert.Equal(49, Local(robe).EnergyShield); // (17 rolled base + 10) * 1.5 local * 1.2 quality = 48.6.
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, robe);
        Inventory.Add(6, 0, 0, Ring((BodyES, 30), ("maximum_energy_shield_+%", 50)));

        Assert.Equal(90u, Program.PoolsFor(conn, 100).EnergyShield); // 49 * (1 + .03 + .50 + .30) = 89.67.
        Assert.Equal(49, Local(robe).EnergyShield);
    }

    [Theory]
    [InlineData("local_no_energy_shield")]
    [InlineData("local_unique_tabula_rasa_no_requirement_or_energy_shield")]
    public void Local_body_ES_prohibitions_remain_zero_before_body_scaling(string prohibition)
    {
        var conn = Character();
        var body = Armour("Plate Vest", 100, 100, 101, true, (prohibition, 1));
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, body);
        Inventory.Add(6, 0, 0, Ring((BodyES, 30), ("base_maximum_energy_shield", 100)));

        Assert.Equal(0, Local(body).EnergyShield);
        Assert.Equal(103u, Program.PoolsFor(conn, 100).EnergyShield); // Independent ES remains; no phantom body subset.
    }

    [Theory]
    [InlineData(true, false, 121)]
    [InlineData(true, true, 121)]
    [InlineData(false, false, 59)]
    [InlineData(false, true, 59)]
    public void ES_to_life_uses_local_body_ES_before_body_scaling_or_armour_doubling(
        bool full, bool doubled, uint life)
    {
        var conn = Character();
        uint bareLife = Program.PoolsFor(conn, 100).Life;
        Equip121ES();
        if (doubled) conn.Passives.Add(DoubleNode);
        Inventory.Add(6, 0, 0, Ring((BodyES, 30), (Full, full ? 1 : 0), (Half, full ? 0 : 50)));
        Inventory.Add(7, 0, 0, Ring(("base_maximum_energy_shield", 100), ("maximum_energy_shield_+%", 100)));
        Discipline(conn);

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(bareLife + life, pools.Life); // Half truncates each local source: 50 body + 6 gloves + 3 shield.
        Assert.Equal(325u, pools.EnergyShield); // Only (100 independent + 60 Discipline) * 2.03 survives as ES.
    }

    [Fact]
    public void Requirement_disabled_body_supplies_no_scaled_or_doubled_defence()
    {
        var conn = Character();
        var body = Inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            Armour("Glorious Plate", 100, 100, 101, removeRequirements: false));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Wool Gloves", 25, 13, 13));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Twig Spirit Shield", 30, 20, 7));
        Inventory.Add(6, 0, 0, Ring((BodyES, 30)));
        conn.Passives.Add(DoubleNode);

        var pools = Program.PoolsFor(conn, 100);

        Assert.Contains(body.Id, Program.InactiveItems[_name]);
        Assert.Equal(55, pools.Armour);
        Assert.Equal(49, pools.Evasion);
        Assert.Equal(21u, pools.EnergyShield);
    }

    [Fact]
    public void Backpack_body_and_its_body_ES_modifier_do_not_change_other_equipment()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.MainInventory, 0, 0, Armour("Plate Vest", 1000, 1000, 1000, true, (BodyES, 1000)));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Wool Gloves", 25, 13, 13));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Twig Spirit Shield", 30, 20, 7));
        conn.Passives.Add(DoubleNode);

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(55, pools.Armour);
        Assert.Equal(49, pools.Evasion);
        Assert.Equal(21u, pools.EnergyShield);
    }

    [Fact]
    public void Removing_body_clears_its_scaled_pool_and_published_values_without_losing_other_sources()
    {
        var conn = Character();
        var body = Equip121ES();
        Inventory.Add(6, 0, 0, Ring((BodyES, 30)));
        conn.Passives.Add(DoubleNode);
        conn.Pools = Program.PoolsFor(conn, 100);
        var before = Published(conn);
        Assert.Equal(255, before[Key("physical_damage_reduction_rating")]);
        Assert.Equal(151, before[Key("evasion_rating")]);
        Assert.Equal(155, before[Key("maximum_energy_shield")]); // Body 101 * 1.33 + other 20 * 1.03 = 154.93.

        Assert.NotNull(Inventory.Remove(PlayerInventories.BodyArmour, body.Id));
        conn.Pools = Program.PoolsFor(conn, 100);
        var after = Published(conn);

        Assert.Equal(55, after[Key("physical_damage_reduction_rating")]);
        Assert.Equal(49, after[Key("evasion_rating")]);
        Assert.Equal(21, after[Key("maximum_energy_shield")]);
    }

    [Fact]
    public void A_reduced_body_ES_bucket_cannot_subtract_from_independent_ES()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(EnergyShield: 200, BodyEnergyShield: 100, BodyEnergyShieldPct: -200));

        Assert.Equal(103u, pools.EnergyShield); // Body factor clamps at zero; other 100 * 1.03 remains.
    }

    [Fact]
    public void Large_body_defences_and_increases_saturate_instead_of_wrapping()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: int.MaxValue, Evasion: int.MaxValue, EnergyShield: int.MaxValue,
                BodyArmour: int.MaxValue, BodyEvasion: int.MaxValue, BodyEnergyShield: int.MaxValue,
                BodyEnergyShieldPct: int.MaxValue, DoubleBodyArmour: true, IronReflexes: true,
                ArmourMorePct: int.MaxValue, EvasionMorePct: int.MaxValue));

        Assert.Equal(int.MaxValue, pools.Armour);
        Assert.Equal(0, pools.Evasion);
        Assert.Equal((uint)int.MaxValue, pools.EnergyShield);
    }

    [Fact]
    public void Saturated_equipment_total_keeps_the_body_source_when_the_shield_is_excluded()
    {
        // The total may have saturated while adding two individually representable local item values.
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(EnergyShield: int.MaxValue, BodyEnergyShield: int.MaxValue,
                ShieldEnergyShield: int.MaxValue, BodyEnergyShieldPct: 30, NoShieldDefences: true));

        Assert.Equal((uint)int.MaxValue, pools.EnergyShield);
    }

    [Fact]
    public void Fractional_body_and_other_contributions_survive_until_one_final_round()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(Armour: 2, ArmourPct: 40, BodyArmour: 1, DoubleBodyArmour: true,
                EnergyShield: 2, EnergyShieldPct: 34, BodyEnergyShield: 1, BodyEnergyShieldPct: 40));

        Assert.Equal(4, pools.Armour); // 1.4 other + 2.8 doubled body = 4.2.
        Assert.Equal(3u, pools.EnergyShield); // 1.37 other + 1.77 body = 3.14.
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
        Program.GemSkillIdsOf.TryRemove(_name, out _);
    }
}
