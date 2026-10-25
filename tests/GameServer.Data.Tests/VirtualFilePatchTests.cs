using System.Text;
using GameServer.Data.Ggpk;
using LibGGPK3;
using LibGGPK3.Records;

namespace GameServer.Data.Tests;

public class VirtualFilePatchTests
{
    const string Target = "metadata/test.psg", Other = "data/unrelated.datc64";
    static readonly byte[] Original = Encoding.UTF8.GetBytes("original graph bytes"), Unrelated = Encoding.UTF8.GetBytes("another file's untouched bytes");
    sealed class Injected : IOException;

    [Fact]
    public void Cli_apply_and_check_refuse_wrong_manifest_revision_and_changed_ordinary_source_before_writes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "localexile-patch-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "Content.ggpk"), manifest = Path.Combine(root, "passive-skill-graphs.json");
            CreateFixture(path, addWrongNormalGraph: true);
            byte[] before = File.ReadAllBytes(path);
            string json = """
                {"revision":"3.29.3b","clientPatchVersion":1,
                 "normal":{"sha256":"82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32"},
                 "originalPhreciaSha256":"9A6D43A997BE44B53FC13FB6CED2E9F935AA11F8E4C4940CE8ECF9300A2FC1BC"}
                """;
            foreach (string mode in new[] { "--apply", "--check" })
            {
                File.WriteAllText(manifest, json.Replace("3.29.3b", "future"));
                var revision = GgpkUndoJournalTests.RunTool(mode, path, manifest);
                Assert.NotEqual(0, revision.ExitCode); Assert.Contains("revision/patch version", revision.Output);
                File.WriteAllText(manifest, json);
                var normal = GgpkUndoJournalTests.RunTool(mode, path, manifest);
                Assert.NotEqual(0, normal.ExitCode); Assert.Contains("installed ordinary PSG", normal.Output);
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.False(Directory.Exists(VirtualFilePatch.BackupFolder(path)));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Every_real_GGPK_write_boundary_in_apply_and_restore_recovers_the_entire_pack_in_a_fresh_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "localexile-patch-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] replacement = Enumerable.Range(0, 4097).Select(i => (byte)(i * 7)).ToArray();
            foreach (bool restore in new[] { false, true })
            {
                string baseline = Path.Combine(root, "baseline-" + restore); Directory.CreateDirectory(baseline);
                string baselinePack = Path.Combine(baseline, "Content.ggpk"); CreateFixture(baselinePack);
                string baselineBackup = VirtualFilePatch.BackupFolder(baselinePack);
                var operations = new List<int>();
                void Capture(GgpkUndoJournal.Boundary b) { if (b.Stage == GgpkUndoJournal.Stage.BeforeRecord) operations.Add(b.Operation); }
                if (restore)
                {
                    VirtualFilePatch.Apply(baselinePack, Target, replacement, VirtualFilePatch.Hash(Original), baselineBackup, new ManagedOodleDecoder());
                    VirtualFilePatch.Restore(baselinePack, baselineBackup, Capture);
                }
                else VirtualFilePatch.Apply(baselinePack, Target, replacement, VirtualFilePatch.Hash(Original), baselineBackup, new ManagedOodleDecoder(), Capture);
                Assert.NotEmpty(operations);
                foreach (int operation in operations)
                    foreach (var stage in new[] { GgpkUndoJournal.Stage.BeforeRecord, GgpkUndoJournal.Stage.BeforeMutation,
                                 GgpkUndoJournal.Stage.PartialMutation, GgpkUndoJournal.Stage.AfterMutation })
                    {
                        string directory = Path.Combine(root, $"{restore}-{operation}-{stage}"); Directory.CreateDirectory(directory);
                        string path = Path.Combine(directory, "Content.ggpk"), backup = VirtualFilePatch.BackupFolder(path);
                        CreateFixture(path);
                        if (restore) VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, new ManagedOodleDecoder());
                        byte[] before = File.ReadAllBytes(path);
                        void Fail(GgpkUndoJournal.Boundary b) { if (b.Operation == operation && b.Stage == stage) throw new Injected(); }
                        Assert.Throws<Injected>(() =>
                        {
                            if (restore) VirtualFilePatch.Restore(path, backup, Fail);
                            else VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, new ManagedOodleDecoder(), Fail);
                        });
                        GgpkUndoJournalTests.RecoverInFreshProcess(path);
                        Assert.Equal(before, File.ReadAllBytes(path));
                        using var fs = GameFileSystem.Open(path, new ManagedOodleDecoder());
                        Assert.Equal(Unrelated, fs.ReadFile(Other));
                        Assert.Equal(restore ? replacement : Original, fs.ReadFile(Target));
                    }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Equivalent_foreign_bundle_descriptors_are_refused_and_a_reused_unindexed_record_is_never_removed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "localexile-patch-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] replacement = [9, 8, 7, 6];
            string name = "localexile-phrecia-" + VirtualFilePatch.Hash(replacement)[..16].ToLowerInvariant();
            int id = 0;
            foreach (string foreign in new[] { name.ToUpperInvariant(), "Bundles2/" + name + ".bundle.bin", "bundles2/" + name.ToUpperInvariant() + ".BUNDLE.BIN" })
            {
                string path = Path.Combine(root, "foreign-" + id++ + ".ggpk"), backup = path + ".backup";
                CreateFixture(path, foreign);
                byte[] before = File.ReadAllBytes(path);
                Assert.Throws<InvalidDataException>(() => VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, new ManagedOodleDecoder()));
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            string reused = Path.Combine(root, "reused.ggpk"), reuseBackup = reused + ".backup";
            CreateFixture(reused);
            string bundlePath = "Bundles2/" + name + ".bundle.bin";
            byte[] bytes = StoredBundle.Write(replacement);
            using (var pack = new GGPK(reused)) { pack.Root.FindOrAddFile(bundlePath, out var file, bytes.Length); file.Write(bytes); }
            Assert.True(VirtualFilePatch.Apply(reused, Target, replacement, VirtualFilePatch.Hash(Original), reuseBackup, new ManagedOodleDecoder()));
            Assert.True(VirtualFilePatch.Restore(reused, reuseBackup));
            Assert.Equal(bytes, Raw(reused, bundlePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(262144)] [InlineData(262145)] [InlineData(524291)]
    public void Stored_Oodle_bundle_round_trips_through_the_independent_decoder(int size)
    {
        byte[] bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 37 + 5)).ToArray();
        byte[] bundle = StoredBundle.Write(bytes);
        Assert.Equal(bytes, BundleFile.DecompressWhole(bundle, new ManagedOodleDecoder()));
    }

    [Fact]
    public void Synthetic_pack_redirect_restore_and_reapplication_preserve_every_other_virtual_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "localexile-virtual-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Content.ggpk"), backup = Path.Combine(directory, "backup");
            CreateFixture(path);
            byte[] oldIndex = Raw(path, "Bundles2/_.index.bin"), oldBundle = Raw(path, "Bundles2/original.bundle.bin");
            byte[] replacement = Encoding.UTF8.GetBytes("a replacement graph with a different size");
            var decoder = new ManagedOodleDecoder();
            Assert.True(VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, decoder));
            long patchedLength = new FileInfo(path).Length;
            Assert.False(VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, decoder));
            Assert.Equal(patchedLength, new FileInfo(path).Length);
            using (var fs = GameFileSystem.Open(path, decoder))
            {
                Assert.Equal(replacement, fs.ReadFile(Target));
                Assert.Equal(Unrelated, fs.ReadFile(Other));
            }
            Assert.Equal(oldBundle, Raw(path, "Bundles2/original.bundle.bin"));
            Assert.Equal(Original, VirtualFilePatch.OriginalFile(backup, VirtualFilePatch.Hash(Original)));
            Assert.True(VirtualFilePatch.Restore(path, backup));
            Assert.Equal(oldIndex, Raw(path, "Bundles2/_.index.bin"));
            using (var fs = GameFileSystem.Open(path, decoder)) Assert.Equal(Original, fs.ReadFile(Target));
            Assert.False(VirtualFilePatch.Restore(path, backup));
            Assert.True(VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, decoder));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Busy_pack_wrong_original_and_intervening_index_changes_are_refused_without_overwriting_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "localexile-virtual-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Content.ggpk"), backup = Path.Combine(directory, "backup");
            CreateFixture(path);
            byte[] replacement = [1, 2, 3];
            var decoder = new ManagedOodleDecoder();
            Assert.Throws<InvalidDataException>(() => VirtualFilePatch.Apply(path, Target, replacement, "wrong", backup, decoder));
            Assert.False(Directory.Exists(backup));
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(VirtualFilePatch.Matches(path, Target, Original, decoder));
                Assert.Throws<IOException>(() => VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, decoder));
            }
            Assert.False(Directory.Exists(backup));
            Assert.True(VirtualFilePatch.Apply(path, Target, replacement, VirtualFilePatch.Hash(Original), backup, decoder));
            byte[] edited = Raw(path, "Bundles2/_.index.bin"); edited[^1] ^= 1;
            using (var pack = new GGPK(path)) Find(pack, "Bundles2/_.index.bin").Write(edited);
            Assert.Throws<InvalidDataException>(() => VirtualFilePatch.Restore(path, backup));
            Assert.Equal(edited, Raw(path, "Bundles2/_.index.bin"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static byte[] Raw(string path, string name)
    {
        using var pack = new GGPK(path);
        return Find(pack, name).Read();
    }

    static FileRecord Find(GGPK pack, string path)
    {
        Assert.True(pack.Root.TryFindNode(path, out var node));
        return Assert.IsType<FileRecord>(node);
    }

    static void CreateFixture(string path, string? foreignBundleName = null, bool addWrongNormalGraph = false)
    {
        using (var output = new BinaryWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
        {
            output.Write(28u); output.Write(0x4b504747u); output.Write(3u); output.Write(28L); output.Write(0L);
            output.Write(50u); output.Write(0x52494450u); output.Write(1); output.Write(0); output.Write(new byte[32]); output.Write((ushort)0);
        }
        using var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, true))
        {
            byte[] name = Encoding.UTF8.GetBytes("original");
            writer.Write(foreignBundleName is null ? 1 : 2); writer.Write(name.Length); writer.Write(name); writer.Write(Original.Length + Unrelated.Length);
            if (foreignBundleName is not null)
            {
                byte[] foreign = Encoding.UTF8.GetBytes(foreignBundleName); writer.Write(foreign.Length); writer.Write(foreign); writer.Write(Unrelated.Length);
            }
            writer.Write(addWrongNormalGraph ? 3 : 2);
            foreach (var (file, offset, size) in new[] { (Target, 0, Original.Length), (Other, Original.Length, Unrelated.Length) })
            {
                writer.Write(MurmurHash64A.Hash(Encoding.UTF8.GetBytes(file)));
                bool foreign = foreignBundleName is not null && file == Other;
                writer.Write(foreign ? 1 : 0); writer.Write(foreign ? 0 : offset); writer.Write(size);
            }
            if (addWrongNormalGraph)
            {
                writer.Write(MurmurHash64A.Hash(Encoding.UTF8.GetBytes("metadata/passiveskillgraph.psg")));
                writer.Write(0); writer.Write(0); writer.Write(Original.Length);
            }
            writer.Write(0); // no path directory records needed for hash lookup
            writer.Write(StoredBundle.Write([]));
        }
        using var pack = new GGPK(path);
        byte[] bundle = StoredBundle.Write([.. Original, .. Unrelated]);
        pack.Root.FindOrAddFile("Bundles2/original.bundle.bin", out var original, bundle.Length); original.Write(bundle);
        byte[] packedIndex = StoredBundle.Write(index.ToArray());
        pack.Root.FindOrAddFile("Bundles2/_.index.bin", out var indexFile, packedIndex.Length); indexFile.Write(packedIndex);
        if (foreignBundleName is not null)
        {
            string foreignPath = foreignBundleName.StartsWith("Bundles2/", StringComparison.OrdinalIgnoreCase) ? foreignBundleName : "Bundles2/" + foreignBundleName;
            if (!foreignPath.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase)) foreignPath += ".bundle.bin";
            byte[] foreign = StoredBundle.Write(Unrelated); pack.Root.FindOrAddFile(foreignPath, out var other, foreign.Length); other.Write(foreign);
        }
        pack.RenewHashes(forceRenewRoot: true);
    }
}
