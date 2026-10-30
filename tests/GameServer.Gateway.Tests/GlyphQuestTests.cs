using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class GlyphQuestTests : IDisposable
{
    const uint Glyph1 = 3228085585, Glyph2 = 1721197556, Glyph3 = 278505504, Other = 99;
    readonly string folder = TestSaves.Catalog(new TestSaves.Type(Glyph1, GlyphQuest.Glyphs[0]), new TestSaves.Type(Glyph2, GlyphQuest.Glyphs[1]),
        new TestSaves.Type(Glyph3, GlyphQuest.Glyphs[2], Height: 2), new TestSaves.Type(Other, "Metadata/Items/Other"));
    readonly string character = "Mud" + Guid.NewGuid().ToString("N")[..12];
    public GlyphQuestTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; Directory.Delete(folder, true); }

    PlayerInventories Inventory() => PlayerInventories.For(character, new ItemCatalog(folder));
    static int? GlyphOf(Item i) => i.TypeHash switch { Glyph1 => 0, Glyph2 => 1, Glyph3 => 2, _ => null };
    static Item Make(uint hash) => new(hash, ["Base"], 1);
    HashSet<string> Flags() => [.. CharacterSaves.Load(character)?.QuestFlags ?? []];

    // The Mud Flats seed 1 as the recorder captured it (data/walkability/1_1_3_seed1.objects.json).
    void WriteCapture(string area = "1_1_3", int seed = 1) => File.WriteAllText(Path.Combine(folder, $"{area}_seed{seed}.objects.json"), JsonSerializer.Serialize(new
    {
        area, seed,
        objects = new object[]
        {
            new { objectType = GlyphQuest.Wall, model = "Metadata/Terrain/Beach/Transitions/BeachToWatercave_v2/Beach_to_watercave_v2_stone_switch.ao",
                x = 829, y = 375, rotation = 4.71238911f, z = -165.61325f, code = 3 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 793, y = 563, rotation = 0f, z = -57.5458f, code = 0 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 425, y = 609, rotation = 4.712389f, z = -57.5458f, code = 3 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 678, y = 770, rotation = 1.570796f, z = -57.5458f, code = 1 },
            new { objectType = "Metadata/Chests/Other", model = "Metadata/Other.ao", x = 1, y = 1, rotation = 0f, z = 0f, code = 0 },
        },
    }));

    [Fact]
    public void CapturedNestsGetOneGlyphEachTheSameEveryTime()
    {
        WriteCapture();
        var layout = GlyphQuest.Load("1_1_3", 1, folder)!;
        Assert.Equal(3, layout.Nests.Count);
        Assert.Equal(new[] { 0, 1, 2 }, layout.Nests.Select(n => n.Glyph).Order());
        Assert.All(layout.Nests, n => Assert.Equal(GlyphQuest.NestTypes[n.Glyph], n.Type));
        Assert.Equal(layout.Nests.Select(n => (n.At.X, n.At.Y, n.Glyph)), GlyphQuest.Load("1_1_3", 1, folder)!.Nests.Select(n => (n.At.X, n.At.Y, n.Glyph)));
        Assert.Equal(-57.5458f, layout.Nests[0].At.Z!.Value, 3);
        Assert.Equal((829, 375, 3), (layout.Wall!.X, layout.Wall.Y, layout.Wall.Code));
    }

}
