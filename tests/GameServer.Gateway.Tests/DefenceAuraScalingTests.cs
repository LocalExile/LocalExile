using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DefenceAuraScalingTests : IDisposable
{
    readonly string _name = "DefenceAuras" + Guid.NewGuid().ToString("N");
    static readonly Lazy<SkillCatalog> NativeSkills = new(() => new SkillCatalog(Data()));

    public DefenceAuraScalingTests() => TestSaves.Ensure();

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    ClientState Character() => new(new(0, "DefenceAuras", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static AuraCatalog.Aura Aura(string family, int level, int quality = 0, bool ruthless = false)
    {
        var skills = ruthless ? NativeSkills.Value : Program.Skills;
        int effect = skills.EffectRow(ruthless ? family + "HardMode" : family);
        string id = skills.EffectId(effect)!;
        return new(family == "Determination" ? (ushort)0x103 : (ushort)0x104, effect, id, family,
            family == "Determination" ? (ushort)1 : (ushort)3, AuraCatalog.BuffRow(id), 0, 5000,
            level, quality, SourceBuff: family == "Determination" ? (ushort)2 : (ushort)4);
    }

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Ring(params (string Stat, int Value)[] mods) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static Item Shield(int armour, int evasion) => ItemCatalog.Create(Program.Items.Find("Twig Spirit Shield")!, defenceBaseRoll: 0) with
        { Mods = [Mod("local_base_physical_damage_reduction_rating", armour), Mod("local_base_evasion_rating", evasion)] };

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).ToDictionary(s => s.Item1, s => s.Item2);

    [Theory]
    [InlineData("Determination", false, 1, 0, 90, 40)]
    [InlineData("Determination", false, 20, 0, 1026, 49)]
    [InlineData("Determination", false, 20, 20, 1026, 49)]
    [InlineData("Grace", false, 1, 0, 136, 20)]
    [InlineData("Grace", false, 20, 0, 1545, 29)]
    [InlineData("Grace", false, 20, 20, 1545, 29)]
    [InlineData("Determination", true, 1, 0, 0, 40)]
    [InlineData("Determination", true, 20, 20, 0, 59)]
    [InlineData("Grace", true, 1, 0, 0, 40)]
    [InlineData("Grace", true, 20, 20, 0, 59)]
    public void Native_levels_quality_and_mode_supply_their_own_flat_and_more_values(
        string family, bool ruthless, int level, int quality, int flat, int more)
    {
        var skills = ruthless ? NativeSkills.Value : Program.Skills;
        var aura = Aura(family, level, quality, ruthless);
        var bonus = AuraCatalog.Bonus([aura], 0, skills, Program.Stats);

        Assert.Equal(flat, family == "Determination" ? bonus.Armour : bonus.Evasion);
        Assert.Equal(more, family == "Determination" ? bonus.ArmourMorePct : bonus.EvasionMorePct);
        Assert.Equal(0, family == "Determination" ? bonus.Evasion : bonus.Armour);
        Assert.Equal(0, bonus.EnergyShield);
        var own = skills.EffectStats(aura.Effect, level, quality).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(quality * 2, own.GetValueOrDefault(Program.Stats.Key("base_skill_area_of_effect_+%")!.Value));
        Assert.Equal((0, 5000), skills.Reservation(aura.Effect, level));
    }

    [Theory]
    [InlineData("Determination", false, 1539, 73)]
    [InlineData("Grace", false, 2317, 43)]
    [InlineData("Determination", true, 0, 88)]
    [InlineData("Grace", true, 0, 88)]
    public void Aura_effect_truncates_flat_and_more_stats_before_character_calculation(
        string family, bool ruthless, int flat, int more)
    {
        var skills = ruthless ? NativeSkills.Value : Program.Skills;
        var bonus = AuraCatalog.Bonus([Aura(family, 20, 20, ruthless)], 50, skills, Program.Stats);

        Assert.Equal(flat, family == "Determination" ? bonus.Armour : bonus.Evasion);
        Assert.Equal(more, family == "Determination" ? bonus.ArmourMorePct : bonus.EvasionMorePct);
        // Ordinary Grace: 1545 * 1.5 -> 2317; its 29% more becomes 43, not 44.
        // Ordinary Determination's 49% more becomes 73; both Ruthless 59% values become 88.
    }

    [Fact]
    public void Determination_flat_is_global_while_shield_increases_remain_scoped_to_the_shield()
    {
        var conn = Character();
        conn.Auras[0x103] = Aura("Determination", 20);
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield(100, 0));
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", 50), ("shield_armour_+%", 50),
            ("physical_damage_reduction_rating_+%", 50)));

        conn.Pools = Program.PoolsFor(conn, 100);

        // (1539 aura * 1.5 global + 100 shield * 2.0 global/shield) * 1.73 = 4339.705.
        Assert.Equal(4340, conn.Pools.Armour);
        Assert.Equal(4340, Published(conn)[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
    }

    [Fact]
    public void Grace_flat_and_shield_sources_keep_global_dexterity_and_more_ordering()
    {
        var conn = Character();
        conn.Auras[0x104] = Aura("Grace", 20);
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield(0, 100));
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", 50), ("shield_armour_+%", 50),
            ("evasion_rating_+%", 50)));

        conn.Pools = Program.PoolsFor(conn, 100);

        // ((15 character + 2317 aura) * 1.52 + 100 shield * 2.02) * 1.43 = 5357.6952.
        Assert.Equal(5358, conn.Pools.Evasion);
    }

    [Fact]
    public void Both_defence_auras_feed_Iron_Reflexes_with_shared_increases_counted_once()
    {
        var conn = Character();
        conn.Auras[0x103] = Aura("Determination", 20);
        conn.Auras[0x104] = Aura("Grace", 20);
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield(100, 100));
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", 50), ("shield_armour_+%", 50),
            ("global_defences_+%", 20), ("keystone_iron_reflexes", 1)));

        conn.Pools = Program.PoolsFor(conn, 100);

        // Original armour = (1539 * 1.2 + 100 * 1.7) * 1.73 = 3489.064.
        // Converted EV = ((15 + 2317) * 1.2 + 100 * 1.7) * 1.43 * 1.73 = 7343.52476.
        Assert.Equal(10833, conn.Pools.Armour);
        Assert.Equal(0, conn.Pools.Evasion);
    }

    [Fact]
    public void Removing_Grace_clears_its_scaled_flat_and_more_contributions_from_the_panel()
    {
        var conn = Character();
        conn.Auras[0x104] = Aura("Grace", 20);
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", 50)));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(3401, Published(conn)[Program.Stats.Key("evasion_rating")!.Value]);

        conn.Auras.Clear();
        conn.Pools = Program.PoolsFor(conn, 100);

        Assert.Equal(15, Published(conn)[Program.Stats.Key("evasion_rating")!.Value]);
    }

    [Theory]
    [InlineData("Grace")]
    [InlineData("Determination")]
    public void Fully_reduced_aura_effect_keeps_reservation_without_negative_defences(string family)
    {
        var aura = Aura(family, 20);
        var bonus = AuraCatalog.Bonus([aura], -200, Program.Skills, Program.Stats);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, bonus);

        Assert.Equal(0, bonus.Armour);
        Assert.Equal(0, bonus.Evasion);
        Assert.Equal(0m, bonus.ArmourMorePct);
        Assert.Equal(0m, bonus.EvasionMorePct);
        Assert.Equal(325, AuraCatalog.RequiredMana(pools, [aura])); // 50% of the Witch's ordinary 650 mana.
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
