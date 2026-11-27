using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Mercenary;

public sealed partial class MercenaryModule
{
    /// <summary>Persistent character roster stored in <c>character_module_data</c> under <see cref="RosterSaveKey"/>.</summary>
    public sealed class RosterState
    {
        public Dictionary<byte, HiredMercenary> Slots { get; set; } = [];
        public byte? InvitedKey { get; set; }
        public List<byte[]> ExiledPairs { get; set; } = [];
    }

    /// <summary>One hired mercenary (keys <c>0..2</c> permanent in <c>Mercenary1..3</c>; key <c>3</c> temporary in <c>MercenaryTemporary</c>).</summary>
    public sealed class HiredMercenary
    {
        public byte Key { get; set; }
        public int ClassRow { get; set; }
        public int BuildRow { get; set; }
        public byte Level { get; set; }
        public int Seed { get; set; }
        public uint FirstNameHash32 { get; set; }
        public uint SecondNameHash32 { get; set; }
        public ushort FlavourHash16 { get; set; }
        public List<MercenaryTables.RolledSkill> Skills { get; set; } = [];
        public bool Dead { get; set; }
        public uint ReviveCost { get; set; }
        /// <summary>Remaining combat areas for a temporary mercenary (key 3): 3 in campaign, 0 (current map only) in maps.</summary>
        public int RemainingAreas { get; set; }
        public bool TemporaryMapOnly { get; set; }
        /// <summary>Last combat instance area id + seed visited with this temporary mercenary, so re-entering the same map/zone after visiting town/hideout does not burn a charge or expire it.</summary>
        public string? LastAreaId { get; set; }
        public uint LastAreaSeed { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsPermanent => Key <= 2;

        public InstanceMercenaries.MercenaryRosterRecord ToWireRecord()
        {
            var build = MercenaryTables.BuildOf(BuildRow);
            var baseRec = MercenaryWire.BaseRecord(build?.Hash16 ?? 0, Level, FirstNameHash32, SecondNameHash32, FlavourHash16, Skills);
            return new InstanceMercenaries.MercenaryRosterRecord(baseRec, Key, ReviveCost, Dead);
        }
    }

    // ---- Roster Persistence ---------------------------------------------------------------------------------------------------

    public static RosterState LoadRoster(Player player)
    {
        string name = CharacterName(player);
        string? json = CharacterSaves.ModuleData(name, RosterSaveKey);
        if (string.IsNullOrWhiteSpace(json)) return new RosterState();
        try { return JsonSerializer.Deserialize<RosterState>(json) ?? new RosterState(); }
        catch (JsonException) { return new RosterState(); }
    }

    static RosterState MutateRoster(Player player, Action<RosterState> mutate)
    {
        string name = CharacterName(player);
        RosterState latest = new();
        CharacterSaves.ChangeModuleData(name, RosterSaveKey, existing =>
        {
            RosterState state;
            if (string.IsNullOrWhiteSpace(existing)) state = new RosterState();
            else
            {
                try { state = JsonSerializer.Deserialize<RosterState>(existing) ?? new RosterState(); }
                catch (JsonException) { state = new RosterState(); }
            }
            mutate(state);
            latest = state;
            return JsonSerializer.Serialize(state);
        });
        return latest;
    }

    static byte? FirstFreePermanentKey(RosterState roster)
    {
        for (byte k = 0; k <= 2; k++)
            if (!roster.Slots.ContainsKey(k)) return k;
        return null;
    }

    static byte? EffectiveInvitedKey(Player player, RosterState roster)
    {
        if (roster.InvitedKey is byte k && roster.Slots.TryGetValue(k, out var rec))
        {
            if (rec.IsPermanent && !CanHavePermanentFollower(player)) return null;
            return k;
        }
        return null;
    }

    // ---- Area Ruleset (0xa2) Synchronization ----------------------------------------------------------------------------------

