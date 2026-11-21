using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Deepwater;

/// <summary>Ruleset 172 DeepwaterHubRules (the client's ClientDeepwaterHub) messages: the C→S 0x2ca ops of the deck's
/// windows (the Chart and Voyage windows, the Loot window) and the S→C 0x2c9 data the Voyage window reads (reader
/// <c>0x141683030</c>; Agents/docs/protocol/03-area-and-world.md §8). Both are little-endian after the op or mode byte.</summary>
internal static class DeepwaterHubRuleset
{
    /// <summary>The Rulesets row of DeepwaterHubRules (sent with 0x1f on The Sovereign and in the encounter areas).</summary>
    public static readonly Lazy<byte?> Row = new(() =>
        ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == "DeepwaterHubRules") is { Table: not null } row && row.Index <= byte.MaxValue
            ? (byte)row.Index : null);

    /// <summary>The ops, named after ClientDeepwaterHub's lambdas (H). There is no op 3 on this row.</summary>
    public enum Op : byte
    {
        /// <summary>Begin Voyage: u8 n, n × u8 (per board slot the chart's x in inventory 104, 0xff empty).</summary>
        OpenMission = 0,
        /// <summary>The Chart window's Descend: the chart in inventory 103.</summary>
        Open1x1Mission = 1,
        /// <summary>Vesper's crafting: u32, u32.</summary>
        DoCraft = 2,
        /// <summary>A drag inside inventory 104: u8 from, u8 to.</summary>
        MoveStoredChart = 4,
        /// <summary>Right click on a stored chart: u8 x in 104.</summary>
        RotateStoredChart = 5,
        /// <summary>u8 x in 104.</summary>
        DeleteStoredChart = 6,
        RerollBorderModifiers = 7,
        /// <summary>Yes on the "the collection will be emptied" popup (after mode 2).</summary>
        ConfirmEnterMission = 8,
        /// <summary>The Loot window's Take All.</summary>
        TakeAllLoot = 9,
    }

    /// <summary>A C→S 0x2ca for this ruleset: its op and the bytes after it; null for another ruleset's action.</summary>
    public static (Op Op, byte[] Args)? Read(byte[] message)
    {
        var (row, payload) = InstanceRulesets.ReadAction(message);
        if (Row.Value != row || payload.Length < 1) return null;
        return ((Op)payload[0], payload[1..]);
    }

    /// <summary>The reroll cost that disables the Voyage window's reroll button (<c>0x140419720</c>).</summary>
    public const uint NoReroll = uint.MaxValue;

    /// <summary>Mode 1: u32 the next reroll cost (ClientStrings 8470, compared with the player's Dead Man's Sulphur;
    /// <see cref="NoReroll"/> disables the button), u8 k, k × (u16 Mods HASH16, one compact int per stat): the 12 border
    /// modifiers in segment order (the window uses them only when exactly 12 are present).</summary>
    public static byte[]? Border(uint cost, IReadOnlyList<ItemMod> mods)
    {
        if (Row.Value is not { } row) return null;
        var w = new BlobWriter().U8(1).U32(cost).U8((byte)Math.Min(mods.Count, 255));
        foreach (var m in mods.Take(255))
        {
            w.U16(m.Hash);
            foreach (int v in m.Values) w.CompactI(v);
        }
        return InstanceRulesets.Data(row, w.ToArray());
    }

    /// <summary>Mode 2: the HUD asks "Starting a new Voyage will remove all items from the capsule collection inventory"
    /// (ClientStrings 8445); Yes sends <see cref="Op.ConfirmEnterMission"/>.</summary>
    public static byte[]? ConfirmEnter() => Row.Value is { } row ? InstanceRulesets.Data(row, [2]) : null;
}
