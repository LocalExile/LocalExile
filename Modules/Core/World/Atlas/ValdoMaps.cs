using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Atlas;

/// <summary>
/// Valdo's Puzzle Box (<c>CurrencyValdoPuzzleBox</c>, CurrencyItems action <c>create_random_reliquary_map</c>) and the
/// tier 17 Valdo Map (<c>MapKeyReliquary</c>, area level 84) it opens into. Right-clicking a box (<c>C→S 0x0058</c>)
/// rolls one of world.db's <c>valdo_box_outcomes</c> by weight (the reward unique's rarity) and puts a foil Valdo Map on
/// the cursor: a random rare name, the outcome's layout and reward (the MapKey component names both: "Burial Chambers
/// Map", "Reward: Foil Mageblood"), its modifiers rolled from <c>valdo_map_mods</c> (the MapSupporter Mods rows), with
/// <c>valdo_void_map_chance_pct</c> of maps also "sent to the Void". The server reads the layout and reward back from
/// <see cref="Item.ContentData"/>. The map device opens that layout at level 84 without the Atlas
/// tree (Valdo Maps are unaffected by it, and take no fragments). The map is cleared once every rare and unique
/// monster is dead and at least 90% of the monsters are; then the Celestial foil unique drops, and the Atlas's Valdo's
/// Oubliette node (not the layout's own) completes.
/// </summary>
public static class ValdoMaps
{
    public const string BoxId = "Metadata/Items/Currency/CurrencyValdoPuzzleBox";
    public const string MapId = "Metadata/Items/Maps/MapKeyReliquary";
    const string AreaKey = "valdo.area", RewardKey = "valdo.reward", FoilKey = "valdo.foil";
    const string VoidModId = "MapSupporterVoidOnDeath";
    const string OublietteNodeId = "ValdoMap";
    const int Tier = 17, AreaLevel = 84;
    const double ClearedFraction = 0.9;

    sealed record Outcome(string MapName, string? AreaId, string Reward, byte Foil, double Weight);
    sealed record AreaRow(ushort Hash16, string Id, string Name);

    static IReadOnlyList<Outcome> _outcomes = [];
    static IReadOnlyList<(string ModId, int Weight)> _mods = [];
    static IReadOnlyDictionary<string, AreaRow> _areas = new Dictionary<string, AreaRow>();

    /// <summary>Per instance: the most monsters seen alive at once, the next check and whether the reward dropped.</summary>
    sealed class ClearState
    {
        public int Peak;
        public double NextCheck;
        public bool Rewarded;
    }
    static readonly RuleTable<object, ClearState> Clears = new();

    public static void LoadData()
    {
        var areas = new Dictionary<string, AreaRow>(StringComparer.Ordinal);
        foreach (var row in ClientTables.TableOrEmpty("WorldAreas"))
            if (row.Text("Id") is { Length: > 0 } id && row.IntOrNull("HASH16") is int hash)
                areas.TryAdd(id, new((ushort)hash, id, row.Text("Name") ?? id));
        _areas = areas;
        if (GameData.Store is not { } store) return;
        _outcomes = [.. store.LoadRows("valdo_box_outcomes", "id").Select(r => new Outcome(
            (string)r["map_name"]!, r["area_id"] as string, (string)r["reward"]!,
            (byte)Convert.ToInt64(r["foil"]), Convert.ToDouble(r["weight"])))];
        _mods = [.. store.LoadRows("valdo_map_mods", "mod_id")
            .Select(r => ((string)r["mod_id"]!, (int)Convert.ToInt64(r["weight"])))
            .Where(m => m.Item2 > 0)];
    }

    public static void Register(string owner)
    {
        GameEvents.ItemUse.Add(owner, OpenBox);
        GameEvents.MapOpening.Add(owner, Opening);
        GameEvents.ObjectUse.Add(owner, OpenChest);
        GameEvents.AreaTick.Add(owner, player => RewardOf(global::Game.MapRunOf(player)) is null ? null : TickClear);
    }

    static string? RewardOf(MapDevice.Run? run) =>
        run?.ContentData is { } data && data.TryGetValue(RewardKey, out string? reward) ? reward : null;

