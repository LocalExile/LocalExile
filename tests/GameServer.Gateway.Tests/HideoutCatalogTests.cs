using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class HideoutCatalogTests
{
    [Fact]
    public void All_template_rows_are_owned_including_high_mtx_rows_without_duplicates()
    {
        var campaign = new HideoutCatalog.Hideout(0, 1, "Campaign", 1, "Campaign", []);
        var mtx = campaign with { Row = 102, AreaId = "MTX" };
        Assert.Equal(new byte[] { 0, 102 }, HideoutCatalog.SelectableRows([mtx, campaign, mtx]));
        Assert.Empty(HideoutCatalog.SelectableRows([]));
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}

public class LeagueQuestFlagBitTests
{
}
