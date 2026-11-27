using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class ClientChannelTests
{
    static byte[] RulesetAction(byte row, ReadOnlySpan<byte> payload)
    {
        var w = new LoginWriter().U16(InstanceRulesets.ActionId).U8(row);
        InstanceProtocol.CompactUInt(w, (uint)payload.Length);
        foreach (byte b in payload) w.U8(b);
        return w.ToArray();
    }

    static byte[] ChatPacket(string text) =>
        new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray();

    [Fact]
    public async Task Client_for_player_sends_ruleset_packet_and_chat_and_dispatches_inbound_with_owner_cleanup()
    {
        const string owner = "test-client-channel-owner";
        using var runtime = new PartyFollowerTests.Runtime();
        var layout = new AreaLayout(1, "1_1_1", 100, 100, 50, 50, (_, _) => true, []);
        var player = new Player(layout, 1, 1)
        {
            Instance = new(),
            Character = { Name = "Chan" + Guid.NewGuid().ToString("N") },
            LeagueModule = new(new League("chan-test", "Standard", false, false), new HashSet<string>([owner])),
        };
        player.Connection.Session = new("127.0.0.1:41234", null!, new ConnectionCiphers(new byte[64], false));
        player.Connection.PlayerSent = true;

        List<byte[]> outbox = [];
        List<string> replies = [];
        player.Connection.Outbox = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; };
        player.Commands = new ChatCommands
        {
            Send = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; },
            Reply = text => { replies.Add(text); return Task.CompletedTask; },
        };

        var channel = Client.For(player);
        Assert.Same(channel, Client.For(Program.PlayerOf(player)));
        Assert.Equal($"{player.Connection.Remote}#{player.Id}", channel.ConnectionId);
        Assert.True(channel.Connected);

        byte[] payload = [0x01, 0x02, 0xAA, 0xBB];
        await channel.SendRuleset(Client.DefaultRulesetId, payload);
        Assert.Single(outbox);
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(outbox[0]));
        Assert.Equal(Client.DefaultRulesetId, outbox[0][2]);
        Assert.Equal(payload, outbox[0].AsSpan(4).ToArray());

        outbox.Clear();
        await channel.SendPacket(0x02F0, payload);
        Assert.Single(outbox);
        Assert.Equal(0x02F0, BinaryPrimitives.ReadUInt16BigEndian(outbox[0]));
        Assert.Equal(payload, outbox[0].AsSpan(2).ToArray());

        await channel.SendChat("line1\nline2");
        Assert.Equal(["line1\nline2"], replies);

        outbox.Clear();
        player.Commands = null;
        await channel.SendChat("alpha\n\nbeta");
        Assert.Equal(3, outbox.Count);
        Assert.All(outbox, p => Assert.Equal(InstanceChat.ChatLineId, BinaryPrimitives.ReadUInt16BigEndian(p)));

        List<(byte Row, byte[] Bytes)> rulesets = [];
        List<string> commands = [];
        using var rulesetSub = Client.OnRuleset(owner, Client.DefaultRulesetId, (ch, bytes) =>
        {
            Assert.Same(channel, ch);
            rulesets.Add((Client.DefaultRulesetId, bytes.ToArray()));
            return Task.FromResult(true);
        });
        using var commandSub = Client.OnCommand(owner, (ch, line) =>
        {
            Assert.Same(channel, ch);
            commands.Add(line);
            return Task.FromResult(true);
        });

        try
        {
            Assert.True(Client.IsCommand("/nm"));
            Assert.True(Client.IsCommand("/nm env test"));
            Assert.False(Client.IsCommand("/nmother"));
            Assert.False(Client.IsCommand("/other"));

            await Game.OnClientMessages([(InstanceRulesets.ActionId, RulesetAction(Client.DefaultRulesetId, payload))], [], player);
            Assert.Single(rulesets);
            Assert.Equal(payload, rulesets[0].Bytes);
            Assert.False(await Client.DispatchRulesetAsync(player, RulesetAction(0xA1, payload)));

            replies.Clear();
            await Game.OnClientMessages([(InstanceChat.ChatId, ChatPacket("/nm env test"))], [], player);
            Assert.Equal(["/nm env test"], commands);
            Assert.Empty(replies);
            Assert.False(await Client.DispatchCommandAsync(player, "/other"));

            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            Assert.False(await Client.DispatchRulesetAsync(player, RulesetAction(Client.DefaultRulesetId, payload)));
            Assert.False(await Client.DispatchCommandAsync(player, "/nm env test"));
        }
        finally
        {
            player.Closed = true;
            Client.Release(player);
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
        }
    }

    [Fact]
    public async Task Client_release_on_disconnect_clears_per_player_handlers_and_marks_channel_disconnected()
    {
        var layout = new AreaLayout(1, "1_1_1", 100, 100, 50, 50, (_, _) => true, []);
        var player = new Player(layout, 1, 1)
        {
            Instance = new(),
            Character = { Name = "Rel" + Guid.NewGuid().ToString("N") },
        };
        player.Connection.Session = new("127.0.0.1:41235", null!, new ConnectionCiphers(new byte[64], false));

        List<byte[]> outbox = [];
        List<string> replies = [];
        player.Connection.Outbox = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; };
        player.Commands = new ChatCommands
        {
            Send = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; },
            Reply = text => { replies.Add(text); return Task.CompletedTask; },
        };

        var channel = Client.For(player);
        int rulesetHits = 0;
        int commandHits = 0;
        using var rulesetSub = channel.OnRuleset(Client.DefaultRulesetId, _ => { rulesetHits++; return Task.FromResult(true); });
        using var commandSub = channel.OnCommand(_ => { commandHits++; return Task.FromResult(true); });

        Assert.True(await Client.DispatchRulesetAsync(player, RulesetAction(Client.DefaultRulesetId, [0x09])));
        Assert.True(await Client.DispatchCommandAsync(player, "/nm ping"));
        Assert.Equal(1, rulesetHits);
        Assert.Equal(1, commandHits);

        player.Closed = true;
        Client.Release(player);

        Assert.False(channel.Connected);
        await channel.SendRuleset(Client.DefaultRulesetId, new byte[] { 0x09 });
        await channel.SendPacket(0x02F0, new byte[] { 0x09 });
        await channel.SendChat("ignored");
        Assert.Empty(outbox);
        Assert.Empty(replies);

        Assert.False(await Client.DispatchRulesetAsync(player, RulesetAction(Client.DefaultRulesetId, [0x09])));
        Assert.False(await Client.DispatchCommandAsync(player, "/nm ping"));
        Assert.Equal(1, rulesetHits);
        Assert.Equal(1, commandHits);
    }
}