    async Task EnsureAreaRulesSent(Player player)
    {
        if (MercenaryTables.AreaRulesRow is not { } areaRow) return;
        bool sendAdd;
        lock (toldAreaRules)
        {
            sendAdd = !toldAreaRules.TryGetValue(player, out _);
            if (sendAdd) toldAreaRules.Add(player, new object());
        }
        if (sendAdd)
            await Send(player, InstanceProtocol.AddRuleset(areaRow), $"0x001f ruleset MercenaryAreaRules (row {areaRow})");
    }

    async Task SendAreaRosterSnapshot(Player player, RosterState roster)
    {
        if (MercenaryTables.AreaRulesRow is null) return;
        await EnsureAreaRulesSent(player);
        var records = roster.Slots.Values.OrderBy(r => r.Key).Select(r => r.ToWireRecord()).ToArray();
        var pairs = roster.ExiledPairs.Where(p => p.Length >= 2).Select(p => (p[0], p[1])).ToArray();
        byte? invited = EffectiveInvitedKey(player, roster);
        await Send(player, InstanceMercenaries.AreaRosterSnapshot(records, pairs),
            $"0x02c9 ruleset 0xa2 sub 0 ({records.Length} mercenaries)");
        await Send(player, InstanceMercenaries.AreaSetInvited(invited),
            $"0x02c9 ruleset 0xa2 sub 6 (invited={invited?.ToString() ?? "none"})");
    }

    /// <summary>Called on every <see cref="GameEvents.PlayerEntered"/>: syncs <c>0xa2</c> roster, handles temporary
    /// mercenary area expiration, spawns reserved NPCs in Town/Hideout, or spawns active companion in combat areas.</summary>
    async Task EnterRoster(Player player)
    {
        bool townOrHideout = IsTownOrHideout(player);
        bool isMap = MapDevices.IsMapArea(player.Layout.Hash);

        // Expire temporary mercenary when transitioning to a DIFFERENT combat area if its duration ended.
        bool clearedTemp = false;
        IReadOnlyList<uint> removedTempIds = [];
        string areaId = player.Layout.Id;
        uint areaSeed = player.Seed;
        var roster = MutateRoster(player, r =>
        {
            if (!r.Slots.TryGetValue(3, out var temp)) return;
            if (townOrHideout) return; // Keep temporary counter untouched while visiting town/hideout
            bool sameInstance = string.Equals(temp.LastAreaId, areaId, StringComparison.OrdinalIgnoreCase)
                && temp.LastAreaSeed == areaSeed;
            if (sameInstance) return; // Re-entering the same map/zone (e.g. via portal) never burns a charge or expires it

            if (temp.TemporaryMapOnly || temp.RemainingAreas <= 0)
            {
                r.Slots.Remove(3);
                if (r.InvitedKey == 3) r.InvitedKey = null;
                clearedTemp = true;
            }
            else
            {
                temp.RemainingAreas--;
                temp.LastAreaId = areaId;
                temp.LastAreaSeed = areaSeed;
            }
        });
        if (clearedTemp)
        {
            var inv = ItemsOf(player);
            removedTempIds = inv.ClearRow(PlayerInventories.MercenaryTemporary);
            if (removedTempIds.Count > 0)
                await Send(player, InstanceItems.InventoryChange(
                    PlayerInventories.InventoryId(PlayerInventories.MercenaryTemporary),
                    inv.NextRevision(PlayerInventories.MercenaryTemporary), removedTempIds, []),
                    "0x0041 clear expired MercenaryTemporary inventory");
        }

        await SendAreaRosterSnapshot(player, roster);

        if (townOrHideout)
            await SpawnTownMercenaries(player, roster);
        else
            await SpawnActiveCompanionIfEligible(player, roster);
    }

    // ---- Town / Hideout Reserved & Hired NPCs ---------------------------------------------------------------------------------

