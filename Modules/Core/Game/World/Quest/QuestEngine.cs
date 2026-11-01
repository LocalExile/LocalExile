using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

// The quest engine's hooks (Quests, Agents/docs/protocol/12-quests.md §5, Agents/docs/game/campaign-quests.md): what the
// quests' tables and the client's data ask of the game on entering an area, on a kill, a pickup, an item move or use, a click
// on a quest object, a dialogue choice and every player tick. The tables come from a module (QuestObjects' Acts/); with none
// registered every query is empty and these do nothing. Quest flags are read through conn.QuestFlagsSnapshot /
// ClientHasQuestFlag and changed through SetQuestFlags (under the Monsters lock); flag changes and the item syncs hold
// conn.QuestGate, inventory removals happen only on the message path (area entry, after a read that moved items) so their
// 0xbf revisions keep their order.
static partial class Game
{
    // Ids of the server's walk blockers that mirror the client's own quest blockers (Quests.BlockerRule): blockers only,
    // never created on the client (it spawns those objects itself from the room).
    internal const uint QuestBlockerFirstId = 0xfe000000;

    // ---- quest items (QuestItems: the .it Quest component) ----

    internal static QuestItemFlags? QuestFlagsOf(ClientState conn, Item item) => ItemsFor(conn).ByHash(item.TypeHash)?.Quest;

    internal static IEnumerable<QuestItemFlags> QuestItemTypes(ClientState conn) => ItemsFor(conn).QuestItemTypes.Select(t => t.Quest!);

