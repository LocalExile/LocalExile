namespace GameServer.Gateway.Tests;

// `GameServer.Gateway commands` lists the chat commands without a connection, for the launcher's console.
public class ChatCommandCatalogTests
{
    [Fact]
    public void Every_command_is_listed_once_with_a_usage_that_starts_with_its_name()
    {
        var usages = Program.CommandUsages().ToList();

        Assert.True(usages.Count > 50);
        Assert.Equal(usages.Count, usages.Select(u => u.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(usages, u => Assert.StartsWith($"/{u.Name}", u.Usage, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(usages, u => u.Name == "commands");
        Assert.Equal(usages.Select(u => u.Name).Order(StringComparer.OrdinalIgnoreCase), usages.Select(u => u.Name));
    }

    [Fact]
    public void Every_argument_kind_a_command_names_has_values_and_goto_takes_areas()
    {
        var usages = Program.CommandUsages().ToList();
        Assert.Equal(["area"], usages.Single(u => u.Name == "goto").Arguments);
        Assert.Equal(["monster"], usages.Single(u => u.Name == "spawn_mob").Arguments);
        Assert.All(usages.SelectMany(u => u.Arguments).Distinct(), kind => Assert.NotNull(Program.CompletionValues(kind)));
        Assert.Null(Program.CompletionValues("nothing"));
    }

    [Fact]
    public void Area_values_are_what_goto_finds_with_how_the_gateway_lays_each_out()
    {
        var areas = Program.CompletionValues("area")!.ToList();
        Assert.DoesNotContain(areas, a => a.Value == "NULL");
        var town = areas.Single(a => a.Value == "1_1_town");
        Assert.StartsWith("Lioneye's Watch · ", town.Detail);
        Assert.All(areas, a => Assert.NotNull(AreaTransitionCatalog.HashOf(a.Value)));
    }
}
