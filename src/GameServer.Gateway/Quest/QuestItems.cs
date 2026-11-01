using GameServer.Core.Protocol.Instance;

/// <summary>
/// Quest items, from their .it Quest component (<see cref="QuestItemFlags"/>; Agents/docs/protocol/12-quests.md §5). The same
/// rules for every quest item: a1q4's glyphs, a1q5's Medicine Chest, a1q6's Allflame, the quest books, Act 2's Thaumetic
/// Emblem …
/// <list type="bullet">
/// <item><c>grant_flag</c> is held while one is carried (any inventory or the cursor): the server sets and clears it to follow
/// the items (after a pickup or an item move, on entering an area, and about once a second);</item>
/// <item><c>extra_flag</c> is set when one is first carried and stays (a1q4-KnowsAboutGlyphs);</item>
/// <item><c>remove_flag</c>: once set, carried copies are taken away (on entering an area or after an item move), a copy on
/// the ground goes when clicked, and none drops again; a dialogue row that sets it hands one in (Nessa's "The Medicine
/// Chest" sets a1q5-NessaCongratulated, Fairgraves' "Thank You" a1q6-DeliveredAllFlame): without one carried the row does
/// nothing;</item>
/// <item><c>use_flag</c> is set by using the item (right-click), which uses it up (the quest books: QuestStaticRewards keys
/// their points by it).</item>
/// </list>
/// A quest item drops (from its monster or chest) unless its remove_flag is set, or one is carried or lies on this
/// instance's ground. The stash does not count: it is the account's, so another character's copy must not keep this one
/// from its item (a character that stashes its own gets another).
/// </summary>
static class QuestItems
{
    /// <summary>Every item the character carries: each of its inventories and the cursor.</summary>
    public static IEnumerable<(byte Row, InventoryItem Item)> Carried(PlayerInventories items)
    {
        var rows = Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r).Append(PlayerInventories.Cursor).Distinct();
        foreach (byte row in rows)
            foreach (var it in items.Items(row))
                yield return (row, it);
    }

    /// <summary>Whether the quest item is done with: its remove_flag is set.</summary>
    public static bool Removed(QuestItemFlags flags, Func<string, bool> has) => flags.Remove is { } remove && has(remove);

    /// <summary>Whether a monster or chest drops the item: not once its remove_flag is set, nor while one is carried or lies
    /// on this instance's ground.</summary>
    public static bool Drops(QuestItemFlags? flags, Func<string, bool> has, bool carried, bool onGround) =>
        !(flags is not null && Removed(flags, has)) && !carried && !onGround;

    /// <summary>The flags a pickup sets that the character lacks (grant_flag, extra_flag); none once the item is done with.</summary>
    public static List<string> PickupFlags(QuestItemFlags flags, Func<string, bool> has) =>
        Removed(flags, has) ? [] : [.. new[] { flags.Grant, flags.Extra }.OfType<string>().Where(f => !has(f)).Distinct()];

    /// <summary>The flag changes that make the grant flags follow the quest items carried (and the extra flags, once one is
    /// carried), over every quest item type <paramref name="types"/> knows; and the types whose carried copies must go (their
    /// remove_flag is set).</summary>
    public static (List<string> Set, List<string> Clear, List<QuestItemFlags> Remove) Sync(IEnumerable<QuestItemFlags> types,
        IReadOnlySet<QuestItemFlags> carried, Func<string, bool> has)
    {
        List<string> set = [], clear = [];
        List<QuestItemFlags> remove = [];
        var grants = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var t in types.Distinct())
        {
            bool removed = Removed(t, has), held = carried.Contains(t);
            if (held && removed) remove.Add(t);
            // Two types may share a grant flag: it is held while any of them is.
            if (t.Grant is { } grant) grants[grant] = grants.GetValueOrDefault(grant) || held && !removed;
            if (held && !removed && t.Extra is { } extra && !has(extra) && !set.Contains(extra)) set.Add(extra);
        }
        foreach (var (grant, want) in grants)
        {
            if (want && !has(grant)) set.Add(grant);
            if (!want && has(grant)) clear.Add(grant);
        }
        return (set, clear, remove);
    }

    /// <summary>The remove_flags a dialogue row's QuestFlag3 lists for carried items whose grant flag the row requires: a row
    /// that takes several items at once names them all there (Sin's "_take_ingredients_", NPCTalk 1336, requires both
    /// ingredients and lists both DeliveredItem flags; its QuestFlag4 is only the first).</summary>
    public static IEnumerable<string> HandInsOfRow(NpcCatalog.Talk talk, IEnumerable<QuestItemFlags> carried) =>
        talk.HeardFlags.Where(f => carried.Any(t => t.Remove == f && t.Grant is { } grant && talk.RequiredFlags.Contains(grant)));

    /// <summary>The flags of the NPC's other available rows with the same text whose QuestFlag4 hands in a carried item: they
    /// go with the chosen one in the same click (Piety's three organs, NPCTalk 624-626, all "Malachai's Organs").</summary>
    public static IEnumerable<string> SiblingHandIns(NpcCatalog.Talk talk, IEnumerable<NpcCatalog.Talk> npcTalks, IReadOnlySet<string> flags,
        IEnumerable<QuestItemFlags> carried) =>
        npcTalks.Where(t => t.Row != talk.Row && t.Option == talk.Option && t.Available(flags) && t.StartFlag is { } f
            && !flags.Contains(f) && carried.Any(c => c.Remove == f)).Select(t => t.StartFlag!);

    /// <summary>The quest item a dialogue row hands in: the type whose remove_flag the row sets (QuestFlag4) while the
    /// character does not have it yet; null for other rows.</summary>
    public static QuestItemFlags? HandedIn(string? startFlag, IEnumerable<QuestItemFlags> types, Func<string, bool> has) =>
        startFlag is null || has(startFlag) ? null : types.FirstOrDefault(t => t.Remove == startFlag);

    /// <summary>The C→S messages after which the carried quest items may have changed (a drop, a place or pick-up, the stash,
    /// a sale, a dev /item): the message path syncs after a read that had one of them.</summary>
    public static bool MovesItems(ushort id) => id is InstanceItems.DropItemId or InstanceItems.PlaceItemId or InstanceItems.PickUpItemId
        or InstanceStash.MoveToTabId or InstanceStash.MoveToAffinityTabId or InstanceStash.TakeFromTabId
        or InstanceNpcs.TradeOfferId or InstanceNpcs.TradeAcceptId or InstanceNpcs.TradeAnswerId or InstanceChat.ChatId;

    /// <summary>A quest item picked up: puts it and sets the flags it brings that the save lacks, in one save transaction.
    /// Refused (null, nothing put) once the save has its remove_flag: a copy left in another instance is not needed any more.</summary>
    public static T? Claim<T>(string character, QuestItemFlags flags, Func<T> put) where T : class => Saves.Atomically(() =>
    {
        var saved = (CharacterSaves.Load(character)?.QuestFlags ?? []).ToHashSet();
        if (Removed(flags, saved.Contains)) return null;
        var placed = put();
        var missing = PickupFlags(flags, saved.Contains);
        if (missing.Count > 0) CharacterSaves.ChangeQuestFlags(character, missing, true);
        return placed;
    });

    sealed class Refused(string message) : Exception(message);

    /// <summary>What <see cref="Consume"/> did: the items it took (by row) and the grant flags it cleared.</summary>
    public sealed record Consumed(List<(byte Row, InventoryItem Item)> Removed, List<string> Cleared);

    /// <summary>A quest object used with quest items (the Thaumetic Seal with the emblem, the tree with the spike and the gem,
    /// the Dark Altar with the Apex): sets the flags and takes one carried copy of every quest item type whose remove_flag is
    /// among them, clearing its grant_flag, in one save transaction. Null (nothing changes) when a copy cannot leave its
    /// inventory (on offer to a vendor).</summary>
    public static Consumed? Consume(string character, PlayerInventories items, Func<Item, QuestItemFlags?> flagsOf, IReadOnlyCollection<string> flags)
    {
        try
        {
            return Saves.Atomically<Consumed?>(() =>
            {
                var removed = new List<(byte Row, InventoryItem Item)>();
                var cleared = new List<string>();
                var done = new HashSet<QuestItemFlags>();
                foreach (var (row, item) in Carried(items).ToList())
                {
                    if (flagsOf(item.Item) is not { Remove: { } remove } type || !flags.Contains(remove) || !done.Add(type)) continue;
                    removed.Add((row, items.Remove(row, item.Id) ?? throw new Refused($"quest item {item.Id} cannot leave row {row}")));
                    if (type.Grant is { } grant && !cleared.Contains(grant)) cleared.Add(grant);
                }
                CharacterSaves.ChangeQuestFlags(character, [.. flags], true);
                if (cleared.Count > 0) CharacterSaves.ChangeQuestFlags(character, cleared, false);
                return new Consumed(removed, cleared);
            });
        }
        catch (Refused)
        {
            return null;   // rolled back
        }
    }

    /// <summary>Takes every carried copy of these types (their remove_flag is set), in one save transaction; the removed items
    /// by row.</summary>
    public static List<(byte Row, InventoryItem Item)> RemoveAll(PlayerInventories items, Func<Item, QuestItemFlags?> flagsOf,
        IReadOnlyCollection<QuestItemFlags> types) => Saves.Atomically(() =>
        Carried(items).Where(c => flagsOf(c.Item.Item) is { } f && types.Contains(f)).ToList()
            .Select(c => (c.Row, Removed: items.Remove(c.Row, c.Item.Id))).Where(c => c.Removed is not null)
            .Select(c => (c.Row, c.Removed!)).ToList());

    /// <summary>A quest item used (C→S 0x58, the quest books): one leaves its stack and its use_flag is set (and its
    /// grant_flag cleared), together. Null when the item is gone (nothing changes).</summary>
    public static PlayerInventories.Consumption? Use(PlayerInventories items, string character, byte row, uint itemId, QuestItemFlags flags) =>
        Saves.Atomically(() =>
        {
            if (flags.Use is not { } use || items.ConsumeOne(row, itemId) is not { } taken) return null;
            CharacterSaves.ChangeQuestFlags(character, [use], true);
            // The books' remove_flag is their use_flag: another copy goes with the next sync, so the grant_flag goes now.
            if (flags.Grant is { } grant) CharacterSaves.ChangeQuestFlags(character, [grant], false);
            return taken;
        });
}
