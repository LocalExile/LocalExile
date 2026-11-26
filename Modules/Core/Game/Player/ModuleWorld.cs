using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadModuleWorldData()
    {
        StandardCrafting = new(Items!, Rolls!);
    }

    public sealed class ModuleSave(
        Func<string, string?> get,
        Func<string, Func<string?, string?>, bool> change,
        Func<string, bool>? remove = null,
        Func<string?, IReadOnlyList<string>>? keys = null) : ISavedData
    {
        public string? Get(string key) => get(key);
        public bool Change(string key, Func<string?, string?> apply) => change(key, apply);
        public bool Remove(string key) => remove?.Invoke(key) ?? false;
        public IReadOnlyList<string> Keys(string? prefix = null) => keys?.Invoke(prefix) ?? [];
    }

    public static ModuleItemCrafting StandardCrafting = default!;
    public static ModuleItemCrafting HardModeCrafting = default!;

    public sealed class ModuleWorld(Player player) : IPlayerWorld
    {
        Task Send(byte[] message, string? log) => Program.Send(player, message, log);

        public ushort AreaHash => player.Layout.Hash;
        public (int X, int Y) Spawn => (player.Layout.SpawnX, player.Layout.SpawnY);
        public bool PlayerReady => player.Connection.PlayerSent;
        public uint NewObjectId() => player.TakeObjectId();
        public void SetBlocker(uint objectId, int x, int y, int size) => player.SetBlocker(objectId, x, y, size);
        public void RemoveBlocker(uint objectId) => player.RemoveBlocker(objectId);
        public Task PushOutOfCollision() => Game.PushOutOfCollision(player);

        public async Task<uint?> SpawnHideoutStation(HideoutSlot slot, string metadataPath, string editorKey, string name)
        {
            if (player.CurrentHideoutScene is not { } scene) return null;
            if (HideoutCatalog.TypeOf(metadataPath) is not var (components, _)) return null;
            if (components.Count == 0 || !HideoutObject.CanSerialize(components)) return null;
            var (x, y, rotation) = scene.Station(slot, editorKey);
            uint id = player.TakeObjectId();
            var obj = new HideoutObject(x, y, rotation, components, new HideoutDoodad(0, 0));
            await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(metadataPath), obj.Blob()),
                $"0x02bb create {name} 0x{id:x} at ({x}, {y})");
            await SendWorldRecord(player, id, () => InstanceProtocol.EnterWakeZone(id));
            await RegisterStationaryCollision(player, id, metadataPath, x, y, rotation, editorKey);
            return id;
        }

        public (int X, int Y)? StationAt(uint objectId) => player.HideoutEditor.PositionOf(objectId) is { } p ? (p.X, p.Y) : null;

        public async Task<bool> WalkUpTo(int x, int y, int reach, double requestedAt)
        {
            if (!TryBeginObjectWalk(player, requestedAt, x, y, reach, out var walk)) return false;
            if (walk.Arrive > walk.Now) await AreaDelay(player, walk.Arrive - walk.Now);
            return !player.Closed && PlayerActionCurrent(player, walk.Action) && PlayerWalk.InWalkUpRange(walk.Stop, (x, y), reach);
        }

        public Task<bool> WalkUpToStation(uint objectId, double requestedAt) => StationAt(objectId) is { } at
            ? WalkUpTo(at.X, at.Y, (int)player.StationaryInteractionSize(objectId), requestedAt) : Task.FromResult(false);

        public async Task OpenWindow(string owner, byte ui, string log)
        {
            player.BenchOpen = false;
            player.ModuleWindow = owner;
            var here = player.Walk.Position(AreaTime(player));
            player.ModuleWindowAnchor = (owner, here.X, here.Y);
            await RefreshBenchBalance(player, clear: true);
            await Send(InstanceWaypoints.OpenUi(ui), log);
        }

        public string? OpenWindowOwner => player.ModuleWindow;

        public Task RefreshStats() => SendCharacterStats(player, PlayerStatChange.Build);

        public async Task<bool> TravelTo(string areaId, string reason)
        {
            if (AreaTransitionCatalog.HashOf(areaId) is not { } hash) return false;
            await ChangeArea(player, new PendingTransfer(hash, 0, false, AtSpawn: true), reason);
            return true;
        }

        public async Task<bool> TravelToAsync(PendingTransfer transfer, string reason)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            if (player.Closed || player.LeavingArea || transfer.Area == 0) return false;
            await ChangeArea(player, transfer, reason);
            return true;
        }

        public async Task<uint?> SpawnPortalAsync(int x, int y, string targetAreaId, PortalOptions? options = null)
        {
            if (AreaTransitionCatalog.HashOf(targetAreaId) is not { } hash) return null;
            return await SpawnCustomPortal(player, x, y, hash, options);
        }

        public Task<bool> RemovePortalAsync(uint objectId) => RemoveCustomPortal(player, objectId);

        public (int X, int Y)? FindOpenSpot((int X, int Y)? near = null, int minRadius = 0, int maxRadius = 40,
            IReadOnlyList<(int X, int Y)>? avoid = null, int minAvoidDistance = 12)
        {
            if (player.Layout.Walkable is not { } walkable) return null;
            var origin = near ?? player.Walk.Position(AreaTime(player));
            int minR = Math.Max(0, minRadius);
            int maxR = Math.Max(minR, maxRadius);
            long minRSq = (long)minR * minR;
            long maxRSq = (long)maxR * maxR;
            long avoidSq = (long)Math.Max(0, minAvoidDistance) * Math.Max(0, minAvoidDistance);
            bool ClearOfAvoid(int cx, int cy)
            {
                if (avoid is null || avoid.Count == 0 || avoidSq <= 0) return true;
                for (int i = 0; i < avoid.Count; i++)
                {
                    long dx = cx - avoid[i].X, dy = cy - avoid[i].Y;
                    if (dx * dx + dy * dy < avoidSq) return false;
                }
                return true;
            }
            for (int r = minR; r <= maxR; r++)
            {
                if (r == 0)
                {
                    if (origin.X >= 0 && origin.Y >= 0 && origin.X < player.Layout.GridW && origin.Y < player.Layout.GridH
                        && walkable(origin.X, origin.Y) && ClearOfAvoid(origin.X, origin.Y))
                        return origin;
                    continue;
                }
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        long distSq = (long)dx * dx + (long)dy * dy;
                        if (distSq < minRSq || distSq > maxRSq) continue;
                        int cx = origin.X + dx, cy = origin.Y + dy;
                        if (cx < 0 || cy < 0 || cx >= player.Layout.GridW || cy >= player.Layout.GridH) continue;
                        if (walkable(cx, cy) && ClearOfAvoid(cx, cy)) return (cx, cy);
                    }
                }
            }
            return null;
        }

        public async Task<uint?> SpawnWorldObjectAsync(string metadataPath, int x, int y, WorldObjectOptions? options = null)
        {
            if (player.Closed || player.LeavingArea || string.IsNullOrWhiteSpace(metadataPath)) return null;
            var opts = options ?? new WorldObjectOptions();
            var info = QuestObjectTypes.Find(metadataPath);
            var overlay = ObjectTypes.FindOverlay(metadataPath);
            IReadOnlyList<string>? components = info?.Components ?? overlay?.Components;
            StateMachineData? stateMachine = opts.StateMachine ?? overlay?.StateMachine;
            if (components is null || !QuestObject.CanSerialize(components, stateMachine is not null)) return null;
            uint id = player.TakeObjectId();
            var obj = new QuestObject(x, y, components, opts.Rotation, opts.Height,
                Model: opts.Model ?? info?.Model ?? overlay?.Model,
                TransitionState: opts.TransitionState,
                Opened: opts.Opened,
                Blocked: opts.Blocked)
            {
                Awake = opts.Awake,
                HideMinimapIcon = opts.HideMinimapIcon,
                StateMachine = stateMachine,
                Stats = opts.Stats ?? [],
                Variables = opts.Variables ?? [],
            };
            uint typeHash = InstanceEntities.ObjectTypeHash(metadataPath);
            await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, typeHash, obj.Blob()),
                $"0x02bb create world object 0x{id:x} ({metadataPath}) at ({x}, {y})", wake: true, awaitBirth: false);
            int blockerSize = opts.BlockerSize > 0 ? opts.BlockerSize : info is { Blocking: true } ? info.Size : 0;
            if (blockerSize > 0) player.SetBlocker(id, x, y, blockerSize);
            return id;
        }

        public async Task<bool> RemoveWorldObjectAsync(uint objectId, string? reason = null)
        {
            if (objectId == 0) return false;
            player.RemoveBlocker(objectId);
            if (!player.Closed && !player.LeavingArea)
                await SendWorldRemoval(player, objectId, reason ?? $"0x02bd remove world object 0x{objectId:x}");
            return true;
        }

        public async Task<uint?> SpawnMonsterAsync(string varietyQuery, int x, int y, MonsterSpawnOptions? options = null)
        {
            if (player.Closed || player.LeavingArea || string.IsNullOrWhiteSpace(varietyQuery)) return null;
            if (Monsters.Find(varietyQuery) is not { Supported: true } variety) return null;
            var opts = options ?? new MonsterSpawnOptions();
            byte level = opts.Level > 0
                ? opts.Level
                : (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
            uint id = await SpawnMonster(player, variety, level, opts.Rarity, x, y,
                name: opts.Name,
                friendly: opts.Friendly,
                mods: opts.Mods,
                rotation: opts.Rotation,
                extraStats: opts.ExtraStats,
                extraMods: opts.ExtraMods,
                lifePercent: opts.LifePercent,
                instanceOwned: opts.InstanceOwned,
                nonBlocking: opts.NonBlocking);
            return id == 0 ? null : id;
        }

        public async Task<bool> ApplyTimedBuffAsync(uint targetObjectId, ushort buffRow, IReadOnlyDictionary<uint, int>? stats,
            double seconds, uint sourceObjectId = 0)
        {
            if (player.Closed || player.LeavingArea || targetObjectId == 0 || seconds <= 0 || !double.IsFinite(seconds))
                return false;
            bool isPlayer = targetObjectId == player.Id;
            if (!isPlayer && !player.Area.Monsters.ContainsKey(targetObjectId)) return false;

            ushort tag = isPlayer ? player.NextBuff++ : (ushort)0;
            uint source = sourceObjectId != 0 ? sourceObjectId : player.Id;
            if (buffRow != 0)
                player.Area.Buffs.Set(targetObjectId, BuffRows.Spec(buffRow, source, stats, seconds, tag));

            if (stats is { Count: > 0 })
            {
                var owner = new object();
                var entity = new EntityId(targetObjectId);
                var copy = new Dictionary<uint, int>(stats);
                player.Area.SetStatBuff(entity, owner, copy);
                if (isPlayer) await RefreshStats();
                _ = AreaFrames.Start(async () =>
                {
                    try
                    {
                        await AreaDelay(player, seconds);
                        player.Area.RemoveStatBuff(entity, owner);
                        if (isPlayer && !player.Closed && !player.LeavingArea)
                            await RefreshStats();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
                });
            }
            return true;
        }

        public ISavedData AccountData => new ModuleSave(
            key => PersonalStashOf(player).ModuleData(key),
            (key, change) => PersonalStashOf(player).ChangeModuleData(key, change),
            key => PersonalStashOf(player).RemoveModuleData(key),
            prefix => PersonalStashOf(player).ModuleDataKeys(prefix));

        public ISavedData CharacterData => new ModuleSave(
            key => CharacterSaves.ModuleData(CharacterName(player), key),
            (key, change) => CharacterSaves.ChangeModuleData(CharacterName(player), key, change),
            key => CharacterSaves.RemoveModuleData(CharacterName(player), key),
            prefix => CharacterSaves.ModuleDataKeys(CharacterName(player), prefix));

        public ISavedData LeagueData => new ModuleSave(
            key => AccountStash.ForLeague(player.Character.League, ItemsFor(player)).ModuleData(key),
            (key, change) => AccountStash.ForLeague(player.Character.League, ItemsFor(player)).ChangeModuleData(key, change),
            key => AccountStash.ForLeague(player.Character.League, ItemsFor(player)).RemoveModuleData(key),
            prefix => AccountStash.ForLeague(player.Character.League, ItemsFor(player)).ModuleDataKeys(prefix));

        public IItemCrafting Crafting => LeagueFor(player).HardMode ? HardModeCrafting : StandardCrafting;

        SpawnedNpc? TalkingTo()
        {
            lock (player.Npcs) return player.Npcs.GetValueOrDefault(player.TalkingTo);
        }

        public Task NpcSay(string npc, string option, int index, int row)
        {
            player.InNpcTalk = true;
            return Send(InstanceNpcs.Talk(player.TalkingTo, (byte)index), $"0x0084 npc 0x{player.TalkingTo:x} {npc} talks: NPCTalk row {row} \"{option}\" (index {index})");
        }

        public void NpcEndTalk() => player.InNpcTalk = false;

        public Task NpcClose(string log)
        {
            player.InNpcTalk = false;
            player.TalkingTo = 0;
            return Send(InstanceNpcs.Talk(0), log);
        }

        public Task NpcIdentifyItems() => TalkingTo() is { } npc ? IdentifyInventory(player, npc) : Task.CompletedTask;
        public Task<bool> NpcOfferReward(int offer) => OfferNpcReward(player, offer);

        public uint ItemIdIn(byte row) => ItemsOf(player).Items(row).FirstOrDefault()?.Id ?? 0;

        public async Task<InventoryItem?> CraftInSlot(byte row, Func<Item, CraftResult?> craft, string log)
        {
            var items = ItemsOf(player);
            var stash = PersonalStashOf(player);
            if (ItemMoves.CraftInSlot(items, stash, row, craft) is not { } result) return null;
            // Clear and refill the slot in separate changes: a client panel watching the slot sees the item replaced.
            uint inventory = PlayerInventories.InventoryId(row);
            await Send(InstanceItems.InventoryChange(inventory, items.NextRevision(row), [result.Changed.Id], []), $"{log}: remove crafted item {result.Changed.Id} from the slot");
            await Send(InstanceItems.InventoryChange(inventory, items.NextRevision(row), [], [result.Changed]), $"{log}: crafted item {result.Changed.Id}");
            foreach (var payment in result.Payments)
            {
                if (payment.Inventory is { } paid)
                    await Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                        items.NextRevision(PlayerInventories.MainInventory), [.. paid.Removed, .. paid.Changed.Select(c => c.Id)], paid.Changed),
                        $"{log}: paid from inventory");
                await SendStashPayment(player, stash, payment.Stash);
            }
            return result.Changed;
        }

        public async Task<bool> PayCurrency(uint currency, int amount, uint gold, Func<bool> commit, string log)
        {
            var items = ItemsOf(player);
            var stash = PersonalStashOf(player);
            if (ItemMoves.PayCurrency(items, stash, currency, amount, gold, commit) is not { } paid) return false;
            if (paid.Inventory is { } inventory)
                await Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), [.. inventory.Removed, .. inventory.Changed.Select(c => c.Id)], inventory.Changed),
                    $"{log}: paid from inventory");
            await SendStashPayment(player, stash, paid.Stash);
            if (gold > 0) await Send(InstanceEntities.Gold(stash.Gold), $"0x02aa gold: {stash.Gold} ({log})");
            return true;
        }

        public async Task<int> ReceiveCurrency(uint currency, int amount, Func<int, bool> commit, string log)
        {
            if (ItemsFor(player).ByHash(currency) is not { } type) return 0;
            var items = ItemsOf(player);
            var (placed, stacks) = ItemMoves.ReceiveCurrency(items, ItemCatalog.Create(type), amount, ShapeOf, commit);
            foreach (var stack in stacks)
                await Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), stack.RemovedIds, stack.AddedItems), $"{log}: {placed} {type.Name}");
            return placed;
        }

        public uint Gold => PersonalStashOf(player).Gold;

        public async Task<uint> AddGoldAsync(long delta, string? reason = null)
        {
            if (HardcoreProgressBlocked(player)) return PersonalStashOf(player).Gold;
            uint total = PersonalStashOf(player).AddGold(delta);
            if (!player.Closed && player.Connection.PlayerSent)
                await Send(InstanceEntities.Gold(total), $"0x02aa gold: {total} ({reason ?? "world"})");
            return total;
        }

        public Task<bool> SetLevelAsync(int level, string? reason = null) =>
            SetCharacterLevel(player, level, reason ?? "world");

        public bool HasQuestFlag(string flag, bool earnedOnly = false) =>
            earnedOnly ? player.Character.EarnedQuestFlag(flag) : player.Character.HasQuestFlag(flag);

        public Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true) =>
            SetQuestFlags(player, flags, on);

        public Task SetLeagueQuestFlagsAsync(IEnumerable<int> flags, bool on = true) =>
            SetLeagueQuestFlags(player, flags, on);

        public Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null) =>
            GrantWaypoints(player, areaIds, reason ?? "world");

        public ushort RefundPoints => player.Character.RefundPoints;

        public Task<ushort> SetRefundPointsAsync(ushort points) =>
            SetRefundPoints(player, points);

        public Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys) =>
            AwardAscendancyPoints(player, awardKeys);

        public Task<string?> AllocatePassivesAsync(IReadOnlyList<ushort> nodes, ushort? masteryEffect = null, string? reason = null) =>
            ChangePassives(player, nodes, allocate: true, reason ?? "world", masteryEffect);

        public IReadOnlyList<CharacterSaves.LeagueCharacterInfo> GetLeagueCharacters(bool includeDead = false) =>
            player.Character.Account is { Length: > 0 } account
                ? CharacterSaves.GetLeagueCharacters(account, player.Character.League ?? GameServer.Core.Accounts.LeagueCatalog.Standard, includeDead)
                : [];
    }
}

