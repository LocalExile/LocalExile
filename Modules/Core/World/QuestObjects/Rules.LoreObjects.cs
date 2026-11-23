using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Data.Terrain.Generation;

static partial class QuestobjectsRules
{
    internal const string LorePrefix = "Metadata/NPC/Info/";
    internal const string TutorialNpcId = "Metadata/NPC/Info/TutorialNPC";
    internal const string CraftingBenchPlaceholderId = "Metadata/NPC/Info/CraftingBenchPlaceholder";

    internal sealed record LorePlacement(string Type, string? Model, int X, int Y, float Rotation, float? Height);

    static readonly RuleTable<Player, Dictionary<string, int>> LoreReadCycles = new();

    /// <summary>Whether an object type is a <c>Metadata/NPC/Info/</c> lore interactable (excluding the Tencent tutorial NPC
    /// and crafting-bench placeholder).</summary>
    internal static bool IsLoreType(string type) =>
        type.StartsWith(LorePrefix, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(TutorialNpcId, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(CraftingBenchPlaceholderId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lore interactables spawned by <see cref="SpawnLoreObjects"/> (Labyrinth trial monographs are spawned by
    /// <see cref="SpawnTrialObjects"/>).</summary>
    internal static bool IsGeneralLoreType(string type) => IsLoreType(type) && !LabyrinthTrials.IsGlyph(type);

    /// <summary>Collects every general lore interactable placed by the area's rooms (<see cref="ServerLevels.Level.RoomObjects"/>)
    /// and tiles (<see cref="ServerLevels.Level.TileLoreObjects"/>), or from captured room objects when no generated level is
    /// present, deduplicated by type and cell.</summary>
    internal static List<LorePlacement> CollectLorePlacements(string areaId, uint seed, ServerLevels.Level? level)
    {
        var seen = new HashSet<(string Type, int X, int Y)>();
        var result = new List<LorePlacement>();

        void Add(string type, string? model, int x, int y, float rotation, float? height)
        {
            if (!IsGeneralLoreType(type)) return;
            if (!seen.Add((type.ToLowerInvariant(), x, y))) return;
            result.Add(new(type, string.IsNullOrEmpty(model) ? null : model, x, y, rotation, height));
        }

        if (level is not null)
        {
            foreach (var o in level.RoomObjects)
                Add(o.Type, o.Model, o.X, o.Y, o.Rotation, float.IsNaN(o.Z) ? null : o.Z);
            foreach (var o in level.TileLoreObjects)
                Add(o.Type, o.Model, o.X, o.Y, o.Rotation, float.IsNaN(o.Z) ? null : o.Z);
            return result;
        }

        foreach (var o in AreaPopulation.LoadObjects(areaId, seed, []))
            Add(o.Type, o.Model, o.X, o.Y, o.Angle, o.Height);
        return result;
    }

    /// <summary>Prepares a lore <see cref="NpcCatalog.Npc"/> for spawning: applies the room/tile model and fixed height,
    /// and starts Transitionable lore objects (Damp Diary, Shavronne's Manifesto: <c>create_untransitioned</c> disables
    /// targeting and hides their minimap icon) in their transitioned state so they are targetable and visible.</summary>
    internal static NpcCatalog.Npc PrepareLoreNpc(NpcCatalog.Npc npc, string? model, float? height)
    {
        byte state = npc.Components?.Contains("Transitionable") == true ? NpcTransitionStates(npc) : npc.TransitionState;
        return npc with
        {
            Model = !string.IsNullOrEmpty(model) ? model : npc.Model,
            Height = height ?? npc.Height,
            TransitionState = state,
        };
    }

    /// <summary>Chooses the NPCTalk row to open directly via S→C 0x84 for a lore interactable, or null when the lore object
    /// has multiple differently named pages (e.g. Book of Memories, The Word of the Dreamer) and should show the option list
    /// (<c>0x84 0xff</c>) instead.</summary>
    internal static NpcCatalog.Talk? SelectLoreTalk(IReadOnlyList<NpcCatalog.Talk> available, IReadOnlySet<string> flags, int cycleIndex)
    {
        if (available.Count == 0) return null;
        if (available.Count == 1) return available[0];
        // Multiple differently named entries (Page 1..16, Visions I..V): let the player pick from the normal option list.
        if (available.Select(t => t.Option).Distinct(StringComparer.Ordinal).Count() > 1) return null;
        // Identical "Read" entries (Letters of Exile: 15 letters with HeardFlags ReadLetter1..15): read the first unread
        // letter, or cycle through all letters once every one has been read.
        var unread = available.FirstOrDefault(t => t.HeardFlags.Count > 0 && t.HeardFlags.Any(f => !flags.Contains(f)));
        return unread ?? available[Math.Abs(cycleIndex) % available.Count];
    }

    /// <summary>Quest flags recorded when reading a lore row: its start/finish/script flags and heard flags, excluding the
    /// <c>NEVER_SET</c> sentinel.</summary>
    internal static List<string> LoreFlagsToSet(NpcCatalog.Talk talk)
    {
        var flags = new List<string>();
        void Add(string? flag)
        {
            if (flag is { Length: > 0 } && flag != "NEVER_SET" && !flags.Contains(flag)) flags.Add(flag);
        }
        Add(talk.StartFlag);
        Add(talk.FinishFlag);
        foreach (string f in talk.ScriptSets) Add(f);
        foreach (string f in talk.HeardFlags) Add(f);
        return flags;
    }

    // Spawns every lore interactable placed by the area's rooms and tiles (Act 1 Weathered Carvings, Sailor's Skin, Damp
    // Diary, Shavronne's Journal, Letters of Exile, Lioneye's Standard, Arteri's Letter, and all campaign lore objects).
    internal static async Task SpawnLoreObjects(Player player)
    {
        if (Environment.GetEnvironmentVariable("Game_NPCS") == "0" || Npcs.Error is not null) return;
        var level = player.Layout.Level ?? ServerLevels.For(player.Layout.Id, player.Seed);
        var placements = CollectLorePlacements(player.Layout.Id, player.Seed, level);
        if (placements.Count == 0) return;
        var quests = QuestsOf(player);
        int spawned = 0;
        foreach (var p in placements)
        {
            if (quests.ManagesNpc(player.Layout.Id, p.Type)) continue;
            if (Npcs.Find(p.Type) is not { Supported: true } baseNpc
                || !baseNpc.Id.Equals(p.Type, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Npcs.TalksOf(baseNpc).Any()) continue;
            bool duplicate;
            lock (player.Npcs)
                duplicate = player.Npcs.Values.Any(n => n.Npc.Id.Equals(baseNpc.Id, StringComparison.OrdinalIgnoreCase)
                    && n.X == p.X && n.Y == p.Y);
            if (duplicate) continue;
            var npc = PrepareLoreNpc(baseNpc, p.Model, p.Height);
            await SpawnNpc(player, npc, p.X, p.Y, p.Rotation);
            spawned++;
        }
        if (spawned > 0)
            Console.WriteLine($"[gateway] {player.Connection.Remote} lore objects: spawned {spawned} in {player.Layout.Id}");
    }

    // Clicking a lore interactable (or Labyrinth trial monograph) opens its reading window directly with S→C 0x84 <index>,
    // leaving player.InNpcTalk = false so C→S 0x85 closes cleanly via S→C 0x84 object 0 without bouncing back to an option list.
    // Returns false only for multi-page named lore books so TalkToNpc opens their option list (0x84 0xff).
    internal static async Task<bool> ReadLoreObject(Player player, uint id, SpawnedNpc npc)
    {
        if (LabyrinthTrials.IsGlyph(npc.Npc.Id))
        {
            await ReadTrialMonograph(player, id, npc);
            return true;
        }
        var flags = Quests.Names(PlayerQuestFlags(player));
        var available = Npcs.TalksOf(npc.Npc).Where(t => t.Available(flags)).ToList();
        if (available.Count == 0)
        {
            player.TalkingTo = 0;
            player.InNpcTalk = false;
            await Send(player, InstanceNpcs.Talk(0), $"0x0084 lore {npc.Npc.Id}: no available text");
            return true;
        }
        var cycles = LoreReadCycles.GetOrCreateValue(player);
        int cycle = cycles.GetValueOrDefault(npc.Npc.Id, 0);
        var talk = SelectLoreTalk(available, flags, cycle);
        if (talk is null) return false;
        if (available.Count > 1) cycles[npc.Npc.Id] = (available.IndexOf(talk) + 1) % available.Count;
        var toSet = LoreFlagsToSet(talk).Where(f => !ClientHasQuestFlag(player, f)).ToList();
        if (toSet.Count > 0) await SetQuestFlags(player, toSet);
        // Leave player.InNpcTalk = false: on C→S 0x85 (Continue / Esc / walk away), CloseNpcDialogue ends dialogue with 0x84 0.
        player.InNpcTalk = false;
        await Send(player, InstanceNpcs.Talk(id, (byte)talk.Index),
            $"0x0084 read lore 0x{id:x} {npc.Npc.Id} ({npc.Npc.Name}): NPCTalk row {talk.Row} (index {talk.Index})");
        return true;
    }
}