    async Task SpawnTownMercenaries(Player player, RosterState roster)
    {
        // Clean up any stale town mercenary NPCs from an earlier visit before spawning fresh ones
        if (loadoutNpcs.TryGetValue(player, out var existing))
        {
            uint[] oldIds;
            lock (existing) { oldIds = [.. existing.Keys]; existing.Clear(); }
            foreach (uint oldId in oldIds)
            {
                player.Area.Monsters.Remove(oldId);
                player.Instance?.World.Area.Monsters.Remove(oldId);
                player.MinionOrigins.TryRemove(oldId, out _);
                RemoveEntitySync(player, oldId);
                lock (player.Npcs) player.Npcs.Remove(oldId);
            }
        }
        if (roster.Slots.Count == 0) return;
        var map = loadoutNpcs.GetOrCreateValue(player);
        var baseAt = player.Walk.Position(AreaTime(player));
        int index = 0;
        foreach (var merc in roster.Slots.Values.OrderBy(m => m.Key))
        {
            if (MercenaryTables.ClassOf(merc.ClassRow) is not { } kind || MercenaryTables.BuildOf(merc.BuildRow) is not { } build) continue;
            if (Monsters.Find(kind.AlliedPath) is not { Supported: true } allied) continue;
            var offset = index switch { 0 => (8, -6), 1 => (-8, -6), 2 => (8, 6), _ => (-8, 6) };
            index++;
            var at = player.Layout.Near((baseAt.X + offset.Item1, baseAt.Y + offset.Item2));
            await SpawnTownMercenaryOne(player, merc, kind, build, allied, at, map);
        }
        RecordPlayerSnapshot(player);
    }

    async Task SpawnTownMercenaryOne(Player player, HiredMercenary merc, MercenaryTables.Class kind, MercenaryTables.Build build,
        MonsterCatalog.Variety allied, (int X, int Y) at, Dictionary<uint, byte> map)
    {
        if (States(allied, MercenaryWire.HiredStates) is not { } hired) return;
        var invRow = PlayerInventories.MercenaryRowForKey(merc.Key);
        var items = invRow is byte row ? ItemsOf(player).Items(row) : [];
        var looks = MercenaryEquipment.VisualsFromItems(kind, build, items);
        var computed = MercenaryEquipment.ComputeStats(player, kind, build, merc.Level, AreaLevelOf(player), items, merc.IsPermanent, merc.Skills);
        string displayName = MercenaryTables.FormatName(merc.FirstNameHash32, merc.SecondNameHash32) ?? build.Name;
        uint id = await SpawnMonster(player, allied, merc.Level, MonsterRarity.Unique, at.X, at.Y,
            friendly: true, instanceOwned: false, scripted: true, ownedLooks: looks,
            extraStats: computed.ClientStats,
            owned: (_, _) => { },
            ownedBaseData: self => [(MonsterObject.OwnerKey, 0, player.Id), (MonsterObject.SelfKey, 1, self)],
            ownedMaximumLife: computed.MaxLife,
            ownedMaximumEnergyShield: computed.MaxEnergyShield, ownedEnergyShield: computed.MaxEnergyShield,
            shape: m => m with
            {
                Name = displayName,
                Team = MercenaryWire.AlliedTeam,
                StateMachine = hired,
                MercenaryBuild = build.Hash16,
                HasDialogue = true,
                NpcFlags = 1,
                InteractionFlags = 1,
            });
        if (id == 0) return;
        if (player.Area.Monsters.TryGetValue(id, out var body))
        {
            body.States = hired;
            RecordEntitySync(player, id, looks, computed.ClientStats, body.Life);
        }
        RegisterLoadoutNpc(player, id, merc.Key, kind, at);
        lock (map) map[id] = merc.Key;
    }

    void RegisterLoadoutNpc(Player player, uint entityId, byte key, MercenaryTables.Class kind, (int X, int Y) at)
    {
        var npc = Npcs.Find(kind.AlliedPath) ?? Npcs.Find(kind.Path);
        var variety = Monsters.Find(kind.AlliedPath) ?? Monsters.Find(kind.Path);
        if (npc is null || variety is null) return;
        lock (player.Npcs)
        {
            player.Npcs[entityId] = new SpawnedNpc(npc, at.X, at.Y)
            {
                MonsterBodySize = Math.Clamp(variety.ObjectSize - 1, 0, 127),
            };
        }
        var map = loadoutNpcs.GetOrCreateValue(player);
        lock (map) map[entityId] = key;
    }

