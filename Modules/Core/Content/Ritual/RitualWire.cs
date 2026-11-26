using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Modules.Ritual;

/// <summary>
/// The Ritual objects' create blobs and the Ritual S→C / C→S messages, byte for byte as the official 3.29.3c/d client
/// binary and captures define them.
/// <para>
/// S→C: 0x165 (altar state), 0x166 (favours window metadata), 0x167 (tribute), 0x007b kind 0x38 (open favours window).
/// C→S: 0x168 (reroll favours), 0x169 (open favours window), 0x283 (buy/claim favour item), 0x284 (defer favour item).
/// </para>
/// </summary>
static class RitualWire
{
    public const ushort AltarStateId = 0x165, FavoursId = 0x166, TributeId = 0x167;
    public const ushort RerollFavoursId = 0x168, OpenFavoursId = 0x169, BuyFavourId = 0x283, DeferFavourId = 0x284;

    /// <summary>S→C 0x007b kind 0x38 (56): opens RitualRewardWindow bound to the Manager-1 inventory id in arg a.</summary>
    public const byte FavoursUiKind = 0x38;

    /// <summary>Manager-1 inventory id used for the instance's Ritual favour rewards shop (distinct from NPC shop 0x600+).</summary>
    public const uint ShopInventoryId = 0x620;

    /// <summary>Manager-1 inventory slot for the Ritual favour window (12×7, flags 0x60 passed to AddInventory).</summary>
    public static readonly InstanceInventories.Slot ShopSlot = new(0, 0x00, 12, 7);

    /// <summary>Manager-0 Inventories.dat row 57 (RitualSavedRewards1, invId 58): client checks 50 - count for defer cap.</summary>
    public const byte SavedRewardsRow = 57;
    public const uint SavedRewardsInventoryId = SavedRewardsRow + 1;

    /// <summary>0x0166 per-item flag values verified from RitualRewardWindow (0x140320c60).</summary>
    public const byte FlagNormal = 0, FlagDeferred = 1, FlagDeferredWarning = 2, FlagDeferredExpiring = 3;

    /// <summary>Properties: the RitualRuneTypes row (u32), the kill progress (f32), a pool's altar (u32, the Interactable's id).</summary>
    public const uint PropRune = 0x027aa346, PropProgress = 0x460e6134, PropPoolOwner = 0x1723d91c;

    /// <summary>The Interactable's constant middle (bytes 40..145) and the Object's (29..98): the same in every capture
    /// but for Interactable byte 108, 0x40 once a kill was credited, else 0.</summary>
    const string InteractableMid =
        "01b7c401000000000000404001010002"
        + "00026601ab050000807f0000803f0100"
        + "00000000000000000000020000005700"
        + "00000001000000000000000000000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000";
    const string ObjectMid =
        "0189df01000000000000404001000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000000000000000"
        + "00000000000000000000000000000000"
        + "000000000000";

    /// <summary>The byte after 03 in the Interactable / Object tails: 0x17 / 0x09 for Golden2 (row 62), 0x1f / 0x0c for
    /// PhysicalBloodUber1 (row 73) in the captures. What it is is not known; the other runes take the second pair.</summary>
    static (byte Interactable, byte Object) RuneTail(int row) => row switch { 62 => (0x17, 0x09), _ => (0x1f, 0x0c) };

    /// <summary>The index of a component in an object type's list, which the update messages name [data: the client's .ot
    /// order, data/object-types.json]. The fallback is the index the .ot order gives, used when the type is not in the
    /// catalogue.</summary>
    public static byte Component(string type, string name, byte fallback) =>
        QuestObjectTypes.Find(type) is { } info && info.Components.ToList().IndexOf(name) is >= 0 and var i ? (byte)i : fallback;