    /// <summary>True when the player is in a Valdo Map's instance: the layout's own Atlas node does not complete there.</summary>
    public static bool InValdoMap(Player player) => RewardOf(global::Game.MapRunOf(player)) is not null;

    static double Setting(string key, double fallback) =>
        SystemSettings.Default.Values.TryGetValue(key, out double value) && double.IsFinite(value) ? value : fallback;

    // ---- Opening a box --------------------------------------------------------------------------------------------------

    static async Task<bool> OpenBox(Player player, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (!type.Id.Equals(BoxId, StringComparison.Ordinal)) return false;
        var modPlayer = PlayerOf(player);
        string label = $"{modPlayer.Character} <= 0x0058 open Valdo's Puzzle Box ({item.Id})";
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea)
        {
            Console.WriteLine($"[valdo] {label}: refused (player unavailable)");
            return true;
        }
        if (row is not (PlayerInventories.MainInventory or PlayerInventories.ExpandedInventory))
        {
            Console.WriteLine($"[valdo] {label}: refused (row {row} is not the player inventory)");
            return true;
        }
        var items = ItemsOf(player);
        if (items.Items(PlayerInventories.Cursor).Count > 0)
        {
            await modPlayer.Tell("Empty your cursor before opening Valdo's Puzzle Box.");
            return true;
        }
        var random = AreaRng(player);
        if (Roll(random) is not { } outcome || CreateMap(player, outcome, random) is not { } map)
        {
            Console.WriteLine($"[valdo] {label}: refused (no outcome: {_outcomes.Count} outcomes, {_mods.Count} mods)");
            await modPlayer.Tell("Valdo's Puzzle Box will not open: its outcomes are missing from world.db.");
            return true;
        }

