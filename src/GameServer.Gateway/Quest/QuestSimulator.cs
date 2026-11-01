using System.Text.Json;
using GameServer.Core.World;

/// <summary>
/// Plays quest events through the engine without a client (Agents/docs/protocol/12-quests.md §5, "The oracle"): the quests'
/// tables (<see cref="Quests"/>), the NPCTalk rows (availability, QuestFlag4/5, scripts, hand-ins, reward offers), the quest
/// items' .it flags (<see cref="QuestItems"/>), QuestStaticRewards and the client's own gate (WorldAreas EndQuestFlags) and
/// the Gateway's exits (<see cref="Quests.FlagToEnter"/>). Each step throws <see cref="StepFailed"/> when the game would not
/// let it happen (a closed exit, an option not offered, an item not dropped), so a sequence that runs is one the server
/// accepts. QuestEngineTests feeds every act's sequence and checks the result against CharacterStartQuestState.
/// Code-driven quest objects (a1q4's Glyph Wall) register in <see cref="ObjectHandlers"/>.
/// </summary>
sealed class QuestSimulator
{
    public sealed class StepFailed(string message) : Exception(message);

    readonly NpcCatalog npcs;
    readonly ItemCatalog items;
    readonly QuestStaticRewards rewards;
    readonly QuestRewardCatalog offers;
    readonly Dictionary<string, (string[] Start, string[] End)> areaFlags;
    readonly AreaVisitFlags visits;

    /// <summary>The character's own flags (what the save has), and the session flags (lost on leaving).</summary>
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SessionFlags { get; } = new(StringComparer.Ordinal);
    /// <summary>The areas entered (their World panel visit flags count as the client's flags).</summary>
    public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
    /// <summary>The quest items carried and on the current area's ground, by BaseItemTypes Id.</summary>
    public List<string> Carried { get; } = [];
    public List<string> Ground { get; } = [];
    public string? Area { get; private set; }
    public int RefundPoints { get; private set; }
    /// <summary>The flags every character is given (<see cref="Quests.EveryCharacter"/>) that the play has not set yet: not
    /// earned, as ClientState.UnearnedQuestFlags (Kitava's EndGameAvailable counts for his resistance penalty only once an
    /// event sets it).</summary>
    public HashSet<string> Unearned { get; } = new(Quests.EveryCharacter, StringComparer.Ordinal);
    readonly HashSet<string> credited = new(StringComparer.Ordinal);
    public List<string> Log { get; } = [];

    /// <summary>Code-driven quest objects: what using one does (a1q4's Glyph Wall, GlyphQuest.Simulate).</summary>
    public static readonly Dictionary<string, Action<QuestSimulator>> ObjectHandlers = new(StringComparer.Ordinal)
    {
        [GlyphQuest.Wall] = GlyphQuest.Simulate,
    };

    public QuestSimulator(string folder = "data")
    {
        npcs = NpcCatalog.Load(folder);
        items = new ItemCatalog(folder);
        rewards = QuestStaticRewards.Load(folder);
        offers = QuestRewardCatalog.Load(folder);
        visits = AreaVisitFlags.Load(Path.Combine(folder, "tables", "WorldAreas.json"), Path.Combine(folder, "tables", "MapPins.json"));
        areaFlags = [];
        var flagNames = Quests.ParseRows(File.ReadAllText(Path.Combine(folder, "tables", "QuestFlags.json"))).ToDictionary(kv => kv.Value, kv => kv.Key);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables", "WorldAreas.json")));
        foreach (var w in doc.RootElement.EnumerateArray())
        {
            string[] Names(string column) => [.. w.GetProperty(column).EnumerateArray().Select(e => flagNames.GetValueOrDefault(e.GetInt32())).OfType<string>()];
            areaFlags.TryAdd(w.GetProperty("Id").GetString() ?? "", (Names("StartQuestFlags"), Names("EndQuestFlags")));
        }
        // As on login: an endgame flag not earned yet is not the character's (Quests.EndgameOnly; the oracle plays no
        // hideout or map area, where the client would have it).
        Flags.UnionWith(Quests.EveryCharacter.Except(Quests.EndgameOnly));
    }

    /// <summary>Whether the client has the flag: the character's own, a session flag, or a visited area's World panel flag.</summary>
    public bool Has(string flag) => Flags.Contains(flag) || SessionFlags.Contains(flag)
        || Visited.Any(a => VisitFlags(a).Contains(flag));

