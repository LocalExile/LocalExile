using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GameServer.Data.Ggpk;

/// <summary>
/// Write-ahead byte-range undo for the Windows client GGPK. Recovery never opens or parses the GGPK index: it validates
/// the journal and file identity, restores before-images in reverse order, and restores each operation's previous length.
/// A GGPK object using this stream must be completely disposed before <see cref="Commit"/>.
/// </summary>
public sealed class GgpkUndoJournal : Stream
{
    public enum Stage { BeforeRecord, BeforeMutation, PartialMutation, AfterMutation, AfterCommit, RecoveryBefore, RecoveryPartial, RecoveryAfter }
    public readonly record struct Boundary(int Operation, string Kind, Stage Stage);
    public delegate void Fault(Boundary boundary);
    const uint RecordMagic = 0x31554e44, Footer = 0x454e4455, HeaderFooter = 0x48445255;
    const int MetaLength = 37, BufferSize = 65536;
    static readonly byte[] Magic = "LEXUNDO1"u8.ToArray();
    readonly FileStream _file, _journal;
    readonly string _journalPath;
    readonly Fault? _fault;
    readonly object _gate = new();
    int _sequence;
    bool _poisoned, _committed, _disposed;

    sealed record Identity(uint Volume, uint FileHigh, uint FileLow, ulong Creation);
    sealed record Header(int Version, string Path, long OriginalLength, Identity Identity, int PrefixLength, string PrefixSha256);
    sealed record Entry(byte Kind, int Sequence, long BeforeLength, long Offset, long Amount, long DataLength, long DataPosition);
    sealed record Parsed(Header Header, IReadOnlyList<Entry> Entries, bool Terminal, long ValidEnd);

    GgpkUndoJournal(FileStream file, FileStream journal, string journalPath, Fault? fault)
    { _file = file; _journal = journal; _journalPath = journalPath; _fault = fault; }

    public static void EnsureNoPending(string journalPath)
    {
        if (File.Exists(journalPath) || File.Exists(journalPath + ".prepare"))
            throw new InvalidDataException("An interrupted GGPK write journal is present. Close the client and run passive-graphs --recover before reading the index.");
    }

