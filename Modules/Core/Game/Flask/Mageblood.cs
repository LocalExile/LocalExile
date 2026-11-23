using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Mageblood's three mods, each read from its own stat on any worn item, in any slot: how many magic utility flasks
    // always apply, counted from the left (num_magic_utility_flasks_always_apply) or, on the Foulborn belt, from the
    // right (num_magic_utility_flasks_always_apply_rightmost, the mutated mod DivinationEquipmentRewards puts on it),
    // and "Magic Utility Flasks cannot be Used" (magic_utility_flasks_cannot_be_used). The belt has all of them; an
    // item that carries one of the lines (a mod's, a later league's) gets that line's effect and no other.
    // The client shows the item and enforces "cannot be used" itself (the mod); the server is the backstop here.
    // The flask buffs are kept in step by StepUniqueBeltEffects (Modules/Core/Item/UniqueBelts/UniqueBeltsFeature.cs).
    // "Effects cannot be removed": nothing in the server removes a running flask effect besides expiry, death and the
    // flask leaving the bar, which the game does too, so there is nothing to guard yet.
    public const uint MagebloodLeftmostStat = Stat.NumMagicUtilityFlasksAlwaysApply;
    public const uint MagebloodRightmostStat = Stat.NumMagicUtilityFlasksAlwaysApplyRightmost;
    public static readonly uint MagebloodNoUseStat = Stat.Of("magic_utility_flasks_cannot_be_used");

    // One stat summed over the worn items whose requirements are met, whatever slot they are in. Each item's sum is
    // remembered (PlainItemSum), so this is a lookup an item.
    static int WornActiveStat(Player player, uint stat, IEnumerable<Item>? activeItems = null)
    {
        var mods = ItemsFor(player).Mods;
        long sum = 0;
        if (activeItems is not null)
            foreach (var item in activeItems) sum += PlainItemSum(item, mods).GetValueOrDefault(stat);
        else
            foreach (var (_, worn) in EquipmentForStats(player))
                if (!IsInactive(player, worn.Id)) sum += PlainItemSum(worn.Item, mods).GetValueOrDefault(stat);
        return (int)Math.Clamp(sum, int.MinValue, int.MaxValue);
    }

    /// <summary>How many magic utility flasks the worn items keep applied (2-4 on Mageblood in 3.29, at most the five
    /// flask slots), and whether they are counted from the right (Foulborn Mageblood) instead of the left. Any worn
    /// item with the stat counts, in any slot; <paramref name="activeItems"/> when the caller has them already.</summary>
    public static (int Count, bool Rightmost) MagebloodCount(Player player, IEnumerable<Item>? activeItems = null)
    {
        var active = activeItems as Item[] ?? activeItems?.ToArray();
        int right = WornActiveStat(player, MagebloodRightmostStat, active);
        return right > 0 ? (Math.Clamp(right, 0, 5), true)
            : (Math.Clamp(WornActiveStat(player, MagebloodLeftmostStat, active), 0, 5), false);
    }

    /// <summary>Whether the item is a magic utility flask: the flasks Mageblood's mod applies to (not normal or unique
    /// flasks, and not life/mana flasks).</summary>
    public static bool IsMagicUtilityFlask(Item item) => IsMagicUtilityFlask(item, Items);

    public static bool IsMagicUtilityFlask(Item item, ItemCatalog items) =>
        item.Rarity == ItemRarity.Magic && items.ByHash(item.TypeHash) is { } type && items.UtilityFlaskOf(type) is not null;

    /// <summary>The flasks Mageblood keeps applied: the leftmost (or rightmost) <paramref name="count"/> magic utility
    /// flasks of the flask bar, in bar order. Other flasks in between do not count.</summary>
    public static IReadOnlyList<InventoryItem> MagebloodFlasks(IEnumerable<InventoryItem> bar, (int Count, bool Rightmost) count) =>
        MagebloodFlasks(bar, count, Items);

    public static IReadOnlyList<InventoryItem> MagebloodFlasks(IEnumerable<InventoryItem> bar, (int Count, bool Rightmost) count,
        ItemCatalog items)
    {
        var magic = bar.Where(i => IsMagicUtilityFlask(i.Item, items)).OrderBy(i => i.X).ToList();
        int take = Math.Clamp(count.Count, 0, magic.Count);
        return count.Rightmost ? magic[^take..] : magic[..take];
    }

    /// <summary>The sources of the magic utility flasks Mageblood keeps applied, or none when it is not worn. The flasks'
    /// own effect mods count; <paramref name="character"/> is the character's stats for increased flask effect/duration.</summary>
    public static IReadOnlyList<UtilityFlasks.Source> MagebloodSources(Player player, Func<uint, int> character,
        int? playerLevel = null)
    {
        var items = ItemsFor(player);
        var count = MagebloodCount(player);
        if (count.Count == 0) return [];
        List<UtilityFlasks.Source> sources = [];
        double now = AreaTime(player);
        foreach (var flask in MagebloodFlasks(ItemsOf(player).Items(PlayerInventories.Flask), count, items))
            if (items.ByHash(flask.Item.TypeHash) is { } type && items.UtilityFlaskOf(type) is { } utility)
            {
                int adjacentEffect = flask.X <= byte.MaxValue ? AdjacentTinctureFlaskEffect(player, (byte)flask.X, character, now) : 0;
                Func<uint, int> flaskCharacter = adjacentEffect == 0 ? character
                    : stat => stat == Stat.FlaskEffectPlusPct ? character(stat) + adjacentEffect : character(stat);
                sources.AddRange(ItemUtilityFlaskFor(utility, flask.Item, ItemModStats.Local(flask.Item, items.Mods), flaskCharacter, playerLevel ?? player.Pools.Level).Stats);
            }
        return sources;
    }

    /// <summary>A running flask and Mageblood's permanent one can be the same effect: combine both source sets before
    /// one strongest-per-(effect, stat) pass, so the effect counts once (a Granite Flask gives 1500 armour, not 3000)
    /// while different effects still stack.</summary>
    public static IReadOnlyList<(uint Stat, int Value)> CombineUtilitySources(
        IEnumerable<UtilityFlasks.Source> running, IEnumerable<UtilityFlasks.Source> permanent) =>
        UtilityFlasks.Strongest(running.Concat(permanent));

    /// <summary>The utility flask stats that count now: the running effects deduplicated against Mageblood's permanent
    /// ones. Callers apply this once, not the two sets separately.</summary>
    public static IReadOnlyList<(uint Stat, int Value)> UtilityStats(Player player, Func<uint, int> character,
        int? playerLevel = null) =>
        CombineUtilitySources(player.UtilityFlaskEffects.Sources()
            .Concat(KillOnslaughtSources(player, character))
            .Concat(CatOnslaughtSources(player, character))
            .Concat(ChargeOnslaughtSources(player, character))
            .Concat(player.RecoveryFlaskStats.Sources(player.FlaskEffects)),
            MagebloodSources(player, character, playerLevel));

    /// <summary>Whether any modelled source currently grants Onslaught. Cat's owned effect is independent of the
    /// kill/flask state and therefore remains true when either of those ends.</summary>
    public static bool HasOnslaught(Player player) => HasIndependentOnslaught(player) || ChargeOnslaughtActive(player);

    public static bool HasIndependentOnslaught(Player player)
    {
        return player.UtilityFlaskEffects.ShowsOnslaught
            || player.MagebloodBuffs.Values.Any(b => b.Extra is not null)
            || KillOnslaughtActiveLocked(player)
            || CatOnslaughtActiveLocked(player);
    }

    /// <summary>Whether a worn item forbids using this flask: magic utility flasks cannot be used while Mageblood's
    /// "cannot be Used" line is worn (normal and unique ones still can).</summary>
    public static bool MagebloodBlocksFlaskUse(Player player, Item flask) =>
        IsMagicUtilityFlask(flask, ItemsFor(player)) && WornActiveStat(player, MagebloodNoUseStat) > 0;

    /// <summary>After the flask bar changed while Mageblood is worn: apply the new permanent flask stats now (the pools, the
    /// walk speed and the client's stats), instead of waiting for an equipment change or a relogin.</summary>
    public static async Task RefreshMagebloodFlasks(Player player)
    {
        if (MagebloodCount(player).Count == 0) return;
        RecomputePoolsAndSpeed(player);
        await SendPlayerStatSnapshot(player,
            () => PlayerStatsOffFrames(player),
            "0x01b3 player stats (Mageblood flasks)");
    }
}
