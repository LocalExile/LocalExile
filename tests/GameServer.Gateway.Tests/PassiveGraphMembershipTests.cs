using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class PassiveGraphMembershipTests
{
    [Fact]
    public void An_ordinary_node_missing_from_the_client_graph_is_refused_without_dropping_the_saved_list()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, AscendancyCatalog.GraphFile), JsonSerializer.Serialize(new
            {
                revision = AscendancyCatalog.Revision,
                normal = new { nodes = new[] { new { id = 10 }, new { id = 20 } } },
                phrecia = new { nodes = new[] { new { id = 10 } } },
            }));
            var catalog = new AscendancyCatalog(folder);
            ushort[] saved = [10, 20];
            Assert.True(catalog.GraphLoaded);
            catalog.ValidateOrdinaryNodes(AscendancyFamily.Normal, saved);
            Assert.Throws<AscendancyRuleException>(() => catalog.ValidateOrdinaryNodes(AscendancyFamily.Phrecia, saved));
            Assert.Equal(new ushort[] { 10, 20 }, saved);
            catalog.ValidateOrdinaryNodes(AscendancyFamily.Phrecia, [10]);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
