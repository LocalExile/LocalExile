using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The release script stages the hash-verified mod outside the installed mods folder.
sealed class PublishedGmCommandsFactAttribute : FactAttribute
{
    public PublishedGmCommandsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("Game_RELEASE_GM_FOLDER") is null)
            Skip = "run scripts/release/check-gm-commands.ps1 with a pinned mod release";
    }
}

public sealed class PublishedGmCommandsTests
{
    [PublishedGmCommandsFact]
    public async Task Published_mod_sends_position_and_death_screen()
    {
        using var modules = new ModHost("Modules", Console.WriteLine, modules: true)
        {
            Wanted = () => new HashSet<string>(["Game"])
        };
        modules.Start(watch: false);
        using var mods = new ModHost(Environment.GetEnvironmentVariable("Game_RELEASE_GM_FOLDER")!, Console.WriteLine)
        {
            Modules = modules
        };
        mods.Start(watch: false);
        var status = Assert.Single(mods.Mods());
        Assert.Equal("gm-commands", status.Id);
        Assert.True(status.Error is null, $"GM Commands failed to load: {status.Error}");

        using var wire = new CaptureWire();
        var player = new Player(new(1, "ReleaseCheck", 1000, 1000, 123, 87, null, []), 1, 1)
        {
            Connection = { Session = new("release-check", wire.Stream, wire.ServerCiphers) },
            LeagueModule = new(new League("release-check", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        player.Commands = Program.Commands(player);
        Assert.True(await player.Commands.RunAsync("/pos"), "GM Commands did not register /pos");
        Assert.True(await player.Commands.RunAsync("/death_screen 02"), "GM Commands did not register /death_screen");

        var packets = wire.FinishAndDecrypt();
        var position = InstanceChat.ChatLine("(123, 87) in ReleaseCheck");
        Assert.True(packets.AsSpan().StartsWith(position), "/pos did not send the expected coordinates and area in chat");
        Assert.Equal("005502", Convert.ToHexString(packets.AsSpan(position.Length)));
    }
}