    public static GgpkUndoJournal Open(string filePath, string journalPath, Fault? fault = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The client GGPK journal requires Windows file identity.");
        filePath = Path.GetFullPath(filePath);
        journalPath = Path.GetFullPath(journalPath);
        EnsureNoPending(journalPath);
        var file = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            int prefixLength = (int)Math.Min(4096, file.Length);
            byte[] prefix = new byte[prefixLength]; file.ReadExactly(prefix); file.Position = 0;
            var header = new Header(1, filePath, file.Length, FileIdentity(file), prefixLength, Convert.ToHexString(SHA256.HashData(prefix)));
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
            byte[] bytes = new byte[12 + json.Length];
            Magic.CopyTo(bytes, 0); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), json.Length); json.CopyTo(bytes, 12);
            string preparation = journalPath + ".prepare";
            using (var staging = new FileStream(preparation, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                staging.Write(bytes); staging.Write(SHA256.HashData(bytes));
                Span<byte> footer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(footer, HeaderFooter); staging.Write(footer);
                staging.Flush(flushToDisk: true);
            }
            // Publish a complete, durable header before the first target-file mutation.
            if (!MoveFileEx(preparation, journalPath, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var journal = new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            journal.Position = journal.Length;
            return new(file, journal, journalPath, fault);
        }
        catch { file.Dispose(); throw; }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            Writable();
            if (buffer.Length == 0) return;
            int sequence = ++_sequence;
            try
            {
                Notify(sequence, "Write", Stage.BeforeRecord);
                long offset = _file.Position, before = _file.Length;
                long overlap = Math.Min(buffer.Length, Math.Max(0, before - offset));
                Record(1, sequence, before, offset, buffer.Length, overlap);
                Notify(sequence, "Write", Stage.BeforeMutation);
                if (_fault is null) _file.Write(buffer);
                else
                {
                    int first = Math.Max(1, buffer.Length / 2);
                    _file.Write(buffer[..first]); Notify(sequence, "Write", Stage.PartialMutation); _file.Write(buffer[first..]);
                }
                Notify(sequence, "Write", Stage.AfterMutation);
            }
            catch { _poisoned = true; throw; }
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) { Span<byte> bytes = stackalloc byte[1]; bytes[0] = value; Write(bytes); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        lock (_gate)
        {
            Writable();
            long before = _file.Length;
            if (before == value) return;
            int sequence = ++_sequence;
            try
            {
                Notify(sequence, "SetLength", Stage.BeforeRecord);
                Record(2, sequence, before, value, value, Math.Max(0, before - value));
                Notify(sequence, "SetLength", Stage.BeforeMutation);
                if (_fault is not null)
                {
                    _file.SetLength(before + (value - before) / 2);
                    Notify(sequence, "SetLength", Stage.PartialMutation);
                }
                _file.SetLength(value);
                Notify(sequence, "SetLength", Stage.AfterMutation);
            }
            catch { _poisoned = true; throw; }
        }
    }

    void Record(byte kind, int sequence, long before, long offset, long amount, long dataLength)
    {
        byte[] header = FrameHeader(kind, sequence, before, offset, amount, dataLength);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _journal.Write(header); hash.AppendData(header);
        long position = _file.Position;
        try
        {
            _file.Position = offset;
            byte[] buffer = new byte[BufferSize];
            for (long remaining = dataLength; remaining > 0;)
            {
                int n = (int)Math.Min(buffer.Length, remaining); _file.ReadExactly(buffer.AsSpan(0, n));
                _journal.Write(buffer, 0, n); hash.AppendData(buffer.AsSpan(0, n)); remaining -= n;
            }
        }
        finally { _file.Position = position; }
        _journal.Write(hash.GetHashAndReset());
        Span<byte> footer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(footer, Footer); _journal.Write(footer);
        _journal.Flush(flushToDisk: true);
    }

    public void Commit()
    {
        lock (_gate)
        {
            Writable();
            _file.Flush(flushToDisk: true);
            Record(3, ++_sequence, 0, 0, 0, 0);
            _committed = true;
            Notify(_sequence, "Commit", Stage.AfterCommit);
            _journal.Dispose();
            File.Delete(_journalPath);
        }
    }

    /// <summary>Recover before any GameFileSystem/GGPK parsing. A committed leftover is cleaned, never undone.</summary>
    public static bool Recover(string filePath, string journalPath, Fault? fault = null)
    {
        if (!File.Exists(journalPath) && !File.Exists(journalPath + ".prepare")) return false;
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(journalPath))
        {
            // Even a truncated unpublished header is safe: no target write is allowed until the atomic rename.
            // Archive this owned preparation artifact without touching (or parsing) the target file.
            ArchivePreparation(journalPath);
            return false;
        }
        using var file = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var journal = new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        var parsed = Parse(journal);
        ValidateIdentity(file, filePath, parsed.Header);
        if (!parsed.Terminal)
        {
            VerifyOriginalPrefix(file, journal, parsed);
            byte[] buffer = new byte[BufferSize];
            foreach (var entry in parsed.Entries.Reverse())
            {
                fault?.Invoke(new(entry.Sequence, "Undo", Stage.RecoveryBefore));
                // Extending first makes a previously truncated before-image writable.
                if (file.Length < entry.BeforeLength) file.SetLength(entry.BeforeLength);
                file.Position = entry.Offset; journal.Position = entry.DataPosition;
                bool notified = false;
                for (long remaining = entry.DataLength; remaining > 0;)
                {
                    int n = (int)Math.Min(buffer.Length, remaining); journal.ReadExactly(buffer.AsSpan(0, n));
                    file.Write(buffer, 0, n); remaining -= n;
                    if (!notified) { fault?.Invoke(new(entry.Sequence, "Undo", Stage.RecoveryPartial)); notified = true; }
                }
                if (!notified) fault?.Invoke(new(entry.Sequence, "Undo", Stage.RecoveryPartial));
                file.SetLength(entry.BeforeLength);
                fault?.Invoke(new(entry.Sequence, "Undo", Stage.RecoveryAfter));
            }
            file.SetLength(parsed.Header.OriginalLength);
            file.Flush(flushToDisk: true);
            if (PrefixHash(file, parsed.Header.PrefixLength) != parsed.Header.PrefixSha256)
                throw new InvalidDataException("GGPK recovery did not restore the original file prefix; journal retained.");
            // Retain the complete before-images until a durable recovery-complete marker exists. A crash while
            // appending that marker simply repeats the idempotent undo on the next startup.
            journal.SetLength(parsed.ValidEnd); journal.Position = parsed.ValidEnd;
            byte[] completed = FrameHeader(4, parsed.Entries.Count + 1, 0, 0, 0, 0);
            journal.Write(completed); journal.Write(SHA256.HashData(completed));
            Span<byte> ending = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(ending, Footer); journal.Write(ending);
            journal.Flush(flushToDisk: true);
        }
        journal.Dispose();
        File.Delete(journalPath);
        ArchivePreparation(journalPath);
        return !parsed.Terminal;
    }

    static void ArchivePreparation(string journalPath)
    {
        string staging = journalPath + ".prepare";
        if (File.Exists(staging)) File.Move(staging, staging + ".unpublished-" + Guid.NewGuid().ToString("N"));
    }

    static Parsed Parse(FileStream journal)
    {
        var header = ReadHeader(journal);
        var entries = new List<Entry>();
        long validEnd = journal.Position;
        bool terminal = false;
        int sequence = 0;
        long expectedLength = header.OriginalLength;
        byte[] buffer = new byte[BufferSize];
        while (journal.Position < journal.Length)
        {
            long start = journal.Position;
            if (journal.Length - start < 12) break; // torn final frame: no corresponding mutation could start
            byte[] prefix = new byte[12]; journal.ReadExactly(prefix);
            if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != RecordMagic)
            {
                if (!HasValidFrameAfter(journal, start + 1)) break;
                throw new InvalidDataException("Corrupt GGPK journal frame marker before a later complete frame.");
            }
            long bodyLength = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(4));
            if (bodyLength < MetaLength || bodyLength > long.MaxValue - 48)
            {
                if (!HasValidFrameAfter(journal, start + 1)) break;
                throw new InvalidDataException("Corrupt GGPK journal frame length before a later complete frame.");
            }
            if (bodyLength > journal.Length - journal.Position - 36)
            {
                if (!HasValidFrameAfter(journal, start + 1)) break;
                throw new InvalidDataException("Corrupt GGPK journal length hides a later complete frame.");
            }
            byte[] meta = new byte[MetaLength]; journal.ReadExactly(meta);
            long dataPosition = journal.Position;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(prefix); hash.AppendData(meta);
            for (long remaining = bodyLength - MetaLength; remaining > 0;)
            {
                int n = (int)Math.Min(buffer.Length, remaining); journal.ReadExactly(buffer.AsSpan(0, n)); hash.AppendData(buffer.AsSpan(0, n)); remaining -= n;
            }
            byte[] expected = new byte[32]; journal.ReadExactly(expected);
            byte[] ending = new byte[4]; journal.ReadExactly(ending);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()) || BinaryPrimitives.ReadUInt32LittleEndian(ending) != Footer)
            {
                // A final frame may have reached its allocated length before all data/footer bytes were written.
                // Such a frame never passed durable completion, so its target mutation could not have begun.
                if (!HasValidFrameAfter(journal, start + 1)) break;
                throw new InvalidDataException("Corrupt GGPK journal frame checksum; no recovery writes were made.");
            }
            byte kind = meta[0]; int serial = BinaryPrimitives.ReadInt32LittleEndian(meta.AsSpan(1));
            long before = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(5)), offset = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(13));
            long amount = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(21)), dataLength = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(29));
            if (terminal || serial != ++sequence || before < 0 || offset < 0 || amount < 0 || dataLength < 0 || bodyLength != MetaLength + dataLength)
                throw new InvalidDataException("Invalid GGPK journal operation metadata.");
            if (kind == 1)
            {
                if (before != expectedLength || amount > int.MaxValue || offset > long.MaxValue - amount || dataLength != Math.Min(amount, Math.Max(0, before - offset)))
                    throw new InvalidDataException("Invalid GGPK journal write range.");
                expectedLength = Math.Max(before, offset + amount);
            }
            else if (kind == 2)
            {
                if (before != expectedLength || offset != amount || dataLength != Math.Max(0, before - amount))
                    throw new InvalidDataException("Invalid GGPK journal truncation range.");
                expectedLength = amount;
            }
            else if (kind is 3 or 4)
            {
                if (before != 0 || offset != 0 || amount != 0 || dataLength != 0) throw new InvalidDataException("Invalid GGPK journal commit marker.");
                terminal = true;
            }
            else throw new InvalidDataException("Unknown GGPK journal operation.");
            if (kind is not (3 or 4)) entries.Add(new(kind, serial, before, offset, amount, dataLength, dataPosition));
            validEnd = journal.Position;
        }
        return new(header, entries, terminal, validEnd);
    }

    // An allocated final sector can contain zero/partial frame-header bytes after a crash. A later fully checksummed
    // frame distinguishes corrupt middle data (refuse) from an unpublished final frame (ignore, no target write ran).
    static bool HasValidFrameAfter(FileStream journal, long start)
    {
        long saved = journal.Position;
        try
        {
            byte[] scan = new byte[BufferSize + 3];
            for (long position = start; position < journal.Length; position += BufferSize)
            {
                int count = (int)Math.Min(scan.Length, journal.Length - position);
                journal.Position = position; journal.ReadExactly(scan.AsSpan(0, count));
                for (int i = 0; i + 4 <= count && i < BufferSize; i++)
                    if (BinaryPrimitives.ReadUInt32LittleEndian(scan.AsSpan(i)) == RecordMagic && ValidFrame(journal, position + i)) return true;
            }
            return false;
        }
        finally { journal.Position = saved; }
    }

    static bool ValidFrame(FileStream journal, long offset)
    {
        if (journal.Length - offset < 12 + MetaLength + 36) return false;
        journal.Position = offset;
        byte[] prefix = new byte[12]; journal.ReadExactly(prefix);
        long length = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(4));
        if (length < MetaLength || length > journal.Length - journal.Position - 36) return false;
        byte[] meta = new byte[MetaLength]; journal.ReadExactly(meta);
        if (meta[0] is < 1 or > 4 || BinaryPrimitives.ReadInt32LittleEndian(meta.AsSpan(1)) <= 0 ||
            BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(29)) != length - MetaLength) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(prefix); hash.AppendData(meta);
        byte[] buffer = new byte[BufferSize];
        for (long left = length - MetaLength; left > 0;)
        {
            int n = (int)Math.Min(buffer.Length, left); journal.ReadExactly(buffer.AsSpan(0, n)); hash.AppendData(buffer.AsSpan(0, n)); left -= n;
        }
        byte[] expected = new byte[32]; journal.ReadExactly(expected);
        byte[] end = new byte[4]; journal.ReadExactly(end);
        return BinaryPrimitives.ReadUInt32LittleEndian(end) == Footer && CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset());
    }

    static Header ReadHeader(Stream journal)
    {
        byte[] prefix = new byte[12]; journal.ReadExactly(prefix);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (!prefix.AsSpan(0, 8).SequenceEqual(Magic) || length is < 1 or > 65536) throw new InvalidDataException("Invalid GGPK journal header.");
        byte[] json = new byte[length]; journal.ReadExactly(json);
        byte[] digest = new byte[32]; journal.ReadExactly(digest);
        byte[] end = new byte[4]; journal.ReadExactly(end);
        byte[] hashed = [.. prefix, .. json];
        if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(hashed)) || BinaryPrimitives.ReadUInt32LittleEndian(end) != HeaderFooter)
            throw new InvalidDataException("Corrupt GGPK journal header checksum.");
        var header = JsonSerializer.Deserialize<Header>(json) ?? throw new InvalidDataException("Invalid GGPK journal header data.");
        if (header.Version != 1 || header.OriginalLength < 0 || header.PrefixLength != Math.Min(4096, header.OriginalLength) || header.PrefixSha256.Length != 64)
            throw new InvalidDataException("Unsupported GGPK journal header values.");
        return header;
    }

    static void VerifyOriginalPrefix(FileStream file, FileStream journal, Parsed parsed)
    {
        byte[] prefix = new byte[parsed.Header.PrefixLength];
        file.Position = 0;
        int n = (int)Math.Min(prefix.Length, file.Length); file.ReadExactly(prefix.AsSpan(0, n));
        foreach (var entry in parsed.Entries.Reverse())
        {
            if (entry.Offset < prefix.Length)
            {
                int count = (int)Math.Min(entry.DataLength, prefix.Length - entry.Offset);
                journal.Position = entry.DataPosition; journal.ReadExactly(prefix.AsSpan((int)entry.Offset, count));
            }
            if (entry.BeforeLength < prefix.Length) prefix.AsSpan((int)entry.BeforeLength).Clear();
        }
        if (Convert.ToHexString(SHA256.HashData(prefix)) != parsed.Header.PrefixSha256)
            throw new InvalidDataException("The journal cannot reconstruct the original GGPK fingerprint; no recovery writes were made.");
    }

    static string PrefixHash(FileStream file, int size)
    {
        file.Position = 0; byte[] bytes = new byte[size]; file.ReadExactly(bytes); return Convert.ToHexString(SHA256.HashData(bytes));
    }

    static byte[] FrameHeader(byte kind, int sequence, long before, long offset, long amount, long dataLength)
    {
        byte[] bytes = new byte[12 + MetaLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RecordMagic); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), checked(MetaLength + dataLength));
        bytes[12] = kind; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(17), before); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(25), offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(33), amount); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(41), dataLength);
        return bytes;
    }

    static void ValidateIdentity(FileStream file, string path, Header header)
    {
        if (!string.Equals(path, header.Path, StringComparison.OrdinalIgnoreCase) || FileIdentity(file) != header.Identity)
            throw new InvalidDataException("The GGPK journal belongs to a different file; no recovery writes were made.");
    }

    static Identity FileIdentity(FileStream file)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GGPK recovery requires Windows file identity.");
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Volume, info.IndexHigh, info.IndexLow, ((ulong)info.CreationHigh << 32) | info.CreationLow);
    }

    void Notify(int operation, string kind, Stage stage) => _fault?.Invoke(new(operation, kind, stage));
    void Writable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned || _committed) throw new IOException("The GGPK journal transaction is no longer writable.");
    }

    public override bool CanRead => !_disposed && _file.CanRead;
    public override bool CanSeek => !_disposed && _file.CanSeek;
    public override bool CanWrite => !_disposed && !_poisoned && !_committed;
    public override long Length => _file.Length;
    public override long Position { get => _file.Position; set => _file.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _file.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
    public override void Flush() => _file.Flush(flushToDisk: true);
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing) { try { _journal.Dispose(); } finally { _file.Dispose(); } }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MoveFileEx(string existing, string replacement, uint flags);
}
