using GameServer.Storage;

namespace GameServer.Gateway.Tests;

/// <summary>What a fight asks of a character's save many times a second (its skill cosmetics, its remembered auras, a
/// module's text) comes from the save as last read, not from the tables each time: 1000 kills read the whole save 1766
/// times in ten seconds (2026-11-13). A write, by this server or by another process, is still seen by the next reader.</summary>
public class SavedPartReadTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void A_part_of_a_save_is_read_from_the_tables_once_until_something_is_written()
    {
        TestEnvironment.OpenOwnSaves("saved-parts");
        // Under the database's lock, so the reads counted are this test's own.
        Saves.Db.Read(() => { Check("SavedParts"); return 0; });
    }

    [Fact]
    public void Experience_is_read_without_the_whole_save()
    {
        TestEnvironment.OpenOwnSaves("saved-experience");
        try { Saves.Db.Read(() => { CheckExperience("SavedExperience", "NoSavedExperience"); return 0; }); }
        finally { CharacterSaves.ForgetExperience("SavedExperience"); CharacterSaves.ForgetExperience("NoSavedExperience"); }
    }

    // Every kill publishes the character's experience twice (Game.CurrentExperience): it must not read the save's items.
    static void CheckExperience(string name, string unsaved)
    {
        CharacterSaves.Write(name, new([], Experience: 100));
        long before = Saves.CharacterReads;
        Assert.Equal(100u, CharacterSaves.Experience(name));
        // A kill's gain waits in memory, starting from the saved total, and is the current experience.
        Assert.Equal((100u, 150u), CharacterSaves.AddExperience(name, 0, xp => xp + 50));
        for (int i = 0; i < 50; i++) Assert.Equal(150u, CharacterSaves.Experience(name));
        // A character without a save has none, even with a gain waiting (as Load gives no save).
        Assert.Null(CharacterSaves.Experience(unsaved));
        CharacterSaves.AddExperience(unsaved, 7, xp => xp + 1);
        Assert.Null(CharacterSaves.Experience(unsaved));
        Assert.Equal(0, Saves.CharacterReads - before);

        // The same answer as the whole save, before and after the gain is saved.
        Assert.Equal(CharacterSaves.Load(name)!.Experience, CharacterSaves.Experience(name));
        CharacterSaves.SaveExperience(name);
        Assert.Equal(150u, CharacterSaves.Experience(name));
        Assert.Equal(150u, CharacterSaves.Load(name)!.Experience);
        CharacterSaves.Write(name, new([]));
        Assert.Null(CharacterSaves.Experience(name));
        Assert.Null(CharacterSaves.Load(name)!.Experience);
    }

    static void Check(string name)
    {
        CharacterSaves.Write(name, new CharacterSaves.Save([])
        {
            ActiveAuras = ["Anger"], Stance = "Sand", PetrifiedBlood = true,
            ModuleData = new() { ["test.key"] = "one" },
            SkillCosmetics = new() { [CharacterSaves.SkillCosmeticKey(7, 1)] = new(3) },
        });

        long before = Saves.CharacterReads;
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal("one", CharacterSaves.ModuleData(name, "test.key"));
            Assert.Single(CharacterSaves.SkillCosmetics(name));
            var remembered = CharacterSaves.Remembered(name);
            Assert.Equal(["Anger"], remembered.Auras);
            Assert.Equal(("Sand", true), (remembered.Stance, remembered.PetrifiedBlood));
        }
        Assert.Equal(1, Saves.CharacterReads - before);
        // A character without a save is not asked for again either.
        before = Saves.CharacterReads;
        for (int i = 0; i < 10; i++) Assert.Null(CharacterSaves.ModuleData("NoSuchCharacter", "test.key"));
        Assert.Equal(1, Saves.CharacterReads - before);
        // A kill of a monster no Voidstone names does not look at the save at all (every kill asks).
        before = Saves.CharacterReads;
        Assert.Null(MapBossFights.Current.Unlock(name, "Metadata/Monsters/Zombies/ZombieBite"));
        Assert.Equal(0, Saves.CharacterReads - before);

        // This server's own write is seen by the next reader.
        CharacterSaves.ChangeModuleData(name, "test.key", _ => "two");
        Assert.Equal("two", CharacterSaves.ModuleData(name, "test.key"));
        CharacterSaves.SetStance(name, "Blood");
        Assert.Equal("Blood", CharacterSaves.Remembered(name).Stance);
        // Inside a transaction a reader sees what that transaction has written so far, and nothing of it stays when
        // the transaction is undone.
        Assert.Throws<InvalidOperationException>(() => Saves.Atomically(() =>
        {
            CharacterSaves.SetStance(name, "Sand");
            Assert.Equal("Sand", CharacterSaves.Remembered(name).Stance);
            throw new InvalidOperationException("undo");
        }));
        Assert.Equal("Blood", CharacterSaves.Remembered(name).Stance);
        // Another connection's write (Auth's process, a tool with the file open) is seen too.
        using (var other = new SaveDatabase(Saves.Db.Path, Saves.Db.AuthPath))
            SaveRows.WriteCharacter(other, name, CharacterSaves.Load(name)! with { Stance = "Sand", ActiveAuras = [] });
        Assert.Empty(CharacterSaves.Remembered(name).Auras);
        Assert.Equal("Sand", CharacterSaves.Remembered(name).Stance);
        // Load still gives every caller a save of its own, never the kept one.
        Assert.NotSame(CharacterSaves.Load(name), CharacterSaves.Load(name));
    }

    [Fact]
    public void Selective_inventory_experience_and_stash_writes_preserve_other_parts_and_respect_rollbacks_and_external_writers()
    {
        TestEnvironment.OpenOwnSaves("selective-writes");
        string name = "SelectiveChar";
        string account = "SelectiveAccount";
        try
        {
            var gem = new CharacterSaves.SavedItem(9001, GemExperience: 500);
            var armour = new CharacterSaves.SavedItem(1001, Sockets: [new(GameServer.Core.Protocol.Instance.SocketColour.Blue, gem)]);
            var weapon = new CharacterSaves.SavedItem(1002);
            var scroll1 = new CharacterSaves.SavedItem(2001, StackSize: 5);
            var scroll2 = new CharacterSaves.SavedItem(2002, StackSize: 3);
            var startWeapon = new CharacterSaves.SavedItem(3001, Sockets: [new(GameServer.Core.Protocol.Instance.SocketColour.Red, gem)]);
            var tutorial = new CharacterSaves.OpeningTutorial(new(4001), new(4002), Talked: true, ZombieKilled: true);

            CharacterSaves.Write(name, new CharacterSaves.Save(
                new()
                {
                    [0] = [new(1, 0, 0, scroll1), new(2, 1, 0, scroll2)],
                    [1] = [new(3, 0, 0, armour)],
                    [2] = [new(4, 0, 0, weapon)],
                },
                SkillBar: new() { [0] = "Action/Move" },
                Experience: 100,
                Passives: [10, 20],
                QuestFlags: ["a1q1"],
                StartWeapon: startWeapon,
                Tutorial: tutorial,
                ModuleData: new() { ["k"] = "v" },
                Stance: "Sand"));

            // Updating inventories must not read the whole save, must preserve tutorial gems and other fields,
            // and must cleanly remove the start weapon together with its socketed child row.
            long readsBefore = Saves.CharacterReads;
            CharacterSaves.SetInventories(name, new()
            {
                [0] = [new(1, 0, 0, scroll1)],
                [1] = [new(3, 0, 0, armour)],
                [2] = [new(4, 0, 0, weapon)],
            }, startWeapon: null);
            Assert.Equal(readsBefore, Saves.CharacterReads);

            var afterInv = CharacterSaves.Load(name)!;
            Assert.Single(afterInv.Inventories[0]);
            Assert.Equal(1u, afterInv.Inventories[0][0].Id);
            Assert.Equal(500, afterInv.Inventories[1][0].Item.Sockets![0].Gem!.GemExperience);
            Assert.Single(afterInv.Inventories[2]);
            Assert.Null(afterInv.StartWeapon);
            Assert.NotNull(afterInv.Tutorial);
            Assert.Equal(4001u, afterInv.Tutorial!.SkillGem.Type);
            Assert.Equal(4002u, afterInv.Tutorial.SupportGem.Type);
            Assert.Equal([10, 20], afterInv.Passives);
            Assert.Equal("Action/Move", afterInv.SkillBar![0]);
            Assert.Equal(["a1q1"], afterInv.QuestFlags);
            Assert.Equal("Sand", afterInv.Stance);
            Assert.Equal("v", afterInv.ModuleData!["k"]);
            Assert.Equal(100u, afterInv.Experience);

            // SaveExperience updates only the experience column with zero full-save reads.
            CharacterSaves.AddExperience(name, 0, xp => xp + 50);
            readsBefore = Saves.CharacterReads;
            CharacterSaves.SaveExperience(name);
            Assert.Equal(readsBefore, Saves.CharacterReads);
            Assert.Equal(150u, CharacterSaves.Load(name)!.Experience);

            // A rolled-back inventory write leaves disk state intact and invalidates the write cache.
            Assert.Throws<InvalidOperationException>(() => Saves.Atomically(() =>
            {
                CharacterSaves.SetInventories(name, new()
                {
                    [1] = [new(3, 0, 0, armour)],
                    [2] = [new(4, 0, 0, weapon)],
                }, startWeapon: null);
                throw new InvalidOperationException("undo");
            }));
            Assert.Single(CharacterSaves.Load(name)!.Inventories[0]);

            // Emptying row 0 now commits cleanly after the rollback invalidated its cache entry.
            CharacterSaves.SetInventories(name, new()
            {
                [1] = [new(3, 0, 0, armour)],
                [2] = [new(4, 0, 0, weapon)],
            }, startWeapon: null);
            Assert.False(CharacterSaves.Load(name)!.Inventories.ContainsKey(0));

            // Another connection modifying the database advances data_version and invalidates SaveRows caches.
            using (var other = new SaveDatabase(Saves.Db.Path, Saves.Db.AuthPath))
                SaveRows.WriteCharacterInventories(other, name, new()
                {
                    [0] = [new(9, 0, 0, scroll2)],
                    [1] = [new(3, 0, 0, armour)],
                    [2] = [new(4, 0, 0, weapon)],
                }, null, null);
            Assert.Single(CharacterSaves.Load(name)!.Inventories[0]);
            CharacterSaves.SetInventories(name, new()
            {
                [1] = [new(3, 0, 0, armour)],
                [2] = [new(4, 0, 0, weapon)],
            }, startWeapon: null);
            Assert.False(CharacterSaves.Load(name)!.Inventories.ContainsKey(0));

            // Stash header caching keeps tabs intact on metadata-only writes, rolls back cleanly, and sees external writes.
            List<AccountStash.SavedTab> tabs =
            [
                new("One", 0, 1, 2, 3, []),
                new("Currency", 3, 4, 5, 6, [], Affinities: 2),
            ];
            var baseHeader = new AccountStash.Save(tabs, LastTab: 0, NextItemId: AccountStash.FirstItemId, Gold: 10,
                ModuleData: new() { ["m"] = "1" });
            SaveRows.WriteStash(Saves.Db, account, baseHeader);
            SaveRows.WriteStash(Saves.Db, account, baseHeader with { LastTab = 1, Gold = 25 });
            var readStash = SaveRows.LoadStash(Saves.Db, account)!;
            Assert.Equal((1, 25u), (readStash.LastTab, readStash.Gold));
            Assert.Equal(["One", "Currency"], readStash.Tabs.Select(t => t.Name));
            Assert.Equal("1", readStash.ModuleData!["m"]);

            var renamed = baseHeader with { Tabs = [tabs[0] with { Name = "Renamed" }, tabs[1]] };
            Assert.Throws<InvalidOperationException>(() => Saves.Atomically(() =>
            {
                SaveRows.WriteStash(Saves.Db, account, renamed);
                throw new InvalidOperationException("undo stash");
            }));
            Assert.Equal("One", SaveRows.LoadStash(Saves.Db, account)!.Tabs[0].Name);
            SaveRows.WriteStash(Saves.Db, account, renamed);
            Assert.Equal("Renamed", SaveRows.LoadStash(Saves.Db, account)!.Tabs[0].Name);

            using (var other = new SaveDatabase(Saves.Db.Path, Saves.Db.AuthPath))
                SaveRows.WriteStash(other, account, baseHeader);
            Assert.Equal("One", SaveRows.LoadStash(Saves.Db, account)!.Tabs[0].Name);
            SaveRows.WriteStash(Saves.Db, account, renamed);
            Assert.Equal("Renamed", SaveRows.LoadStash(Saves.Db, account)!.Tabs[0].Name);
        }
        finally { CharacterSaves.ForgetExperience(name); }
    }
}
