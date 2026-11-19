using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A quest object of a room or a tile that the server creates (the Gateway's quest engine, Agents/docs/protocol/12-quests.md
/// §5): a seal, a lever, a door, a quest chest, an altar. Its components are its .ot chain's deserialized ones
/// (data/object-types.json, scripts/re/ot_components.py: the first <c>read</c> of them, in order); this blob writes each the
/// way the objects already traced write it:
/// <list type="bullet">
/// <item>Positioned: x, y, rotation, u16, flags0 (0x04: a fixed height, i32 steps of 7.8125 and f32 z, as a tile record
/// gives it), flags1 0x02 with hidden 0 / shown 1 (the Glyph Wall, the Rhoa Nests);</item>
/// <item>Animated: 0 (the type's own model) or 1 and a u32-counted UTF-16 .ao path (a tile record's virtual model);</item>
/// <item>Transitionable (0x1421c8e20): u8 state, 1 … num_states (0 is invalid: it crashed the client);</item>
/// <item>Chest (0x141e16f10): u8 opened (bit 0), u8, u8; Stats and ObjectMagicProperties as an area transition's;</item>
/// <item>TriggerableBlockage (0x141e47880): u8 blocked, and only while blocked a u8 count (0 here);</item>
/// <item>NPC: has dialogue 0, no component that must be alive (0xff), as a boss that speaks;</item>
/// <item>InteractionAction: flags 0; MinimapIcon: flags 0, or 6 for an explicitly hidden marker; BaseEvents: nothing;</item>
/// <item>HarvestWorldObject (0x141eb0cb0): u8 n, n × {u8 HarvestSeeds row, u8 count}, the seed list a grove object's
/// label shows ("{count} x MonsterVarieties.Name").</item>
/// <item>Buffs: u16 0 (no buffs, as a shrine's); Functions and ControlZone: no data;</item>
/// <item>then the tasks (0x141ee6950): u8 n, per task the u16 GameObjectTasks HASH16 little-endian and the task's own data
/// (its reader consumes its own bytes); later changes are S→C 0x2c2/0x2c3/0x2c5 (InstanceEntities.AddTask).</item>
/// </list>
/// Types with any other deserialized component are not written (<see cref="CanSerialize"/>).
/// </summary>
public sealed record QuestObject(int GridX, int GridY, IReadOnlyList<string> Components, float Rotation = 0, float? Height = null,
    string? Model = null, byte TransitionState = 1, bool Opened = false, bool Blocked = false, ushort Team = 0)
{
    public IReadOnlyList<string> Components { get; init => field = Owned.List(value); } = Owned.List(Components);
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
    /// <summary>HarvestWorldObject's seed list: HarvestSeeds rows and their counts.</summary>
    public IReadOnlyList<(byte SeedRow, byte Count)> HarvestSeeds { get; init => field = Owned.List(value); } = [];
    /// <summary>The object's running tasks: GameObjectTasks HASH16 and the task's data. Empty by default.</summary>
    public IReadOnlyList<(ushort Id, byte[] Data)> Tasks { get; init => field = Owned.List(value); } = [];

    static readonly HashSet<string> Supported = ["Positioned", "BaseEvents", "Animated", "Transitionable", "InteractionAction", "MinimapIcon",
        "TriggerableBlockage", "Chest", "Stats", "ObjectMagicProperties", "NPC", "Movement", "StateMachine", "HarvestWorldObject",
        "Buffs", "Functions", "ControlZone"];

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
        w.U8(0);            // object flags
        if (EntityVariables.Count > byte.MaxValue) throw new ArgumentException("too many entity variables");
        w.U8((byte)EntityVariables.Count);
        foreach (var (hash, entity) in EntityVariables.OrderBy(v => v.Key))
            w.U32(hash).U8(0).U8(1).U8(1).U32(entity);
        foreach (string c in Components)
        {
            switch (c)
            {
                case "Positioned":
                    w.I32(GridX).I32(GridY).F32(Rotation).U16(Team).U8((byte)(Height is null ? 0 : 0x04)).U8(0x02);
                    if (Height is float z) w.I32((int)MathF.Round(z / 7.8125f)).F32(z);
                    w.U8(0).U8(1);
                    break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "Transitionable": w.U8((byte)(Math.Max((byte)1, TransitionState) & 0x7f)); break;
                case "Chest": w.U8((byte)(Opened ? 1 : 0)).U8(0).U8(0); break;
                // As a monster's (0x1421c98b0): compact count + pairs, u32, f32, u8.
                case "Stats": w.Stats(Stats).U32(0).U32(0).U8(0); break;
                // Stationary initially; the server drives the rail with MoveTo (as for the Labyrinth's roombas).
                case "Movement": w.U8(0).I32(GridX).I32(GridY).U32(0).U32(0).U32(0).U32(0).U8(0).U8(0); break;
                case "ObjectMagicProperties":
                    if (Mods.Count == 0 && Rarity == 0) w.U16(0).U8(0).U8(0);
                    else { MagicProperties.Write(w, Rarity, Identified, Mods, NameWords); w.U8(0); }
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
