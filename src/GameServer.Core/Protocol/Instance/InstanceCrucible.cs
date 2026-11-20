using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Wire messages for weapon passive tree operations and Forge of the Titans crafting UI (3.29.3c).
/// All packet header/body integers are big-endian via <see cref="LoginWriter"/> / <see cref="LoginReader"/>.
/// </summary>
public static class InstanceCrucible
{
    /// <summary>Rulesets.dat row 137 (0x89, CrucibleRules) and row 140 (0x8c, CrucibleMapRules) for S→C 0x1f / 0x02c9.</summary>
    public const byte CrucibleRulesetRow = 0x89;
    public const byte CrucibleMapRulesetRow = 0x8c;

    /// <summary>ActiveSkills row 1815 (<c>crucible_channel</c>, ActionTypes 1163 <c>CrucibleChannel</c>) action id
    /// (<c>HASH16 0x5f29 &amp; 0x3fff = 0x1f29</c>, matching <c>SkillCatalog</c>'s 14-bit action/skill id space).</summary>
    public const ushort CrucibleChannelSkillHash = 0x1f29;

    // ---- S→C 0x02c9 (CrucibleRules 0x89 sub-messages, 0x141682b90, LE payload) ------------------------------------------

    /// <summary>Sub-message 0x00: player entered the Crucible Forge zone (inZone = 1; plays CruciblePopup / shows KB/M popup).</summary>
    public static byte[] ForgeEnterZone() => InstanceProtocol.RulesetMessage(CrucibleRulesetRow, [0x00]);

    /// <summary>Sub-message 0x01: player left the Crucible Forge zone (inZone = 0; plays CruciblePopupFade / hides popup).</summary>
    public static byte[] ForgeLeaveZone() => InstanceProtocol.RulesetMessage(CrucibleRulesetRow, [0x01]);

    /// <summary>Sub-message 0x02: lock the selected weapon at the forge (weaponLocked = 1; hides Back button).</summary>
    public static byte[] ForgeLockWeapon() => InstanceProtocol.RulesetMessage(CrucibleRulesetRow, [0x02]);

    /// <summary>Sub-message 0x03 + f32 LE charge (0.0..1.0): updates CrucibleDifficulty bar, CrucibleChannel action and CrucibleThreat shader.</summary>
    public static byte[] ForgeCharge(float charge) =>
        InstanceProtocol.RulesetMessage(CrucibleRulesetRow, new BlobWriter().U8(0x03).F32(charge).ToArray());

    /// <summary>Sub-message 0x04 + u8 active: plays CrucibleChannelStart+Loop (true) or CrucibleChannelEnd (false).</summary>
    public static byte[] ForgeChannelling(bool active) =>
        InstanceProtocol.RulesetMessage(CrucibleRulesetRow, [0x04, (byte)(active ? 1 : 0)]);

    /// <summary>Sub-message 0x05 + u8 state: updates forge FMOD state and threat fade (state == 2 closes gamepad popup; >= 3 decays fade).</summary>
    public static byte[] ForgeState(byte state) =>
        InstanceProtocol.RulesetMessage(CrucibleRulesetRow, [0x05, state]);

    /// <summary>Sub-message 0x06 + u32 LE forgeEntityId: opens the gamepad Forge popup bound to the forge entity.</summary>
    public static byte[] ForgeOpenGamepadPopup(uint forgeEntityId) =>
        InstanceProtocol.RulesetMessage(CrucibleRulesetRow, new BlobWriter().U8(0x06).U32(forgeEntityId).ToArray());

    /// <summary>S→C 0x7b kind 0x4b argument 1 modes (CrucibleAddWeaponTreeToItemCraftingWindow, 0x1403d6fd0).</summary>
    public const uint AntiqueForgeMode = 0;
    public const uint AntiqueForgeCorruptedMode = 1;
    public const uint ScouringForgeMode = 2;

    /// <summary>S→C 0x7b kind 0x4a (CrucibleCombiningWindow): arg1 = 0 normal (non-uniques), 1 uber (allows uniques).</summary>
    public static byte[] OpenCombiningUi(bool uber = false) =>
        InstanceWaypoints.OpenUi(InstanceWaypoints.CrucibleCombiningUi, uber ? 1u : 0u);

