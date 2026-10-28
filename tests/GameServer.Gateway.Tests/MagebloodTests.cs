using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Mageblood's item markers and which flasks it applies (Program.Mageblood): the leftmost magic utility flasks only.
public class MagebloodTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/Flasks.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("Flasks.json");
    }

    static readonly Lazy<(ItemCatalog Items, Item Belt)> Setup = new(() =>
    {
        string data = Data();
        var items = new ItemCatalog(data);
        return (items, new UniqueCatalog(items, data).Create("Mageblood", new Random(1))!);
    });

    [Fact]
    public void Mageblood_is_recognised_by_its_unique_word_and_mod_flask_count()
    {
        var (items, belt) = Setup.Value;

        Assert.True(Program.IsMageblood(belt));
        Assert.False(Program.IsMageblood(ItemCatalog.Create(items.Find("Heavy Belt")!)));
        Assert.InRange(Program.MagebloodFlaskCount(belt), 2, 4);
    }

    [Fact]
    public void Only_the_leftmost_magic_utility_flasks_are_applied()
    {
        var (items, _) = Setup.Value;
        InventoryItem At(byte x, string type, ItemRarity rarity) =>
            new((uint)(x + 1), x, 0, ItemCatalog.Create(items.Find(type)!) with { Rarity = rarity });
        var bar = new[]
        {
            At(0, "Small Life Flask", ItemRarity.Normal),
            At(1, "Quicksilver Flask", ItemRarity.Magic),
            At(2, "Granite Flask", ItemRarity.Normal),
            At(3, "Quartz Flask", ItemRarity.Magic),
            At(4, "Ruby Flask", ItemRarity.Magic),
        };

        Assert.True(Program.IsMagicUtilityFlask(bar[1].Item));
        Assert.False(Program.IsMagicUtilityFlask(bar[0].Item));   // life flask
        Assert.False(Program.IsMagicUtilityFlask(bar[2].Item));   // normal rarity
        var applied = Program.MagebloodFlasks(bar, 2);
        Assert.Equal([(uint)2, (uint)4], applied.Select(i => i.Id));
    }

    [Fact]
    public void A_running_effect_and_magebloods_permanent_one_are_deduplicated()
    {
        // The same effect (Granite's armour buff) from a running flask and Mageblood's permanent one must count once;
        // different effects (Ruby's fire resistance) still stack.
        var granite = new UtilityFlasks.Source("buff 1", "base_physical_damage_reduction_rating", 1500);
        var ruby = new UtilityFlasks.Source("buff 2", "base_fire_damage_resistance_%", 40);

        var combined = Program.CombineUtilitySources([granite], [granite, ruby]);

        Assert.Equal(2, combined.Count);
        Assert.Equal(1500, combined.Single(s => s.Stat == "base_physical_damage_reduction_rating").Value);
        Assert.Equal(40, combined.Single(s => s.Stat == "base_fire_damage_resistance_%").Value);
        // Without the belt only the running source is left.
        Assert.Equal([("base_physical_damage_reduction_rating", 1500)], Program.CombineUtilitySources([granite], []));
    }

    [Fact]
    public void A_running_flask_before_the_belt_is_not_doubled_and_stays_after_it_is_removed()
    {
        TestSaves.Ensure();
        string name = "Mage" + Guid.NewGuid().ToString("N")[..12];
        var items = PlayerInventories.For(name, Program.Items);
        try
        {
            var graniteType = Program.Items.Find("Granite Flask")!;
            var rubyType = Program.Items.Find("Ruby Flask")!;
            var granite = items.Add(PlayerInventories.Flask, 0, 0,
                ItemCatalog.Create(graniteType) with { Rarity = ItemRarity.Magic });
            var ruby = items.Add(PlayerInventories.Flask, 1, 0,
                ItemCatalog.Create(rubyType) with { Rarity = ItemRarity.Magic });
            var conn = new ClientState(new AreaLayout(0, "test", 100, 100, 30, 30, null, []), 0, 1) { Character = name };
            int Armour() => Program.UtilityStats(conn, _ => 0)
                .Where(s => s.Stat == "base_physical_damage_reduction_rating").Sum(s => s.Value);
            int FireResistance() => Program.UtilityStats(conn, _ => 0)
                .Where(s => s.Stat == "base_fire_damage_resistance_%").Sum(s => s.Value);
            void Use(InventoryItem flask, ItemCatalog.BaseType type)
            {
                var utility = Program.Items.UtilityFlaskOf(type)!;
                var (sources, _) = UtilityFlasks.For(utility, flask.Item, ItemModStats.Local(flask.Item, Program.Items.Mods), _ => 0);
                conn.UtilityFlaskEffects.Start(new UtilityFlasks.Active(flask.Id, flask.X, type.Name, utility, 1, null, sources, 0, 1000));
            }

            // The flasks run before the belt is equipped.
            Use(granite, graniteType);
            Use(ruby, rubyType);
            int armour = Armour(), fire = FireResistance();

            // Equipping the belt must not add the same effects a second time.
            var belt = items.Add(PlayerInventories.Belt, 0, 0, Program.Uniques.Create("Mageblood", new Random(1))!);
            Assert.Equal(armour, Armour());
            Assert.Equal(fire, FireResistance());

            // Removing it leaves the running effects until their own duration ends.
            items.PickUp(PlayerInventories.Belt, belt.Id);
            Assert.Equal(armour, Armour());
            Assert.Equal(fire, FireResistance());
        }
        finally { PlayerInventories.Forget(name); }
    }

    [Theory]
    [InlineData(false, 20, 60)]
    [InlineData(true, 0, 40)]
    public void Permanent_silver_and_quicksilver_effects_use_the_characters_native_mode(
        bool ruthless, int silverMovement, int combinedMovement)
    {
        TestSaves.Ensure();
        string name = "MageMode" + Guid.NewGuid().ToString("N")[..12];
        var conn = new ClientState(new AreaLayout(0, "test", 100, 100, 30, 30, null, []), 0, 1)
            { Character = name, Ruthless = ruthless };
        var catalog = Program.ItemsFor(conn);
        var inventory = PlayerInventories.For(name, catalog);
        try
        {
            inventory.Add(PlayerInventories.Belt, 0, 0, new UniqueCatalog(catalog, Data()).Create("Mageblood", new Random(1))!);
            var silver = inventory.Add(PlayerInventories.Flask, 0, 0,
                ItemCatalog.Create(catalog.Find("Silver Flask")!) with { Rarity = ItemRarity.Magic });
            int Value(string stat) => Program.UtilityStats(conn, _ => 0).Where(s => s.Stat == stat).Sum(s => s.Value);

            // Ruthless Onslaught retains attack/cast speed but has no movement bonus, including with Mageblood.
            Assert.Equal(20, Value("attack_speed_+%"));
            Assert.Equal(20, Value("base_cast_speed_+%"));
            Assert.Equal(silverMovement, Value("base_movement_velocity_+%"));
            Assert.True(Program.MagebloodBlocksFlaskUse(conn, silver.Item));

            // Quicksilver is a separate permanent effect and retains its native 40% in both modes.
            inventory.Add(PlayerInventories.Flask, 1, 0,
                ItemCatalog.Create(catalog.Find("Quicksilver Flask")!) with { Rarity = ItemRarity.Magic });
            Assert.Equal(combinedMovement, Value("base_movement_velocity_+%"));
            Assert.Empty(conn.UtilityFlaskEffects.Sources());
        }
        finally { PlayerInventories.Forget(name); }
    }
}
