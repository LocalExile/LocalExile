using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Creates one NPC (0x2bb, its NPCs.dat Id is an .ot type) and puts it in the wake zone; kept for clicks and /npcs.
    public static async Task<uint> SpawnNpc(Player player, NpcCatalog.Npc npc,
        int x, int y, float rotation)
    {
        uint id = player.TakeObjectId();
        var obj = new NpcObject(x, y, npc.Components!, rotation, npc.StateMachine, npc.TransitionState, npc.Model, npc.Height);
        uint type = InstanceEntities.ObjectTypeHash(npc.Id);
        string log = $"0x02bb create npc 0x{id:x} {npc} type=0x{type:x8} at ({x}, {y}) rotation {rotation:0.##}, components {string.Join(", ", npc.Components!)}";
        // An NPC is the player's quest view: their quest state chose it, so it is theirs alone to see (AreaDelta's private entities).
        if (InAreaFrame(player))
        {
            player.Area.Delta.Add(id, InstanceEntities.CreateObject(id, type, obj.Blob()), log, AreaDelta.Kind.Create, only: player);
            player.Area.Delta.Add(id, InstanceProtocol.EnterWakeZone(id), null, only: player);
        }
        else
        {
            var previousView = Program.ViewOf.Value;
            Program.ViewOf.Value = player;
            try
            {
                await SendWorldCreate(player, id, InstanceEntities.CreateObject(id, type, obj.Blob()), log);
                await SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 npc 0x{id:x} enters wake zone");
            }
            finally { Program.ViewOf.Value = previousView; }
        }
        lock (player.Npcs) player.Npcs[id] = new SpawnedNpc(npc, x, y);
        await RegisterPlayerCollision(player, id, npc.Id, x, y, rotation,
            HideoutCatalog.ByArea(player.Layout.Hash) is not null ? "npc:" + npc.Id : null, npc.Model);
        return id;
    }

    // The town's NPCs (NpcCatalog.In) at their cells.
    public static async Task SpawnTownNpcs(Player player)
    {
        var notes = new List<string>();
        var furniture = HideoutFurniture(player);
        var placements = furniture?.Npcs ?? NpcCatalog.In(player.Layout.Hash, player.Seed, notes);
        if (player.Layout.Id == "1_4_town")
            placements = [.. placements, new("Metadata/NPC/League/Delve/DelveMiner", 352, 618, 280 * MathF.PI / 180)];
        foreach (var note in notes) Console.WriteLine($"[gateway] {player.Connection.Remote} npcs: {note}");
        if (placements.Count == 0 || Environment.GetEnvironmentVariable("Game_NPCS") == "0") return;
        if (Npcs.Error is { } error) { Console.WriteLine($"[gateway] {player.Connection.Remote} npcs: {error}"); return; }
        if (StashCatalog.FromArrival(player.Layout.Hash))
            placements = [.. placements.Select(p => p with { X = player.Layout.SpawnX + p.X, Y = player.Layout.SpawnY + p.Y })];
        var quests = QuestsOf(player);
        foreach (var p in placements)
        {
            // A town NPC a quest stages (DiallaTown / DiallaTownWithered, Lilly shipside / sitting …) comes with the quest NPCs (World/Quest/QuestNpcs.cs).
            if (quests.ManagesNpc(player.Layout.Id, p.Id) || p.ShownBy is not null || p.GoneWith is not null) continue;
            if (Npcs.Place(p) is not { Supported: true } npc) { Console.WriteLine($"[gateway] {player.Connection.Remote} npc {p.Id}: unknown or unsupported components"); continue; }
            var at = furniture is null ? TownNpcCell(player.Layout, p) : (p.X, p.Y);
            if (at != (p.X, p.Y))
                Console.WriteLine($"[gateway] {player.Connection.Remote} npc {npc.Name}: blocked cell ({p.X}, {p.Y}), placed at {at}");
            await SpawnNpc(player, npc, at.Item1, at.Item2, p.Rotation);
        }
    }

    // Keep the client's room placements, which can intentionally stand on an unwalkable platform.
    public static (int X, int Y) TownNpcCell(AreaLayout layout, NpcCatalog.Placement placement)
    {
        var at = (placement.X, placement.Y);
        return !placement.FromRoom && layout.Walkable?.Invoke(at.X, at.Y) == false ? layout.Near(at) : at;
    }

    public static int NpcInteractionSize(SpawnedNpc npc) =>
        npc.MonsterBodySize ?? Math.Max(0, StationaryCollision.Resolve(npc.Npc.Id, npc.Npc.Model).Size);

    // A click on an NPC (C→S 0x24 on its object): the client walks to it first; follow it and answer once the player is there.
    public static async Task TalkToNpc(Player player,
        InstanceTransfer.UseSkillOnObject use, SpawnedNpc npc, double requestedAt)
    {
        // Only a fresh click opens/reopens NPC dialogue; held-LMB repeats (~100 ms later, 0x0400 clear) must not wipe
        // an open quest reward offer and send 0x0084 over it.
        if (!use.InitialPress) return;
        if (!TryBeginObjectWalk(player, requestedAt, npc.X, npc.Y, NpcInteractionSize(npc), out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 npc {npc.Npc} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        if (LeagueFor(player).Rules.RefillsVitalsAt(npc.Npc.Id))
        {
            if (player.PlayerLife == 0 || player.Character.HardcoreDeathRecorded) return;
            player.PlayerLife = LifeCap(player);
            player.PlayerMana = (uint)UnreservedMana(player);
            await GainFlaskCharges(player, int.MaxValue, "vendor refill");
            PersistVitals(player);
            Note("vendor restores life");
            await SendPlayerMana(player, "vendor restores mana");
        }
        uint id;
        lock (player.Npcs) id = player.Npcs.FirstOrDefault(n => ReferenceEquals(n.Value, npc)).Key;
        player.StashOpen = false;
        player.GuildStashOpen = false;
        player.TradeStashOpen = false;
        player.BenchOpen = false;
        player.ModuleWindow = null;
        player.ModuleWindowAnchor = null;
        await RefreshBenchBalance(player, clear: true);
        player.AutomaticNpcTalkRows.Clear();
        player.TalkingTo = id;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcRewardOffer = null;
        await CharacterVoiceLines.OnNpcTalked(player, npc.Npc.Id);
        if (await GameEvents.AnyNpcTalk(player, id, npc)) return;
        if (await ContinueQuestConversation(player, id)) return;
        // Ctrl, Alt or both held on the click (0x24 flags 0x0004, 0x0200): the NPC's option with that shortcut, as if it
        // was picked from the list: Nessa's Sell Items, Purchase Items and Identify Items, Faustus' Currency Exchange.
        // A talk the NPC still has for the player came first, above. An NPC without that shortcut, or an option nothing
        // answers (a league without its module), lists the options as a plain click does.
        if (Npcs.ShortcutOf(npc.Npc, NpcCatalog.ShortcutFor(use.Ctrl, use.Alt), Quests.Names(PlayerQuestFlags(player))) is { } shortcut)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} npc {npc.Npc.Name} {(use.Ctrl ? "Ctrl + " : "")}{(use.Alt ? "Alt + " : "")}click: NPCTalk row {shortcut.Row} \"{shortcut.Option}\" ({shortcut.Category})");
            if (await RunNpcOption(player, id, npc, shortcut, onClick: true)) return;
        }
        await Send(player, InstanceNpcs.Talk(id), $"0x0084 talk to npc 0x{id:x} {npc.Npc.Name}",
            still: () => PlayerActionCurrent(player, action));
    }

    // C→S 0x85: the dialogue window was closed or, while a talk plays, Continue was pressed. The client stays in its dialogue
    // state (and the player cannot move) until the server answers: back to the option list (0x84 0xff) after a talk, else
    // 0x84 with object 0, which ends the dialogue.
    public static async Task CloseNpcDialogue(Player player)
    {
        if (await GameEvents.AnyRewardClosing(player)) return;
        await GameEvents.RunNpcDialogueClosing(player);
        uint id = player.TalkingTo;
        if (id != 0 && player.InNpcTalk && !IsWoundedExile())
        {
            player.InNpcTalk = false;
            if (player.NpcFinishFlag is { } finish)
            {
                player.NpcFinishFlag = null;
                await SetQuestFlags(player, [finish]);
            }
            if (player.NpcFinishLeagueFlag is int finishLeague)
            {
                player.NpcFinishLeagueFlag = null;
                if (!player.Character.LeagueQuestFlags.Contains(finishLeague)) await SetLeagueQuestFlags(player, [finishLeague]);
            }
            // The row's script decides what follows its talk: the next page, or the end of the dialogue.
            if (player.NpcTalkScript is { } script)
            {
                player.NpcTalkScript = null;
                SpawnedNpc? speaker;
                lock (player.Npcs) speaker = player.Npcs.GetValueOrDefault(id);
                if (speaker is not null && await EndTalkScript(player, id, speaker, script)) return;
            }
            if (await ContinueQuestConversation(player, id)) return;
            await Send(player, InstanceNpcs.Talk(id), $"0x0084 npc 0x{id:x}: back to the options (0x85 after a talk)");
            return;
        }
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcTalkScript = null;
        player.NpcRewardOffer = null;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (0x85)");

        bool IsWoundedExile()
        {
            lock (player.Npcs) return player.Npcs.GetValueOrDefault(id)?.Npc.Id == TutorialObjects.Exile;
        }
    }

    public static async Task<bool> ContinueQuestConversation(Player player, uint id)
    {
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(id);
        if (npc is null || player.TalkingTo != id) return false;
        if (await GameEvents.ResolveNpcQuestConversation(player, id, npc) is bool handled) return handled;
        var flags = Quests.Names(PlayerQuestFlags(player));
        NpcCatalog.Talk? next;
        if (GameEvents.UsesNpcLeagueFlags(player, npc.Npc))
            next = Npcs.NextConversationOf(npc.Npc, flags, player.AutomaticNpcTalkRows, new HashSet<int>(player.Character.LeagueQuestFlags));
        else next = Npcs.NextConversationOf(npc.Npc, flags, player.AutomaticNpcTalkRows);
        if (next is null) return false;
        player.AutomaticNpcTalkRows.Add(next.Row);
        await ExecuteQuestConversation(player, id, npc, next);
        return player.InNpcTalk || player.NpcRewardOffer is not null || player.TalkingTo != id;
    }

    // C→S 0x82: an option picked in the NPC dialogue window (an NPCTalk row of the NPC talked to).
    public static async Task ChooseNpcOption(Player player, byte index)
    {
        uint id = player.TalkingTo;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(id);
        if (npc is null) { Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0082 npc option {index}: no NPC dialogue open"); return; }
        if (npc.Npc.Id == TutorialObjects.Exile) return;
        var talk = Npcs.TalkOf(npc.Npc, index);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0082 npc {npc.Npc.Name} option {index}: " +
            (talk is null ? "no NPCTalk row" : $"NPCTalk row {talk.Row} \"{talk.Option}\" ({talk.Category}), text audio [{string.Join(", ", talk.TextAudio)}]"));
        if (talk is null) return;
        await ExecuteQuestConversation(player, id, npc, talk);
    }

    public static async Task ExecuteQuestConversation(Player player, uint id, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (!await BanditChoices.ChooseDialogue(player, id, npc, talk))
            await RunNpcOption(player, id, npc, talk);
    }

    // An NPCTalk row takes effect: picked from the list (0x82), next in a quest's talk, or the click's shortcut
    // (onClick: no talk is open). False when the row is not for this character now, or nothing answers it.
    static async Task<bool> RunNpcOption(Player player, uint id, SpawnedNpc npc, NpcCatalog.Talk talk, bool onClick = false)
    {
        var flags = Quests.Names(PlayerQuestFlags(player));
        bool usesLeagueFlags = GameEvents.UsesNpcLeagueFlags(player, npc.Npc);
        if (usesLeagueFlags)
        {
            if (!talk.Available(flags, new HashSet<int>(player.Character.LeagueQuestFlags))) return false;
        }
        else if (!talk.Available(flags)) return false;
        if (!await GameEvents.AllowNpcOption(player, talk)) return false;
        // A row that sets a quest item's remove_flag takes the item (Nessa's Medicine Chest, Fairgraves' Allflame).
        if (!await HandInQuestItems(player, talk, npc.Npc.Id)) return false;
        if (usesLeagueFlags)
        {
            var toSet = talk.LeagueFlagsSet.Where(f => f is > 1 and <= Quests.League.Count).ToList();
            if (talk.StartLeagueFlag is int slf && !toSet.Contains(slf)) toSet.Add(slf);
            if (toSet.Count > 0) await SetLeagueQuestFlags(player, toSet);
        }
        await ApplyQuestConversationStart(player, id, npc, talk, applyRowRules: true);
        player.NpcFinishFlag = talk.FinishFlag;
        player.NpcFinishLeagueFlag = usesLeagueFlags ? talk.FinishLeagueFlag : null;
        player.NpcTalkScript = null;
        var option = new NpcOption(player, npc.Npc.Name, talk.Category, talk.Option,
            talk.Script, talk.TextAudio.Count > 0, talk.Index, talk.Row, talk.OpensGuildHideouts, talk.RewardOffer);
        bool offeredScript = false;
        // A scripted row's end (FinishNPCChat, DoNPCChat, a screen, a move): after its talk, or now when it has none.
        if (EndsWithScript(talk) && talk.Category is "Gossip" or "Quests" or "QuestReward" && talk.RewardOffer is null)
        {
            if (talk.TextAudio.Count > 0) player.NpcTalkScript = talk;
            else
            {
                if (player.NpcFinishFlag is { } finish) { player.NpcFinishFlag = null; await SetQuestFlags(player, [finish]); }
                if (player.NpcFinishLeagueFlag is int finishLeague)
                {
                    player.NpcFinishLeagueFlag = null;
                    if (!player.Character.LeagueQuestFlags.Contains(finishLeague)) await SetLeagueQuestFlags(player, [finishLeague]);
                }
                // Let the owning module execute its no-audio script before generic FinishNPCChat consumes it.
                offeredScript = true;
                if (await GameEvents.AnyNpcOption(option)) return true;
                // On the click itself a script that only ends the talk shows nothing: FinishNPCChat with a screen or an
                // event of a system the server does not have (Einhar's BestiaryScreen). The caller lists the options.
                if (onClick && talk.NextChat is null && talk.AreaTransition is null
                    && !(talk.OpensScreen is { } screen && ScriptScreens.ContainsKey(screen)))
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} npc option {talk.Category} \"{talk.Option}\": its script shows nothing here ({talk.Script.Trim()})");
                    return false;
                }
                if (await EndTalkScript(player, id, npc, talk)) return true;
            }
        }
        // What an option does is a module's (Modules/Core/npcdialogue); one nothing answers does nothing.
        if (!offeredScript && await GameEvents.AnyNpcOption(new(player, npc.Npc.Name, talk.Category, talk.Option, talk.Script, talk.TextAudio.Count > 0,
                talk.Index, talk.Row, talk.OpensGuildHideouts, talk.RewardOffer))) return true;
        Console.WriteLine($"[gateway] {player.Connection.Remote} npc option category {talk.Category}: no module answers it");
        return false;
    }

    /// <summary>For an offer with a league quest flag taken flag, whether the league holds it; null for any other offer.</summary>
    public static bool? LeagueRewardTaken(Player player, QuestRewardCatalog.Offer offer)
    {
        if (offer.TakenLeagueFlag is not int flag) return null;
        return player.Character.LeagueQuestFlags.Contains(flag);
    }

    // A quest's reward window (0xc0 and 0x87). False when the offer has nothing for this character, or was taken.
    public static async Task<bool> OfferNpcReward(Player player, int offer)
    {
        var rewardCatalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        if (rewardCatalog.Find(offer) is not { } rewardOffer) return false;
        HashSet<int> leagueFlags = [.. player.Character.LeagueQuestFlags];
        var allChoices = rewardCatalog.Choices(offer, player.Character.ClassRow);
        var unlockedChoices = allChoices
            .Where(c => c.RequiredLeagueFlag is not int req || leagueFlags.Contains(req))
            .ToArray();
        if (allChoices.Count > 0 && unlockedChoices.Length == 0) return false;
        var rewardItems = unlockedChoices
            .Select(c => (Choice: c, Item: QuestRewardCatalog.Create(c, ItemsFor(player), RollsFor(player))))
            .Where(c => c.Item is not null)
            .Select(c => ItemsFor(player).ByHash(c.Item!.TypeHash) is { } type
                ? (c.Choice, Item: (Item?)GameEvents.ResolveRewardItem(player, offer, c.Item!, type)) : c)
            .ToArray();
        if (rewardOffer.Reclaimable)
        {
            var carriedTypes = QuestItems.Carried(ItemsOf(player)).Select(x => x.Item.Item.TypeHash).ToHashSet();
            rewardItems = [.. rewardItems.Where(c => !carriedTypes.Contains(c.Item!.TypeHash))];
            if (rewardItems.Length == 0) return false;
        }
        // An offer none of whose choices can be built yet (unique gear, jewels) still counts as taken, so its quest completes
        // (campaign-quests.md E10): its taken flag is set without an item, and the options come back.
        if (rewardItems.Length == 0)
        {
            if (rewardOffer.TakenFlag is not { } taken || ClientHasQuestFlag(player, taken)) return false;
            Console.WriteLine($"[gateway] {player.Connection.Remote} offer {offer}: no choice can be built yet; {taken} set without an item");
            await SetQuestFlags(player, [taken]);
            await Send(player, InstanceNpcs.Talk(player.TalkingTo), $"0x0084 npc 0x{player.TalkingTo:x}: options again (offer {offer} taken without an item)");
            return true;
        }
        var rewardQuest = ItemsFor(player).ByHash(rewardItems[0].Item!.TypeHash)?.Quest;
        // An offer with a league flag is taken by it or by its own flags (an offer with only a league flag has none).
        // Reclaimable offers skip the single-item taken check (already filtered against carried items above).
        if (LeagueRewardTaken(player, rewardOffer) == true
            || !rewardOffer.Reclaimable
            && (rewardOffer.TakenLeagueFlag is null || rewardOffer.TakenFlag is not null || rewardQuest is not null)
            && QuestRewardCatalog.Taken(rewardOffer.TakenFlag, rewardQuest, f => ClientHasQuestFlag(player, f))) return false;
        player.InNpcTalk = false;
        player.NpcRewardOffer = offer;
        player.NpcRewardItems = rewardItems.ToDictionary(c => c.Choice.Id, c => c.Item!);
        await Send(player, InstanceInventories.AddInventory(0x600, NpcShops.Tab, NpcShops.Manager,
            QuestRewardCatalog.Arrange(rewardItems.Select(c => (c.Choice.Id, c.Item!)), ItemsFor(player))),
            $"0x00c0 quest reward inventory: offer {offer}, {rewardItems.Length} choices");
        await Send(player, InstanceNpcs.OpenQuestReward(0x600, checked((ushort)offer)),
            $"0x0087 reward inventory 0x600: quest reward offer {offer}");
        return true;
    }

    // An NPC's Identify Items: the main inventory's unidentified items become identified (their explicit mods and rare names
    // are sent from now on). Removing and adding the same ids in one 0xbf replaces them on the client (see UseItemOn).
    public static async Task IdentifyInventory(Player player, SpawnedNpc npc)
    {
        const byte row = PlayerInventories.MainInventory;
        var items = ItemsOf(player);
        var identified = items.IdentifyAll(row);
        if (identified.Count == 0)
        {
            // The list grays the option itself, so this is the click's shortcut (Ctrl + Alt): list the options, where the
            // client says why ("No items in your inventory can be identified").
            Console.WriteLine($"[gateway] {player.Connection.Remote} npc {npc.Npc.Name} identify items: nothing unidentified");
            await Send(player, InstanceNpcs.Talk(player.TalkingTo), $"0x0084 npc 0x{player.TalkingTo:x}: options (nothing to identify)");
            return;
        }
        uint inventory = PlayerInventories.InventoryId(row);
        await Send(player, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [.. identified.Select(i => i.Id)], identified),
            $"0x00bf inventory {inventory}: {npc.Npc.Name} identified {identified.Count} item(s)");
        foreach (var i in identified)
            Console.WriteLine($"[gateway] {player.Connection.Remote} npc {npc.Npc.Name} identified item {i.Id}: {string.Join("; ", i.Item.Mods.Select(Items.Mods.Describe))}");
        // ClientStrings IdentifyItemsChatMessage / IdentifyItemsChatMessageSingle, as a chat line (S→C 0x17).
        string text = identified.Count == 1 ? "1 Item identified" : $"{identified.Count} Items identified";
        await Send(player, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        // The client decides itself whether the option is usable (NPCTalkCategory IdentifyItems, 0x141e7bb90: gray with
        // "No items in your inventory can be identified" when nothing is unidentified), but only when the options are
        // listed: list them again.
        await Send(player, InstanceNpcs.Talk(player.TalkingTo), $"0x0084 npc 0x{player.TalkingTo:x}: options again");
    }

    // What a price took from the stash (ItemMoves.Price.Stash), to the client: one 0xbf per tab this connection has the
    // contents of (the others come whole when it asks, 0xc7).
    public static async Task SendStashPayment(Player player, AccountStash stash, AccountStash.Payment? paid)
    {
        foreach (var (tab, (removed, changed)) in paid?.Tabs ?? new Dictionary<int, (List<uint>, List<InventoryItem>)>())
            if (TabsSentOf(player, stash).Contains(tab))
                await Send(player, InstanceItems.InventoryChange(stash.InventoryOf(tab), stash.NextRevision(tab), [.. removed, .. changed.Select(c => c.Id)], changed, stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
                    $"0x00bf stash tab {tab}: paid (stacks emptied [{string.Join(", ", removed)}], changed [{string.Join(", ", changed.Select(c => $"{c.Id}={c.Item.StackSize}"))}])");
    }
}
