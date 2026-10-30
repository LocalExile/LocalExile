namespace GameServer.Gateway.Tests;

/// <summary>
/// The owned microtransactions (Microtransactions.cs) over made-up tables: only MtxTypes rows the client can resolve in
/// MTXTypeGameSpecific are owned (a record it cannot resolve crashed the client, 23-microtransactions.md §1), ids are
/// the row + 1, and a saved pick becomes a worn-table entry.
/// </summary>
public class MtxCatalogTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "mtx-" + Guid.NewGuid().ToString("N"));

    public MtxCatalogTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "tables"));
        File.WriteAllText(Path.Combine(_folder, "tables", "MtxTypes.json"),
            """[{"HASH32": 100}, {"HASH32": 200}, {"HASH32": 300}, {"Name": "no hash"}]""");
        File.WriteAllText(Path.Combine(_folder, "tables", "MtxTypeGameSpecific.json"),
            """[{"HASH32": 300}, {"HASH32": 100}]""");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Only_rows_the_client_resolves_are_owned()
    {
        var catalog = new MtxCatalog(_folder);
        Assert.Equal([(100u, 1u), (300u, 3u)], catalog.Owned.Select(r => (r.MtxTypeHash, r.Id)));
        Assert.Null(catalog.Find(2));
        Assert.Equal(300u, catalog.Find(3)?.MtxTypeHash);
        Assert.Equal(1u, catalog.ByHash(100)?.Id);
    }

}
