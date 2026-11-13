using System.Text.Json;
using GameServer.Core.Saves;

namespace GameServer.Core.Tests;

public class SaveFilesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"savefiles-{Guid.NewGuid():N}");

    public SaveFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    string Save => Path.Combine(_folder, "save.json");

    [Fact]
    public void A_write_keeps_the_previous_version_next_to_the_new_one()
    {
        SaveFiles.Write(Save, "one");
        SaveFiles.Write(Save, "two");

        Assert.Equal("two", File.ReadAllText(Save));
        Assert.Equal("one", File.ReadAllText(Save + ".bak"));
    }

    [Fact]
    public void A_held_backup_does_not_stop_the_save()
    {
        SaveFiles.Write(Save, "one");
        SaveFiles.Write(Save, "two");

        // Someone reading the backup (no write sharing): the backup can't be refreshed, but the save must still happen.
        using (FileHold.For(Save + ".bak", FileShare.Read, 4000))
            SaveFiles.Write(Save, "three");

        Assert.Equal("three", File.ReadAllText(Save));
    }

    [WindowsFact]
    public void A_write_that_cannot_replace_the_file_leaves_the_old_one_whole()
    {
        // Only Windows lets a holder stop a replace: elsewhere the rename succeeds and the holder keeps the old file.
        SaveFiles.Write(Save, "old");

        // Held for longer than SaveFiles waits: the write gives up.
        using (FileHold.For(Save, FileShare.None, 4000))
            Assert.ThrowsAny<Exception>(() => SaveFiles.Write(Save, "new"));

        Assert.Equal("old", File.ReadAllText(Save));
    }

    [Fact]
    public void Only_a_missing_file_loads_as_nothing()
    {
        Assert.Null(SaveFiles.Load<Dictionary<string, int>>(Save, JsonSerializerOptions.Default));

        File.WriteAllText(Save, "null");
        Assert.Throws<UnreadableSaveException>(() => SaveFiles.Load<Dictionary<string, int>>(Save, JsonSerializerOptions.Default));
        File.WriteAllText(Save, "{ not json");
        Assert.Throws<UnreadableSaveException>(() => SaveFiles.Load<Dictionary<string, int>>(Save, JsonSerializerOptions.Default));
    }
}