    IReadOnlySet<string> All => Flags.Concat(SessionFlags).Concat(Visited.SelectMany(VisitFlags)).ToHashSet();

    // A visited area's flags (its World panel visit and access flags, WorldAreas StartQuestFlags), the same every time: kept
    // per area, as every Has asks them for every visited area.
    readonly Dictionary<string, IReadOnlySet<string>> visitFlags = new(StringComparer.Ordinal);
    IReadOnlySet<string> VisitFlags(string area)
    {
        if (!visitFlags.TryGetValue(area, out var names))
            visitFlags[area] = names = Quests.Names(visits.For(area)).Concat(areaFlags.GetValueOrDefault(area).Start ?? []).ToHashSet(StringComparer.Ordinal);
        return names;
    }

    public QuestSimulator Set(IEnumerable<string> flags, bool on = true)
    {
        foreach (var f in flags)
        {
            // A session flag, or an instance's own state (Quests.IsInstanceFlag): both go with the area.
            var target = QuestStates.Default.Value.IsSession(f) || Quests.IsInstanceFlag(f) ? SessionFlags : Flags;
            if (on ? target.Add(f) | Unearned.Remove(f) : target.Remove(f)) Log.Add($"{(on ? "+" : "-")}{f}");
        }
        var (points, due) = rewards.NewRefunds(Flags, credited);
        RefundPoints += points;
        credited.UnionWith(due);
        return this;
    }

    // The quest items' flags follow the items carried (QuestItems.Sync), and done ones go.
    void Sync()
    {
        var carried = Carried.Select(id => items.Find(id)?.Quest).OfType<QuestItemFlags>().ToHashSet();
        var (set, clear, remove) = QuestItems.Sync(items.QuestItemTypes.Select(t => t.Quest!), carried, Has);
        Set(set);
        Set(clear, false);
        Carried.RemoveAll(id => items.Find(id)?.Quest is { } q && remove.Contains(q));
    }

    /// <summary>Enters an area: from the current one through its exit (the Gateway's exit flag), or by waypoint/travel
    /// (<paramref name="travel"/>), always through the client's own gate (WorldAreas EndQuestFlags). Session flags and the
    /// ground go with the old area.</summary>
    public QuestSimulator Enter(string area, bool travel = false)
    {
        if (!travel && Area is { } from && Quests.FlagToEnter(from, area) is { } needed && !Has(needed))
            throw new StepFailed($"the exit from {from} to {area} needs {needed}");
        if (areaFlags.TryGetValue(area, out var gate) && gate.End.FirstOrDefault(f => !Has(f)) is { } missing)
            throw new StepFailed($"{area} needs {missing} (WorldAreas EndQuestFlags)");
        Area = area;
        Visited.Add(area);
        SessionFlags.Clear();
        Appeared.Clear();
        Ground.Clear();
        Log.Add($"enter {area}");
        Set(Quests.OnEntered(area));
        Sync();
        return this;
    }

    string Here => Area ?? throw new StepFailed("in no area");

    /// <summary>Comes near a living monster of the variety.</summary>
    readonly Dictionary<string, HashSet<string>> killedHere = new(StringComparer.Ordinal);

    public QuestSimulator See(string variety) { Set(Quests.OnMonsterSeen(Here, variety)); return this; }

    /// <summary>Comes near a room object of this type (Quests.NearRule).</summary>
    public QuestSimulator Near(string objectType)
    {
        var rules = Quests.NearObjects(Here).Where(r => r.ObjectType == objectType).ToList();
        if (rules.Count == 0) throw new StepFailed($"no rule watches {objectType} in {Here}");
        if (rules.Where(r => r.Needs is not null).SelectMany(r => r.Needs!).Where(f => !Has(f)).ToList() is { Count: > 0 } missing
            && rules.All(r => r.Needs is not null))
            throw new StepFailed($"coming near {objectType} needs {string.Join(", ", missing)}");
        Set(rules.Where(r => r.Needs is null || r.Needs.All(Has)).SelectMany(r => r.Flags));
        Sync();
        return this;
    }

