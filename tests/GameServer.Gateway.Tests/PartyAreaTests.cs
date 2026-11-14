using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Social;

namespace GameServer.Gateway.Tests;

public class PartyAreaTests
{
    // #715: a member's row in the party panel draws the area at known-player +0xa8, read as a WorldAreas HASH16 and looked
    // up (0x140207a95 -> 0x140208210). It carried the character's level, which is no area, so every member in another area
    // read "Travelling..." and their travel button stayed disabled. The party list now carries the area each member is in.
    [Fact]
    public async Task The_party_list_carries_each_members_area_as_a_worldareas_hash()
    {
        // The real input path must frame 0x92, including fragments and compact Unicode names, before dispatch.
        foreach (string name in new[] { "AreaMember", "Voyageur\u00e9\U0001f409" })
        {
            byte[] request = new LoginWriter().U16(InstanceSocial.PartyTravelId).WStr(name).ToArray();
            byte[] following = new LoginWriter().U16(InstanceSocial.PartyAcceptId).U32(0x101).ToArray();
            for (int cut = 1; cut < request.Length; cut++)
            {
                Assert.Empty(InstanceProtocol.SplitClientMessages(request.AsSpan(0, cut), out var partial, out var unknown));
                Assert.Empty(unknown);
                Assert.Equal(request[..cut], partial);
                var messages = InstanceProtocol.SplitClientMessages([.. partial, .. request[cut..], .. following],
                    out partial, out unknown);
                Assert.Empty(partial); Assert.Empty(unknown); Assert.Equal(2, messages.Count);
                Assert.Equal(InstanceSocial.PartyTravelId, messages[0].Id);
                Assert.Equal(name, InstanceSocial.ReadPartyTravel(messages[0].Bytes));
                Assert.Equal(InstanceSocial.PartyAcceptId, messages[1].Id);
            }
        }
        // 0xa7 reads the member's cosmetics from a save database, as a server has one open (the scratch store pattern of
        // PartyKickTests).
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-party-area-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        const ushort town = 0x235c, graveyard = 0xd801; // 1_1_town and The Ship Graveyard, as WorldAreas lists them.
        var presence = new InstancePresence();
        var instance = new InstanceState(presence);
        var sent = new Dictionary<ClientState, List<byte[]>>();
        var players = new List<ClientState>();
        ClientState Player(string name, ushort area, string areaId, InstanceState? world = null, string league = "Standard")
        {
            var c = new ClientState(new(area, areaId, 100, 100, 20, 20, null, []), 1, 1)
            {
                Character = name, Account = name, League = league, Instance = world ?? instance, PlayerSent = true,
                PlayerLife = 100, ClassRow = 0, Controlled = false, InstanceNumber = (ulong)players.Count + 10,
            };
            var packets = new List<byte[]>();
            c.Outbox = (bytes, _) => { lock (packets) packets.Add(bytes); return Task.CompletedTask; };
            sent[c] = packets;
            players.Add(c);
            Program.InGame.TryAdd(c, 0);
            return c;
        }
        try
        {
            var leader = Player("AreaLeader", town, "1_1_town");
            var member = Player("AreaMember", graveyard, "1_1_9");
            Assert.True(await PartyRoster.InviteHuman(leader, member.Character!));
            uint party = PartyRoster.PartyOf(leader);
            await PartyRoster.AcceptHuman(member, party);
            foreach (var packets in sent.Values) lock (packets) packets.Clear();
            await PartyRoster.Publish(party);
            // A real area change replaces both the connection and its instance presence. Travel must cross that boundary.
            member.Closed = true;
            member = Player("AreaMember", graveyard, "1_1_9", new());
            await PartyRoster.Rebind(member);
            // Each side's row for the other names the area the other is in, so the panel has something to draw and travel to.
            Assert.Equal(graveyard, AreaOf(sent[leader], member.Account!));
            Assert.Equal(town, AreaOf(sent[member], leader.Account!));
            Assert.Same(member, PartyTravel.Member(leader, member.Character!));
            var foreign = Player(member.Character!, graveyard, "1_1_9", new(), "DifferentLeague");
            await PartyRoster.Rebind(foreign);
            Assert.Null(PartyTravel.Member(leader, member.Character!));
            foreign.Closed = true;
            await PartyRoster.Rebind(member);
            Assert.Null(PartyTravel.Member(leader, leader.Character!));
            Assert.Null(PartyTravel.Member(leader, "NotInThisParty"));
            Assert.Equal(member.Character, InstanceSocial.ReadPartyTravel(
                new LoginWriter().U16(InstanceSocial.PartyTravelId).WStr(member.Character!).ToArray()));
            // The area gate uses the travelling player's flags, not the destination owner's.
            var required = GameServer.Core.ClientTables.Table("WorldAreas")
                .First(r => r.Int("HASH16") == graveyard).Ints("EndQuestFlags");
            Assert.NotEmpty(required);
            Assert.Null(PartyTravel.Destination(leader, member));
            leader.QuestFlags.UnionWith(required.Select(r => GameServer.Core.ClientTables.Table("QuestFlags")[r].Text("Id")!));
            var travel = Assert.IsType<PendingTransfer>(PartyTravel.Destination(leader, member));
            Assert.Equal((graveyard, member.InstanceNumber), (travel.Area, travel.Instance));
            Assert.Equal((20, 20), travel.ArriveAt);
            member.ReturnPortal = (123, 35, 45); member.ReturnPortalTo = town;
            Assert.Equal((43, 45), PartyTravel.Destination(leader, member)!.ArriveAt);
            member.ReturnPortal = null;
            // Same area is not the same instance; another member must not win the selected target's destination.
            var other = Player("OtherAreaMember", graveyard, "1_1_9");
            await PartyRoster.InviteHuman(leader, other.Character!); await PartyRoster.AcceptHuman(other, party);
            other.QuestFlags.UnionWith(leader.QuestFlags);
            Assert.Equal(member.InstanceNumber, PartyTravel.Destination(other, member)?.Instance);
            // Rebound connections retire the previous identity even if it has not yet been marked closed.
            var replaced = Player(leader.Character!, town, "1_1_town");
            await PartyRoster.Rebind(replaced);
            Assert.Null(PartyTravel.Member(leader, member.Character!));
            Assert.Same(member, PartyTravel.Member(replaced, member.Character!));
            leader = replaced;
            member.Closed = true; Assert.Null(PartyTravel.Member(leader, member.Character!)); member.Closed = false;
            member.LeavingArea = true; Assert.Null(PartyTravel.Member(leader, member.Character!)); member.LeavingArea = false;
            leader.PlayerLife = 0; Assert.Null(PartyTravel.Member(leader, member.Character!)); leader.PlayerLife = 100;
            var mapArea = new MapDevice.MapArea(graveyard, "test-map", "test map", 1);
            var origin = new MapDevice.Origin(town, leader.InstanceNumber, 70, 80);
            try
            {
                MapDevice.Activate(member.Character!, mapArea, entrance: origin);
                MapDevice.Entered(member.Character!, mapArea, member.InstanceNumber);
                leader.QuestFlags.UnionWith(GameServer.Core.ClientTables.Table("WorldAreas")
                    .First(r => r.Int("HASH16") == town).Ints("EndQuestFlags")
                    .Select(r => GameServer.Core.ClientTables.Table("QuestFlags")[r].Text("Id")!));
                var entrance = Assert.IsType<PendingTransfer>(PartyTravel.Destination(leader, member));
                Assert.Equal((town, leader.InstanceNumber, (70, 80)), (entrance.Area, entrance.Instance, entrance.ArriveAt));
                Assert.Equal(6, MapDevice.OpenOf(member.Character!)!.PortalsLeft);
                MapDevice.Activate(member.Character!, mapArea);
                MapDevice.Entered(member.Character!, mapArea, member.InstanceNumber);
                Assert.Null(PartyTravel.Destination(leader, member)); // No origin cannot mean free entry.
            }
            finally { MapDevice.Close(member.Character!); }
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
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    // The area a known-player packet for this account carries: reads past the account, the empty second string, the flags,
    // the character, +0xb9 and the packed class, to +0xa8.
    static ushort AreaOf(List<byte[]> packets, string account)
    {
        List<byte[]> known;
        lock (packets) known = [.. packets.Where(p => p.Length >= 2 && p[0] == 0 && p[1] == InstanceSocial.KnownPlayerId)];
        foreach (var bytes in known)
        {
            var reader = new LoginReader(bytes);
            reader.U16();
            if (reader.WStr() != account) continue;
            reader.WStr();
            reader.U8();
            reader.WStr();
            reader.U8();
            reader.U8();
            return reader.U16();
        }
        throw new InvalidOperationException($"no 0xa7 for {account}");
    }
}
