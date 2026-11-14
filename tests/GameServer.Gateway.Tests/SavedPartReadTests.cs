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
}
