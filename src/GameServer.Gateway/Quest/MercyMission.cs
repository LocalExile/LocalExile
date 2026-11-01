using GameServer.Core.Protocol.Instance;

/// <summary>
/// a1q5 "Mercy Mission" (Agents/docs/protocol/12-quests.md §7). Nessa asks for a medicine chest (her NPCTalk "Lioneye's
/// Watch" sets <see cref="ReceivedQuest"/>), Bestel says where his ship ran aground (<see cref="BestelClue"/>), Hailrake
/// guards the wreck on The Tidal Island and drops the Medicine Chest when he dies; Nessa takes it ("The Medicine Chest":
/// <see cref="NessaCongratulated"/>) and offers the rewards (QuestRewardOffers 1 and 2).
/// The dialogue flags are NPCTalk's own and the quest log follows the flags on the client (QuestStates); this class holds
/// the item rules the server must apply itself.
/// </summary>
static class MercyMission
{
    public const string Area = "1_1_2a";
    public const string ReceivedQuest = "a1q5-ReceivedQuest", BestelClue = "a1q5-BestelClue", HaveMedicineChest = "a1q5-HaveMedicineChest",
        NessaCongratulated = "a1q5-NessaCongratulated", QuestRewardTaken = "a1q5-QuestRewardTaken";

    /// <summary>MonsterVarieties 2887, WorldAreas 1_1_2a's Bosses: placed at the medicine_boat room's "mapboss".</summary>
    public const string Hailrake = "Metadata/Monsters/Cannibal/CannibalMaleBossHailrake";

    /// <summary>BaseItemTypes 1534, "Medicine Chest" (2×3, item class 31). Its .it Quest component: grant_flag
    /// a1q5-HaveMedicineChest (held while carried), remove_flag a1q5-NessaCongratulated (gone once Nessa has it).</summary>
    public const string MedicineChest = "Metadata/Items/QuestItems/MedicineSet1";

    /// <summary>QuestRewardOffers 1 (Nessa's "Medicine Chest Reward"): a normal Medium Life, Medium Mana or Quicksilver Flask
    /// at item level 4; QuestRewardOffers 2 ("Medicine Chest Reward 2", a1q5-QuestReward2Taken): a level 1 support gem of
    /// the character's class. Both are listed by the client once Nessa has the chest.</summary>
    public const int RewardOffer = 1, SupportRewardOffer = 2;

