using System.Text;
using GameServer.Data.Ggpk;

namespace GameServer.Data.Tests;

/// <summary>
/// The GGPK's gateway list: every player's copy must carry identical bytes, because the patch welcome compares the
/// GGPK's root hash (Agents/docs/dev/lan-play.md).
/// </summary>
public class GatewayListTests
{
    const string List =
        "Europe;Amsterdam (EU);127.0.0.1:20493;eu.speedtest.pathofexile.com;false\r\n" +
        "America;Texas (US);127.0.0.1:20493;us.speedtest.pathofexile.com;false\r\n";

    [Fact]
    public void Replacing_the_login_keeps_every_other_field_and_the_line_breaks()
    {
        string patched = GatewayList.WithLogin(List, "me.frp.example.com:26000");

        string[] lines = patched.Split("\r\n");
        Assert.Equal(3, lines.Length);                         // two gateways and the trailing empty line
        Assert.Equal("Europe;Amsterdam (EU);me.frp.example.com:26000;eu.speedtest.pathofexile.com;false", lines[0]);
        Assert.Equal("America;Texas (US);me.frp.example.com:26000;us.speedtest.pathofexile.com;false", lines[1]);
        Assert.Equal("", lines[2]);
    }

    [Fact]
    public void Encoding_and_decoding_round_trip_the_utf16_list_with_its_bom()
    {
        byte[] bytes = GatewayList.Encode(List);

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Equal(List, GatewayList.Decode(bytes));
        Assert.Equal(List, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }
}