        PlayerInventories.Consumption? consumed = null;
        InventoryItem? held = null;
        Saves.Atomically(() =>
        {
            if (items.Items(PlayerInventories.Cursor).Count > 0) return;
            if (items.ConsumeOne(row, item.Id) is not { } taken) return;
            consumed = taken;
            held = items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, map));
        });
        if (consumed is null || held is null)
        {
            Console.WriteLine($"[valdo] {label}: refused (box missing or cursor occupied)");
            return true;
        }
        uint source = PlayerInventories.InventoryId(row), cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        await modPlayer.Send(InstanceItems.InventoryChange(source, items.NextRevision(row), [item.Id],
            consumed.Left is { } left ? [left] : []), $"0x00bf inventory {source}: Valdo's Puzzle Box {item.Id} consumed");
        await modPlayer.Send(InstanceItems.InventoryChange(cursor, items.NextRevision(PlayerInventories.Cursor), [], [held]),
            $"0x00bf cursor ({cursor}): Valdo Map {held.Id} ({outcome.Reward})");
        var mods = ItemsFor(player).Mods;
        Console.WriteLine($"[valdo] {label} -> {map.ContentData?.GetValueOrDefault(AreaKey) ?? "no layout"}, reward {outcome.Reward}"
            + $" (foil {outcome.Foil}); mods {string.Join(", ", map.Mods.Select(m => mods.ByHash(m.Hash)?.Id ?? $"0x{m.Hash:x4}"))}");
        return true;
    }

    static Outcome? Roll(Random random)
    {
        double total = _outcomes.Sum(o => o.Weight);
        if (total <= 0) return null;
        double pick = random.NextDouble() * total;
        foreach (var outcome in _outcomes)
            if ((pick -= outcome.Weight) < 0) return outcome;
        return _outcomes[^1];
    }

    /// <summary>A rare, identified foil Valdo Map for <paramref name="outcome"/> with its rolled Valdo modifiers.</summary>
    static Item? CreateMap(Player player, Outcome outcome, Random random)
    {
        var catalog = ItemsFor(player);
        if (catalog.Find(MapId) is not { Supported: true } type) return null;
        var pool = _mods.Select(m => (Mod: catalog.Mods.ById(m.ModId), m.Weight)).Where(m => m.Mod is not null).ToList();
        if (pool.Count == 0) return null;
        int min = (int)Math.Clamp(Setting("valdo_map_mods_min", 4), 1, pool.Count);
        int max = (int)Math.Clamp(Setting("valdo_map_mods_max", 7), min, pool.Count);
        int count = random.Next(min, max + 1);
        var chosen = new List<ModCatalog.Mod>();
        var families = new HashSet<int>();
        if (random.NextDouble() * 100 < Setting("valdo_void_map_chance_pct", 25) && catalog.Mods.ById(VoidModId) is { } voidMod)
        {
            chosen.Add(voidMod);
            families.UnionWith(voidMod.Families);
        }
        while (chosen.Count < count)
        {
            var open = pool.Where(m => !chosen.Contains(m.Mod!) && !m.Mod!.Families.Any(families.Contains)).ToList();
            int total = open.Sum(m => m.Weight);
            if (total <= 0) break;
            int pick = random.Next(total);
            var next = open.First(m => (pick -= m.Weight) < 0).Mod!;
            chosen.Add(next);
            families.UnionWith(next.Families);
        }
        // MapSupporterAdditionalModifiers: "Area has N additional random Modifiers", rolled as ordinary map affixes.
        int extra = chosen.Sum(m => m.Stats.Where(s => s.Id == Stat.MapNumberOfAdditionalMods).Sum(s => s.Min));
        if (extra > 0)
        {
            // The Valdo base's own tags roll no map affix: weigh them as a tier 16 map's.
            var tags = (catalog.Find("Metadata/Items/Maps/MapKeyTier16") ?? type).Tags;
            var affixes = catalog.Mods.All.Where(m => m.Domain == 5 && m.GenerationType is 1 or 2 && m.Level <= AreaLevel
                && m.WeightFor(tags) > 0).ToList();
            for (int i = 0; i < extra; i++)
            {
                var open = affixes.Where(m => !chosen.Contains(m) && !m.Families.Any(families.Contains)).ToList();
                int total = open.Sum(m => m.WeightFor(tags));
                if (total <= 0) break;
                int pick = random.Next(total);
                var next = open.First(m => (pick -= m.WeightFor(tags)) < 0);
                chosen.Add(next);
                families.UnionWith(next.Families);
            }
        }
        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RewardKey] = outcome.Reward,
            [FoilKey] = outcome.Foil.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        // The layout is fixed when the box opens, so the map names it ("Burial Chambers Map").
        AreaRow? layout = outcome.AreaId is { } areaId && _areas.TryGetValue(areaId, out var row) ? row : RandomLayout(random);
        if (layout is not null) content[AreaKey] = layout.Id;
        var uniques = UniquesFor(player);
        uint? rewardWord = uniques.Find(outcome.Reward) is { } definition ? uniques.NameWord(definition) : null;
        return ItemCatalog.Create(type) with
        {
            ItemLevel = AreaLevel,
            Rarity = ItemRarity.Rare,
            Identified = true,
            NameWords = RollsFor(player).RareName(type, random),
            RelicStyle = outcome.Foil,
            Mods = [.. chosen.Select(m => m.Roll(ModSection.Explicit, random))],
            ContentData = content,
            MapKey = new MapKeyData(RewardWord: rewardWord, Area: layout?.Hash16),
        };
    }

    /// <summary>A random ordinary Atlas layout, for an outcome whose layout is unknown.</summary>
    static AreaRow? RandomLayout(Random random) =>
        MapDevices.Atlas?.Layouts.Where(l => !l.RequiresSpecificItem).ToList() is { Count: > 0 } layouts
            && layouts[random.Next(layouts.Count)] is var pick ? new(pick.Hash16, pick.Id, pick.Name) : null;

    // ---- The map device -------------------------------------------------------------------------------------------------

    static void Opening(MapOpening request)
    {
        if (request.Campaign) return;
        var valdo = request.Slots.Where(s => s.BaseId == MapId).ToList();
        if (valdo.Count == 0) return;
        request.Opening = null;
        if (valdo.Count != 1 || request.Slots.Count != 1)
        {
            request.Reason = request.Slots.Count(s => s.IsMap) > 1 ? "Only one map can be activated at a time."
                : "Map fragments cannot be used with a Valdo Map.";
            return;
        }
        var slot = valdo[0];
        if (slot.SourceItem?.ContentData is not { } data || !data.TryGetValue(RewardKey, out string? reward))
        {
            request.Reason = "This Valdo Map holds no reward: open a new one from Valdo's Puzzle Box.";
            return;
        }
        AreaRow? layout = data.TryGetValue(AreaKey, out string? areaId) && _areas.TryGetValue(areaId, out var row) ? row
            : RandomLayout(global::Game.AreaRng(request.Player));
        if (layout is null)
        {
            request.Reason = "No area is available for this Valdo Map.";
            return;
        }
        var area = new MapDevice.MapArea(layout.Hash16, layout.Id, $"{layout.Name} (Valdo Map)", Tier)
        {
            Level = AreaLevel,
            UsesAtlas = false,
        };
        var content = new Dictionary<string, string>(data, StringComparer.Ordinal) { [AreaKey] = layout.Id, [RewardKey] = reward };
        request.Opening = new MapDevice.Opening(area, MapDevices.ModsOf(request.Slots, ItemsFor(request.Player).Mods), slot.BaseRow)
        {
            ContentData = content,
        };
        request.Reason = "";
    }

    // ---- Clearing the map -----------------------------------------------------------------------------------------------

    static async Task TickClear(Player player, double _)
    {
        if (player.Instance is not { } instance || global::Game.MapRunOf(player) is not { } run || RewardOf(run) is not { } reward)
            return;
        var state = Clears.GetValue(instance, _ => new ClearState());
        double now = AreaTime(player);
        if (state.Rewarded || now < state.NextCheck) return;
        state.NextCheck = now + 1;
        int living = LivingMonstersLocked(player);
        state.Peak = Math.Max(state.Peak, living);
        if (state.Peak == 0) return;
        bool eliteLeft = player.Area.Monsters.Values.Concat(player.Area.DormantMonsters.Values)
            .Any(m => MonsterLiving(m) && m.Rarity >= MonsterRarity.Rare);
        if (eliteLeft || living > state.Peak * (1 - ClearedFraction)) return;
        state.Rewarded = true;
        await Reward(player, run, reward);
    }

    /// <summary>The cleared map's Singular Reliquary. The client registers object types from its tables' rows, so the
    /// object is sent as the Chests row <c>ForgottenReliquaryValdo</c> (the bare <c>ValdoReliquaryChest</c> is not
    /// registered: "Unknown object type serialized by server"), with the components of its InheritsFrom
    /// <c>ValdoReliquaryChest</c> (the row has no object file of its own). Created with <c>spawned</c> 0 and switched
    /// to 1, which plays its rise; a click opens it and drops the reward.</summary>
    const string ChestPath = "Metadata/Chests/Legacy/ForgottenReliquaryValdo", ChestBasePath = "Metadata/Chests/ValdoReliquaryChest";

    sealed class ChestState
    {
        public uint Id;
        public int X, Y;
        public string Reward = "";
        public byte Foil;
        public bool Opened;
    }
    static readonly RuleTable<object, ChestState> Chests = new();

    static async Task Reward(Player player, MapDevice.Run run, string reward)
    {
        var data = run.ContentData!;
        byte foil = data.TryGetValue(FoilKey, out string? f) && byte.TryParse(f, out byte parsed) ? parsed : (byte)7;
        var peers = InGame.Keys.Where(c => ReferenceEquals(c.Instance, player.Instance) && !c.Closed && !c.LeavingArea).ToList();
        var spot = player.Walk.Position(AreaTime(player));
        var at = player.Layout.Near((spot.X + 6, spot.Y + 6));
        var chest = new ChestState { X = at.X, Y = at.Y, Reward = reward, Foil = foil };
        if (QuestObjectTypes.Find(ChestBasePath) is { } info && QuestObject.CanSerialize(info.Components, haveStateMachine: true))
        {
            chest.Id = player.TakeObjectId();
            Chests.AddOrUpdate(player.Instance, chest);
            await SendChest(player, info, chest, spawned: 0, "appears");
            int machine = info.Components.ToList().IndexOf("StateMachine");
            if (machine >= 0)
                await SendWorldRecord(player, chest.Id,
                    InstanceEntities.StateMachineUpdate(chest.Id, (byte)machine, new StateMachineData([1], 0, ["spawned"])),
                    $"0x02bc Singular Reliquary 0x{chest.Id:x} spawned=1");
            Console.WriteLine($"[valdo] {player.Connection.Remote} Valdo Map cleared: Singular Reliquary 0x{chest.Id:x} holds {reward} (foil {foil}) at ({at.X}, {at.Y})");
            foreach (var peer in peers) await PlayerOf(peer).Tell("Valdo Map cleared: a Singular Reliquary has appeared.");
        }
        else
        {
            Console.WriteLine($"[valdo] {player.Connection.Remote} Valdo Map cleared: no {ChestBasePath} object type; the reward drops");
            await DropReward(player, chest);
        }
        if (AtlasProgress.Find(OublietteNodeId) is not { } node) return;
        foreach (var peer in peers)
        {
            try
            {
                if (!AtlasProgress.Complete(WorldOf(PlayerOf(peer)).CharacterData, [node])) continue;
                await Send(peer, AtlasProgress.NodeMessage(node, bonus: false), $"0x01a4 Atlas map completed: {node.Name} ({node.Id}) by a Valdo Map");
                await AtlasFeature.Refresh(peer);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    static Task<bool> SendChest(Player player, QuestObjectTypes.Info info, ChestState chest, int spawned, string what)
    {
        var obj = new QuestObject(chest.X, chest.Y, info.Components, Opened: chest.Opened)
        {
            StateMachine = new StateMachineData([spawned], 0, ["spawned"]),
            Model = info.AnimatedModel,
            Rarity = (byte)ItemRarity.Unique,
        };
        return SendWorldCreate(player, chest.Id,
            () => InstanceEntities.CreateObject(chest.Id, InstanceEntities.ObjectTypeHash(ChestPath), obj.Blob()),
            $"0x02bb Singular Reliquary 0x{chest.Id:x} {what} at ({chest.X}, {chest.Y})", wake: true);
    }

    /// <summary>A click on the Singular Reliquary: walk to it, open it, drop the foil unique.</summary>
    static async Task<bool> OpenChest(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || !Chests.TryGetValue(instance, out var chest) || use.TargetId != chest.Id) return false;
        if (chest.Opened) return true;
        if (!TryBeginObjectWalk(player, requestedAt, chest.X, chest.Y, 4, out var walk)) return true;
        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action) || chest.Opened) return true;
        chest.Opened = true;
        await SendWorldRecord(player, chest.Id, InstanceProtocol.AnimationUpdate(chest.Id, 0, 0, 1),
            $"0x02bc Singular Reliquary 0x{chest.Id:x} opens");
        if (QuestObjectTypes.Find(ChestBasePath) is { } info)
        {
            await SendWorldRemoval(player, chest.Id, null);
            await SendChest(player, info, chest, spawned: 1, "opened");
        }
        await DropReward(player, chest);
        return true;
    }

    static async Task DropReward(Player player, ChestState chest)
    {
        var peers = InGame.Keys.Where(c => ReferenceEquals(c.Instance, player.Instance) && !c.Closed && !c.LeavingArea).ToList();
        if (UniquesFor(player).Create(chest.Reward, AreaRng(player)) is not { } unique)
        {
            Console.WriteLine($"[valdo] {player.Connection.Remote} reward {chest.Reward} is not a unique this server can create");
            foreach (var peer in peers) await PlayerOf(peer).Tell($"The reward ({chest.Reward}) is not available.");
            return;
        }
        var at = player.Layout.Near((chest.X + 1, chest.Y + 1));
        var ground = new WorldItemObject(at.X, at.Y, unique with { RelicStyle = chest.Foil });
        uint id = player.Instance.Ground.Add(ground, player);
        await SendGroundItem(player, id, ground, $"Valdo Map reward {chest.Reward} (foil {chest.Foil})", justDropped: true);
        Console.WriteLine($"[valdo] {player.Connection.Remote} Singular Reliquary opened: {chest.Reward} (foil {chest.Foil}) at ({at.X}, {at.Y})");
    }
}