/// <summary>The Gateway's item rules for one data set, as modules see them.</summary>
public sealed class ModuleItemCrafting : IItemCrafting
{
    readonly ItemCatalog catalog;
    readonly ItemRolls rolls;

    public ModuleItemCrafting(ItemCatalog catalog, ItemRolls rolls)
    {
        this.catalog = catalog;
        this.rolls = rolls;
        _modTypes = catalog.Mods.All.Select(m => m.ModType).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    readonly IReadOnlySet<string> _modTypes;

    public uint? TypeHash(string id) => catalog.Find(id)?.Hash;
    public IReadOnlySet<string> ModTypes => _modTypes;
    public bool Modifiable(Item item) => rolls.Modifiable(item);

    public bool HasExplicitStat(Item item, IReadOnlyCollection<string> stats) => item.Mods.Where(m => m.Section == ModSection.Explicit)
        .Any(m => catalog.Mods.ByHash(m.Hash)?.Stats.Any(s => stats.Contains(s.Name)) == true);

    public Item? RollRequiredTag(Item item, ItemRarity rarity, Random rng, string tag) => rolls.RollRequiredTag(item, rarity, rng, tag);
    public Item? RollAffinity(Item item, ItemRarity rarity, Random rng, int percent) => rolls.RollAffinity(item, rarity, rng, percent);
    public Item? Annul(Item item, Random rng) => rolls.Annul(item, rng);
    public (int Prefix, int Suffix) RareMaxAffixes(Item item) => rolls.RareMaxAffixes(item);
    public Item? AddTaggedAffix(Item item, (int Prefix, int Suffix) max, Random rng, string tag) => rolls.AddTaggedAffix(item, max, rng, tag);

    public Item? ConvertMod(Item item, IReadOnlyDictionary<string, string> map, Random rng)
    {
        // A unique's explicits are its own rolls, not pool affixes: the bench refuses uniques and unmodifiable items.
        if (item.Rarity == ItemRarity.Unique || !rolls.Modifiable(item)) return null;
        var fractured = item.FracturedIndexes();
        var candidates = Enumerable.Range(0, item.Mods.Count).Where(i => item.Mods[i].Section == ModSection.Explicit
            && !fractured.Contains(i) && catalog.Mods.ByHash(item.Mods[i].Hash) is { } mod && map.ContainsKey(mod.ModType)).ToList();
        if (candidates.Count == 0) return null;
        int at = candidates[rng.Next(candidates.Count)];
        var source = catalog.Mods.ByHash(item.Mods[at].Hash)!;
        if (catalog.ByHash(item.TypeHash) is not { } type) return null;
        // The target is another ModType (ColdResistance -> FireResistance): the nearest-level row of that group that
        // can roll on this base, with the same affix kind.
        var targetType = map[source.ModType];
        var tags = rolls.TagsWith(type, item.Influences);
        var target = catalog.Mods.All.Where(m => m.ModType.Equals(targetType, StringComparison.OrdinalIgnoreCase) &&
                m.GenerationType == source.GenerationType && m.Domain == source.Domain && m.Level <= item.ItemLevel &&
                !m.EssenceOnly && m.WeightFor(tags) > 0)
            .OrderBy(m => Math.Abs(m.Level - source.Level)).ThenBy(m => m.Level).FirstOrDefault();
        if (target is null) return null;
        for (int i = 0; i < item.Mods.Count; i++)
            if (i != at && item.Mods[i].Section == ModSection.Explicit && catalog.Mods.ByHash(item.Mods[i].Hash) is { } other &&
                (other.Hash == target.Hash || other.Families.Intersect(target.Families).Any())) return null;
        var mods = item.Mods.ToArray();
        mods[at] = target.Roll(mods[at].Section, rng);
        return item with { Mods = mods };
    }
}
