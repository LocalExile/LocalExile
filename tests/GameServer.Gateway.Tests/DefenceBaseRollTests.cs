using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DefenceBaseRollTests : IDisposable
{
    readonly string _name = "BaseDefence" + Guid.NewGuid().ToString("N");

    public DefenceBaseRollTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "BaseDefenceRoll", 128, 128, 32, 32, null, []), 0, 1)
    { Character = _name, ClassRow = 4 }; // Duelist: meets Scale Vest's 17 strength/dexterity requirements.

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Armour(string name, byte roll, byte quality = 0, params (string Stat, int Value)[] mods) =>
        ItemCatalog.Create(Program.Items.Find(name)!, defenceBaseRoll: roll) with
        { Quality = quality, Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static (int Armour, int Evasion, int EnergyShield) Equipped(ClientState conn)
    {
        var (armour, evasion, shield, _) = ((decimal, decimal, int, decimal))typeof(Program).GetMethod("EquippedBaseDefences",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn, null])!;
        return ((int)Math.Min(int.MaxValue, armour), (int)Math.Min(int.MaxValue, evasion), shield);
    }

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    [Theory]
    [InlineData(0, 19)]
    [InlineData(1, 19)]
    [InlineData(2, 20)]
    [InlineData(8, 21)]
    [InlineData(16, 23)]
    [InlineData(30, 27)]
    [InlineData(31, 27)]
    [InlineData(32, 19)]
    public void Native_ordinary_rolls_interpolate_the_base_and_special_32_uses_the_minimum(byte roll, int expected)
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", roll));

        // Native Armour: round_half_up(19 + (27 - 19) * min(roll, 31) / 31), except special wire 32 -> base roll 0.
        Assert.Equal((expected, 0, 0), Equipped(conn));
    }

    [Theory]
    [InlineData(0, 329, 329, 67)]
    [InlineData(15, 353, 353, 72)]
    [InlineData(16, 354, 354, 72)]
    [InlineData(31, 378, 378, 77)]
    [InlineData(32, 329, 329, 67)]
    public void One_stored_roll_controls_all_three_defence_ranges(byte roll, int armour, int evasion, int energyShield)
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Sacrificial Garb", roll));

        Assert.Equal((armour, evasion, energyShield), Equipped(conn));
    }

    [Theory]
    [InlineData("Plate Vest", PlayerInventories.BodyArmour, "local_physical_damage_reduction_rating_+%", 41, 0, 0)]
    [InlineData("Rawhide Gloves", PlayerInventories.Gloves, "local_evasion_rating_+%", 0, 29, 0)]
    [InlineData("Simple Robe", PlayerInventories.BodyArmour, "local_energy_shield_+%", 0, 0, 31)]
    public void The_base_rounds_before_local_increases_and_quality_for_every_defence(
        string type, byte row, string increase, int armour, int evasion, int energyShield)
    {
        var conn = Character();
        Inventory.Add(row, 0, 0, Armour(type, 16, 20, (increase, 50)));

        // The native base is whole first, then the local item result rounds once: 23/16/17 * 1.5 * 1.2.
        // Carrying the fractional interpolated base through all multipliers would instead give 42/28/31.
        Assert.Equal((armour, evasion, energyShield), Equipped(conn));
    }

    [Fact]
    public void Local_flat_mods_are_added_after_interpolation_rather_than_being_scaled_by_the_roll()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 2, 20,
            ("local_base_physical_damage_reduction_rating", 10),
            ("local_physical_damage_reduction_rating_+%", 50)));

        Assert.Equal((54, 0, 0), Equipped(conn)); // round((round(19 + 8 * 2/31) + 10) * 1.5 * 1.2).
    }

    [Fact]
    public void Changing_a_stored_roll_refreshes_the_real_pools_and_published_panel()
    {
        var conn = Character();
        var placed = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 0, 20,
            ("physical_damage_reduction_rating_+%", 50), ("evasion_rating_+%", 50)));
        conn.Pools = Program.PoolsFor(conn, 100);
        var minimum = Published(conn);
        Assert.Equal(48, minimum[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(72, minimum[Program.Stats.Key("evasion_rating")!.Value]);

        Assert.NotNull(Inventory.Change(PlayerInventories.BodyArmour, placed.Id, i => i with { DefenceBaseRoll = 31 }));
        conn.Pools = Program.PoolsFor(conn, 100);
        var maximum = Published(conn);
        Assert.Equal(69, maximum[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(94, maximum[Program.Stats.Key("evasion_rating")!.Value]); // (15 base + 46 local) * 1.54 = 93.94.

        Assert.NotNull(Inventory.Change(PlayerInventories.BodyArmour, placed.Id, i => i with { DefenceBaseRoll = 32 }));
        conn.Pools = Program.PoolsFor(conn, 100);
        var special = Published(conn);
        Assert.Equal(48, special[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(72, special[Program.Stats.Key("evasion_rating")!.Value]);
    }

    [Fact]
    public void The_active_shields_own_roll_follows_the_existing_weapon_swap()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Splintered Tower Shield", 31));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Armour("Goathide Buckler", 16));
        Assert.Equal((12, 0, 0), Equipped(conn));

        Inventory.SwapWeaponSets();
        Assert.Equal((0, 17, 0), Equipped(conn));
        Inventory.SwapWeaponSets();
        Assert.Equal((12, 0, 0), Equipped(conn));
    }

    sealed class EndpointRandom(int result) : Random
    {
        public int Draws;
        public override int Next(int maxValue) => Next(0, maxValue);
        public override int Next(int minValue, int maxValue)
        {
            Assert.Equal(0, minValue);
            Assert.Equal(32, maxValue);
            Draws++;
            return result;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Newly_created_armour_draws_one_ordinary_roll_including_both_endpoints(byte roll)
    {
        var random = new EndpointRandom(roll);
        var type = Program.Items.Find("Plate Vest")! with { Implicits = [] };
        var item = ItemCatalog.Create(type, random: random);

        Assert.Equal(roll, item.DefenceBaseRoll);
        Assert.Equal(1, random.Draws);
        _ = item.Payload();
        _ = item.Payload();
        Assert.Equal(1, random.Draws); // Serialization cannot reroll an existing item's base.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    public void Explicit_rolls_do_not_draw_an_unwanted_new_base_roll(byte roll)
    {
        var random = new EndpointRandom(31);
        var type = Program.Items.Find("Plate Vest")! with { Implicits = [] };
        var item = ItemCatalog.Create(type, random: random, defenceBaseRoll: roll);

        Assert.Equal(roll, item.DefenceBaseRoll);
        Assert.Equal(0, random.Draws);
    }

    [Fact]
    public void Non_armour_creation_does_not_invent_a_defence_roll_or_consume_its_random_draw()
    {
        var random = new EndpointRandom(31);
        var type = Program.Items.Find("Scroll of Wisdom")! with { Implicits = [] };
        Assert.DoesNotContain("Armour", type.Components);
        var item = ItemCatalog.Create(type, random: random);

        Assert.Equal(0, item.DefenceBaseRoll);
        Assert.Equal(0, random.Draws);
    }

    [Fact]
    public void Ordinary_mod_rerolls_quality_crafting_and_mirroring_preserve_the_existing_roll()
    {
        var rolls = new ItemRolls(Program.Items);
        var item = Armour("Plate Vest", 17, 12) with { ItemLevel = 100 };
        var rare = rolls.Roll(item, ItemRarity.Rare, new Random(75));
        Assert.Equal(17, rare.DefenceBaseRoll);
        var divine = rolls.Reforge(rare, new Random(76));
        Assert.NotNull(divine);
        Assert.Equal(17, divine.DefenceBaseRoll);
        var normal = CurrencyEffects.Scouring(divine, rolls);
        Assert.NotNull(normal);
        Assert.Equal(17, normal.DefenceBaseRoll);
        var quality = CurrencyEffects.ArmourerScrap(normal);
        Assert.NotNull(quality);
        Assert.Equal(13, quality.Quality);
        Assert.Equal(17, quality.DefenceBaseRoll);
        var mirror = CurrencyEffects.Mirror(quality, rolls);
        Assert.NotNull(mirror);
        Assert.Equal(17, mirror.DefenceBaseRoll);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
