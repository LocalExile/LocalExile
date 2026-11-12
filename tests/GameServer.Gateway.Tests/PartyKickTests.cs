using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Social;

namespace GameServer.Gateway.Tests;

public class PartyKickTests
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
    static uint PartyOf(byte[] m) => BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(2));
    static List<byte[]> WithId(List<byte[]> messages, ushort id) => [.. messages.Where(m => m.Length >= 2 && Id(m) == id)];

    [Fact]
    public async Task Kick_removes_the_member_updates_the_rest_and_disbands_below_two()
    {
        // The smaller party (SendParty) introduces every member (0xa7): that reads the character's cosmetics from a save
        // database, as a server has one open (the same scratch store pattern as PartyExperienceChecks).
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-party-kick-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        using var runtime = new PartyFollowerTests.Runtime();
        var instance = new InstanceState();
        using var wire = new PartyFollowerTests.Wire();
        var leader = runtime.Connection(instance, wire, "KickLeader");
        var first = runtime.Connection(instance, wire, "KickMemberA");
        var second = runtime.Connection(instance, wire, "KickMemberB");
        var (leaderMessages, leaderOutbox) = Capture();
        var (firstMessages, firstOutbox) = Capture();
        var (secondMessages, secondOutbox) = Capture();
        leader.Outbox = leaderOutbox;
        first.Outbox = firstOutbox;
        second.Outbox = secondOutbox;
        const uint party = 0x9000_0101;
        SocialFeature.Parties[party] = [leader, first, second];
        try
        {
            // The leader kicks one of three: it is told (0xaa) and the remaining two get the smaller party (0xa9).
            Assert.True(await SocialFeature.KickOrCancelParty("kick-test", leader, "KickMemberA"));
            var removed = Assert.Single(WithId(firstMessages, InstanceSocial.PartyRemoveId));
            Assert.Equal(party, PartyOf(removed));
            Assert.True(SocialFeature.Parties.TryGetValue(party, out var left));
            Assert.Equal(["KickLeader", "KickMemberB"], left.Select(c => c.Character));
            foreach (var (to, messages) in new[] { (leader, leaderMessages), (second, secondMessages) })
            {
                var info = Assert.Single(WithId(messages, InstanceSocial.PartyInfoId));
                var reader = new LoginReader(info);
                Assert.Equal(InstanceSocial.PartyInfoId, reader.U16());
                Assert.Equal(party, reader.U32());
            }
            // Two left: the second kick leaves one member, so the party is disbanded on both.
            leaderMessages.Clear();
            firstMessages.Clear();
            secondMessages.Clear();
            Assert.True(await SocialFeature.KickOrCancelParty("kick-test", leader, "KickMemberB"));
            Assert.False(SocialFeature.Parties.ContainsKey(party));
            Assert.Single(WithId(secondMessages, InstanceSocial.PartyRemoveId));
            var disbanded = Assert.Single(WithId(leaderMessages, InstanceSocial.PartyRemoveId));
            Assert.Equal(party, PartyOf(disbanded));
        }
        finally { SocialFeature.Parties.TryRemove(party, out _); TestEnvironment.ReleaseSaves(saves); }
    }

    [Fact]
    public async Task Kick_cancels_a_pending_invitation_and_refuses_players_who_do_not_lead()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        var instance = new InstanceState();
        using var wire = new PartyFollowerTests.Wire();
        var leader = runtime.Connection(instance, wire, "CancelLeader");
        var invited = runtime.Connection(instance, wire, "CancelTarget");
        var (invitedMessages, invitedOutbox) = Capture();
        invited.Outbox = invitedOutbox;
        const uint invite = 0x9000_0111, party = 0x9000_0112;
        SocialFeature.PartyInvites[invite] = (leader, invited);
        try
        {
            // The invited player never joined: the leader drops the invitation and that player is told (0xaa).
            Assert.True(await SocialFeature.KickOrCancelParty("kick-test", leader, "CancelTarget"));
            Assert.False(SocialFeature.PartyInvites.ContainsKey(invite));
            var dropped = Assert.Single(WithId(invitedMessages, InstanceSocial.PartyRemoveId));
            Assert.Equal(invite, PartyOf(dropped));
        }
        finally { SocialFeature.PartyInvites.TryRemove(invite, out _); }

        var member = runtime.Connection(instance, wire, "CancelMember");
        var (messages, outbox) = Capture();
        member.Outbox = outbox;
        SocialFeature.Parties[party] = [leader, member];
        try
        {
            // The client offers Kick only to the leader: a member's message changes nothing and sends nothing.
            Assert.True(await SocialFeature.KickOrCancelParty("kick-test", member, "CancelLeader"));
            Assert.Equal(2, SocialFeature.Parties[party].Count);
            Assert.Empty(messages);
        }
        finally { SocialFeature.Parties.TryRemove(party, out _); }

        // A player in no party is another module's (the PlayerBots one owns its bots' parties): leave it alone.
        Assert.False(await SocialFeature.KickOrCancelParty("kick-test", member, "SomeBot"));
    }
}
