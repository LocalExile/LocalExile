using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Vesper's Allflame Crafting below deck on The Sovereign (05-inventories-and-ui.md, 03-area-and-world.md §8):
/// data-driven <c>DeepwaterCraftingCurrencies</c>/<c>DeepwaterCraftingClasses</c>/<c>DeepwaterBalancePerLevel</c>, the exact
/// client two-division Sulphur cost formula (<c>0x141d63ef0</c>/<c>0x141d64e00</c>), <c>C→S 0x010f</c> quick-move into/out of
/// inventory 105, <c>C→S 0x02ca</c> ruleset 172 op 2 (<c>DoCraft</c>), Intangibility copy-penalty roll &amp; accumulation,
/// and the 4 <c>GhostflameCraftingOutput</c> display-case vats backed by inventory 106 and <c>QF 3578</c>.</summary>
public static class DeepwaterCrafting
{
    /// <summary>Inventories rows: DeepwaterCraftingInput (2 × 4) and DeepwaterCraftingOutputStorage (8 × 4 = 4 × 2 × 4).</summary>
    public const byte InputRow = 105, OutputRow = 106;
    /// <summary>S→C 0x7b kind DeepwaterCraftingWindow (all 4 u32 args ignored; requires ruleset 172 before 0x21).</summary>
    public const byte CraftingUi = 0x69;
    /// <summary>NPCs row 795 <c>Metadata/NPC/League/Deepwater/VesperCrafting</c>.</summary>
    public const int VesperNpc = 795;
    /// <summary>QuestFlags 3578: disables targeting Vesper while unclaimed Ghostly Copies sit in the vats (H).</summary>
    public const string CraftInProgress = DeepwaterConstants.CraftInProgress;
    /// <summary>The 4 display-case vats below deck in <c>hub_interior.arm</c> (HeistRewardDisplay, required_open_attempts = 2).</summary>
    public const string VatType = "Metadata/Terrain/Leagues/Deepwater/Objects/GhostflameCraftingOutput";
    /// <summary>Sentinel value in <c>DeepwaterCraftingCurrencies.__col5</c> meaning no league quest flag is required (H).</summary>
    public const int NoLeagueFlag = 1911;

    const uint TreatUniqueAsRareStat = Stat.CanBeGhostflameCraftedAsThoughRare;

    // 6 Influenced Exalted Orbs (BaseItemTypes Id → ItemInfluence).
    static readonly Dictionary<string, ItemInfluence> InfluencedExalts = new(StringComparer.Ordinal)
    {
        ["Metadata/Items/AtlasExiles/AddModToRareCrusader"] = ItemInfluence.Crusader,
        ["Metadata/Items/AtlasExiles/AddModToRareRedeemer"] = ItemInfluence.Redeemer,
        ["Metadata/Items/AtlasExiles/AddModToRareHunter"] = ItemInfluence.Hunter,
        ["Metadata/Items/AtlasExiles/AddModToRareWarlord"] = ItemInfluence.Warlord,
        ["Metadata/Items/Currency/CurrencyAddModToRareShaper"] = ItemInfluence.Shaper,
        ["Metadata/Items/Currency/CurrencyAddModToRareElder"] = ItemInfluence.Elder,
    };

    // 3 Foulborn Orbs (BaseItemTypes Id).
    const string FoulbornAug = "Metadata/Items/Currency/CurrencyMutatedAddModToMagic";
    const string FoulbornRegal = "Metadata/Items/Currency/CurrencyMutatedUpgradeMagicToRare";
    const string FoulbornExalt = "Metadata/Items/Currency/CurrencyMutatedAddModToRare";

    /// <summary>One row of <c>DeepwaterCraftingCurrencies</c> joined via <c>CurrencyItems.BaseItemTypesKey</c>.</summary>
    public sealed record CurrencyRow(uint CurrencyHash, int Tier, int MinCopies, int MaxCopies, int Cost,
        int RequiredLeagueFlag, int IntangMin, int IntangMax, int HardModeCost, bool DisabledInRuthless);

    /// <summary>One row of <c>DeepwaterCraftingClasses</c>: allowed <c>ItemClasses</c> row, cost multiplier %, and whether
    /// item level scales cost via <c>DeepwaterBalancePerLevel</c> (false only for Jewel).</summary>
    public sealed record ClassRow(int ItemClass, int Multiplier, bool ScalesWithLevel);

    // ---- client.db tables ----

    static Dictionary<uint, CurrencyRow[]> Currencies = [];
    static Dictionary<int, ClassRow> Classes = [];
    static (Dictionary<int, int> ByLevel, int MinLevel, int MaxLevel) Balance = ([], 0, 0);

    public static void LoadGameData()
    {
        var map = new Dictionary<uint, List<CurrencyRow>>();
        var currencyItems = ClientTables.TableOrEmpty("CurrencyItems");
        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterCraftingCurrencies"))
        {
            if (r.IntOrNull("Currency") is not int curRow || curRow < 0 || curRow >= currencyItems.Count) continue;
            if (currencyItems[curRow].IntOrNull("BaseItemTypesKey") is not int baseRow || baseRow < 0 || baseRow >= bases.Count) continue;
            var b = bases[baseRow];
            string? id = b.Text("Id");
            string? name = b.Text("Name");
            if (string.IsNullOrEmpty(id) || id == DeepwaterDucats.UnusedDucat
                || (name is not null && name.StartsWith("[DNT]", StringComparison.Ordinal))) continue;
            uint hash = b.UInt("HASH32");
            var entry = new CurrencyRow(
                hash,
                r.IntOrNull("__col1") ?? 0,
                r.IntOrNull("__col2") ?? 1,
                r.IntOrNull("__col3") ?? 1,
                r.IntOrNull("Cost") ?? 0,
                r.IntOrNull("__col5") ?? NoLeagueFlag,
                r.IntOrNull("__col12") ?? 0,
                r.IntOrNull("__col13") ?? 0,
                r.IntOrNull("__col14") ?? 0,
                r.BoolOrNull("__col15") == true);
            if (!map.TryGetValue(hash, out var list)) map[hash] = list = [];
            list.Add(entry);
        }
        Currencies = map.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(e => e.Tier).ToArray());

        var classes = new Dictionary<int, ClassRow>();
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterCraftingClasses"))
        {
            if (r.IntOrNull("ItemClass") is not int cls) continue;
            classes[cls] = new ClassRow(cls, r.IntOrNull("__col1") ?? 100, r.BoolOrNull("__col2") ?? true);
        }
        Classes = classes;