    /// <summary>Kills a monster of the variety: its flags, drops and second form (which <paramref name="variety"/>'s caller kills next).</summary>
    public QuestSimulator Kill(string variety)
    {
        Log.Add($"kill {variety}");
        Set(Quests.OnMonsterKilled(Here, variety));
        // A multi-kill (Quests.KilledAllRule) completes once each of its varieties died here (one monster of each).
        if (!killedHere.TryGetValue(Here, out var killed)) killedHere[Here] = killed = new(StringComparer.Ordinal);
        killed.Add(variety);
        foreach (var rule in Quests.KilledAllOf(Here, variety).Where(r => r.Varieties.All(killed.Contains))) Set(rule.Flags);
        // A boss that never dies (Kitava in Act 5) reaches its threshold instead (/kill in the Gateway does the same).
        if (Quests.ThresholdOf(Here, variety) is { } threshold) Set(threshold.Flags);
        foreach (var item in Quests.DropsOnKill(Here, variety)) Drop(item);
        foreach (var rule in Quests.AppearOnKill(Here, variety)) Appeared.Add(rule.NpcType);
        return this;
    }

    /// <summary>The last hostile monster of the area dies.</summary>
    public QuestSimulator Clear() { Set(Quests.OnCleared(Here, Has)); return this; }

    void Drop(string item)
    {
        var q = items.Find(item)?.Quest;
        if (QuestItems.Drops(q, Has, Carried.Contains(item), Ground.Contains(item))) { Ground.Add(item); Log.Add($"drop {item}"); }
    }

    /// <summary>Opens a quest chest of this type in the area (Quests.ChestRule): its used flags and its item.</summary>
    public QuestSimulator OpenChest(string chestType)
    {
        var rule = Quests.Chests(Here).FirstOrDefault(r => r.ChestType == chestType) ?? throw new StepFailed($"no quest chest {chestType} in {Here}");
        if (rule.OpensWith is { } needs && !Has(needs)) throw new StepFailed($"{chestType} cannot be opened before {needs}");
        Set(Quests.OnUsed(Here, chestType, Has));
        Drop(rule.Item);
        return this;
    }

    /// <summary>Uses a quest object (a code-driven one from <see cref="ObjectHandlers"/>, else its UsedRule flags, when the
    /// character has their needs; the quest items those flags finish go with the sync, as QuestItems.Consume takes them).</summary>
    public QuestSimulator Use(string objectType)
    {
        if (ObjectHandlers.TryGetValue(objectType, out var handler)) handler(this);
        else
        {
            var flags = Quests.OnUsed(Here, objectType, Has);
            if (flags.Count == 0 && Quests.UseMissing(Here, objectType, Has) is { Count: > 0 } missing)
                throw new StepFailed($"{objectType} needs {string.Join(", ", missing)}");
            Log.Add($"use {objectType}");
            Set(flags);
        }
        Sync();
        return this;
    }

    /// <summary>Picks up a quest item lying on the ground (or, with <paramref name="given"/>, one the step gives: a1q4's
    /// nests, which GlyphQuest drops).</summary>
    public QuestSimulator PickUp(string item, bool given = false)
    {
        if (!given && !Ground.Remove(item)) throw new StepFailed($"{item} is not on the ground in {Here}");
        var q = items.Find(item)?.Quest;
        if (q is not null && QuestItems.Removed(q, Has)) throw new StepFailed($"{item} is not needed any more");
        Carried.Add(item);
        if (q is not null) Set(QuestItems.PickupFlags(q, Has));
        Sync();
        return this;
    }

    /// <summary>Takes a carried quest item away (a code-driven object that consumes it).</summary>
    public bool Take(string item) => Carried.Remove(item);

    /// <summary>Uses a carried quest item (a book): its use_flag, and QuestStaticRewards' points.</summary>
    public QuestSimulator UseItem(string item)
    {
        var q = items.Find(item)?.Quest;
        if (!Carried.Contains(item) || q?.Use is not { } use) throw new StepFailed($"{item} is not carried or not usable");
        Carried.Remove(item);
        Set([use]);
        if (q.Grant is { } grant) Set([grant], false);
        Sync();
        return this;
    }

    /// <summary>The NPC's available rows whose option contains the text, preferring one whose QuestFlag3 ("heard") flags are
    /// not all set: the client lists a quest's next row (Tarkleigh's second "Dweller of the Deep" after the kill).</summary>
    NpcCatalog.Talk Option(string npc, string option, bool reward, int? index = null)
    {
        var n = npcs.Find(npc) ?? throw new StepFailed($"no NPC {npc}");
        if (Area is { } here && !NpcHere(here, n.Id)) throw new StepFailed($"{n.Name} ({n.Id}) is not in {here} now");
        var all = All;
        var rows = npcs.TalksOf(n).Where(t => t.Option.Contains(option, StringComparison.OrdinalIgnoreCase) && t.Available(all)
            && (t.Category == "QuestReward") == reward && (index is null || t.Index == index)).ToList();
        if (rows.Count == 0) throw new StepFailed($"{n.Name} offers no {(reward ? "reward" : "option")} \"{option}\"");
        return rows.FirstOrDefault(t => !(t.HeardFlags.Count > 0 && t.HeardFlags.All(Has))) ?? rows[0];
    }

