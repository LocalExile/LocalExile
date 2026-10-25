using System.Buffers.Binary;
using System.Diagnostics;
using GameServer.Data.Ggpk;

namespace GameServer.Data.Tests;

public sealed class GgpkUndoJournalTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "localexile-undo-" + Guid.NewGuid().ToString("N"));
    static readonly byte[] Original = Enumerable.Range(0, 512).Select(i => (byte)(i * 31 + 7)).ToArray();
    public GgpkUndoJournalTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    string FilePath => Path.Combine(_directory, "Content.ggpk");
    string Journal => VirtualFilePatch.JournalPath(VirtualFilePatch.BackupFolder(FilePath));
    sealed class Injected : IOException;

    static void Mutate(Stream stream)
    {
        stream.Position = 0; stream.Write(new byte[] { 99, 98, 97 });
        stream.Position = 9; stream.WriteByte(87);
        stream.Position = 32; stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, 0, 4).GetAwaiter().GetResult();
        stream.Position = 80; stream.WriteAsync(new byte[] { 7, 6, 5 }.AsMemory()).GetAwaiter().GetResult();
        stream.SetLength(97); stream.SetLength(187);
        stream.Position = 300; stream.Write(new byte[] { 4, 5, 6 }.AsSpan());
        stream.SetLength(12); stream.SetLength(320);
    }

    [Fact]
    public void Every_write_and_length_boundary_recovers_byte_for_byte_in_a_fresh_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var stage in new[] { GgpkUndoJournal.Stage.BeforeRecord, GgpkUndoJournal.Stage.BeforeMutation,
                     GgpkUndoJournal.Stage.PartialMutation, GgpkUndoJournal.Stage.AfterMutation })
            for (int operation = 1; operation <= 9; operation++)
            {
                File.WriteAllBytes(FilePath, Original);
                Assert.Throws<Injected>(() =>
                {
                    using var stream = GgpkUndoJournal.Open(FilePath, Journal, b =>
                    { if (b.Operation == operation && b.Stage == stage) throw new Injected(); });
                    Mutate(stream); stream.Commit();
                });
                RecoverInFreshProcess(FilePath);
                Assert.Equal(Original, File.ReadAllBytes(FilePath));
                Assert.False(File.Exists(Journal));
            }
    }

    [Fact]
    public void A_committed_leftover_is_cleaned_without_undo_and_interrupted_recovery_is_repeatable()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllBytes(FilePath, Original);
        Assert.Throws<Injected>(() =>
        {
            using var stream = GgpkUndoJournal.Open(FilePath, Journal, b => { if (b.Stage == GgpkUndoJournal.Stage.AfterCommit) throw new Injected(); });
            Mutate(stream); stream.Commit();
        });
        byte[] committed = File.ReadAllBytes(FilePath);
        RecoverInFreshProcess(FilePath);
        Assert.Equal(committed, File.ReadAllBytes(FilePath));
        File.WriteAllBytes(FilePath, Original);
        using (var stream = GgpkUndoJournal.Open(FilePath, Journal)) Mutate(stream); // no commit
        Assert.Throws<Injected>(() => GgpkUndoJournal.Recover(FilePath, Journal, b =>
        { if (b.Stage == GgpkUndoJournal.Stage.RecoveryPartial) throw new Injected(); }));
        RecoverInFreshProcess(FilePath);
        Assert.Equal(Original, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void Torn_final_frames_including_full_length_torn_footers_do_not_block_earlier_undo()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllBytes(FilePath, Original);
        Assert.Throws<Injected>(() =>
        {
            using var stream = GgpkUndoJournal.Open(FilePath, Journal, b =>
            { if (b.Operation == 2 && b.Stage == GgpkUndoJournal.Stage.BeforeMutation) throw new Injected(); });
            stream.Write(new byte[] { 99, 98 }); stream.Write(new byte[] { 77, 76 });
        });
        byte[] journal = File.ReadAllBytes(Journal), mutated = File.ReadAllBytes(FilePath);
        int first = 12 + BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(8)) + 36;
        int second = checked(first + 12 + (int)BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(first + 4)) + 36);
        for (int length = second; length < journal.Length; length++)
        {
            File.WriteAllBytes(FilePath, mutated); File.WriteAllBytes(Journal, journal[..length]);
            Assert.True(GgpkUndoJournal.Recover(FilePath, Journal)); Assert.Equal(Original, File.ReadAllBytes(FilePath));
        }
        File.WriteAllBytes(FilePath, mutated); byte[] torn = journal.ToArray(); torn[^1] ^= 1; File.WriteAllBytes(Journal, torn);
        RecoverInFreshProcess(FilePath); Assert.Equal(Original, File.ReadAllBytes(FilePath));
        foreach (int clearLength in new[] { 12, journal.Length - second })
        {
            File.WriteAllBytes(FilePath, mutated); byte[] zeros = journal.ToArray(); zeros.AsSpan(second, clearLength).Clear(); File.WriteAllBytes(Journal, zeros);
            RecoverInFreshProcess(FilePath); Assert.Equal(Original, File.ReadAllBytes(FilePath));
        }
        // Corruption in an earlier frame must refuse all rollback writes.
        File.WriteAllBytes(FilePath, mutated); byte[] corrupt = journal.ToArray(); corrupt[first + 49] ^= 1; File.WriteAllBytes(Journal, corrupt);
        Assert.Throws<InvalidDataException>(() => GgpkUndoJournal.Recover(FilePath, Journal));
        Assert.Equal(mutated, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void Corrupt_middle_lengths_cannot_hide_later_complete_writes_outside_the_fingerprint()
    {
        if (!OperatingSystem.IsWindows()) return;
        byte[] original = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(FilePath, original);
        using (var stream = GgpkUndoJournal.Open(FilePath, Journal))
        {
            stream.Position = 6000; stream.Write(new byte[] { 42, 43 });
            stream.Position = 7000; stream.Write(new byte[] { 52, 53 });
            stream.Position = 7100; stream.Write(new byte[] { 62, 63 });
        }
        byte[] source = File.ReadAllBytes(Journal), mutated = File.ReadAllBytes(FilePath);
        int first = 12 + BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(8)) + 36;
        foreach (long length in new[] { long.MaxValue - 49, (long)source.Length - first - 12 - 36 })
        {
            byte[] corrupt = source.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(corrupt.AsSpan(first + 4), length); File.WriteAllBytes(Journal, corrupt);
            Assert.Throws<InvalidDataException>(() => GgpkUndoJournal.Recover(FilePath, Journal));
            Assert.Equal(mutated, File.ReadAllBytes(FilePath)); Assert.Equal(corrupt, File.ReadAllBytes(Journal));
        }
    }

    [Fact]
    public void Unpublished_headers_at_every_byte_boundary_are_archived_without_touching_the_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllBytes(FilePath, Original);
        using (GgpkUndoJournal.Open(FilePath, Journal)) { }
        byte[] header = File.ReadAllBytes(Journal); File.Delete(Journal);
        for (int length = 0; length <= header.Length; length++)
        {
            File.WriteAllBytes(Journal + ".prepare", header[..length]);
            Assert.False(GgpkUndoJournal.Recover(FilePath, Journal));
            Assert.Equal(Original, File.ReadAllBytes(FilePath));
            Assert.False(File.Exists(Journal + ".prepare"));
        }
    }

    [Fact]
    public void A_different_file_at_the_same_path_is_refused_and_readonly_checks_refuse_a_pending_journal()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllBytes(FilePath, Original);
        using (var stream = GgpkUndoJournal.Open(FilePath, Journal)) stream.WriteByte(42);
        Assert.Throws<InvalidDataException>(() => GgpkUndoJournal.EnsureNoPending(Journal));
        File.Move(FilePath, FilePath + ".previous"); File.WriteAllBytes(FilePath, Original);
        Assert.Throws<InvalidDataException>(() => GgpkUndoJournal.Recover(FilePath, Journal));
        Assert.Equal(Original, File.ReadAllBytes(FilePath));
        Assert.True(File.Exists(Journal));
    }

    internal static void RecoverInFreshProcess(string path)
    {
        var result = RunTool("--recover", path);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    internal static (int ExitCode, string Output) RunTool(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) { start.WorkingDirectory = directory.FullName; break; }
        foreach (string arg in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "GameServer.Data.Tests.runtimeconfig.json"),
                     typeof(global::PassiveGraphToolMarker).Assembly.Location }.Concat(args)) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("Recovery CLI did not exit."); }
        return (child.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
}
