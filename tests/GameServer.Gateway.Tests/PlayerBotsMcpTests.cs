using System.Text.Json;
using GameServer.Modules.PlayerBots;

namespace GameServer.Gateway.Tests;

public sealed class PlayerBotsMcpTests
{
    sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Now;
    }

    [Fact]
    public async Task Party_bots_publish_campaign_levels_and_keep_external_control_boundaries()
    {
        (int Min, int Max)[] ranges = [(1, 3), (4, 14), (11, 21), (19, 29), (26, 36),
            (33, 43), (40, 50), (48, 58), (55, 65), (59, 75)];
        var random = new Random(41);
        for (int act = 1; act <= 10; act++)
        {
            string town = $"{(act <= 5 ? 1 : 2)}_{act}_town";
            var range = ranges[act - 1];
            Assert.Equal(range, BotDirector.LevelsForTown(town));
            var picked = new HashSet<int>();
            for (int i = 0; i < 100; i++)
            {
                int level = BotDirector.LevelForTown(0, town, random);
                picked.Add(level);
                Assert.InRange(level, range.Min, range.Max);
                Assert.Equal(level, BotDirector.LevelForTown(level, town, random));
                var resident = new Bot { AccountId = Guid.Empty, CharacterId = Guid.Empty, Account = "bot", Name = "resident",
                    Class = GameServer.Core.Accounts.CharacterClass.Marauder, PeerId = 0x38000000, Level = level, Town = town };
                Assert.Equal(CharacterStats.ExperienceFor(level), BotDirector.Template(resident).Player.Experience);
            }
            Assert.True(picked.Count > 1);
        }
        using (var runtime = new PartyFollowerTests.Runtime())
        using (var wire = new PartyFollowerTests.Wire())
        {
            var instance = new InstanceState();
            var observer = runtime.Connection(instance, wire);
            var resident = new Bot { AccountId = Guid.Empty, CharacterId = Guid.Empty, Account = "bot", Name = "resident",
                Class = GameServer.Core.Accounts.CharacterClass.Marauder, PeerId = 0x38000000, Level = 59, Town = "2_10_town" };
            var before = BotDirector.Template(resident);
            instance.PublishPlayer(new(resident.PeerId, before.Type, before.Player, 100, 100, before.Player.Life));
            await Game.SyncInstancePlayers("town", wire.Stream, wire.Ciphers, observer);
            Assert.Equal(CharacterStats.ExperienceFor(59), observer.SeenPlayers[resident.PeerId].Template.Experience);
            resident.Level = 60;
            var after = BotDirector.Template(resident);
            instance.PublishPlayer(new(resident.PeerId, after.Type, after.Player, 100, 100, after.Player.Life));
            string update = Convert.ToHexString(GameServer.Core.Protocol.Instance.InstanceEntities.PlayerExperienceUpdate(
                resident.PeerId, CharacterStats.ExperienceFor(60)));
            int Count() => Program.HistoryFor(wire.Ciphers).Lines().Count(line => line.Contains(update, StringComparison.Ordinal));
            await Game.SyncInstancePlayers("town", wire.Stream, wire.Ciphers, observer);
            Assert.Equal(1, Count());
            await Game.SyncInstancePlayers("town", wire.Stream, wire.Ciphers, observer);
            Assert.Equal(1, Count());
        }
        int calls = 0;
        var protocol = new BotMcpProtocol((name, args) => { calls++; return new { name }; }, () => new { port = 6194 });
        JsonElement Reply(string value, string? header = null)
        {
            using var json = JsonDocument.Parse(value);
            return JsonSerializer.SerializeToElement(protocol.Reply(json.RootElement, header));
        }
        var initial = Reply("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}} """);
        Assert.Equal("2025-11-25", initial.GetProperty("result").GetProperty("protocolVersion").GetString());
        var modern = Reply("""{"jsonrpc":"2.0","id":2,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""", "2026-07-28");
        Assert.Contains("2026-07-28", modern.GetProperty("result").GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()));
        var unsupported = Reply("""{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2099-01-01"}}}""");
        Assert.Equal(-32022, unsupported.GetProperty("error").GetProperty("code").GetInt32());
        var tools = Reply("""{"jsonrpc":"2.0","id":4,"method":"tools/list"}""").GetProperty("result").GetProperty("tools");
        Assert.Equal(7, tools.GetArrayLength());
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(tools.EnumerateArray(), t => Assert.False(t.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean()));
        var rules = Reply("""{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"playerbots://rules"}}""");
        Assert.Contains("server enforces recovery", rules.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
        var valid = Reply("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_bots","arguments":{}}}""");
        Assert.False(valid.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(1, calls);
        Assert.Equal(JsonValueKind.Null, Reply("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"list_bots","arguments":{}}}""").ValueKind);
        foreach (string input in new[]
        {
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_bots","arguments":{"shell":"anything"}}}""",
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"claim_bot","arguments":{"botId":"not-a-guid","controller":"test"}}}""",
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"command_bot","arguments":{"seconds":9}}}""",
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"observe_bot","arguments":{"botId":7}}}""",
        }) Assert.True(Reply(input).TryGetProperty("error", out _));
        Assert.Equal(1, calls);
        var clock = new Clock();
        var lease = new BotControlLease("test", 30, clock);
        Assert.True(lease.Owns(lease.Id));
        Assert.False(lease.Owns("other"));
        int applied = 0;
        object Apply() { applied++; return new { accepted = true }; }
        object first = lease.Accept(1, "command", Apply);
        Assert.Same(first, lease.Accept(1, "command", Apply));
        Assert.Throws<InvalidOperationException>(() => lease.Accept(1, "different", Apply));
        Assert.Throws<InvalidOperationException>(() => lease.Accept(0, "old", Apply));
        Assert.Equal(1, applied);
        clock.Now = 20; lease.Renew(); clock.Now = 49;
        Assert.True(lease.Active);
        clock.Now = 50;
        Assert.False(lease.Active);
        Assert.False(lease.Owns(lease.Id));

        var bot = new ClientState(new(1, "mcp-bounds", 300, 300, 100, 100, null, []), 1, 1)
            { Controlled = true, PlayerLife = 100 };
        var leader = new ClientState(bot.Layout, 1, 1) { PlayerLife = 100 };
        bot.Walk.Teleport(100, 100, 0); leader.Walk.Teleport(100, 100, 0);
        Assert.True(Game.ControlledMoveFits(bot, leader, 125, 100, 0));
        Assert.False(Game.ControlledMoveFits(bot, leader, 161, 100, 0));
        Assert.False(Game.ControlledMoveFits(bot, leader, -1, 100, 0));
        double now = bot.Clock.Elapsed.TotalSeconds;
        Game.QueueControlledIntent(bot, new("first", "hold", now + 2));
        Assert.Equal("first", Game.CurrentControlledIntent(bot, now)!.Id);
        Game.QueueControlledIntent(bot, new("second", "hold", now + 3));
        Assert.Equal("second", Game.CurrentControlledIntent(bot, now)!.Id);
        Assert.Null(Game.CurrentControlledIntent(bot, now + 4));
        Assert.Equal("expired", Game.ControlledIntentReceipt(bot)!.State);
        Assert.Throws<ArgumentException>(() => Game.QueueControlledIntent(bot, new("too-long", "hold", now + 100)));
        Assert.Throws<ArgumentException>(() => Game.QueueControlledIntent(bot, new("shell", "shell", now + 2)));
        bot.Controlled = false;
        Assert.Throws<InvalidOperationException>(() => Game.QueueControlledIntent(bot, new("human", "hold", now + 2)));
    }
}
