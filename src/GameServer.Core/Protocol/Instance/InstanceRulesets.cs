using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A ruleset's own messages (Agents/docs/protocol/03-area-and-world.md §8): S→C 0x2c9 carries the ruleset's data and C→S
/// 0x2ca its actions. Both are <c>u16 id, u8 Rulesets row, compact-uint n, n bytes</c>; what the bytes mean belongs to the
/// ruleset's row.
/// </summary>
public static class InstanceRulesets
{
    public const ushort EventId = 0x1e, AttachId = InstanceProtocol.AddRulesetId, DataId = 0x2c9, ActionId = 0x2ca;

    /// <summary>S→C 0x1f: attaches the ruleset of <c>Rulesets.dat</c> row <paramref name="row"/> to the instance.</summary>
    public static byte[] Attach(byte row, uint value = 0) => InstanceProtocol.AddRuleset(row, value);

    /// <summary>S→C 0x1e (reader <c>0x1421a44a0</c>): dispatches a named event to every attached ruleset's slot 3
    /// (<c>u16be 0x001e, u32be entityId, u16be byteLen, utf8[byteLen]</c>).</summary>
    public static byte[] Event(string name, uint entityId = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
        if (utf8.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(name));
        return new LoginWriter().U16(EventId).U32(entityId).U16((ushort)utf8.Length).Bytes(utf8).ToArray();
    }

    /// <summary><c>Rulesets.dat</c> row 99 (<c>AtlasPrimordialBossInfluenceRules</c>): drives
    /// <c>atlas_primordial_influence_hud_display</c> (<c>0x140da2910</c>) via S→C 0x2c9 sub-id 0 (<c>0x141750170</c>).</summary>
    public const byte PrimordialInfluenceRulesetRow = 99;

    /// <summary>One active Eldritch Altar mod in <see cref="PrimordialAltarHud"/>: its <c>Mods.dat</c> <c>HASH16</c>
    /// (u16 LE) followed by one signed <see cref="InstanceProtocol.CompactInt"/> per non-null stat on that row
    /// (read by <c>0x142436e50</c> + <c>0x141e4c7e0</c>).</summary>
    public readonly record struct PrimordialAltarMod(ushort ModHash16, IReadOnlyList<int> StatValues);

    /// <summary>S→C 0x2c9 payload for ruleset 99 (<c>AtlasPrimordialBossInfluenceRules</c>, reader <c>0x141750170</c>):
    /// <c>u8 0</c> (sub-id 0) followed by six <c>(u8 count, count × PrimordialAltarMod)</c> lists in client offset order
    /// (<c>+0x158</c>..+<c>0x1d0</c>): player upsides/downsides, eldritch minion upsides/downsides, map boss upsides/downsides.</summary>
    public static byte[] PrimordialAltarHud(
        IReadOnlyList<PrimordialAltarMod> playerUpsides,
        IReadOnlyList<PrimordialAltarMod> playerDownsides,
        IReadOnlyList<PrimordialAltarMod> minionUpsides,
        IReadOnlyList<PrimordialAltarMod> minionDownsides,
        IReadOnlyList<PrimordialAltarMod> bossUpsides,
        IReadOnlyList<PrimordialAltarMod> bossDownsides)
    {
        var w = new BlobWriter().U8(0);
        WritePrimordialModList(w, playerUpsides);
        WritePrimordialModList(w, playerDownsides);
        WritePrimordialModList(w, minionUpsides);
        WritePrimordialModList(w, minionDownsides);
        WritePrimordialModList(w, bossUpsides);
        WritePrimordialModList(w, bossDownsides);
        return Data(PrimordialInfluenceRulesetRow, w.ToArray());
    }

    static void WritePrimordialModList(BlobWriter w, IReadOnlyList<PrimordialAltarMod> mods)
    {
        int count = Math.Min(mods.Count, byte.MaxValue);
        w.U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            var m = mods[i];
            w.U16(m.ModHash16);
            foreach (int value in m.StatValues)
                w.CompactI(value);
        }
    }

    /// <summary>S→C 0x2c9: data for the ruleset of this Rulesets row.</summary>
    public static byte[] Data(byte row, ReadOnlySpan<byte> payload)
    {
        var w = new LoginWriter().U16(DataId).U8(row);
        InstanceProtocol.CompactUInt(w, (uint)payload.Length);
        foreach (byte b in payload) w.U8(b);
        return w.ToArray();
    }

    /// <summary>The length of a C→S 0x2ca at the start of <paramref name="m"/>, or null while it is cut off (or invalid).</summary>
    public static int? ActionLength(ReadOnlySpan<byte> m) =>
        MeasureActionLength(m, out int length) == WireMeasure.Ok ? length : null;

    /// <summary>Measures a C→S 0x2ca ruleset action (writer 0x141fa7c10 / reader 0x141fa7ca0, payload cap 0xffff).</summary>
    internal static WireMeasure MeasureActionLength(ReadOnlySpan<byte> m, out int length)
    {
        length = 0;
        if (m.Length < 4) return WireMeasure.Incomplete;
        int lengthBytes = CompactUIntLength(m[3]);
        if (m.Length < 3 + lengthBytes) return WireMeasure.Incomplete;
        uint payloadLen = CompactUInt(m[3..]);
        if (payloadLen > ushort.MaxValue) return WireMeasure.Invalid;
        int total = 3 + lengthBytes + (int)payloadLen;
        if (m.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    /// <summary>C→S 0x2ca: the Rulesets row and the action's bytes.</summary>
    public static (byte Row, byte[] Payload) ReadAction(ReadOnlySpan<byte> m)
    {
        if (m.Length < 4 || (m[0] << 8 | m[1]) != ActionId) throw new InvalidDataException("not a 0x2ca ruleset action");
        int lengthBytes = CompactUIntLength(m[3]);
        if (m.Length < 3 + lengthBytes) throw new InvalidDataException("0x2ca ruleset action is cut off");
        uint n = CompactUInt(m[3..]);
        if (m.Length < 3 + lengthBytes + n) throw new InvalidDataException("0x2ca ruleset action is cut off");
        return (m[2], m.Slice(3 + lengthBytes, (int)n).ToArray());
    }

    static int CompactUIntLength(byte first) => first < 0x80 ? 1 : first < 0xc0 ? 2 : first < 0xe0 ? 3 : first < 0xf0 ? 4 : 5;

    static uint CompactUInt(ReadOnlySpan<byte> b) => CompactUIntLength(b[0]) switch
    {
        1 => b[0],
        2 => (uint)((b[0] & 0x3f) << 8 | b[1]),
        3 => (uint)((b[0] & 0x1f) << 16 | b[1] << 8 | b[2]),
        4 => (uint)((b[0] & 0x0f) << 24 | b[1] << 16 | b[2] << 8 | b[3]),
        _ => (uint)(b[1] << 24 | b[2] << 16 | b[3] << 8 | b[4]),
    };
}