    public static byte InteractableAction => Component(RitualEncounter.InteractableType, "InteractionAction", 7);
    public static byte InteractableStates => Component(RitualEncounter.InteractableType, "StateMachine", 8);
    public static byte ObjectStates => Component(RitualEncounter.ObjectType, "StateMachine", 6);
    public static byte LightPositioned => Component(RitualEncounter.LightType, "Positioned", 0);
    public static byte LightStates => Component(RitualEncounter.LightType, "StateMachine", 2);
    public static byte PoolTransition => Component(RitualEncounter.PoolType, "Transitionable", 3);

    /// <summary>RitualRuneInteractable (160 B): 2 properties, Positioned, the constant middle, 03 X 14 00 00, enabled,
    /// StateMachine (state, enabled), 00 00.</summary>
    public static byte[] Interactable((int X, int Y) cell, float rotation, int runeRow, float progress, int state, int enabled)
    {
        byte[] mid = Convert.FromHexString(InteractableMid);
        mid[108 - 40] = progress > 0 ? (byte)0x40 : (byte)0;
        var w = new BlobWriter().U8(0).U8(2);
        w.U32(PropRune).U8(0).U8(1).U8(8).U32((uint)runeRow);
        w.U32(PropProgress).U8(0).U8(1).U8(9).F32(progress);
        w.I32(cell.X).I32(cell.Y).F32(rotation).Zeros(4).Bytes(mid);
        w.U8(3).U8(RuneTail(runeRow).Interactable).U8(0x14).U8(0).U8(0).U8((byte)(enabled & 1));
        new StateMachineData([state, enabled], 0).Write(w);
        return w.U8(0).U8(0).ToArray();
    }

    /// <summary>RitualRuneObject (109 B): 1 property (progress), Positioned, the constant middle, 03 X 14 00 00,
    /// StateMachine (state), 00.</summary>
    public static byte[] Object((int X, int Y) cell, float rotation, int runeRow, float progress, int state)
    {
        var w = new BlobWriter().U8(0).U8(1);
        w.U32(PropProgress).U8(0).U8(1).U8(9).F32(progress);
        w.I32(cell.X).I32(cell.Y).F32(rotation).Zeros(4).Bytes(Convert.FromHexString(ObjectMid));
        w.U8(3).U8(RuneTail(runeRow).Object).U8(0x14).U8(0).U8(0);
        new StateMachineData([state], 0).Write(w);
        return w.U8(0).ToArray();
    }

    /// <summary>RitualRuneLight (32 B, 44 B with a scale): Positioned (u16 0, flags 0x04, | 0x20 with three f32 of scale),
    /// i32 -10, f32 78.125, 00, StateMachine (state), 00.</summary>
    public static byte[] Light((int X, int Y) cell, float rotation, int state, float? scale)
    {
        var w = new BlobWriter().U8(0).U8(0).I32(cell.X).I32(cell.Y).F32(rotation).U16(0);
        if (scale is float s) w.U8(0x24).U8(0).F32(s).F32(1).F32(1);
        else w.U8(0x04).U8(0);
        w.I32(-10).F32(78.125f).U8(0);
        new StateMachineData([state], 0).Write(w);
        return w.U8(0).ToArray();
    }

    /// <summary>BloodPool (32 B): flag 02 (the create wakes it), 1 property (its altar's id), Positioned, 00 01 00.</summary>
    public static byte[] Pool((int X, int Y) cell, float rotation, uint altar)
    {
        var w = new BlobWriter().U8(2).U8(1);
        w.U32(PropPoolOwner).U8(0).U8(1).U8(1).U32(altar);
        return w.I32(cell.X).I32(cell.Y).F32(rotation).Zeros(4).U8(0).U8(1).U8(0).ToArray();
    }

    /// <summary>RitualBlocker (34 B): flag 02, Positioned at rotation 0 with the scale (radius / 23, 1, 1), 00 01 00 00.</summary>
    public static byte[] Blocker((int X, int Y) cell, int radiusCells) =>
        new BlobWriter().U8(2).U8(0).I32(cell.X).I32(cell.Y).F32(0).U16(0).U8(0x20).U8(0)
            .F32(radiusCells / 23f).F32(1).F32(1).U8(0).U8(1).U8(0).U8(0).ToArray();