    /// <summary>The rooms around Hailrake's "mapboss" (Area2a, tag medicineboat). They have two doodads each, too few for
    /// the doodad fit, so the room points take their captured placement (AreaPopulation.FitRooms); no other room does.</summary>
    public static readonly IReadOnlySet<string> BoatRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Metadata/Terrain/Act1/Area2a/Rooms/medicine_boat_1.arm", "Metadata/Terrain/Act1/Area2a/Rooms/medicine_boat_2.arm",
    };

    /// <summary>Whether Nessa has the chest already (or the reward is taken): no chest is needed, dropped or kept any more.</summary>
    public static bool Done(Func<string, bool> has) => has(NessaCongratulated) || has(QuestRewardTaken);

    /// <summary>Whether this monster's death is the one that can drop the chest.</summary>
    public static bool DropsChest(string areaId, string varietyId) => areaId == Area && varietyId == Hailrake;

    /// <summary>Every item the character carries: each of its inventories and the cursor.</summary>
    public static IEnumerable<(byte Row, InventoryItem Item)> Carried(PlayerInventories items)
    {
        var rows = Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r).Append(PlayerInventories.Cursor).Distinct();
        foreach (byte row in rows)
            foreach (var it in items.Items(row))
                yield return (row, it);
    }

    /// <summary>Whether Hailrake's death drops the chest: not once Nessa has it, nor while this character carries one or one
    /// lies on this instance's ground. The stash is the account's, so a chest stashed by another character does not count
    /// (nor this one's own: its tracker would say "Find the Medicine Chest" while the chest is out of reach). A chest lost
    /// any other way (left in another instance, sold, destroyed) drops again from the next Hailrake.</summary>
    public static bool HailrakeDrops(uint chestHash, bool done, PlayerInventories items, InstanceGround ground) =>
        !done && !Carried(items).Any(c => c.Item.Item.TypeHash == chestHash) && !ground.All().Any(e => e.Item.Item.TypeHash == chestHash);

    /// <summary>The flag changes that make <see cref="HaveMedicineChest"/> follow the chest (the .it grant_flag): set while
    /// one is carried, cleared when none is or once Nessa has it; and whether carried chests must go (the remove_flag).</summary>
    public static (bool? Set, bool Remove) Sync(bool flagged, bool done, bool carried)
    {
        bool want = !done && carried;
        return (want == flagged ? null : want, done && carried);
    }

    /// <summary>What a click on a ground item does: not a chest (the normal pickup), a chest the character still needs (the
    /// normal pickup, then the flag), or a chest after Nessa has one (the player walks to it and it is removed).</summary>
    public enum Pickup { NotQuestItem, PickUp, Remove }

    public static Pickup PickupOf(bool isChest, bool done) => !isChest ? Pickup.NotQuestItem : done ? Pickup.Remove : Pickup.PickUp;

    /// <summary>Whether choosing an NPCTalk row with this start flag hands the chest in: Nessa's "The Medicine Chest", while
    /// she does not have it yet.</summary>
    public static bool HandsIn(string? startFlag, bool done) => startFlag == NessaCongratulated && !done;

    /// <summary>The C→S messages after which the carried chest may have changed (a drop, a place or pick-up, the stash, a
    /// sale, a dev /item): the message path syncs after a read that had one of them.</summary>
    public static bool MovesItems(ushort id) => id is InstanceItems.DropItemId or InstanceItems.PlaceItemId or InstanceItems.PickUpItemId
        or InstanceStash.MoveToTabId or InstanceStash.MoveToAffinityTabId or InstanceStash.TakeFromTabId
        or InstanceNpcs.TradeOfferId or InstanceNpcs.TradeAcceptId or InstanceNpcs.TradeAnswerId or InstanceChat.ChatId;

    sealed class HandInRefused(string message) : Exception(message);

    /// <summary>Nessa takes the chest: removes one carried chest, sets <see cref="NessaCongratulated"/> and clears
    /// <see cref="HaveMedicineChest"/>, in one save transaction. The removed item and its row, or null (nothing changes)
    /// when no chest is carried. The lookup runs inside the transaction, which is one at a time: of two quick choices only
    /// the first finds the chest.</summary>
    public static (byte Row, InventoryItem Item)? HandIn(string character, PlayerInventories items, uint chestHash)
    {
        try
        {
            return Saves.Atomically<(byte Row, InventoryItem Item)?>(() =>
            {
                if (Carried(items).FirstOrDefault(c => c.Item.Item.TypeHash == chestHash) is not { Item: not null } found) return null;
                var removed = items.Remove(found.Row, found.Item.Id) ?? throw new HandInRefused($"chest {found.Item.Id} cannot leave row {found.Row}");
                CharacterSaves.ChangeQuestFlags(character, [NessaCongratulated], true);
                CharacterSaves.ChangeQuestFlags(character, [HaveMedicineChest], false);
                return (found.Row, removed);
            });
        }
        catch (HandInRefused)
        {
            return null;   // rolled back: the chest was on offer to a vendor
        }
    }

    /// <summary>Takes every carried chest (the remove_flag once Nessa has one: a copy picked up in another instance or taken
    /// from the stash), in one save transaction; the removed items by row.</summary>
    public static List<(byte Row, InventoryItem Item)> RemoveAll(PlayerInventories items, uint chestHash) => Saves.Atomically(() =>
        Carried(items).Where(c => c.Item.Item.TypeHash == chestHash).ToList()
            .Select(c => (c.Row, Removed: items.Remove(c.Row, c.Item.Id))).Where(c => c.Removed is not null)
            .Select(c => (c.Row, c.Removed!)).ToList());
}
