using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Creates one NPC (0x2b9, its NPCs.dat Id is an .ot type) and puts it in the wake zone; kept for clicks and /npcs.
    internal static async Task<uint> SpawnNpc(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcCatalog.Npc npc,
        int x, int y, float rotation)
    {
        uint id = conn.NextObjectId++;
        var obj = new NpcObject(x, y, npc.Components!, rotation, npc.StateMachine, npc.TransitionState, npc.Model, npc.Height);
        uint type = InstanceEntities.ObjectTypeHash(npc.Id);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob()),
            $"0x02b9 create npc 0x{id:x} {npc} type=0x{type:x8} at ({x}, {y}) rotation {rotation:0.##}, components {string.Join(", ", npc.Components!)}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 npc 0x{id:x} enters wake zone");
        lock (conn.Npcs) conn.Npcs[id] = new SpawnedNpc(npc, x, y);
        await RegisterStationaryCollision(remote, stream, ciphers, conn, id, npc.Id, x, y, rotation,
            HideoutCatalog.ByArea(conn.Layout.Hash) is not null ? "npc:" + npc.Id : null, npc.Model);
        return id;
    }

    // The town's NPCs (NpcCatalog.In) at their cells.
    internal static async Task SpawnTownNpcs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var notes = new List<string>();
        var placements = HideoutFurniture(conn)?.Npcs ?? NpcCatalog.In(conn.Layout.Hash, conn.Seed, notes);
        foreach (var note in notes) Console.WriteLine($"[gateway] {remote} npcs: {note}");
        if (placements.Count == 0 || Environment.GetEnvironmentVariable("Game_NPCS") == "0") return;
        if (Npcs.Value.Error is { } error) { Console.WriteLine($"[gateway] {remote} npcs: {error}"); return; }
        if (StashCatalog.FromArrival(conn.Layout.Hash))
            placements = [.. placements.Select(p => p with { X = conn.Layout.SpawnX + p.X, Y = conn.Layout.SpawnY + p.Y })];
        var quests = QuestsOf(conn);
        foreach (var p in placements)
        {
            // A town NPC a quest stages (DiallaTown / DiallaTownWithered …) comes with the quest NPCs (World/Quest/QuestNpcs.cs).
            if (quests.ManagesNpc(conn.Layout.Id, p.Id)) continue;
            if (Npcs.Value.Place(p) is not { Supported: true } npc) { Console.WriteLine($"[gateway] {remote} npc {p.Id}: unknown or unsupported components"); continue; }
            if (!p.FromRoom && conn.Layout.Walkable is { } w && !w(p.X, p.Y)) Console.WriteLine($"[gateway] {remote} npc {npc.Name}: ({p.X}, {p.Y}) is not walkable");
            await SpawnNpc(remote, stream, ciphers, conn, npc, p.X, p.Y, p.Rotation);
        }
    }

    // A click on an NPC (C→S 0x24 on its object): the client walks to it first; follow it and answer once the player is there.
    internal static async Task TalkToNpc(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedNpc npc, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, npc.X, npc.Y, Math.Max(0, StationaryCollision.Resolve(npc.Npc.Id, npc.Npc.Model).Size), out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 npc {npc.Npc} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        if (LeagueFor(conn).Rules.RefillsVitalsAt(npc.Npc.Id))
        {
            lock (conn.Monsters)
            {
                if (conn.PlayerLife == 0 || conn.HardcoreDeathRecorded) return;
                conn.PlayerLife = conn.PlayerMaxLife;
                conn.PlayerMana = (uint)UnreservedMana(conn);
            }
            await GainFlaskCharges(remote, stream, ciphers, conn, int.MaxValue, "vendor refill");
            PersistVitals(conn);
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife), "vendor restores life");
            await SendPlayerMana(remote, stream, ciphers, conn, "vendor restores mana");
        }
        uint id;
        lock (conn.Npcs) id = conn.Npcs.FirstOrDefault(n => ReferenceEquals(n.Value, npc)).Key;
        conn.TalkingTo = id;
        conn.InNpcTalk = false;
        conn.NpcFinishFlag = null;
        conn.NpcRewardOffer = null;
        if (await GameEvents.AnyNpcTalk(conn, id, npc)) return;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(id), $"0x0084 talk to npc 0x{id:x} {npc.Npc.Name}",
            still: () => PlayerActionCurrent(conn, action));
    }

    // C→S 0x85: the dialogue window was closed or, while a talk plays, Continue was pressed. The client stays in its dialogue
    // state (and the player cannot move) until the server answers: back to the option list (0x84 0xff) after a talk, else
    // 0x84 with object 0, which ends the dialogue.
    internal static async Task CloseNpcDialogue(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        await GameEvents.RunNpcDialogueClosing(conn);
        uint id = conn.TalkingTo;
        if (id != 0 && conn.InNpcTalk && !IsWoundedExile())
        {
            conn.InNpcTalk = false;
            if (conn.NpcFinishFlag is { } finish)
            {
                conn.NpcFinishFlag = null;
                await SetQuestFlags(remote, stream, ciphers, conn, [finish]);
            }
            // The row's script decides what follows its talk: the next page, or the end of the dialogue.
            if (conn.NpcTalkScript is { } script)
            {
                conn.NpcTalkScript = null;
                SpawnedNpc? speaker;
                lock (conn.Npcs) speaker = conn.Npcs.GetValueOrDefault(id);
                if (speaker is not null && await EndTalkScript(remote, stream, ciphers, conn, id, speaker, script)) return;
            }
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(id), $"0x0084 npc 0x{id:x}: back to the options (0x85 after a talk)");
            return;
        }
        conn.TalkingTo = 0;
        conn.InNpcTalk = false;
        conn.NpcFinishFlag = null;
        conn.NpcTalkScript = null;
        conn.NpcRewardOffer = null;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (0x85)");

        bool IsWoundedExile()
        {
            lock (conn.Npcs) return conn.Npcs.GetValueOrDefault(id)?.Npc.Id == TutorialObjects.Exile;
        }
    }

    // C→S 0x82: an option picked in the NPC dialogue window (an NPCTalk row of the NPC talked to).
    internal static async Task ChooseNpcOption(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte index)
    {
        uint id = conn.TalkingTo;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(id);
        if (npc is null) { Console.WriteLine($"[gateway] {remote} <= 0x0082 npc option {index}: no NPC dialogue open"); return; }
        if (npc.Npc.Id == TutorialObjects.Exile) return;
        var talk = Npcs.Value.TalkOf(npc.Npc, index);
        Console.WriteLine($"[gateway] {remote} <= 0x0082 npc {npc.Npc.Name} option {index}: " +
            (talk is null ? "no NPCTalk row" : $"NPCTalk row {talk.Row} \"{talk.Option}\" ({talk.Category}), text audio [{string.Join(", ", talk.TextAudio)}]"));
        if (talk is null) return;
        if (!talk.Available(Quests.Names(PlayerQuestFlags(conn)))) return;
        if (!await GameEvents.AllowNpcOption(conn, talk)) return;
        // A row that sets a quest item's remove_flag takes the item (Nessa's Medicine Chest, Fairgraves' Allflame).
        if (!await HandInQuestItems(remote, stream, ciphers, conn, talk, npc.Npc.Id)) return;
        if (TalkStartFlag(conn, talk) is { } start) await SetQuestFlags(remote, stream, ciphers, conn, [start]);
        if (QuestsOf(conn).OnTalkRow(talk.Row) is { Count: > 0 } byRow) await SetQuestFlags(remote, stream, ciphers, conn, byRow);
        await OnQuestTalk(remote, stream, ciphers, conn, id, npc, talk);
        conn.NpcFinishFlag = talk.FinishFlag;
        conn.NpcTalkScript = null;
        // A scripted row's end (FinishNPCChat, DoNPCChat, a screen, a move): after its talk, or now when it has none.
        if (EndsWithScript(talk) && talk.Category is "Gossip" or "Quests" or "QuestReward" && talk.RewardOffer is null)
        {
            if (talk.TextAudio.Count > 0) conn.NpcTalkScript = talk;
            else
            {
                if (conn.NpcFinishFlag is { } finish) { conn.NpcFinishFlag = null; await SetQuestFlags(remote, stream, ciphers, conn, [finish]); }
                if (await EndTalkScript(remote, stream, ciphers, conn, id, npc, talk)) return;
            }
        }
        // What an option does is a module's (Modules/Core/npcdialogue); one nothing answers does nothing.
        if (!await Program.ServerHooks.RunNpcOption(new(PlayerOf(conn), npc.Npc.Name, talk.Category, talk.Option, talk.Script, talk.TextAudio.Count > 0,
                talk.Index, talk.Row, talk.OpensGuildHideouts, talk.RewardOffer)))
            Console.WriteLine($"[gateway] {remote} npc option category {talk.Category}: no module answers it");
    }

    // A quest's reward window (0xc0 and 0x87). False when the offer has nothing for this character, or was taken.
    internal static async Task<bool> OfferNpcReward(ClientState conn, int offer)
    {
        var (remote, stream, ciphers) = conn.Wire;
        var rewardCatalog = QuestRewardCatalog.Load(hardMode: LeagueFor(conn).HardMode, supportGems: LeagueFor(conn).Rules.SupportGemRewards);
        var rewardItems = rewardCatalog.Choices(offer, conn.ClassRow)
            .Select(c => (Choice: c, Item: QuestRewardCatalog.Create(c, ItemsFor(conn), RollsFor(conn))))
            .Where(c => c.Item is not null).ToArray();
        if (rewardCatalog.Find(offer) is not { } rewardOffer) return false;
        // An offer none of whose choices can be built yet (unique gear, jewels) still counts as taken, so its quest completes
        // (campaign-quests.md E10): its taken flag is set without an item, and the options come back.
        if (rewardItems.Length == 0)
        {
            if (rewardOffer.TakenFlag is not { } taken || ClientHasQuestFlag(conn, taken)) return false;
            Console.WriteLine($"[gateway] {remote} offer {offer}: no choice can be built yet; {taken} set without an item");
            await SetQuestFlags(remote, stream, ciphers, conn, [taken]);
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(conn.TalkingTo), $"0x0084 npc 0x{conn.TalkingTo:x}: options again (offer {offer} taken without an item)");
            return true;
        }
        if (QuestRewardCatalog.Taken(rewardOffer.TakenFlag, ItemsFor(conn).ByHash(rewardItems[0].Item!.TypeHash)?.Quest,
                f => ClientHasQuestFlag(conn, f))) return false;
        conn.InNpcTalk = false;
        conn.NpcRewardOffer = offer;
        conn.NpcRewardItems = rewardItems.ToDictionary(c => c.Choice.Id, c => c.Item!);
        await Send(remote, stream, ciphers, InstanceInventories.AddInventory(0x600, NpcShops.Tab, NpcShops.Manager,
            QuestRewardCatalog.Arrange(rewardItems.Select(c => (c.Choice.Id, c.Item!)), ItemsFor(conn))),
            $"0x00c0 quest reward inventory: offer {offer}, {rewardItems.Length} choices");
        await Send(remote, stream, ciphers, InstanceNpcs.OpenQuestReward(0x600, checked((ushort)offer)),
            $"0x0087 reward inventory 0x600: quest reward offer {offer}");
        return true;
    }

    // An NPC's Identify Items: the main inventory's unidentified items become identified (their explicit mods and rare names
    // are sent from now on). Removing and adding the same ids in one 0xbf replaces them on the client (see UseItemOn).
    internal static async Task IdentifyInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedNpc npc)
    {
        const byte row = PlayerInventories.MainInventory;
        var items = ItemsOf(conn);
        var identified = items.IdentifyAll(row);
        if (identified.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} npc {npc.Npc.Name} identify items: nothing unidentified");
            return;
        }
        uint inventory = PlayerInventories.InventoryId(row);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [.. identified.Select(i => i.Id)], identified),
            $"0x00bf inventory {inventory}: {npc.Npc.Name} identified {identified.Count} item(s)");
        foreach (var i in identified)
            Console.WriteLine($"[gateway] {remote} npc {npc.Npc.Name} identified item {i.Id}: {string.Join("; ", i.Item.Mods.Select(Items.Mods.Describe))}");
        // ClientStrings IdentifyItemsChatMessage / IdentifyItemsChatMessageSingle, as a chat line (S→C 0x17).
        string text = identified.Count == 1 ? "1 Item identified" : $"{identified.Count} Items identified";
        await Send(remote, stream, ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        // The client decides itself whether the option is usable (NPCTalkCategory IdentifyItems, 0x141e754b0: gray with
        // "No items in your inventory can be identified" when nothing is unidentified), but only when the options are
        // listed: list them again.
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(conn.TalkingTo), $"0x0084 npc 0x{conn.TalkingTo:x}: options again");
    }

    // What a price took from the stash (ItemMoves.Price.Stash), to the client: one 0xbf per tab this connection has the
    // contents of (the others come whole when it asks, 0xc7).
    internal static async Task SendStashPayment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AccountStash stash, AccountStash.Payment? paid)
    {
        foreach (var (tab, (removed, changed)) in paid?.Tabs ?? new Dictionary<int, (List<uint>, List<InventoryItem>)>())
            if (TabsSentOf(conn, stash).Contains(tab))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(stash.InventoryOf(tab), stash.NextRevision(tab), [.. removed, .. changed.Select(c => c.Id)], changed, stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
                    $"0x00bf stash tab {tab}: paid (stacks emptied [{string.Join(", ", removed)}], changed [{string.Join(", ", changed.Select(c => $"{c.Id}={c.Item.StackSize}"))}])");
    }
}
