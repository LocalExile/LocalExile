using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Local requirement modifiers on uniques: percentages, reductions, all-attribute ones, and one row counted once.</summary>
public class UniqueRequirementTests
{
    [Fact]
    public void Local_requirement_modifiers_change_the_attribute_requirements()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items);

        (Item Item, ItemCatalog.BaseType Type) Create(string name)
        {
            var item = Assert.IsType<Item>(uniques.Create(name, new Random(17)));
            return (item, Assert.IsType<ItemCatalog.BaseType>(items.ByHash(item.TypeHash)));
        }
        int Required(Item item, ItemCatalog.BaseType type, int which) => which switch
        {
            0 => ItemModStats.AttributeRequirements(items, item, type).Str,
            1 => ItemModStats.AttributeRequirements(items, item, type).Dex,
            _ => ItemModStats.AttributeRequirements(items, item, type).Int,
        };

        // Agnerod East: 40% increased Strength Requirement.
        var (agnerod, agnerodType) = Create("Agnerod East");
        int baseStr = items.Requirements(agnerodType).Str;
        Assert.True(baseStr > 0);
        Assert.Equal((int)Math.Floor(baseStr * 1.4), Required(agnerod, agnerodType, 0));

        // Chober Chaber: 20% reduced Strength Requirement.
        var (chober, choberType) = Create("Chober Chaber");
        int choberBase = items.Requirements(choberType).Str;
        Assert.True(choberBase > 0);
        Assert.True(Required(chober, choberType, 0) < choberBase);

        // Queen's Escape: the 25% row is both the base implicit and an explicit mod; it counts once, not twice.
        var (queen, queenType) = Create("Queen's Escape");
        int queenBase = items.Requirements(queenType).Str;
        Assert.True(queenBase > 0);
        Assert.Equal((int)Math.Floor(queenBase * 1.25), Required(queen, queenType, 0));

        // Hand of the Lords: 400% increased attribute requirements applies to every attribute.
        var (lords, lordsType) = Create("Hand of the Lords");
        var lordsBase = items.Requirements(lordsType);
        Assert.True(lordsBase.Dex > 0);
        Assert.Equal(lordsBase.Dex * 5, Required(lords, lordsType, 1));
    }
}
