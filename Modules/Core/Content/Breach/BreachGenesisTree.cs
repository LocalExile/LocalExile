using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// The Genesis Tree (<c>PassiveSkillTrees#6 BrequelTree</c>, <c>ChayulaTreePassiveSkillGraph</c>):
/// 4 sub-trees (<c>BrequelPassiveSubTrees</c>: Equipment, Currency, Uniques, Misc), 4 Wombgift womb slots
/// (<c>BrequelTreeSlots</c>: <c>33919</c>, <c>41240</c>, <c>28988</c>, <c>7411</c>), tree point unlocks
/// (<c>BrequelFruitQuests</c> + <c>BodyPart1..10</c> + <c>BrequelTreePassiveUnlocks</c>), binary-verified wire
/// protocol (<c>S→C 0x02cf</c>, <c>C→S 0x02d0</c>/<c>0x02d1</c>/<c>0x02d3</c>), node allocation &amp; refund
/// validation against the PSG graph (including the 6 <c>Devoted</c>/<c>Forsaken</c> multiple-choice groups),
/// Wombgift factory (<see cref="CreateWombgift"/>), and full reward crafting evaluating all 88
/// <c>brequel_*_fruit_*</c> Genesis Tree passive stats and all 365 <c>MutatedUnique*</c> Foulborn modifiers.
/// </summary>
public static partial class BreachGenesisTree
{
    const string StateKey = "breach.genesis_tree";

    /// <summary>Binary-verified Genesis Tree opcodes (3.29.3c/d).</summary>
    public const ushort TreeStatePacketId = 0x02cf;
    public const ushort AllocateNodePacketId = 0x02d0;
    public const ushort RefundNodePacketId = 0x02d1;
    public const ushort FertilisePacketId = 0x02d3;
    public const int AllocateRefundPacketLength = 4;
    public const int FertilisePacketLength = 10;

    /// <summary>Sub-tree indices matching <c>BrequelPassiveSubTrees._row</c>.</summary>
    public const int SubTreeEquipment = 0, SubTreeCurrency = 1, SubTreeUniques = 2, SubTreeMisc = 3, SubTreeCount = 4;

    /// <summary>BaseItemTypes Ids for the 4 Wombgift types (<c>BrequelFruitTypes</c>).</summary>
    public const string EquipmentFruitId = "Metadata/Items/Chayula/EquipmentFruit";
    public const string CurrencyFruitId = "Metadata/Items/Chayula/CurrencyFruit";
    public const string UniqueItemFruitId = "Metadata/Items/Chayula/UniqueItemFruit";
    public const string MiscFruitId = "Metadata/Items/Chayula/MiscFruit";

    // Foulborn & Graft currency BaseItemTypes Ids.
    const string FoulbornAugId = "Metadata/Items/Currency/CurrencyMutatedAddModToMagic";
    const string FoulbornRegalId = "Metadata/Items/Currency/CurrencyMutatedUpgradeMagicToRare";
    const string FoulbornExaltId = "Metadata/Items/Currency/CurrencyMutatedAddModToRare";
    static readonly string[] FoulbornCurrencyIds = [FoulbornAugId, FoulbornRegalId, FoulbornExaltId];
    static readonly string[] GraftCurrencyIds =
    [
        "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueFire",
        "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueCold",
        "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueLightning",
        "Metadata/Items/Currency/CurrencyBreachUpgradeUniquePhysical",
        "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueChaos",
        "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueGeneral",
        "Metadata/Items/Currency/CurrencyGraftAddModToMagic",
        "Metadata/Items/Currency/CurrencyGraftUpgradeMagicToRare",
        "Metadata/Items/Currency/CurrencyGraftAddModToRare",
        "Metadata/Items/Currency/CurrencyGraftCorrupt",
    ];
    static readonly string[] BreachSpecialUniqueNames =
    [
        "Xoph's Heart", "Xoph's Blood", "Xoph's Inception", "Xoph's Nurture", "The Formless Flame", "The Formless Inferno",
        "The Halcyon", "The Pandemonius", "Tulborn", "Tulfall", "The Snowblind Grace", "The Perfect Form",
        "Voice of the Storm", "Choir of the Storm", "Hand of Thought and Motion", "Hand of Wisdom and Action", "Esh's Mirror", "Esh's Visage",
        "The Anticipation", "The Surrender", "Uul-Netol's Kiss", "Uul-Netol's Embrace", "Uul-Netol's Vow",
        "Eye of Chayula", "Presence of Chayula", "Severed in Sleep", "United in Dream", "Skin of the Loyal", "Skin of the Lords",
        "The Red Dream", "The Red Nightmare", "The Green Dream", "The Green Nightmare", "The Blue Dream", "The Blue Nightmare",
        "The Will of Xoph", "The Will of Tul", "The Will of Esh", "The Will of Uul-Netol",
    ];
    static readonly string[] ShardCurrencyIds =
    [
        "Metadata/Items/Currency/CurrencyUpgradeToRareShard",
        "Metadata/Items/Currency/CurrencyRerollRareShard",
        "Metadata/Items/Currency/CurrencyUpgradeMagicToRareShard",
        "Metadata/Items/Currency/CurrencyRemoveModShard",
        "Metadata/Items/Currency/CurrencyRerollUniqueShard",
        "Metadata/Items/Currency/CurrencyAddModToRareShard",
        "Metadata/Items/Currency/CurrencyFractureRareShard",
        "Metadata/Items/Currency/CurrencyDuplicateShard",
    ];

    /// <summary>One passive node on the Genesis Tree (<c>PassiveSkills</c> where <c>BrequelSubTree IS NOT NULL</c>).</summary>
    public sealed record TreeNode(
        int Row,
        ushort GraphId,
        string Id,
        string Name,
        int SubTree,
        bool IsStart,
        bool IsWomb,
        bool IsMultipleChoice,
        bool IsMultipleChoiceOption,
        bool IsNotable,
        bool IsKeystone,
        IReadOnlyList<(uint StatKey, int Value)> Stats);

    /// <summary>Metadata for one of the 4 <c>BrequelFruitTypes</c> rows.</summary>
    public readonly record struct FruitDefinition(
        int Row,
        int SubTree,
        string BaseItemId,
        uint TypeHash,
        int BaseHivebloodCost);

    /// <summary>A Wombgift currently planted in one of the 4 Genesis Tree wombs.</summary>
    public sealed class PlantedWombgift
    {
        public int SubTree { get; set; }
        public string BaseItemId { get; set; } = "";
        public byte ItemLevel { get; set; } = 68;
        public int HivebloodInvested { get; set; }
        public int HivebloodRequired { get; set; }
        [JsonIgnore] public bool Ready => HivebloodRequired > 0 && HivebloodInvested >= HivebloodRequired;
    }

