using System.Buffers.Binary;
using System.Text;

namespace GameServer.Modules.Bestiary;

/// <summary>
/// The ServerEffect creates of a Bestiary mission, byte for byte as the official server sent them: the disappear.ao
/// at Einhar's arrival, the harpoon gun at a capture, the disappear.ao at both ends of his teleport and at his
/// leaving. They are written into the 0x2bb create of the <see cref="EffectType"/> object.
/// </summary>
public static class BestiaryEffects
{
    /// <summary>The ServerEffect object type.</summary>
    public const string EffectType = "Metadata/Effects/ServerEffect";

    /// <summary>Einhar's teleport effect.</summary>
    public const string DisappearAo = "Metadata/Effects/Spells/monsters_effects/League_Bestiary/Einhar/transition/disappear.ao";

    /// <summary>The harpoon-gun effect at a capture.</summary>
    public const string HarpoonAo = "Metadata/Effects/Spells/monsters_effects/League_Bestiary/Einhar/trailer/fire_harpoon_gun/fire_gun_no_portal.ao";

    /// <summary>Frames a disappear.ao effect lives before the server removes it.</summary>
    public const int EffectLifeFrames = 46;

    /// <summary>Frames a harpoon effect lives before the server removes it.</summary>
    public const int HarpoonLifeFrames = 238;

    /// <summary>Einhar's NPC component index in the runtime order, for the variety whose component list does not name it.</summary>
    public const int NpcComponent = 13;

    /// <summary>The leave effect's 13-byte tail.</summary>
    static readonly byte[] LeaveTail = [0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0x3f, 0x00];

    static byte[] Positioned((int X, int Y) cell, double rotation)
    {
        var body = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0, 4), cell.X);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4, 4), cell.Y);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8, 4), (float)rotation);
        return body;
    }

    static byte[] PropertyU32(uint key, uint value)
    {
        var body = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), key);
        body[4] = 0;
        body[5] = 1;
        body[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7, 4), value);
        return body;
    }

    /// <summary>The two ServerEffect properties: 0x14406536 is Einhar's entity id, 0x4ac375a4 a bool set to 1.</summary>
    static IEnumerable<byte> Properties(uint einharEid)
    {
        foreach (byte b in PropertyU32(0x14406536, einharEid)) yield return b;
        foreach (byte b in new byte[] { 0xa4, 0x75, 0xc3, 0x4a, 0x00, 0x01, 0x0a, 0x01 }) yield return b;
    }

    static void AnimatedPath(List<byte> body, string ao)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)ao.Length);
        body.AddRange(length);
        body.AddRange(Encoding.Unicode.GetBytes(ao));
    }

    /// <summary>The disappear.ao where Einhar arrives, 198 bytes: no properties, Positioned, 4 zero bytes, Animated 01,
    /// the ao, 00.</summary>
    public static byte[] ArriveBody((int X, int Y) cell, double rotation)
    {
        var body = new List<byte> { 2, 0 };
        body.AddRange(Positioned(cell, rotation));
        body.AddRange(new byte[4]);
        body.Add(0x01);
        AnimatedPath(body, DisappearAo);
        body.Add(0);
        return body.ToArray();
    }

    /// <summary>The disappear.ao at one end of Einhar's teleport, 217 bytes: the 2 properties, Positioned, 4 zero bytes,
    /// Animated 01, the ao, 00.</summary>
    public static byte[] TeleportBody((int X, int Y) cell, double rotation, uint einharEid)
    {
        var body = new List<byte> { 2, 2 };
        body.AddRange(Properties(einharEid));
        body.AddRange(Positioned(cell, rotation));
        body.AddRange(new byte[4]);
        body.Add(0x01);
        AnimatedPath(body, DisappearAo);
        body.Add(0);
        return body.ToArray();
    }

    /// <summary>The fire_gun_no_portal.ao at a capture, 252 bytes: no properties, Positioned, the flags word 00 00 04 00,
    /// the height group (0, +0.0), Animated 01, the ao, 00.</summary>
    public static byte[] HarpoonBody((int X, int Y) cell, double rotation)
    {
        var body = new List<byte> { 2, 0 };
        body.AddRange(Positioned(cell, rotation));
        body.AddRange(Convert.FromHexString("000004000000000000000000"));
        body.Add(0x01);
        AnimatedPath(body, HarpoonAo);
        body.Add(0);
        return body.ToArray();
    }

    /// <summary>The disappear.ao when Einhar leaves, 237 bytes: the 2 properties, Positioned with rotation 0, the flags
    /// word, the height group (0, -0.0), Animated 09, the ao, the 13-byte tail.</summary>
    public static byte[] LeaveBody((int X, int Y) cell, uint einharEid)
    {
        var body = new List<byte> { 2, 2 };
        body.AddRange(Properties(einharEid));
        body.AddRange(Positioned(cell, 0.0));
        body.AddRange(Convert.FromHexString("000004000000000000000080"));
        body.Add(0x09);
        AnimatedPath(body, DisappearAo);
        body.AddRange(LeaveTail);
        return body.ToArray();
    }
}
