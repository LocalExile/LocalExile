using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Deepwater;

/// <summary>Ruleset 171 DeepwaterRules (the client's ClientDeepwater) messages: S→C 0x2c9 data read by
/// <c>0x141683c40</c>, the first byte a mode and the fields little-endian, and the C→S 0x2ca actions
/// (Agents/docs/protocol/03-area-and-world.md §8).</summary>
internal static class DeepwaterRuleset
{
    /// <summary>The Rulesets row of DeepwaterRules, sent (0x1f) only in the encounter areas.</summary>
    public static byte? Row { get; private set; }

    public static void LoadGameData() => Row =
        ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == "DeepwaterRules") is { Table: not null } row && row.Index <= byte.MaxValue
            ? (byte)row.Index : null;

    public enum Op : byte { PlaceLantern = 0, SendItemsToSurface = 2, RequestExtraction = 3 }

    static byte[]? Data(params byte[] payload) => Row is { } row ? InstanceRulesets.Data(row, payload) : null;

    static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
    static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    /// <summary>Mode 0: a lantern anchor (i32 x, i32 y, u32), appended to the client's list that placement snaps to. No
    /// client code was found reading the u32: the lantern's object id here.</summary>
    public static byte[]? Anchor(int x, int y, uint id) => Data([0, .. I32(x), .. I32(y), .. U32(id)]);
    /// <summary>Mode 1: the Sulphur gauge (u32, u32), filled a/b in five steps.</summary>
    public static byte[]? Sulphur(uint collected, uint cap) => Data([1, .. U32(collected), .. U32(cap)]);
    /// <summary>Mode 2: (u8 placed, u8 total): the HUD's lantern counter, the final-lantern banner when placed + 1 == total
    /// and total &gt; 2, and the client's own placement gate (placed &lt; total).</summary>
    public static byte[]? Lanterns(int placed, int total) =>
        Data(2, (byte)Math.Clamp(placed, 0, 255), (byte)Math.Clamp(total, 0, 255));
    /// <summary>Mode 3: the collapse state (3 hides the HUD; 0 → 1 or 2 plays DeepwaterCollapseStart).</summary>
    public static byte[]? Collapse(byte state) => Data(3, state);
    /// <summary>Mode 4: the collection chest's item count (its model's amount_looted).</summary>
    public static byte[]? CollectionCount(uint count) => Data([4, .. U32(count)]);
    /// <summary>Mode 5: the run succeeded (completion popup, Valerie's success line).</summary>
    public static byte[]? Success() => Data(5);
    /// <summary>Mode 6: the run failed (Valerie's fail line).</summary>
    public static byte[]? Failure() => Data(6);
    /// <summary>Mode 7: closes the capsule window.</summary>
    public static byte[]? CloseCapsule() => Data(7);

    /// <summary>A C→S 0x2ca for ruleset 171: the op and, for PlaceLantern, the grid cell (i32 x, i32 y LE); null for
    /// another ruleset's action or a payload too short for its op.</summary>
    public static (Op Op, int X, int Y)? Read(byte[] message)
    {
        var (row, payload) = InstanceRulesets.ReadAction(message);
        if (Row != row || payload.Length < 1) return null;
        var op = (Op)payload[0];
        if (op != Op.PlaceLantern) return (op, 0, 0);
        if (payload.Length < 9) return null;
        return (op, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1)), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5)));
    }
}
