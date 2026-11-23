using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Messages in a Bottle (<c>BaseItemTypes#5437</c> <c>Metadata/Items/Deepwater/DeepwaterBottledItem</c>):
/// right-clicking a non-Dredge bottle (<c>C→S 0x0058</c>, <see cref="GameEvents.ItemUse"/>) consumes it, rolls an
/// outcome from <c>DeepwaterLootBottles</c> (14 Normal / 12 HardMode rows, weighted by <c>world.db</c>
/// <c>system_settings</c> keys <c>deepwater_bottle_weight_&lt;Id&gt;</c>), places the reward into <c>Cursor1</c>
/// (inventory row 12) at <c>(0, 0)</c> via <c>0x00bf</c>, and opens <c>BottledItemRevealWindow</c>
/// (<c>S→C 0x007b</c> kind <c>0x6d</c>, <c>c = ClientStrings2._row</c>: <c>256..269</c> from
/// <c>DeepwaterLootBottles.Description</c> or <c>270..280</c> from <c>DeepwaterLootBottleUniqueMessages.Message</c>).
/// Client wire facts are H (<c>0x142169dd0</c> case <c>0x6d</c> → <c>0x140b79340</c>); outcome weights are G
/// (server-side policy estimated from <c>wiki.md</c>).</summary>
public static class DeepwaterBottles
{
    /// <summary>BaseItemTypes#5437: Message in a Bottle (2×2, StackSize 1).</summary>
    public const string BottleItemId = "Metadata/Items/Deepwater/DeepwaterBottledItem";

    /// <summary>S→C 0x7b kind <c>BottledItemRevealWindow</c>: <c>a=0, b=0, c=ClientStrings2._row, d=0</c>.
    /// Binds its 2×2 item frame directly to <c>Cursor1</c> (inventory row 12).</summary>
    public const byte RevealUi = 0x6d;

    /// <summary>Prefix for tunable <c>world.db</c> <c>system_settings</c> outcome weights
    /// (<c>deepwater_bottle_weight_&lt;Id&gt;</c>).</summary>
    public const string WeightKeyPrefix = "deepwater_bottle_weight_";

    const string HardModeSuffix = "HardMode";
    const string HinekorasLockId = "Metadata/Items/Currency/CurrencyHinekorasLock";
    const string FracturingOrbId = "Metadata/Items/Currency/CurrencyFractureRare";
    const string DivineOrbId = "Metadata/Items/Currency/CurrencyModValues";
    const string ReflectingMistId = "Metadata/Items/Currency/ReflectiveMist";
    const string StackedDeckId = "Metadata/Items/DivinationCards/DivinationCardDeck";
    const string BrokenMirrorId = "Metadata/Items/Currency/CurrencyDuplicateShattered";
    const string AlbinoCrabClawId = "Metadata/Items/Currency/CurrencyAlbinoCrabClaw";
    const string ChainHookGemId = "Metadata/Items/Gems/SkillGemChainHook";
    const int ChainHookOfAnglingVariant = 1; // GemEffects#724 ChainHookAltX ("Chain Hook of Angling")

    /// <summary>One row of <c>DeepwaterLootBottles</c>: <c>Id</c>, canonical <c>BaseId</c> (with any
    /// <c>HardMode</c>/<c>Hardmode</c> suffix stripped), 0-based <c>ClientStrings2</c> row (<c>Description</c>), and
    /// whether the row belongs to Ruthless (<c>__col4</c>).</summary>
    public sealed record BottleOutcome(int Row, string Id, string BaseId, int Description, bool IsHardMode);

    /// <summary>One row of <c>DeepwaterLootBottleUniqueMessages</c>: a named unique (<c>Unique</c> → <c>Words._row</c>)
    /// and its custom parchment text (<c>Message</c> → 0-based <c>ClientStrings2._row</c>, 270..280).</summary>
    public sealed record UniqueMessage(int Row, int WordRow, string Name, int Message);

    /// <summary>A rolled bottle reward: the chosen table row, the built item, the resolved 0-based <c>ClientStrings2</c>
    /// row (unique override when present, else <see cref="BottleOutcome.Description"/>), and a log label.</summary>
    public sealed record BottleReward(BottleOutcome Outcome, Item Item, int MessageRow, string Label);

    // G: Fallback outcome weights scaled to ~10,000 when world.db system_settings has no deepwater_bottle_weight_<Id>
    // row (estimated from wiki.md §4.6 community sample n≈2000; exact GGG weights are unsourced). In HardMode/Ruthless
    // DeepwaterLootBottles omits BottledDivineOrbStack and BottledStackedDeckStack via __col4 = 1.
    static readonly Dictionary<string, double> DefaultWeights = new(StringComparer.Ordinal)
    {
        ["BottledUniqueBelt"] = 1500,          // G: ~15%
        ["BottledUniqueJewel"] = 1500,         // G: ~15%
        ["BottledUniqueRing"] = 2000,          // G: ~20%
        ["BottledUniqueAmulet"] = 1500,        // G: ~15%
        ["BottledUniqueFlask"] = 1200,         // G: ~12%
        ["BottledHinekorasLock"] = 5,          // G: ~0.05%
        ["BottledFracturingOrb"] = 100,        // G: ~1%
        ["BottledDivineOrb"] = 500,            // G: ~5%
        ["BottledDivineOrbStack"] = 50,        // G: ~0.5% (Normal only)
        ["BottledReflectingMist"] = 100,       // G: ~1%
        ["BottledStackedDeckStack"] = 1400,    // G: ~14% (Normal only)
        ["BottledBrokenMirror"] = 4,           // G: ~0.04%
        ["BottledAlbinoCrabClaw"] = 4,         // G: ~0.04%
        ["BottledChainHookOfAngling"] = 5,     // G: ~0.05%
    };

    static BottleOutcome[] OutcomesCache = [];
    static BottleOutcome[] NormalOutcomesCache = [];
    static BottleOutcome[] HardModeOutcomesCache = [];
    static UniqueMessage[] UniqueMessagesCache = [];
    static Dictionary<string, UniqueMessage> UniqueMessagesByKeyCache = new(StringComparer.OrdinalIgnoreCase);

    public static void LoadGameData()
    {
        OutcomesCache = LoadOutcomes();
        NormalOutcomesCache = [.. OutcomesCache.Where(o => !o.IsHardMode)];
        HardModeOutcomesCache = [.. OutcomesCache.Where(o => o.IsHardMode)];
        UniqueMessagesCache = LoadUniqueMessages();
        var map = new Dictionary<string, UniqueMessage>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in UniqueMessagesCache)
            map.TryAdd(UniqueCatalog.NameKey(entry.Name), entry);
        UniqueMessagesByKeyCache = map;
    }

    /// <summary>All rows of <c>DeepwaterLootBottles</c> loaded at startup from <c>client.db</c> (26 rows: 14 Normal + 12 HardMode).</summary>
    public static IReadOnlyList<BottleOutcome> Outcomes => OutcomesCache;

    /// <summary>The <c>DeepwaterLootBottles</c> rows for Standard/Normal (<c>false</c>, 14 rows) or Ruthless/HardMode (<c>true</c>, 12 rows).</summary>
    public static IReadOnlyList<BottleOutcome> OutcomesFor(bool hardMode) =>
        hardMode ? HardModeOutcomesCache : NormalOutcomesCache;

    /// <summary>All 11 named-unique overrides from <c>DeepwaterLootBottleUniqueMessages</c>, loaded at startup from <c>client.db</c>.</summary>
    public static IReadOnlyList<UniqueMessage> UniqueMessages => UniqueMessagesCache;

    /// <summary>The custom 0-based <c>ClientStrings2</c> row (<c>270..280</c>) for a named unique in
    /// <c>DeepwaterLootBottleUniqueMessages</c>, or <c>null</c> for any other unique.</summary>
    public static int? UniqueMessageRow(string uniqueName) =>
        UniqueMessagesByKeyCache.TryGetValue(UniqueCatalog.NameKey(uniqueName), out var msg) ? msg.Message : null;

    /// <summary>Effective weight for an outcome row: reads <c>world.db</c> <c>system_settings</c> key
    /// <c>deepwater_bottle_weight_&lt;Id&gt;</c> (then <c>deepwater_bottle_weight_&lt;BaseId&gt;</c> for HardMode rows),
    /// falling back to the built-in G estimate when absent.</summary>
    public static double WeightOf(BottleOutcome outcome)
    {
        var settings = SystemSettings.Default.Values;
        if (settings.TryGetValue(WeightKeyPrefix + outcome.Id, out double exact) && double.IsFinite(exact))
            return Math.Max(0, exact);
        if (!outcome.BaseId.Equals(outcome.Id, StringComparison.Ordinal)
            && settings.TryGetValue(WeightKeyPrefix + outcome.BaseId, out double shared) && double.IsFinite(shared))
            return Math.Max(0, shared);
        return DefaultWeights.GetValueOrDefault(outcome.BaseId, 0);
    }

    /// <summary>Rolls one outcome row for the given league mode using <see cref="WeightOf"/>; returns <c>null</c> when
    /// the table is empty or all weights are zero.</summary>
    public static BottleOutcome? RollOutcome(bool hardMode, Random random)
    {
        var pool = OutcomesFor(hardMode);
        if (pool.Count == 0) return null;
        double total = 0;
        var weights = new double[pool.Count];
        for (int i = 0; i < pool.Count; i++)
        {
            double w = WeightOf(pool[i]);
            weights[i] = w;
            total += w;
        }
        if (total <= 0) return null;
        double roll = random.NextDouble() * total;
        int lastPositive = -1;
        for (int i = 0; i < pool.Count; i++)
        {
            if (weights[i] <= 0) continue;
            lastPositive = i;
            roll -= weights[i];
            if (roll < 0) return pool[i];
        }
        return lastPositive >= 0 ? pool[lastPositive] : null;
    }

    /// <summary>Builds the reward item and resolves its 0-based <c>ClientStrings2</c> message row for a rolled
    /// <see cref="BottleOutcome"/>. Pure and side-effect-free.</summary>
    public static BottleReward? BuildReward(BottleOutcome outcome, ItemCatalog catalog, UniqueCatalog uniques,
        Func<string, double> uniqueWeight, byte bottleLevel, Random random) => outcome.BaseId switch
    {
        "BottledUniqueBelt" => RollUnique(outcome, c => c == "Belt", catalog, uniques, uniqueWeight, bottleLevel, random),
        "BottledUniqueJewel" => RollUnique(outcome, c => c is "Jewel" or "AbyssJewel", catalog, uniques, uniqueWeight, bottleLevel, random),
        "BottledUniqueRing" => RollUnique(outcome, c => c == "Ring", catalog, uniques, uniqueWeight, bottleLevel, random),
        "BottledUniqueAmulet" => RollUnique(outcome, c => c == "Amulet", catalog, uniques, uniqueWeight, bottleLevel, random),
        "BottledUniqueFlask" => RollUnique(outcome, IsFlaskClass, catalog, uniques, uniqueWeight, bottleLevel, random),
        "BottledHinekorasLock" => CreateFixed(outcome, CreateStack(catalog, HinekorasLockId, 1, bottleLevel)),
        "BottledFracturingOrb" => CreateFixed(outcome, CreateStack(catalog, FracturingOrbId, 1, bottleLevel)),
        "BottledDivineOrb" => CreateFixed(outcome, CreateStack(catalog, DivineOrbId, 1, bottleLevel)),
        "BottledDivineOrbStack" => CreateFixed(outcome, CreateStack(catalog, DivineOrbId, random.Next(3, 9), bottleLevel)),
        "BottledReflectingMist" => CreateFixed(outcome, CreateStack(catalog, ReflectingMistId, 1, bottleLevel)),
        "BottledStackedDeckStack" => CreateFixed(outcome, CreateStack(catalog, StackedDeckId, 20, bottleLevel)),
        "BottledBrokenMirror" => CreateFixed(outcome, CreateStack(catalog, BrokenMirrorId, 1, bottleLevel)),
        "BottledAlbinoCrabClaw" => CreateFixed(outcome, CreateStack(catalog, AlbinoCrabClawId, 1, bottleLevel)),
        "BottledChainHookOfAngling" => CreateFixed(outcome, CreateChainHookOfAngling(catalog)),
        _ => null,
    };

    /// <summary><see cref="GameEvents.ItemUse"/> handler (<c>C→S 0x0058</c>) for <c>DeepwaterBottledItem</c>.
    /// Returns <c>false</c> for all other items so they continue to other handlers.</summary>
    public static async Task<bool> Use(IModuleContext module, Player player, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (!type.Id.Equals(BottleItemId, StringComparison.Ordinal)) return false;

        var modPlayer = PlayerOf(player);
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea)
        {
            module.Log($"{modPlayer.Character} <= 0x0058 open Message in a Bottle ({item.Id}): refused (player unavailable)");
            return true;
        }
        if (row is not (PlayerInventories.MainInventory or PlayerInventories.ExpandedInventory))
        {
            module.Log($"{modPlayer.Character} <= 0x0058 open Message in a Bottle ({item.Id}): refused (row {row} is not player inventory)");
            return true;
        }
        if (item.Item.HeaderFlags != 0 || DeepwaterDredge.Unmarked(player, item.Item))
        {
            module.Log($"{modPlayer.Character} <= 0x0058 open Message in a Bottle ({item.Id}): refused (Dredge item cannot be opened during a run)");
            return true;
        }

        var items = ItemsOf(player);
        if (items.Items(PlayerInventories.Cursor).Count > 0)
        {
            module.Log($"{modPlayer.Character} <= 0x0058 open Message in a Bottle ({item.Id}): refused (Cursor1 is not empty)");
            return true;
        }

        bool hardMode = LeagueFor(player).HardMode;
        var random = AreaRng(player);
        var outcome = RollOutcome(hardMode, random);
        var reward = outcome is null ? null
            : BuildReward(outcome, ItemsFor(player), UniquesFor(player), DropsFor(player).UniqueWeight, item.Item.ItemLevel, random);
        if (reward is null)
        {
            module.Log($"{modPlayer.Character} <= 0x0058 open Message in a Bottle ({item.Id}): refused (could not roll reward for {(outcome?.Id ?? "empty table")})");
            return true;
        }

        PlayerInventories.Consumption? consumed = null;
        InventoryItem? held = null;
        Saves.Atomically(() =>
        {
            if (items.Items(PlayerInventories.Cursor).Count > 0) return;
            if (items.ConsumeOne(row, item.Id) is not { } taken) return;
            consumed = taken;
            held = items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, reward.Item));
        });
        if (consumed is null || held is null)
        {
            module.Log($"{modPlayer.Character} <= 0x0058 open Message in a Bottle ({item.Id}): refused (bottle missing or Cursor1 occupied)");
            return true;
        }

        uint sourceInvId = PlayerInventories.InventoryId(row);
        uint cursorInvId = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        await modPlayer.Send(
            InstanceItems.InventoryChange(sourceInvId, items.NextRevision(row), [item.Id],
                consumed.Left is { } left ? [left] : []),
            $"0x00bf inventory {sourceInvId}: Message in a Bottle {item.Id} consumed");
        await modPlayer.Send(
            InstanceItems.InventoryChange(cursorInvId, items.NextRevision(PlayerInventories.Cursor), [], [held]),
            $"0x00bf cursor ({cursorInvId}): add bottle reward item {held.Id} ({reward.Label} x{reward.Item.StackSize})");
        await modPlayer.Send(
            InstanceWaypoints.OpenUi(RevealUi, 0, 0, (uint)reward.MessageRow, 0),
            $"0x007b open BottledItemRevealWindow (kind 0x{RevealUi:x2}, ClientStrings2#{reward.MessageRow})");
        module.Log($"{modPlayer.Character} <= 0x0058 opened Message in a Bottle ({item.Id}) -> {reward.Outcome.Id}: {reward.Label} x{reward.Item.StackSize} (ClientStrings2#{reward.MessageRow})");
        return true;
    }

    static bool IsFlaskClass(string itemClass) =>
        itemClass is "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical";

    static BottleReward? CreateFixed(BottleOutcome outcome, (Item Item, string Label)? created) =>
        created is { } built ? new BottleReward(outcome, built.Item, outcome.Description, built.Label) : null;

    static (Item Item, string Label)? CreateStack(ItemCatalog catalog, string baseTypeId, int count, byte bottleLevel)
    {
        if (catalog.Find(baseTypeId) is not { Supported: true } type) return null;
        int maxStack = Math.Max(1, (int)type.MaxStack);
        ushort stack = (ushort)Math.Clamp(count, 1, maxStack);
        byte level = (byte)Math.Clamp((int)bottleLevel, 1, 100);
        var item = ItemCatalog.Create(type) with { StackSize = stack, ItemLevel = level };
        return (item, type.Name);
    }

    static (Item Item, string Label)? CreateChainHookOfAngling(ItemCatalog catalog)
    {
        if (catalog.Find(ChainHookGemId) is not { Supported: true } type) return null;
        int variant = ChainHookOfAnglingVariant;
        string label = "Chain Hook of Angling";
        if (catalog.Gem(type) is { } gem)
        {
            if (gem.Variants.FirstOrDefault(v => v.Name.Equals(label, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                variant = named.Index;
                label = named.Name;
            }
            else if (variant < gem.Variants.Count)
            {
                label = gem.Variants[variant].Name;
            }
        }
        var item = ItemCatalog.Create(type) with { GemVariant = variant };
        return (item, label);
    }

    static BottleReward? RollUnique(BottleOutcome outcome, Func<string, bool> matchesClass, ItemCatalog catalog,
        UniqueCatalog uniques, Func<string, double> uniqueWeight, byte bottleLevel, Random random)
    {
        var candidates = new List<(UniqueCatalog.Definition Def, double Weight)>();
        var fallback = new List<UniqueCatalog.Definition>();
        foreach (var def in uniques.All)
        {
            if (def.EncounterOnly) continue;
            if (catalog.Find(def.BaseTypeId) is not { Supported: true } baseType || !matchesClass(baseType.Class)) continue;
            fallback.Add(def);
            double weight = uniqueWeight(def.Name);
            if (double.IsFinite(weight) && weight > 0) candidates.Add((def, weight));
        }
        if (candidates.Count == 0)
            foreach (var def in fallback) candidates.Add((def, 1.0));

        while (candidates.Count > 0)
        {
            double total = 0;
            foreach (var (_, w) in candidates) total += w;
            int chosenIndex = candidates.Count - 1;
            if (total > 0)
            {
                double roll = random.NextDouble() * total;
                for (int i = 0; i < candidates.Count; i++)
                {
                    roll -= candidates[i].Weight;
                    if (roll < 0) { chosenIndex = i; break; }
                }
            }
            var chosen = candidates[chosenIndex].Def;
            candidates.RemoveAt(chosenIndex);
            byte level = (byte)Math.Clamp(Math.Max((int)bottleLevel, (int)chosen.ItemLevel), 1, 100);
            if (uniques.Create(chosen with { ItemLevel = level }, random) is not { } created) continue;
            int messageRow = UniqueMessageRow(chosen.Name) ?? outcome.Description;
            return new BottleReward(outcome, created, messageRow, chosen.Name);
        }
        return null;
    }

    static BottleOutcome[] LoadOutcomes()
    {
        var rows = new List<BottleOutcome>();
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterLootBottles"))
        {
            if (r.TextOrNull("Id") is not { Length: > 0 } id) continue;
            if (r.IntOrNull("Description") is not int description) continue;
            bool hardMode = r.BoolOrNull("__col4") == true;
            rows.Add(new BottleOutcome(r.Index, id, StripHardModeSuffix(id), description, hardMode));
        }
        return [.. rows];
    }

    static UniqueMessage[] LoadUniqueMessages()
    {
        var words = ClientTables.TableOrEmpty("Words");
        if (words.Count == 0) return [];
        var rows = new List<UniqueMessage>();
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterLootBottleUniqueMessages"))
        {
            if (r.IntOrNull("Unique") is not int wordRow || wordRow < 0 || wordRow >= words.Count) continue;
            if (r.IntOrNull("Message") is not int message) continue;
            if (words[wordRow].TextOrNull("Text") is not { Length: > 0 } text) continue;
            rows.Add(new UniqueMessage(r.Index, wordRow, text, message));
        }
        return [.. rows];
    }

    static string StripHardModeSuffix(string id) =>
        id.EndsWith(HardModeSuffix, StringComparison.OrdinalIgnoreCase)
            ? id[..^HardModeSuffix.Length]
            : id;
}