    async Task DespawnLoadoutNpcForKey(Player player, byte key)
    {
        uint? found = null;
        if (loadoutNpcs.TryGetValue(player, out var map))
        {
            lock (map)
            {
                foreach (var (id, k) in map)
                    if (k == key) { found = id; break; }
                if (found is uint fid) map.Remove(fid);
            }
        }
        if (found is uint entityId)
        {
            player.Area.Monsters.Remove(entityId);
            player.Instance?.World.Area.Monsters.Remove(entityId);
            player.MinionOrigins.TryRemove(entityId, out _);
            RemoveEntitySync(player, entityId);
            lock (player.Npcs) player.Npcs.Remove(entityId);
            await SendWorldRemoval(player, entityId, $"0x02bd remove mercenary npc 0x{entityId:x} (key {key})");
        }
    }

    // ---- C->S 0x2ca Ruleset 0xa2 (MercenaryAreaRules) Actions -----------------------------------------------------------------

    async Task<bool> HandleRulesetAction(Player player, byte[] message)
    {
        byte row;
        byte[] payload;
        try { (row, payload) = InstanceRulesets.ReadAction(message); }
        catch (InvalidDataException) { return false; }

        if (row == MercenaryTables.EncounterRulesRow)
            return await Choose(player, payload);
        if (row == MercenaryTables.AreaRulesRow)
            return await HandleAreaAction(player, payload);
        return false;
    }

    async Task<bool> HandleAreaAction(Player player, byte[] payload)
    {
        if (!InstanceMercenaries.TryReadAreaAction(payload, out var action)) return false;
        switch (action.Kind)
        {
            case InstanceMercenaries.AreaActionKind.Invite:
                return await HandleInvite(player, action.Key);
            case InstanceMercenaries.AreaActionKind.Reserve:
                return await HandleReserve(player);
            case InstanceMercenaries.AreaActionKind.Revive:
                return await HandleRevive(player, action.Key);
            case InstanceMercenaries.AreaActionKind.Dismiss:
                return await HandleDismiss(player, action.Key);
            default:
                return false;
        }
    }

    async Task<bool> HandleInvite(Player player, byte key)
    {
        if (!IsTownOrHideout(player)) return true;
        var roster = LoadRoster(player);
        if (!roster.Slots.TryGetValue(key, out var merc) || merc.Dead) return true;
        if (merc.Level > player.Pools.Level + 20) return true;
        if (merc.IsPermanent && !CanHavePermanentFollower(player)) return true;

        roster = MutateRoster(player, r =>
        {
            if (r.Slots.TryGetValue(key, out var m) && !m.Dead)
                r.InvitedKey = key;
        });
        await Send(player, InstanceMercenaries.AreaSetInvited(roster.InvitedKey),
            $"0x02c9 ruleset 0xa2 sub 6 invite key {key}");
        return true;
    }

    async Task<bool> HandleReserve(Player player)
    {
        var roster = MutateRoster(player, r => r.InvitedKey = null);
        await DespawnCompanion(player);
        await Send(player, InstanceMercenaries.AreaSetInvited(null), "0x02c9 ruleset 0xa2 sub 6 reserve (none invited)");
        return true;
    }

    async Task<bool> HandleRevive(Player player, byte key)
    {
        uint? remainingGold = null;
        bool revived = false;
        Saves.Atomically(() =>
        {
            MutateRoster(player, r =>
            {
                if (!r.Slots.TryGetValue(key, out var m) || !m.Dead) return;
                var stash = PersonalStashOf(player);
                if (m.ReviveCost > 0)
                {
                    remainingGold = stash.TrySpendGold(m.ReviveCost);
                    if (remainingGold is null) return;
                }
                else remainingGold = stash.Gold;

                m.Dead = false;
                revived = true;
            });
        });
        if (!revived) return true;
        if (remainingGold is uint gold)
            await Send(player, InstanceEntities.Gold(gold), $"0x026e gold {gold} after reviving mercenary key {key}");
        await Send(player, InstanceMercenaries.AreaSetAlive(key), $"0x02c9 ruleset 0xa2 sub 2 revive key {key}");
        return true;
    }

