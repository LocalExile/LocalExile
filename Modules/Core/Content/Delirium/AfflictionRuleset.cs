using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

/// <summary>
/// Delirium client rulesets verified against 3.29.3c/d <c>Client/Game.exe</c>:
/// <list type="bullet">
/// <item><description>Row 102 (<c>0x66</c>) <c>AfflictionEncounterRules</c> (ctor <c>0x141742140</c>,
/// S→C <c>0x2c9</c> slot 8 reader <c>0x1417442e0</c>): map Delirium Mirror fog waves, region grid, reward tiles, and pause state.</description></item>
/// <item><description>Row 103 (<c>0x67</c>) <c>AfflictionEndGameRules</c> (factory <c>0x141764c70</c>, reader <c>0x141745400</c>):
/// Simulacrum reward HUD (mode 1 feeds instance <c>+0xda58</c> via <c>0x141fbacc0</c> → <c>0x142172a00</c>).</description></item>
/// </list>
/// </summary>
public static class AfflictionRuleset
{
    /// <summary>C→S <c>0x0260</c> (2 bytes, senders <c>0x140deb800</c> / <c>0x142172b40</c>): player clicks the HUD button to end a map Delirium Mirror encounter.</summary>
    public const ushort EndEncounterPacketId = 0x0260;

    /// <summary>The Rulesets row of <c>AfflictionEncounterRules</c> (row 102 / <c>0x66</c>), sent via S→C <c>0x1f</c> in maps with a Delirium Mirror.</summary>
    public static byte? EncounterRow { get; private set; }

    /// <summary>The Rulesets row of <c>AfflictionEndGameRules</c> (row 103 / <c>0x67</c>), sent via S→C <c>0x1f</c> in Simulacrum arenas.</summary>
    public static byte? Row { get; private set; }

    public static void LoadGameData()
    {
        EncounterRow = FindRow("AfflictionEncounterRules");
        Row = FindRow("AfflictionEndGameRules");
    }

