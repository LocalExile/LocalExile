namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A quest object of a room or a tile that the server creates (the Gateway's quest engine, Agents/docs/protocol/12-quests.md
/// §5): a seal, a lever, a door, a quest chest, an altar. Its components are its .ot chain's deserialized ones
/// (data/object-types.json, scripts/re/ot_components.py: the first <c>read</c> of them, in order); this blob writes each the
/// way the objects already traced write it:
/// <list type="bullet">
/// <item>Positioned: x, y, rotation, u16, flags0 (0x04: a fixed height, i32 steps of 7.8125 and f32 z, as a tile record
/// gives it); blocking is the type's own (<see cref="BlobWriter.Positioned"/>);</item>
/// <item>Animated: 0 (the type's own model) or 1 and a u32-counted UTF-16 .ao path (a tile record's virtual model);</item>
/// <item>Transitionable (0x1421c8e20): u8 state, 1 … num_states (0 is invalid: it crashed the client);</item>
/// <item>Chest (0x141e16f10): u8 opened (bit 0), u8, u8; Stats and ObjectMagicProperties as an area transition's;</item>
/// <item>TriggerableBlockage (0x141e47880): u8 blocked, and only while blocked a u8 count (0 here);</item>
/// <item>NPC: has dialogue 0, no component that must be alive (0xff), as a boss that speaks;</item>
/// <item>InteractionAction: flags 0; MinimapIcon: flags 0, or 6 for an explicitly hidden marker; BaseEvents: nothing;</item>
/// <item>HarvestWorldObject (0x141eb0cb0): u8 n, n × {u8 HarvestSeeds row, u8 count}, the seed list a grove object's
/// label shows ("{count} x MonsterVarieties.Name").</item>
/// <item>HeistRewardDisplay (0x141eb4120): u8 hasPrimary, u8 hasSecondary, raw <see cref="Item.Payload"/> for each present
/// item (no u16 length prefix), then i32 LE openAttempts (clamped &gt;= 0).</item>
/// <item>Buffs: u16 0 (no buffs, as a shrine's); Functions and ControlZone: no data;</item>
/// <item>then the tasks (0x141ee6950): u8 n, per task the u16 GameObjectTasks HASH16 little-endian and the task's own data
/// (its reader consumes its own bytes); later changes are S→C 0x2c2/0x2c3/0x2c5 (InstanceEntities.AddTask).</item>
/// </list>
/// Types with any other deserialized component are not written (<see cref="CanSerialize"/>).
/// </summary>
public sealed record QuestObject(int GridX, int GridY, IReadOnlyList<string> Components, float Rotation = 0, float? Height = null,
    string? Model = null, byte TransitionState = 1, bool Opened = false, bool Blocked = false, ushort Team = 0)
{
    public bool Awake { get; init; }
    public IReadOnlyList<string> Components { get; init => field = Owned.List(value); } = Owned.List(Components);
    /// <summary>The Chest component's other flag bits (bit 0 is <see cref="Opened"/>): bit 1 makes the client play the
    /// object's warning sound, its own stat checks deciding (a strongbox's <c>chest_play_warning_sound</c>).</summary>
    public byte ChestFlags { get; init; }
    /// <summary>ObjectMagicProperties' corrupted bit (0x141e7e080 trailing flags bit 0 → +0x360), checked by
    /// the client's chest currency validator (0x141f8ddd0 → ItemErrorTargetCorrupted).</summary>
    public bool Corrupted { get; init; }
    /// <summary>Suppress the object's own map marker without removing its model or interaction.</summary>
    public bool HideMinimapIcon { get; init; }
    public StateMachineData? StateMachine { get; init; }
    public IReadOnlyList<ItemMod> Mods { get; init => field = Owned.List(value); } = [];
    public byte Rarity { get; init; }
    /// <summary>ObjectMagicProperties' name as Words.dat HASH32 parts (flags bit 8), as a unique item's or monster's. Empty by default.</summary>
    public IReadOnlyList<uint> NameWords { get; init => field = Owned.List(value); } = [];
    public bool Identified { get; init; } = true;
    public IReadOnlyList<(uint Key, int Value)> Stats { get; init => field = Owned.List(value); } = [];
    /// <summary>Base-data script variables containing one entity reference (141dedf80 / 141dfe5a0, type 1).</summary>
    public IReadOnlyDictionary<uint, uint> EntityVariables { get; init; } = new Dictionary<uint, uint>();
    /// <summary>Base-data script variables containing one i32 (type 9, read by 0x141806520), written after the entity ones.</summary>
    public IReadOnlyDictionary<uint, int> IntVariables { get; init; } = new Dictionary<uint, int>();
    /// <summary>Base-data script variables containing one f32 (type 8), written after the int ones.</summary>
    public IReadOnlyDictionary<uint, float> FloatVariables { get; init; } = new Dictionary<uint, float>();
    /// <summary>Extended or multi-value base-data script variables (types 1, 7, 8, 9, 10, 11 or empty clears), written after the typed dictionaries.</summary>
    public IReadOnlyList<EntityVariable> Variables { get; init => field = Owned.List(value); } = [];

    /// <summary>A script variable's name hash (0x1406db8f0): MurmurHash2 of the UTF-16LE name, seed 0.</summary>
    public static uint VariableHash(string name) =>
        InstanceEntities.Murmur2(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()));
    /// <summary>The HeistRewardDisplay component's displayed items and open-attempt count.</summary>
    public RewardDisplayData? RewardDisplay { get; init; }
    /// <summary>HarvestWorldObject's seed list: HarvestSeeds rows and their counts.</summary>
    public IReadOnlyList<(byte SeedRow, byte Count)> HarvestSeeds { get; init => field = Owned.List(value); } = [];
    /// <summary>The object's running tasks: GameObjectTasks HASH16 and the task's data. Empty by default.</summary>
    public IReadOnlyList<(ushort Id, byte[] Data)> Tasks { get; init => field = Owned.List(value); } = [];

    static readonly HashSet<string> Supported = ["Positioned", "BaseEvents", "Animated", "Transitionable", "InteractionAction", "MinimapIcon",
        "TriggerableBlockage", "Chest", "Stats", "ObjectMagicProperties", "NPC", "Movement", "StateMachine", "HarvestWorldObject",
        "HeistRewardDisplay", "Buffs", "Functions", "ControlZone"];

    /// <summary>Whether <see cref="Blob"/> can write a type with these deserialized components.</summary>
    public static bool CanSerialize(IReadOnlyList<string> components) =>
        CanSerialize(components, false);

    public static bool CanSerialize(IReadOnlyList<string> components, bool haveStateMachine) =>
        components.Count > 0 && components[0] == "Positioned"
        && components.All(c => Supported.Contains(c) && (c != "StateMachine" || haveStateMachine));

    /// <summary>The index of the Transitionable component (for 0x2bc updates), or −1.</summary>
    public static int TransitionableIndex(IReadOnlyList<string> components) => components.ToList().IndexOf("Transitionable");

    public byte[] Blob()
    {
        if (!CanSerialize(Components, StateMachine is not null)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(Awake ? (byte)2 : (byte)0);
        int variables = EntityVariables.Count + IntVariables.Count + FloatVariables.Count + Variables.Count;
        if (variables > byte.MaxValue) throw new ArgumentException("too many script variables");
        w.U8((byte)variables);
        // Each: u32 name hash, u8 flags 0, u8 value count, and when count > 0: u8 type + values.
        foreach (var (hash, entity) in EntityVariables.OrderBy(v => v.Key))
            w.U32(hash).U8(0).U8(1).U8((byte)EntityVariableType.Entity).U32(entity);
        foreach (var (hash, value) in IntVariables.OrderBy(v => v.Key))
            w.U32(hash).U8(0).U8(1).U8((byte)EntityVariableType.Int).I32(value);
        foreach (var (hash, value) in FloatVariables.OrderBy(v => v.Key))
            w.U32(hash).U8(0).U8(1).U8((byte)EntityVariableType.Float).F32(value);
        foreach (var variable in Variables)
            variable.Write(w);
        foreach (string c in Components)
        {
            switch (c)
            {
                // A placement without a fixed height (AreaObjects' NaN) has none: a NaN height hides the object
                // (live 2026-11-19: The Sovereign's ChartLocker was neither drawn nor targetable).
                case "Positioned": w.Positioned(GridX, GridY, Rotation, Team, fixedHeight: Height is float z && !float.IsNaN(z) ? BlobWriter.FixedHeight(z) : null); break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "Transitionable": w.U8((byte)(Math.Max((byte)1, TransitionState) & 0x7f)); break;
                case "Chest": w.U8((byte)((Opened ? 1 : 0) | (ChestFlags & 0xfe))).U8(0).U8(0); break;
                // As a monster's (0x1421c98b0): compact count + pairs, u32, f32, u8.
                case "Stats": w.Stats(Stats).U32(0).U32(0).U8(0); break;
                // Stationary initially; the server drives the rail with MoveTo (as for the Labyrinth's roombas).
                case "Movement": w.U8(0).I32(GridX).I32(GridY).U32(0).U32(0).U32(0).U32(0).U8(0).U8(0); break;
                case "ObjectMagicProperties":
                    if (Mods.Count == 0 && Rarity == 0 && !Corrupted) w.U16(0).U8(0).U8(0);
                    else { MagicProperties.Write(w, Rarity, Identified, Mods, NameWords); w.U8((byte)(Corrupted ? 1 : 0)); }
                    break;
                case "StateMachine": StateMachine!.Write(w); break;
                case "TriggerableBlockage": if (Blocked) w.U8(1).U8(0); else w.U8(0); break;
                case "NPC": w.U8(0).U8(0xff); break;
                case "InteractionAction": w.U8(0); break;
                case "MinimapIcon": w.U8((byte)(HideMinimapIcon ? 0x06 : 0)); break;
                case "HarvestWorldObject":
                    if (HarvestSeeds.Count > byte.MaxValue) throw new ArgumentException("too many harvest seed rows");
                    w.U8((byte)HarvestSeeds.Count);
                    foreach (var (row, count) in HarvestSeeds) w.U8(row).U8(count);
                    break;
                case "HeistRewardDisplay":
                    var display = RewardDisplay;
                    w.U8((byte)(display?.Primary is not null ? 1 : 0))
                     .U8((byte)(display?.Secondary is not null ? 1 : 0));
                    if (display?.Primary is { } primary) w.Bytes(primary.Payload());
                    if (display?.Secondary is { } secondary) w.Bytes(secondary.Payload());
                    w.I32(Math.Max(0, display?.OpenAttempts ?? 0));
                    break;
                case "Buffs": w.U16(0); break;
                // BaseEvents, Functions, ControlZone: no data.
            }
        }
        if (Tasks.Count > byte.MaxValue) throw new ArgumentException("too many tasks");
        w.U8((byte)Tasks.Count);
        foreach (var (id, data) in Tasks) w.U16(id).Bytes(data);
        return w.ToArray();
    }
}

/// <summary>The HeistRewardDisplay component (reader 0x141eb4120, writer 0x141eb4070): u8 hasPrimary, u8 hasSecondary,
/// raw <see cref="Item.Payload"/> for each present item (no u16 length prefix), then i32 LE openAttempts (clamped &gt;= 0).</summary>
public sealed record RewardDisplayData(Item? Primary, Item? Secondary = null, int OpenAttempts = 0);

/// <summary>Entity base-data KV value types read by 0x141dfe5a0 via dispatch table 0x1435a2110 (3.29.3c).</summary>
public enum EntityVariableType : byte
{
    Entity = 1,
    /// <summary>Two little-endian u32 values (0x141e00260 → 0x140df7350).</summary>
    IntPair = 7,
    /// <summary>IEEE-754 little-endian f32 (0x141e00330).</summary>
    Float = 8,
    /// <summary>Little-endian i32 (0x141e00430).</summary>
    Int = 9,
    /// <summary>Single u8 / boolean byte (0x141e00530).</summary>
    Byte = 10,
    /// <summary>u32 LE char count + UTF-16LE wide string (0x141e00620 → 0x140e27600).</summary>
    String = 11,
}

/// <summary>
/// One base-data key-value variable entry for 0x2bb creation and S→C 0x2c1 live updates (reader 0x141dedf80 /
/// 0x141dfe5a0). Entries carry u32 LE name hash, u8 flags, u8 count; only when count &gt; 0 do the u8 type and
/// <see cref="Count"/> little-endian values follow.
/// </summary>
public sealed class EntityVariable
{
    readonly Action<BlobWriter> _writeValues;

    public uint Hash { get; }
    public byte Flags { get; }
    public EntityVariableType Type { get; }
    public byte Count { get; }

    EntityVariable(uint hash, byte flags, EntityVariableType type, int count, Action<BlobWriter> writeValues)
    {
        if (count < 0 || count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(count), count, "At most 255 values per variable.");
        Hash = hash;
        Flags = flags;
        Type = type;
        Count = (byte)count;
        _writeValues = writeValues;
    }

    public static EntityVariable Empty(uint hash, byte flags = 0) => new(hash, flags, 0, 0, _ => { });
    public static EntityVariable Empty(string name, byte flags = 0) => Empty(QuestObject.VariableHash(name), flags);

    public static EntityVariable Entity(uint hash, params uint[] values) => Entity(hash, 0, values);
    public static EntityVariable Entity(uint hash, byte flags, params uint[] values)
    {
        var copy = values.ToArray();
        return new(hash, flags, EntityVariableType.Entity, copy.Length, w => { foreach (uint v in copy) w.U32(v); });
    }
    public static EntityVariable Entity(string name, params uint[] values) => Entity(QuestObject.VariableHash(name), values);

    public static EntityVariable IntPair(uint hash, params (uint A, uint B)[] values) => IntPair(hash, 0, values);
    public static EntityVariable IntPair(uint hash, byte flags, params (uint A, uint B)[] values)
    {
        var copy = values.ToArray();
        return new(hash, flags, EntityVariableType.IntPair, copy.Length, w => { foreach (var (a, b) in copy) w.U32(a).U32(b); });
    }
    public static EntityVariable IntPair(string name, params (uint A, uint B)[] values) => IntPair(QuestObject.VariableHash(name), values);

    public static EntityVariable Float(uint hash, params float[] values) => Float(hash, 0, values);
    public static EntityVariable Float(uint hash, byte flags, params float[] values)
    {
        var copy = values.ToArray();
        return new(hash, flags, EntityVariableType.Float, copy.Length, w => { foreach (float v in copy) w.F32(v); });
    }
    public static EntityVariable Float(string name, params float[] values) => Float(QuestObject.VariableHash(name), values);

    public static EntityVariable Int(uint hash, params int[] values) => Int(hash, 0, values);
    public static EntityVariable Int(uint hash, byte flags, params int[] values)
    {
        var copy = values.ToArray();
        return new(hash, flags, EntityVariableType.Int, copy.Length, w => { foreach (int v in copy) w.I32(v); });
    }
    public static EntityVariable Int(string name, params int[] values) => Int(QuestObject.VariableHash(name), values);

    public static EntityVariable Byte(uint hash, params byte[] values) => Byte(hash, 0, values);
    public static EntityVariable Byte(uint hash, byte flags, params byte[] values)
    {
        var copy = values.ToArray();
        return new(hash, flags, EntityVariableType.Byte, copy.Length, w => { foreach (byte v in copy) w.U8(v); });
    }
    public static EntityVariable Byte(string name, params byte[] values) => Byte(QuestObject.VariableHash(name), values);

    public static EntityVariable Bool(uint hash, params bool[] values) => Byte(hash, 0, [.. values.Select(v => v ? (byte)1 : (byte)0)]);
    public static EntityVariable Bool(string name, params bool[] values) => Bool(QuestObject.VariableHash(name), values);

    public static EntityVariable String(uint hash, params string[] values) => String(hash, 0, values);
    public static EntityVariable String(uint hash, byte flags, params string[] values)
    {
        var copy = values.ToArray();
        return new(hash, flags, EntityVariableType.String, copy.Length, w => { foreach (string v in copy) w.WStr32(v); });
    }
    public static EntityVariable String(string name, params string[] values) => String(QuestObject.VariableHash(name), values);

    public void Write(BlobWriter w)
    {
        w.U32(Hash).U8(Flags).U8(Count);
        if (Count == 0) return;
        w.U8((byte)Type);
        _writeValues(w);
    }

    public static void WriteAll(BlobWriter w, IReadOnlyList<EntityVariable> variables)
    {
        if (variables.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(variables), variables.Count, "At most 255 base-data variables.");
        w.U8((byte)variables.Count);
        foreach (var v in variables) v.Write(w);
    }

    public static byte[] EncodeBlob(IReadOnlyList<EntityVariable> variables)
    {
        var w = new BlobWriter();
        WriteAll(w, variables);
        return w.ToArray();
    }
}