    async Task<bool> HandleDismiss(Player player, byte key)
    {
        bool wasInvited = false;
        bool removed = false;
        IReadOnlyList<uint> clearedIds = [];
        byte? invRow = PlayerInventories.MercenaryRowForKey(key);
        RosterState roster = new();

        Saves.Atomically(() =>
        {
            roster = MutateRoster(player, r =>
            {
                if (!r.Slots.Remove(key)) return;
                removed = true;
                if (r.InvitedKey == key) { r.InvitedKey = null; wasInvited = true; }
            });
            if (removed && invRow is byte row)
                clearedIds = ItemsOf(player).ClearRow(row);
        });

        if (!removed) return true;

        if (wasInvited)
            await DespawnCompanion(player);
        await DespawnLoadoutNpcForKey(player, key);

        if (invRow is byte rRow && clearedIds.Count > 0)
        {
            var inv = ItemsOf(player);
            await Send(player, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(rRow), inv.NextRevision(rRow), clearedIds, []),
                $"0x0041 clear dismissed mercenary inventory row {rRow}");
        }

        await Send(player, InstanceMercenaries.AreaRemove(key), $"0x02c9 ruleset 0xa2 sub 1 dismiss key {key}");
        if (wasInvited)
            await Send(player, InstanceMercenaries.AreaSetInvited(null), "0x02c9 ruleset 0xa2 sub 6 invited cleared on dismiss");
        return true;
    }

    // ---- Equipment Placement Validation, 0x4a Swap, C->S 0x010c Quick-Move & Live Refresh -------------------------------------

    string? ValidateMercenaryPlacement(InventoryPlacement placement)
    {
        if (!PlayerInventories.IsMercenaryRow(placement.Row)) return null;
        if (!PlayerInventories.IsPermanentMercenaryRow(placement.Row))
            return "temporary mercenary gear is locked";
        if (PlayerInventories.MercenaryKeyForRow(placement.Row) is not byte key)
            return "invalid mercenary inventory row";
        if (PlayerInventories.MercenarySlotAtX(placement.X) is not { } spec)
            return $"no mercenary equipment slot at x={placement.X}";

        var roster = LoadRoster(placement.Player);
        if (!roster.Slots.TryGetValue(key, out var merc))
            return $"no mercenary in slot {key}";
        if (!IsTownOrHideout(placement.Player) && roster.InvitedKey != key)
            return "reserved mercenary gear can only be changed in town or hideout";
        if (MercenaryTables.ClassOf(merc.ClassRow) is not { } kind || MercenaryTables.BuildOf(merc.BuildRow) is not { } build)
            return "unknown mercenary class or build";

        var currentItems = ItemsOf(placement.Player).Items(placement.Row);
        return MercenaryEquipment.CanEquip(placement.Player, kind, build, merc.Level, merc.IsPermanent,
            spec.EquipmentRow, placement.Item, currentItems);
    }

