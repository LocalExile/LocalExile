namespace GameServer.Core.Tests;

/// <summary>Holds a file open for a moment and lets it go, as an antivirus scan or the search indexer does.</summary>
static class FileHold
{
    /// <summary>Opens <paramref name="path"/> for reading with the given sharing and closes it after
    /// <paramref name="ms"/> milliseconds. FileShare.ReadWrite (no Delete) lets others read but not replace the file;
    /// FileShare.None lets nobody even read it.</summary>
    public static FileStream For(string path, FileShare share, int ms)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        // A thread of its own: on the thread pool the release waited behind the other tests' work, and on a busy
        // two-core machine it came after the save had given up (2 s), which failed the test that expects the save.
        new Thread(() => { Thread.Sleep(ms); stream.Dispose(); }) { IsBackground = true }.Start();
        return stream;
    }
}