        var byLevel = new Dictionary<int, int>();
        int min = int.MaxValue, max = int.MinValue;
        foreach (var r in ClientTables.TableOrEmpty("DeepwaterBalancePerLevel"))
        {
            if (r.IntOrNull("__col0") is not int lvl) continue;
            byLevel[lvl] = r.IntOrNull("__col1") ?? 0;
            if (lvl < min) min = lvl;
            if (lvl > max) max = lvl;
        }
        Balance = (byLevel, min, max);

        VatInfo = QuestObjectTypes.Find(VatType) is { } info && QuestObject.CanSerialize(info.Components, true) ? info : null;
        VatStates = ObjectTypes.All().Where(t => t.Type == VatType)
            .Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates()))
            .FirstOrDefault();
    }

    public static bool HasCurrency(uint currencyHash) => Currencies.ContainsKey(currencyHash);

    /// <summary>Resolves the highest unlocked tier row for <paramref name="currencyHash"/> (matching client
    /// <c>FUN_141d64b80</c>, H), or null when the currency is unknown or all tiers are locked.</summary>
    public static CurrencyRow? ResolveCurrency(uint currencyHash, Func<int, bool> hasLeagueFlag)
    {
        if (!Currencies.TryGetValue(currencyHash, out var rows)) return null;
        CurrencyRow? best = null;
        foreach (var row in rows)
            if ((row.RequiredLeagueFlag == NoLeagueFlag || hasLeagueFlag(row.RequiredLeagueFlag))
                && (best is null || row.Tier > best.Tier))
                best = row;
        return best;
    }

    public static CurrencyRow? ResolveCurrency(Player player, uint currencyHash) =>
        ResolveCurrency(currencyHash, f => HasLeagueFlag(player, f));

    public static ClassRow? ClassOf(int itemClassRow) => Classes.TryGetValue(itemClassRow, out var cls) ? cls : null;

    /// <summary><c>DeepwaterBalancePerLevel.__col1</c> at <paramref name="level"/> (−66 at 1, 0 at 67, +144 at 85..100),
    /// clamped to the table bounds.</summary>
    public static int BalanceAt(int level)
    {
        var (byLevel, min, max) = Balance;
        if (byLevel.Count == 0) return 0;
        return byLevel.GetValueOrDefault(Math.Clamp(level, min, max));
    }

    /// <summary>Exact Dead Man's Sulphur cost matching client <c>0x141d63ef0</c> / <c>0x141d64e00</c> (H): two separate
    /// truncated integer divisions by 100.</summary>
    public static int SulphurCost(CurrencyRow row, Item? target, ItemCatalog.BaseType? targetType, bool hardMode)
    {
        int baseCost = (hardMode && row.HardModeCost != 0) ? row.HardModeCost : row.Cost;
        int classMul = 100, levelMul = 100;
        if (target is not null && targetType is not null)
        {
            if (ClassOf(targetType.ClassRow) is { } cls)
            {
                classMul = cls.Multiplier;
                if (cls.ScalesWithLevel)
                    levelMul = BalanceAt(target.Components.Contains("Mods") ? target.ItemLevel : targetType.DropLevel) + 100;
            }
            else
            {
                levelMul = BalanceAt(target.Components.Contains("Mods") ? target.ItemLevel : targetType.DropLevel) + 100;
            }
        }
        return (((baseCost * classMul) / 100) * levelMul) / 100;
    }

    static bool HasLeagueFlag(Player player, int flag) =>
        player.Character.LeagueQuestFlags.Contains(flag);

    // ---- inventory 105 / 106 validation & C→S 0x010f quick-move ----

    /// <summary><c>GameEvents.InventoryRefusal</c> for inventories 105 and 106.</summary>
    public static string? Refusal(Player player, byte row, Item item)
    {
        if (row == OutputRow) return "the Allflame crafting outputs are taken from the display cases";
        if (row != InputRow) return null;
        if (ItemsOf(player).Items(OutputRow).Count > 0 || player.Character.HasQuestFlag(CraftInProgress))
            return "claim a Ghostly Copy from the display cases before crafting again";
        if (DeepwaterDredge.Unmarked(player, item))
            return "contraband items cannot be Allflame crafted";
        return InputItemRefusal(ItemsFor(player), item);
    }

    /// <summary>Validates whether <paramref name="item"/> can sit in inventory 105 (<c>DeepwaterCraftingInput</c>) as a craft target.</summary>
    public static string? InputItemRefusal(ItemCatalog catalog, Item item)
    {
        if (!item.Components.Contains("Mods")) return "only items with modifiers can be Allflame crafted";
        if (catalog.ByHash(item.TypeHash) is not { Supported: true } type) return "unsupported item base";
        if (ClassOf(type.ClassRow) is null) return $"item class {type.Class} cannot be Allflame crafted";
        if (type.Unmodifiable) return "this item base cannot be modified";
        if (item.Corrupted) return "corrupted items cannot be Allflame crafted";
        if (item.Mirrored) return "mirrored items cannot be Allflame crafted";
        if (item.Vestigial) return "vestigial items cannot be Allflame crafted";
        if (item.Foreseeing) return "items with Hinekora's Lock cannot be Allflame crafted";
        if (item.Sockets.Any(s => s.Gem is not null)) return "remove socketed gems before Allflame crafting";
        if (item.HeaderFlags != 0) return "contraband items cannot be Allflame crafted";
        return null;
    }

    /// <summary>C→S 0x010f (<see cref="InstanceItems.CraftingSlotMoveId"/>): Ctrl + click an item into or out of
    /// inventory 105 (<c>DeepwaterCraftingInput</c>).</summary>
    public static async Task QuickMove(IModuleContext module, Player player, InstanceItems.LeagueWindowMove move)
    {
        var modPlayer = PlayerOf(player);
        if (!DeepwaterModule.IsOnDeck(player))
        {
            module.Log($"{modPlayer.Character} <= 0x010f Allflame Crafting quick-move: refused (not on The Sovereign)");
            return;
        }
        var items = ItemsOf(player);
        byte from = move.Into ? move.FromExpanded ? PlayerInventories.ExpandedInventory : PlayerInventories.MainInventory : InputRow;
        string what = $"0x010f Ctrl + click item {move.ItemId} {(move.Into ? "into" : "out of")} Allflame Crafting";
        var it = items.Items(from).FirstOrDefault(i => i.Id == move.ItemId);
        string? why = it is null ? $"not in row {from}" : move.Into ? Refusal(player, InputRow, it.Item) : null;
        if (why is not null)
        {
            module.Log($"{modPlayer.Character} <= {what}: refused ({why})");
            return;
        }
        if (move.Into)
        {
            var existing = items.Items(InputRow).FirstOrDefault();
            (byte X, byte Y)? swapCell = null;
            if (existing is not null)
            {
                var existingShape = ShapeOf(existing.Item) ?? new(1, 1, "");
                swapCell = FindSwapCell(items, from, it!, existingShape);
                if (swapCell is null)
                {
                    module.Log($"{modPlayer.Character} <= {what}: refused (existing item does not fit back in row {from})");
                    return;
                }
            }
            InventoryItem? placed = null, swapped = null;
            Saves.Atomically(() =>
            {
                if (items.Remove(from, it!.Id) is not { } taken) return;
                var prev = existing is null ? null : items.Remove(InputRow, existing.Id);
                placed = items.Put(InputRow, taken with { X = 0, Y = 0 });
                if (prev is not null && swapCell is { } sc)
                    swapped = items.Put(from, prev with { X = sc.X, Y = sc.Y });
            });
            if (placed is null)
            {
                module.Log($"{modPlayer.Character} <= {what}: refused (item moved)");
                return;
            }
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(from), items.NextRevision(from),
                    [it!.Id], swapped is null ? [] : [swapped]),
                $"0x00bf inventory {PlayerInventories.InventoryId(from)}: remove item {it.Id}" + (swapped is null ? "" : $", swap in {swapped.Id}"));
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(InputRow), items.NextRevision(InputRow),
                    existing is null ? [] : [existing.Id], [placed]),
                $"0x00bf inventory {PlayerInventories.InventoryId(InputRow)}: place item {placed.Id}");
            module.Log($"{modPlayer.Character} <= {what}: moved row {from} → {InputRow}" + (swapped is null ? "" : $" (swapped with {swapped.Id})"));
            return;
        }
        var shape = ShapeOf(it!.Item) ?? new(1, 1, "");
        byte to = PlayerInventories.MainInventory;
        var cell = items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
        if (cell is null && ExpandedInventoryGranted(player))
        {
            to = PlayerInventories.ExpandedInventory;
            cell = items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
        }
        if (cell is not { } dest)
        {
            module.Log($"{modPlayer.Character} <= {what}: refused (no room)");
            return;
        }
        InventoryItem? moved = null;
        Saves.Atomically(() =>
        {
            if (items.Remove(InputRow, it.Id) is { } taken) moved = items.Put(to, taken with { X = dest.X, Y = dest.Y });
        });
        if (moved is null)
        {
            module.Log($"{modPlayer.Character} <= {what}: refused (item moved)");
            return;
        }
        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(InputRow), items.NextRevision(InputRow), [moved.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(InputRow)}: remove item {moved.Id}");
        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(to), items.NextRevision(to), [], [moved]),
            $"0x00bf inventory {PlayerInventories.InventoryId(to)}: add item {moved.Id} at ({moved.X}, {moved.Y})");
        module.Log($"{modPlayer.Character} <= {what}: moved row {InputRow} → {to} at ({moved.X}, {moved.Y})");
    }

    static (byte X, byte Y)? FindSwapCell(PlayerInventories items, byte row, InventoryItem removing, PlayerInventories.Shape incoming)
    {
        var slot = InstanceInventories.PlayerDefaults[row];
        var taken = items.Items(row).Where(i => i.Id != removing.Id)
            .Select(i => ShapeOf(i.Item) is { } s ? (X: (int)i.X, Y: (int)i.Y, W: (int)s.Width, H: (int)s.Height) : (X: (int)i.X, Y: (int)i.Y, W: 1, H: 1))
            .ToList();
        if (removing.X + incoming.Width <= slot.Width && removing.Y + incoming.Height <= slot.Height
            && !taken.Any(t => t.X < removing.X + incoming.Width && removing.X < t.X + t.W && t.Y < removing.Y + incoming.Height && removing.Y < t.Y + t.H))
            return (removing.X, removing.Y);
        for (int x = 0; x + incoming.Width <= slot.Width; x++)
            for (int y = 0; y + incoming.Height <= slot.Height; y++)
                if (!taken.Any(t => t.X < x + incoming.Width && x < t.X + t.W && t.Y < y + incoming.Height && y < t.Y + t.H))
                    return ((byte)x, (byte)y);
        return null;
    }

    // ---- opening DeepwaterCraftingWindow from Vesper (NPCTalk#3910) ----

    /// <summary><c>GameEvents.NpcOptionPicked</c> for Vesper (#795): opens <c>DeepwaterCraftingWindow</c> (S→C 0x7b 0x69)
    /// when her option runs <c>SendOpenScreen( DeepwaterCraftingWindow )</c>. Returns false when handled, true to pass through.</summary>
    public static async Task<bool> TryOpenOption(IModuleContext module, Player player, NpcCatalog.Talk talk, bool isHubOwner)
    {
        if (talk.Npc != VesperNpc || !talk.Script.Contains("DeepwaterCraftingWindow", StringComparison.Ordinal))
            return true;
        var modPlayer = PlayerOf(player);
        if (!isHubOwner || ItemsOf(player).Items(OutputRow).Count > 0 || player.Character.HasQuestFlag(CraftInProgress))
        {
            string why = !isHubOwner ? "not the hub owner" : "unclaimed Ghostly Copies are waiting in the display cases";
            module.Log($"{modPlayer.Character} <= Vesper NPCTalk {talk.Row} \"{talk.Option}\": refused ({why})");
            return false;
        }
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcTalkScript = null;
        await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: Vesper's dialogue ends (FinishNPCChat, NPCTalk {talk.Row})");
        await modPlayer.Send(InstanceWaypoints.OpenUi(CraftingUi), $"0x007b open DeepwaterCraftingWindow (kind 0x{CraftingUi:x2}, NPCTalk {talk.Row})");
        return false;
    }

    // ---- executing a craft (C→S 0x02ca ruleset 172 op 2 DoCraft) ----

    /// <summary>Ruleset 172 op 2 (<c>DoCraft</c>): payload <c>u32 LE currencyInvId, u32 LE currencyItemId</c>.</summary>
    public static async Task DoCraft(IModuleContext module, Player player, byte[] args, bool isHubOwner)
    {
        var modPlayer = PlayerOf(player);
        string who = modPlayer.Character;
        if (!DeepwaterModule.IsOnDeck(player) || !isHubOwner || args.Length < 8)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (area={modPlayer.AreaId}, owner={isHubOwner}, len={args.Length})");
            return;
        }
        var items = ItemsOf(player);
        if (items.Items(OutputRow).Count > 0 || player.Character.HasQuestFlag(CraftInProgress))
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (unclaimed outputs in inventory 106)");
            return;
        }

        uint currencyInvId = BinaryPrimitives.ReadUInt32LittleEndian(args.AsSpan(0, 4));
        uint currencyItemId = BinaryPrimitives.ReadUInt32LittleEndian(args.AsSpan(4, 4));
        if (PlayerInventories.Row(currencyInvId) is not byte currencyRow
            || currencyRow is not (PlayerInventories.MainInventory or PlayerInventories.ExpandedInventory))
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (invalid currency inventory {currencyInvId})");
            return;
        }
        var currencyInvItem = items.Items(currencyRow).FirstOrDefault(i => i.Id == currencyItemId);
        var catalog = ItemsFor(player);
        if (currencyInvItem is null || catalog.ByHash(currencyInvItem.Item.TypeHash) is not { } currencyType)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (currency item {currencyItemId} not found in row {currencyRow})");
            return;
        }
        if (!HasCurrency(currencyType.Hash))
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused ({currencyType.Name} is not an Allflame crafting currency)");
            return;
        }
        var row = ResolveCurrency(player, currencyType.Hash);
        if (row is null)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused ({currencyType.Name} is locked by league quest flags)");
            return;
        }
        bool hardMode = LeagueFor(player).HardMode;
        if (hardMode && row.DisabledInRuthless)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused ({currencyType.Name} is disabled in Ruthless)");
            return;
        }

        bool isChangeling = currencyType.Hash == DeepwaterDucats.ChangelingHash;
        InventoryItem? targetInvItem = null;
        ItemCatalog.BaseType? targetType = null;
        if (!isChangeling)
        {
            var inputs = items.Items(InputRow);
            if (inputs.Count != 1)
            {
                module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (expected 1 item in inventory 105, found {inputs.Count})");
                return;
            }
            targetInvItem = inputs[0];
            if (InputItemRefusal(catalog, targetInvItem.Item) is { } refusal)
            {
                module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused ({refusal})");
                return;
            }
            targetType = catalog.ByHash(targetInvItem.Item.TypeHash);
            if (targetType is null)
            {
                module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (unknown target base)");
                return;
            }
        }

        int cost = SulphurCost(row, targetInvItem?.Item, targetType, hardMode);
        int held = DeepwaterLocker.SulphurHeld(player);
        if (held < cost)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused ({cost} Sulphur needed, {held} held)");
            await modPlayer.Send(InstanceChat.ChatLine($"Not enough Dead Man's Sulphur ({cost} needed, {held} held)."),
                "0x0017 chat line: insufficient Sulphur for Allflame crafting");
            return;
        }

        var random = AreaRng(player);
        int copyCount = Math.Clamp(row.MaxCopies, 1, 4);
        bool intangTriggered = targetInvItem is { Item.Intangibility: > 0 } && copyCount > 1
            && random.Next(100) < targetInvItem.Item.Intangibility;
        if (intangTriggered) copyCount = 1;

        var copies = new List<Item>(copyCount);
        bool destroyed = false;
        if (currencyType.Hash == DeepwaterDucats.TzamotoHash)
        {
            if (targetInvItem is null || targetInvItem.Item.Intangibility == 0)
            {
                module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (Tzamoto's Ducat requires Intangibility > 0)");
                await modPlayer.Send(InstanceChat.ChatLine("Tzamoto's Ducat requires an item with Intangibility."),
                    "0x0017 chat line: Tzamoto's Ducat refused");
                return;
            }
            if (DeepwaterDucats.ApplyTzamoto(targetInvItem.Item, random, out destroyed) is { } survived)
                copies.Add(survived);
            else if (!destroyed)
            {
                module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (Tzamoto's Ducat target invalid)");
                return;
            }
        }
        else
        {
            for (int i = 0; i < copyCount; i++)
            {
                var rolled = ApplyCraftCurrency(player, currencyType, targetInvItem?.Item, targetType, random);
                if (rolled is null)
                {
                    if (i == 0)
                    {
                        module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused ({currencyType.Name} cannot apply to {targetType?.Name ?? "none"} or is not implemented)");
                        await modPlayer.Send(InstanceChat.ChatLine($"{currencyType.Name} cannot be applied to {targetType?.Name ?? "this item"}."),
                            "0x0017 chat line: Allflame craft currency refused");
                        return;
                    }
                    break;
                }
                rolled = ItemRolls.ClearForesee(rolled);
                if (targetInvItem is { Item: var src })
                {
                    // G: Intangibility increase rolled independently per Ghostly Copy; preserve prior Intangibility and
                    // MemoryStrands unconditionally so currencies that build a new Item (e.g. Ancient Orb) cannot wipe them.
                    int add = row.IntangMax > 0 ? random.Next(row.IntangMin, Math.Max(row.IntangMin, row.IntangMax) + 1) : 0;
                    byte intang = (byte)Math.Min(100, src.Intangibility + add);
                    if (rolled.Intangibility != intang || rolled.MemoryStrands != src.MemoryStrands)
                        rolled = rolled with { Intangibility = intang, MemoryStrands = src.MemoryStrands };
                }
                copies.Add(rolled);
            }
            if (copies.Count == 0) return;
        }

        // Atomic commit: consume currency, remove target, pay Sulphur, store outputs in 106; throw to roll back on any failure.
        DeepwaterLocker.SulphurPayment sulphurPayment = default;
        PlayerInventories.Consumption? consumed = null;
        InventoryItem? removedTarget = null;
        var placedOutputs = new List<InventoryItem>(copies.Count);
        bool committed = false;

        try
        {
            Saves.Atomically(() =>
            {
                if (items.Items(OutputRow).Count > 0) return;
                if (!items.Items(currencyRow).Any(i => i.Id == currencyItemId)) return;
                if (!isChangeling && !items.Items(InputRow).Any(i => i.Id == targetInvItem!.Id)) return;
                // Check Sulphur availability before mutating items so insufficient Sulphur exits cleanly without rolling back live inventories.
                if (cost > 0 && DeepwaterLocker.SulphurHeld(player) < cost) return;
                if (!DeepwaterLocker.TryPaySulphurInTransaction(player, cost, out sulphurPayment))
                    return;
                consumed = items.ConsumeOne(currencyRow, currencyItemId);
                if (consumed is null) throw new DeepwaterLocker.AbortTransactionException();
                if (!isChangeling)
                {
                    removedTarget = items.Remove(InputRow, targetInvItem!.Id);
                    if (removedTarget is null) throw new DeepwaterLocker.AbortTransactionException();
                }
                for (int i = 0; i < copies.Count; i++)
                {
                    var placed = items.Put(OutputRow, new InventoryItem(items.NewItemId(), (byte)(i * 2), 0, copies[i]));
                    placedOutputs.Add(placed);
                }
                committed = true;
            });
        }
        catch (DeepwaterLocker.AbortTransactionException)
        {
            committed = false;
            consumed = null;
            removedTarget = null;
            placedOutputs.Clear();
        }

        if (!committed || consumed is null)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: refused (inventory or Sulphur changed before commit)");
            return;
        }

        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(currencyRow), items.NextRevision(currencyRow),
                [currencyItemId], consumed.Left is null ? [] : [consumed.Left]),
            $"0x00bf inventory {PlayerInventories.InventoryId(currencyRow)}: consume 1 {currencyType.Name}");
        if (removedTarget is not null)
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(InputRow), items.NextRevision(InputRow),
                    [removedTarget.Id], []),
                $"0x00bf inventory {PlayerInventories.InventoryId(InputRow)}: remove crafted target {removedTarget.Id}");
        await DeepwaterLocker.SendSulphurPayment(player, sulphurPayment);
        if (placedOutputs.Count > 0)
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(OutputRow), items.NextRevision(OutputRow),
                    [], placedOutputs),
                $"0x00bf inventory {PlayerInventories.InventoryId(OutputRow)}: {placedOutputs.Count} Ghostly Copies stored");

        if (!HasLeagueFlag(player, DeepwaterConstants.CraftingTutorialCompletedLeagueFlag))
        {
            await SetLeagueQuestFlags(player, [DeepwaterConstants.CraftingTutorialCompletedLeagueFlag]);
            await DeepwaterProgression.RefreshHubNpcs(module, player);
        }

        if (destroyed)
        {
            module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: {currencyType.Name} ({cost} Sulphur) destroyed {targetType?.Name}");
            await modPlayer.Send(InstanceChat.ChatLine("Tzamoto's Ducat destroyed your item."),
                "0x0017 chat line: Tzamoto's Ducat destroyed item");
            return;
        }

        await SetQuestFlags(player, [CraftInProgress]);
        module.Log($"{who} <= 0x02ca DeepwaterHubRules DoCraft: {currencyType.Name} on {targetType?.Name ?? "(none)"} for {cost} Sulphur → {placedOutputs.Count} copies"
            + (intangTriggered ? $" (Intangibility {targetInvItem!.Item.Intangibility}% triggered → 1 copy)" : ""));

        // If the crafting window pauses a lone player, update the world vats once unpaused so lockstep records never stall.
        if (player.Paused)
        {
            var instance = player.Instance;
            _ = Task.Run(async () =>
            {
                try
                {
                    await global::Program.WhilePaused(player);
                    if (!player.Closed && !player.LeavingArea && player.Instance == instance)
                        await ShowCopies(module, player, placedOutputs, targetInvItem?.Item);
                }
                catch (Exception ex) { module.Log($"{who}: Allflame crafting vat update failed: {ex.Message}"); }
            });
        }
        else
        {
            await ShowCopies(module, player, placedOutputs, targetInvItem?.Item);
        }
    }

    // ---- currency application ----

    /// <summary>Applies a single Allflame crafting currency to <paramref name="target"/> (or null for Changeling's Ducat).
    /// Returns null for invalid targets or deferred niche currencies (Resonators, Veiled, Conflict/Dominance, Heist, Memory)
    /// before any Sulphur or currency is spent.</summary>
    public static Item? ApplyCraftCurrency(Player player, ItemCatalog.BaseType currencyType, Item? target,
        ItemCatalog.BaseType? targetType, Random random) =>
        ApplyCraftCurrency(ItemsFor(player), RollsFor(player), EssencesFor(player), UniquesFor(player), DropsFor(player),
            currencyType, target, targetType, random);

    public static Item? ApplyCraftCurrency(ItemCatalog catalog, ItemRolls rolls, EssenceCrafting essences,
        UniqueCatalog uniques, ItemDrops drops, ItemCatalog.BaseType currencyType, Item? target,
        ItemCatalog.BaseType? targetType, Random random)
    {
        if (currencyType.Hash == DeepwaterDucats.ChangelingHash)
            return DeepwaterDucats.Apply(catalog, rolls, currencyType.Hash, null, null, random);

        if (target is null || targetType is null) return null;
        if (target.Rarity != ItemRarity.Normal && !target.Identified) return null;

        // Stat 23023 (can_be_ghostflame_crafted_as_though_rare) allows rare-targeting crafts on qualifying Uniques (H).
        bool treatUniqueAsRare = target.Rarity == ItemRarity.Unique
            && ItemModStats.LocalValue(target, catalog.Mods, TreatUniqueAsRareStat) != 0;

        // Unique-aware currencies operate directly on the real item so UniqueCatalog rolls/reforge work.
        switch (currencyType.Id)
        {
            case CurrencyEffects.BlessedOrb:
                return rolls.Bless(target, random, uniques.RollsOf(target));
            case CurrencyEffects.DivineOrb:
                return rolls.Reforge(target, random, uniques.RollsOf(target));
            case CurrencyEffects.SacredOrb:
                return CurrencyEffects.Sacred(target, catalog.Defences(targetType), random);
            case CurrencyEffects.AncientOrb:
                return uniques.Reforge(target, drops.UniqueWeight, random);
        }

        Item effective = treatUniqueAsRare ? target with { Rarity = ItemRarity.Rare } : target;
        Item? result;

        if (DeepwaterDucats.IsDucat(currencyType.Hash))
        {
            result = DeepwaterDucats.Apply(catalog, rolls, currencyType.Hash, effective, targetType, random);
        }
        else if (essences.Contains(currencyType.Hash))
        {
            result = essences.Apply(currencyType.Hash, effective, random);
        }
        else if (InfluencedExalts.TryGetValue(currencyType.Id, out var influence))
        {
            result = ApplyInfluencedExalt(catalog, rolls, effective, targetType, influence, random);
        }
        else
        {
            result = currencyType.Id switch
            {
                CurrencyEffects.OrbOfTransmutation => CurrencyEffects.Transmutation(effective, rolls, random),
                CurrencyEffects.OrbOfAugmentation => CurrencyEffects.Augmentation(effective, rolls, random),
                CurrencyEffects.OrbOfAlteration => CurrencyEffects.Alteration(effective, rolls, random),
                CurrencyEffects.RegalOrb => CurrencyEffects.Regal(effective, rolls, random),
                CurrencyEffects.OrbOfAlchemy => CurrencyEffects.Alchemy(effective, rolls, random),
                CurrencyEffects.OrbOfBinding => CurrencyEffects.Binding(effective, targetType, rolls, catalog.Requirements(targetType), random),
                CurrencyEffects.ChaosOrb => CurrencyEffects.Chaos(effective, rolls, random),
                CurrencyEffects.ExaltedOrb => CurrencyEffects.Exalted(effective, rolls, random),
                CurrencyEffects.OrbOfAnnulment => CurrencyEffects.Annulment(effective, rolls, random),
                CurrencyEffects.OrbOfScouring => CurrencyEffects.Scouring(effective, rolls),
                CurrencyEffects.OrbOfChance => CurrencyEffects.Chance(effective, rolls, random),
                CurrencyEffects.EldritchChaosOrb => CurrencyEffects.EldritchChaos(effective, rolls, random),
                CurrencyEffects.EldritchExaltedOrb => CurrencyEffects.EldritchExalted(effective, rolls, random),
                CurrencyEffects.EldritchAnnulmentOrb => CurrencyEffects.EldritchAnnulment(effective, rolls, random),
                CurrencyEffects.FracturingOrb => CurrencyEffects.Fracture(effective, rolls, random),
                FoulbornAug => ApplyFoulborn(catalog, rolls, effective, targetType, FoulbornKind.Augment, random),
                FoulbornRegal => ApplyFoulborn(catalog, rolls, effective, targetType, FoulbornKind.Regal, random),
                FoulbornExalt => ApplyFoulborn(catalog, rolls, effective, targetType, FoulbornKind.Exalt, random),
                // Deferred niche currencies (Resonators, Veiled, Conflict/Dominance, Tempering/Tailoring, Memory): refuse cleanly.
                _ => null,
            };
        }

        if (result is null) return null;
        if (treatUniqueAsRare) result = result with { Rarity = ItemRarity.Unique, NameWords = target.NameWords };
        return result;
    }

    static Item? ApplyInfluencedExalt(ItemCatalog catalog, ItemRolls rolls, Item target, ItemCatalog.BaseType targetType,
        ItemInfluence influence, Random random)
    {
        if (target.Rarity != ItemRarity.Rare || target.Influences != ItemInfluence.None || target.Synthesised
            || target.FracturedCount > 0 || rolls.HasEldritchImplicit(target)
            || target.Components.Contains("Flask") || !rolls.Modifiable(target))
            return null;
        var withInfluence = rolls.TagsWith(targetType, influence);
        string? inflTag = withInfluence.FirstOrDefault(t => !targetType.Tags.Contains(t));
        if (inflTag is null) return null;

        var influenced = target with { Influences = influence };
        var eligible = rolls.AffixesToAdd(influenced, rolls.RareMaxAffixes(influenced))
            .Where(p => p.Mod.SpawnWeights.Any(w => w.Tag == inflTag && w.Weight > 0))
            .ToList();
        if (eligible.Count == 0) return null;

        var chosen = DeepwaterDucats.WeightedPick(eligible, random);
        return DeepwaterDucats.InsertExplicitMod(influenced, catalog.Mods, chosen, random);
    }

    enum FoulbornKind { Augment, Regal, Exalt }

    // G: Foulborn orbs bias eligible affixes toward higher-level tiers via weight * (1 + mod.Level / 20).
    static Item? ApplyFoulborn(ItemCatalog catalog, ItemRolls rolls, Item target, ItemCatalog.BaseType targetType,
        FoulbornKind kind, Random random)
    {
        if (!rolls.Modifiable(target)) return null;
        if (kind is FoulbornKind.Augment or FoulbornKind.Regal && target.Rarity != ItemRarity.Magic) return null;
        if (kind == FoulbornKind.Exalt && target.Rarity != ItemRarity.Rare) return null;
        if (kind is FoulbornKind.Regal or FoulbornKind.Exalt && target.Components.Contains("Flask")) return null;

        var max = kind == FoulbornKind.Augment ? (1, 1) : rolls.RareMaxAffixes(target);
        var affixes = rolls.AffixesToAdd(target, max);
        if (affixes.Count == 0) return null;

        long total = 0;
        var weighted = new (ModCatalog.Mod Mod, long Weight)[affixes.Count];
        for (int i = 0; i < affixes.Count; i++)
        {
            long w = Math.Max(1L, (long)affixes[i].Weight * (1 + affixes[i].Mod.Level / 20));
            weighted[i] = (affixes[i].Mod, w);
            total += w;
        }
        long roll = random.NextInt64(Math.Max(1, total));
        var chosen = weighted[^1].Mod;
        foreach (var (mod, weight) in weighted)
        {
            if (roll < weight) { chosen = mod; break; }
            roll -= weight;
        }

        var result = DeepwaterDucats.InsertExplicitMod(target, catalog.Mods, chosen, random);
        return kind == FoulbornKind.Regal
            ? result with { Rarity = ItemRarity.Rare, Identified = true, NameWords = rolls.RareName(targetType, random) }
            : result;
    }

    // ---- 4 GhostflameCraftingOutput display cases & relog reconstruction ----

    sealed class VatGroup
    {
        public readonly Lock Gate = new();
        public string? Owner;
        public List<Vat> List = [];
    }

    sealed class Vat
    {
        public uint Id;
        public required int X, Y, Reach;
        public required float Rotation;
        public float? Z;
        public string? Model;
        public uint OutputItemId;  // item id in inv 106 (0 if empty)
        public Item? Copy;         // Primary item in HeistRewardDisplay
        public Item? Original;     // Secondary item (in-session hover mod diff; null after relog)
        public int OpenAttempts;   // 0 = intact, 1 = cracked (CrackMeAllflame.epk)
        public int TakeState;      // 0 = idle, 2 = audio_attach.ao after claim
    }

    static readonly ConditionalWeakTable<InstanceState, VatGroup> Vats = new();

    static QuestObjectTypes.Info? VatInfo;
    static StateMachineData? VatStates;

    /// <summary>Spawns the 4 <c>GhostflameCraftingOutput</c> vats below deck on The Sovereign, reconstructing any unclaimed
    /// Ghostly Copies from inventory 106 and syncing <c>QF 3578 HasDeepwaterCraftInProgress</c>.</summary>
    public static async Task SpawnVats(IModuleContext module, Player player, bool isHubOwner)
    {
        var modPlayer = PlayerOf(player);
        if (!DeepwaterModule.IsOnDeck(player) || player.Instance is not { } instance) return;
        if (VatInfo is not { } info || VatStates is null)
        {
            module.Log($"{modPlayer.Character}: {VatType} cannot be serialized ({string.Join(", ", QuestObjectTypes.Find(VatType)?.Components ?? [])})");
            return;
        }

        var group = Vats.GetOrCreateValue(instance);
        var outputs = ItemsOf(player).Items(OutputRow).OrderBy(i => i.X).ToList();
        Vat[] snapshot;

        lock (group.Gate)
        {
            if (group.List.Count == 0)
            {
                var placements = (player.Layout.Level?.RoomObjects ?? [])
                    .Where(o => o.Type == VatType)
                    .OrderBy(o => o.Y).ThenBy(o => o.X)
                    .ToList();
                foreach (var o in placements)
                {
                    group.List.Add(new Vat
                    {
                        Id = player.TakeObjectId(),
                        X = o.X,
                        Y = o.Y,
                        Reach = info.Size + 2,
                        Rotation = o.Rotation,
                        Z = o.Z,
                        Model = string.IsNullOrEmpty(o.Model) || o.Model == info.AnimatedModel ? null : o.Model,
                    });
                }
            }
            // Reconstruct unclaimed copies from inventory 106 on fresh hub entry / relog.
            if (isHubOwner && outputs.Count > 0 && !group.List.Any(v => v.Copy is not null))
            {
                group.Owner = modPlayer.Character;
                for (int i = 0; i < group.List.Count; i++)
                {
                    var v = group.List[i];
                    if (i < outputs.Count)
                        (v.OutputItemId, v.Copy, v.Original, v.OpenAttempts, v.TakeState) = (outputs[i].Id, outputs[i].Item, null, 0, 0);
                    else
                        (v.OutputItemId, v.Copy, v.Original, v.OpenAttempts, v.TakeState) = (0, null, null, 0, 0);
                }
            }
            snapshot = [.. group.List];
        }

        // Sync QF 3578 HasDeepwaterCraftInProgress with inventory 106.
        bool hasOutputs = outputs.Count > 0;
        if (hasOutputs && !player.Character.HasQuestFlag(CraftInProgress))
            await SetQuestFlags(player, [CraftInProgress]);
        else if (!hasOutputs && player.Character.HasQuestFlag(CraftInProgress))
            await SetQuestFlags(player, [CraftInProgress], on: false);

        foreach (var vat in snapshot) await CreateVat(player, vat);
    }

    /// <summary>Populates the 4 vats with the newly rolled Ghostly Copies (and pre-craft <paramref name="original"/> as
    /// <c>Secondary</c> so hovering shows the client's native mod diff).</summary>
    public static async Task ShowCopies(IModuleContext module, Player player, IReadOnlyList<InventoryItem> outputs, Item? original)
    {
        if (player.Instance is not { } instance || !Vats.TryGetValue(instance, out var group)) return;
        var updates = new List<(Vat Vat, uint OldId)>();
        lock (group.Gate)
        {
            group.Owner = PlayerOf(player).Character;
            for (int i = 0; i < group.List.Count; i++)
            {
                var v = group.List[i];
                uint oldId = v.Id;
                v.Id = player.TakeObjectId();
                if (i < outputs.Count)
                    (v.OutputItemId, v.Copy, v.Original, v.OpenAttempts, v.TakeState) = (outputs[i].Id, outputs[i].Item, original, 0, 0);
                else
                    (v.OutputItemId, v.Copy, v.Original, v.OpenAttempts, v.TakeState) = (0, null, null, 0, 0);
                updates.Add((v, oldId));
            }
        }
        foreach (var (vat, oldId) in updates) await RecreateVat(player, vat, oldId);
    }

    /// <summary>C→S 0x0024 (<c>Interact</c>) on one of the 4 <c>GhostflameCraftingOutput</c> vats: click 1 cracks the glass
    /// (<c>openAttempts = 1</c>, uncracking any other cracked vat), click 2 claims the copy, clears all 4 vats and inventory
    /// 106, and clears <c>QF 3578</c>. Returns false when <paramref name="use"/> is not a vat.</summary>
    public static async Task<bool> UseVat(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double at, bool isHubOwner)
    {
        var modPlayer = PlayerOf(player);
        if (!DeepwaterModule.IsOnDeck(player) || player.Instance is not { } instance || !Vats.TryGetValue(instance, out var group))
            return false;

        Vat? clicked;
        lock (group.Gate) clicked = group.List.FirstOrDefault(v => v.Id == use.TargetId);
        if (clicked is null) return false;

        if (!await WorldOf(player).WalkUpTo(clicked.X, clicked.Y, clicked.Reach, at)) return true;

        bool ownsVats;
        lock (group.Gate) ownsVats = isHubOwner && group.Owner is not null
            && group.Owner.Equals(modPlayer.Character, StringComparison.OrdinalIgnoreCase);
        if (!ownsVats || clicked.Copy is null)
        {
            await DeepwaterLoot.EndAction(player);
            return true;
        }

        // Click 1: crack this vat (openAttempts = 1) and reset any previously cracked vat back to 0 (G: Heist display case rule).
        if (clicked.OpenAttempts == 0)
        {
            var updates = new List<(Vat Vat, uint OldId)>();
            lock (group.Gate)
            {
                if (clicked.Copy is null || clicked.OpenAttempts != 0)
                {
                    updates.Clear();
                }
                else
                {
                    foreach (var other in group.List)
                    {
                        if (other == clicked || other.OpenAttempts == 0) continue;
                        uint oldOtherId = other.Id;
                        other.OpenAttempts = 0;
                        other.Id = player.TakeObjectId();
                        updates.Add((other, oldOtherId));
                    }
                    uint oldId = clicked.Id;
                    clicked.OpenAttempts = 1;
                    clicked.Id = player.TakeObjectId();
                    updates.Add((clicked, oldId));
                }
            }
            foreach (var (vat, oldId) in updates) await RecreateVat(player, vat, oldId);
            module.Log($"{modPlayer.Character} <= 0x0024 GhostflameCraftingOutput at ({clicked.X}, {clicked.Y}): cracked (attempt 1/2)");
            await DeepwaterLoot.EndAction(player);
            return true;
        }

        // Click 2: claim the copy, clear inventory 106, clear all 4 vats, and clear QF 3578.
        Item? claimedCopy;
        uint claimedOutputId;
        lock (group.Gate)
        {
            claimedCopy = clicked.Copy;
            claimedOutputId = clicked.OutputItemId;
        }
        if (claimedCopy is null || claimedOutputId == 0)
        {
            await DeepwaterLoot.EndAction(player);
            return true;
        }

        var items = ItemsOf(player);
        var shape = ShapeOf(claimedCopy) ?? new(1, 1, "");
        bool canDrop = GameEvents.AllowsLoot(player) && Environment.GetEnvironmentVariable("Game_DROPS") != "0"
            && ItemsFor(player).ByHash(claimedCopy.TypeHash) is not null;

        var removedIds = new List<uint>();
        InventoryItem? placed = null;
        byte destRow = PlayerInventories.MainInventory;
        bool noRoom = false;
        bool claimed = false;
        Saves.Atomically(() =>
        {
            var outputs = items.Items(OutputRow);
            if (!outputs.Any(o => o.Id == claimedOutputId)) return;
            var cell = items.FreeCell(destRow, shape.Width, shape.Height, ShapeOf);
            if (cell is null && ExpandedInventoryGranted(player))
            {
                destRow = PlayerInventories.ExpandedInventory;
                cell = items.FreeCell(destRow, shape.Width, shape.Height, ShapeOf);
            }
            if (cell is null && !canDrop) { noRoom = true; return; }
            foreach (var o in outputs.ToList())
                if (items.Remove(OutputRow, o.Id) is not null) removedIds.Add(o.Id);
            if (cell is { } dest)
                placed = items.Put(destRow, new InventoryItem(items.NewItemId(), dest.X, dest.Y, claimedCopy));
            claimed = true;
        });

        if (noRoom)
        {
            await modPlayer.Send(InstanceChat.ChatLine("Make room in your inventory to claim this Ghostly Copy."),
                "0x0017 chat line: inventory full when claiming Allflame crafting vat");
            await DeepwaterLoot.EndAction(player);
            return true;
        }
        if (!claimed)
        {
            await DeepwaterLoot.EndAction(player);
            return true;
        }

        var clearedVats = new List<(Vat Vat, uint OldId)>();
        lock (group.Gate)
        {
            group.Owner = null;
            foreach (var v in group.List)
            {
                uint oldId = v.Id;
                v.Id = player.TakeObjectId();
                (v.OutputItemId, v.Copy, v.Original, v.OpenAttempts, v.TakeState) = (0, null, null, 0, v == clicked ? 2 : 0);
                clearedVats.Add((v, oldId));
            }
        }

        if (removedIds.Count > 0)
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(OutputRow), items.NextRevision(OutputRow), removedIds, []),
                $"0x00bf inventory {PlayerInventories.InventoryId(OutputRow)}: cleared {removedIds.Count} Ghostly Copies");
        if (placed is not null)
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(destRow), items.NextRevision(destRow), [], [placed]),
                $"0x00bf inventory {PlayerInventories.InventoryId(destRow)}: claimed Ghostly Copy {placed.Id} at ({placed.X}, {placed.Y})");
        else if (ItemsFor(player).ByHash(claimedCopy.TypeHash) is { } claimedType)
        {
            var here = player.Walk.Position(AreaTime(player));
            await DeepwaterLoot.Drop(player, here.X, here.Y, 1, "Allflame crafting vat", [new(claimedCopy, claimedType)]);
        }

        foreach (var (vat, oldId) in clearedVats) await RecreateVat(player, vat, oldId);

        await SetQuestFlags(player, [CraftInProgress], on: false);
        module.Log($"{modPlayer.Character} <= 0x0024 GhostflameCraftingOutput at ({clicked.X}, {clicked.Y}): claimed copy"
            + (placed is not null ? $" into row {destRow} ({placed.X}, {placed.Y})" : " (dropped on ground)"));
        await DeepwaterLoot.EndAction(player);
        return true;
    }

    static async Task CreateVat(Player player, Vat vat)
    {
        if (VatInfo is not { } info || VatStates is not { } states) return;
        var blob = new QuestObject(vat.X, vat.Y, info.Components, vat.Rotation, vat.Z, vat.Model)
        {
            StateMachine = states.Set("has_item", vat.Copy is not null ? 1 : 0).Set("take", vat.TakeState),
            RewardDisplay = new RewardDisplayData(vat.Copy, vat.Original, vat.OpenAttempts),
        }.Blob();
        var modPlayer = PlayerOf(player);
        if (!await global::Game.SendWorldCreate(modPlayer, vat.Id, InstanceEntities.CreateObject(vat.Id, InstanceEntities.ObjectTypeHash(VatType), blob),
            $"0x02bb GhostflameCraftingOutput 0x{vat.Id:x} at ({vat.X}, {vat.Y}), has_item {(vat.Copy is not null ? 1 : 0)}, open {vat.OpenAttempts}, take {vat.TakeState}"))
            return;
        await global::Game.SendWorldRecord(modPlayer, vat.Id, InstanceProtocol.EnterWakeZone(vat.Id));
        if (info.Blocking) player.SetObjectBlocker(vat.Id, vat.X, vat.Y, Math.Max(1, info.Size), info.ActorOrChest);
    }

    static async Task RecreateVat(Player player, Vat vat, uint oldId)
    {
        foreach (var member in DeepwaterLoot.Members(player))
        {
            member.RemoveBlocker(oldId);
            await global::Game.SendWorldRemoval(PlayerOf(member), oldId, $"0x02bc remove GhostflameCraftingOutput 0x{oldId:x}");
            await CreateVat(member, vat);
        }
    }
}
