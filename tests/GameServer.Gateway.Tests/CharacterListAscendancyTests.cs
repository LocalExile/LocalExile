using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class CharacterListAscendancyTests
{
    [Fact]
    public void LoginListReadsCommittedAscendancyAndPreservesCosmetics()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-portrait-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SaveDatabase(path);
            var accounts = new AccountService(new SqliteAccountStore(db));
            var account = accounts.Register("PortraitAccount", "pw1234").Value!;
            var character = accounts.CreateCharacter(account.Id, "PortraitProbe", CharacterClass.Ranger, "Standard").Value!;
            accounts.SetPortrait(character.Name, 7);
            accounts.SetPortraitFrame(character.Name, 9);
            var projection = new CharacterListClass(db);
            var session = new LoginServerSession(accounts) { CharacterListClass = projection.For };
            session.Handle(new AccountLogin(2, account.Name, new byte[32], AccountService.ClientPasswordDigest("pw1234"),
                new byte[32], false, false, 0x40, 0));
            ListEntry15 Entry() => Assert.IsType<List15>(Assert.Single(session.Handle(new EmptyMessage(0x18)))).Entries[0];
            Assert.Equal((byte)CharacterClass.Ranger, Entry().B4a);
            void Select(string? id) => db.Put(SaveDatabase.Table.Characters, character.Name, JsonSerializer.Serialize(new
            {
                MainAscendancy = new { Selection = id is null ? null : new { Id = id } }
            }));
            Select("Deadeye");
            Assert.Equal(CharacterClassByte.Encode((byte)CharacterClass.Ranger, 2), Entry().B4a);
            Assert.Equal(7, Entry().U46);
            Assert.Equal(9, Entry().U44);
            Select("Pathfinder");
            Assert.Equal(CharacterClassByte.Encode((byte)CharacterClass.Ranger, 3), Entry().B4a);
            Select(null);
            Assert.Equal((byte)CharacterClass.Ranger, Entry().B4a);
        }
        finally
        {
            foreach (string file in new[] { path, SaveDatabase.AuthPathFor(path) })
                foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(file + suffix);
        }
    }
}
