using GameServer.Core.Accounts;
using GameServer.Modules.PlayerBots;
using GameServer.Storage;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class BotResumeLimitTests
{
    [Fact]
    public void Restarting_at_a_zero_limit_preserves_the_character_for_a_later_restart()
    {
        string dir = Directory.CreateTempSubdirectory("bot-resume-").FullName;
        string db = Path.Combine(dir, "test.db"), state = Path.Combine(dir, "bots.json");
        Saves.Open(db);
        try
        {
            var store = new SqliteAccountStore(Saves.Db);
            var account = new Account(Guid.NewGuid(), "AuditBot", "PlayerBots:test", "", DateTimeOffset.UtcNow);
            store.AddAccount(account);
            var actor = new AccountService(store).CreateCharacter(account.Id, "AuditBot", CharacterClass.Ranger).Value!;
            File.WriteAllText(state, JsonSerializer.Serialize(new[] { new { AccountId = account.Id, CharacterId = actor.Id,
                Account = account.Name, actor.Name, actor.Class, Level = 1, QuestIndex = 0, Deaths = 0 } }));
            foreach (int limit in new[] { 0, 0, 1 })
            {
                var director = new BotDirector(new(MaxBots: limit), [], new BotLog(dir), _ => { }, state);
                director.Start();
                try { Assert.Equal(limit, director._bots.Count); }
                finally { director.Stop(); }
                Assert.NotNull(store.GetCharacter(actor.Id));
                using var saved = JsonDocument.Parse(File.ReadAllText(state));
                Assert.Equal(actor.Id, Assert.Single(saved.RootElement.EnumerateArray()).GetProperty("CharacterId").GetGuid());
            }
        }
        finally { TestEnvironment.ReleaseSaves(db); Directory.Delete(dir, true); }
    }
}
