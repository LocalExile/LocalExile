using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public sealed class SacredOrbTests : IDisposable
{
    readonly string _character = "Sacred" + Guid.NewGuid().ToString("N");
    readonly string _account = "SacredStash" + Guid.NewGuid().ToString("N");
    const byte Main = PlayerInventories.MainInventory;

    public SacredOrbTests() => TestSaves.Ensure();

    static ItemCatalog Catalog => Program.Items;
    PlayerInventories Items => PlayerInventories.For(_character, Catalog);
    AccountStash Stash => AccountStash.For(_account, Catalog);

    static Item Armour(string name = "Plate Vest", byte roll = 0) =>
        ItemCatalog.Create(Catalog.Find(name)!, defenceBaseRoll: roll);

    static Item Orbs(ushort amount) =>
        ItemCatalog.Create(Catalog.Find(CurrencyEffects.SacredOrb)!) with { StackSize = amount };

    static Item? Apply(Item item, Random random) => CurrencyEffects.Sacred(item,
        Catalog.ByHash(item.TypeHash) is { } type ? Catalog.Defences(type) : null, random);

    static PlayerInventories.Shape? Shape(Item item) => Catalog.ByHash(item.TypeHash) is { } type
        ? new(type.Width, type.Height, type.Class, type.MaxStack) : null;

    sealed class RollRandom(int roll) : Random
    {
        public int Draws;
        public override int Next(int maximum)
        {
            Assert.Equal(32, maximum);
            Draws++;
            return roll;
        }
    }

    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
        return (Items, Stash);
    }

    InventoryItem PlaceInStash(Item item, int x) =>
        Stash.Place(0, x, 0, new InventoryItem(1, 0, 0, item), Shape, out _)!.Value.Placed;

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    [Fact]
    public void Sacred_orb_uses_the_native_currency_type_and_stack_metadata()
    {
        var type = Catalog.Find(CurrencyEffects.SacredOrb)!;
        Assert.NotNull(type);
        Assert.Equal("Sacred Orb", type.Name);
        Assert.Equal("StackableCurrency", type.Class);
        Assert.Contains("Stack", type.Components);
        Assert.True(type.MaxStack > 1);
    }

    [Theory]
    [InlineData(0, 329, 329, 67)]
    [InlineData(16, 354, 354, 72)]
    [InlineData(31, 378, 378, 77)]
    public void One_ordinary_roll_can_reach_both_endpoints_and_controls_every_defence(
        byte roll, int armour, int evasion, int energyShield)
    {
        var random = new RollRandom(roll);
        var before = Armour("Sacrificial Garb", roll: 7);
        var after = Apply(before, random)!;

        Assert.NotNull(after);
        Assert.Equal(roll, after.DefenceBaseRoll);
        Assert.Equal(1, random.Draws);
        Assert.Equal((armour, evasion, energyShield), Catalog.Defences(Catalog.ByHash(after.TypeHash)!)!.Value.AtRoll(after.DefenceBaseRoll));
        Assert.Equal(7, before.DefenceBaseRoll);
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void Rarity_does_not_prevent_an_otherwise_eligible_armour_reroll(ItemRarity rarity)
    {
        var before = Armour() with { Rarity = rarity, Identified = false };
        var after = Apply(before, new RollRandom(31))!;

        Assert.NotNull(after);
        Assert.Equal(rarity, after.Rarity);
        Assert.False(after.Identified);
        Assert.Equal(31, after.DefenceBaseRoll);
    }

    [Theory]
    [InlineData("Driftwood Wand")]
    [InlineData("Iron Ring")]
    [InlineData("Small Life Flask")]
    public void Non_armour_targets_are_refused_without_drawing_a_roll(string type)
    {
        var random = new RollRandom(31);
        var item = ItemCatalog.Create(Catalog.Find(type)!);
        Assert.DoesNotContain("Armour", item.Components);

        Assert.Null(Apply(item, random));
        Assert.Equal(0, random.Draws);
    }

    [Fact]
    public void Armour_without_a_Mods_component_is_refused_without_drawing_a_roll()
    {
        var random = new RollRandom(31);
        var before = Armour();
        before = before with { Components = before.Components.Where(c => c != "Mods").ToArray() };

        Assert.Null(Apply(before, random));
        Assert.Equal(0, random.Draws);
    }

    [Fact]
    public void An_unknown_base_defence_range_is_refused_without_drawing_a_roll()
    {
        var random = new RollRandom(31);

        Assert.Null(CurrencyEffects.Sacred(Armour(), null, random));
        Assert.Equal(0, random.Draws);
    }

    [Theory]
    [InlineData("Kaom's Plate")]
    [InlineData("Kaom's Greaves")]
    [InlineData("Golden Wreath")]
    public void Fixed_native_ranges_are_refused_even_when_the_target_has_armour_components(string name)
    {
        var before = Armour(name, 7);
        var defences = Catalog.Defences(Catalog.ByHash(before.TypeHash)!)!.Value;
        Assert.Contains("Armour", before.Components);
        Assert.Contains("Mods", before.Components);
        Assert.False(defences.HasVariableRange);
        var random = new RollRandom(31);

        Assert.Null(Apply(before, random));
        Assert.Equal(0, random.Draws);
        Assert.Equal(7, before.DefenceBaseRoll);
    }

    [Fact]
    public void Ward_only_native_armour_is_rerollable_despite_zero_armour_evasion_and_ES_ranges()
    {
        var before = Armour("Runic Helm", 7);
        var defences = Catalog.Defences(Catalog.ByHash(before.TypeHash)!)!.Value;
        Assert.Equal((0, 0, 0), defences.AtRoll(0));
        Assert.Equal((0, 0, 0), defences.AtRoll(31));
        Assert.True(defences.HasAdditionalVariableRange);
        Assert.True(defences.HasVariableRange);
        var random = new RollRandom(31);

        var after = Apply(before, random)!;

        Assert.NotNull(after);
        Assert.Equal(31, after.DefenceBaseRoll);
        Assert.Equal(1, random.Draws);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Rerolling_the_special_minimum_state_replaces_it_with_an_ordinary_roll(byte roll)
    {
        var before = Armour(roll: Item.SpecialDefenceBaseRoll);
        var random = new RollRandom(roll);

        var after = Apply(before, random)!;

        Assert.NotNull(after);
        Assert.Equal(roll, after.DefenceBaseRoll);
        Assert.Equal(1, random.Draws);
        Assert.Equal(Armour(roll: roll).Payload(), after.Payload());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Corrupted_or_mirrored_armour_is_refused_without_drawing_a_roll(bool corrupted, bool mirrored)
    {
        var random = new RollRandom(31);
        var before = Armour(roll: 7) with { Corrupted = corrupted, Mirrored = mirrored };

        Assert.Null(Apply(before, random));
        Assert.Equal(0, random.Draws);
        Assert.Equal(7, before.DefenceBaseRoll);
    }

    [Fact]
    public void Reroll_changes_only_the_base_roll_and_preserves_fractures_quality_sockets_gems_and_mods()
    {
        var gem = ItemCatalog.Create(Catalog.Find("Metadata/Items/Gems/SkillGemFireball")!)
            with { Quality = 8, GemExperience = 1200 };
        var mod = Catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == "local_physical_damage_reduction_rating_+%"))
            .Roll(ModSection.Explicit, new Random(75));
        var before = Armour(roll: 7) with
        {
            ItemLevel = 85, Quality = 20, Rarity = ItemRarity.Rare, Identified = true,
            Mods = [mod], FracturedCount = 1, NameWords = [2, 3],
            Sockets = [new(SocketColour.Blue, gem), new(SocketColour.White)], Links = [2],
        };

        var after = Apply(before, new RollRandom(31))!;

        Assert.Equal(before with { DefenceBaseRoll = 31 }, after);
        Assert.Same(before.Mods, after.Mods);
        Assert.Same(before.Sockets, after.Sockets);
        Assert.Same(before.Links, after.Links);
        Assert.Equal(before.Payload(), (after with { DefenceBaseRoll = before.DefenceBaseRoll }).Payload());
    }

    [Fact]
    public void Rerolling_the_same_value_still_spends_exactly_one_orb_and_saves_the_target()
    {
        var items = Items;
        var stack = items.Add(Main, 0, 0, Orbs(2));
        var target = items.Add(Main, 2, 0, Armour(roll: 16));
        var random = new RollRandom(16);
        var changed = Apply(target.Item, random)!;
        Assert.NotNull(changed);

        var applied = ItemMoves.UseOn(items, Main, stack.Id, Main, target.Id, _ => changed)!.Value;

        Assert.Equal(1, applied.Used.Left!.Item.StackSize);
        Assert.Equal(target.Item, applied.Changed.Item);
        Assert.Equal(1, random.Draws);
        var restored = Reload().Items.Items(Main);
        Assert.Equal(1, restored.Single(i => i.Id == stack.Id).Item.StackSize);
        Assert.Equal(16, restored.Single(i => i.Id == target.Id).Item.DefenceBaseRoll);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Inventory_and_stash_combinations_persist_the_roll_and_remove_the_last_orb(bool currencyInStash, bool targetInStash)
    {
        var items = Items;
        var stash = Stash;
        var stack = currencyInStash ? PlaceInStash(Orbs(1), 0) : items.Add(Main, 0, 0, Orbs(1));
        var target = targetInStash ? PlaceInStash(Armour(roll: 7), 2) : items.Add(Main, 2, 0, Armour(roll: 7));
        var changed = Apply(target.Item, new RollRandom(31))!;

        var applied = ItemMoves.UseAcross(items, stash, new(currencyInStash ? 0 : null, Main, stack.Id),
            new(targetInStash ? 0 : null, Main, target.Id), _ => changed)!.Value;

        Assert.Null(applied.Left);
        Assert.Equal(target.Id, applied.Changed.Id);
        Assert.Equal(target.X, applied.Changed.X);
        Assert.Equal(target.Y, applied.Changed.Y);
        var (loadedItems, loadedStash) = Reload();
        var currencies = currencyInStash ? loadedStash.Inventory(0).Items : loadedItems.Items(Main);
        Assert.DoesNotContain(currencies, i => i.Id == stack.Id);
        var targets = targetInStash ? loadedStash.Inventory(0).Items : loadedItems.Items(Main);
        var restored = targets.Single(i => i.Id == target.Id);
        Assert.Equal(31, restored.Item.DefenceBaseRoll);
        Assert.Equal(changed.Payload(), restored.Item.Payload());
    }

    [Fact]
    public void Failure_saving_a_cross_stash_reroll_rolls_back_the_currency_and_defence_roll()
    {
        var items = Items;
        var stash = Stash;
        var stack = PlaceInStash(Orbs(2), 0);
        var target = items.Add(Main, 2, 0, Armour(roll: 7));
        var changed = Apply(target.Item, new RollRandom(31))!;
        Saves.FailWrite = key => key.Equals(_character, StringComparison.OrdinalIgnoreCase);
        try
        {
            Assert.ThrowsAny<IOException>(() => ItemMoves.UseAcross(items, stash, new(0, Main, stack.Id),
                new(null, Main, target.Id), _ => changed));
        }
        finally { Saves.FailWrite = null; }

        var (loadedItems, loadedStash) = Reload();
        Assert.Equal(2, loadedStash.Inventory(0).Items.Single(i => i.Id == stack.Id).Item.StackSize);
        Assert.Equal(7, loadedItems.Items(Main).Single(i => i.Id == target.Id).Item.DefenceBaseRoll);
    }

    [Fact]
    public void An_equipped_armours_rerolled_base_flows_into_the_real_pools_and_next_panel_update()
    {
        var conn = new ClientState(new(0, "SacredPanel", 128, 128, 32, 32, null, []), 0, 1)
            { Character = _character, ClassRow = 4 };
        var items = Items;
        var stack = items.Add(Main, 0, 0, Orbs(1));
        var target = items.Add(PlayerInventories.BodyArmour, 0, 0, Armour());
        conn.Pools = Program.PoolsFor(conn, 100);
        uint armourKey = Program.Stats.Key("physical_damage_reduction_rating")!.Value;
        Assert.Equal(19, Published(conn)[armourKey]);
        var changed = Apply(target.Item, new RollRandom(31))!;

        Assert.NotNull(ItemMoves.UseOn(items, Main, stack.Id, PlayerInventories.BodyArmour, target.Id, _ => changed));
        conn.Pools = Program.PoolsFor(conn, 100);

        Assert.Equal(27, conn.Pools.Armour);
        Assert.Equal(27, Published(conn)[armourKey]);
        Assert.Empty(items.Items(Main));
        Assert.Equal(31, Reload().Items.Items(PlayerInventories.BodyArmour).Single().Item.DefenceBaseRoll);
    }

    public void Dispose()
    {
        Saves.FailWrite = null;
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
        Program.InactiveItems.TryRemove(_character, out _);
    }
}
