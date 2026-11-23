using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>QuestRewardOffers and class-filtered QuestRewards. Row zero is a real offer/flag.</summary>
public sealed class QuestRewardCatalog
{
    /// <summary>One choice of an offer (a QuestRewards row). <paramref name="Level"/> is a gem's level and other items' item
    /// level; <see cref="Rarity"/> is the row's rarity column (0 normal, 1 magic; none for gems).</summary>
    public sealed record Choice(uint Id, int ItemRow, int Level, int Stack, IReadOnlyList<int> Classes)
    {
        public int? Rarity { get; init; }
        /// <summary>A league quest flag required for this choice to be offered (<c>__col13</c>, a <see cref="Quests.League"/>
        /// id; the table's out-of-range 1911 is none).</summary>
        public int? RequiredLeagueFlag { get; init; }
    }
    /// <summary>A QuestRewardOffers row. <paramref name="TakenFlag"/> is the row's QuestFlag, null when it has none: the
    /// quest books, keys and other quest items, whose own Quest flags (grant_flag held, remove_flag once used) tell that
    /// the reward was taken (<see cref="Claim"/>).</summary>
    public sealed record Offer(int Row, string? TakenFlag, IReadOnlyList<Choice> Choices)
    {
        /// <summary>The row's league quest flag taken flag (<c>__col3</c>, a <see cref="Quests.League"/> id; the table's
        /// out-of-range 1911 is none): an offer every character of the league takes once, such as a league's tutorial
        /// reward. With it the offer is taken when the account's league holds the flag.</summary>
        public int? TakenLeagueFlag { get; init; }
        /// <summary>Whether each choice is offered whenever not already carried (rather than a single once-only claim):
        /// offers without a <see cref="TakenFlag"/> whose <c>__col5</c> and <c>__col6</c> are both set.</summary>
        public bool Reclaimable { get; init; }
    }
    readonly Dictionary<int, Offer> offers = [];
    public Offer? Find(int row) => offers.GetValueOrDefault(row);
    public IReadOnlyCollection<Offer> Offers => offers.Values;
    public IReadOnlyList<Choice> Choices(int row, int classRow) =>
        Find(row) is { } offer ? [.. offer.Choices.Where(c => c.Classes.Count == 0 || c.Classes.Contains(classRow))] : [];

    public static QuestRewardCatalog Load(string folder = "data", bool hardMode = false, bool supportGems = true)
    {
        var result = new QuestRewardCatalog();
        var names = ClientTables.Table("QuestFlags").Select(f => f.Text("Id")!).ToArray();
        var choices = ClientTables.Table("QuestRewards");
        HashSet<int> supportRows = [];
        if (!supportGems)
            supportRows = ClientTables.Table("SkillGems").Where(r => r.Bool("IsSupport"))
                .Select(r => r.Int("BaseItemTypesKey")).ToHashSet();
        // Pinned 3.29 QuestRewards column16: 0 shared, 1 regular-only, 2 HardMode-only.
        // This selects the Uncarved Gemstone/gear alternatives rather than normal supports or movement gems.
        var byOffer = choices.Where(r => (!r.Has("__col16")
                || r.Int("__col16") == 0 || r.Int("__col16") == (hardMode ? 2 : 1))
                && (supportGems || !supportRows.Contains(r.Int("Reward"))))
            .GroupBy(r => r.Int("RewardOffer"))
            .ToDictionary(g => g.Key, g => g.Select(r => new Choice((uint)r.Int("__col1") + 1,
                r.Int("Reward"), r.Int("RewardLevel"), r.Int("RewardStack"), r.Ints("Characters"))
            {
                Rarity = r.IntOrNull("__col5"),
                RequiredLeagueFlag = r.Has("__col13") && r.IntOrNull("__col13") is int lf and > 0 and <= Quests.League.Count ? lf : null,
            }).ToArray());
        foreach (var r in ClientTables.Table("QuestRewardOffers"))
        {
            int index = r.Index;
            int? league = r.Has("__col3") && r.IntOrNull("__col3") is int l and > 0 and <= Quests.League.Count ? l : null;
            // Without a QuestFlag the offer is taken when its quest item's flags say so (the Act 1 books: offers 5, 6, 9, 11).
            if (r.IntOrNull("QuestFlag") is not int f)
            {
                bool reclaimable = r.Has("__col5") && r.IntOrNull("__col5") is > 0
                    && r.Has("__col6") && r.IntOrNull("__col6") is > 0;
                result.offers[index] = new(index, null, byOffer.GetValueOrDefault(index) ?? [])
                {
                    TakenLeagueFlag = league,
                    Reclaimable = reclaimable,
                };
                continue;
            }
            if (f < 0 || f >= names.Length || names[f] == "NEVER_SET") continue;
            result.offers[index] = new(index, names[f], byOffer.GetValueOrDefault(index) ?? []) { TakenLeagueFlag = league };
        }
        return result;
    }

