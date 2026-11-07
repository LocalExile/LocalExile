using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class FlowTests
{
    private static AccountService Svc(string? path = null) => new(new JsonAccountStore(path));

    [Fact]
    public void SetPassword_replaces_both_hashes_and_allows_a_single_space()
    {
        var s = Svc();
        s.Register("carol", "first-pass");
        Assert.True(s.SetPassword("carol", " ").Ok);
        Assert.True(s.Login("carol", " ").Ok);
        Assert.False(s.Login("carol", "first-pass").Ok);
        Assert.True(s.LoginWithClientDigest("carol", AccountService.ClientPasswordDigest(" ")).Ok);
        Assert.False(s.LoginWithClientDigest("carol", AccountService.ClientPasswordDigest("first-pass")).Ok);
        Assert.Equal(AuthError.NotFound, s.SetPassword("nobody", " ").Error);
        Assert.Equal(AuthError.WeakPassword, s.SetPassword("carol", "").Error);
    }

    [Fact]
    public void Register_Login_CreateCharacter_Flow()
    {
        var s = Svc();
        var acc = s.Register("alice", "hunter2");
        Assert.True(acc.Ok);
        Assert.Equal(AuthError.NameTaken, s.Register("ALICE", "x1234").Error);
        Assert.Equal(AuthError.BadCredentials, s.Login("alice", "wrong").Error);
        var sess = s.Login("alice", "hunter2");
        Assert.True(sess.Ok);
        Assert.True(s.Resume(sess.Value!.Token).Ok);
        var c = s.CreateCharacter(acc.Value!.Id, "Exile_One", CharacterClass.Witch);
        Assert.True(c.Ok);
        Assert.Equal("1_1_1", c.Value!.AreaId);   // new characters wash up on The Twilight Strand
        Assert.Equal(AuthError.CharacterNameTaken, s.CreateCharacter(acc.Value.Id, "exile_one", CharacterClass.Ranger).Error);
        Assert.Single(s.Characters(acc.Value.Id));
        Assert.True(s.DeleteCharacter(acc.Value.Id, c.Value.Id).Ok);
        Assert.Empty(s.Characters(acc.Value.Id));
    }

    [Fact]
    public void SetLevel_changes_the_level_from_1_to_100_only()
    {
        var s = Svc();
        var acc = s.Register("dave", "pass1234").Value!;
        s.CreateCharacter(acc.Id, "Leveller", CharacterClass.Marauder);
        Assert.Equal(37, s.SetLevel("leveller", 37).Value!.Level);
        Assert.Equal(37, s.Characters(acc.Id)[0].Level);
        Assert.Equal(AuthError.InvalidLevel, s.SetLevel("Leveller", 0).Error);
        Assert.Equal(AuthError.InvalidLevel, s.SetLevel("Leveller", 101).Error);
        Assert.Equal(AuthError.NotFound, s.SetLevel("Nobody", 5).Error);
        Assert.Equal(37, s.Characters(acc.Id)[0].Level);
    }

    [Fact]
    public void Store_Persists_To_Disk()
    {
        var p = Path.Combine(Path.GetTempPath(), $"Game-{Guid.NewGuid():N}.json");
        try
        {
            var a = Svc(p).Register("bob", "pass1234").Value!;
            var s2 = Svc(p);
            Assert.True(s2.Login("bob", "pass1234").Ok);
            Assert.Equal(a.Id, new JsonAccountStore(p).FindAccount("bob")!.Id);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Instances_Towns_Shared_Others_Private_And_Switchable()
    {
        var m = new InstanceManager();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var t1 = m.Enter(a, "1_1_town");
        var t2 = m.Enter(b, "1_1_town");
        Assert.Same(t1, t2);
        var z1 = m.Enter(a, "1_1_2");
        Assert.NotSame(t1, z1);
        Assert.Same(z1, m.Current(a));
        var z2 = m.Enter(b, "1_1_2");
        Assert.NotSame(z1, z2);
        m.Enter(a, "1_1_town");
        Assert.Equal(2, m.Count);
    }
}
