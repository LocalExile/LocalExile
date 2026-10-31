using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The Azurite Mine Encampment: the Delve data (sulphite) and the Crawler (live 2026-10-29).
public class DelveHubTests
{
    [Theory]
    [InlineData(EffectObject.TypePath)]
    [InlineData(DelveCrawlerAttachmentObject.LightType)]
    public void Wheel_and_light_types_are_present_in_the_exported_component_catalog(string type)
    {
        using var catalog = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("data/object-types.json"));
        Assert.Contains(catalog.RootElement.GetProperty("types").EnumerateArray(),
            row => row.GetProperty("type").GetString() == type);
    }

    [Theory]
    [InlineData("Delve_Hub", "foothills_DelveEntrance01_area_transition.ao", 64840)]
    [InlineData("Delve_Main", "delvemine_areatransition_v01_01_area_transition.ao", 35739)]
    [InlineData("Delve_Main", "foothills_DelveEntrance01_area_transition.ao", 0)]
    [InlineData("Delve_Hub", "foothills_descent_elevator_v01_01_area_transition.ao", 0)]
    [InlineData("1_4_1", "foothills_DelveEntrance01_area_transition.ao", 0)]
    public void The_mine_entrance_leads_between_hub_and_mine_without_pairing_the_unused_elevator(
        string area, string model, int expected)
    {
        Assert.Equal(expected != 0, AreaTransitionCatalog.TryDelveEntrance(area, model, out var destination));
        Assert.Equal((ushort)expected, destination);
    }

    [Fact]
    public async Task Repeated_crawler_spawn_keeps_the_existing_object_and_blockers()
    {
        var state = new ClientState(new(StashCatalog.DelveHub, "Delve_Hub", 100, 100, 10, 10, null, []), 0, 1)
            { DelveCrawlerObject = 123 };
        state.DelveCrawlerBlockers.Add(456);
        uint next = state.NextObjectId;
        var method = typeof(Program).GetMethod("SpawnDelveCrawler",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)method.Invoke(null, ["test", null, null, state])!;
        Assert.Equal(123u, state.DelveCrawlerObject);
        Assert.Equal(new uint[] { 456 }, state.DelveCrawlerBlockers);
        Assert.Equal(next, state.NextObjectId);
    }

    [Fact]
    public void Delve_data_puts_the_sulphite_at_bytes_16_to_18_of_the_20_byte_part()
    {
        // Live: this message made the Subterranean Chart show 300 sulphite (instance +0xd892).
        Assert.Equal(Convert.FromHexString("0228" + "00" + "00000000000000000000000000000000" + "2C010000" + "0000000000000000000000000000"),
            InstanceDelve.DelveData(300));
    }

    [Fact]
    public void The_crawler_blob_carries_its_variation_after_the_light()
    {
        byte[] blob = new DelveRobotObject(416, 324, 0).Blob();
        // DelveLight starts after flags (2), Positioned (18), Animated (1 + 4 + model), Transitionable (1), Stats, Buffs and
        // Life; live, the client read it at byte 197 of this blob with 18 bytes left before the variation was added.
        int light = blob.Length - (4 + 4 + 1 + 1 + 4) - 1 - 6 - 1;
        Assert.Equal(new byte[14], blob[light..(light + 14)]);   // u32, u32 n, u8, u8, u32 variation 0 (Default)
        Assert.Equal([0, 2, 0, 0, 0x02, 0, 0x02, 0], blob[^8..]); // MinimapIcon, StateMachine 2 states kept as sent, tasks
        Assert.Equal(DelveRobotObject.Model.Length * 2, BitConverter.ToInt32(blob, 2 + 18 + 1) * 2);
    }

    [Fact]
    public void Sulphite_is_saved_with_the_character()
    {
        TestSaves.Ensure();
        string name = "Sulphite-" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(name, new([], Experience: 5));
        CharacterSaves.SetSulphite(name, 300);
        var save = CharacterSaves.Load(name)!;
        Assert.Equal((ushort)300, save.Sulphite);
        Assert.Equal(5u, save.Experience);
    }
}
