using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.M_commands;

namespace GameServer.Gateway.Tests;

public sealed class CharacterPlaytimeTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();
    static Player Conn(string name) => new(new(1, "played", 1000, 1000, 100, 100, null, []), 1, 1)
    {
        Character = name, PlayerSent = true,
        LeagueModule = new(new GameServer.Core.Accounts.League("played-test", "Standard", false, false), new HashSet<string>(["Game", "Commands"]))
    };

    [Fact]
    public void Online_time_survives_checkpoints_area_changes_and_module_reload_without_offline_credit()
    {
        TestEnvironment.OpenOwnSaves("played-time");
        CharacterSaves.Write("Played", new([], Experience: 123));
        double now = 100;
        var conn = Conn("Played");
        using var first = new CharacterPlaytime(() => now);
        first.Begin(conn); first.Begin(conn);
        now += 30.25; first.Step(conn);
        Assert.Equal(30.25, first.Seconds(conn), 5);
        now += 5.5; first.End(conn); first.End(conn);
        Assert.Equal("35.75", CharacterSaves.ModuleData("Played", CharacterPlaytime.SaveKey));
        now += 10000;
        var next = Conn("Played"); first.Begin(next);
        Assert.Equal(35.75, first.Seconds(next), 5);
        now += 4.25; first.Dispose();
        Assert.Equal("40", CharacterSaves.ModuleData("Played", CharacterPlaytime.SaveKey));
        now += 5000;
        using var replacement = new CharacterPlaytime(() => now);
        replacement.Begin(next); now += 2;
        Assert.Equal(42, replacement.Seconds(next));
        first.Begin(next); first.Step(next); first.Dispose();
        replacement.End(next);
        Assert.Equal("42", CharacterSaves.ModuleData("Played", CharacterPlaytime.SaveKey));
        Assert.Equal(123u, CharacterSaves.Experience("Played"));
        var bot = Conn("Bot"); bot.Controlled = true;
        replacement.Begin(bot); now += 60; replacement.End(bot);
        Assert.Null(CharacterSaves.ModuleData("Bot", CharacterPlaytime.SaveKey));
    }

    [Fact]
    public async Task Native_played_command_is_handled_and_unrelated_generic_commands_fall_through()
    {
        TestEnvironment.OpenOwnSaves("played-command");
        CharacterSaves.Write("PlayedCommand", new([]));
        CharacterSaves.ChangeModuleData("PlayedCommand", CharacterPlaytime.SaveKey, _ => "93784");
        var conn = Conn("PlayedCommand");
        var replies = new List<string>();
        conn.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = text => { replies.Add(text); return Task.CompletedTask; } };
        using var context = new Context();
        try
        {
            new CommandsModule().Load(context);
            Assert.True(await GameEvents.AnyPacket(conn, InstanceItems.CommandId, [0, 0x16, 2, 0, 0]));
            Assert.Equal("You have played for 1 day, 2 hours, 3 minutes and 4 seconds.", Assert.Single(replies));
            Assert.False(await GameEvents.AnyPacket(conn, InstanceItems.CommandId, [0, 0x16, 9, 0, 0]));
            var commands = conn.Commands;
            foreach (var build in GameEvents.CommandsBuilding.For(conn)) commands = build(commands, conn);
            conn.Commands = commands;
            Assert.True(await commands.RunAsync("/played"));
            Assert.Equal(2, replies.Count);
        }
        finally { GameEventOwners.Release("Commands", GameEventOwners.ModuleRank); }
    }

    sealed class Context : IModuleContext, IDisposable
    {
        readonly ModuleLifetime lifetime = new("Commands");
        public ModManifest Manifest => new("Commands", "1.0.0", ModKind.Feature, ["Game"], "commands.dll");
        public IModuleLifetime Lifetime => lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => lifetime.Stop(e => throw e);
    }
}
