using System.Net.Sockets;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;

static partial class CommandsRules
{
    // The campaign driver's dev commands (Agents/docs/dev/campaign-automation.md, CampaignState.cs): /state for what the
    // driver sees, /kill for the bosses on the normal death path, /quest_preset to start a quick check at a later act.
    internal static ChatCommands AddCampaignCommands(ChatCommands commands, string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        commands
        .Add("state", "/state [brief | all] (dev: one STATE {json} reply for the campaign driver: area, character, quest flags, waypoints and this area's monsters, NPCs, objects, ground items and exits; long replies go on in STATE+ lines; brief leaves out the area's things, all lists every monster)", async args =>
        {
            bool brief = args.Any(a => a.Equals("brief", StringComparison.OrdinalIgnoreCase));
            bool all = args.Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase));
            foreach (string line in CampaignState.Lines(StateJson(conn, brief, all).ToJsonString()))
                await conn.Commands!.Reply(line);
        })
        .Add("kill", "/kill <id> | <name> [n] | boss | near [radius] | all (dev: kill monsters here through the normal death path: experience, drops, quest progress, boss stages skipped)", async args =>
        {
            List<(uint Id, SpawnedMonster Monster)> targets;
            string? error;
            lock (conn.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                targets = CampaignState.KillTargets(conn.Monsters, conn.Walk.Position(now), now, args, out error);
            }
            if (error is not null) { await conn.Commands!.Reply(error); return; }
            var killed = new List<string>();
            foreach (var (id, m) in targets)
                if (await KillMonster(remote, stream, ciphers, conn, id, m)) killed.Add($"{m.Label} 0x{id:x}");
            await conn.Commands!.Reply(killed.Count == 0 ? "Nothing killed." : $"Killed {killed.Count}: {string.Join(", ", killed)}");
        }, "monster")
        .Add("quest_preset", "/quest_preset <CharacterStartQuestState Id> (dev: add that preset's quest flags and the earlier acts', e.g. /quest_preset Act1Complete)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("a CharacterStartQuestState Id, e.g. Act1Complete");
            if (CampaignState.PresetFlags(args[0], out string? error) is not { } flags) { await conn.Commands!.Reply(error!); return; }
            int before = conn.QuestFlagsSnapshot().Count;
            await SetQuestFlags(remote, stream, ciphers, conn, flags);
            int added = conn.QuestFlagsSnapshot().Count - before;
            int waypoints = await UnlockPresetWaypoints(remote, stream, ciphers, conn, args[0]);
            var held = await GivePresetQuestItems(remote, stream, ciphers, conn, flags);
            await conn.Commands!.Reply($"Preset {args[0]}: {added} of {flags.Count} quest flag(s) added, {waypoints} waypoint(s) unlocked" +
                (held.Count > 0 ? $", quest item(s) held: {string.Join(", ", held)}." : "."));
        });

    // A character that finished an act knows its waypoints; a preset stands in for having played it: the waypoints of every
    // campaign area of the acts the preset's quests belong to (and the earlier ones). Their areas' gates need no check: the
    // presets list story flags only (Act1Complete has no a1q1-KilledHillock), and an activated waypoint brings its area's
    // access flags with it (16-waypoints.md §4). Without them a preset character starts on The Twilight Strand with no way to
    // travel but Act 1's gated exits.
    static async Task<int> UnlockPresetWaypoints(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string presetId)
    {
        var states = QuestStates.Default.Value;
        var preset = states.FindPreset(presetId)
            ?? states.Presets.FirstOrDefault(p => p.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase));
        int act = preset is null ? 0 : Enumerable.Range(1, 11).Where(a => states.QuestsOfAct(a).Intersect(preset.Quests).Any()).DefaultIfEmpty(0).Max();
        // The acts it completes, and the preset's own waypoints (CharacterStartQuestState Areas: where a demo start begins,
        // E3StartOfAct7's The Brine King's Reef, which lists no quests).
        var unlock = PresetWaypointAreas.Value.Where(w => w.Act <= act).Select(w => w.Id).Concat(CampaignState.PresetWaypoints(presetId))
            .Distinct().Where(id => WaypointCatalog.Areas.Entries.Any(e => e.Id == id)).ToList();
        List<string> added;
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn)) return 0;
            lock (conn.Waypoints) added = [.. unlock.Where(conn.Waypoints.Add)];
            if (added.Count > 0) CharacterSaves.AddWaypoints(CharacterName(conn), added);
        }
        if (added.Count > 0) await SendWaypointList(remote, stream, ciphers, conn, $"/quest_preset {presetId}: {added.Count} waypoint(s)");
        return added.Count;
    }

    // The campaign areas with a waypoint (WorldAreas: Id 1_<act>_… or 2_<act>_…, HasWaypoint) and their act.
    static readonly Lazy<List<(string Id, int Act)>> PresetWaypointAreas = new(() =>
    {
        var list = new List<(string, int)>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("data/tables/WorldAreas.json"));
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^[12]_\d+_") && r.GetProperty("HasWaypoint").GetBoolean())
                    list.Add((id, r.GetProperty("Act").GetInt32()));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { }
        return list;
    });

    // The quest items a preset's character holds: those whose grant_flag the preset sets and whose remove_flag it does not
    // (E3StartOfAct7 has a6q4-HaveRewardBook without a6q4-UsedRewardBook: Lilly's Book of Regrets, not used yet). Given into
    // the main inventory unless one is carried already.
    static async Task<List<string>> GivePresetQuestItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IReadOnlyCollection<string> presetFlags)
    {
        // A Hardcore character whose death is being recorded gains no items (as a ground-item pickup).
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return [];
        var catalog = ItemsFor(conn);
        var items = ItemsOf(conn);
        var carried = QuestItems.Carried(items).Select(c => catalog.ByHash(c.Item.Item.TypeHash)?.Id).OfType<string>().ToHashSet();
        var given = new List<string>();
        var added = new List<InventoryItem>();
        foreach (var type in catalog.QuestItemTypes)
        {
            var q = type.Quest!;
            if (q.Grant is not { } grant || !presetFlags.Contains(grant) || q.Remove is { } remove && presetFlags.Contains(remove)
                || carried.Contains(type.Id) || !type.Supported) continue;
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) break;
            added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type))));
            given.Add(type.Name);
        }
        if (added.Count > 0)
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: preset quest items {string.Join(", ", given)}");
        return given;
    }

    // A lethal hit of exactly the monster's life, through the same path a skill hit takes (ApplyMonsterHitLocked,
    // NotifyMonsterHit → MonsterKilled): the death animation, experience within range, drops, quest flags and quest item
    // drops. A boss's stage (Hillock's sword) is skipped: it would leave the boss alive at full life.
    static async Task<bool> KillMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        MonsterHit? hit;
        int damage;
        lock (conn.Monsters)
        {
            if (!ReferenceEquals(conn.Monsters.GetValueOrDefault(id), m) || m.Dead || m.Life == 0) return false;
            m.StageTaken = true;
            m.StageRunning = false;
            damage = (int)Math.Min(int.MaxValue, m.Life);
            hit = ApplyMonsterHitLocked(conn, m, damage);
        }
        if (hit is not { } h) return false;
        await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, h, damage, "/kill");
        return h.Life == 0 || m.ThresholdReached;
    }

    // /god [on | off | immune]: the toggle as before; immune also takes no damage (hits and ailments). Both are kept per
    // character for the Gateway's lifetime (GodCharacters, ImmuneCharacters), so area changes and relogs keep them.
    internal static async Task GodCommand(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        bool on = mode switch
        {
            "" => !conn.God, "on" or "immune" => true, "off" => false,
            _ => throw new ArgumentException("on, off or immune"),
        };
        bool immune = on && (mode == "immune" || (mode == "on" && conn.Immune));
        lock (conn.Monsters) conn.Immune = immune;
        if (immune) ImmuneCharacters[CharacterName(conn)] = true; else ImmuneCharacters.TryRemove(CharacterName(conn), out _);
        await SetGod(remote, stream, ciphers, conn, on);
        // A burning Righteous Fire takes its radius when lit: light it again with the new area.
        await RelightRighteousFire(remote, stream, ciphers, conn, "/god");
        await conn.Commands!.Reply(on ? immune ? "God mode on, immune to damage." : "God mode on." : "God mode off.");
    }

    // /state's JSON (the schema is in Agents/docs/dev/campaign-automation.md).
    internal static JsonObject StateJson(ClientState conn, bool brief, bool allMonsters = false)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        var player = conn.Walk.Position(now);
        static JsonArray Cell(int x, int y) => [x, y];
        // The flags as the client has them: the character's own and the World panel's visit and access flags it is sent.
        var flags = conn.QuestFlagsSnapshot();
        flags.UnionWith(Quests.Names(PlayerQuestFlags(conn)));
        var state = new JsonObject
        {
            ["v"] = 1,
            ["area"] = conn.Layout.Id,
            ["areaName"] = AreaTransitionCatalog.NameOf(conn.Layout.Hash),
            ["town"] = InTown(conn),
            ["seed"] = conn.Seed,
            ["instance"] = conn.Ticket,
            ["character"] = CharacterName(conn),
            ["classRow"] = conn.ClassRow,
        };
        lock (conn.Monsters)
        {
            state["level"] = conn.Pools.Level;
            state["xp"] = conn.Experience;
            state["life"] = conn.PlayerLife;
            state["maxLife"] = conn.PlayerMaxLife;
            state["dead"] = conn.PlayerLife == 0;
            state["god"] = conn.God;
            state["immune"] = conn.Immune;
            state["pos"] = Cell(player.X, player.Y);
            state["passives"] = StatePassives(conn);
        }
        // The flags every character is given are left out until earned (EndGameAvailable after Kitava).
        HashSet<string> unearned;
        lock (conn.Monsters) unearned = [.. conn.UnearnedQuestFlags];
        state["flags"] = new JsonArray([.. flags.Where(f => !unearned.Contains(f)).Order(StringComparer.Ordinal).Select(f => JsonValue.Create(f))]);
        lock (conn.Waypoints) state["waypoints"] = new JsonArray([.. conn.Waypoints.Order(StringComparer.Ordinal).Select(w => JsonValue.Create(w))]);
        state["resistancePenalty"] = ResistancePenalty(conn);
        // What the league's other modules add (the Pantheon module's "pantheon").
        GameEvents.RunStateBuilding(conn, state);
        // The area's Stash (towns, hideouts): the driver puts what the inventory cannot hold into it (C→S 0xe0).
        state["stash"] = conn.StashObject != 0 ? new JsonObject { ["id"] = conn.StashObject, ["pos"] = Cell(conn.StashAt.X, conn.StashAt.Y) } : null;
        state["talking"] = conn.TalkingTo;
        state["inTalk"] = conn.InNpcTalk;
        state["reward"] = conn.NpcRewardOffer is int offer
            ? new JsonObject { ["offer"] = offer, ["items"] = new JsonArray([.. conn.NpcRewardItems.Select(kv => StateItem(conn, kv.Key, kv.Value))]) }
            : null;
        if (brief) return state;

        var monsters = new JsonArray();
        lock (conn.Monsters)
        {
            var listed = conn.Monsters.Select(kv => (kv.Key, M: kv.Value, At: kv.Value.Walk.Position(now)))
                .OrderBy(e => Dist(e.At, player)).ToList();
            // Every boss and living monster near the player; the rest only with /state all (an area has hundreds).
            foreach (var (id, m, at) in listed.Where(e => allMonsters || CampaignState.IsBoss(e.M)
                || (!e.M.Dead && e.M.Life > 0 && Dist(e.At, player) <= StateMonsterRadius)).Take(allMonsters ? int.MaxValue : StateMonsterLimit))
                monsters.Add(new JsonObject
                {
                    ["id"] = id, ["variety"] = m.Variety?.Id, ["name"] = m.Label, ["rarity"] = m.Rarity.ToString(),
                    ["alive"] = !m.Dead && m.Life > 0, ["life"] = m.Life, ["hostile"] = m.Hostile, ["pos"] = Cell(at.X, at.Y),
                    ["boss"] = CampaignState.IsBoss(m),
                });
        }
        state["monsters"] = monsters;

        var clientFlags = Quests.Names(PlayerQuestFlags(conn));
        var npcs = new JsonArray();
        lock (conn.Npcs)
            foreach (var (id, n) in conn.Npcs)
                npcs.Add(new JsonObject
                {
                    ["id"] = id, ["npc"] = n.Npc.Id, ["name"] = n.Npc.Name, ["pos"] = Cell(n.X, n.Y),
                    ["options"] = new JsonArray([.. Npcs.Value.TalksOf(n.Npc).Where(t => t.Available(clientFlags)).OrderBy(t => t.Index)
                        .Select(t => new JsonObject { ["index"] = t.Index, ["row"] = t.Row, ["text"] = t.Option, ["category"] = t.Category, ["reward"] = t.RewardOffer,
                            // NPCTalk QuestFlag3 all set: the row was heard (of two rows with one text, the other is the quest's next).
                            ["heard"] = t.HeardFlags.Count > 0 && t.HeardFlags.All(clientFlags.Contains) })]),
                });
        state["npcs"] = npcs;
        state["objects"] = new JsonArray([.. StateObjects(conn).Select(o => new JsonObject
        {
            ["id"] = o.Id, ["type"] = o.Type, ["name"] = o.Type[(o.Type.LastIndexOf('/') + 1)..], ["pos"] = Cell(o.X, o.Y), ["used"] = o.Used,
        })]);
        // (No instance before the connection entered an area.)
        state["items"] = new JsonArray([.. (conn.Instance?.Ground.All() ?? []).Select(g =>
        {
            var item = StateItem(conn, g.Id, g.Item.Item);
            item["pos"] = Cell(g.Item.GridX, g.Item.GridY);
            return item;
        })]);
        state["transitions"] = new JsonArray([.. conn.Layout.AreaTransitions.Where(t => TransitionShown(conn, t.Id, t.Transition)).Select(t => new JsonObject
        {
            ["id"] = t.Id, ["to"] = AreaTransitionCatalog.IdOf(t.Transition.DestinationHash16) ?? $"0x{t.Transition.DestinationHash16:x4}",
            ["name"] = AreaTransitionCatalog.NameOf(t.Transition.DestinationHash16), ["pos"] = Cell(t.Transition.GridX, t.Transition.GridY),
        })]);
        bool active;
        lock (conn.Waypoints) active = conn.Waypoints.Contains(conn.Layout.Id);
        state["waypoint"] = conn.Layout.Waypoint is { } w
            ? new JsonObject { ["id"] = WaypointCatalog.ObjectId, ["pos"] = Cell(w.GridX, w.GridY), ["active"] = active } : null;
        var inventory = ItemsOf(conn);
        state["inventory"] = new JsonObject
        {
            ["w"] = 12, ["h"] = 5,
            ["items"] = new JsonArray([.. inventory.Items(PlayerInventories.MainInventory).Select(i =>
            {
                var item = StateItem(conn, i.Id, i.Item);
                item["x"] = i.X;
                item["y"] = i.Y;
                return item;
            })]),
        };
        state["equipped"] = new JsonObject
        {
            ["weapon"] = inventory.Items(PlayerInventories.Weapon1).Select(i => StateItem(conn, i.Id, i.Item)).FirstOrDefault(),
        };
        return state;
    }

    // /state lists the living monsters within this many cells (and every boss), at most StateMonsterLimit of them.
    const double StateMonsterRadius = 120;
    const int StateMonsterLimit = 60;

    static JsonObject StateItem(ClientState conn, uint id, Item item)
    {
        var catalog = ItemsFor(conn);
        var type = catalog.ByHash(item.TypeHash);
        var shape = ShapeOf(item);
        return new JsonObject
        {
            ["id"] = id, ["base"] = type?.Id, ["name"] = type is null ? null : catalog.Gem(type) is not null ? catalog.GemName(item) : type.Name,
            ["class"] = type?.Class, ["w"] = shape?.Width ?? 1, ["h"] = shape?.Height ?? 1,
            ["quest"] = type?.Class == "QuestItem",
        };
    }

    // Unspent points and the ordinary passives one more point can take (connected, no keystone, mastery, socket or
    // cluster node): the driver's simple allocation takes the first. Under the Monsters lock.
    static JsonObject StatePassives(ClientState conn)
    {
        var tree = PassivesFor(conn);
        // The same budget the allocation checks (0x33's extra_skill_points): the quest books' points too.
        int quest = QuestPassivePoints(conn);
        int budget = Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints + quest + tree.PointsGranted(conn.Passives) + AscendancyOrdinaryPoints(conn);
        int unspent = Math.Max(0, budget - conn.Passives.Count - conn.ClusterPassives.Count);
        var candidates = OrdinaryPassives.Frontier((byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId,
                conn.MainAscendancy.Selection?.Family ?? AscendancyFamily.Normal, conn.Passives)
            .Where(n => tree.Find(n) is { IsKeystone: false, IsJewelSocket: false, IsProxy: false, IsExpansion: false, IsMastery: false, IsAscendancy: false })
            .OrderBy(n => tree.Find(n)!.IsNotable).ThenBy(n => n).Take(16);
        return new JsonObject
        {
            ["unspent"] = unspent,
            ["quest"] = quest,
            ["refund"] = (int)conn.RefundPoints,
            ["allocated"] = new JsonArray([.. conn.Passives.Select(n => JsonValue.Create(n))]),
            ["candidates"] = new JsonArray([.. candidates.Select(n => JsonValue.Create(n))]),
        };
    }

    /// <summary>An object of the area the driver can use: its id, object type, cell, and whether it was used.</summary>
    internal sealed record StateObject(uint Id, string Type, int X, int Y, bool Used);

    // The interactive server objects of the area: the opening's support chest, a1q4's Rhoa Nests and Glyph Wall, the quest
    // engine's chests and objects (used: opened; a quest object once its flags are set). /tp finds them by these ids too.
    internal static List<StateObject> StateObjects(ClientState conn)
    {
        var list = new List<StateObject>();
        if (conn.TutorialChestId != 0 && conn.TutorialChest is { } chest)
            list.Add(new(conn.TutorialChestId, chest.Type, chest.X, chest.Y, CharacterSaves.Load(CharacterName(conn))?.Tutorial?.ChestOpened == true));
        lock (conn.GlyphNests)
            foreach (var (id, nest) in conn.GlyphNests)
                list.Add(new(id, nest.Type, nest.At.X, nest.At.Y, conn.Instance.ChestOpened(nest.At.X, nest.At.Y)));
        if (conn.GlyphWallId != 0 && conn.Glyphs?.Wall is { } wall)
            list.Add(new(conn.GlyphWallId, GlyphQuest.Wall, wall.X, wall.Y, ClientHasQuestFlag(conn, GlyphQuest.PlacedGlyphs)));
        lock (conn.QuestChests)
            foreach (var (id, (rule, at)) in conn.QuestChests)
                list.Add(new(id, rule.ChestType, at.X, at.Y, conn.Instance.ChestOpened(at.X, at.Y)));
        bool Has(string flag) => ClientHasQuestFlag(conn, flag);
        lock (conn.QuestObjects)
            foreach (var (id, o) in conn.QuestObjects)
            {
                var flags = QuestsOf(conn).OnUsed(conn.Layout.Id, o.Rule.ObjectType, _ => true);
                bool used = o.Rule.TransitionedBy is { } f ? Has(f) : flags.Count > 0 && flags.All(Has);
                list.Add(new(id, o.Rule.ObjectType, o.At.X, o.At.Y, used));
            }
        return list;
    }
}
