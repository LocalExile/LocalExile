using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class PortraitFrameTests
{
    [Fact]
    public void KnownPlayerCarriesTheClassFrameAndPortrait()
    {
        // In the order the client reads them (0x141f8f850).
        var r = new LoginReader(InstanceSocial.KnownPlayer("Account", "Character", 90, packedClass: 3, frame: 12, portrait: 7));

        Assert.Equal(InstanceSocial.KnownPlayerId, r.U16());
        Assert.Equal("Account", r.WStr());
        Assert.Equal("", r.WStr());
        Assert.Equal(0x06, r.U8());      // bit 1 the frame and portrait, bit 2 the character section
        Assert.Equal("Character", r.WStr());
        r.U8();
        Assert.Equal(3, r.U8());         // +0xb8
        Assert.Equal(90, r.U16());       // +0xa8
        r.U16();
        r.WStr();
        r.U8();
        Assert.Equal(12, r.U16());       // +0xb0
        Assert.Equal(7, r.U16());        // +0xb2
        Assert.False(r.Short);

        // Neither: the pair is left out, as the client's own writer does.
        var plain = new LoginReader(InstanceSocial.KnownPlayer("Account", "Character", 90));
        plain.U16();
        plain.WStr();
        plain.WStr();
        Assert.Equal(0x04, plain.U8());
    }

    [Fact]
    public void APickShowsOnTheCharacterListAndReachesTheGameAtTheNextLogin()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var account = accounts.Register("FrameAccount", "pw1234").Value!;
        accounts.CreateCharacter(account.Id, "FrameProbe", CharacterClass.Witch, "Standard");
        var session = new LoginServerSession(accounts);
        session.Handle(new AccountLogin(2, account.Name, new byte[32], AccountService.ClientPasswordDigest("pw1234"),
            new byte[32], false, false, 0x40, 0));

        accounts.SetPortraitFrame("FrameProbe", 5);
        session.Handle(new U8U32(0, 0));
        Assert.Equal(5, session.CharacterPortraitFrame);

        accounts.SetPortraitFrame("FrameProbe", 9);
        var list = Assert.IsType<List15>(Assert.Single(session.Handle(new EmptyMessage(0x18))));
        Assert.Equal(9, list.Entries[0].U44);
        Assert.Equal(5, session.CharacterPortraitFrame);

        session.Handle(new U8U32(0, 0));
        Assert.Equal(9, session.CharacterPortraitFrame);
    }
}