    /// <summary>S→C 0x7b kind 0x4b (CrucibleAddWeaponTreeToItemCraftingWindow): arg1 = mode (0/1/2), arg2 = forge entity id.</summary>
    public static byte[] OpenUniqueForgeUi(uint mode, uint forgeEntityId) =>
        InstanceWaypoints.OpenUi(InstanceWaypoints.CrucibleUniqueForgeUi, mode, forgeEntityId);

    // ---- C→S 0x0116: Orb of Scouring weapon passive node unallocation --------------------------------------------------

    public const ushort WeaponPassiveUnallocateId = 0x0116;
    public const int WeaponPassiveUnallocateLength = 26;

    /// <summary>
    /// C→S 0x0116 (0x1435ca4e0 / writer 0x141fa09e0, 26 bytes BE): unallocate the deepest allocated node of a weapon
    /// passive tree using an Orb of Scouring.
    /// </summary>
    public sealed record WeaponPassiveUnallocate(
        uint CurrencyInventoryId, uint CurrencyItemId,
        uint TargetInventoryId, uint TargetItemId,
        uint Column, uint Row);

    public static WeaponPassiveUnallocate ReadWeaponPassiveUnallocate(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
    }

    // ---- C→S 0x0299 / S→C 0x029a: Crucible Combining Bench -------------------------------------------------------------

    public const ushort CombineRequestId = 0x0299;
    public const int CombineRequestLength = 18;
    public const ushort CombineResultId = 0x029a;

    /// <summary>C→S 0x0299 (0x1403e2490 → 0x141fa43e0, 18 bytes BE): combine top (sacrificed) onto bottom (surviving) item.</summary>
    public sealed record CombineRequest(
        uint TopInventoryId, uint TopItemId,
        uint BottomInventoryId, uint BottomItemId);

    public static CombineRequest ReadCombineRequest(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32(), r.U32(), r.U32(), r.U32());
    }

    /// <summary>3-bit mutation flag mask on S→C 0x029a entries (0x14309f3a0 → 0x1403f0fc0).</summary>
    [Flags]
    public enum NodeMutationFlags : byte
    {
        None = 0,
        UpgradedTier = 0x01,
        DowngradedTier = 0x02,
        Mutated = 0x04,
    }

    public sealed record NodeMutation(uint Column, uint Row, NodeMutationFlags Flags);

    /// <summary>
    /// S→C 0x029a (0x1421b7790 → 0x14250a640 → 0x1403e2d60): combining result and node mutation visual list.
    /// Send alongside 0xbf removing the top item and updating the bottom item.
    /// </summary>
    public static byte[] CombineResult(uint resultInventoryId, uint resultItemId, IReadOnlyList<NodeMutation>? mutations = null)
    {
        var list = mutations ?? [];
        var w = new LoginWriter().U16(CombineResultId).U32(resultInventoryId).U32(resultItemId);
        InstanceProtocol.CompactUInt(w, (uint)list.Count);
        foreach (var m in list) w.U32(m.Column).U32(m.Row).U8((byte)m.Flags);
        return w.ToArray();
    }

    // ---- C→S 0x029b / S→C 0x029c: Antique / Scouring Furnace -----------------------------------------------------------

    public const ushort UniqueForgeRequestId = 0x029b;
    public const int UniqueForgeRequestLength = 10;
    public const ushort UniqueForgeResultId = 0x029c;

    /// <summary>C→S 0x029b (0x1403d7d70 → 0x141f98f90, 10 bytes BE): add tree to unique or remove tree from non-unique.</summary>
    public sealed record UniqueForgeRequest(uint InventoryId, uint ItemId);

    public static UniqueForgeRequest ReadUniqueForgeRequest(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32(), r.U32());
    }

    /// <summary>S→C 0x029c (0x141fa51e0 → 0x1403d7fa0, 10 bytes BE): furnace completion ack; send alongside 0xbf update.</summary>
    public static byte[] UniqueForgeResult(uint inventoryId, uint itemId) =>
        new LoginWriter().U16(UniqueForgeResultId).U32(inventoryId).U32(itemId).ToArray();
}
