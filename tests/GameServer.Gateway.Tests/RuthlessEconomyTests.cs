using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RuthlessEconomyTests
{
    static string Data => BenchCraftingTests.Data();
    static readonly Lazy<ItemCatalog> Catalog = new(() => new(Data, ruthless: true));
    static PlayerInventories.Shape? Shape(Item item) => Catalog.Value.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Fact]
    public void Ruthless_quest_rewards_exclude_normal_supports_movement_and_select_the_token_alternatives()
    {
        var rewards = QuestRewardCatalog.Load(Data, ruthless: true);
        Assert.Empty(rewards.Choices(2, 1)); // Mercy Mission's regular support offer.
        Assert.Empty(rewards.Choices(4, 1)); // Breaking Some Eggs' regular movement offer.
        Assert.DoesNotContain(rewards.Choices(7, 1), c => Catalog.Value.Row(c.ItemRow)!.Name == "Flame Dash");
        foreach (int offer in new[] { 24, 37, 65 })
            Assert.Equal("Uncarved Gemstone", Catalog.Value.Row(Assert.Single(rewards.Choices(offer, 1)).ItemRow)!.Name);
        Assert.All(rewards.Offers.SelectMany(o => o.Choices), c =>
            Assert.False(Catalog.Value.Gem(Catalog.Value.Row(c.ItemRow)!)?.IsSupport ?? false));
        var regular = QuestRewardCatalog.Load(Data);
        Assert.DoesNotContain(regular.Choices(24, 1), c => Catalog.Value.Row(c.ItemRow)!.Name == "Uncarved Gemstone");
    }

    [Fact]
    public void Ruthless_gear_rewards_use_their_actual_level_and_rarity()
    {
        var rewards = QuestRewardCatalog.Load(Data, ruthless: true);
        var choice = rewards.Choices(8, 1)[0];
        var item = QuestRewardCatalog.Create(choice, Catalog.Value, new ItemRolls(Catalog.Value, Data));
        Assert.NotNull(item);
        Assert.Equal(choice.Level, item.ItemLevel);
        Assert.Equal(ItemRarity.Magic, item.Rarity);
        Assert.NotEmpty(item.Mods);
        var choices = rewards.Choices(8, 1).Select(c => (c.Id,
            QuestRewardCatalog.Create(c, Catalog.Value, new ItemRolls(Catalog.Value, Data))!));
        var placed = QuestRewardCatalog.Arrange(choices, Catalog.Value);
        Assert.Equal(rewards.Choices(8, 1).Count, placed.Count);
        Assert.Equal(0, placed[0].X);
        Assert.Equal(2, placed[1].X);
    }

    [Theory]
    [InlineData("Metadata/NPC/Act1/Nessa")]
    [InlineData("Metadata/NPC/Act6/Lilly")]
    [InlineData("Metadata/NPC/Act1/Bestel")]
    public void Ordinary_Ruthless_vendors_have_no_gems_jewellery_or_debug_map_stock(string npc)
    {
        uint id = 1;
        var tabs = NpcShops.Tabs(npc, Catalog.Value, () => id++, [], Data, ruthless: true);
        Assert.All(tabs.SelectMany(t => t.Items), p =>
        {
            var type = Catalog.Value.ByHash(p.Item.TypeHash)!;
            Assert.DoesNotContain("SkillGem", type.Components);
            Assert.DoesNotContain(type.Class, new[] { "Ring", "Amulet", "Belt", "Map" });
            Assert.Single(p.Item.Price);
        });
    }

    [Theory]
    [InlineData("Metadata/NPC/Act1/Nessa")]
    [InlineData("Metadata/NPC/Act3/Siosa")]
    public void Full_development_stock_cannot_bypass_Ruthless_vendor_unlocks_or_token_prices(string npc)
    {
        var catalog = Catalog.Value;
        var vendor = new GemVendorStock(catalog, QuestRewardCatalog.Load(Data, ruthless: true),
            NpcCatalog.Load(Data), new SkillCatalog(Data, ruthless: true), Data);
        var allowed = vendor.Stock(npc, 1, new HashSet<string> { GemVendorStock.SiosaUnlock }, ruthless: true);
        List<NpcShops.ShopTab> Shop(bool full)
        {
            uint id = 1;
            return NpcShops.Tabs(npc, catalog, () => id++, [], Data, allowed, vendor.Price,
                ruthless: true, fullGemStock: full);
        }
        var ordinary = Shop(false);
        var requestedFull = Shop(true);
        Assert.Equal(ordinary.Select(t => t.Name), requestedFull.Select(t => t.Name));
        Assert.Equal(ordinary.SelectMany(t => t.Items).Select(i => (i.Item.TypeHash, i.Item.GemVariant, Assert.Single(i.Item.Price))),
            requestedFull.SelectMany(t => t.Items).Select(i => (i.Item.TypeHash, i.Item.GemVariant, Assert.Single(i.Item.Price))));
        Assert.DoesNotContain(requestedFull, t => t.Name == "Skills" || t.Name.StartsWith("Skills "));
        var gems = requestedFull.SelectMany(t => t.Items)
            .Where(i => i.Item.Components.Contains("SkillGem")).ToArray();
        if (npc.EndsWith("/Siosa", StringComparison.Ordinal))
        {
            Assert.NotEmpty(gems);
            Assert.All(gems, i =>
            {
                Assert.Contains(catalog.ByHash(i.Item.TypeHash)!.Row, allowed);
                Assert.False(catalog.Gem(catalog.ByHash(i.Item.TypeHash)!)!.IsSupport);
                Assert.Equal((catalog.Find("Uncarved Gemstone")!.Hash, 1u), Assert.Single(i.Item.Price));
            });
        }
        else Assert.Empty(gems);
    }

    [Fact]
    public void Ruthless_prices_and_native_shop_rows_come_from_the_clients_mode_specific_tables()
    {
        var catalog = Catalog.Value;
        var prices = new RuthlessShopPrices(catalog, Data);
        Assert.Equal((catalog.Find("Orb of Transmutation")!.Hash, 1u), prices.For(catalog.Find("Rusted Sword")!)!.Value);
        Assert.Equal((catalog.Find("Scroll of Wisdom")!.Hash, 3u), prices.For(catalog.Find("Small Life Flask")!)!.Value);
        var npc = NpcCatalog.Load(Data).Find("Metadata/NPC/Act1/Nessa")!;
        Assert.Equal(11, npc.ShopRow);
        Assert.Equal(49, npc.RuthlessShopRow);
    }

    [Fact]
    public void Siosa_exchanges_only_eligible_active_gems_for_one_uncarved_token()
    {
        const string siosa = "Metadata/NPC/Act3/Siosa";
        var catalog = Catalog.Value;
        var stock = new GemVendorStock(catalog, QuestRewardCatalog.Load(Data, ruthless: true),
            NpcCatalog.Load(Data), new SkillCatalog(Data, ruthless: true), Data);
        Assert.Empty(stock.Stock(siosa, 1, new HashSet<string>(), ruthless: true));
        var allowed = stock.Stock(siosa, 1, new HashSet<string> { GemVendorStock.SiosaUnlock }, ruthless: true);
        Assert.NotEmpty(allowed);
        Assert.DoesNotContain(catalog.Find("Frostblink")!.Row, allowed);
        Assert.All(allowed, row => Assert.False(catalog.Gem(catalog.Row(row)!)!.IsSupport));
        uint id = 100;
        var tabs = NpcShops.Tabs(siosa, catalog, () => id++, [], Data, allowed, ruthless: true);
        var gem = tabs.SelectMany(t => t.Items).First();
        var token = catalog.Find("Uncarved Gemstone")!;
        Assert.All(tabs.SelectMany(t => t.Items), p => Assert.Equal((token.Hash, 1u), Assert.Single(p.Item.Price)));
        Assert.Equal((token.Hash, 1u), Assert.Single(gem.Item.Price));
        TestSaves.Ensure();
        string character = "RuthlessToken" + Guid.NewGuid().ToString("N");
        var inventory = PlayerInventories.For(character, catalog, Data);
        inventory.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(token));
        Assert.NotNull(ItemMoves.BuyToInventory(inventory, null, gem.Item with { Price = [] }, (token.Hash, 1), Shape, out _));
        Assert.DoesNotContain(inventory.Items(PlayerInventories.MainInventory), p => p.Item.TypeHash == token.Hash);
        Assert.Contains(inventory.Items(PlayerInventories.MainInventory), p => p.Item.TypeHash == gem.Item.TypeHash);
    }

    [Fact]
    public void Ruthless_selling_uses_basic_scaled_shards_instead_of_regular_deterministic_recipes()
    {
        var catalog = Catalog.Value;
        var sword = ItemCatalog.Create(catalog.Find("Rusted Sword")!,
            [new(SocketColour.Red), new(SocketColour.Green), new(SocketColour.Blue)], [3]);
        var offered = Enumerable.Range(1, 5).Select(n => new InventoryItem((uint)n, 0, 0, sword)).ToArray();
        var recipes = new VendorRecipes(catalog, Data);
        Assert.Contains(recipes.Evaluate(offered).Rewards, r => r.TypeHash == catalog.Find("Chromatic Orb")!.Hash);
        var ruthless = recipes.Evaluate(offered, ruthlessRewardMultiplier: 0.20);
        Assert.Equal("Ruthless basic sale", Assert.Single(ruthless.Recipes));
        var reward = Assert.Single(ruthless.Rewards);
        Assert.Equal(catalog.Find("Scroll Fragment")!.Hash, reward.TypeHash);
        Assert.Equal(1, reward.StackSize);
    }

    [Fact]
    public void Ruthless_currency_sales_use_the_actual_currency_class_and_refuse_unknown_conversions()
    {
        var catalog = Catalog.Value;
        var portal = catalog.Find("Portal Scroll")!;
        Assert.Equal("StackableCurrency", portal.Class);
        Assert.DoesNotContain("Currency", portal.Components);
        var recipes = new VendorRecipes(catalog, Data);
        var reward = Assert.Single(recipes.Evaluate(
            [new InventoryItem(1, 0, 0, ItemCatalog.Create(portal) with { StackSize = 5 })],
            ruthlessRewardMultiplier: 0.20).Rewards);
        Assert.Equal(catalog.Find("Scroll of Wisdom")!.Hash, reward.TypeHash);
        Assert.Equal(1, reward.StackSize);
        Assert.Throws<NotSupportedException>(() => recipes.Evaluate(
            [new InventoryItem(2, 0, 0, ItemCatalog.Create(catalog.Find("Chaos Orb")!))],
            ruthlessRewardMultiplier: 0.20));
    }

    [Theory]
    [InlineData("Metadata/NPC/Act1/Nessa", true)]
    [InlineData("Metadata/NPC/Missions/Hideout/Helena", true)]
    [InlineData("Metadata/NPC/League/Delve/DelveMinerHideout", true)]
    [InlineData("Metadata/NPC/Act3/Siosa", false)]
    [InlineData("Metadata/NPC/Act1/Tarkleigh", false)]
    public void Only_the_appropriate_npcs_offer_Ruthless_refills(string npc, bool expected) =>
        Assert.Equal(expected, Program.CanRefillRuthless(npc));
}