    // The grant flags follow the quest items carried and the extra flags come with them (QuestItems.Sync), whatever moved
    // them (a pickup, a drop, a sale, the stash). With removeItems (the message path only), carried items whose
    // remove_flag is set go: the glyphs once placed, a Medicine Chest after Nessa has one, a used book's copies.
    internal static async Task SyncQuestItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool removeItems)
    {
        await conn.QuestGate.WaitAsync();
        try
        {
            var items = ItemsOf(conn);
            List<string> set, clear;
            List<(byte Row, InventoryItem Item)> removed = [];
            lock (conn.Monsters)
            {
                if (HardcoreProgressBlocked(conn)) return;
                var own = conn.QuestFlagsSnapshot();
                var client = PlayerQuestFlags(conn);
                bool Has(string flag) => Quests.ClientHas(flag, own, client);
                var carried = QuestItems.Carried(items).Select(c => QuestFlagsOf(conn, c.Item.Item)).OfType<QuestItemFlags>().ToHashSet();
                (set, clear, var remove) = QuestItems.Sync(QuestItemTypes(conn), carried, Has);
                if (removeItems && remove.Count > 0) removed = QuestItems.RemoveAll(items, i => QuestFlagsOf(conn, i), remove);
            }
            foreach (var row in removed.GroupBy(r => r.Row))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(r => r.Item.Id)], []),
                    $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: quest items {string.Join(", ", row.Select(r => r.Item.Id))} removed (remove_flag set)");
            if (set.Count > 0) await SetQuestFlagsHeld(remote, stream, ciphers, conn, set, true);
            if (clear.Count > 0) await SetQuestFlagsHeld(remote, stream, ciphers, conn, clear, false);
        }
        finally { conn.QuestGate.Release(); }
    }

    // Quest items on this instance's ground whose remove_flag the character has are not needed any more: they go (a glyph
    // left behind before the glyphs were placed).
    internal static async Task RemoveDoneQuestItemsFromGround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var done = conn.Instance.Ground.All().Where(e => QuestFlagsOf(conn, e.Item.Item) is { } q && QuestItems.Removed(q, f => ClientHasQuestFlag(conn, f)))
            .Select(e => e.Id).ToList();
        foreach (uint id in done)
            if (conn.Instance.Ground.Take(id) is not null)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove quest item 0x{id:x}: its remove_flag is set");
    }

    // A quest item dropped by a monster or a chest where it stands (on the nearest walkable cell), unless the character no
    // longer needs it (QuestItems.Drops: remove_flag set, one carried or on this instance's ground).
    internal static async Task DropQuestItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string itemId, (int X, int Y) at, string source)
    {
        if (ItemsFor(conn).Find(itemId) is not { Supported: true } type || type.Id != itemId)
        {
            Console.WriteLine($"[gateway] {remote} quest drop {itemId} from {source}: not in the item data");
            return;
        }
        bool carried = QuestItems.Carried(ItemsOf(conn)).Any(c => c.Item.Item.TypeHash == type.Hash);
        bool onGround = conn.Instance.Ground.All().Any(e => e.Item.Item.TypeHash == type.Hash);
        if (!QuestItems.Drops(type.Quest, f => ClientHasQuestFlag(conn, f), carried, onGround))
        {
            Console.WriteLine($"[gateway] {remote} {source}: no {type.Name}, the character has one or does not need it");
            return;
        }
        var (x, y) = conn.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? conn.Layout.Near(at) : at;
        var ground = new WorldItemObject(x, y, ItemCatalog.Create(type));
        uint id = conn.Instance.Ground.Add(ground);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 quest item {type.Name} 0x{id:x} from {source} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }

    // C→S 0x82 on a dialogue row that sets a quest item's remove_flag (QuestItems.HandedIn: Nessa's "The Medicine Chest",
    // Fairgraves' "Thank You"): the item is handed in with the flag, in one save. Without one carried the talk does not
    // happen: the option list comes back. True when the talk may go on.
    internal static async Task<bool> HandInQuestItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcCatalog.Talk talk,
        string npcId)
    {
        // A row whose script sets carried quest items' remove_flag (Eramir's "Bandit Amulets", NPCTalk 95:
        // SetQuestFlag(…, "a2b-DeliveredAmulets")) takes them with that flag, in one save; so does a row a hand-in rule
        // names (Quests.HandInRule: Hargan's busts), for each of its items carried.
        var carried = QuestItems.Carried(ItemsOf(conn)).Select(c => ItemsFor(conn).ByHash(c.Item.Item.TypeHash)?.Id).OfType<string>().ToHashSet();
        var ruled = QuestsOf(conn).HandsIn(npcId, talk.Option, talk.Row).Where(carried.Contains)
            .Select(i => ItemsFor(conn).Find(i)?.Quest?.Remove).OfType<string>();
        bool Has(string f) => ClientHasQuestFlag(conn, f);
        var carriedTypes = carried.Select(i => ItemsFor(conn).Find(i)?.Quest).OfType<QuestItemFlags>().ToList();
        // Every item the row takes goes in this one click and one save, as the official servers do: the items whose
        // remove_flag its QuestFlag4 sets (Silk's "Black Venom": the venom and the map; Yeena's "Fireflies": all seven), its
        // script sets or a hand-in rule names, and those whose remove_flag its QuestFlag3 lists for an item it requires
        // (Sin's "_take_ingredients_", NPCTalk 1336: QuestFlag4 only DeliveredItem1, QuestFlag3 both ingredients).
        var flags = talk.ScriptSets.Where(f => QuestItemTypes(conn).Any(t => t.Remove == f)).Concat(ruled).ToList();
        if (QuestItems.HandedIn(talk.StartFlag, QuestItemTypes(conn), Has) is { } type)
        {
            if (!carriedTypes.Any(t => t.Remove == type.Remove))
            {
                Console.WriteLine($"[gateway] {remote} {talk.Option}: the quest item for {type.Remove} is not carried");
                await SyncQuestItems(remote, stream, ciphers, conn, removeItems: true);
                await Send(remote, stream, ciphers, InstanceNpcs.Talk(conn.TalkingTo), $"0x0084 npc 0x{conn.TalkingTo:x}: options again (no quest item)");
                return false;
            }
            flags.Add(type.Remove!);
        }
        flags.AddRange(QuestItems.HandInsOfRow(talk, carriedTypes));
        if (Npcs.Value.Find(npcId) is { } speaker)
        {
            var all = new HashSet<string>(Quests.Names(PlayerQuestFlags(conn)));
            all.UnionWith(conn.QuestFlagsSnapshot());
            flags.AddRange(QuestItems.SiblingHandIns(talk, Npcs.Value.TalksOf(speaker), all, carriedTypes));
        }
        flags = [.. flags.Where(f => !Has(f)).Distinct()];
        if (flags.Count > 0) await ConsumeQuestItemsAndSet(remote, stream, ciphers, conn, flags, $"{talk.Option} (NPCTalk {talk.Row})");
        return true;
    }

    // C→S 0x58 on a quest item with a use_flag (the quest books): it is used up and its flag set, in one save; the
    // QuestStaticRewards of the flag (passive or refund points) follow from it (SetQuestFlags sends the tree).
    internal static async Task UseQuestItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items,
        byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        var flags = type.Quest!;
        await conn.QuestGate.WaitAsync();
        try
        {
            PlayerInventories.Consumption? taken;
            lock (conn.Monsters)
            {
                if (HardcoreProgressBlocked(conn)) return;
                taken = QuestItems.Use(items, CharacterName(conn), row, item.Id, flags);
            }
            if (taken is null) return;
            uint inventoryId = PlayerInventories.InventoryId(row);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventoryId}: {type.Name} used ({flags.Use})");
            await SetQuestFlagsHeld(remote, stream, ciphers, conn, [flags.Use!]);
            if (flags.Grant is { } grant) await SetQuestFlagsHeld(remote, stream, ciphers, conn, [grant], on: false);
        }
        finally { conn.QuestGate.Release(); }
    }

    // ---- static rewards (QuestStaticRewards) ----

    // The passive skill points the character's quest flags give (a1q6/a1q7/a1q9 books …): part of 0x33's extra_skill_points.
    internal static int QuestPassivePoints(ClientState conn) => QuestStaticRewards.Default.Value.PassivePoints(conn.QuestFlagsSnapshot());

    // QuestStaticRewards' permanent stats as stat sources, as a passive's would be: the bandit lords' help rewards (a2q1: 8%
    // movement speed, a2q2: +15% to all elemental resistances, a2q3: +40 life). Kitava's two penalties are
    // ResistancePenalty's (Player/Player.cs), so their flags are left out here.
    internal static Dictionary<uint, int> WithQuestStats(ClientState conn, Dictionary<uint, int> sources)
    {
        var rewards = QuestStaticRewards.Default.Value.Stats(conn.QuestFlagsSnapshot().Except(ResistancePenaltyAct5).Except(ResistancePenaltyAct10));
        if (rewards.Count == 0) return sources;
        var stats = new Dictionary<uint, int>(sources);
        foreach (var (stat, value) in rewards)
            if (Stats.Key(stat) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    // Whether a flag change moves QuestStaticRewards' permanent stats (the pools and the panel follow).
    internal static bool ChangesQuestStats(IEnumerable<string> changed) =>
        changed.Any(f => !ResistancePenaltyAct5.Contains(f) && !ResistancePenaltyAct10.Contains(f) && QuestStaticRewards.Default.Value.Find(f) is { Stats.Count: > 0 });

    // Whether a flag change moves what QuestStaticRewards gives (the tree must be sent again).
    internal static bool ChangesStaticRewards(IEnumerable<string> changed) =>
        changed.Any(f => QuestStaticRewards.Default.Value.Find(f) is { PassivePoints: > 0 } or { RefundPoints: > 0 } or { AscendancyPoints: > 0 });

    // ---- kills ----

    // After a monster's kill flags (both kill paths commit those first; the QuestObjects module runs this from the kill's
    // GameEvents.Kill, after the commit): its quest drops, a fight of several bosses, a boss that forms, NPCs that appear or
    // are freed, its second form, and whether the area is now cleared.
    internal static async Task QuestKillEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m,
        MonsterCatalog.Variety variety, (int X, int Y) at)
    {
        string area = conn.Layout.Id;
        foreach (string item in QuestsOf(conn).DropsOnKill(area, variety.Id))
            await DropQuestItem(remote, stream, ciphers, conn, item, at, m.Label);
        // A fight of several bosses (Quests.KilledAllRule): its flags once none of them is left alive in the instance.
        foreach (var rule in QuestsOf(conn).KilledAllOf(area, variety.Id))
        {
            bool left;
            lock (conn.Monsters) left = conn.Monsters.Values.Any(o => o.Variety is { } v && rule.Varieties.Contains(v.Id) && !o.Dead && o.Life > 0);
            if (!left) await SetQuestFlags(remote, stream, ciphers, conn, rule.Flags);
        }
        // A boss that forms once these are all dead (Quests.FormsRule: The Depraved Trinity), at its own boss point.
        foreach (var rule in QuestsOf(conn).FormsAfter(area, variety.Id))
        {
            bool left, formed;
            (int X, int Y) where;
            lock (conn.Monsters)
            {
                left = conn.Monsters.Values.Any(o => o.Variety is { } v && rule.Varieties.Contains(v.Id) && !o.Dead && o.Life > 0);
                formed = conn.Monsters.Values.Any(o => o.Variety?.Id == rule.Next);
                where = conn.LaterFormPoints.GetValueOrDefault(rule.Next, at);
            }
            if (left || formed || Monsters.Find(rule.Next) is not { Supported: true } boss || boss.Id != rule.Next) continue;
            Console.WriteLine($"[gateway] {remote} {m.Label} died, the last of {rule.Varieties.Length}: {boss.Name} forms at ({where.X}, {where.Y})");
            await SpawnMonster(remote, stream, ciphers, conn, boss, m.Level, MonsterRarity.Unique, where.X, where.Y);
        }
        if (QuestsOf(conn).AppearOnKill(area, variety.Id).Count > 0) await QuestNpcsAppear(remote, stream, ciphers, conn, variety, at);
        if (QuestsOf(conn).SecondForm(area, variety.Id) is { } next && Monsters.Find(next) is { Supported: true } form && form.Id == next)
        {
            Console.WriteLine($"[gateway] {remote} {m.Label} died: its second form {form.Name} appears");
            await SpawnMonster(remote, stream, ciphers, conn, form, m.Level, MonsterRarity.Unique, at.X, at.Y);
        }
        // A room NPC this death frees (Quests.NpcRule.FreedBy): its last Transitionable state (UpdateQuestNpcs, QuestNpcs.cs).
        bool frees;
        lock (conn.QuestNpcs) frees = conn.KilledVarieties.Add(variety.Id) && QuestsOf(conn).Frees(area, variety.Id);
        if (frees) await UpdateQuestNpcs(remote, stream, ciphers, conn, $"{m.Label} died");
        if (QuestsOf(conn).WatchesClear(area))
        {
            bool alive;
            lock (conn.Monsters) alive = conn.Monsters.Values.Any(o => o.Hostile && !o.Dead && o.Life > 0);
            if (!alive) await SetQuestFlags(remote, stream, ciphers, conn, QuestsOf(conn).OnCleared(area, f => ClientHasQuestFlag(conn, f)));
        }
    }

    // ---- area entry ----

    // Entering an area: quest items synced (and done ones removed), the entered flags, and the quest objects of the area's
    // rooms and tiles: chests, NPCs, the engine's own quest objects (seals, levers, altars), the mirrored blockers and the
    // objects the sight rules watch.
    internal static async Task StartQuestArea(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        await SyncQuestItems(remote, stream, ciphers, conn, removeItems: true);
        await RemoveDoneQuestItemsFromGround(remote, stream, ciphers, conn);
        string area = conn.Layout.Id;
        if (QuestsOf(conn).OnEntered(area) is { Count: > 0 } entered) await SetQuestFlags(remote, stream, ciphers, conn, entered);
        var chests = QuestsOf(conn).Chests(area);
        var near = QuestsOf(conn).NearObjects(area);
        var blockers = QuestsOf(conn).Blockers(area);
        var questObjects = QuestsOf(conn).Objects(area);
        var notes = new List<string>();
        var placed = new QuestObjectTypes.AreaPlacements(area, conn.Seed, notes);
        lock (conn.QuestNpcs) conn.QuestNpcs.AddRange([.. QuestNpcCandidates(remote, conn, placed), .. AppearedBefore(conn)]);
        await UpdateQuestNpcs(remote, stream, ciphers, conn, "entering the area");
        var nearSpawns = QuestsOf(conn).NearSpawns(area);
        if (chests.Count + near.Count + blockers.Count + questObjects.Count + nearSpawns.Count == 0) return;
        lock (conn.Monsters)
        {
            conn.QuestNear = [.. near.SelectMany(r => placed.Of(r.ObjectType).Select(o => (r, o.X, o.Y)))];
            conn.QuestNearSpawns = [.. nearSpawns.SelectMany(r => placed.Of(r.ObjectType).Take(1).Select(o => (r, o.X, o.Y, false)))];
            conn.QuestBlockers = [.. blockers.SelectMany(r => placed.Of(r.ObjectType).Select(o => (r, o.X, o.Y)))];
            UpdateQuestBlockers(conn);
        }
        foreach (var n in notes.Where(n => n.StartsWith("no ", StringComparison.Ordinal))) Console.WriteLine($"[gateway] {remote} quest objects: {n}");
        if (conn.QuestBlockers.Count > 0)
            Console.WriteLine($"[gateway] {remote} quest blockers: {conn.QuestBlockers.Count} on the server's walk map until " +
                string.Join(", ", conn.QuestBlockers.Select(b => b.Rule.UnblockedBy).Distinct()));
        var scattered = ScatterQuestChests(conn, [.. chests.Where(r => r.Scattered && placed.Of(r.ChestType).Count == 0)]);
        foreach (var rule in chests)
        {
            var at = placed.Of(rule.ChestType);
            if (at.Count == 0 && scattered.TryGetValue(rule, out var spot)) at = [spot];
            if (at.Count == 0) Console.WriteLine($"[gateway] {remote} quest chest {rule.ChestType}: no room or tile places it");
            foreach (var o in at)
                await CreateQuestChest(remote, stream, ciphers, conn, rule, o with { Model = QuestChestModel(o.Type) ?? o.Model });
        }
        foreach (var rule in questObjects)
        {
            var at = placed.Of(rule.ObjectType);
            if (at.Count == 0) Console.WriteLine($"[gateway] {remote} quest object {rule.ObjectType}: no room or tile places it");
            foreach (var o in at) await CreateQuestObject(remote, stream, ciphers, conn, rule, o);
        }
    }

    // ---- the engine's quest objects (Quests.ObjectRule) ----

    // Where a quest object is transitioned: its rule's flag (the seal opened, the tree poisoned).
    internal static bool QuestObjectTransitioned(ClientState conn, Quests.ObjectRule rule) => rule.TransitionedBy is { } flag && ClientHasQuestFlag(conn, flag);

    internal static byte[] QuestObjectBlob(ClientState conn, Quests.ObjectRule rule, GlyphQuest.Placement at, QuestObjectTypes.Info info)
    {
        bool done = QuestObjectTransitioned(conn, rule);
        // A tile record's virtual model; a room line's object keeps its type's own.
        string? model = at.Z is not null ? at.Model : info.Model;
        return new QuestObject(at.X, at.Y, info.Components, at.Rotation, at.Z, model, (byte)(done ? info.States : 1), Blocked: !done).Blob();
    }

    internal static async Task CreateQuestObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Quests.ObjectRule rule, GlyphQuest.Placement at)
    {
        if (QuestObjectTypes.Find(rule.ObjectType) is not { } info || !QuestObject.CanSerialize(info.Components))
        {
            Console.WriteLine($"[gateway] {remote} quest object {rule.ObjectType}: not in data/object-types.json or components not written " +
                $"({string.Join(", ", QuestObjectTypes.Find(rule.ObjectType)?.Components ?? [])})");
            return;
        }
        uint id = conn.NextObjectId++;
        lock (conn.QuestObjects) conn.QuestObjects[id] = (rule, at, info);
        if (info.Blocking) conn.SetBlocker(id, at.X, at.Y, info.Size);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(rule.ObjectType), QuestObjectBlob(conn, rule, at, info)),
            $"0x02b9 quest object 0x{id:x} {rule.ObjectType} at ({at.X}, {at.Y}), {(QuestObjectTransitioned(conn, rule) ? "transitioned" : "untransitioned")}" +
            (rule.TransitionedBy is { } f ? $" (flag {f})" : ""));
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }

    // C→S 0x24 on a quest object: walk up; with the needs of its use rule the flags are set and the carried quest items
    // they finish are taken (QuestItems.Consume), in one save; then what the use brings (Quests.SpawnRule).
    internal static async Task UseQuestObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use,
        Quests.ObjectRule rule, GlyphQuest.Placement at, QuestObjectTypes.Info info, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, info.Size, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player uses quest object 0x{use.TargetId:x} {rule.ObjectType.Split('/')[^1]} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
            still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        try
        {
            if (!GameServer.Core.World.PlayerWalk.InReach(stop, (at.X, at.Y), info.Size, conn.Layout.Walkable))
            {
                Console.WriteLine($"[gateway] {remote} {rule.ObjectType}: out of reach from ({stop.X}, {stop.Y})");
                return;
            }
            string area = conn.Layout.Id;
            bool Has(string f) => ClientHasQuestFlag(conn, f);
            var flags = QuestsOf(conn).OnUsed(area, rule.ObjectType, Has).Where(f => !Has(f)).ToList();
            if (flags.Count == 0)
            {
                var missing = QuestsOf(conn).UseMissing(area, rule.ObjectType, Has);
                Console.WriteLine($"[gateway] {remote} {rule.ObjectType}: nothing to do" + (missing.Count > 0 ? $" (needs {string.Join(", ", missing)})" : " (done)"));
                return;
            }
            // The instance's own states (the Library's hidden door) are kept on the instance; the rest are the character's.
            var instance = flags.Where(Quests.IsInstanceFlag).Where(conn.Instance.SetState).ToList();
            if (instance.Count > 0)
            {
                Console.WriteLine($"[gateway] {remote} {rule.ObjectType.Split('/')[^1]} used: instance state {string.Join(", ", instance)} set");
                await UpdateShownTransitions(remote, stream, ciphers, conn, $"instance state {string.Join(", ", instance)}");
                await UpdateQuestObjectStates(remote, stream, ciphers, conn, instance);
            }
            if (flags.Where(f => !Quests.IsInstanceFlag(f)).ToList() is { Count: > 0 } own)
                await ConsumeQuestItemsAndSet(remote, stream, ciphers, conn, own, rule.ObjectType.Split('/')[^1]);
            byte level = (byte)Math.Clamp(Packs.For(area)?.AreaLevel ?? 1, 1, 100);
            foreach (string id in QuestsOf(conn).SpawnsOnUse(area, rule.ObjectType))
            {
                if (Monsters.Find(id) is not { Supported: true } variety || variety.Id != id)
                {
                    Console.WriteLine($"[gateway] {remote} {rule.ObjectType}: {id} cannot be created");
                    continue;
                }
                bool already;
                lock (conn.Monsters) already = conn.Monsters.Values.Any(m => m.Variety?.Id == id && !m.Dead && m.Life > 0);
                if (already) continue;
                var (x, y) = conn.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? conn.Layout.Near((at.X, at.Y)) : (at.X, at.Y);
                Console.WriteLine($"[gateway] {remote} {rule.ObjectType.Split('/')[^1]} used: {variety.Name} appears at ({x}, {y})");
                await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, x, y);
            }
        }
        finally { await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null); }
    }

    // Sets the flags and takes the carried quest items whose remove_flag is among them (and clears their grant flags), in
    // one save (QuestItems.Consume): the emblem into its seal, the spike and the gem into the tree, the Apex on the altar.
    internal static async Task ConsumeQuestItemsAndSet(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IReadOnlyList<string> flags, string what)
    {
        var items = ItemsOf(conn);
        await conn.QuestGate.WaitAsync();
        try
        {
            QuestItems.Consumed? consumed;
            lock (conn.Monsters)
            {
                if (HardcoreProgressBlocked(conn)) return;
                consumed = QuestItems.Consume(CharacterName(conn), items, i => QuestFlagsOf(conn, i), flags);
            }
            if (consumed is null) return;
            foreach (var row in consumed.Removed.GroupBy(r => r.Row))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key), [.. row.Select(r => r.Item.Id)], []),
                    $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: quest items {string.Join(", ", row.Select(r => r.Item.Id))} used on {what}");
            await SetQuestFlagsHeld(remote, stream, ciphers, conn, flags);
            if (consumed.Cleared.Count > 0) await SetQuestFlagsHeld(remote, stream, ciphers, conn, consumed.Cleared, on: false);
        }
        finally { conn.QuestGate.Release(); }
    }

    // The quest objects and chests whose state follows a flag the client now has differently: their Transitionable steps
    // (0x2ba); an object with a TriggerableBlockage (no update reader) is created again, unblocked. (The quest NPCs follow
    // their flags in UpdateQuestNpcs, QuestNpcs.cs.)
    internal static async Task UpdateQuestObjectStates(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IReadOnlyCollection<string> effective)
    {
        List<(uint Id, Quests.ObjectRule Rule, GlyphQuest.Placement At, QuestObjectTypes.Info Info)> objects;
        lock (conn.QuestObjects)
            objects = [.. conn.QuestObjects.Where(o => o.Value.Rule.TransitionedBy is { } f && effective.Contains(f)).Select(o => (o.Key, o.Value.Rule, o.Value.At, o.Value.Info))];
        foreach (var (id, rule, at, info) in objects)
        {
            bool done = QuestObjectTransitioned(conn, rule);
            if (info.Components.Contains("TriggerableBlockage"))
            {
                uint next = conn.NextObjectId++;
                lock (conn.QuestObjects) { conn.QuestObjects.Remove(id); conn.QuestObjects[next] = (rule, at, info); }
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(next, InstanceEntities.ObjectTypeHash(rule.ObjectType), QuestObjectBlob(conn, rule, at, info)),
                    $"0x02b9 quest object 0x{next:x} {rule.ObjectType.Split('/')[^1]} again (was 0x{id:x}), {(done ? "transitioned, unblocked" : "blocked")}");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(next), null);
                continue;
            }
            if (QuestObject.TransitionableIndex(info.Components) is int index and >= 0)
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)index, (byte)(done ? info.States : 1)),
                    $"0x02ba quest object 0x{id:x} {rule.ObjectType.Split('/')[^1]} Transitionable → {(done ? info.States : 1)}");
        }
        List<(uint Id, Quests.ChestRule Rule, GlyphQuest.Placement At)> chests;
        lock (conn.QuestChests) chests = [.. conn.QuestChests.Where(c => c.Value.Rule.OpensWith is { } f && effective.Contains(f)).Select(c => (c.Key, c.Value.Rule, c.Value.At))];
        foreach (var (id, rule, at) in chests)
            if (QuestObjectTypes.Find(rule.ChestType) is { } info && QuestObject.TransitionableIndex(info.Components) is int index and >= 0 && !conn.Instance.ChestOpened(at.X, at.Y))
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)index, QuestChestState(conn, rule, info, false)),
                    $"0x02ba quest chest 0x{id:x} {rule.ChestType.Split('/')[^1]} Transitionable → {QuestChestState(conn, rule, info, false)}");
    }

    // The mirrored blockers (Quests.BlockerRule) as the client has their flags: on the server's walk map until then.
    // Under the Monsters lock.
    internal static void UpdateQuestBlockers(ClientState conn)
    {
        for (int i = 0; i < conn.QuestBlockers.Count; i++)
        {
            var (rule, x, y) = conn.QuestBlockers[i];
            if (ClientHasQuestFlag(conn, rule.UnblockedBy)) conn.RemoveBlocker(QuestBlockerFirstId + (uint)i);
            else conn.SetBlocker(QuestBlockerFirstId + (uint)i, x, y, rule.Size);
        }
    }

    // ---- quest chests (Quests.ChestRule) ----

    // Chests.dat AOFiles: a chest's model by its Id.
    internal static readonly Lazy<Dictionary<string, string>> ChestModels = new(() =>
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine("data", "tables", "Chests.json");
        if (!File.Exists(path)) return models;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in doc.RootElement.EnumerateArray())
            if (r.GetProperty("Id").GetString() is { Length: > 0 } id && r.GetProperty("AOFiles").EnumerateArray().FirstOrDefault().GetString() is { Length: > 0 } model)
                models.TryAdd(id, model);
        return models;
    });

    internal static string? QuestChestModel(string type) => ChestModels.Value.GetValueOrDefault(type);

    // A quest chest's Positioned size (Chest.ot object_size 2, blocking).
    internal const int QuestChestSize = 1;

    // A quest chest's Transitionable state: its last once opened; else 2 once it may be opened (OpensWith), 1 before.
    internal static byte QuestChestState(ClientState conn, Quests.ChestRule rule, QuestObjectTypes.Info info, bool opened) =>
        (byte)(opened ? info.States : rule.OpensWith is not { } flag || ClientHasQuestFlag(conn, flag) ? Math.Min(2, info.States) : 1);

    // A quest chest's 0x2b9 blob: its type's components (QuestObjectTypes; a Chests.dat row without an .ot as its
    // InheritsFrom), else the Rhoa Nest's.
    internal static byte[] QuestChestBlob(ClientState conn, Quests.ChestRule rule, GlyphQuest.Placement at, bool opened) =>
        QuestObjectTypes.Find(rule.ChestType) is { } info && QuestObject.CanSerialize(info.Components)
            ? new QuestObject(at.X, at.Y, info.Components, at.Rotation, at.Z, at.Model, QuestChestState(conn, rule, info, opened), opened).Blob()
            : GlyphQuest.NestBlob(at, opened);

    internal static async Task CreateQuestChest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Quests.ChestRule rule, GlyphQuest.Placement at)
    {
        bool opened = conn.Instance.ChestOpened(at.X, at.Y);
        uint id = conn.NextObjectId++;
        lock (conn.QuestChests) conn.QuestChests[id] = (rule, at);
        conn.SetBlocker(id, at.X, at.Y, QuestChestSize);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(rule.ChestType), QuestChestBlob(conn, rule, at, opened)),
            $"0x02b9 quest chest 0x{id:x} {rule.ChestType} at ({at.X}, {at.Y}), opened={opened}" + (rule.OpensWith is { } f ? $", opens with {f}" : ""));
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }

    // C→S 0x24 on a quest chest: walk up, open it (once per instance; the opened chest replaces it under a new id, as
    // Chest has no update reader), its used flags, and its item while the character needs it.
    internal static async Task OpenQuestChest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use,
        Quests.ChestRule rule, GlyphQuest.Placement at, double requestedAt)
    {
        if (conn.Instance.ChestOpened(at.X, at.Y)) return;
        if (rule.OpensWith is { } needs && !ClientHasQuestFlag(conn, needs))
        {
            Console.WriteLine($"[gateway] {remote} {rule.ChestType}: cannot be opened before {needs}");
            return;
        }
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, QuestChestSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player opens quest chest 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
            still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        if (!GameServer.Core.World.PlayerWalk.InReach(stop, (at.X, at.Y), QuestChestSize, conn.Layout.Walkable) || !conn.Instance.OpenChest(at.X, at.Y))
        {
            Console.WriteLine($"[gateway] {remote} {rule.ChestType}: out of reach from ({stop.X}, {stop.Y})");
            await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
            return;
        }
        uint opened = conn.NextObjectId++;
        lock (conn.QuestChests) { conn.QuestChests.Remove(use.TargetId); conn.QuestChests[opened] = (rule, at); }
        conn.RemoveBlocker(use.TargetId);
        conn.SetBlocker(opened, at.X, at.Y, QuestChestSize);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), null);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(opened, InstanceEntities.ObjectTypeHash(rule.ChestType), QuestChestBlob(conn, rule, at, true)),
            $"0x02b9 opened quest chest 0x{opened:x} (was 0x{use.TargetId:x})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(opened), null);
        if (QuestsOf(conn).OnUsed(conn.Layout.Id, rule.ChestType, f => ClientHasQuestFlag(conn, f)) is { Count: > 0 } used) await SetQuestFlags(remote, stream, ciphers, conn, used);
        await DropQuestItem(remote, stream, ciphers, conn, rule.Item, (at.X, at.Y), rule.ChestType.Split('/')[^1]);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
    }

    // ---- dialogue ----

    // A dialogue row chosen (after its availability and hand-in checks): its scripts' flags, and an NPC whose script
    // transitions a monster variety turns into it (Quests.TurnsIntoRule: Fairgraves).
    internal static async Task OnQuestTalk(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint npcId, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (talk.ScriptSets is { Count: > 0 } sets) await SetQuestFlags(remote, stream, ciphers, conn, sets);
        if (talk.ScriptClears is { Count: > 0 } clears) await SetQuestFlags(remote, stream, ciphers, conn, clears, on: false);
        await RunTalkWorldScript(remote, stream, ciphers, conn, npcId, npc, talk);
        foreach (var rule in QuestsOf(conn).TurnsInto(conn.Layout.Id).Where(r => r.NpcType == npc.Npc.Id && talk.TransitionsVarieties.Contains(r.Variety)))
        {
            bool already;
            lock (conn.Monsters) already = conn.Monsters.Values.Any(m => m.Variety?.Id == rule.Variety && !m.Dead && m.Life > 0);
            if (already || Monsters.Find(rule.Variety) is not { Supported: true } variety || variety.Id != rule.Variety) continue;
            // The NPC's Transitionable steps to 2: its on_transition disables targeting, fades it out and hides its marker.
            int component = npc.Npc.Components!.ToList().IndexOf("Transitionable");
            if (component >= 0)
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(npcId, (byte)component, 2), $"0x02ba npc 0x{npcId:x} {npc.Npc.Name} transitions");
            lock (conn.Npcs) conn.Npcs.Remove(npcId);
            byte level = (byte)Math.Clamp(Packs.For(conn.Layout.Id)?.AreaLevel ?? 1, 1, 100);
            Console.WriteLine($"[gateway] {remote} {npc.Npc.Name} turns into {variety.Name} ({talk.Option})");
            await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, npc.X, npc.Y);
        }
    }

    // ---- the player tick ----

    // Every player tick: the sight rules for room objects and exits (Quests.NearRule, NearExitRule).
    internal static async Task CheckQuestSights(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (int X, int Y) at)
    {
        foreach (var (rule, x, y) in conn.QuestNear)
        {
            if (Dist(at, (x, y)) > Quests.SightRange || rule.Flags.All(conn.HasQuestFlag)) continue;
            if (rule.Needs is null) { await SetQuestFlags(remote, stream, ciphers, conn, rule.Flags); continue; }
            // A conditional sight (Quests.NearWith): only with its needs, and the quest items its flags finish go.
            if (!rule.Needs.All(f => ClientHasQuestFlag(conn, f))) continue;
            await ConsumeQuestItemsAndSet(remote, stream, ciphers, conn, [.. rule.Flags.Where(f => !conn.HasQuestFlag(f))],
                rule.ObjectType.Split('/')[^1]);
        }
        foreach (var rule in QuestsOf(conn).NearExits(conn.Layout.Id))
            if (!rule.Flags.All(conn.HasQuestFlag) && conn.Layout.AreaTransitions.Any(t => AreaTransitionCatalog.IdOf(t.Transition.DestinationHash16) == rule.To
                && Dist(at, (t.Transition.GridX, t.Transition.GridY)) <= Quests.SightRange))
                await SetQuestFlags(remote, stream, ciphers, conn, rule.Flags);
    }

    // The proximity spawns (Quests.NearSpawnRule): the monster appears at its object once per instance as the player comes
    // near (NearSpawnRange: a little beyond sight, as the event starts around the scene: Plaguewing's spawner is 61 cells
    // from Bannon), unless its Until flag is set.
    internal const double NearSpawnRange = Quests.SightRange * 1.5;

    internal static async Task CheckQuestNearSpawns(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (int X, int Y) at)
    {
        for (int i = 0; i < conn.QuestNearSpawns.Count; i++)
        {
            var (rule, x, y, spawned) = conn.QuestNearSpawns[i];
            if (spawned || Dist(at, (x, y)) > NearSpawnRange || rule.Until is { } until && ClientHasQuestFlag(conn, until)) continue;
            conn.QuestNearSpawns[i] = (rule, x, y, true);
            if (Monsters.Find(rule.Variety) is not { Supported: true } variety || variety.Id != rule.Variety)
            {
                Console.WriteLine($"[gateway] {remote} {rule.ObjectType}: {rule.Variety} cannot be created");
                continue;
            }
            byte level = (byte)Math.Clamp(Packs.For(conn.Layout.Id)?.AreaLevel ?? 1, 1, 100);
            var (sx, sy) = conn.Layout.Walkable is { } walks && !walks(x, y) ? conn.Layout.Near((x, y)) : (x, y);
            Console.WriteLine($"[gateway] {remote} near {rule.ObjectType.Split('/')[^1]}: {variety.Name} appears at ({sx}, {sy})");
            await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, sx, sy);
        }
    }

    // Places for the area's scattered quest chests (Quests.ChestRule.Scattered: no room or tile has them): walkable cells of
    // a coarse grid, away from the arrival, each as far from the others as can be (farthest-point picking from a first cell
    // the seed chooses), so the same seed gives the same cells.
    internal static Dictionary<Quests.ChestRule, GlyphQuest.Placement> ScatterQuestChests(ClientState conn, IReadOnlyList<Quests.ChestRule> rules)
    {
        var result = new Dictionary<Quests.ChestRule, GlyphQuest.Placement>();
        var layout = conn.Layout;
        if (rules.Count == 0 || layout.Walkable is not { } walkable || layout.GridW <= 0 || layout.GridH <= 0) return result;
        var cells = ScatterCells(walkable, layout.GridW, layout.GridH, (layout.SpawnX, layout.SpawnY), rules.Count, HashCode.Combine(layout.Hash, conn.Seed, 0x7f1a));
        for (int i = 0; i < cells.Count; i++)
            result[rules[i]] = new GlyphQuest.Placement(rules[i].ChestType, QuestChestModel(rules[i].ChestType) ?? "", cells[i].X, cells[i].Y, 0, null, 0);
        return result;
    }

    internal static List<(int X, int Y)> ScatterCells(Func<int, int, bool> walkable, int width, int height, (int X, int Y) arrival, int count, int seed)
    {
        const int Step = 12, Margin = 40;
        var cells = new List<(int X, int Y)>();
        for (int y = Step; y < height - Step; y += Step)
            for (int x = Step; x < width - Step; x += Step)
                if (walkable(x, y) && walkable(x + 2, y) && walkable(x - 2, y) && walkable(x, y + 2) && walkable(x, y - 2)
                    && Dist((x, y), arrival) >= Margin)
                    cells.Add((x, y));
        if (cells.Count == 0 || count <= 0) return [];
        var rng = new Random(seed);
        var chosen = new List<(int X, int Y)> { cells[rng.Next(cells.Count)] };
        while (chosen.Count < count) chosen.Add(cells.MaxBy(c => chosen.Min(p => Dist(c, p))));
        return chosen;
    }

    // ---- thresholds (Quests.ThresholdRule) ----

    // Every player tick in an area with a quest threshold: a monster any hit took to its threshold (a projectile, Rampage, a
    // monster mod's damage: every path that takes life marks it, TakeDamageLocked) leaves the fight here, wherever the hit
    // came from; melee and spell hits also call QuestThreshold at once.
    internal static async Task CheckQuestThresholds(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!QuestsOf(conn).HasThresholds(conn.Layout.Id)) return;
        List<(uint Id, SpawnedMonster Monster)> reached;
        lock (conn.Monsters) reached = [.. conn.Monsters.Where(m => m.Value.ThresholdReached && !m.Value.Dead).Select(m => (m.Key, m.Value))];
        foreach (var (id, m) in reached) await QuestThreshold(remote, stream, ciphers, conn, id, m);
    }

    // A monster that never dies reached its quest threshold (Kitava destroys the Sign of Purity): its flags, once, and it
    // leaves the fight (removed from the instance, no death, no drops, no experience).
    internal static async Task QuestThreshold(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        Quests.ThresholdRule? rule;
        lock (conn.Monsters)
        {
            if (!m.ThresholdReached || m.Dead || !ReferenceEquals(conn.Monsters.GetValueOrDefault(id), m)) return;
            rule = m.Threshold;
            m.Dead = true;
            m.Aggro = false;
            conn.Monsters.Remove(id);
        }
        if (rule is null) return;
        Console.WriteLine($"[gateway] {remote} {m.Label} 0x{id:x} reached {rule.LifePct}% life: {string.Join(", ", rule.Flags)}; it leaves the fight");
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb monster 0x{id:x} {m.Label} leaves (quest threshold)");
        await SetQuestFlags(remote, stream, ciphers, conn, rule.Flags);
    }
}