    /// <summary>Persisted per-account/league Genesis Tree state.</summary>
    public sealed class SavedTreeState
    {
        public List<ushort> Allocated { get; set; } = [];
        public Dictionary<int, PlantedWombgift> Slots { get; set; } = [];
        public int TotalHarvested { get; set; }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Name of the extracted Genesis Tree graph in <c>client.db passive_graphs</c> (can be updated to match
    /// <c>@data-framework</c>'s exact key; checked against <c>"brequel"</c>, <c>"chayula"</c>, <c>"BrequelTree"</c>).</summary>
    public static string[] CandidateGraphNames { get; set; } = ["brequel", "BrequelTree", "chayula", "ChayulaTree"];

    /// <summary>Optional Item decorator attaching Component 170 (<c>BrequelFruit</c> <c>u32 LE fleshgraftLevel</c>) when
    /// creating a Wombgift, wired once @data-framework adds the component field to <see cref="Item"/>.</summary>
    public static Func<Item, uint, Item>? AttachFruitComponent { get; set; }

    /// <summary>Optional Item decorator reading <c>BrequelFruit</c> <c>fleshgraftLevel</c> from an existing Wombgift,
    /// falling back to <see cref="Item.ItemLevel"/>.</summary>
    public static Func<Item, int?>? ReadFruitLevel { get; set; }

    /// <summary>Optional Item decorator setting <c>Mods</c> component flags bit 9 (<c>0x0200</c>, <c>Mods + 0x2e6</c>)
    /// on Foulborn Uniques so the client displays the Breach icon and <c>"Foulborn {0}"</c> prefix.</summary>
    public static Func<Item, Item>? MarkFoulbornItem { get; set; }

    // Loaded static tables.
    static Dictionary<ushort, TreeNode> NodesByGraphId = [];
    static Dictionary<int, TreeNode> NodesByRow = [];
    static Dictionary<int, ushort> StartNodeBySubTree = [];
    static Dictionary<int, ushort> WombNodeBySubTree = [];
    static Dictionary<ushort, int> SubTreeByWombNode = [];
    static Dictionary<int, FruitDefinition> FruitBySubTree = [];
    static Dictionary<uint, FruitDefinition> FruitByHash = [];
    static Dictionary<ushort, HashSet<ushort>> Adjacency = [];
    static Dictionary<ushort, int> PassiveUnlockThresholds = [];
    static int[] SubTreeMaxPoints = [24, 16, 24, 12];

    // Cached Foulborn unique mutation index (built lazily from ItemCatalog + UniqueCatalog).
    static long _foulbornRevision = -1;
    static Dictionary<string, List<ModCatalog.Mod>> _mutatedModsByUniqueName = new(StringComparer.OrdinalIgnoreCase);
    static List<ModCatalog.Mod> _allMutatedMods = [];
    static readonly Lock FoulbornLock = new();

    [GeneratedRegex(@"^Mutated(Unique[A-Za-z]+\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MutatedUniquePrefixRegex();

    [GeneratedRegex(@"Unique[A-Za-z]+\d+", RegexOptions.CultureInvariant)]
    private static partial Regex UniqueModPrefixRegex();

    /// <summary>Loads all Genesis Tree tables (<c>BrequelPassiveSubTrees</c>, <c>BrequelTreeSlots</c>,
    /// <c>BrequelFruitTypes</c>, <c>BrequelTreePassiveUnlocks</c>, <c>PassiveSkills</c>, and
    /// <c>ChayulaTreePassiveSkillGraph</c>) from <c>client.db</c>.</summary>
    public static void LoadGameData()
    {
        var nodesByGid = new Dictionary<ushort, TreeNode>();
        var nodesByRow = new Dictionary<int, TreeNode>();
        var starts = new Dictionary<int, ushort>();
        var wombs = new Dictionary<int, ushort>();
        var wombToSub = new Dictionary<ushort, int>();

        foreach (var r in ClientTables.TableOrEmpty("PassiveSkills"))
        {
            if (r.IntOrNull("BrequelSubTree") is not int subTree) continue;
            int rawGid = r.IntOrNull("PassiveSkillGraphId") ?? 0;
            if (rawGid is <= 0 or > ushort.MaxValue) continue;
            ushort gid = (ushort)rawGid;

            var statRows = r.Ints("Stats");
            var stats = new List<(uint, int)>(statRows.Length);
            for (int i = 0; i < statRows.Length && i < 5; i++)
                stats.Add(((uint)statRows[i] + 1, r.IntOrNull($"Stat{i + 1}Value") ?? 0));

            bool isStart = r.BoolOrNull("IsBrequelStartingNode") == true;
            bool isWomb = r.BoolOrNull("IsBrequelWomb") == true;
            var node = new TreeNode(
                Row: r.Index,
                GraphId: gid,
                Id: r.TextOrNull("Id") ?? "",
                Name: r.TextOrNull("Name") ?? "",
                SubTree: subTree,
                IsStart: isStart,
                IsWomb: isWomb,
                IsMultipleChoice: r.BoolOrNull("IsMultipleChoice") == true,
                IsMultipleChoiceOption: r.BoolOrNull("IsMultipleChoiceOption") == true,
                IsNotable: r.BoolOrNull("IsNotable") == true,
                IsKeystone: r.BoolOrNull("IsKeystone") == true,
                Stats: stats);

            nodesByGid[gid] = node;
            nodesByRow[r.Index] = node;
            if (isStart) starts[subTree] = gid;
            if (isWomb) { wombs[subTree] = gid; wombToSub[gid] = subTree; }
        }

        // Cross-check BrequelTreeSlots for authoritative womb mapping.
        foreach (var r in ClientTables.TableOrEmpty("BrequelTreeSlots"))
        {
            if (r.IntOrNull("PassiveSkill") is int psRow && r.IntOrNull("BrequelSubTree") is int sub
                && nodesByRow.TryGetValue(psRow, out var n))
            {
                wombs[sub] = n.GraphId;
                wombToSub[n.GraphId] = sub;
            }
        }

        // BrequelPassiveSubTrees max points if present in schema.
        int[] maxPoints = [24, 16, 24, 12];
        foreach (var r in ClientTables.TableOrEmpty("BrequelPassiveSubTrees"))
        {
            if (r.Index is >= 0 and < SubTreeCount && r.IntOrNull("MaxPoints") is int mp and > 0)
                maxPoints[r.Index] = mp;
        }

        // BrequelFruitTypes -> base Hiveblood cost & BaseItemTypes mapping.
        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        var fruitBySub = new Dictionary<int, FruitDefinition>();
        var fruitByHash = new Dictionary<uint, FruitDefinition>();
        foreach (var r in ClientTables.TableOrEmpty("BrequelFruitTypes"))
        {
            if (r.IntOrNull("BaseItemType") is not int baseRow || baseRow < 0 || baseRow >= bases.Count) continue;
            if (bases[baseRow].TextOrNull("Id") is not { Length: > 0 } id) continue;
            int sub = r.IntOrNull("Reward") ?? r.Index;
            int baseCost = r.IntOrNull("__col2") ?? 100;
            uint hash = InstanceEntities.ObjectTypeHash(id);
            var def = new FruitDefinition(r.Index, sub, id, hash, baseCost);
            fruitBySub[sub] = def;
            fruitByHash[hash] = def;
        }

        // BrequelTreePassiveUnlocks (optional until @data-framework extracts it into client.db).
        var unlocks = new Dictionary<ushort, int>();
        foreach (var r in ClientTables.TableOrEmpty("BrequelTreePassiveUnlocks"))
        {
            if (r.IntOrNull("Passive") is int psRow && nodesByRow.TryGetValue(psRow, out var n))
                unlocks[n.GraphId] = r.IntOrNull("__col2") ?? 0;
        }

        // PSG graph adjacency (ChayulaTreePassiveSkillGraph).
        Dictionary<ushort, HashSet<ushort>> links = [];
        if (PassiveGraphs.Available)
        {
            foreach (string graphName in CandidateGraphNames)
            {
                if (PassiveGraphs.Graph(graphName) is not null)
                {
                    links = PassiveGraphs.Links(graphName);
                    break;
                }
            }
        }

        NodesByGraphId = nodesByGid;
        NodesByRow = nodesByRow;
        StartNodeBySubTree = starts;
        WombNodeBySubTree = wombs;
        SubTreeByWombNode = wombToSub;
        FruitBySubTree = fruitBySub;
        FruitByHash = fruitByHash;
        PassiveUnlockThresholds = unlocks;
        SubTreeMaxPoints = maxPoints;
        Adjacency = links;
    }

    /// <summary>Lookup a Genesis Tree node by its <c>PassiveSkillGraphId</c>.</summary>
    public static TreeNode? FindNode(ushort graphId) => NodesByGraphId.GetValueOrDefault(graphId);

    /// <summary>Lookup a Wombgift fruit definition by its <c>BaseItemTypes</c> hash.</summary>
    public static FruitDefinition? FindFruit(uint typeHash) =>
        FruitByHash.TryGetValue(typeHash, out var def) ? def : null;

    /// <summary>Lookup a Wombgift fruit definition by sub-tree index (0..3).</summary>
    public static FruitDefinition? FindFruitBySubTree(int subTree) =>
        FruitBySubTree.TryGetValue(subTree, out var def) ? def : null;

    // ---- Persisted state ----

    public static SavedTreeState LoadState(Player player)
    {
        string? raw = WorldOf(player).AccountData.Get(StateKey);
        SavedTreeState state;
        if (string.IsNullOrWhiteSpace(raw)) state = new();
        else
        {
            try { state = JsonSerializer.Deserialize<SavedTreeState>(raw, JsonOptions) ?? new(); }
            catch (JsonException) { state = new(); }
        }
        EnsureStartNodes(state);
        return state;
    }

    static bool MutateState(Player player, Func<SavedTreeState, bool> update, out SavedTreeState result)
    {
        SavedTreeState finalState = new();
        bool changed = WorldOf(player).AccountData.Change(StateKey, raw =>
        {
            SavedTreeState state;
            if (string.IsNullOrWhiteSpace(raw)) state = new();
            else
            {
                try { state = JsonSerializer.Deserialize<SavedTreeState>(raw, JsonOptions) ?? new(); }
                catch (JsonException) { state = new(); }
            }
            EnsureStartNodes(state);
            if (!update(state)) return null;
            state.Allocated.Sort();
            finalState = state;
            return JsonSerializer.Serialize(state, JsonOptions);
        });
        result = changed ? finalState : LoadState(player);
        return changed;
    }

    static bool IsImplicitRoot(ushort gid) =>
        StartNodeBySubTree.Values.Contains(gid) || WombNodeBySubTree.Values.Contains(gid);

    static void EnsureStartNodes(SavedTreeState state)
    {
        // Keep only non-root allocated nodes: both the 4 StartNodes (22476, 19625, 42956, 47967) and the 4 Wombs
        // (33919, 41240, 28988, 7411) are implicitly active on the 3.29.3c/d client.
        if (StartNodeBySubTree.Count == 0 && WombNodeBySubTree.Count == 0) return;
        state.Allocated.RemoveAll(IsImplicitRoot);
    }

    // ---- Wire packet builders & handlers (S→C 0x02cf, C→S 0x02d0 / 0x02d1 / 0x02d3) ----

    /// <summary>Builds the binary-verified <c>S→C 0x02cf</c> Genesis Tree state packet:
    /// <c>u16 BE 0x02cf</c>, <c>4 × CompactUInt allowedPoints[0..3]</c> (total earned points per sub-tree; client
    /// <c>0x141ef8440</c> computes <c>available = allowed + free - allocated</c>), <c>u8 pointsOnly</c>, and (when 0)
    /// <c>u16 BE nodeCount</c> + <c>nodeCount × u16 LE allocatedNodeIds</c> sorted ascending (excluding the 4 implicit
    /// starting root nodes).</summary>
    public static byte[] BuildTreeStatePacket(SavedTreeState state, int[] earnedBySubTree, bool pointsOnly = false)
    {
        var w = new LoginWriter().U16(TreeStatePacketId);
        for (int sub = 0; sub < SubTreeCount; sub++)
        {
            int allowed = sub < earnedBySubTree.Length ? Math.Max(0, earnedBySubTree[sub]) : 0;
            InstanceProtocol.CompactUInt(w, (uint)allowed);
        }
        w.U8(pointsOnly ? (byte)1 : (byte)0);
        if (!pointsOnly)
        {
            var sorted = state.Allocated.Where(id => !IsImplicitRoot(id)).Distinct().Order().ToArray();
            w.U16(checked((ushort)sorted.Length));
            Span<byte> le = stackalloc byte[2];
            foreach (ushort id in sorted)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(le, id);
                w.Bytes(le);
            }
        }
        return w.ToArray();
    }

    /// <summary>Enter-area message pair for <c>GameEvents.EnterMessages</c> (<c>S→C 0x02cf</c>).</summary>
    public static (string Log, byte[] Message) EnterMessage(Player player)
    {
        var state = LoadState(player);
        var earned = EarnedPointsBySubTree(player);
        return ($"0x02cf Genesis Tree ({state.Allocated.Count} nodes)", BuildTreeStatePacket(state, earned));
    }

    /// <summary>Handles <c>C→S 0x02d0</c> (4 bytes: <c>u16 BE 0x02d0, u16 BE nodeGraphId</c>) allocate request.</summary>
    public static async Task<bool> HandleAllocatePacketAsync(IModuleContext module, Player player, byte[] bytes)
    {
        if (bytes.Length < AllocateRefundPacketLength) return false;
        ushort nodeId = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        string? err = await AllocateNodeAsync(player, nodeId);
        if (err is not null)
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02d0 allocate Genesis Tree node {nodeId}: refused ({err})");
            await SyncStateAsync(player, $"refused allocate {nodeId}");
        }
        else
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02d0 allocated Genesis Tree node {nodeId} ({FindNode(nodeId)?.Name})");
        }
        return true;
    }

