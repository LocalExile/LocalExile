using System.Buffers.Binary;
using LibGGPK3;
using LibGGPK3.Records;

namespace GameServer.Data.Ggpk;

/// <summary>Check record metadata before LibGGPK allocates directory arrays or reads a name from damaged input.</summary>
sealed class ReadOnlyGgpk(Stream stream) : GGPK(stream, false)
{
    public override BaseRecord ReadRecord(long offset)
    {
        if (offset < 0 || offset > baseStream.Length - 8)
            throw new GgpkFormatException($"GGPK record offset {offset} is outside the archive");
        return base.ReadRecord(offset);
    }

    public override BaseRecord ReadRecord()
    {
        lock (baseStream)
        {
            long offset = baseStream.Position;
            Span<byte> header = stackalloc byte[16];
            baseStream.ReadExactly(header[..8]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int tag = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (length < 8 || length > baseStream.Length - offset)
                throw new GgpkFormatException($"GGPK record at {offset} has invalid length {length}");
            if ((offset == 0 && (tag != GGPKRecord.Tag || Record is not null))
                || (Record is not null && offset == Record.RootDirectoryOffset && tag != DirectoryRecord.Tag))
                throw new GgpkFormatException("GGPK header or root record has an invalid type");
            if (tag == GGPKRecord.Tag)
            {
                if (length < 28) throw new GgpkFormatException("GGPK header record is truncated");
            }
            else if (tag is DirectoryRecord.Tag or FileRecord.Tag)
            {
                int fixedBytes = tag == DirectoryRecord.Tag ? 48 : 44;
                if (length < fixedBytes) throw new GgpkFormatException($"GGPK node at {offset} is truncated");
                baseStream.ReadExactly(header[8..16]);
                int name = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
                int entries = tag == DirectoryRecord.Tag ? BinaryPrimitives.ReadInt32LittleEndian(header[12..]) : 0;
                long nameBytes = (long)name * (Version == 4 ? 4 : 2);
                if (name <= 0 || entries < 0 || nameBytes > length - fixedBytes
                    || (long)entries * 12 > length - fixedBytes - nameBytes)
                    throw new GgpkFormatException($"GGPK node at {offset} has invalid name or directory sizes");
                if (name > 4096 || entries > 4_000_000)
                    throw new GgpkFormatException("validation-limit", $"GGPK node at {offset} exceeds metadata read limits");
            }
            else
                throw new GgpkFormatException($"GGPK record at {offset} has an invalid tag or size");
            baseStream.Position = offset;
            return base.ReadRecord();
        }
    }
}