    // Grant level-one gems/currency and normal flasks at the row's level. With the mode's ItemRolls (Game's OfferNpcReward passes
    // them in every mode) gear is built at its actual item level and rarity: HardMode's magic armours, the later acts' rare
    // belts and amulets. Scripted rewards without a model remain unavailable; OfferNpcReward omits a choice when this
    // returns null.
    public static Item? Create(Choice choice, ItemCatalog items, ItemRolls? rolls = null)
    {
        if (choice.Stack <= 0 || items.Row(choice.ItemRow) is not { Supported: true } type) return null;
        bool normalFlask = type.Components.Contains("Flask") && choice.Rarity == 0 && choice.Level is >= 1 and <= 100;
        if ((choice.Level != 1 && !normalFlask) || choice.Rarity is > 0)
        {
            if (rolls is null || choice.Level is < 1 or > 100 || choice.Rarity is not (0 or 1 or 2)
                || !type.Components.Contains("Mods")) return null;
            var (sockets, links) = RewardSockets(type, choice.Level, items, Random.Shared);
            return rolls.Roll(ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)choice.Level },
                (ItemRarity)choice.Rarity.Value, Random.Shared);
        }
        return ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(choice.Stack, ushort.MaxValue), ItemLevel = (byte)choice.Level };
    }

    // Act-appropriate linked sockets for quest reward gear: 2L in Act 1, up to 3L from level 12+,
    // and up to 4L from level 25+ (capped by the slot's and item level's socket limit).
    static (IReadOnlyList<ItemSocket> Sockets, IReadOnlyList<byte>? Links) RewardSockets(
        ItemCatalog.BaseType type, int itemLevel, ItemCatalog items, Random random)
    {
        if (!type.Components.Contains("Sockets") || ItemCatalog.ResonatorSockets(type) > 0
            || type.Id == AbyssJewels.StygianVise || type.HasOneSocket)
            return items.DefaultSockets(type, itemLevel, random: random);
        int classCap = SystemSettings.Default.SocketMax(type.Class);
        if (classCap <= 0) return items.DefaultSockets(type, itemLevel, random: random);
        int levelCap = itemLevel >= 50 ? 6 : itemLevel >= 35 ? 5 : itemLevel >= 25 ? 4 : itemLevel >= 2 ? 3 : 2;
        int max = Math.Min(classCap, levelCap);
        int target = itemLevel >= 25 ? Math.Min(max, 4) : itemLevel >= 12 ? Math.Min(max, 3) : Math.Min(max, 2);
        if (target <= 0) return ([], null);
        var requirements = items.Requirements(type);
        var sockets = new ItemSocket[target];
        for (int i = 0; i < target; i++)
            sockets[i] = new(SocketColours.Roll(requirements, itemLevel, 0, random));
        return (sockets, [(byte)target]);
    }

    /// <summary>Pack rewards using their actual sizes; HardMode gear choices must not overlap like 1×1 gems.</summary>
    public static IReadOnlyList<InventoryItem> Arrange(IEnumerable<(uint Id, Item Item)> rewards, ItemCatalog catalog)
    {
        var placed = new List<(InventoryItem Placement, byte Width, byte Height)>();
        foreach (var (id, item) in rewards)
        {
            var type = catalog.ByHash(item.TypeHash) ?? throw new InvalidOperationException("Unknown quest reward type");
            (byte X, byte Y)? cell = null;
            for (byte y = 0; y + type.Height <= NpcShops.Tab.Height && cell is null; y++)
                for (byte x = 0; x + type.Width <= NpcShops.Tab.Width; x++)
                    if (!placed.Any(p => p.Placement.X < x + type.Width && x < p.Placement.X + p.Width
                        && p.Placement.Y < y + type.Height && y < p.Placement.Y + p.Height))
                    { cell = (x, y); break; }
            if (cell is not { } at) throw new InvalidOperationException("Quest reward choices exceed the native reward grid");
            placed.Add((new InventoryItem(id, at.X, at.Y, item), type.Width, type.Height));
        }
        return [.. placed.Select(p => p.Placement)];
    }

    /// <summary>Whether a character with these flags has taken the offer: its taken flag, or for an offer without one, the
    /// reward's quest item held or used (its grant_flag or remove_flag). An offer with neither cannot be told apart, so it
    /// counts as taken (it is not offered).</summary>
    public static bool Taken(string? takenFlag, QuestItemFlags? quest, Func<string, bool> has) =>
        takenFlag is not null ? has(takenFlag)
        : quest?.Grant is not { } grant || has(grant) || quest.Remove is { } remove && has(remove);

    /// <summary>Puts the reward into an admitted carried grid and records the offer as taken (its taken flag, or the quest item's
    /// pickup flags: an Act 1 book's a1q7-HaveRewardBook), in one save transaction. Null (nothing changes) when it is taken
    /// already or does not fit. With <paramref name="leagueFlag"/> (an <see cref="Offer.TakenLeagueFlag"/>) and the account's
    /// <paramref name="leagueFlags"/> stash, the offer is also taken when that stash holds the flag, and the claim sets it in
    /// the same transaction.</summary>
    public static PlayerInventories.Stacking? Claim(string character, string? takenFlag, QuestItemFlags? quest, PlayerInventories inventory,
        Item reward, Func<Item, PlayerInventories.Shape?> shapeOf, byte row = PlayerInventories.MainInventory,
        bool expandedAllowed = false, int? leagueFlag = null, AccountStash? leagueFlags = null, bool reclaimable = false) => Saves.Atomically(() =>
    {
        if (row != PlayerInventories.MainInventory && (row != PlayerInventories.ExpandedInventory || !expandedAllowed)) return null;
        // Read the durable flags again inside the transaction: a retry or another connection cannot duplicate a reward.
        var saved = (CharacterSaves.Load(character)?.QuestFlags ?? []).ToHashSet();
        bool league = leagueFlag is int lf && leagueFlags is not null;
        // A reclaimable offer prevents claiming duplicates while allowing any missing unlocked item;
        // otherwise an offer with only a league flag is taken by it alone (Taken(null, null) would call it taken already).
        bool taken = league && leagueFlags!.LeagueQuestFlags.Contains(leagueFlag!.Value)
            || (reclaimable
                ? QuestItems.Carried(inventory).Any(c => c.Item.Item.TypeHash == reward.TypeHash)
                : (!league || takenFlag is not null || quest is not null) && Taken(takenFlag, quest, saved.Contains));
        if (taken || !inventory.Fits(row, reward, shapeOf)) return null;
        var added = inventory.AddStacked(row, reward, shapeOf);
        if (added.Left != 0) throw new InvalidOperationException("A quest reward did not fit after its capacity check");
        if (league) leagueFlags!.ChangeLeagueQuestFlags([leagueFlag!.Value], true);
        var flags = new List<string>();
        if (takenFlag is not null) flags.Add(takenFlag);
        if (quest is not null) flags.AddRange(new[] { quest.Grant, quest.Extra }.OfType<string>().Where(f => !saved.Contains(f)));
        if (flags.Count > 0) CharacterSaves.ChangeQuestFlags(character, flags, true);
        return added;
    });

    /// <summary>Result of claiming all available rewards from a multi-item quest reward offer (<c>C→S 0x008b</c>).</summary>
    public sealed record ClaimAllResult(
        IReadOnlyList<uint> RemovedMainIds,
        IReadOnlyList<InventoryItem> AddedMain,
        IReadOnlyList<uint> RemovedExpandedIds,
        IReadOnlyList<InventoryItem> AddedExpanded,
        IReadOnlyList<uint> ClaimedOfferIds,
        IReadOnlyList<string> GrantedFlags,
        bool LeagueFlagSet);

    /// <summary>Claims every eligible reward from a multi-item offer ("TAKE ALL", <c>C→S 0x008b</c>) into the player's
    /// carried inventories in one save transaction. For reclaimable offers, skips any item already carried.</summary>
    public static ClaimAllResult? ClaimAll(string character, string? takenFlag, PlayerInventories inventory,
        IReadOnlyList<(uint OfferItemId, Item Reward, QuestItemFlags? Quest)> rewards,
        Func<Item, PlayerInventories.Shape?> shapeOf, bool expandedAllowed = false,
        int? leagueFlag = null, AccountStash? leagueFlags = null, bool reclaimable = false) => Saves.Atomically(() =>
    {
        if (rewards.Count == 0) return null;
        var saved = (CharacterSaves.Load(character)?.QuestFlags ?? []).ToHashSet();
        bool league = leagueFlag is int lf && leagueFlags is not null;
        if (league && leagueFlags!.LeagueQuestFlags.Contains(leagueFlag!.Value)) return null;
        if (!reclaimable && takenFlag is not null && saved.Contains(takenFlag)) return null;

        var removedMain = new List<uint>();
        var addedMain = new List<InventoryItem>();
        var removedExpanded = new List<uint>();
        var addedExpanded = new List<InventoryItem>();
        var claimedIds = new List<uint>();
        var granted = new List<string>();
        var carriedTypes = reclaimable
            ? QuestItems.Carried(inventory).Select(c => c.Item.Item.TypeHash).ToHashSet()
            : null;
        int eligible = 0;

        foreach (var (offerItemId, reward, quest) in rewards)
        {
            if (reclaimable)
            {
                if (carriedTypes!.Contains(reward.TypeHash)) continue;
            }
            else if (takenFlag is null && (!league || quest is not null) && Taken(null, quest, saved.Contains)) continue;
            eligible++;

            byte dest;
            if (inventory.Fits(PlayerInventories.MainInventory, reward, shapeOf))
                dest = PlayerInventories.MainInventory;
            else if (expandedAllowed && inventory.Fits(PlayerInventories.ExpandedInventory, reward, shapeOf))
                dest = PlayerInventories.ExpandedInventory;
            else
                continue;

            var placed = inventory.AddStacked(dest, reward, shapeOf);
            if (placed.Left != 0) throw new InvalidOperationException("A quest reward did not fit after its capacity check");
            if (dest == PlayerInventories.MainInventory)
            {
                removedMain.AddRange(placed.RemovedIds);
                addedMain.AddRange(placed.AddedItems);
            }
            else
            {
                removedExpanded.AddRange(placed.RemovedIds);
                addedExpanded.AddRange(placed.AddedItems);
            }
            claimedIds.Add(offerItemId);
            carriedTypes?.Add(reward.TypeHash);
            if (quest is not null)
                foreach (var f in new[] { quest.Grant, quest.Extra }.OfType<string>())
                    if (saved.Add(f)) granted.Add(f);
        }

        if (claimedIds.Count == 0) return null;
        bool allClaimed = claimedIds.Count == eligible;
        if (league && (reclaimable || allClaimed)) leagueFlags!.ChangeLeagueQuestFlags([leagueFlag!.Value], true);
        if (takenFlag is not null && (reclaimable || allClaimed) && saved.Add(takenFlag)) granted.Add(takenFlag);
        if (granted.Count > 0) CharacterSaves.ChangeQuestFlags(character, granted, true);
        return new ClaimAllResult(removedMain, addedMain, removedExpanded, addedExpanded, claimedIds, granted, league && (reclaimable || allClaimed));
    });
}
