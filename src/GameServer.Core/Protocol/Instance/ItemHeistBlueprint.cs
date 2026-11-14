using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

public sealed record HeistJob(byte Row, byte Level);
public sealed record HeistRoom(byte Row, bool Revealed);
public sealed record HeistWing(bool Revealed, IReadOnlyList<HeistJob> Jobs, IReadOnlyList<byte> Crew, IReadOnlyList<HeistRoom> Rooms)
{
    public IReadOnlyList<HeistJob> Jobs { get; init => field = Owned.List(value); } = Owned.List(Jobs);
    public IReadOnlyList<byte> Crew { get; init => field = Owned.List(value); } = Owned.List(Crew);
    public IReadOnlyList<HeistRoom> Rooms { get; init => field = Owned.List(value); } = Owned.List(Rooms);
}

/// <summary>HeistBlueprint reader 0x1424afe90 and wing reader 0x1424c1bb0. Indexes are client table rows.</summary>
public sealed record HeistBlueprintState(uint Seed, byte AreaLevel, byte Format, bool Confirmed, IReadOnlyList<HeistWing> Wings)
{
    public IReadOnlyList<HeistWing> Wings { get; init => field = Owned.List(value); } = Owned.List(Wings);

    internal void Write(BlobWriter writer)
    {
        if (Wings.Count > 127 || Wings.Any(w => w.Jobs.Count > 127 || w.Crew.Count > 255 || w.Rooms.Count > 255
            || w.Rooms.Any(r => r.Row > 127))) throw new ArgumentException("Blueprint exceeds native count fields");
        writer.U32(Seed).U8(AreaLevel).U8(Format).U8((byte)(Wings.Count * 2 | (Confirmed ? 1 : 0)));
        foreach (var wing in Wings)
        {
            writer.U8((byte)(wing.Jobs.Count * 2 | (wing.Revealed ? 1 : 0)));
            foreach (var job in wing.Jobs) writer.U8(job.Row).U8(job.Level);
            writer.U8((byte)wing.Crew.Count);
            foreach (byte member in wing.Crew) writer.U8(member);
            writer.U8((byte)wing.Rooms.Count);
            foreach (var room in wing.Rooms) writer.U8((byte)(room.Row * 2 | (room.Revealed ? 1 : 0)));
        }
    }
}
