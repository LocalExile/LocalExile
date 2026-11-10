using System.Reflection;

namespace GameServer.Gateway.Tests;

public class ModCopyTests
{
    [Fact]
    public void An_old_copy_in_use_survives_the_cleanup_and_a_stopped_runs_goes()
    {
        string assembly = typeof(GameServer.Modding.IMod).Assembly.Location;
        var loaded = new ModLoadContext("in-use", Path.GetDirectoryName(assembly)!, []);
        loaded.LoadCopy(assembly);
        // A copy no running Gateway holds: the same folder a run leaves behind when it ends.
        string left = Path.Combine(Path.GetDirectoryName(loaded.CopyDirectory)!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(left);
        File.WriteAllText(Path.Combine(left, "old.dll"), "");
        foreach (string directory in new[] { loaded.CopyDirectory, left })
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddHours(-1));

        ModLoadContext.DeleteOldCopies();

        Assert.True(File.Exists(Path.Combine(loaded.CopyDirectory, Path.GetFileName(assembly))));
        Assert.False(Directory.Exists(left));
    }
}
