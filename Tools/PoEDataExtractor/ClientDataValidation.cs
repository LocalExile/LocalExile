using System.ComponentModel;
using GameServer.Data.Dat;
using GameServer.Data.Ggpk;
using LibGGPK3;

namespace PoEDataExtractor;

/// <summary>A versioned, read-only readiness result, not a certificate of every asset's integrity.</summary>
public sealed record ArchiveValidationReport(bool Ok, string Message, string? Reason = null, string? Details = null,
    string? IndexSha256 = null, int FileCount = 0, int BundleCount = 0, string[]? CheckedTables = null)
{
    public int Protocol => 1;
}

public static class ClientDataValidation
{
    static readonly string[] RequiredTables = ["data/worldareas.datc64", "data/baseitemtypes.datc64", "data/stats.datc64"];
    static readonly GgpkReadLimits Limits = new(IndexBytes: 128 << 20, BundleBytes: 256 << 20,
        FileBytes: 64 << 20, ChunkBytes: 16 << 20, IndexRecords: 4_000_000);

    public static ArchiveValidationReport Check(string archive, Func<IOodleDecoder> createDecoder)
    {
        string stage = "archive";
        IOodleDecoder? decoder = null;
        try
        {
            // A read-only handle also prevents a concurrent writer on Windows. Never use the GGPK path constructor here.
            using var readable = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (readable.Length < 28) throw new GgpkFormatException("GGPK header is missing or truncated");
            stage = "decoder";
            decoder = createDecoder();
            stage = "index";
            using var fs = GameFileSystem.Open(archive, decoder, bundleCacheBytes: 32 << 20, limits: Limits);
            foreach (string table in RequiredTables)
            {
                stage = table;
                var data = fs.ReadFile(table);
                if (DatFile.Parse(data).RowCount <= 0)
                    throw new DatFormatException($"{table} contains no rows");
            }
            return new(true, "The index and sampled required tables are readable.", IndexSha256: fs.IndexSha256,
                FileCount: fs.Index.FileCount, BundleCount: fs.Index.BundleFiles.Count, CheckedTables: [.. RequiredTables]);
        }
        catch (UnauthorizedAccessException e) { return Fail("access-denied", "The operating system refused access to the client.", e); }
        catch (FileNotFoundException e) { return Fail(stage == "archive" ? "missing-client-file" : "required-data-unreadable", "A required client file could not be found.", e); }
        catch (DirectoryNotFoundException e) { return Fail("missing-client-file", "The client folder could not be found.", e); }
        catch (GgpkFormatException e)
        {
            string message = e.Reason switch
            {
                "validation-limit" => "The quick check exceeded its read budget; this does not establish that the client is damaged.",
                "decoder-error" => "The decompression worker could not complete the check.",
                "invalid-index" => "The client's game-data index is empty, truncated or invalid.",
                _ => "The game archive or a required bundle could not be read."
            };
            return Fail(e.Reason, message, e);
        }
        catch (DatFormatException e) { return Fail("required-data-unreadable", "A sampled required game table could not be read.", e); }
        catch (Win32Exception e) { return Fail("validation-unavailable", "The decompression worker could not be started.", e); }
        catch (GGPKBrokenException e) { return Fail("invalid-archive", "The game archive has invalid container records.", e); }
        catch (EndOfStreamException e) { return Fail("invalid-archive", "The game archive ended unexpectedly.", e); }
        catch (InvalidDataException e) { return Fail("invalid-archive", "The game archive could not be opened.", e); }
        catch (IOException e) { return Fail(stage == "decoder" ? "validation-unavailable" : "client-unreadable", "The client could not be read; close any updater using it and retry.", e); }
        finally { (decoder as IDisposable)?.Dispose(); }

        ArchiveValidationReport Fail(string reason, string message, Exception error) =>
            new(false, message, reason, $"{archive} [{stage}]: {error.GetType().Name}: {error.Message}");
    }
}
