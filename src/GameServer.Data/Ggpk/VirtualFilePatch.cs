using System.Security.Cryptography;
using System.Text.Json;
using LibGGPK3;
using LibGGPK3.Records;

namespace GameServer.Data.Ggpk;

/// <summary>
/// Reversible redirection of one existing bundled file. The original bundle stays untouched; the small raw index and
/// original virtual file are backed up before mutation. The index switches only after the new bundle is complete.
/// Exclusive GGPK access prevents patching a running client. Unknown intervening index edits are never overwritten.
/// </summary>
public static class VirtualFilePatch
{
    const string IndexPath = "Bundles2/_.index.bin";
    const string IndexBackup = "original-index.bundle.bin";
    const string FileBackup = "original-file.bin";
    const string StateName = "state.json";

    public sealed record State(string Ggpk, string VirtualPath, string OriginalFileSha256, string ReplacementSha256,
        string OriginalIndexSha256, string PatchedIndexSha256, string BundlePath, string ReplacementBundleSha256, string Phase,
        bool CreatedBundle = false);

    public static string JournalPath(string backupFolder) => Path.Combine(backupFolder, "ggpk-write-journal.bin");
    public static string BackupFolder(string ggpkPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ggpkPath))!, "LocalExileBackups", "phrecia-passive-graph-v1");
    public static void EnsureNoPending(string backupFolder) => GgpkUndoJournal.EnsureNoPending(JournalPath(backupFolder));
    public static bool Recover(string ggpkPath, string backupFolder) => GgpkUndoJournal.Recover(ggpkPath, JournalPath(backupFolder));

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static bool Matches(string ggpkPath, string virtualPath, ReadOnlySpan<byte> expected, IOodleDecoder decoder, string? backupFolder = null)
    {
        EnsureNoPending(backupFolder ?? BackupFolder(ggpkPath));
        using var fs = GameFileSystem.Open(ggpkPath, decoder);
        return fs.ReadFile(virtualPath).AsSpan().SequenceEqual(expected);
    }

    public static bool Apply(string ggpkPath, string virtualPath, ReadOnlySpan<byte> replacement, string originalSha256,
        string backupFolder, IOodleDecoder decoder, GgpkUndoJournal.Fault? fault = null)
    {
        ggpkPath = Path.GetFullPath(ggpkPath);
        Recover(ggpkPath, backupFolder); // raw byte undo must precede any index parsing
        string replacementHash = Hash(replacement);
        byte[] current;
        using (var fs = GameFileSystem.Open(ggpkPath, decoder)) current = fs.ReadFile(virtualPath);
        if (current.AsSpan().SequenceEqual(replacement)) return false;
        if (Hash(current) != originalSha256)
            throw new InvalidDataException("The virtual file is neither the pinned original nor this patch; no changes were made.");
        byte[] oldIndex = ReadRaw(ggpkPath, IndexPath);
        string bundleName = "localexile-phrecia-" + replacementHash[..16].ToLowerInvariant();
        string bundlePath = "Bundles2/" + bundleName + ".bundle.bin";
        byte[] newBundle = StoredBundle.Write(replacement);
        byte[] newIndex = StoredBundle.Write(BundleIndexRedirect.Create(BundleFile.DecompressWhole(oldIndex, decoder),
            virtualPath, bundleName, replacement.Length));
        var state = new State(ggpkPath, virtualPath, originalSha256, replacementHash, Hash(oldIndex), Hash(newIndex), bundlePath, Hash(newBundle), "Prepared");
        if (File.Exists(Path.Combine(backupFolder, StateName)))
        {
            var previous = LoadState(backupFolder, ggpkPath);
            if (previous.OriginalFileSha256 != state.OriginalFileSha256 || previous.OriginalIndexSha256 != state.OriginalIndexSha256 ||
                previous.VirtualPath != virtualPath || previous.ReplacementSha256 != replacementHash)
                throw new InvalidDataException("Existing patch backups belong to a different source or patch; retain them and restore first.");
            VerifyBackups(backupFolder, previous);
        }
        // Open exclusively before creating backups or writing anything into the pack.
        using var stream = GgpkUndoJournal.Open(ggpkPath, JournalPath(backupFolder), fault);
        using (var pack = new GGPK(stream, leaveOpen: true))
        {
        var indexFile = Get(pack, IndexPath);
        if (Hash(indexFile.Read()) != state.OriginalIndexSha256) throw new IOException("The bundle index changed during patch preparation.");
        Directory.CreateDirectory(backupFolder);
        BackupOnce(Path.Combine(backupFolder, IndexBackup), oldIndex);
        BackupOnce(Path.Combine(backupFolder, FileBackup), current);
        if (pack.Root.TryFindNode(bundlePath, out var existing))
        {
            if (existing is not FileRecord file || !file.Read().AsSpan().SequenceEqual(newBundle))
                throw new InvalidDataException("The patch bundle path is occupied by different data.");
        }
        else
        {
            state = state with { CreatedBundle = true };
            SaveState(backupFolder, state);
            pack.Root.FindOrAddFile(bundlePath, out FileRecord file, newBundle.Length);
            file.Write(newBundle);
        }
        if (!state.CreatedBundle) SaveState(backupFolder, state);
        // Verify the independent decoder before making the index refer to this new record.
        if (!BundleFile.DecompressWhole(Get(pack, bundlePath).Read(), decoder).AsSpan().SequenceEqual(replacement))
            throw new InvalidDataException("The newly written patch bundle failed verification; the original index is retained.");
        pack.Flush();
        stream.Flush();
        indexFile.Write(newIndex);
        pack.RenewHashes(forceRenewRoot: true);
        pack.Flush();
        stream.Flush();
        if (Hash(indexFile.Read()) != state.PatchedIndexSha256) throw new InvalidDataException("Patched index verification failed; use the retained backup.");
        } // GGPK.Dispose/RenewHashes/Flush may write; they must remain inside the journal transaction.
        stream.Commit();
        SaveState(backupFolder, state with { Phase = "Applied" });
        return true;
    }

    public static bool Restore(string ggpkPath, string backupFolder, GgpkUndoJournal.Fault? fault = null)
    {
        ggpkPath = Path.GetFullPath(ggpkPath);
        Recover(ggpkPath, backupFolder);
        var state = LoadState(backupFolder, ggpkPath);
        VerifyBackups(backupFolder, state);
        byte[] original = File.ReadAllBytes(Path.Combine(backupFolder, IndexBackup));
        using var stream = GgpkUndoJournal.Open(ggpkPath, JournalPath(backupFolder), fault);
        bool changed;
        using (var pack = new GGPK(stream, leaveOpen: true))
        {
        var index = Get(pack, IndexPath);
        string current = Hash(index.Read());
        if (current != state.PatchedIndexSha256 && current != state.OriginalIndexSha256)
            throw new InvalidDataException("The index has other changes; restoring this backup would overwrite them. No changes were made.");
        FileRecord? patchBundle = null;
        if (state.CreatedBundle && pack.Root.TryFindNode(state.BundlePath, out var extra))
        {
            if (state.BundlePath != "Bundles2/localexile-phrecia-" + state.ReplacementSha256[..16].ToLowerInvariant() + ".bundle.bin" ||
                extra is not FileRecord file || Hash(file.Read()) != state.ReplacementBundleSha256)
                throw new InvalidDataException("The patch bundle was modified; no restoration changes were made.");
            patchBundle = file;
        }
        changed = current != state.OriginalIndexSha256;
        if (changed) index.Write(original);
        patchBundle?.Remove();
        pack.RenewHashes(forceRenewRoot: true);
        pack.Flush();
        stream.Flush();
        if (Hash(index.Read()) != state.OriginalIndexSha256) throw new InvalidDataException("Restored index did not match its backup.");
        }
        stream.Commit();
        SaveState(backupFolder, state with { Phase = "Restored" });
        return changed;
    }

    public static byte[] OriginalFile(string backupFolder, string expectedHash)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(backupFolder, FileBackup));
        if (Hash(bytes) != expectedHash) throw new InvalidDataException("The original passive graph backup is not the pinned client data.");
        return bytes;
    }

    static State LoadState(string folder, string ggpkPath)
    {
        var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(Path.Combine(folder, StateName)))
            ?? throw new InvalidDataException("Patch state is unreadable; backups were retained.");
        if (!string.Equals(Path.GetFullPath(state.Ggpk), ggpkPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Patch state belongs to a different GGPK.");
        return state;
    }

    static void VerifyBackups(string folder, State state)
    {
        if (Hash(File.ReadAllBytes(Path.Combine(folder, IndexBackup))) != state.OriginalIndexSha256 ||
            Hash(File.ReadAllBytes(Path.Combine(folder, FileBackup))) != state.OriginalFileSha256)
            throw new InvalidDataException("A patch backup is missing or modified; no GGPK changes were made.");
    }

    static FileRecord Get(GGPK pack, string path) => pack.Root.TryFindNode(path, out var node) && node is FileRecord file
        ? file : throw new FileNotFoundException("GGPK record is missing.", path);

    static byte[] ReadRaw(string path, string name)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pack = new GGPK(input, leaveOpen: true);
        return Get(pack, name).Read();
    }

    static void BackupOnce(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("An existing backup has different content.");
            return;
        }
        WriteAtomic(path, bytes);
    }

    static void SaveState(string folder, State state) => WriteAtomic(Path.Combine(folder, StateName), JsonSerializer.SerializeToUtf8Bytes(state));

    static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }
}
