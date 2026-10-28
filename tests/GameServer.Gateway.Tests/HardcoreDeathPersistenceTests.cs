using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class HardcoreDeathPersistenceTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"hardcore-death-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(_folder, "saves.db");
    static readonly DateTimeOffset DiedAt = DateTimeOffset.Parse("2026-10-26T12:00:00Z");

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }

    static Character Seed(SaveDatabase db, string league = "HC SSF Allflame")
    {
        var c = new Character(Guid.NewGuid(), Guid.NewGuid(), "DeathProbe", CharacterClass.Witch, 12, league,
            "1_1_2", DiedAt.AddDays(-1), DiedAt, "1_1_town");
        new SqliteAccountStore(db).AddCharacter(c);
        db.Put(SaveDatabase.Table.Characters, c.Name, "{\"carriedItems\":[1,2],\"xp\":120}");
        db.Put(SaveDatabase.Table.Stashes, "account@HC SSF Allflame", "source stash and gold");
        db.Put(SaveDatabase.Table.Stashes, "account@SSF Allflame", "destination stash and gold");
        return c;
    }

    [Fact]
    public void Death_and_carried_progress_survive_restart_without_moving_either_stash()
    {
        Character original;
        using (var db = new SaveDatabase(DbPath))
        {
            original = Seed(db);
            var dead = HardcoreDeathPersistence.Commit(db, original.Name, LeagueCatalog.Default.Find(original.League)!,
                () => db.Put(SaveDatabase.Table.Characters, original.Name, "{\"carriedItems\":[1,2,3],\"xp\":150}"), DiedAt);
            Assert.Equal("SSF Allflame", dead.League);
        }
        using var again = new SaveDatabase(DbPath);
        var store = new SqliteAccountStore(again);
        Assert.Equal("SSF Allflame", store.FindCharacter(original.Name)!.League);
        Assert.Equal(new HardcoreDeathRecord(original.League, DiedAt, false), store.FindCharacter(original.Name)!.HardcoreDeath);
        Assert.Equal("{\"carriedItems\":[1,2,3],\"xp\":150}", again.Get(SaveDatabase.Table.Characters, original.Name));
        Assert.Equal("source stash and gold", again.Get(SaveDatabase.Table.Stashes, "account@HC SSF Allflame"));
        Assert.Equal("destination stash and gold", again.Get(SaveDatabase.Table.Stashes, "account@SSF Allflame"));
        // Auth may have read this snapshot before the Gateway committed death.
        store.UpdateCharacter(original with { Level = 13, Portrait = 7 });
        Assert.Equal(("SSF Allflame", 13, (ushort)7),
            (store.FindCharacter(original.Name)!.League, store.FindCharacter(original.Name)!.Level, store.FindCharacter(original.Name)!.Portrait));
        Assert.NotNull(store.FindCharacter(original.Name)!.HardcoreDeath);
    }

    [Fact]
    public void Failure_rolls_back_both_the_progress_and_the_death_and_can_be_retried()
    {
        using var db = new SaveDatabase(DbPath);
        var c = Seed(db, "HC SSF R Allflame");
        var source = LeagueCatalog.Default.Find(c.League)!;
        Assert.Throws<IOException>(() => HardcoreDeathPersistence.Commit(db, c.Name, source, () =>
        {
            db.Put(SaveDatabase.Table.Characters, c.Name, "partial");
            throw new IOException("simulated failed save");
        }, DiedAt));
        Assert.Equal(c, new SqliteAccountStore(db).FindCharacter(c.Name));
        Assert.Equal("{\"carriedItems\":[1,2],\"xp\":120}", db.Get(SaveDatabase.Table.Characters, c.Name));
        var dead = HardcoreDeathPersistence.Commit(db, c.Name, source, () => { }, DiedAt);
        Assert.Equal("Void", dead.League);
        Assert.False(HardcoreDeathRules.CanEnter(dead));
        Assert.NotNull(db.Get(SaveDatabase.Table.Characters, c.Name));
    }

    [Fact]
    public void Repeated_callbacks_do_not_flush_stale_progress_over_the_softcore_character()
    {
        using var db = new SaveDatabase(DbPath);
        var c = Seed(db);
        var source = LeagueCatalog.Default.Find(c.League)!;
        var dead = HardcoreDeathPersistence.Commit(db, c.Name, source, () => { }, DiedAt);
        db.Put(SaveDatabase.Table.Characters, c.Name, "new softcore progress");
        var repeated = HardcoreDeathPersistence.Commit(db, c.Name, source,
            () => throw new InvalidOperationException("must not flush the old connection"), DiedAt.AddDays(1));
        Assert.Equal(dead, repeated);
        Assert.Equal("new softcore progress", db.Get(SaveDatabase.Table.Characters, c.Name));
    }

    [Fact]
    public void A_reused_name_cannot_receive_an_old_characters_death()
    {
        using var db = new SaveDatabase(DbPath);
        var c = Seed(db);
        Assert.Throws<InvalidOperationException>(() => HardcoreDeathPersistence.Commit(db, c.Name,
            LeagueCatalog.Default.Find(c.League)!, () => throw new Exception("must not flush"),
            expectedCharacterId: Guid.NewGuid()));
        Assert.Null(new SqliteAccountStore(db).FindCharacter(c.Name)!.HardcoreDeath);
    }

    [Fact]
    public void A_transient_failed_death_is_retried_before_reentry_and_keeps_its_original_timestamp()
    {
        using var db = new SaveDatabase(DbPath);
        var c = Seed(db, "HC SSF R Allflame");
        var source = LeagueCatalog.Default.Find(c.League)!;
        bool failed = true;
        int flushes = 0;
        void Flush()
        {
            flushes++;
            if (failed) throw new IOException("storage unavailable");
            db.Put(SaveDatabase.Table.Characters, c.Name, "carried progress retained");
        }
        Assert.Throws<IOException>(() => HardcoreDeathPersistence.Commit(db, c.Name, source, Flush, DiedAt, c.Id));
        Assert.Throws<IOException>(() => HardcoreDeathPersistence.RetryPendingCharacter(db, c.Id));
        Assert.Equal(c, new SqliteAccountStore(db).FindCharacter(c.Name));
        failed = false;
        HardcoreDeathPersistence.RetryPendingCharacter(db, c.Id);
        var dead = new SqliteAccountStore(db).FindCharacter(c.Name)!;
        Assert.False(HardcoreDeathRules.CanEnter(dead));
        Assert.Equal(new HardcoreDeathRecord(c.League, DiedAt, true), dead.HardcoreDeath);
        Assert.Equal("carried progress retained", db.Get(SaveDatabase.Table.Characters, c.Name));
        int completed = flushes;
        HardcoreDeathPersistence.RetryPendingCharacter(db, c.Id);
        Assert.Equal(completed, flushes);
    }

    [Fact]
    public void Timer_retry_recovers_an_ordinary_hardcore_death_after_the_connection_is_gone()
    {
        using var db = new SaveDatabase(DbPath);
        var c = Seed(db);
        bool failed = true;
        Assert.Throws<IOException>(() => HardcoreDeathPersistence.Commit(db, c.Name, LeagueCatalog.Default.Find(c.League)!,
            () => { if (failed) throw new IOException("storage unavailable"); }, DiedAt, c.Id));
        // A tick while storage still fails retains the pending outcome instead of throwing away the other saves.
        HardcoreDeathPersistence.RetryPending(db);
        Assert.Null(new SqliteAccountStore(db).FindCharacter(c.Name)!.HardcoreDeath);
        failed = false;
        HardcoreDeathPersistence.RetryPending(db);
        Assert.Equal("SSF Allflame", new SqliteAccountStore(db).FindCharacter(c.Name)!.League);
    }
}
