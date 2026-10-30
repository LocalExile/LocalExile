using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class HideoutTests
{
    [Fact]
    public void Hideouts_update_is_player_component_flag_0x10_with_two_keys()
    {
        // Lush Hideout: Hideouts.dat HASH16 0xff63 (21-hideouts.md §1).
        string hex = Convert.ToHexString(InstanceEntities.PlayerHideoutsUpdate(0x1000, 0xff63, 0));
        Assert.Equal("02BA" + "00001000" + "00000000" + "0000" + "0007" + "06" + "1000" + "63FF" + "0000", hex);
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}

public class VisitHideoutTests
{
    [Fact]
    public void The_mine_encampment_pin_is_framed_as_six_bytes()
    {
        // Live 2026-10-30: the World panel's Mine Encampment pin, opened from waypoint object 0x2f00.
        var messages = InstanceProtocol.SplitClientMessages(Convert.FromHexString("017D00002F00" + "017D00002F00"), out var partial, out var unknown);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(InstanceWaypoints.VisitDelveHubId, m.Item1));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }
}

public class HideoutWaypointTests
{
}

public class HideoutObjectTests
{
}

public class LeagueQuestFlagTests
{
}
