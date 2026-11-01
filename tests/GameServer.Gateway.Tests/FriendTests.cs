using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class FriendTests
{
    [Fact]
    public void Account_relations_survive_reopen_and_remove_both_sides_without_touching_other_friends()
    {
        string path = Path.Combine(Path.GetTempPath(), "LocalExile-friends-" + Guid.NewGuid().ToString("N") + ".db");
        var a = new Account(Guid.NewGuid(), "FriendA", "", "", DateTimeOffset.UnixEpoch);
        var b = a with { Id = Guid.NewGuid(), Name = "FriendB" };
        var c = a with { Id = Guid.NewGuid(), Name = "FriendC" };
        try
        {
            using (var db = new SaveDatabase(path))
            {
                var accounts = new SqliteAccountStore(db);
                foreach (var account in new[] { a, b, c }) accounts.AddAccount(account);
                var friends = new SqliteFriendStore(db);
                Assert.False(friends.Add(a.Id, a.Id));
                Assert.False(friends.Add(a.Id, Guid.NewGuid()));
                Assert.True(friends.Add(a.Id, b.Id));
                Assert.False(friends.Add(a.Id, b.Id));
                Assert.Equal(new[] { a.Id }, friends.Incoming(b.Id));
                Assert.Empty(friends.Friends(b.Id));
                Assert.True(friends.Add(b.Id, a.Id));
                Assert.True(friends.Add(a.Id, c.Id));
                Assert.Throws<IOException>(() => db.Atomically(() =>
                {
                    friends.Remove(a.Id, b.Id);
                    throw new IOException("rollback");
                }));
                Assert.Contains(b.Id, friends.Friends(a.Id));
            }
            using (var db = new SaveDatabase(path))
            {
                var friends = new SqliteFriendStore(db);
                Assert.Contains(a.Id, friends.Friends(b.Id));
                Assert.True(friends.Remove(b.Id, a.Id));
                Assert.Empty(friends.Friends(b.Id));
                Assert.Equal(new[] { c.Id }, friends.Friends(a.Id));
                Assert.False(friends.Remove(b.Id, a.Id));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Native_relation_flags_and_unicode_names_round_trip_without_a_hideout_invitation()
    {
        foreach (var (accepted, remove, flags) in new[] { (false, false, 0), (true, false, 1), (false, true, 4) })
        {
            var bytes = InstanceSocial.FriendRelation("Друг😀", accepted, remove);
            var r = new LoginReader(bytes);
            Assert.Equal(0xc9, r.U16());
            Assert.Equal(flags, r.U8());
            Assert.Equal("Друг😀", r.WStr());
            Assert.Equal("", r.WStr());
            Assert.False(r.Short);
            Assert.Equal(bytes.Length, r.Position);
        }
        var request = new LoginWriter().U16(0x80).WStr("Друг😀").U8(0).U8(1).ToArray();
        Assert.Equal(("Друг😀", (ushort)0x100), InstanceSocial.ReadFriendAdd(request));
        for (int i = 0; i < request.Length; i++) Assert.Null(InstanceSocial.FriendAddLength(request.AsSpan(0, i)));
        Assert.Equal(request.Length, InstanceSocial.FriendAddLength(request));
        foreach (ushort action in new ushort[] { 0, 1, 0x100, 0x200 })
        {
            var message = new LoginWriter().U16(0x80).WStr("Друг😀").U8((byte)action).U8((byte)(action >> 8)).ToArray();
            Assert.Equal(action, InstanceSocial.ReadFriendAdd(message).U16);
        }
        var offline = new LoginReader(InstanceSocial.OfflinePlayer("Друг"));
        Assert.Equal(0xa7, offline.U16());
        Assert.Equal("Друг", offline.WStr());
        Assert.Equal("", offline.WStr());
        Assert.Equal(0, offline.U8());
        Assert.Equal(0ul, offline.RawU64());
        Assert.False(offline.Short);
    }
}
