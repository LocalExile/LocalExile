using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The label over a map device's portal (21-hideouts.md 搂5): the S鈫扖 0x008f flags byte the client's nametag
/// reads, and the hand-over that answers whether the map is completed.</summary>
public sealed class MapPortalLabelTests
{
    // 0x008f: u16 id, u8 flags, u8 +0x2e, u8 +0x31, u32 BE device, u16 BE +0x2c, u8 +0x2f, u8 +0x30, u8 n, then each
    // entry's u32 BE portal object and u16 BE area HASH16. Bit 1 of the flags is the completed label (+0x33); bit 0 is
    // the "just opened" render path (+0x32) and is never set.
    [Fact]
    public void A_portals_list_carries_the_completed_flag_and_never_the_opened_bit()
    {
        InstanceMapDevice.Portal[] portals = [new(0x400, 0x2a5a, "Owner", Style: 1, Variation: 6)];
        byte[] plain = InstanceMapDevice.Portals(0x300, portals);
        byte[] completed = InstanceMapDevice.Portals(0x300, portals, completed: true);

        Assert.Equal(InstanceMapDevice.PortalsId, BinaryPrimitives.ReadUInt16BigEndian(plain));
        Assert.Equal((byte)0x00, plain[2]);
        Assert.Equal((byte)0x02, completed[2]);
        // The flag is all that moves: the device, the entry and the area the label is named after stay as they were.
        Assert.Equal(plain.Length, completed.Length);
        Assert.Equal(plain[..2], completed[..2]);
        Assert.Equal(plain[3..], completed[3..]);
        Assert.Equal(0x300u, BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(5)));
        Assert.Equal((byte)1, plain[13]);
        Assert.Equal(0x400u, BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(14)));
        Assert.Equal((ushort)0x2a5a, BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(18)));
        // A portal listed with area 0 closes, and its list is a plain one.
        Assert.Equal((byte)0x00, InstanceMapDevice.Portals(0x300, [new(0x400, 0)])[2]);
    }


    // The mark behind the flag is per run and per character: an area change is a new connection of the same character (the
    // hideout re-sends the list, so the label has to survive it), another map is another run, and a map going into the
    // device clears it.
    [Fact]
    public void The_completed_label_follows_the_character_and_a_new_map_clears_it()
    {
        var anywhere = new InstanceState();
        ClientState Player(string name) => new(new(0x235c, "1_1_town", 32, 32, 10, 10, null, []), 0, 1)
        {
            Character = name, Account = name, League = "Standard", Instance = anywhere, PlayerSent = true, PlayerLife = 100,
        };
        var first = Player("label-run-first");
        var next = Player("label-run-first"); // the same character's connection after an area change
        const string strand = "MapWorldsStrand", chateau = "MapWorldsChateau";
        Assert.False(Game.RunBossKilled(first, strand));
        Game.MarkRunBossKilled(first, strand);
        Assert.True(Game.RunBossKilled(first, strand));
        Assert.True(Game.RunBossKilled(next, strand));
        // Another map is another run, and another character has its own.
        Assert.False(Game.RunBossKilled(first, chateau));
        Assert.False(Game.RunBossKilled(Player("label-run-second"), strand));
        // A map put in the device starts unmarked.
        Game.ResetRunCompleted(next);
        Assert.False(Game.RunBossKilled(first, strand));
    }
}
