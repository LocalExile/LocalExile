extern alias LocalExileLauncher;

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Data.Ggpk;
using LibGGPK3;
using LibGGPK3.Records;
using LocalExileLauncher::LocalExile;
using PoEDataExtractor;

namespace GameServer.Gateway.Tests;

public class ClientValidationTests
{
    [Fact]
    public async Task Readiness_is_read_only_and_helper_failures_are_distinct_from_unusable_game_data()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-client-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string archive = Path.Combine(folder, "Content.ggpk");
            WriteArchive(archive, emptyIndex: false);
            byte[] before = File.ReadAllBytes(archive);
            DateTime written = File.GetLastWriteTimeUtc(archive);
            ArchiveValidationReport report;
            using (var readOnly = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
                report = ClientDataValidation.Check(archive, () => new IdentityDecoder());
            Assert.True(report.Ok, report.Details);
            Assert.Equal(3, report.FileCount);
            Assert.Equal(3, report.CheckedTables!.Length);
            Assert.Equal(before, File.ReadAllBytes(archive));
            Assert.Equal(written, File.GetLastWriteTimeUtc(archive));
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var parsed = ClientArchiveCheck.Parse(0, JsonSerializer.Serialize(report, options), "");
            Assert.Equal(report.IndexSha256, parsed.IndexSha256);

            string executable = Path.Combine(folder, "fixture.exe");
            File.WriteAllBytes(executable, [1, 2, 3]);
            using (var lease = ClientReadLease.Open(folder, executable))
            {
                lease.AssertUnchanged();
                if (OperatingSystem.IsWindows())
                    Assert.Throws<IOException>(() => { using var writer = new FileStream(archive, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
                else
                {
                    File.SetLastWriteTimeUtc(archive, written.AddSeconds(-10));
                    Assert.Equal("client-changed", Assert.Throws<LauncherException>(lease.AssertUnchanged).Reason);
                }
            }
            File.SetLastWriteTimeUtc(archive, written);
            string checkedImage = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)));
            File.WriteAllBytes(executable, [4, 5, 6]);
            using (var changed = ClientReadLease.Open(folder, executable))
                Assert.Equal("client-changed", Assert.Throws<LauncherException>(() => changed.AssertExecutable(checkedImage)).Reason);

            var worker = ClientDataValidation.Check(archive, () => throw new Win32Exception("worker unavailable"));
            Assert.Equal("validation-unavailable", worker.Reason);
            Assert.Equal(ExitCode.Unexpected, Assert.Throws<LauncherException>(() =>
                ClientArchiveCheck.Parse(1, JsonSerializer.Serialize(worker, options), "")).Code);
            foreach (string output in new[] { "", "usage: old tool", "{}", "{\"protocol\":2,\"ok\":true}",
                "{\"protocol\":1,\"ok\":true,\"indexSha256\":\"x\",\"fileCount\":3,\"bundleCount\":1}" })
                Assert.Equal("validation-unavailable", Assert.Throws<LauncherException>(() => ClientArchiveCheck.Parse(0, output, "")).Reason);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    ClientArchiveCheck.CaptureAsync(new ProcessStartInfo("must-not-start"), cancelled.Token));
            }

            WriteArchive(archive, emptyIndex: true);
            byte[] badBefore = File.ReadAllBytes(archive);
            var bad = ClientDataValidation.Check(archive, () => new IdentityDecoder());
            Assert.False(bad.Ok);
            Assert.Equal("invalid-index", bad.Reason);
            var failure = Assert.Throws<LauncherException>(() => ClientArchiveCheck.Parse(1, JsonSerializer.Serialize(bad, options), ""));
            Assert.Equal(ExitCode.Client, failure.Code);
            Assert.Contains("official PoE launcher", failure.Message);
            Assert.Equal(badBefore, File.ReadAllBytes(archive));
            Assert.Equal("validation-unavailable", Assert.Throws<LauncherException>(() =>
                ClientArchiveCheck.Parse(0, JsonSerializer.Serialize(bad, options), "")).Reason);
            Assert.Equal("missing-client-file", ClientDataValidation.Check(Path.Combine(folder, "missing.ggpk"),
                () => throw new Xunit.Sdk.XunitException("missing archive reached the decoder")).Reason);
            var malformed = new byte[78];
            BinaryPrimitives.WriteInt32LittleEndian(malformed, 28);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(4), GGPKRecord.Tag);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(8), 3);
            BinaryPrimitives.WriteInt64LittleEndian(malformed.AsSpan(12), 28);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(28), 50);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(32), DirectoryRecord.Tag);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(36), 1);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(40), int.MaxValue);
            File.WriteAllBytes(archive, malformed);
            Assert.Equal("invalid-archive", ClientDataValidation.Check(archive, () => new IdentityDecoder()).Reason);
            WriteArchive(archive, emptyIndex: false);
            using (var container = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var reader = new BinaryReader(container, Encoding.UTF8, leaveOpen: true);
                container.Position = 12;
                long directory = reader.ReadInt64();
                container.Position = directory + 8;
                int nameChars = reader.ReadInt32();
                container.Position = directory + 48 + nameChars * 2 + 4;
                using var writer = new BinaryWriter(container, Encoding.UTF8, leaveOpen: true);
                writer.Write(0L); // A child points to the GGPK header, not a tree node.
            }
            Assert.Equal("invalid-index", ClientDataValidation.Check(archive, () => new IdentityDecoder()).Reason);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(40), 0);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(32), FileRecord.Tag);
            File.WriteAllBytes(archive, malformed);
            Assert.Equal("invalid-archive", ClientDataValidation.Check(archive, () => new IdentityDecoder()).Reason);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    static void WriteArchive(string path, bool emptyIndex)
    {
        // A generated empty GGPK root, then generated index/table records. No game files or native codec are fixtures.
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write(28); output.Write(GGPKRecord.Tag); output.Write(3); output.Write(28L); output.Write(0L);
            output.Write(50); output.Write(DirectoryRecord.Tag); output.Write(1); output.Write(0);
            output.Write(new byte[32]); output.Write((ushort)0);
        }
        using var ggpk = new GGPK(path);
        byte[] table = [1, 0, 0, 0, 0, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];
        var payload = Enumerable.Range(0, 3).SelectMany(_ => table).ToArray();
        using var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1); writer.Write(4); writer.Write(Encoding.UTF8.GetBytes("test")); writer.Write(payload.Length);
            writer.Write(3);
            int i = 0;
            foreach (string name in new[] { "data/worldareas.datc64", "data/baseitemtypes.datc64", "data/stats.datc64" })
            {
                writer.Write(MurmurHash64A.Hash(Encoding.UTF8.GetBytes(name)));
                writer.Write(0); writer.Write(i++ * table.Length); writer.Write(table.Length);
            }
            writer.Write(0);
        }
        void Add(string name, byte[] content)
        {
            ggpk.Root.FindOrAddFile(name, out var record, content.Length);
            record.Write(content);
        }
        Add("Bundles2/_.index.bin", Bundle(emptyIndex ? [] : index.ToArray()));
        Add("Bundles2/test.bundle.bin", Bundle(payload));
    }

    static byte[] Bundle(byte[] payload)
    {
        var result = new byte[60 + (payload.Length == 0 ? 0 : 4 + payload.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(result, payload.Length);
        if (payload.Length != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(36), 1);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(60), payload.Length);
            payload.CopyTo(result, 64);
        }
        return result;
    }

    sealed class IdentityDecoder : IOodleDecoder
    {
        public byte[] Decompress(ReadOnlyMemory<byte> compressed, int size) => compressed.ToArray();
    }
}