    /// <summary>Talks to the NPC and picks the option (its hand-in, QuestFlag4, scripts, then QuestFlag5 when the talk ends).</summary>
    public QuestSimulator Talk(string npc, string option, int? index = null)
    {
        var talk = Option(npc, option, reward: false, index);
        Log.Add($"talk {npc}: {talk.Option} (row {talk.Row})");
        if (QuestItems.HandedIn(talk.StartFlag, items.QuestItemTypes.Select(t => t.Quest!), Has) is { } type)
        {
            string item = items.QuestItemTypes.First(t => t.Quest == type).Id;
            if (!Carried.Remove(item)) throw new StepFailed($"{talk.Option}: {item} is not carried");
            if (type.Grant is { } grant) Set([grant], false);
        }
        if (Quests.TalkStartFlag(talk) is { } start) Set([start]);
        // The other items the row takes in the same click (QuestFlag3 of required items: Sin's "_take_ingredients_").
        var carried = Carried.Select(id => items.Find(id)?.Quest).OfType<QuestItemFlags>().ToList();
        Set(QuestItems.HandInsOfRow(talk, carried).ToList());
        // The NPC's other rows with the same text that hand in a carried item go with it (Piety's organs).
        Set(QuestItems.SiblingHandIns(talk, npcs.TalksOf(npcs.Find(npc)!), All, carried).ToList());
        Set(Quests.OnTalkRow(talk.Row));
        // Hand-in rules (Hargan's busts): each carried item's remove_flag.
        foreach (string item in Quests.HandsIn(npcs.Find(npc)!.Id, talk.Option, talk.Row).Where(Carried.Contains))
            if (items.Find(item)?.Quest?.Remove is { } remove) Set([remove]);
        Set(talk.ScriptSets);
        Set(talk.ScriptClears, false);
        if (talk.FinishFlag is { } finish) Set([finish]);
        Sync();
        return this;
    }

    /// <summary>Talks to the NPC and claims the reward option's offer (its taken flag, or the quest item it gives).</summary>
    public QuestSimulator Reward(string npc, string option)
    {
        var talk = Option(npc, option, reward: true);
        if (talk.StartFlag is { } start) Set([start]);
        var offer = talk.RewardOffer is int o ? offers.Find(o) : null;
        if (offer is null || offer.Choices.Count == 0) throw new StepFailed($"{talk.Option}: offer {talk.RewardOffer} has nothing to claim");
        var item = items.Row(offer.Choices[0].ItemRow);
        if (QuestRewardCatalog.Taken(offer.TakenFlag, item?.Quest, Has)) throw new StepFailed($"{talk.Option}: offer {offer.Row} is taken");
        // (An offer with no buildable item still sets its taken flag: Game.OfferNpcReward.)
        Log.Add($"reward {npc}: {talk.Option} (offer {offer.Row})");
        if (offer.TakenFlag is { } taken) Set([taken]);
        if (item?.Quest is not null) { Carried.Add(item.Id); Set(QuestItems.PickupFlags(item.Quest, Has)); }
        Sync();
        return this;
    }

    /// <summary>The NPCs that appeared where a monster died in this area (Quests.AppearsRule), gone with the area.</summary>
    public HashSet<string> Appeared { get; } = new(StringComparer.Ordinal);

    // Whether an NPC a quest decides stands in the area now (the Game module's QuestNpcs.cs); NPCs no rule of this area
    // manages are not checked (the steps name the NPC's own area).
    bool NpcHere(string area, string npc)
    {
        var appears = Quests.Appearing(area).Where(r => r.NpcType == npc).ToList();
        if (appears.Count > 0)
            return Appeared.Contains(npc) && Quests.Exists(npc, Has) && appears.All(r => r.GoneWith is null || !Has(r.GoneWith))
                || appears.Any(r => r.GoneWith is { } gone && !Has(gone) && Quests.OnMonsterKilled(area, r.Variety).Any(Has));
        return !Quests.ManagesNpc(area, npc) || Quests.NpcPresent(area, npc, Has);
    }

    /// <summary>The passive points the flags give (QuestStaticRewards).</summary>
    public int PassivePoints => rewards.PassivePoints(Flags);
}
