using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class InstancePauseLifecycleTests
{
    // Issue #991: the Escape menu's pause stops the instance's shared clock, and "Exit to character selection" from that
    // menu sends C→S 0x153 without a 0x1d4 0 first. The pause ends with the connection that asked for it, and an arrival
    // never finds the instance frozen (every walk of a later visitor started from one cell at one game time).
    [Fact]
    public async Task An_escape_menu_pause_ends_when_its_connection_closes_or_another_player_arrives()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-pause-lifecycle-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var arriving = new Player(new(1, "pause-arrival", 300, 300, 100, 100, null, []), 1, 1)
        {
            Character = { Name = $"PauseArrival{Guid.NewGuid():N}", ClassRow = 0 },
            Instance = instance,
            LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        try
        {
            // Exit to character selection from the open menu: the connection closes with the clock still stopped.
            var leaver = runtime.PlayerOf(instance, wire, "Leaver");
            await Game.SetPaused(leaver, true);
            Assert.False(instance.World.Clock.IsRunning);
            Close(leaver);
            Assert.True(instance.World.Clock.IsRunning);
            Assert.Null(instance.World.PausedBy);

            // A pause a module took over (the Survivors reward window) is its module's to end.
            var holder = runtime.PlayerOf(instance, wire, "Holder");
            await Game.SetPaused(holder, true);
            holder.PauseHeld = true;
            Close(holder);
            Assert.False(instance.World.Clock.IsRunning);

            // Nobody holds that stopped clock any more: the next arrival finds it running.
            Game.ValidateSavedProgress(arriving, null);
            Game.PrepareCharacterForArea(arriving, 1);
            Assert.True(instance.World.Clock.IsRunning);

            // The clock pauses only for a player alone in the instance: another player coming in ends that pause, and the
            // pausing player's world is resumed too.
            var pauser = runtime.PlayerOf(instance, wire, "Pauser");
            instance.PublishPlayer(new InstancePlayer(pauser.Id, 1, null!, 100, 100, 1000, pauser));
            var sent = new List<byte[]>();
            pauser.Connection.Outbox = (bytes, _) => { lock (sent) sent.Add(bytes); return Task.CompletedTask; };
            await Game.SetPaused(pauser, true);
            Assert.False(instance.World.Clock.IsRunning);
            Game.PrepareCharacterForArea(arriving, 1);
            Assert.True(instance.World.Clock.IsRunning);
            Assert.Null(instance.World.PausedBy);
            lock (sent) Assert.Equal(InstancePause.State(false), Assert.Single(sent));

            // A module-held pause of a player still there stays.
            pauser.PauseHeld = true;
            instance.World.Clock.Stop();
            Game.PrepareCharacterForArea(arriving, 1);
            Assert.False(instance.World.Clock.IsRunning);
        }
        finally
        {
            PlayerInventories.Forget(arriving.Character.Name!);
            TestEnvironment.ReleaseSaves(path);
        }

        static void Close(Player player)
        {
            player.Closed = true;
            Program.GameLogicOrNull!.Closing(player);
        }
    }
}