    /// <summary>The light's scale as its Positioned update (component 0): 20 00 + f32 x, 1, 1.</summary>
    public static byte[] LightScale(float scale) => new BlobWriter().U8(0x20).U8(0).F32(scale).F32(1).F32(1).ToArray();

    /// <summary>0x165: u32 BE the altar (its Interactable), u32 BE x, u32 BE y, u8 state (0 inactive, 1 ready, 2 running,
    /// 3 done).</summary>
    public static byte[] AltarState(uint altar, (int X, int Y) cell, int state) =>
        new LoginWriter().U16(AltarStateId).U32(altar).U32((uint)cell.X).U32((uint)cell.Y).U8((byte)state).ToArray();

    /// <summary>0x167: u32 BE the area's tribute.</summary>
    public static byte[] Tribute(int total) => new LoginWriter().U16(TributeId).U32((uint)Math.Max(0, total)).ToArray();

    /// <summary>0x166, the favours window (reader 0x1421b5aa0 → 0x14250f110 → 0x14250f280):
    /// u32 BE tribute, u8 rerollsUsed, u8 count, count × (u32 BE itemId, u16 BE buyCost, u16 BE originalCost, u8 flag),
    /// u8 totalAltars, u8 completedAltars. Rows MUST be sorted ascending by ItemId (binary searched by 0x140320c60).</summary>
    public static byte[] Favours(int tribute, int rerollsUsed,
        IReadOnlyList<(uint Id, ushort BuyCost, ushort OriginalCost, byte Flag)> rows, int altars, int done)
    {
        int count = Math.Min(rows.Count, byte.MaxValue);
        var w = new LoginWriter().U16(FavoursId)
            .U32((uint)Math.Max(0, tribute))
            .U8((byte)Math.Clamp(rerollsUsed, 0, byte.MaxValue))
            .U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            var (id, buy, orig, flag) = rows[i];
            w.U32(id).U16(buy).U16(orig).U8(flag);
        }
        return w.U8((byte)Math.Clamp(altars, 0, byte.MaxValue))
            .U8((byte)Math.Clamp(done, 0, byte.MaxValue))
            .ToArray();
    }

    public static byte[] Favours(int tribute, IReadOnlyList<(uint Id, ushort BuyCost, ushort OriginalCost, byte Flag)> rows, int altars, int done) =>
        Favours(tribute, 0, rows, altars, done);

    /// <summary>S→C 0x007b kind 0x38: opens the Ritual favour window bound to Manager-1 <paramref name="inventoryId"/>.</summary>
    public static byte[] OpenFavoursWindow(uint inventoryId = ShopInventoryId) =>
        InstanceWaypoints.OpenUi(FavoursUiKind, inventoryId);

    /// <summary>C→S 0x0283 (18 bytes, writer 0x141fa43e0): buy/claim a revealed favour item.
    /// <see cref="DestX"/> / <see cref="DestY"/> are -1 on Ctrl+click (auto-place).</summary>
    public readonly record struct BuyFavour(uint ItemId, uint DestInventoryRow, int DestX, int DestY);

    public static bool TryReadBuyFavour(ReadOnlySpan<byte> message, out BuyFavour buy)
    {
        if (message.Length < 18) { buy = default; return false; }
        buy = new(
            BinaryPrimitives.ReadUInt32BigEndian(message[2..6]),
            BinaryPrimitives.ReadUInt32BigEndian(message[6..10]),
            BinaryPrimitives.ReadInt32BigEndian(message[10..14]),
            BinaryPrimitives.ReadInt32BigEndian(message[14..18]));
        return true;
    }

    /// <summary>C→S 0x0284 (6 bytes, sender 0x14061cd90): defer a revealed favour item by itemId.</summary>
    public static bool TryReadDeferFavour(ReadOnlySpan<byte> message, out uint itemId)
    {
        if (message.Length < 6) { itemId = 0; return false; }
        itemId = BinaryPrimitives.ReadUInt32BigEndian(message[2..6]);
        return true;
    }
}
