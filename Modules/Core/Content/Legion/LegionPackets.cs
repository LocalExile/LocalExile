using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Legion ruleset's data (S→C 0x2c9 for Rulesets row LegionRules) and the bodies of the objects the encounter creates,
/// as the official captures carry them.
/// </summary>
public static class LegionPackets
{
    /// <summary>The monolith's component indices in its .ot order (Positioned 0, InteractionAction 1, BaseEvents 2,
    /// Animated 3, MinimapIcon 4, StateMachine 5).</summary>
    public const byte MonolithInteractionIndex = 1;
    public const byte MonolithStateMachineIndex = 5;

    /// <summary>The flashlight's Animated index in its .ot order (Positioned 0, StateMachine 1, Animated 2).</summary>
    public const byte FlashLightAnimatedIndex = 2;

    static byte[] Ruleset(byte[] payload) =>
        LegionTables.RulesetRow is { } row ? InstanceRulesets.Data(row, payload) : [];

    /// <summary>The ruleset's first message (37 bytes of data): u8 0, i32 x, i32 y, u32 level, u16 0, u32 monolith id,
    /// 10 zero bytes, f32 angle, f32 seconds of the freeze. The official captures decode to exactly this.</summary>
    public static byte[] Full((int X, int Y) cell, int level, uint monolith, double angle, double seconds)
    {
        byte[] payload = new byte[37];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), cell.X);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5), cell.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9), (uint)level);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(15), monolith);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(29), (float)angle);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(33), (float)seconds);
        return Ruleset(payload);
    }

    /// <summary>The state message: 0x05, u32 monolith id, u8 stage, u8 step.</summary>
    public static byte[] State(uint monolith, int stage, int step)
    {
        byte[] payload = new byte[7];
        payload[0] = 0x05;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), monolith);
        payload[5] = (byte)stage;
        payload[6] = (byte)step;
        return Ruleset(payload);
    }

    /// <summary>The activated message: 0x01, u32 monolith id.</summary>
    public static byte[] Activated(uint monolith)
    {
        byte[] payload = new byte[5];
        payload[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), monolith);
        return Ruleset(payload);
    }

    /// <summary>The environment overrides the encounter sends, all in slot 0 (S->C 0xdf replaces the area's whole list).</summary>
    public static byte[] Environments(params (ushort Row, double Seconds)[] entries) => InstanceEnvironments.Data(entries);

    /// <summary>The monolith's StateMachine (its seven shared states, from object_types) and its component index.</summary>
    public static StateMachineData? MonolithMachine { get; private set; }

    /// <summary>The flashlight's StateMachine (one shared state, foggy).</summary>
    public static StateMachineData? FlashLightMachine { get; private set; }

    public static void LoadGameData()
    {
        MonolithMachine = MachineOf(LegionTables.MonolithPath, "obelisk_state");
        FlashLightMachine = MachineOf(LegionTables.FlashLightPath, "foggy");
    }

    static StateMachineData? MachineOf(string path, string first) =>
        ObjectTypes.All().Where(t => t.Type == path).Select(t => (ClientRow?)t.Row).FirstOrDefault() is { } type
        && ObjectTypes.StateMachine(type, ObjectTypes.SharedStates()) is { } states && states.IndexOf(first) >= 0 ? states : null;

    /// <summary>The monolith's states as an update sends them: every value is the server's (flag 0x02), in the type's order
    /// (obelisk state, charging, shattered, crystal, spawn lightning, locked, active).</summary>
    public static StateMachineData? Monolith(params int[] values) => Override(MonolithMachine, values);

    static StateMachineData? Override(StateMachineData? template, int[] values) =>
        template is null || template.SharedStates.Count != values.Length ? null
            : template with { SharedStates = values, Overridden = new HashSet<int>(Enumerable.Range(0, values.Length)) };

    /// <summary>One dynamic property: the u32 key, the kind bytes 00 01 01, and the u32 entity id.</summary>
    static void DynamicProperty(BlobWriter w, uint key, uint value) => w.U32(key).U8(0).U8(1).U8(1).U32(value);

    /// <summary>Positioned as the league objects carry it: i32 x, i32 y, f32 facing, u16 0, u8 flags, u8 0. The flags are 0x00
    /// for the monolith and the chest, 0x02 for the flashlight and 0x24 for the reward symbol.</summary>
    static void Positioned(BlobWriter w, (int X, int Y) cell, float facing, byte flags) =>
        w.I32(cell.X).I32(cell.Y).F32(facing).U16(0).U8(flags).U8(0);

    /// <summary>The monolith's create body (38 bytes, 49 with a General): the General property, Positioned, InteractionAction
    /// clickable, Animated and MinimapIcon (nothing), StateMachine of seven zeros, no tasks. With a General the monolith
    /// carries property 0xec72e38c = the General's id.</summary>
    public static byte[] MonolithBody((int X, int Y) cell, uint generalId = 0, byte flag = 0)
    {
        var w = new BlobWriter().U8(flag).U8(generalId == 0 ? (byte)0 : (byte)1);
        if (generalId != 0) DynamicProperty(w, 0xec72e38c, generalId);
        Positioned(w, cell, 0f, 0x00);
        w.U8(0x01).U8(0x00).U8(0x00);
        (MonolithMachine ?? throw new InvalidOperationException("The monolith's StateMachine is not loaded.")).Write(w);
        return w.U8(0).ToArray();
    }

    /// <summary>The flashlight's create body (46 bytes): two dynamic properties holding the monolith's id (keys 0x40f0fc9c,
    /// 0x7700c879), Positioned with flag 0x02, one StateMachine value, Animated, no tasks.</summary>
    public static byte[] FlashLightBody((int X, int Y) cell, uint monolithId, byte flag = 0)
    {
        var w = new BlobWriter().U8(flag).U8(2);
        DynamicProperty(w, 0x40f0fc9c, monolithId);
        DynamicProperty(w, 0x7700c879, monolithId);
        Positioned(w, cell, 0f, 0x02);
        (FlashLightMachine ?? throw new InvalidOperationException("The flashlight's StateMachine is not loaded.")).Write(w);
        w.U8(0x00);
        return w.U8(0).ToArray();
    }

    /// <summary>The reward symbol's create body: Positioned with flags 0x24 and the scale (f32), two 1.0 floats and 8 constant
    /// bytes every capture carries, then Animated with the .ao path (LegionRewardTypeVisuals → MiscAnimated). Flag 2 wakes
    /// the object itself, so no wake-zone message follows.</summary>
    public static byte[] SymbolBody((int X, int Y) cell, string ao, double scale = LegionTables.SymbolScale, byte flag = 2)
    {
        var w = new BlobWriter().U8(flag).U8(0);
        Positioned(w, cell, 0f, 0x24);
        w.F32((float)scale);
        w.Bytes(Convert.FromHexString("0000803f0000803f"));
        w.Bytes(Convert.FromHexString("19000000005043c3"));
        w.U8(0x01).WStr32(ao);
        return w.U8(0).ToArray();
    }

    /// <summary>A Legion chest's create body: the reward-symbol property (0xf3a80181, only with a symbol), Positioned, the
    /// capture's constant bytes, one stat (monster_minimap_icon, wire id 2494) = the symbol row's MinimapIconsKey + 1, and 8
    /// more constant bytes. The chest's .ot is not extracted, so the constants are the captures' (8 of 8 chest creates
    /// carry exactly the icon of their symbol + 1).</summary>
    public static byte[] ChestBody((int X, int Y) cell, float facing, uint symbolId, int icon, byte flag = 0)
    {
        var w = new BlobWriter().U8(flag).U8(symbolId == 0 ? (byte)0 : (byte)1);
        if (symbolId != 0) DynamicProperty(w, 0xf3a80181, symbolId);
        Positioned(w, cell, facing, 0x00);
        w.U8(0x00).U8(0x00).U8(0x44).U8(0x00);
        w.CompactU(1).CompactU(2494).CompactI(icon + 1);
        w.Bytes(Convert.FromHexString("000000000000404001"));
        return w.Bytes(Convert.FromHexString("0800000001000000")).ToArray();
    }
}