    /// <summary>Handles <c>C→S 0x02d1</c> (4 bytes: <c>u16 BE 0x02d1, u16 BE nodeGraphId</c>) refund request.</summary>
    public static async Task<bool> HandleRefundPacketAsync(IModuleContext module, Player player, byte[] bytes)
    {
        if (bytes.Length < AllocateRefundPacketLength) return false;
        ushort nodeId = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        string? err = await RefundNodeAsync(player, nodeId);
        if (err is not null)
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02d1 refund Genesis Tree node {nodeId}: refused ({err})");
            await SyncStateAsync(player, $"refused refund {nodeId}");
        }
        else
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02d1 refunded Genesis Tree node {nodeId} ({FindNode(nodeId)?.Name})");
        }
        return true;
    }

    /// <summary>Handles <c>C→S 0x02d3</c> (10 bytes: <c>u16 BE 0x02d3, u32 BE inventoryId, u32 BE itemId</c>):
    /// fertilises &amp; harvests a Wombgift directly from <c>MainInventory1</c>, <c>ExpandedInventory</c>, or
    /// <c>BrequelStorage1</c> (matching 3.29.3c client UI flow where womb selection is client-side).</summary>
    public static async Task<bool> HandleFertilisePacketAsync(IModuleContext module, Player player, byte[] bytes)
    {
        if (bytes.Length < FertilisePacketLength) return false;
        uint inventoryId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2, 4));
        uint itemId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6, 4));

        var (err, rewards) = await FertiliseFromInventoryAsync(player, inventoryId, itemId);
        if (err is not null)
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02d3 fertilise Wombgift inv={inventoryId} item={itemId}: refused ({err})");
            await BreachHiveblood.SyncAsync(player, "fertilise refused");
        }
        else
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02d3 fertilised Wombgift inv={inventoryId} item={itemId}: birthed {rewards.Count} reward(s)");
        }
        return true;
    }

    /// <summary>Validates, pays Hiveblood, consumes the Wombgift in <paramref name="inventoryId"/>, evaluates all
    /// Genesis Tree stats, and spawns the grown reward item(s) at the Genesis Tree.</summary>
    public static async Task<(string? Error, IReadOnlyList<Item> Rewards)> FertiliseFromInventoryAsync(
        Player player, uint inventoryId, uint itemId, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (PlayerInventories.Row(inventoryId) is not byte row)
            return ($"unknown inventory {inventoryId}", []);
        if (row is not (PlayerInventories.MainInventory or PlayerInventories.ExpandedInventory or BreachLocker.LockerRow))
            return ($"inventory row {row} cannot feed the Genesis Tree", []);

        var items = ItemsOf(player);
        var invItem = items.Items(row).FirstOrDefault(i => i.Id == itemId);
        if (invItem is null)
            return ($"Wombgift {itemId} not found in row {row}", []);
        if (FindFruit(invItem.Item.TypeHash) is not { } fruit)
            return ("item is not a Wombgift", []);

        var state = LoadState(player);
        if (!WombNodeBySubTree.ContainsKey(fruit.SubTree))
            return ($"no womb node configured for sub-tree {fruit.SubTree}", []);

        int level = ReadFruitLevel?.Invoke(invItem.Item) ?? invItem.Item.ItemLevel;
        int cost = RequiredHivebloodFor(fruit, level);
        if (BreachHiveblood.Balance(player) < cost)
            return ($"insufficient Hiveblood (need {cost}, have {BreachHiveblood.Balance(player)})", []);

        // Remove the Wombgift atomically before deducting Hiveblood.
        InventoryItem? removed = null;
        Saves.Atomically(() => removed = items.Remove(row, itemId));
        if (removed is null)
            return ("Wombgift moved before fertilising", []);

        if (!await BreachHiveblood.TrySpendAsync(player, cost, countTowardTreeQuests: true, reason: $"fertilise {fruit.BaseItemId}"))
        {
            // Roll back item removal if concurrent Hiveblood spend failed.
            Saves.Atomically(() => items.Put(row, removed));
            return ("insufficient Hiveblood", []);
        }

        await PlayerOf(player).Send(
            InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [itemId], []),
            $"0x00bf inventory {inventoryId}: consumed Wombgift {itemId} on Genesis Tree");

        var stats = CollectSubTreeStats(state, fruit.SubTree);
        var rewards = await RollRewardsAsync(player, fruit.SubTree, Math.Clamp(level, 1, 86), stats, rng);

        MutateState(player, s => { s.TotalHarvested++; return true; }, out _);

        await DeliverRewardsAsync(player, rewards, dropAtTree: PlayerOf(player).AreaId == BreachHub.HubAreaId);

        if (!player.Character.LeagueQuestFlags.Contains(BreachHub.LqfFirstFruitGrown))
            await SetLeagueQuestFlags(player, [BreachHub.LqfFirstFruitGrown]);

        return (null, rewards);
    }

    // ---- Point budget & unlock rules ----

    /// <summary>Computes earned Genesis Tree skill points per sub-tree (0..3) from quest progression:
    /// tutorial completion, <c>BrequelFruitQuests</c> Hiveblood milestones (1..7), and <c>BodyPart1..10</c> turn-ins.</summary>
    public static int[] EarnedPointsBySubTree(Player player)
    {
        int fruitQuests = BreachHub.CountTurnedInFruitQuests(player);
        int bodyParts = BreachHub.CountTurnedInBodyParts(player);
        bool tutorialDone = player.Character.LeagueQuestFlags.Contains(BreachHub.LqfTutorialCompleted);

        // Base starting points (enough to allocate the initial womb + early nodes) plus milestone progression.
        int baseEarned = (tutorialDone ? 4 : 2) + fruitQuests * 2 + bodyParts;
        int[] points = new int[SubTreeCount];
        for (int sub = 0; sub < SubTreeCount; sub++)
            points[sub] = Math.Clamp(baseEarned, 0, SubTreeMaxPoints[sub]);
        return points;
    }

    /// <summary>How many non-start points are currently spent in <paramref name="subTree"/>.</summary>
    public static int SpentPointsInSubTree(SavedTreeState state, int subTree)
    {
        int spent = 0;
        foreach (ushort gid in state.Allocated)
            if (NodesByGraphId.TryGetValue(gid, out var node) && node.SubTree == subTree && !node.IsStart && !node.IsWomb)
                spent++;
        return spent;
    }

    /// <summary>Called when Ailith completes a Hiveblood or Body Part quest milestone.</summary>
    public static async Task GrantMilestonePointsAsync(IModuleContext module, Player player, string reason)
    {
        await SyncStateAsync(player, reason);
        module.Log($"{PlayerOf(player).Character}: Genesis Tree milestone ({reason})");
    }

    /// <summary>Syncs the Genesis Tree allocation and sub-tree points to the client via <c>S→C 0x02cf</c>.</summary>
    public static Task SyncStateAsync(Player player, string reason = "sync")
    {
        var state = LoadState(player);
        var earned = EarnedPointsBySubTree(player);
        return PlayerOf(player).Send(
            BuildTreeStatePacket(state, earned),
            $"0x02cf Genesis Tree ({state.Allocated.Count} nodes, {reason})");
    }

    // ---- Node allocation & refund validation ----

    /// <summary>Validates and allocates <paramref name="graphId"/> on the Genesis Tree. Returns <see langword="null"/>
    /// on success or a human-readable refusal reason.</summary>
    public static async Task<string?> AllocateNodeAsync(Player player, ushort graphId)
    {
        if (!NodesByGraphId.TryGetValue(graphId, out var node))
            return $"unknown Genesis Tree node {graphId}";
        if (node.IsStart || node.IsWomb)
            return "starting and womb nodes are always active";

        int[] earned = EarnedPointsBySubTree(player);
        int totalProgress = BreachHub.CountTurnedInBodyParts(player) + BreachHub.CountTurnedInFruitQuests(player);
        if (PassiveUnlockThresholds.TryGetValue(graphId, out int requiredUnlock) && totalProgress < requiredUnlock)
            return $"node {node.Name} requires Genesis Tree progression {requiredUnlock}";

        string? refusal = null;
        bool changed = MutateState(player, state =>
        {
            if (state.Allocated.Contains(graphId))
            {
                refusal = "node already allocated";
                return false;
            }
            if (SpentPointsInSubTree(state, node.SubTree) >= earned[node.SubTree])
            {
                refusal = $"no available points in sub-tree {node.SubTree}";
                return false;
            }

            var allocatedSet = state.Allocated.ToHashSet();
            if (StartNodeBySubTree.TryGetValue(node.SubTree, out ushort startRoot))
                allocatedSet.Add(startRoot);
            if (WombNodeBySubTree.TryGetValue(node.SubTree, out ushort wombRoot))
                allocatedSet.Add(wombRoot);

            // Connectivity against ChayulaTreePassiveSkillGraph (graph "brequel") when extracted.
            if (Adjacency.Count > 0)
            {
                if (!Adjacency.TryGetValue(graphId, out var neighbors) || !neighbors.Any(allocatedSet.Contains))
                {
                    refusal = $"node {graphId} is not adjacent to an allocated node";
                    return false;
                }

                // MultipleChoiceOption rule: if this node is an option of a MultipleChoice parent, only one option
                // may be allocated per MultipleChoice parent at a time.
                if (node.IsMultipleChoiceOption)
                {
                    foreach (ushort parent in neighbors)
                    {
                        if (NodesByGraphId.TryGetValue(parent, out var pNode) && pNode.IsMultipleChoice
                            && Adjacency.TryGetValue(parent, out var siblings))
                        {
                            foreach (ushort sib in siblings)
                                if (sib != graphId && allocatedSet.Contains(sib)
                                    && NodesByGraphId.TryGetValue(sib, out var sNode) && sNode.IsMultipleChoiceOption)
                                {
                                    refusal = $"another option is already chosen on {pNode.Name}";
                                    return false;
                                }
                        }
                    }
                }
            }

            state.Allocated.Add(graphId);
            return true;
        }, out _);

        if (!changed) return refusal ?? "allocation failed";
        await SyncStateAsync(player, $"allocate node {graphId} ({node.Name})");
        return null;
    }

    /// <summary>Validates and refunds <paramref name="graphId"/> from the Genesis Tree, checking graph connectivity
    /// and refusing to refund a womb node while a Wombgift is planted in it. Returns <see langword="null"/> on success
    /// or a refusal reason.</summary>
    public static async Task<string?> RefundNodeAsync(Player player, ushort graphId)
    {
        if (!NodesByGraphId.TryGetValue(graphId, out var node))
            return $"unknown Genesis Tree node {graphId}";
        if (node.IsStart || node.IsWomb)
            return "cannot refund a starting or womb node";

        string? refusal = null;
        bool changed = MutateState(player, state =>
        {
            if (!state.Allocated.Contains(graphId))
            {
                refusal = "node is not allocated";
                return false;
            }
            if (node.IsWomb && state.Slots.ContainsKey(node.SubTree))
            {
                refusal = "cannot refund a womb while a Wombgift is planted in it";
                return false;
            }

            // Verify every remaining allocated node in this sub-tree stays reachable from its implicit start/womb roots.
            if (Adjacency.Count > 0)
            {
                var remaining = state.Allocated
                    .Where(id => id != graphId && NodesByGraphId.TryGetValue(id, out var n) && n.SubTree == node.SubTree)
                    .ToHashSet();
                var visited = new HashSet<ushort>();
                var queue = new Queue<ushort>();
                if (StartNodeBySubTree.TryGetValue(node.SubTree, out ushort start)) queue.Enqueue(start);
                if (WombNodeBySubTree.TryGetValue(node.SubTree, out ushort womb)) queue.Enqueue(womb);
                while (queue.Count > 0)
                {
                    ushort cur = queue.Dequeue();
                    if (!Adjacency.TryGetValue(cur, out var adj)) continue;
                    foreach (ushort next in adj)
                        if (remaining.Contains(next) && visited.Add(next))
                            queue.Enqueue(next);
                }
                if (visited.Count < remaining.Count)
                {
                    refusal = "refunding this node would disconnect allocated passives";
                    return false;
                }
            }

            state.Allocated.Remove(graphId);
            return true;
        }, out _);

        if (!changed) return refusal ?? "refund failed";
        await SyncStateAsync(player, $"refund node {graphId} ({node.Name})");
        return null;
    }

    /// <summary>Aggregates all passive stat totals for a single sub-tree (0..3) from the player's allocated Genesis
    /// Tree nodes.</summary>
    public static Dictionary<uint, int> CollectSubTreeStats(SavedTreeState state, int subTree)
    {
        var stats = new Dictionary<uint, int>();
        foreach (ushort gid in state.Allocated)
        {
            if (!NodesByGraphId.TryGetValue(gid, out var node) || node.SubTree != subTree) continue;
            foreach (var (key, value) in node.Stats)
                stats[key] = stats.GetValueOrDefault(key) + value;
        }
        return stats;
    }

    // ---- Wombgift drop factory ----

    /// <summary>Computes the exact 3.29.3c client Hiveblood cost (<c>0x1424ad630</c>
    /// <c>BrequelFruit::GetHivebloodCost</c>):
    /// <c>(uint)((ulong)BrequelItemResourceValues[level].GraftbloodStorage * BrequelFruitTypes[type].__col2 / 100)</c>.</summary>
    public static int RequiredHivebloodFor(FruitDefinition fruit, int areaLevel)
    {
        ulong storage = (ulong)Math.Max(1, BreachHiveblood.ResourceValueForLevel(areaLevel));
        ulong mult = (ulong)Math.Max(1, fruit.BaseHivebloodCost);
        return (int)(storage * mult / 100UL);
    }

    /// <summary>Creates a Wombgift item for <paramref name="fruitTypeId"/> (either full <c>BaseItemTypes</c> Id such as
    /// <c>"Metadata/Items/Chayula/EquipmentFruit"</c> or short name <c>"EquipmentFruit"</c>) at
    /// <paramref name="areaLevel"/>. Intended for <c>@breach-world</c> drops and Misc Parthenogenesis rewards.</summary>
    public static Item? CreateWombgift(Player player, string fruitTypeId, int areaLevel, Random rng)
    {
        string fullId = fruitTypeId.Contains('/', StringComparison.Ordinal)
            ? fruitTypeId
            : $"Metadata/Items/Chayula/{fruitTypeId}";

        var catalog = ItemsFor(player);
        if (catalog.ById(fullId) is not { } baseType) return null;
        if (!baseType.Supported) return null;

        byte ilvl = (byte)Math.Clamp(areaLevel, 1, 100);
        var item = ItemCatalog.Create(baseType, random: rng) with { ItemLevel = ilvl };
        if (AttachFruitComponent is { } attach)
            item = attach(item, ilvl);
        return item;
    }

    // ---- Planting, growing & harvesting ----

    /// <summary>Plants a Wombgift from the player's inventory (Main, Expanded, Cursor, or Wombgift Locker) into its
    /// matching allocated womb slot on the Genesis Tree.</summary>
    public static async Task<string?> PlantWombgiftAsync(Player player, byte sourceRow, uint itemId)
    {
        var items = ItemsOf(player);
        var invItem = items.Items(sourceRow).FirstOrDefault(i => i.Id == itemId);
        if (invItem is null) return "Wombgift not found in inventory";
        if (FindFruit(invItem.Item.TypeHash) is not { } fruit) return "item is not a Wombgift";

        var state = LoadState(player);
        if (!WombNodeBySubTree.ContainsKey(fruit.SubTree))
            return "no womb node configured for this Wombgift";
        if (state.Slots.ContainsKey(fruit.SubTree))
            return "womb slot already contains a Wombgift";

        int required = RequiredHivebloodFor(fruit, invItem.Item.ItemLevel);
        bool committed = false;
        Saves.Atomically(() =>
        {
            if (items.Remove(sourceRow, itemId) is null) return;
            committed = MutateState(player, s =>
            {
                if (s.Slots.ContainsKey(fruit.SubTree)) return false;
                s.Slots[fruit.SubTree] = new PlantedWombgift
                {
                    SubTree = fruit.SubTree,
                    BaseItemId = fruit.BaseItemId,
                    ItemLevel = invItem.Item.ItemLevel,
                    HivebloodInvested = 0,
                    HivebloodRequired = required,
                };
                return true;
            }, out _);
        });

        if (!committed) return "failed to plant Wombgift";
        await PlayerOf(player).Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(sourceRow), items.NextRevision(sourceRow), [itemId], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(sourceRow)}: planted Wombgift {itemId}");
        await SyncStateAsync(player, $"planted {fruit.BaseItemId}");
        return null;
    }

    /// <summary>Retrieves an unharvested Wombgift from <paramref name="subTree"/> back into the player's main
    /// inventory (right-click on planted Wombgift).</summary>
    public static async Task<string?> RetrieveWombgiftAsync(Player player, int subTree)
    {
        var state = LoadState(player);
        if (!state.Slots.TryGetValue(subTree, out var planted)) return "no Wombgift planted in this womb";

        var returned = CreateWombgift(player, planted.BaseItemId, planted.ItemLevel, Random.Shared);
        if (returned is null) return "could not recreate Wombgift item";

        var items = ItemsOf(player);
        var shape = ShapeOf(returned) ?? new(1, 1, BreachLocker.WombgiftItemClass);
        if (items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is not { } cell)
            return "no room in inventory";

        InventoryItem? placed = null;
        Saves.Atomically(() =>
        {
            if (!MutateState(player, s => s.Slots.Remove(subTree), out _)) return;
            placed = items.Add(PlayerInventories.MainInventory, cell.X, cell.Y, returned);
        });
        if (placed is null) return "failed to retrieve Wombgift";

        await PlayerOf(player).Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [placed]),
            $"0x00bf inventory 1: retrieved Wombgift {placed.Id}");
        await SyncStateAsync(player, $"retrieved Wombgift from sub-tree {subTree}");
        return null;
    }

    /// <summary>Feeds Hiveblood into the Wombgift planted in <paramref name="subTree"/>. When <paramref name="instantGrow"/>
    /// is true (Ctrl+click) or feeding reaches <see cref="PlantedWombgift.HivebloodRequired"/>, immediately harvests and
    /// delivers the birthed rewards.</summary>
    public static async Task<(string? Error, IReadOnlyList<Item> Rewards)> FeedOrGrowAsync(
        Player player, int subTree, int? maxSpend = null, bool instantGrow = false, Random? rng = null)
    {
        var state = LoadState(player);
        if (!state.Slots.TryGetValue(subTree, out var planted))
            return ("no Wombgift planted in this womb", []);

        int remaining = Math.Max(0, planted.HivebloodRequired - planted.HivebloodInvested);
        if (remaining > 0)
        {
            int available = BreachHiveblood.Balance(player);
            if (available <= 0)
                return ("no Hiveblood to offer", []);

            int toSpend = instantGrow
                ? remaining
                : Math.Min(remaining, Math.Min(available, maxSpend ?? remaining));
            if (instantGrow && available < remaining)
                return ("insufficient Hiveblood", []);
            if (toSpend <= 0)
                return ("insufficient Hiveblood", []);

            if (!await BreachHiveblood.TrySpendAsync(player, toSpend, countTowardTreeQuests: true, reason: $"grow sub-tree {subTree}"))
                return ("insufficient Hiveblood", []);

            MutateState(player, s =>
            {
                if (!s.Slots.TryGetValue(subTree, out var slot)) return false;
                slot.HivebloodInvested += toSpend;
                return true;
            }, out state);
        }

        if (state.Slots.TryGetValue(subTree, out var afterFeed) && afterFeed.Ready)
            return await HarvestAsync(player, subTree, rng);

        await SyncStateAsync(player, $"fed Wombgift in sub-tree {subTree}");
        return (null, []);
    }

    /// <summary>Harvests a fully grown Wombgift in <paramref name="subTree"/>, evaluating all allocated passive stats
    /// for that branch, delivering items to inventory (or dropping at the player's feet in the Monastery if full), and
    /// setting <c>ChayulaFirstFruitGrown</c> (LQF 1622) on the first harvest.</summary>
    public static async Task<(string? Error, IReadOnlyList<Item> Rewards)> HarvestAsync(Player player, int subTree, Random? rng = null)
    {
        rng ??= Random.Shared;
        var state = LoadState(player);
        if (!state.Slots.TryGetValue(subTree, out var planted))
            return ("no Wombgift planted in this womb", []);
        if (!planted.Ready)
            return ("Wombgift is not fully grown yet", []);

        var stats = CollectSubTreeStats(state, subTree);
        var rewards = await RollRewardsAsync(player, subTree, planted.ItemLevel, stats, rng);

        MutateState(player, s =>
        {
            if (!s.Slots.Remove(subTree)) return false;
            s.TotalHarvested++;
            return true;
        }, out _);

        await DeliverRewardsAsync(player, rewards);

        if (!player.Character.LeagueQuestFlags.Contains(BreachHub.LqfFirstFruitGrown))
            await SetLeagueQuestFlags(player, [BreachHub.LqfFirstFruitGrown]);

        await SyncStateAsync(player, $"harvested sub-tree {subTree} ({rewards.Count} items)");
        return (null, rewards);
    }

    static async Task DeliverRewardsAsync(Player player, IReadOnlyList<Item> rewards, bool dropAtTree = false)
    {
        if (rewards.Count == 0) return;
        var items = ItemsOf(player);
        var modPlayer = PlayerOf(player);
        var world = WorldOf(player);
        var origin = dropAtTree ? BreachHub.TreeDropOrigin(player) : player.Walk.Position(AreaTime(player));

        foreach (var item in rewards)
        {
            if (dropAtTree && player.Instance is { } treeInstance)
            {
                var spot = world.FindOpenSpot(origin, minRadius: 8, maxRadius: 28) ?? player.Walk.Position(AreaTime(player));
                var worldItem = new WorldItemObject(spot.X, spot.Y, item);
                uint groundId = treeInstance.Ground.Add(worldItem, player, shared: false);
                await SendGroundItem(player, groundId, worldItem, $"0x02bb Genesis Tree birthed reward 0x{groundId:x}", justDropped: true);
                continue;
            }

            var shape = ShapeOf(item) ?? new(1, 1, "");
            if (items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is { } cell)
            {
                var added = items.Add(PlayerInventories.MainInventory, cell.X, cell.Y, item);
                await modPlayer.Send(
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                        items.NextRevision(PlayerInventories.MainInventory), [], [added]),
                    $"0x00bf Genesis Tree reward item {added.Id} at ({added.X}, {added.Y})");
            }
            else if (player.Instance is { } instance)
            {
                var spot = world.FindOpenSpot(origin, minRadius: 2, maxRadius: 20) ?? origin;
                var worldItem = new WorldItemObject(spot.X, spot.Y, item);
                uint groundId = instance.Ground.Add(worldItem, player, shared: false);
                await SendGroundItem(player, groundId, worldItem, $"0x02bb Genesis Tree reward drop 0x{groundId:x}", justDropped: true);
            }
        }
    }

    // ---- Reward generation (evaluating all 88 brequel_*_fruit_* passive stats) ----

    /// <summary>Rolls all reward items for a harvested Wombgift using the player's allocated Genesis Tree stats.</summary>
    public static async Task<List<Item>> RollRewardsAsync(
        Player player, int subTree, int itemLevel, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        return subTree switch
        {
            SubTreeEquipment => RollEquipmentRewards(player, itemLevel, stats, rng),
            SubTreeCurrency => await RollCurrencyRewardsAsync(player, itemLevel, stats, rng),
            SubTreeUniques => RollUniqueRewards(player, itemLevel, stats, rng),
            SubTreeMisc => RollMiscRewards(player, itemLevel, stats, rng),
            _ => [],
        };
    }

    static int RollItemCount(int additionalPct, Random rng)
    {
        int count = 1 + Math.Max(0, additionalPct) / 100;
        int rem = Math.Max(0, additionalPct) % 100;
        if (rem > 0 && rng.Next(100) < rem) count++;
        return count;
    }

    // ---- 1. Equipment Wombgift (Provisioning) ----

    static List<Item> RollEquipmentRewards(Player player, int baseIlvl, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        int count = RollItemCount(StatVal(stats, Stat.BrequelEquipmentFruitAdditionalItemPct), rng);
        int addIlvlPct = StatVal(stats, Stat.BrequelEquipmentFruitAdditionalItemLevelPct);
        int modTierRating = StatVal(stats, Stat.BrequelEquipmentFruitModTierRatingPlus);
        int divineRolls = StatVal(stats, Stat.BrequelEquipmentFruitXDivineRolls);
        int luckySocketRolls = StatVal(stats, Stat.BrequelEquipmentFruitWeaponAndArmourXLuckySocketNumberAndLinksRolls);
        bool randomQuality = StatVal(stats, Stat.BrequelEquipmentFruitWeaponAndArmourRandomQuality) > 0;
        bool removeLowest = StatVal(stats, Stat.BrequelEquipmentFruitRemoveLowestLevelModifier) > 0;
        int fracturedPct = StatVal(stats, Stat.BrequelEquipmentFruitFracturedChancePct);
        bool jewelEnabled = StatVal(stats, Stat.BrequelEquipmentFruitJewelEnabled) > 0;

        var tagWeights = BuildEquipmentTagMultipliers(stats);
        var results = new List<Item>(count);

        for (int i = 0; i < count; i++)
        {
            int ilvl = baseIlvl;
            int remIlvl = addIlvlPct;
            while (remIlvl > 0)
            {
                if (remIlvl >= 100 || rng.Next(100) < remIlvl) ilvl++;
                remIlvl -= 100;
            }
            ilvl = Math.Clamp(ilvl, 1, 86);

            if (PickEquipmentBase(catalog, ilvl, stats, jewelEnabled, rng) is not { } baseType) continue;

            // Lucky socket & link rolls on weapons and armour.
            var (sockets, links) = RollLuckySockets(catalog, baseType, ilvl, luckySocketRolls, rng);
            byte quality = randomQuality && baseType.Components.Contains("Quality")
                ? (byte)rng.Next(1, 31)
                : (byte)0;

            var item = ItemCatalog.Create(baseType, sockets, links, random: rng) with
            {
                ItemLevel = (byte)ilvl,
                Quality = quality,
                Identified = true,
            };

            item = RollWeightedRareEquipment(item, baseType, catalog, rolls, modTierRating, tagWeights, rng);

            // Severing: remove lowest required-level explicit modifier.
            if (removeLowest)
                item = RemoveLowestLevelExplicit(item, catalog.Mods);

            // Divine rolls: keep best average roll percentile across 1 + divineRolls attempts.
            if (divineRolls > 0)
                item = ApplyLuckyDivineRolls(item, catalog.Mods, rolls, divineRolls, rng);

            // Solid Cysts: chance to fracture one explicit mod.
            if (fracturedPct > 0 && rng.Next(100) < fracturedPct && rolls.Fracture(item, rng) is { } fractured)
                item = fractured;

            results.Add(item);
        }

        return results;
    }

    static Dictionary<string, double> BuildEquipmentTagMultipliers(IReadOnlyDictionary<uint, int> stats)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(uint statKey, params string[] tags)
        {
            int pct = StatVal(stats, statKey);
            if (pct == 0) return;
            double mult = Math.Max(0.05, (100 + pct) / 100.0);
            foreach (string t in tags)
                map[t] = map.TryGetValue(t, out double cur) ? cur * mult : mult;
        }
        Add(Stat.BrequelEquipmentFruitPhysicalModifierChancePlusPct, "physical", "physical_damage");
        Add(Stat.BrequelEquipmentFruitFireModifierChancePlusPct, "fire", "fire_damage", "elemental");
        Add(Stat.BrequelEquipmentFruitColdModifierChancePlusPct, "cold", "cold_damage", "elemental");
        Add(Stat.BrequelEquipmentFruitLightningModifierChancePlusPct, "lightning", "lightning_damage", "elemental");
        Add(Stat.BrequelEquipmentFruitChaosModifierChancePlusPct, "chaos", "chaos_damage");
        Add(Stat.BrequelEquipmentFruitLifeModifierChancePlusPct, "life");
        Add(Stat.BrequelEquipmentFruitManaModifierChancePlusPct, "mana");
        Add(Stat.BrequelEquipmentFruitDefenceModifierChancePlusPct, "defences");
        Add(Stat.BrequelEquipmentFruitAttributeModifierChancePlusPct, "attribute");
        Add(Stat.BrequelEquipmentFruitResistanceModifierChancePlusPct, "resistance", "elemental_resistance", "chaos_resistance");
        Add(Stat.BrequelEquipmentFruitAttackModifierChancePlusPct, "attack");
        Add(Stat.BrequelEquipmentFruitCasterModifierChancePlusPct, "caster");
        Add(Stat.BrequelEquipmentFruitCriticalModifierChancePlusPct, "critical");
        Add(Stat.BrequelEquipmentFruitSpeedModifierChancePlusPct, "speed");
        return map;
    }

    static ItemCatalog.BaseType? PickEquipmentBase(
        ItemCatalog catalog, int ilvl, IReadOnlyDictionary<uint, int> stats, bool jewelEnabled, Random rng)
    {
        double armourPct = StatVal(stats, Stat.BrequelEquipmentFruitArmourChancePlusPct);
        double weaponPct = StatVal(stats, Stat.BrequelEquipmentFruitWeaponChancePlusPct);
        double jewelleryPct = StatVal(stats, Stat.BrequelEquipmentFruitJewelleryChancePlusPct);
        double dexFinal = StatVal(stats, Stat.BrequelEquipmentFruitDexterityRequirementChancePlusPctFinal);
        double strFinal = StatVal(stats, Stat.BrequelEquipmentFruitStrengthRequirementChancePlusPctFinal);
        double intFinal = StatVal(stats, Stat.BrequelEquipmentFruitIntelligenceRequirementChancePlusPctFinal);

        var candidates = new List<(ItemCatalog.BaseType Base, double Weight)>();
        double total = 0;

        foreach (var t in catalog.All)
        {
            if (!ItemDrops.Droppable(t) || !ItemDrops.EquipmentClasses.Contains(t.Class)) continue;
            if (t.DropLevel > ilvl) continue;
            if (t.Class == "Jewel" && !jewelEnabled) continue;

            double w = 1.0;
            // Bias toward bases near the wombgift's item level.
            if (t.DropLevel >= Math.Max(1, ilvl - 15)) w *= 3.0;

            // Broad category multiplier.
            if (IsArmourClass(t.Class)) w *= Math.Max(0.05, (100 + armourPct) / 100.0);
            else if (IsWeaponClass(t.Class)) w *= Math.Max(0.05, (100 + weaponPct) / 100.0);
            else if (IsJewelleryClass(t.Class)) w *= Math.Max(0.05, (100 + jewelleryPct) / 100.0);

            // Specific slot multiplier.
            double slotPct = t.Class switch
            {
                "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre" or "Dagger" or "Rune Dagger" or "Claw" =>
                    StatVal(stats, Stat.BrequelEquipmentFruit1hWeaponChancePlusPct),
                "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace" or "Staff" or "Warstaff" =>
                    StatVal(stats, Stat.BrequelEquipmentFruit2hWeaponChancePlusPct),
                "Bow" or "Wand" => StatVal(stats, Stat.BrequelEquipmentFruitRangedWeaponChancePlusPct),
                "Shield" => StatVal(stats, Stat.BrequelEquipmentFruitShieldChancePlusPct),
                "Quiver" => StatVal(stats, Stat.BrequelEquipmentFruitQuiverChancePlusPct),
                "Helmet" => StatVal(stats, Stat.BrequelEquipmentFruitHelmetChancePlusPct),
                "Body Armour" => StatVal(stats, Stat.BrequelEquipmentFruitBodyArmourChancePlusPct),
                "Boots" => StatVal(stats, Stat.BrequelEquipmentFruitBootsChancePlusPct),
                "Gloves" => StatVal(stats, Stat.BrequelEquipmentFruitGlovesChancePlusPct),
                "Belt" => StatVal(stats, Stat.BrequelEquipmentFruitBeltChancePlusPct),
                "Ring" => StatVal(stats, Stat.BrequelEquipmentFruitRingChancePlusPct),
                "Amulet" => StatVal(stats, Stat.BrequelEquipmentFruitAmuletChancePlusPct),
                "Jewel" => StatVal(stats, Stat.BrequelEquipmentFruitJewelChancePlusPct),
                _ => 0,
            };
            w *= Math.Max(0.05, (100 + slotPct) / 100.0);

            // Attribute requirement final multipliers.
            var req = catalog.Requirements(t);
            if (req.Dex > 0 && dexFinal != 0) w *= Math.Max(0.05, (100 + dexFinal) / 100.0);
            if (req.Str > 0 && strFinal != 0) w *= Math.Max(0.05, (100 + strFinal) / 100.0);
            if (req.Int > 0 && intFinal != 0) w *= Math.Max(0.05, (100 + intFinal) / 100.0);

            if (w <= 0) continue;
            candidates.Add((t, w));
            total += w;
        }

        if (candidates.Count == 0 || total <= 0) return null;
        double pick = rng.NextDouble() * total;
        foreach (var (b, w) in candidates)
        {
            if (pick < w) return b;
            pick -= w;
        }
        return candidates[^1].Base;
    }

    static Item RollWeightedRareEquipment(
        Item item, ItemCatalog.BaseType baseType, ItemCatalog catalog, ItemRolls rolls,
        int modTierRating, Dictionary<string, double> tagWeights, Random rng)
    {
        if (!baseType.Components.Contains("Mods")) return item;
        var pool = rolls.Pool(baseType, item);
        if (pool.Count == 0) return rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = true };

        bool isJewel = baseType.Class is "Jewel" or "AbyssJewel";
        var (maxPrefix, maxSuffix) = rolls.RareMaxAffixes(item);
        int targetCount = isJewel
            ? (rng.Next(20) < 13 ? 3 : 4)
            : rng.Next(12) switch { < 7 => 4, < 10 => 5, _ => 6 };
        targetCount = Math.Min(targetCount, maxPrefix + maxSuffix);

        // Compute highest level per mod family to apply modTierRating cleanly.
        var familyMaxLevel = new Dictionary<int, int>();
        foreach (var (m, _) in pool)
            foreach (int f in m.Families)
                if (!familyMaxLevel.TryGetValue(f, out int cur) || m.Level > cur)
                    familyMaxLevel[f] = m.Level;

        var chosen = new List<ModCatalog.Mod>(targetCount);
        bool Fits(ModCatalog.Mod mod) =>
            chosen.Count(c => c.GenerationType == mod.GenerationType) < (mod.GenerationType == ItemRolls.Prefix ? maxPrefix : maxSuffix)
            && !chosen.Any(c => c.Hash == mod.Hash || c.Families.Intersect(mod.Families).Any());

        // Tier rating shifts weight multiplicatively toward higher-level tiers within each mod family.
        double tierExponent = modTierRating > 0 ? 1.0 + modTierRating / 25.0 : 0.0;

        while (chosen.Count < targetCount)
        {
            var allowed = new List<(ModCatalog.Mod Mod, double Weight)>();
            double totalWeight = 0;
            foreach (var (m, baseWeight) in pool)
            {
                if (!Fits(m) || baseWeight <= 0) continue;
                double w = baseWeight;

                // Apply Devoted/Forsaken modifier tag weights.
                if (tagWeights.Count > 0)
                {
                    foreach (string tag in m.Tags)
                        if (tagWeights.TryGetValue(tag, out double mult))
                            w *= mult;
                }

                // Apply Modifier Tier Rating: higher-level tiers in a family become dramatically likelier.
                if (tierExponent > 0 && m.Families.Count > 0)
                {
                    int fMax = m.Families.Max(f => familyMaxLevel.GetValueOrDefault(f, m.Level));
                    double ratio = fMax > 0 ? Math.Clamp((double)Math.Max(1, m.Level) / fMax, 0.05, 1.0) : 1.0;
                    w *= Math.Pow(ratio, tierExponent);
                }

                if (w <= 0) continue;
                allowed.Add((m, w));
                totalWeight += w;
            }
            if (allowed.Count == 0 || totalWeight <= 0) break;

            double pick = rng.NextDouble() * totalWeight;
            ModCatalog.Mod selected = allowed[^1].Mod;
            foreach (var (m, w) in allowed)
            {
                if (pick < w) { selected = m; break; }
                pick -= w;
            }
            chosen.Add(selected);
        }

        // Fallback to standard Roll for rare name + final assembly if needed.
        var named = rolls.Roll(item, ItemRarity.Rare, rng);
        var keptImplicits = item.Mods.Where(m => m.Section != ModSection.Explicit);
        var explicits = chosen.OrderBy(m => m.GenerationType).Select(m => m.Roll(ModSection.Explicit, rng));
        return named with
        {
            Rarity = ItemRarity.Rare,
            Identified = true,
            Mods = [.. keptImplicits, .. explicits],
        };
    }

    static Item RemoveLowestLevelExplicit(Item item, ModCatalog mods)
    {
        int worstIndex = -1;
        int worstLevel = int.MaxValue;
        int explicitCount = 0;
        for (int i = 0; i < item.Mods.Count; i++)
        {
            if (item.Mods[i].Section != ModSection.Explicit) continue;
            explicitCount++;
            int lvl = mods.ByHash(item.Mods[i].Hash)?.Level ?? 0;
            if (lvl < worstLevel) { worstLevel = lvl; worstIndex = i; }
        }
        if (explicitCount <= 1 || worstIndex < 0) return item;
        var next = item.Mods.Where((_, idx) => idx != worstIndex).ToArray();
        return item with { Mods = next };
    }

    static Item ApplyLuckyDivineRolls(Item item, ModCatalog mods, ItemRolls rolls, int extraRolls, Random rng)
    {
        Item best = item;
        double bestScore = ScoreRollPercentile(item, mods);
        int tries = Math.Clamp(extraRolls, 1, 10);
        for (int i = 0; i < tries; i++)
        {
            if (rolls.Reforge(item, rng) is not { } candidate) break;
            double score = ScoreRollPercentile(candidate, mods);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    static double ScoreRollPercentile(Item item, ModCatalog mods)
    {
        double sum = 0;
        int count = 0;
        foreach (var im in item.Mods)
        {
            if (im.Section != ModSection.Explicit || mods.ByHash(im.Hash) is not { } mod) continue;
            for (int i = 0; i < im.Values.Count && i < mod.Stats.Count; i++)
            {
                var (min, max) = mod.RangeAt(i);
                sum += max > min ? (double)(im.Values[i] - min) / (max - min) : 1.0;
                count++;
            }
        }
        return count > 0 ? sum / count : 0;
    }

    // ---- 2. Currency Wombgift (Lavish) ----

    static async Task<List<Item>> RollCurrencyRewardsAsync(
        Player player, int ilvl, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var catalog = ItemsFor(player);
        var drops = DropsFor(player);

        int count = RollItemCount(StatVal(stats, Stat.BrequelCurrencyFruitAdditionalItemPct), rng);
        if (StatVal(stats, Stat.BrequelCurrencyFruitAdditional3ItemsPct) is int p3 and > 0 && rng.Next(100) < p3)
            count += 3;
        if (StatVal(stats, Stat.BrequelCurrencyFruitAdditional10ItemsPct) is int p10 and > 0 && rng.Next(100) < p10)
            count += 10;

        int luckyRolls = Math.Clamp(StatVal(stats, Stat.BrequelCurrencyFruitXLuckyRolls), 0, 5);
        bool canMutated = StatVal(stats, Stat.BrequelCurrencyFruitCanCreateMutatedCurrency) > 0;
        int mutatedPct = StatVal(stats, Stat.BrequelCurrencyFruitMutatedCurrencyChancePlusPct);
        bool canGraft = StatVal(stats, Stat.BrequelCurrencyFruitCanCreateGraftCurrency) > 0;
        int graftPct = StatVal(stats, Stat.BrequelCurrencyFruitGraftCurrencyChancePlusPct);
        int fullStackPct = StatVal(stats, Stat.BrequelCurrencyFruitFullStackChancePct);
        int goldPct = StatVal(stats, Stat.BrequelCurrencyFruitGoldChancePct);
        bool toShards = StatVal(stats, Stat.BrequelCurrencyFruitBasicCurrencyConvertedToShards) > 0;

        // Encrusted Fruit: chance to award a pile of Gold alongside currency.
        if (goldPct > 0 && rng.Next(100) < goldPct)
        {
            long gold = rng.Next(250, 750) * Math.Max(1, ilvl / 10);
            await WorldOf(player).AddGoldAsync(gold, "Genesis Tree Lavish Wombgift (Encrusted Fruit)");
        }

        // Build currency drop-weight map for lucky comparison.
        var weightById = ItemDrops.Currency.ToDictionary(c => c.Id, c => c.Weight, StringComparer.OrdinalIgnoreCase);
        var rawDrops = new List<ItemDrops.Drop>(count);

        for (int i = 0; i < count; i++)
        {
            // Chance to roll Foulborn currency (Flawed Seeds / Mutation Cultivation).
            if (canMutated && rng.NextDouble() < Math.Clamp(0.08 * (100 + mutatedPct) / 100.0, 0.01, 0.65))
            {
                string fid = FoulbornCurrencyIds[rng.Next(FoulbornCurrencyIds.Length)];
                if (catalog.ById(fid) is { Supported: true } ft)
                {
                    ushort stack = fullStackPct > 0 && rng.Next(100) < fullStackPct ? ft.MaxStack : (ushort)1;
                    rawDrops.Add(new(ItemCatalog.Create(ft) with { StackSize = stack, ItemLevel = (byte)Math.Clamp(ilvl, 1, 100) }, ft));
                    continue;
                }
            }

            // Chance to roll Breach / Graft currency (Grasping Flesh).
            if (canGraft && rng.NextDouble() < Math.Clamp(0.10 * (100 + graftPct) / 100.0, 0.01, 0.65))
            {
                var eligible = GraftCurrencyIds.Select(catalog.ById).OfType<ItemCatalog.BaseType>().Where(t => t.Supported).ToArray();
                if (eligible.Length > 0)
                {
                    var gt = eligible[rng.Next(eligible.Length)];
                    ushort stack = fullStackPct > 0 && rng.Next(100) < fullStackPct ? gt.MaxStack : (ushort)1;
                    rawDrops.Add(new(ItemCatalog.Create(gt) with { StackSize = stack, ItemLevel = (byte)Math.Clamp(ilvl, 1, 100) }, gt));
                    continue;
                }
            }

            // Standard currency drop with Fertile Genesis lucky rolls (keep rarest = lowest weight).
            ItemDrops.Drop? best = null;
            double bestWeight = double.MaxValue;
            for (int r = 0; r <= luckyRolls; r++)
            {
                var candidate = drops.CurrencyDrop(ilvl, rng);
                if (candidate is null) continue;
                double w = weightById.GetValueOrDefault(candidate.Type.Id, 50.0);
                if (best is null || w < bestWeight) { best = candidate; bestWeight = w; }
            }
            if (best is null) continue;

            // Reflexive Contractions: convert common basic currency (weight >= 90) into higher-tier shards.
            if (toShards && weightById.GetValueOrDefault(best.Type.Id, 0) >= 90)
            {
                var shards = ShardCurrencyIds.Select(catalog.ById).OfType<ItemCatalog.BaseType>().Where(t => t.Supported).ToArray();
                if (shards.Length > 0)
                {
                    var st = shards[rng.Next(shards.Length)];
                    ushort stack = (ushort)Math.Clamp(rng.Next(1, 5), 1, st.MaxStack);
                    best = new(ItemCatalog.Create(st) with { StackSize = stack, ItemLevel = (byte)Math.Clamp(ilvl, 1, 100) }, st);
                }
            }

            // Wild Growth: chance for a full stack.
            if (fullStackPct > 0 && best.Type.MaxStack > 1 && rng.Next(100) < fullStackPct)
                best = best with { Item = best.Item with { StackSize = best.Type.MaxStack } };

            rawDrops.Add(best);
        }

        return [.. ItemDrops.Stacked(rawDrops).Select(d => d.Item)];
    }

    // ---- 3. Unique Wombgift (Ancient) & Foulborn Unique Mutation ----

    static List<Item> RollUniqueRewards(Player player, int ilvl, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var catalog = ItemsFor(player);
        var uniques = UniquesFor(player);
        var drops = DropsFor(player);
        var rolls = RollsFor(player);

        int count = RollItemCount(StatVal(stats, Stat.BrequelUniqueFruitAdditionalItemPct), rng);
        int luckyRolls = Math.Clamp(StatVal(stats, Stat.BrequelUniqueFruitXLuckyRolls), 0, 5);
        int mutationPct = StatVal(stats, Stat.BrequelUniqueFruitMutationChancePlusPct);
        int multiMutationPct = StatVal(stats, Stat.BrequelUniqueFruitMultipleMutationChancePct);
        int specialPct = StatVal(stats, Stat.BrequelUniqueFruitSpecialUniqueChancePlusPct);
        bool corruptNonMutated = StatVal(stats, Stat.BrequelUniqueFruitNonMutatedItemsAreCorrupted) > 0;
        bool randomQuality = StatVal(stats, Stat.BrequelUniqueFruitWeaponAndArmourRandomQuality) > 0;
        int luckySocketRolls = StatVal(stats, Stat.BrequelUniqueFruitWeaponAndArmourXLuckySocketNumberAndLinksRolls);

        var specialSet = new HashSet<string>(BreachSpecialUniqueNames, StringComparer.OrdinalIgnoreCase);
        var weightedPool = BuildWeightedUniquePool(catalog, uniques, drops, ilvl, stats, specialSet, specialPct);
        if (weightedPool.Count == 0) return [];

        EnsureFoulbornModIndex(catalog, uniques);
        var results = new List<Item>(count);

        for (int i = 0; i < count; i++)
        {
            // Recessive Genes: lucky rolls pick the rarest unique (lowest stock weight).
            UniqueCatalog.Definition? chosenDef = null;
            double rarestStockWeight = double.MaxValue;
            for (int r = 0; r <= luckyRolls; r++)
            {
                var def = PickWeightedUnique(weightedPool, rng);
                if (def is null) continue;
                double sw = drops.UniqueWeight(def.Name);
                if (chosenDef is null || sw < rarestStockWeight) { chosenDef = def; rarestStockWeight = sw; }
            }
            if (chosenDef is null) continue;
            if (uniques.Create(chosenDef, rng) is not { } item) continue;

            var baseType = catalog.ByHash(item.TypeHash);
            item = item with { ItemLevel = (byte)Math.Clamp(ilvl, 1, 86), Identified = true };

            // Lucky sockets/links & random quality.
            if (baseType is not null)
            {
                if (luckySocketRolls > 0 && (IsWeaponClass(baseType.Class) || IsArmourClass(baseType.Class)))
                {
                    var (sockets, links) = RollLuckySockets(catalog, baseType, ilvl, luckySocketRolls, rng);
                    if (sockets.Count > 0) item = item with { Sockets = sockets, Links = links };
                }
                if (randomQuality && baseType.Components.Contains("Quality"))
                    item = item with { Quality = (byte)rng.Next(1, 31) };
            }

            // Foulborn Unique mutation (base 20% chance scaled by mutation_chance_+%; -100% disables mutations).
            double mutateChance = Math.Clamp(0.20 * (100 + mutationPct) / 100.0, 0.0, 0.95);
            bool mutated = false;
            if (mutateChance > 0 && rng.NextDouble() < mutateChance)
            {
                int mutationCount = (multiMutationPct > 0 && rng.Next(100) < multiMutationPct) ? 2 : 1;
                if (TryMutateUniqueFoulborn(item, chosenDef, catalog, mutationCount, rng) is { } foulborn)
                {
                    item = foulborn;
                    mutated = true;
                }
            }

            // Corrupted Flesh: non-mutated uniques are corrupted (with a chance for corrupted implicit).
            if (!mutated && corruptNonMutated && !item.Corrupted)
            {
                item = item with { Corrupted = true };
                if (baseType is not null)
                {
                    var corrPool = rolls.CorruptedImplicitPool(baseType, ilvl);
                    if (corrPool.Count > 0 && rng.Next(4) == 0)
                    {
                        int tw = corrPool.Sum(p => p.Weight);
                        int pick = rng.Next(Math.Max(1, tw));
                        ModCatalog.Mod pickedMod = corrPool[^1].Mod;
                        foreach (var (m, w) in corrPool) { if (pick < w) { pickedMod = m; break; } pick -= w; }
                        var nonImpl = item.Mods.Where(m => m.Section != ModSection.Implicit);
                        item = item with { Mods = [pickedMod.Roll(ModSection.Implicit, rng), .. nonImpl] };
                    }
                }
            }

            results.Add(item);
        }

        return results;
    }

    static List<(UniqueCatalog.Definition Def, double Weight)> BuildWeightedUniquePool(
        ItemCatalog catalog, UniqueCatalog uniques, ItemDrops drops, int ilvl,
        IReadOnlyDictionary<uint, int> stats, HashSet<string> specialNames, int specialPct)
    {
        double armourPct = StatVal(stats, Stat.BrequelUniqueFruitArmourChancePlusPct);
        double weaponPct = StatVal(stats, Stat.BrequelUniqueFruitWeaponChancePlusPct);
        double jewelleryPct = StatVal(stats, Stat.BrequelUniqueFruitJewelleryChancePlusPct);
        double dexPct = StatVal(stats, Stat.BrequelUniqueFruitDexterityRequirementChancePlusPct);
        double strPct = StatVal(stats, Stat.BrequelUniqueFruitStrengthRequirementChancePlusPct);
        double intPct = StatVal(stats, Stat.BrequelUniqueFruitIntelligenceRequirementChancePlusPct);

        var list = new List<(UniqueCatalog.Definition, double)>();
        foreach (var def in uniques.All)
        {
            bool isSpecial = specialNames.Contains(def.Name);
            if (!isSpecial && !drops.IsDropEligibleUnique(def, ilvl)) continue;
            if (catalog.ById(def.BaseTypeId) is not { Supported: true } baseType) continue;

            double w = Math.Max(0.01, drops.UniqueWeight(def.Name));
            if (isSpecial) w *= Math.Max(0.05, (100 + specialPct) / 100.0);

            if (IsArmourClass(baseType.Class)) w *= Math.Max(0.05, (100 + armourPct) / 100.0);
            else if (IsWeaponClass(baseType.Class)) w *= Math.Max(0.05, (100 + weaponPct) / 100.0);
            else if (IsJewelleryClass(baseType.Class)) w *= Math.Max(0.05, (100 + jewelleryPct) / 100.0);

            double slotPct = baseType.Class switch
            {
                "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre" or "Dagger" or "Rune Dagger" or "Claw" =>
                    StatVal(stats, Stat.BrequelUniqueFruit1hWeaponChancePlusPct),
                "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace" or "Staff" or "Warstaff" =>
                    StatVal(stats, Stat.BrequelUniqueFruit2hWeaponChancePlusPct),
                "Bow" or "Wand" => StatVal(stats, Stat.BrequelUniqueFruitRangedWeaponChancePlusPct),
                "Shield" => StatVal(stats, Stat.BrequelUniqueFruitShieldChancePlusPct),
                "Quiver" => StatVal(stats, Stat.BrequelUniqueFruitQuiverChancePlusPct),
                "Helmet" => StatVal(stats, Stat.BrequelUniqueFruitHelmetChancePlusPct),
                "Body Armour" => StatVal(stats, Stat.BrequelUniqueFruitBodyArmourChancePlusPct),
                "Boots" => StatVal(stats, Stat.BrequelUniqueFruitBootsChancePlusPct),
                "Gloves" => StatVal(stats, Stat.BrequelUniqueFruitGlovesChancePlusPct),
                "Belt" => StatVal(stats, Stat.BrequelUniqueFruitBeltChancePlusPct),
                "Ring" => StatVal(stats, Stat.BrequelUniqueFruitRingChancePlusPct),
                "Amulet" => StatVal(stats, Stat.BrequelUniqueFruitAmuletChancePlusPct),
                "Jewel" or "AbyssJewel" => StatVal(stats, Stat.BrequelUniqueFruitJewelChancePlusPct),
                "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical" =>
                    StatVal(stats, Stat.BrequelUniqueFruitFlaskChancePlusPct),
                _ => 0,
            };
            w *= Math.Max(0.05, (100 + slotPct) / 100.0);

            var req = catalog.Requirements(baseType);
            if (req.Dex > 0 && dexPct != 0) w *= Math.Max(0.05, (100 + dexPct) / 100.0);
            if (req.Str > 0 && strPct != 0) w *= Math.Max(0.05, (100 + strPct) / 100.0);
            if (req.Int > 0 && intPct != 0) w *= Math.Max(0.05, (100 + intPct) / 100.0);

            if (w > 0) list.Add((def, w));
        }
        return list;
    }

    static UniqueCatalog.Definition? PickWeightedUnique(List<(UniqueCatalog.Definition Def, double Weight)> pool, Random rng)
    {
        double total = 0;
        for (int i = 0; i < pool.Count; i++) total += pool[i].Weight;
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        for (int i = 0; i < pool.Count; i++)
        {
            if (pick < pool[i].Weight) return pool[i].Def;
            pick -= pool[i].Weight;
        }
        return pool[^1].Def;
    }

    static void EnsureFoulbornModIndex(ItemCatalog catalog, UniqueCatalog uniques)
    {
        long rev = catalog.Mods.Revision + uniques.Revision;
        if (Volatile.Read(ref _foulbornRevision) == rev) return;
        lock (FoulbornLock)
        {
            if (_foulbornRevision == rev) return;
            var allMutated = catalog.Mods.All
                .Where(m => m.Id.StartsWith("MutatedUnique", StringComparison.Ordinal))
                .ToList();

            var byPrefix = new Dictionary<string, List<ModCatalog.Mod>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in allMutated)
            {
                if (MutatedUniquePrefixRegex().Match(m.Id) is { Success: true } match)
                {
                    string prefix = match.Groups[1].Value;
                    (byPrefix.TryGetValue(prefix, out var list) ? list : byPrefix[prefix] = []).Add(m);
                }
            }

            var byUnique = new Dictionary<string, List<ModCatalog.Mod>>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in uniques.All)
            {
                var matched = new List<ModCatalog.Mod>();
                var seen = new HashSet<ushort>();
                foreach (string modId in def.Mods)
                {
                    foreach (Match m in UniqueModPrefixRegex().Matches(modId))
                    {
                        if (!byPrefix.TryGetValue(m.Value, out var muts)) continue;
                        foreach (var mut in muts)
                            if (seen.Add(mut.Hash)) matched.Add(mut);
                    }
                }
                if (matched.Count > 0) byUnique[def.Name] = matched;
            }

            _allMutatedMods = allMutated;
            _mutatedModsByUniqueName = byUnique;
            Volatile.Write(ref _foulbornRevision, rev);
        }
    }

    /// <summary>Mutates 1 or 2 explicit modifiers on <paramref name="item"/> into Foulborn unique modifiers drawn from
    /// the 365 <c>MutatedUnique*</c> mods in <c>client.db</c>, preferring exact unique-specific mutated mods and falling
    /// back to class-matched mutated mods when a unique has no dedicated prefix match.</summary>
    static Item? TryMutateUniqueFoulborn(Item item, UniqueCatalog.Definition def, ItemCatalog catalog, int count, Random rng)
    {
        if (MutatedUniqueCatalog.Mutate(item, def.Name, count, catalog.Mods, rng) is { } exactMutated)
            return MarkFoulbornItem is { } mark ? mark(exactMutated) : exactMutated;

        List<ModCatalog.Mod>? pool = null;
        if (_mutatedModsByUniqueName.TryGetValue(def.Name, out var exact) && exact.Count > 0)
            pool = exact;
        else if (catalog.ByHash(item.TypeHash) is { } bt && _allMutatedMods.Count > 0)
        {
            string? classToken = ClassTokenForMutatedMod(bt.Class);
            if (classToken is not null)
            {
                var classMatched = _allMutatedMods.Where(m => m.Id.Contains(classToken, StringComparison.OrdinalIgnoreCase)).ToList();
                if (classMatched.Count > 0) pool = classMatched;
            }
        }
        if (pool is not { Count: > 0 }) return null;

        var explicitIndices = item.Mods
            .Select((m, idx) => (m, idx))
            .Where(p => p.m.Section == ModSection.Explicit)
            .Select(p => p.idx)
            .ToList();
        if (explicitIndices.Count == 0) return null;

        var mods = item.Mods.ToList();
        var usedMutHashes = new HashSet<ushort>();
        int applied = 0;

        int replacements = Math.Min(count, Math.Min(explicitIndices.Count, pool.Count));
        for (int k = 0; k < replacements; k++)
        {
            var availableMuts = pool.Where(m => !usedMutHashes.Contains(m.Hash)).ToList();
            if (availableMuts.Count == 0) break;
            var mut = availableMuts[rng.Next(availableMuts.Count)];
            usedMutHashes.Add(mut.Hash);

            int slotPick = rng.Next(explicitIndices.Count);
            int targetIndex = explicitIndices[slotPick];
            explicitIndices.RemoveAt(slotPick);

            var rolled = mut.Roll(ModSection.Explicit, rng);
            mods[targetIndex] = rolled;
            applied++;
        }

        if (applied == 0) return null;
        var mutatedItem = item with { Mods = mods };
        // Mark item-level Mods bit 9 (0x0200, Mods + 0x2e6) for Breach icon & "Foulborn {0}" prefix; per-mod
        // DisplayKind 4 triggers automatically in the 3.29.3c client from Tags#1334 (mutatedunique) on MutatedUnique*.
        if (MarkFoulbornItem is { } markItem) mutatedItem = markItem(mutatedItem);
        return mutatedItem;
    }

    static string? ClassTokenForMutatedMod(string itemClass) => itemClass switch
    {
        "Bow" => "UniqueBow",
        "Wand" => "UniqueWand",
        "Claw" => "UniqueClaw",
        "Dagger" or "Rune Dagger" => "UniqueDagger",
        "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword" => "UniqueSword",
        "One Hand Axe" or "Two Hand Axe" => "UniqueAxe",
        "One Hand Mace" or "Two Hand Mace" or "Sceptre" => "UniqueMace",
        "Staff" or "Warstaff" => "UniqueStaff",
        "Shield" => "UniqueShield",
        "Quiver" => "UniqueQuiver",
        "Helmet" => "UniqueHelmet",
        "Body Armour" => "UniqueBody",
        "Boots" => "UniqueBoots",
        "Gloves" => "UniqueGloves",
        "Belt" => "UniqueBelt",
        "Ring" => "UniqueRing",
        "Amulet" => "UniqueAmulet",
        "Jewel" or "AbyssJewel" => "UniqueJewel",
        "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical" => "UniqueFlask",
        _ => null,
    };

    // ---- 4. Misc Wombgift (Mysterious) ----

    static List<Item> RollMiscRewards(Player player, int ilvl, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var catalog = ItemsFor(player);
        var drops = DropsFor(player);

        int count = RollItemCount(StatVal(stats, Stat.BrequelMiscFruitAdditionalItemPct), rng);
        double commonMult = Math.Max(0.1, (100 + StatVal(stats, Stat.BrequelMiscFruitCommonRewardsChancePlusPct)) / 100.0);
        double rarerMult = Math.Max(0.1, (100 + StatVal(stats, Stat.BrequelMiscFruitRarerRewardsChancePlusPct)) / 100.0);
        bool otherFruits = StatVal(stats, Stat.BrequelMiscFruitOtherFruitsEnabled) > 0;
        double eqFruitMult = Math.Max(0.1, (100 + StatVal(stats, Stat.BrequelMiscFruitEquipmentFruitChancePlusPct)) / 100.0);
        double curFruitMult = Math.Max(0.1, (100 + StatVal(stats, Stat.BrequelMiscFruitCurrencyFruitChancePlusPct)) / 100.0);
        double uniqFruitMult = Math.Max(0.1, (100 + StatVal(stats, Stat.BrequelMiscFruitUniqueFruitChancePlusPct)) / 100.0);

        var results = new List<Item>(count);

        for (int i = 0; i < count; i++)
        {
            double wScarab = 35 * commonMult;
            double wCard = 30 * commonMult;
            double wGem = 20 * commonMult;
            double wRareReward = 15 * rarerMult;
            double wFruit = otherFruits ? 12 * ((eqFruitMult + curFruitMult + uniqFruitMult) / 3.0) : 0;
            double total = wScarab + wCard + wGem + wRareReward + wFruit;
            double roll = rng.NextDouble() * total;

            if (roll < wFruit && otherFruits)
            {
                double ftot = eqFruitMult + curFruitMult + uniqFruitMult;
                double fp = rng.NextDouble() * ftot;
                string fid = fp < eqFruitMult ? EquipmentFruitId
                    : fp < eqFruitMult + curFruitMult ? CurrencyFruitId
                    : UniqueItemFruitId;
                if (CreateWombgift(player, fid, ilvl, rng) is { } fruit) { results.Add(fruit); continue; }
            }
            roll -= wFruit;

            if (roll < wScarab && drops.Scarab(rng, ilvl, null, null) is { } scarab)
            {
                results.Add(scarab.Item);
                continue;
            }
            roll -= wScarab;

            if (roll < wCard && drops.Card(rng, ilvl, null) is { } card)
            {
                results.Add(card.Item);
                continue;
            }
            roll -= wCard;

            if (roll < wGem && drops.GemDrop(ilvl, rng) is { } gem)
            {
                byte q = (byte)Math.Clamp(rng.Next(10, 21), 0, 20);
                results.Add(gem.Item with { Quality = q });
                continue;
            }

            // Rarer rewards: Hivebrain Gland, Breach Blessings / Flesh of Xesht, or high-tier currency fallback.
            string[] rareIds =
            [
                "Metadata/Items/MapFragments/CurrencyBrequelBossKey",
                "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueGeneral",
                "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueChaos",
                "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueFire",
                "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueCold",
                "Metadata/Items/Currency/CurrencyBreachUpgradeUniqueLightning",
                "Metadata/Items/Currency/CurrencyBreachUpgradeUniquePhysical",
            ];
            var rareBases = rareIds.Select(catalog.ById).OfType<ItemCatalog.BaseType>().Where(t => t.Supported).ToArray();
            if (rareBases.Length > 0)
            {
                var picked = rareBases[rng.Next(rareBases.Length)];
                results.Add(ItemCatalog.Create(picked) with { ItemLevel = (byte)Math.Clamp(ilvl, 1, 100) });
            }
            else if (drops.CurrencyDrop(ilvl, rng) is { } fallback)
            {
                results.Add(fallback.Item);
            }
        }

        return results;
    }

    // ---- Shared item rolling utilities ----

    static (IReadOnlyList<ItemSocket> Sockets, IReadOnlyList<byte>? Links) RollLuckySockets(
        ItemCatalog catalog, ItemCatalog.BaseType baseType, int ilvl, int extraRolls, Random rng)
    {
        var best = catalog.DropSockets(baseType, ilvl, rng);
        if (extraRolls <= 0 || best.Sockets.Count == 0) return best;

        static int SocketScore(IReadOnlyList<ItemSocket> s, IReadOnlyList<byte>? l) =>
            s.Count * 10 + (l is { Count: > 0 } ? l.Max() * 15 : 0);

        int bestScore = SocketScore(best.Sockets, best.Links);
        int tries = Math.Clamp(extraRolls, 1, 80);
        for (int i = 0; i < tries; i++)
        {
            var next = catalog.DropSockets(baseType, ilvl, rng);
            int score = SocketScore(next.Sockets, next.Links);
            if (score > bestScore) { bestScore = score; best = next; }
        }
        return best;
    }

    static int StatVal(IReadOnlyDictionary<uint, int> stats, uint key) => stats.GetValueOrDefault(key);

    static bool IsArmourClass(string c) => c is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield";
    static bool IsWeaponClass(string c) => c is "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace"
        or "Sceptre" or "Dagger" or "Rune Dagger" or "Claw" or "Wand" or "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace"
        or "Staff" or "Warstaff" or "Bow" or "Quiver";
    static bool IsJewelleryClass(string c) => c is "Ring" or "Amulet" or "Belt" or "Jewel" or "AbyssJewel";
}
