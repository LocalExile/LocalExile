using System.Text.Json.Nodes;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class CharacterCreationLevelTests
{
    [Fact]
    public void Optional_level_survives_storage_and_native_login_keeps_ordinary_creation_at_level_one()
    {
        string path = Path.Combine(Path.GetTempPath(), $"character-level-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new SaveDatabase(path))
            {
                var accounts = new AccountService(new SqliteAccountStore(db));
                var account = accounts.Register("LevelAccount", "test-password").Value!;
                Assert.Equal(42, accounts.CreateCharacter(account.Id, "Leveled", CharacterClass.Templar, level: 42).Value!.Level);
                Assert.Equal(100, accounts.CreateCharacter(account.Id, "Capped", CharacterClass.Witch, level: 100).Value!.Level);
                Assert.Equal(1, accounts.CreateCharacter(account.Id, "Plain", CharacterClass.Ranger).Value!.Level);
                foreach (int level in new[] { int.MinValue, 0, 101, int.MaxValue })
                    Assert.Equal(AuthError.InvalidLevel, accounts.CreateCharacter(account.Id, "Rejected", CharacterClass.Witch, level: level).Error);
                Assert.Equal(3, accounts.Characters(account.Id).Count);
            }

            using (var db = new SaveDatabase(path))
            {
                var accounts = new AccountService(new SqliteAccountStore(db));
                Assert.Equal(42, accounts.FindCharacter("Leveled")!.Level);
                Assert.Equal(100, accounts.FindCharacter("Capped")!.Level);
                Assert.Equal(1, accounts.FindCharacter("Plain")!.Level);
                var native = new LoginServerSession(accounts);
                native.Handle(new AccountLogin(2, "LevelAccount", new byte[32], AccountService.ClientPasswordDigest("test-password"),
                    new byte[32], false, false, 0x40, 0));
                var entries = Assert.IsType<List15>(Assert.Single(native.Handle(new EmptyMessage(0x18)))).Entries;
                int index = entries.ToList().FindIndex(c => c.Name == "Leveled");
                Assert.Equal(42, entries[index].B48);
                native.Handle(new U8U32(0, (uint)index));
                Assert.Equal(42, native.CharacterLevel);

                var created = native.Handle(new CreateCharacter("NativeOne", "Standard", 0, 0, "IntDefault", new byte[32]));
                Assert.Equal(LoginServerSession.ResultOk, Assert.IsType<CreateCharacterResult>(created[1]).Result);
                Assert.Equal(1, accounts.FindCharacter("NativeOne")!.Level);
            }
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public void Dev_protocol_accepts_optional_integer_levels_and_rejects_invalid_input_without_creating_characters()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var account = accounts.Register("LevelAccount", "test-password").Value!;
        var session = new GameSession(accounts, new InstanceManager());
        JsonNode Call(string request) => JsonNode.Parse(session.Handle(request))!;

        Assert.Equal("not_logged_in", Call("""{"op":"create_character","name":"Rejected","class":"witch","level":42}""")["error"]!.GetValue<string>());
        Assert.True(Call("""{"op":"login","name":"LevelAccount","password":"test-password"}""")["ok"]!.GetValue<bool>());
        Assert.Equal(42, Call("""{"op":"create_character","name":"Leveled","class":"witch","level":42}""")["character"]!["level"]!.GetValue<int>());
        Assert.Equal(100, Call("""{"op":"create_character","name":"Capped","class":"witch","level":100}""")["character"]!["level"]!.GetValue<int>());
        Assert.Equal(1, Call("""{"op":"create_character","name":"Plain","class":"witch"}""")["character"]!["level"]!.GetValue<int>());
        Assert.Equal(1, Call("""{"op":"create_character","name":"NullLevel","class":"witch","level":null}""")["character"]!["level"]!.GetValue<int>());
        foreach (string level in new[] { "0", "101", "-2147483648", "2147483647" })
            Assert.Equal("InvalidLevel", Call($$"""{"op":"create_character","name":"Rejected","class":"witch","level":{{level}}}""")["error"]!.GetValue<string>());
        foreach (string level in new[] { "\"42\"", "42.5", "2147483648", "true", "{}", "[]" })
            Assert.Equal("bad_request", Call($$"""{"op":"create_character","name":"Rejected","class":"witch","level":{{level}}}""")["error"]!.GetValue<string>());
        Assert.Equal(4, accounts.Characters(account.Id).Count);
        Assert.True(Call("""{"op":"create_character","name":"Rejected","class":"witch","level":1}""")["ok"]!.GetValue<bool>());
    }
}