    static byte? FindRow(string id) =>
        ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == id) is { Table: not null } row && row.Index <= byte.MaxValue
            ? (byte)row.Index : null;

    /// <summary>One reward tile entry (13 bytes LE): <c>AfflictionRewardTypes</c> id, bar progress, bar goal, completed count.</summary>
    public readonly record struct RewardTile(byte Type, uint Current, uint Max, uint Count);

    /// <summary>One fog-distance region entry (28 bytes LE in <c>AfflictionEncounterRules</c> mode 0 at <c>0x141744530</c>).</summary>
    public readonly record struct FogRegion(int MinX, int MinY, int MaxX, int MaxY, int OriginX, int OriginY, float DistanceOffset = 0f);

    /// <summary>Mode 1: reward tiles for <c>AfflictionEndGameRules</c> (Simulacrum), or null when the row is unavailable.</summary>
    public static byte[]? RewardTiles(IReadOnlyList<RewardTile> entries) =>
        Row is { } row ? RewardTiles(row, entries) : null;

    /// <summary>Mode 1: reward tiles for an explicit Rulesets row (<c>u8 mode 1</c>, <c>u8 count</c>, <c>count × 13-byte LE</c> entries).</summary>
    public static byte[] RewardTiles(byte row, IReadOnlyList<RewardTile> entries)
    {
        int count = Math.Min(entries.Count, byte.MaxValue);
        var payload = new byte[2 + count * 13];
        payload[0] = 1;
        WriteRewardTiles(payload.AsSpan(1), entries, count);
        return InstanceRulesets.Data(row, payload);
    }

    /// <summary>Mode 1 with zero entries: clears and hides the Delirium reward HUD.</summary>
    public static byte[]? ClearRewardTiles() => RewardTiles([]);

    /// <summary>Mode 1 with zero entries for an explicit Rulesets row.</summary>
    public static byte[] ClearRewardTiles(byte row) => RewardTiles(row, []);

    /// <summary>
    /// <c>AfflictionEncounterRules</c> (row <c>0x66</c>) mode 0 full snapshot verified at <c>0x1417442e0..0x141744652</c>:
    /// <c>u8 0</c>, <c>u8 active</c>, <c>u8 ended</c>, <c>f32 LE outerElapsed</c>, <c>f32 LE innerElapsed</c>,
    /// <c>f32 LE innerDelay</c> (<c>+inf</c> = <c>0x7f800000</c> until dissipating), <c>f32 LE pauseTimer</c>, <c>u8 paused</c>,
    /// <c>i32 LE mirrorX</c>, <c>i32 LE mirrorY</c>, <c>u32 LE mirrorEntityId</c>, <c>u8 regionCount</c>,
    /// <c>regionCount × 28 B LE</c>, then <c>u8 tileCount</c> + <c>tileCount × 13 B LE</c>.
    /// </summary>
    public static byte[]? EncounterSnapshot(bool active, bool ended, float outerElapsed, float innerElapsed,
        float innerDelay, float pauseTimer, bool paused, int mirrorX, int mirrorY, uint mirrorEntityId,
        IReadOnlyList<FogRegion> regions, IReadOnlyList<RewardTile> tiles) =>
        EncounterRow is { } row
            ? EncounterSnapshot(row, active, ended, outerElapsed, innerElapsed, innerDelay, pauseTimer, paused,
                mirrorX, mirrorY, mirrorEntityId, regions, tiles)
            : null;

    /// <summary><see cref="EncounterSnapshot"/> for an explicit Rulesets row.</summary>
    public static byte[] EncounterSnapshot(byte row, bool active, bool ended, float outerElapsed, float innerElapsed,
        float innerDelay, float pauseTimer, bool paused, int mirrorX, int mirrorY, uint mirrorEntityId,
        IReadOnlyList<FogRegion> regions, IReadOnlyList<RewardTile> tiles)
    {
        int regionCount = Math.Min(regions.Count, byte.MaxValue);
        int tileCount = Math.Min(tiles.Count, byte.MaxValue);
        var payload = new byte[33 + regionCount * 28 + 1 + tileCount * 13];
        var span = payload.AsSpan();
        int o = 0;
        span[o++] = 0; // mode 0
        span[o++] = active ? (byte)1 : (byte)0;
        span[o++] = ended ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o, 4), outerElapsed); o += 4;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o, 4), innerElapsed); o += 4;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o, 4), innerDelay); o += 4;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o, 4), pauseTimer); o += 4;
        span[o++] = paused ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), mirrorX); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), mirrorY); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(o, 4), mirrorEntityId); o += 4;
        span[o++] = (byte)regionCount;
        for (int i = 0; i < regionCount; i++)
        {
            var r = regions[i];
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), r.MinX); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), r.MinY); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), r.MaxX); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), r.MaxY); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), r.OriginX); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), r.OriginY); o += 4;
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o, 4), r.DistanceOffset); o += 4;
        }
        WriteRewardTiles(span.Slice(o), tiles, tileCount);
        return InstanceRulesets.Data(row, payload);
    }

    /// <summary><c>AfflictionEncounterRules</c> mode 1: reward tile update for map Delirium Mirror.</summary>
    public static byte[]? EncounterRewardTiles(IReadOnlyList<RewardTile> entries) =>
        EncounterRow is { } row ? RewardTiles(row, entries) : null;

    /// <summary><c>AfflictionEncounterRules</c> mode 2 (<c>0x141744680</c>): updates fog pause timer and paused flag (6 bytes).</summary>
    public static byte[]? EncounterPause(float pauseTimer, bool paused) =>
        EncounterRow is { } row ? EncounterPause(row, pauseTimer, paused) : null;

    /// <summary><see cref="EncounterPause"/> for an explicit Rulesets row.</summary>
    public static byte[] EncounterPause(byte row, float pauseTimer, bool paused)
    {
        var payload = new byte[6];
        payload[0] = 2;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(1, 4), pauseTimer);
        payload[5] = paused ? (byte)1 : (byte)0;
        return InstanceRulesets.Data(row, payload);
    }

    static void WriteRewardTiles(Span<byte> target, IReadOnlyList<RewardTile> entries, int count)
    {
        target[0] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            var entry = entries[i];
            int offset = 1 + i * 13;
            target[offset] = entry.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset + 1, 4), entry.Current);
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset + 5, 4), entry.Max);
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset + 9, 4), entry.Count);
        }
    }
}
