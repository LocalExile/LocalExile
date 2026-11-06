using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class InstanceProtocolTests
{
    // The first bytes a real 3.29.3b client sent to the instance server after 0x14 (127.0.0.1, ticket 0), 2026-10-16.
    private static readonly byte[] RealOpening = Convert.FromHexString("000F00000000000E76240850BFE2B4956AD1D6B07F000001000000004001");

    [Fact]
    public void Real_opening_parses_as_ticket_then_hello()
    {
        var o = InstanceProtocol.TryReadOpening(RealOpening)!;
        Assert.Equal(0u, o.Ticket);
        Assert.Equal("76240850BFE2B4956AD1D6B0", Convert.ToHexString(o.Digest));
        Assert.Equal((0x7F000001u, 0u, (byte)0x40, (byte)1), (o.Address, o.U32_128, o.Realm, o.Flag));
    }

    [Fact]
    public void Opening_needs_all_30_bytes_and_starts_with_0x0f()
    {
        Assert.Null(InstanceProtocol.TryReadOpening(RealOpening.AsSpan(0, 29)));
        byte[] bad = [.. RealOpening];
        bad[1] = 0x0e;
        Assert.Throws<InvalidDataException>(() => InstanceProtocol.TryReadOpening(bad));
    }

    [Fact]
    public void Start_encryption_is_a_bare_0x0011()
    {
        Assert.Equal([0x00, 0x11], InstanceProtocol.StartEncryption());
    }

    [Fact]
    public void Area_instance_encodes_in_write_order()
    {
        var bytes = new AreaInstance(InstanceId: 0x01020304, AreaHash: 0x235c, Seed: 7, Level: 2).Encode();
        // +0x28 is the topology variant: 59 (0x3B), as the server sends it (AreaInstance.TopologyVariant).
        Assert.Equal("001D" + "01020304" + "235C" + "0000" + "00000007" + "03" + "00" + "02" + "23" + "000000" + "02" + "00" + "3B"
                     + "0000" + "0000" + "00" + "0000", Convert.ToHexString(bytes));
    }

    [Fact]
    public void Enter_area_minimal_form()
    {
        Assert.Equal("0021" + "0000" + "0000" + "0000" + "0000" + "000000" + "00" + "00", Convert.ToHexString(InstanceProtocol.EnterArea()));
    }

    [Fact]
    public void Keyring_keys_depend_only_on_secret_and_ticket()
    {
        var secret = new byte[64];
        secret[0] = 1;
        var a = new InstanceKeyring(secret);
        var b = new InstanceKeyring([.. secret]);
        Assert.Equal(a.Key(42), b.Key(42));
        Assert.NotEqual(a.Key(42), a.Key(43));
        Assert.Equal(64, a.Key(42).Length);
    }

    [Fact]
    public void Server_ciphers_mirror_the_clients()
    {
        var keyring = InstanceKeyring.Random();
        var client = new ConnectionCiphers(keyring.Key(7), isClient: true);
        var server = keyring.Ciphers(7);

        byte[] up = [0x00, 0x01, 0x02, 0x03];
        client.Send.Process(up);
        server.Receive.Process(up);
        Assert.Equal([0x00, 0x01, 0x02, 0x03], up);

        byte[] down = [0x00, 0x21];
        server.Send.Process(down);
        client.Receive.Process(down);
        Assert.Equal([0x00, 0x21], down);
    }

    [Fact]
    public void Login_0x14_carries_a_ticket_and_its_key()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var acc = accounts.Register("dave", "pw1234").Value!;
        accounts.CreateCharacter(acc.Id, "DaveWitch", CharacterClass.Witch);
        var keyring = InstanceKeyring.Random();
        var session = new LoginServerSession(accounts) { Keyring = keyring };
        session.Handle(new AccountLogin(2, "dave", new byte[32], AccountService.ClientPasswordDigest("pw1234"), new byte[32], false, false, 0x40, 0));

        var info = Assert.IsType<InstanceInfo>(Assert.Single(session.Handle(new U8U32(0, 0))));
        Assert.NotEqual(0u, info.U32_38);
        Assert.Equal(keyring.Key(info.U32_38), info.Key64);
    }

    [Fact]
    public void Death_screen_0x55_and_resurrect_0x54()
    {
        // Live 2026-10-17: 0x0c showed "Resurrect in Town" and "Resurrect at Checkpoint"; 0x02 hides the screen.
        Assert.Equal("00550C000000000000", Convert.ToHexString(InstanceProtocol.DeathScreen(0x0c)));
        Assert.Equal("005502", Convert.ToHexString(InstanceProtocol.DeathScreen(0x02)));
        Assert.True(InstanceProtocol.ReadResurrectInTown([0x00, 0x54, 0x01]));
        Assert.False(InstanceProtocol.ReadResurrectInTown([0x00, 0x54, 0x00]));
        Assert.Equal(3, InstanceProtocol.KnownClientLength(InstanceProtocol.ResurrectId));
    }

    [Fact]
    public void Exit_to_character_selection_0x153_is_framed()
    {
        // Live: Esc → "Exit to Character Selection" sent 0153 alone; a tick may follow in the same chunk.
        var messages = InstanceProtocol.SplitClientMessages(Convert.FromHexString("0153" + "001B00000000AA"), out byte[] partial, out byte[] unknown);
        Assert.Equal([InstanceProtocol.ExitToCharacterSelectId, InstanceProtocol.TickId], messages.Select(m => m.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Lockstep_moves_and_server_frames()
    {
        // A lockstep click captured live (2026-10-21): the target 41 right and 17 up from the player, Move, flags 0x0402.
        var click = InstanceProtocol.ReadLockstepMoveStart(Convert.FromHexString("018E00000029FFFFFFEF2909040200"));
        Assert.Equal((41, -17, (ushort)0x2909, (ushort)0x0402, (byte)0), click);
        Assert.Equal((-17, -2), InstanceProtocol.ReadLockstepMoveUpdate(Convert.FromHexString("0192FFFFFFEFFFFFFFFE")));
        Assert.Equal("02B7", Convert.ToHexString(InstanceProtocol.FrameEnd()));
        Assert.Equal("02B821", Convert.ToHexString(InstanceProtocol.FrameLength(33)));
        Assert.Equal("01AD00001000000000000000FFFF0000007BFFFFFFFE",
            Convert.ToHexString(InstanceProtocol.ActionTarget(0x1000, 0xffff, 123, -2)));
    }
}
