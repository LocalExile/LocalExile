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
        // 0xa7 reads the member's cosmetics from a save database, as a server has one open (the scratch store pattern of
        // PartyKickTests).
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-party-area-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        const ushort town = 0x235c, graveyard = 0xd801; // 1_1_town and The Ship Graveyard, as WorldAreas lists them.
        var presence = new InstancePresence();
        var instance = new InstanceState(presence);
        var sent = new Dictionary<ClientState, List<byte[]>>();
        var players = new List<ClientState>();
        ClientState Player(string name, ushort area, string areaId)
        {
            var c = new ClientState(new(area, areaId, 100, 100, 20, 20, null, []), 1, 1)
            {
                Character = name, Account = name, League = "Standard", Instance = instance, PlayerSent = true,
                PlayerLife = 100, ClassRow = 0, Controlled = false,
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
            var member = Player("AreaMember", graveyard, "The Ship Graveyard");
            Assert.True(await PartyRoster.InviteHuman(leader, member.Character!));
            uint party = PartyRoster.PartyOf(leader);
            await PartyRoster.AcceptHuman(member, party);
            foreach (var packets in sent.Values) lock (packets) packets.Clear();
            await PartyRoster.Publish(party);
            // Each side's row for the other names the area the other is in, so the panel has something to draw and travel to.
            Assert.Equal(graveyard, AreaOf(sent[leader], member.Account!));
            Assert.Equal(town, AreaOf(sent[member], leader.Account!));
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
