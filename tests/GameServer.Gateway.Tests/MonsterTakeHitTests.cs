namespace GameServer.Gateway.Tests;

/// <summary>
/// data/monster-takehit.json, the file `python -I scripts/re/monster_takehit.py` writes: every MonsterVarieties row's
/// TakeHit clip indexes. A shape change there would make every lookup empty and silently stop the monsters flinching.
/// </summary>
public class MonsterTakeHitTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"localexile-takehit-{Guid.NewGuid():n}");

    public MonsterTakeHitTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    void Write(string json) => File.WriteAllText(Path.Combine(_folder, "monster-takehit.json"), json);

    MonsterCatalog Catalog() => new(_folder);

    [Fact]
    public void A_pool_gives_every_index_in_file_order()
    {
        Write("""
            {"Metadata/Monsters/A": {"index": 7, "indexes": [7, 3, 9], "clips": ["flinch_01", "flinch_02", "flinch_03"]}}
            """);
        Assert.Equal(new[] { 7, 3, 9 }, Catalog().TakeHit("Metadata/Monsters/A").Select(a => a.Index));
    }

}
