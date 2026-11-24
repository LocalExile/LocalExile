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
        var sent = new Dictionary<Player, List<byte[]>>();
        var players = new List<Player>();
        Player Player(string name, ushort area, string areaId, InstanceState? world = null, string league = "Standard", ulong? number = null)
        {
            var c = new Player(new(area, areaId, 100, 100, 20, 20, null, []), 1, 1)
            {
                Character = { Name = name, Account = name, League = league, ClassRow = 0 },
                Instance = world ?? instance,
                Connection = { PlayerSent = true },
                PlayerLife = 100,
                Controlled = false,
                InstanceNumber = number ?? (ulong)players.Count + 10
            };
            var packets = new List<byte[]>();
            c.Connection.Outbox = (bytes, _) => { lock (packets) packets.Add(bytes); return Task.CompletedTask; };
            sent[c] = packets;
            players.Add(c);
            Program.InGame.TryAdd(c, 0);
            return c;
        }
        try
        {
            var leader = Player("AreaLeader", town, "1_1_town");
            var member = Player("AreaMember", graveyard, "1_1_9");
            Assert.True(await PartyRoster.InviteHuman(leader, member.Character.Name!));
            uint party = PartyRoster.PartyOf(leader);
            await PartyRoster.AcceptHuman(member, party);
            foreach (var packets in sent.Values) lock (packets) packets.Clear();
            await PartyRoster.Publish(party);
            // A real area change replaces both the connection and its instance presence. Travel must cross that boundary.
            member.Closed = true;
            member = Player("AreaMember", graveyard, "1_1_9", new());
            await PartyRoster.Rebind(member);
            // Each side's row for the other names the area the other is in, so the panel has something to draw and travel to.
            Assert.Equal(graveyard, AreaOf(sent[leader], member.Character.Account!));
            Assert.Equal(town, AreaOf(sent[member], leader.Character.Account!));
            Assert.Same(member, PartyTravel.Member(leader, member.Character.Name!));
            var foreign = Player(member.Character.Name!, graveyard, "1_1_9", new(), "DifferentLeague");
            await PartyRoster.Rebind(foreign);
            Assert.Null(PartyTravel.Member(leader, member.Character.Name!));
            foreign.Closed = true;
            await PartyRoster.Rebind(member);
            Assert.Null(PartyTravel.Member(leader, leader.Character.Name!));
            Assert.Null(PartyTravel.Member(leader, "NotInThisParty"));
            Assert.Equal(member.Character.Name, InstanceSocial.ReadPartyTravel(
                new LoginWriter().U16(InstanceSocial.PartyTravelId).WStr(member.Character.Name!).ToArray()));
            // The area gate uses the travelling player's flags, not the destination owner's.
            var required = GameServer.Core.ClientTables.Table("WorldAreas")
                .First(r => r.Int("HASH16") == graveyard).Ints("EndQuestFlags");
            Assert.NotEmpty(required);
            Assert.Null(PartyTravel.Destination(leader, member));
            leader.Character.QuestFlags.UnionWith(required.Select(r => GameServer.Core.ClientTables.Table("QuestFlags")[r].Text("Id")!));
            var travel = Assert.IsType<PendingTransfer>(PartyTravel.Destination(leader, member));
            Assert.Equal((graveyard, member.InstanceNumber), (travel.Area, travel.Instance));
            Assert.Equal((20, 20), travel.ArriveAt);
            member.ReturnPortal = (123, 35, 45); member.ReturnPortalTo = town;
            Assert.Equal((43, 45), PartyTravel.Destination(leader, member)!.ArriveAt);
            member.ReturnPortal = null;
            // Same area is not the same instance; another member must not win the selected target's destination.
            var other = Player("OtherAreaMember", graveyard, "1_1_9");
            await PartyRoster.InviteHuman(leader, other.Character.Name!); await PartyRoster.AcceptHuman(other, party);
            other.Character.QuestFlags.UnionWith(leader.Character.QuestFlags);
            Assert.Equal(member.InstanceNumber, PartyTravel.Destination(other, member)?.Instance);
            // Rebound connections retire the previous identity even if it has not yet been marked closed.
            var replaced = Player(leader.Character.Name!, town, "1_1_town");
            await PartyRoster.Rebind(replaced);
            Assert.Null(PartyTravel.Member(leader, member.Character.Name!));
            Assert.Same(member, PartyTravel.Member(replaced, member.Character.Name!));
            leader = replaced;
            member.Closed = true; Assert.Null(PartyTravel.Member(leader, member.Character.Name!)); member.Closed = false;
            member.LeavingArea = true; Assert.Null(PartyTravel.Member(leader, member.Character.Name!)); member.LeavingArea = false;
            leader.PlayerLife = 0; Assert.Null(PartyTravel.Member(leader, member.Character.Name!)); leader.PlayerLife = 100;
            var mapArea = new MapDevice.MapArea(graveyard, "test-map", "test map", 12) { Level = 79 };
            var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapItemDropQuantityPlusPct] = 40, [Stat.MapItemDropRarityPlusPct] = 30 });
            var origin = new MapDevice.Origin(town, leader.InstanceNumber, 70, 80);
            try
            {
                var opened = MapDevice.Activate(member.Character.Name!, mapArea, mods, entrance: origin);
                Assert.Equal(opened.Run, MapDevice.AtEntrance(town, leader.InstanceNumber)?.Run);
                Assert.Null(MapDevice.AtEntrance(town, leader.InstanceNumber + 1));
                Assert.Equal(5, MapDevice.TakePortal(member.Character.Name!, 0, opened.Run)?.Left);
                Assert.Null(MapDevice.TakePortal(member.Character.Name!, 1, opened.Run)); // First transfer not resolved yet.
                MapDevice.Entered(member.Character.Name!, mapArea, member.InstanceNumber);
                // A real connection admits the retained run before publishing its map-dependent loot rules.
                MapDevice.Admit(member.Instance!, 0x50415254, graveyard, member.InstanceNumber);
                leader.Character.QuestFlags.UnionWith(GameServer.Core.ClientTables.Table("WorldAreas")
                    .First(r => r.Int("HASH16") == town).Ints("EndQuestFlags")
                    .Select(r => GameServer.Core.ClientTables.Table("QuestFlags")[r].Text("Id")!));
                var entrance = Assert.IsType<PendingTransfer>(PartyTravel.Destination(leader, member));
                Assert.Equal((town, leader.InstanceNumber, (70, 80)), (entrance.Area, entrance.Instance, entrance.ArriveAt));
                Assert.Equal(5, MapDevice.OpenOf(member.Character.Name!)!.PortalsLeft); // Party travel spends none.
                var guest = Player("MapGuest", graveyard, "test-map", member.Instance, number: member.InstanceNumber);
                MapDevice.Activate(guest.Character.Name!, mapArea); // Same map area, but a different personal run.
                try
                {
                    Assert.Equal(opened.Run, MapDevice.ForInstance(guest)?.Run);
                    Assert.Same(mods, MapDevice.ForInstance(guest)?.Mods);
                    Assert.Equal(79, MapDevice.ForInstance(guest)?.Area.Level);
                    Assert.Equal(1.4, Game.ItemQuantityFactor(guest), 8);
                    Assert.Equal(30, Game.IncreasedItemRarity(guest));
                    for (int i = 1; i < 6; i++)
                        Assert.Equal(5 - i, MapDevice.TakePortal(member.Character.Name!, i, opened.Run)?.Left);
                    Assert.Equal(6, MapDevice.OpenOf(guest.Character.Name!)!.PortalsLeft);
                    Assert.Equal(origin, MapDevice.ForInstance(guest)?.Entrance); // Last entry retains the return route.
                    var replacement = MapDevice.Activate(member.Character.Name!, mapArea, entrance: origin);
                    Assert.Null(MapDevice.TakePortal(member.Character.Name!, 0, opened.Run));
                    MapDevice.Entered(member.Character.Name!, mapArea, 999, opened.Run);
                    MapDevice.Close(member.Character.Name!, opened.Run);
                    Assert.Equal(replacement.Run, MapDevice.OpenOf(member.Character.Name!)?.Run);
                    Assert.Equal(0ul, MapDevice.OpenOf(member.Character.Name!)!.Instance);
                    Assert.Equal(origin, MapDevice.ForInstance(guest)?.Entrance);
                    Assert.Equal(origin.Instance, PartyTravel.Destination(leader, member)?.Instance);
                    // A first transfer finishing after replacement must retain its own destination's rules too.
                    MapDevice.Entered(opened, 999);
                    var delayed = new InstanceState();
                    Assert.Same(mods, MapDevice.ForInstance(mapArea.Hash16, 999, delayed)?.Mods);
                    Assert.Equal(replacement.Run, MapDevice.OpenOf(member.Character.Name!)?.Run);
                    Assert.Equal(0ul, MapDevice.OpenOf(member.Character.Name!)!.Instance);
                }
                finally { MapDevice.Close(guest.Character.Name!); }
            }
            finally { MapDevice.Close(member.Character.Name!); }
            // Visiting another player's personal area must never turn it into the visitor's default home.
            var registry = new GameServer.Core.World.InstanceRegistry { PersonalAreas = new HashSet<ushort> { graveyard } };
            var own = registry.Enter(graveyard, "Owner");
            var guestsHome = registry.Enter(graveyard, "Guest");
            Assert.NotEqual(own.Id, guestsHome.Id);
            Assert.Same(own, registry.EnterExisting(own.Id, "Guest"));
            Assert.Equal("Owner", own.Owner);
            Assert.Same(guestsHome, registry.Enter(graveyard, "Guest"));
            Assert.Same(own, registry.Enter(graveyard, "Owner"));
            // Each player has an independent instance presence when they are in different areas/hideouts; invites still cross them.
            var townInviteSender = Player("CrossAreaSender", town, "1_1_town", new(), number: 70);
            var hideoutInviteReceiver = Player("CrossAreaReceiver", graveyard, "1_1_9", new(), number: 71);
            Assert.True(await PartyRoster.InviteHuman(townInviteSender, hideoutInviteReceiver.Character.Name!));
            uint crossParty = PartyRoster.PartyOf(townInviteSender);
            Assert.NotEqual(0u, crossParty);
            await PartyRoster.AcceptHuman(hideoutInviteReceiver, crossParty);
            Assert.Equal(crossParty, PartyRoster.PartyOf(hideoutInviteReceiver));

            var homeArea = HideoutCatalog.Default!;
            var homeMember = Player("HomeMember", homeArea.AreaHash16, homeArea.AreaId);
            await PartyRoster.InviteHuman(leader, homeMember.Character.Name!); await PartyRoster.AcceptHuman(homeMember, party);
            Assert.Equal(0ul, PartyRoster.SharedInstance(leader, new(homeArea.AreaHash16, 0, false)));
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
