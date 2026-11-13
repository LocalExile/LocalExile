using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Social;

namespace GameServer.Gateway.Tests;

static class MixedPartyChecks
{
    internal static async Task Verify()
    {
        var presence = new InstancePresence();
        var instance = new InstanceState(presence);
        var players = new List<ClientState>();
        var packets = new Dictionary<ClientState, List<byte[]>>();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        ClientState Player(string name, InstanceState? world = null, bool ssf = false, string? league = null, ulong instanceNumber = 0)
        {
            var c = new ClientState(new(1, "mixed-party", 100, 100, 20, 20, null, []), 1, 1)
            {
                Character = name + suffix, Account = name + suffix, League = league ?? "Standard", Ssf = ssf,
                Instance = world ?? instance, InstanceNumber = instanceNumber, PlayerSent = true, PlayerLife = 100, ClassRow = 0
            };
            var sent = new List<byte[]>();
            c.Outbox = (bytes, _) => { sent.Add(bytes); return Task.CompletedTask; };
            players.Add(c); packets[c] = sent; Program.InGame.TryAdd(c, 0);
            return c;
        }
        PartyCompanion Bot(int i) => new("mixed-bot-" + suffix + i, "Companion" + i,
            InstanceSocial.KnownPlayer("mixed-bot-" + suffix + i, "Companion" + i, 60));
        try
        {
            var owner = Player("Owner");
            var friend = Player("Friend");
            var third = Player("Third");
            // A bot-first group must remain the same party when a real friend accepts.
            Assert.True(PartyRoster.TryAddCompanion(owner, Bot(0), out uint party));
            await PartyRoster.Publish(party);
            Assert.True(await PartyRoster.InviteHuman(owner, friend.Character!));
            Assert.Equal(party, View(packets[friend]).Id);
            // A guessed acceptance from a different account must not consume the friend's invitation.
            await PartyRoster.AcceptHuman(third, party);
            await PartyRoster.AcceptHuman(friend, party);
            Assert.Equal(party, PartyRoster.PartyOf(friend));
            foreach (var c in new[] { owner, friend })
            {
                var view = View(packets[c]);
                Assert.Equal(3, view.Members.Length);
                Assert.Equal(c.Account, view.Members[view.Own]);
                Assert.Contains(Bot(0).Account, view.Members);
                Assert.Equal(3, c.PartySize);
            }
            // Inviting from a non-leader and accepting into an existing group appends rather than replaces.
            Assert.True(await PartyRoster.InviteHuman(friend, third.Account!));
            await PartyRoster.AcceptHuman(third, party);
            Assert.Equal(4, PartyRoster.Size(party));
            // Human and bot slots share the capacity; pending invites cannot overfill it.
            var pending = Player("Pending");
            Assert.True(await PartyRoster.InviteHuman(owner, pending.Character!));
            Assert.True(PartyRoster.TryAddCompanion(friend, Bot(1), out uint same));
            Assert.Equal(party, same);
            Assert.True(PartyRoster.TryAddCompanion(owner, Bot(2), out same));
            Assert.False(PartyRoster.TryAddCompanion(owner, Bot(3), out _));
            await PartyRoster.AcceptHuman(pending, party);
            Assert.Equal(0u, PartyRoster.PartyOf(pending));
            await PartyRoster.Publish(party);
            foreach (var c in new[] { owner, friend, third }) Assert.Equal(6, View(packets[c]).Members.Length);
            await PartyRoster.Kick(friend, Bot(0).Account); // Only the leader may remove someone.
            Assert.True(PartyRoster.ContainsCompanion(party, Bot(0).Account));
            await PartyRoster.Kick(owner, Bot(0).Account);
            Assert.False(PartyRoster.ContainsCompanion(party, Bot(0).Account));
            Assert.Equal(5, friend.PartySize);
            // Re-entry replaces the human connection without losing any human or companion.
            owner.Closed = true;
            var replacement = Player("Owner", new InstanceState(presence), instanceNumber: 321);
            await PartyRoster.Rebind(replacement);
            Assert.Same(replacement, PartyRoster.Leader(party));
            Assert.Equal(321ul, PartyRoster.SharedInstance(friend, new(1, 0, false)));
            Assert.Equal(replacement.Account, View(packets[replacement]).Members[0]);
            Assert.True(await PartyRoster.LeaveHuman(replacement, party));
            Assert.Same(friend, PartyRoster.Leader(party));
            Assert.Equal(1, replacement.PartySize);
            Assert.True(PartyRoster.ContainsCompanion(party, Bot(1).Account));
            Assert.Equal(0, View(packets[friend]).Own);
            await PartyRoster.Kick(friend, third.Account!);
            Assert.Equal(0u, PartyRoster.PartyOf(third));
            Assert.Equal(3, PartyRoster.Size(party)); // One friend and two bots.
            await PartyRoster.LeaveHuman(friend, party);
            Assert.Null(PartyRoster.Leader(party));
            Assert.False(PartyRoster.ContainsCompanion(party, Bot(1).Account));
            Assert.Equal(1, friend.PartySize);

            // The reverse order (human-first) and slot reuse after a bot leaves.
            Assert.True(await PartyRoster.InviteHuman(owner = replacement, friend.Character!));
            party = View(packets[friend]).Id;
            await PartyRoster.AcceptHuman(friend, party);
            Assert.True(PartyRoster.TryAddCompanion(owner, Bot(4), out same));
            Assert.Equal(party, same);
            await PartyRoster.Publish(party);
            Assert.Equal(3, View(packets[friend]).Members.Length);
            Assert.True(PartyRoster.RemoveCompanion(party, Bot(4).Account));
            await PartyRoster.Publish(party);
            Assert.Equal(2, friend.PartySize);
            Assert.Equal(2, View(packets[owner]).Members.Length);
            // A member cannot simultaneously join another party, nor can SSF/cross-league actors join.
            Assert.True(await PartyRoster.InviteHuman(third, friend.Character!));
            Assert.Equal(0u, PartyRoster.PartyOf(third));
            var ssf = Player("Solo", ssf: true);
            var otherLeague = Player("OtherLeague", league: "Hardcore");
            Assert.False(PartyRoster.TryAddCompanion(ssf, Bot(5), out _));
            Assert.True(await PartyRoster.InviteHuman(owner, ssf.Character!));
            Assert.True(await PartyRoster.InviteHuman(owner, otherLeague.Character!));
            Assert.Equal(0u, PartyRoster.PartyOf(ssf));
            Assert.Equal(0u, PartyRoster.PartyOf(otherLeague));
        }
        finally
        {
            foreach (var c in players)
            {
                uint party = PartyRoster.PartyOf(c);
                if (party != 0) await PartyRoster.LeaveHuman(c, party);
                c.Closed = true;
                Program.InGame.TryRemove(c, out _);
            }
        }
    }

    static (uint Id, int Own, string[] Members) View(List<byte[]> packets)
    {
        var bytes = packets.Last(p => p.Length >= 2 && p[0] == 0 && p[1] == InstanceSocial.PartyInfoId);
        var reader = new LoginReader(bytes);
        reader.U16();
        uint id = reader.U32();
        reader.WStr(); reader.WStr(); reader.U32();
        int own = reader.U8();
        reader.U8(); reader.U8(); reader.U8();
        int count = reader.U8();
        var members = new string[count];
        for (int i = 0; i < count; i++) { members[i] = reader.WStr(); reader.U8(); }
        Assert.False(reader.Short);
        return (id, own, members);
    }
}
