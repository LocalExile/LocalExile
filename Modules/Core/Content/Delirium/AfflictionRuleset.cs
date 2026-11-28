using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

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

    /// <summary>The Rulesets row of <c>AfflictionEncounterRules</c> (row 102 / <c>0x66</c>) in the initial area header of a Mirror map.</summary>
    public static byte? EncounterRow { get; private set; }

    public static ushort? TerrainPlugin { get; private set; }
    public static byte? PreloadGroup { get; private set; }
    public static ushort? FogEnvironmentRow { get; private set; }

    /// <summary>The Rulesets row of <c>AfflictionEndGameRules</c> (row 103 / <c>0x67</c>), sent via S→C <c>0x1f</c> in Simulacrum arenas.</summary>
    public static byte? Row { get; private set; }

    public static void LoadGameData()
    {
        EncounterRow = FindRow("Rulesets", "AfflictionEncounterRules");
        TerrainPlugin = AreaTerrainPlugins.IdOf("AfflictionPlugin");
        PreloadGroup = FindRow("PreloadGroups", "Affliction");
        var environment = ClientTables.TableOrEmpty("Environments").FirstOrDefault(r => r.Text("Id") == "LeagueAfflictionEncounter");
        FogEnvironmentRow = environment.Table is not null && (uint)environment.Index <= ushort.MaxValue ? (ushort)environment.Index : null;
        Row = FindRow("Rulesets", "AfflictionEndGameRules");
    }

    static byte? FindRow(string table, string id) =>
        ClientTables.TableOrEmpty(table).FirstOrDefault(r => r.Text("Id") == id) is { Table: not null } row && row.Index <= byte.MaxValue
            ? (byte)row.Index : null;

    /// <summary>The map Mirror blends the encounter environment in and out with signed two-second transitions.</summary>
    public static byte[]? FogEnvironment(bool active) => FogEnvironmentRow is { } row
        ? new LoginWriter().U16(0x00df).U8(1).U16(row).U8(0).U32(BitConverter.SingleToUInt32Bits(active ? 2f : -2f)).ToArray()
        : null;

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

    /// <summary>Compacts <paramref name="regions"/> to at most <see cref="byte.MaxValue"/> entries without silently
    /// dropping active corridors: keeps the mirror region and largest regions, merges excess smallest regions into their
    /// nearest kept neighbor, and orders by <see cref="FogRegion.DistanceOffset"/> ascending.</summary>
    public static IReadOnlyList<FogRegion> CompactRegions(IReadOnlyList<FogRegion> regions, Func<int, long>? weightOf = null)
    {
        if (regions.Count <= byte.MaxValue) return regions;
        var work = regions.ToArray();
        long Weight(int i) => weightOf is not null ? weightOf(i)
            : Math.Max(1L, work[i].MaxX - work[i].MinX) * Math.Max(1L, work[i].MaxY - work[i].MinY);

        int mirrorIdx = 0;
        float minDist = work[0].DistanceOffset;
        for (int i = 1; i < work.Length; i++)
            if (work[i].DistanceOffset < minDist) { minDist = work[i].DistanceOffset; mirrorIdx = i; }

        var ranked = Enumerable.Range(0, work.Length)
            .Where(i => i != mirrorIdx)
            .OrderByDescending(Weight)
            .ThenBy(i => work[i].DistanceOffset)
            .ToArray();

        var kept = new List<FogRegion>(byte.MaxValue) { work[mirrorIdx] };
        for (int i = 0; i < byte.MaxValue - 1 && i < ranked.Length; i++)
            kept.Add(work[ranked[i]]);

        for (int i = byte.MaxValue - 1; i < ranked.Length; i++)
        {
            var extra = work[ranked[i]];
            int best = 0;
            double bestD2 = double.MaxValue;
            for (int k = 0; k < kept.Count; k++)
            {
                double dx = extra.OriginX - kept[k].OriginX;
                double dy = extra.OriginY - kept[k].OriginY;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = k; }
            }
            var target = kept[best];
            kept[best] = target with
            {
                MinX = Math.Min(target.MinX, extra.MinX),
                MinY = Math.Min(target.MinY, extra.MinY),
                MaxX = Math.Max(target.MaxX, extra.MaxX),
                MaxY = Math.Max(target.MaxY, extra.MaxY),
            };
        }

        kept.Sort(static (a, b) => a.DistanceOffset.CompareTo(b.DistanceOffset));
        return kept;
    }

    /// <summary><see cref="EncounterSnapshot"/> for an explicit Rulesets row.</summary>
    public static byte[] EncounterSnapshot(byte row, bool active, bool ended, float outerElapsed, float innerElapsed,
        float innerDelay, float pauseTimer, bool paused, int mirrorX, int mirrorY, uint mirrorEntityId,
        IReadOnlyList<FogRegion> regions, IReadOnlyList<RewardTile> tiles)
    {
        var compacted = CompactRegions(regions);
        int regionCount = compacted.Count;
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
            var r = compacted[i];
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
