namespace GameServer.Gateway.Tests;

// What Stibnite, Sulphur and Basalt Flasks make on use (FlaskGrounds): smoke cloud (Blind), consecrated ground, taunt.
public class FlaskGroundsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));

    static List<(FlaskGrounds.Kind, int)> OnUse(string flask)
    {
        var item = ItemCatalog.Create(Catalog.Value.Find(flask)!);
        return [.. FlaskGrounds.OnUse(ItemModStats.Local(item, Catalog.Value.Mods))];
    }

    [Fact]
    public void The_flasks_implicits_make_their_grounds_with_the_tables_radii()
    {
        // BaseItemTypes implicits: UtilityFlaskSmokeCloud 16, UtilityFlaskConsecrate 18, UtilityFlaskTaunt_ 30.
        Assert.Equal([(FlaskGrounds.Kind.SmokeCloud, 16)], OnUse("Stibnite Flask"));
        Assert.Equal([(FlaskGrounds.Kind.ConsecratedGround, 18)], OnUse("Sulphur Flask"));
        Assert.Equal([(FlaskGrounds.Kind.Taunt, 30)], OnUse("Basalt Flask"));
        Assert.Empty(OnUse("Quicksilver Flask"));
        // Aquamarine's chilled ground is not made (monsters cannot be chilled in step with the client yet).
        Assert.Empty(OnUse("Aquamarine Flask"));
    }


    [Fact]
    public void Taunted_recipients_keep_independent_base_budgets_when_curse_expiry_changes()
    {
        var state = new FlaskGrounds.State();
        var taunt = new FlaskGrounds.Ground(FlaskGrounds.Kind.Taunt, 0, 0, 30, Ends: 3);
        taunt.TauntClocks[1] = new(3, 0, .75);
        taunt.TauntClocks[2] = new(3, 0, 1);
        state.Add(taunt);
        Assert.Empty(state.Expire(3)); // The cursed recipient still has one wall second.
        taunt.TauntClocks[1].ChangeRate(2, 1);
        Assert.Equal(1.5, taunt.TauntClocks[1].Remaining, 8);
        Assert.Equal(3.5, taunt.ExpiryDeadline, 8);
        Assert.Empty(state.Expire(3.49));
        Assert.Same(taunt, Assert.Single(state.Expire(3.5)));
    }
}
