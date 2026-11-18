using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Social;

namespace GameServer.Gateway.Tests;

public class PartyChatTests
{
    static (List<byte[]> Messages, Func<byte[], string?, Task> Outbox) Capture()
    {
        var messages = new List<byte[]>();
        Task Outbox(byte[] bytes, string? log)
        {
            lock (messages) messages.Add((byte[])bytes.Clone());
            return Task.CompletedTask;
        }
        return (messages, Outbox);
    }

    static ushort Id(byte[] m) => BinaryPrimitives.ReadUInt16BigEndian(m);
    static List<byte[]> WithId(List<byte[]> messages, ushort id) => [.. messages.Where(m => m.Length >= 2 && Id(m) == id)];

    [Fact]
    public async Task A_party_line_reaches_the_party_and_no_one_else()
    {
        // The party's introductions (SendParty) read the character's cosmetics from a save database, as a server has one
        // open (the same scratch store pattern as PartyKickTests).
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-party-chat-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        using var runtime = new PartyFollowerTests.Runtime();
        var instance = new InstanceState();
        using var wire = new PartyFollowerTests.Wire();
        var leader = runtime.Connection(instance, wire, "ChatLeader");
        var member = runtime.Connection(instance, wire, "ChatMember");
        var outsider = runtime.Connection(instance, wire, "ChatOutsider");
        var (leaderMessages, leaderOutbox) = Capture();
        var (memberMessages, memberOutbox) = Capture();
        var (outsiderMessages, outsiderOutbox) = Capture();
        leader.Outbox = leaderOutbox;
        member.Outbox = memberOutbox;
        outsider.Outbox = outsiderOutbox;
        leader.Controlled = member.Controlled = outsider.Controlled = false;
        try
        {
            await PartyRoster.InviteHuman(leader, member.Character!);
            uint party = PartyRoster.PartyOf(leader);
            await PartyRoster.AcceptHuman(member, party);
            leaderMessages.Clear();
            memberMessages.Clear();
            outsiderMessages.Clear();
            // The prefix is what the chat panel reads the line as: without it a party line prints as local chat.
            Assert.Equal("%ChatLeader", InstanceChat.Sender(TypedChannel.Party, "ChatLeader"));
            Assert.True(await PartyChat.Deliver(leader, "hello party", null));
            foreach (var messages in new[] { leaderMessages, memberMessages })
            {
                var line = Assert.Single(WithId(messages, InstanceChat.ChatLineId));
                var reader = new LoginReader(line);
                Assert.Equal(InstanceChat.ChatLineId, reader.U16());
                Assert.Equal("%ChatLeader", reader.WStr());
                Assert.Equal("hello party", reader.WStr());
            }
            Assert.Empty(WithId(outsiderMessages, InstanceChat.ChatLineId));
        }
        finally
        {
            foreach (var c in new[] { leader, member }) await PartyRoster.LeaveHuman(c, PartyRoster.PartyOf(c));
            TestEnvironment.ReleaseSaves(saves);
        }
        // A player in no party: nothing carries the line.
        Assert.False(await PartyChat.Deliver(outsider, "anyone?", null));
    }
}