    async Task<bool> HandleMercenaryItemAction(Player player, InstanceItems.ItemAction action)
    {
        if (PlayerInventories.Row(action.InventoryId) is not byte row || !PlayerInventories.IsMercenaryRow(row))
            return false;
        if (!PlayerInventories.IsPermanentMercenaryRow(row))
            return true; // Swallow silently on locked temporary mercenary inventory

        var items = ItemsOf(player);
        var held = items.Items(PlayerInventories.Cursor).FirstOrDefault();
        var target = items.Items(row).FirstOrDefault(i => i.Id == action.ItemId);
        if (held is null || target is null) return true;

        // Treat clicking a held equippable item onto an occupied mercenary slot as a slot swap (same as PlaceItem).
        if (GameEvents.FirstInventoryRefusal(player, row, held.Item, target, target.X, target.Y) is not null)
            return true;

        var shapeOf = ShapeOf(player);
        var placed = items.Place(row, target.X, target.Y, shapeOf, out _, player: player);
        if (placed is null) return true;

        await Send(player, InstanceItems.InventoryChange(action.InventoryId, items.NextRevision(row),
            placed.Displaced is { } swapped ? [swapped.Id] : [], [placed.Placed]),
            $"0x0041 mercenary slot swap in row {row}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [held.Id],
            placed.Displaced is { } outItem ? [outItem with { X = 0, Y = 0 }] : []),
            "0x0041 cursor after mercenary swap");
        await GameEvents.RunInventoryChanged(player, row);
        return true;
    }

    async Task<bool> HandleQuickMove(Player player, byte[] message)
    {
        InstanceItems.MercenaryQuickMove req;
        try { req = InstanceItems.ReadMercenaryQuickMove(message); }
        catch (InvalidDataException) { return false; }

        if (PlayerInventories.MercenaryKeyForTarget(req.Target) is not byte key
            || PlayerInventories.MercenaryRowForKey(key) is not byte mercRow)
            return true;
        if (!PlayerInventories.IsPermanentMercenaryRow(mercRow))
            return true;

        var roster = LoadRoster(player);
        if (!roster.Slots.TryGetValue(key, out var merc)) return true;
        if (!IsTownOrHideout(player) && roster.InvitedKey != key) return true;
        if (MercenaryTables.ClassOf(merc.ClassRow) is not { } kind || MercenaryTables.BuildOf(merc.BuildRow) is not { } build)
            return true;

        var items = ItemsOf(player);
        bool expandedAllowed = ExpandedInventoryGranted(player);
        var currentItems = items.Items(mercRow);
        var moved = ItemMoves.MoveMercenaryEquipment(items, req.Target, req.ItemId, req.ToMercenary,
            req.FromExpanded, expandedAllowed, ShapeOf(player),
            canEquip: (equipRow, item) => MercenaryEquipment.CanEquip(player, kind, build, merc.Level, merc.IsPermanent,
                equipRow, item, currentItems) is null);
        if (moved is null) return true;

        // Publish source removal, destination placement, and optional swapped-out item
        if (moved.SwappedOut is { } swapped)
        {
            if (moved.From == moved.SwappedToRow)
            {
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.From),
                    items.NextRevision(moved.From), [req.ItemId], [swapped]),
                    $"0x0041 mercenary quick-move swap in row {moved.From}");
            }
            else
            {
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.From),
                    items.NextRevision(moved.From), [req.ItemId], []),
                    $"0x0041 mercenary quick-move remove from row {moved.From}");
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.SwappedToRow),
                    items.NextRevision(moved.SwappedToRow), [], [swapped]),
                    $"0x0041 mercenary quick-move swapped item to row {moved.SwappedToRow}");
            }
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.To),
                items.NextRevision(moved.To), [swapped.Id], [moved.Placed]),
                $"0x0041 mercenary quick-move place in row {moved.To}");
        }
        else
        {
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.From),
                items.NextRevision(moved.From), [req.ItemId], []),
                $"0x0041 mercenary quick-move remove from row {moved.From}");
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.To),
                items.NextRevision(moved.To), [], [moved.Placed]),
                $"0x0041 mercenary quick-move place in row {moved.To}");
        }

        await GameEvents.RunInventoryChanged(player, mercRow);
        return true;
    }

    PlayerMercenarySnapshot CurrentPlayerSnapshot(Player player)
    {
        var bonuses = LuminaryBonusesOf(player);
        var rules = GameEvents.ResolveCompanionRules(player, bonuses.CanHavePermanentFollower);
        return new PlayerMercenarySnapshot(rules.CanHavePermanentFollower, rules.AllowAllUniqueSlots, bonuses);
    }

    void RecordPlayerSnapshot(Player player) =>
        lastPlayerSnapshot.AddOrUpdate(player, CurrentPlayerSnapshot(player));

    void RecordEntitySync(Player player, uint entityId, IReadOnlyList<VisibleItem> looks,
        IReadOnlyList<(uint Key, int Value)> clientStats, uint life)
    {
        var map = lastEntitySync.GetOrCreateValue(player);
        lock (map) map[entityId] = new EntitySyncSnapshot(looks, clientStats, life);
    }

    void RemoveEntitySync(Player player, uint entityId)
    {
        if (lastEntitySync.TryGetValue(player, out var map))
            lock (map) map.Remove(entityId);
    }

    async Task OnInventoryChanged(Player player, byte row)
    {
        if (!PlayerInventories.IsPermanentMercenaryRow(row)) return;
        if (PlayerInventories.MercenaryKeyForRow(row) is not byte key) return;
        await RefreshMercenaryAppearanceAndStats(player, key, equipmentChanged: true);
    }

    async Task OnPlayerStatsChanged(Player player)
    {
        // Guard against re-entrancy when SyncCompanionSharedAuras updates player aura buffs and re-fires PlayerStatsChanged
        lock (refreshingPlayer)
            if (refreshingPlayer.TryGetValue(player, out _)) return;

        var current = CurrentPlayerSnapshot(player);
        if (lastPlayerSnapshot.TryGetValue(player, out var previous) && previous == current)
            return;
        lastPlayerSnapshot.AddOrUpdate(player, current);

        // Only act if this player has an active permanent companion or town/hideout permanent mercenary NPCs
        var roster = LoadRoster(player);
        if (companions.TryGetValue(player, out var comp) && comp.Mercenary.IsPermanent)
        {
            if (!current.CanHaveFollower)
            {
                MutateRoster(player, r =>
                {
                    if (r.InvitedKey is byte ik && ik <= 2) r.InvitedKey = null;
                });
                await DespawnCompanion(player);
                await Send(player, InstanceMercenaries.AreaSetInvited(null), "0x02c9 ruleset 0xa2 sub 6 follower lost");
                return;
            }
            await RefreshMercenaryAppearanceAndStats(player, comp.Mercenary.Key, equipmentChanged: false);
        }
        else if (IsTownOrHideout(player) && loadoutNpcs.TryGetValue(player, out var map))
        {
            if (!current.CanHaveFollower && roster.InvitedKey is byte ik && ik <= 2)
            {
                MutateRoster(player, r =>
                {
                    if (r.InvitedKey is byte k && k <= 2) r.InvitedKey = null;
                });
                await Send(player, InstanceMercenaries.AreaSetInvited(null), "0x02c9 ruleset 0xa2 sub 6 follower lost");
            }
            byte[] keys;
            lock (map) keys = [.. map.Values.Distinct()];
            foreach (byte key in keys)
                if (roster.Slots.TryGetValue(key, out var m) && m.IsPermanent)
                    await RefreshMercenaryAppearanceAndStats(player, key, equipmentChanged: false);
        }
    }

    async Task RefreshMercenaryAppearanceAndStats(Player player, byte key, bool equipmentChanged)
    {
        lock (refreshingPlayer) refreshingPlayer.AddOrUpdate(player, new object());
        try
        {
            var roster = LoadRoster(player);
            if (!roster.Slots.TryGetValue(key, out var merc)) return;
            if (MercenaryTables.ClassOf(merc.ClassRow) is not { } kind || MercenaryTables.BuildOf(merc.BuildRow) is not { } build) return;
            var invRow = PlayerInventories.MercenaryRowForKey(key);
            var items = invRow is byte r ? ItemsOf(player).Items(r) : [];
            var looks = MercenaryEquipment.VisualsFromItems(kind, build, items);
            var computed = MercenaryEquipment.ComputeStats(player, kind, build, merc.Level, AreaLevelOf(player), items, merc.IsPermanent, merc.Skills);

            // Find spawned entity (either active companion in combat area, or town/hideout NPC)
            uint entityId = 0;
            CompanionRuntime? activeComp = null;
            if (companions.TryGetValue(player, out var comp) && comp.Mercenary.Key == key)
            {
                entityId = comp.EntityId;
                activeComp = comp;
            }
            else if (loadoutNpcs.TryGetValue(player, out var map))
            {
                lock (map)
                {
                    foreach (var (id, k) in map)
                        if (k == key) { entityId = id; break; }
                }
            }
            if (entityId == 0 || !player.Area.Monsters.TryGetValue(entityId, out var monster) || monster.Dead) return;

            // Update monster's server-side mods, MaxLife, MaxES, and ActiveCompanion unique count
            monster.Mods = computed.Mods;
            monster.MaxLife = computed.MaxLife;
            if (monster.Life > monster.MaxLife) monster.Life = monster.MaxLife;
            if (activeComp is not null)
            {
                activeComp.Computed = computed;
                activeComp.ShieldPool.SetMaximum(computed.MaxEnergyShield);
                if (player.Companion is { } ac && ac.EntityId == entityId)
                {
                    ac.EquippedUniqueCount = computed.EquippedUniqueCount;
                    if (equipmentChanged)
                        RebuildCompanionKitAndAuras(player, activeComp, kind, build);
                    else if (activeComp.SharedAuras.Length > 0)
                        SyncCompanionSharedAuras(player, ac, activeComp.SharedAuras, AreaTime(player), scaleByLuminary: true);
                }
                else if (equipmentChanged)
                {
                    RebuildCompanionKitAndAuras(player, activeComp, kind, build);
                }
            }

            // Diff against last sent snapshot so unchanged equipment/stats/life do not spam the wire
            EntitySyncSnapshot? prev = null;
            if (lastEntitySync.TryGetValue(player, out var syncMap))
                lock (syncMap) syncMap.TryGetValue(entityId, out prev);

            bool looksChanged = equipmentChanged || prev is null || !LooksEqual(prev.Looks, looks);
            bool statsChanged = prev is null || !StatsEqual(prev.ClientStats, computed.ClientStats);
            bool lifeChanged = prev is null || prev.Life != monster.Life;

            RecordEntitySync(player, entityId, looks, computed.ClientStats, monster.Life);

            if (looksChanged && monster.Variety is { } variety)
            {
                var dummyObj = new MonsterObject(0, 0, monster.Life, variety.ComponentsOrBase) { Equipped = looks };
                if (InstanceEntities.MonsterEquipmentUpdate(entityId, dummyObj) is { } equipUpdate)
                    await SendWorldRecord(player, entityId, equipUpdate, $"0x02bc mercenary 0x{entityId:x} equipment update");
            }

            if (statsChanged)
            {
                SetStats(player, entityId, computed.ClientStats);
                await SendWorldRecord(player, entityId, InstanceEntities.StatsUpdate(entityId, computed.ClientStats),
                    $"0x01b3 mercenary 0x{entityId:x} stats update");
            }

            if (lifeChanged)
            {
                await SendWorldRecord(player, entityId, InstanceProtocol.LifeUpdate(entityId, monster.Life),
                    $"0x01b1 mercenary 0x{entityId:x} life {monster.Life}/{monster.MaxLife}");
            }
        }
        finally
        {
            lock (refreshingPlayer) refreshingPlayer.Remove(player);
        }
    }

    static bool LooksEqual(IReadOnlyList<VisibleItem> a, IReadOnlyList<VisibleItem> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    static bool StatsEqual(IReadOnlyList<(uint Key, int Value)> a, IReadOnlyList<(uint Key, int Value)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static Func<Item, PlayerInventories.Shape?> ShapeOf(Player player)
    {
        var catalog = ItemsFor(player);
        return item => catalog.ByHash(item.TypeHash) is { } bt
            ? new PlayerInventories.Shape(bt.Width, bt.Height, bt.Class, bt.MaxStack)
            : null;
    }
}
