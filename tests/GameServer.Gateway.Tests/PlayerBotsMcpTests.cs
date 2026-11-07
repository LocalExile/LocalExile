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
    public void Mcp_contract_leases_retries_and_short_orders_keep_human_party_boundaries()
    {
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
