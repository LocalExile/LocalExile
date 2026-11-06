using System.Security.Cryptography;

namespace LocalExile;

/// <summary>Keep critical source files read-only and deny competing writers where supported; detect metadata changes too.</summary>
sealed class ClientReadLease : IDisposable
{
    readonly List<(string Path, FileStream Stream, long Length, DateTime Written)> _files = [];
    public string ExecutableSha256 { get; private set; } = "";
    public string ExecutableName { get; private set; } = "";

    public static ClientReadLease Open(string folder, string executable)
    {
        var lease = new ClientReadLease();
        try
        {
            foreach (string path in new[] { Path.Combine(folder, "Content.ggpk"), executable })
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease._files.Add((path, stream, stream.Length, File.GetLastWriteTimeUtc(path)));
            }
            lease.ExecutableSha256 = Convert.ToHexStringLower(SHA256.HashData(lease._files[1].Stream));
            lease.ExecutableName = Path.GetFileName(executable);
            return lease;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lease.Dispose();
            throw new LauncherException(ExitCode.Client, "The client could not be opened for a stable read. Close its updater and retry.",
                e is UnauthorizedAccessException ? "access-denied" : "client-unreadable", $"{folder}: {e.Message}");
        }
    }

    public void AssertUnchanged()
    {
        foreach (var file in _files)
        {
            var current = new FileInfo(file.Path);
            if (!current.Exists || current.Length != file.Length || file.Stream.Length != file.Length || current.LastWriteTimeUtc != file.Written)
                throw new LauncherException(ExitCode.Client,
                    "The client changed while LocalExile was checking or copying it. Close its updater and retry; the files were kept.",
                    "client-changed", file.Path);
        }
    }

    public void AssertExecutable(string expectedSha256)
    {
        if (ExecutableSha256 != expectedSha256)
            throw new LauncherException(ExitCode.Client,
                "The executable changed after its compatibility check. Close its updater and retry; the files were kept.",
                "client-changed");
    }

    public void Dispose()
    {
        foreach (var file in _files) file.Stream.Dispose();
        _files.Clear();
    }
}
