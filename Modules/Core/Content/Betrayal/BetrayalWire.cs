using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Betrayal;

/// <summary>
/// The objects a Betrayal mission creates, byte for byte as the official server's 0x2bb bodies of them: Jun, the
/// Transportation's carts, the Intervention's assassination portals, the member's entrance portal and the MakeChoice the
/// defeated member leaves. Each is the object flag and property count, the properties, Positioned (x, y, rotation and its
/// 4-byte tail) and the type's own components.
/// </summary>
public static class BetrayalWire
{
    public const string JunType = "Metadata/NPC/League/Betrayal/BetrayalNinjaCopRaid";
    public const string CartType = "Metadata/Terrain/Leagues/Betrayal/Objects/BetrayalEscortCart2";
    public const string ChoiceType = "Metadata/Terrain/Leagues/Betrayal/Objects/BetrayalMakeChoice";
    public const string EffectType = "Metadata/Effects/ServerEffect";
    public const string SpawnerTier1 = "Metadata/Monsters/LeagueBetrayal/AssassinationSpawners/AssassinationEncounterTier1_1";
    public const string SpawnerTier2 = "Metadata/Monsters/LeagueBetrayal/AssassinationSpawners/AssassinationEncounterTier2_1";
    public const string EntrancePortalAo =
        "Metadata/Effects/Spells/monsters_effects/League_Betrayal/CatarinaBargainPortal/catarina_entrance_portal.ao";

    /// <summary>The cart's StateMachine among BetrayalEscortCart2's components (Positioned, Cart, Animated, Transitionable,
    /// StateMachine, ...).</summary>
    public const byte CartStateMachine = 4;

    /// <summary>The assassination portal's Transitionable among its components (Positioned, BaseEvents, Animated,
    /// Transitionable, Render); its type has 4 states.</summary>
    public const byte SpawnerTransitionable = 3;

    /// <summary>The state an assassination portal goes to when the member falls [capture: 6 of 6 went 1 to 4].</summary>
    public const byte SpawnerClosed = 4;

    /// <summary>The cart's shared states, in its type's order.</summary>
    public static readonly StateMachineData CartStates = new([0, 0], 0, ["movement", "cart_finished"]);

    static void Positioned(List<byte> body, (int X, int Y) cell, double rotation)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), cell.X);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), cell.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), (float)rotation);
        body.AddRange(b);
    }

    static void PropertyU32(List<byte> body, uint key, uint value)
    {
        var b = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), key);
        b[5] = 1;
        b[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(7), value);
        body.AddRange(b);
    }

    static void States(List<byte> body, StateMachineData states)
    {
        var w = new BlobWriter();
        states.Write(w);
        body.AddRange(w.ToArray());
    }

    /// <summary>Jun (BetrayalNinjaCopRaid), 35 bytes: one property (0xa22b09a5 = 931), Positioned, then Animated, BaseEvents,
    /// InteractionAction, NPC, MinimapIcon [capture: 5 of 5 creates the same but for the flag and the cell]. He is created asleep
    /// (flag 0) with the mission and comes back awake (flag 2) when the member is dealt with.</summary>
    public static byte[] JunBody((int X, int Y) cell, double rotation, byte flag)
    {
        var body = new List<byte> { flag, 1 };
        PropertyU32(body, 0xa22b09a5, 931);
        Positioned(body, cell, rotation);
        body.AddRange([0x40, 0x01, 0x01, 0x01, 0x00, 0x00]);
        return [.. body];
    }

    /// <summary>A Transportation cart (BetrayalEscortCart2), 32 bytes: no property, Positioned, Cart (u8 0x10, f32 1.0), Animated,
    /// its StateMachine (movement, cart_finished) and the tasks [capture: 3 carts, 7 creates].</summary>
    public static byte[] CartBody((int X, int Y) cell, double rotation)
    {
        var body = new List<byte> { 0, 0 };
        Positioned(body, cell, rotation);
        body.AddRange([0x10, 0x00, 0x00, 0x80, 0x3f, 0x01]);
        States(body, CartStates);
        body.AddRange([0, 0]);
        return [.. body];
    }

    /// <summary>The MakeChoice (BetrayalMakeChoice) where the member fell, 36 bytes, awake: one property (0x636d4000 = the
    /// member's object id), Positioned, InteractionAction 1, its StateMachine (NPCIndex = the member's BetrayalTargets row) and
    /// the tasks [capture: 5 of 5].</summary>
    public static byte[] ChoiceBody((int X, int Y) cell, double rotation, uint member, int targetRow)
    {
        var body = new List<byte> { 2, 1 };
        PropertyU32(body, 0x636d4000, member);
        Positioned(body, cell, rotation);
        body.Add(0x01);
        States(body, new StateMachineData([targetRow], 0));
        body.AddRange([0, 0]);
        return [.. body];
    }

    /// <summary>An assassination portal, 21 bytes, awake: no property, Positioned (rotation 0), Animated, Transitionable 1 and
    /// the tasks [capture: 6 of 6].</summary>
    public static byte[] SpawnerBody((int X, int Y) cell)
    {
        var body = new List<byte> { 2, 0 };
        Positioned(body, cell, 0);
        body.AddRange([0x00, 0x01, 0x00]);
        return [.. body];
    }

    /// <summary>The member's entrance portal (a ServerEffect), 275 bytes for its 106-character ao, awake: two properties
    /// (0x14406536 = the member's object id, 0x4ac375a4 = 1), Positioned with the effect's flags word, Animated (09, the ao's
    /// UTF-16 length and text) and its tail [capture: 3 of 3].</summary>
    public static byte[] PortalBody((int X, int Y) cell, double rotation, uint member)
    {
        var body = new List<byte> { 2, 2 };
        PropertyU32(body, 0x14406536, member);
        body.AddRange([0xa4, 0x75, 0xc3, 0x4a, 0x00, 0x01, 0x0a, 0x01]);
        var b = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), cell.X);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), cell.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), (float)rotation);
        body.AddRange(b);
        body.AddRange([0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x09]);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)EntrancePortalAo.Length);
        body.AddRange(length);
        body.AddRange(Encoding.Unicode.GetBytes(EntrancePortalAo));
        body.AddRange([0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0x3f, 0x00]);
        return [.. body];
    }

    /// <summary>The 0x2bb of an object of <paramref name="type"/> with <paramref name="body"/>.</summary>
    public static byte[] Create(uint id, string type, byte[] body) =>
        InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), body);
}
