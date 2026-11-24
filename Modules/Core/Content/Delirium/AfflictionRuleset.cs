using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

/// <summary>Ruleset 103 AfflictionEndGameRules (factory <c>0x141764c70</c>): S→C 0x2c9 data read by
/// <c>0x141745400</c> (Agents/docs/protocol/21-hideouts.md, 03-area-and-world.md §8). Mode 1 passes its
/// reward-tile list through <c>0x141fbacc0</c> → <c>0x142172a00</c> into instance <c>+0xda58</c>, which the
/// Delirium reward HUD (<c>0x1403341a0</c>) renders.</summary>
public static class AfflictionRuleset
{
    /// <summary>The Rulesets row of AfflictionEndGameRules (row 103 / 0x67), sent via S→C 0x1f in Simulacrum arenas.</summary>
    public static byte? Row { get; private set; }

    public static void LoadGameData() => Row =
        ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == "AfflictionEndGameRules") is { Table: not null } row && row.Index <= byte.MaxValue
            ? (byte)row.Index : null;

    /// <summary>One reward tile entry (13 bytes LE): AfflictionRewardTypes id, bar progress, bar goal, completed count.</summary>
    public readonly record struct RewardTile(byte Type, uint Current, uint Max, uint Count);

    /// <summary>Mode 1: reward tiles for the loaded Rulesets row, or null when the row is unavailable.</summary>
    public static byte[]? RewardTiles(IReadOnlyList<RewardTile> entries) =>
        Row is { } row ? RewardTiles(row, entries) : null;

    /// <summary>Mode 1: reward tiles for an explicit Rulesets row (u8 mode 1, u8 count, count × 13-byte LE entries).</summary>
    public static byte[] RewardTiles(byte row, IReadOnlyList<RewardTile> entries)
    {
        int count = Math.Min(entries.Count, byte.MaxValue);
        var payload = new byte[2 + count * 13];
        payload[0] = 1;
        payload[1] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            var entry = entries[i];
            int offset = 2 + i * 13;
            payload[offset] = entry.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 1, 4), entry.Current);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 5, 4), entry.Max);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 9, 4), entry.Count);
        }
        return InstanceRulesets.Data(row, payload);
    }

    /// <summary>Mode 1 with zero entries: clears and hides the Delirium reward HUD.</summary>
    public static byte[]? ClearRewardTiles() => RewardTiles([]);

    /// <summary>Mode 1 with zero entries for an explicit Rulesets row.</summary>
    public static byte[] ClearRewardTiles(byte row) => RewardTiles(row, []);
}
