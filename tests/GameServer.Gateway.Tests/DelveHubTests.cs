using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The Azurite Mine Encampment: the Delve data (sulphite) and the Crawler (live 2026-10-29).
public class DelveHubTests
{
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
