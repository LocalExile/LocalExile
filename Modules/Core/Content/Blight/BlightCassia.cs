using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>
/// Sister Cassia in Blight encounters (<c>Metadata/Monsters/Masters/BlightBuilderWild</c>, NPCs row 396) and in Hideouts
/// (<c>Metadata/NPC/League/Blight/BlightBuilderHideout</c>, NPCs row 397):
/// - Spawns wild Cassia beside the pump with <c>HasDialogue = true</c> and <c>InteractionFlags = 1</c> and registers her in
///   <c>player.Npcs</c> so players can talk to her to progress "Strange Growths" (Quest 122) and unlock Oil Anointing
///   (<c>QuestFlags 2058 CassiaOnAnointingOils</c>).
/// - Spawns Hideout Cassia via <c>GameEvents.HideoutReady</c> with Hideout Editor persistence.
/// - Plays her authentic <c>NPCTextAudio</c> voice lines (<c>0x01c7</c>) with a fresh packet buffer per party member.
/// </summary>
public static class BlightCassia
{
    public const string IntroVoice = "BlightBuilderIntroduction";
    public const string StartVoice = "BlightBuilderQuest";
    public const string VictoryVoice = "BlightBuilderQuestComplete";
    public const string FailureVoice = "BlightBuilderQuestFailed";

    const string HideoutEditorKey = "npc:" + BlightTables.CassiaHideoutNpcId;

    /// <summary>Spawns talkable Sister Cassia beside the pump once per defence and registers her in each member's <c>Npcs</c>.</summary>
    public static async Task SpawnNearPump(Player player, Defence d, Func<Player, IReadOnlyList<Player>> membersOf)
    {
        uint existingId;
        (int X, int Y) cell;
        lock (d)
        {
            existingId = d.CassiaId;
            cell = d.CassiaCell;
        }
        if (existingId != 0)
        {
            RegisterWildNpc(player, existingId, cell.X, cell.Y);
            return;
        }
        if (Monsters.Find(BlightTables.CassiaWildVariety) is not { Supported: true } variety) return;
        cell = player.Layout.Near((d.Cell.X + 8, d.Cell.Y - 4));
        uint id = await SpawnMonster(player, variety, d.Level, MonsterRarity.Normal, cell.X, cell.Y,
            friendly: true, log: false, alone: false, instanceOwned: true,
            shape: m => m with { HasDialogue = true, InteractionFlags = 1 },
            owned: (_, m) => { m.NonBlocking = true; m.NoRewards = true; m.DamageImmune = true; });
        if (id == 0) return;
        bool keep;
        lock (d)
        {
            keep = d.CassiaId == 0;
            if (keep) { d.CassiaId = id; d.CassiaCell = cell; }
        }
        if (!keep)
        {
            if (player.Area.Monsters.Remove(id, out var orphan)) { orphan.Dead = true; orphan.Life = 0; orphan.ActionGeneration++; }
            player.Instance?.World.MonsterOrigins.TryRemove(id, out _);
            await SendWorldRemoval(player, id, $"Duplicate Sister Cassia 0x{id:x} removed");
            return;
        }
        foreach (var member in membersOf(player))
            RegisterWildNpc(member, id, cell.X, cell.Y);
    }

    /// <summary>Registers the spawned wild Cassia entity in <paramref name="player"/>'s NPC table so clicks open her dialogue.</summary>
    public static void RegisterWildNpc(Player player, uint entityId, int x, int y)
    {
        if (entityId == 0) return;
        if (Npcs.Find(BlightTables.CassiaNpcRow.ToString()) is not { } npc) return;
        lock (player.Npcs) player.Npcs[entityId] = new SpawnedNpc(npc, x, y);
    }

    /// <summary>GameEvents.HideoutReady: places Sister Cassia in the player's hideout using her saved Hideout Editor placement or a clear spot.</summary>
    public static async Task SpawnInHideout(Player player)
    {
        if (Npcs.Find(BlightTables.CassiaHideoutNpcId) is not { Supported: true } npc) return;
        if (HideoutFurniture(player) is not { } scene) return;
        int x, y;
        float rotation;
        if (scene.Saved?.TryGetValue(HideoutEditorKey, out var moved) == true)
        {
            x = moved.X;
            y = moved.Y;
            rotation = moved.Rotation;
        }
        else
        {
            // Place near SmallStation without overlapping existing hideout NPCs.
            var cell = player.Layout.Near((scene.SmallStation.X + 6, scene.SmallStation.Y - 6));
            x = cell.X;
            y = cell.Y;
            rotation = 4.0f;
        }
        await SpawnNpc(player, npc, x, y, rotation);
    }

    /// <summary>GameEvents.NpcTalk: talking to Sister Cassia progresses "Strange Growths" and unlocks Blight Stash Tab anointing.</summary>
    public static async Task<bool> OnNpcTalk(Player player, uint id, SpawnedNpc npc)
    {
        if (npc.Npc.Row != BlightTables.CassiaNpcRow && npc.Npc.Row != BlightTables.CassiaHideoutNpcRow
            && !npc.Npc.Name.Contains("Cassia", StringComparison.OrdinalIgnoreCase))
            return false;
        await GrantFlags(player, [BlightTables.QuestFlagIds.Received, BlightTables.QuestFlagIds.AnointingUnlocked]);
        return false; // Let standard NPCTalk dialogue and options open normally.
    }

    /// <summary>GameEvents.NpcOption: handles Cassia's "Anoint Items" option (script OpenBlightCraftingWindow).</summary>
    public static async Task<bool> OnNpcOption(NpcOption option)
    {
        if (!option.Npc.Contains("Cassia", StringComparison.OrdinalIgnoreCase)) return false;
        bool isAnoint = option.Option.Contains("Anoint", StringComparison.OrdinalIgnoreCase)
            || option.Script.Contains("BlightCrafting", StringComparison.OrdinalIgnoreCase)
            || option.Category.Contains("Blight", StringComparison.OrdinalIgnoreCase);
        if (!isAnoint) return false;
        await GrantFlags(option.Player, [BlightTables.QuestFlagIds.AnointingUnlocked]);
        // BlightCraftingWindow S→C 0x007b kind byte is pending Game.exe verification; track window open state cleanly.
        option.Player.ModuleWindow = "BlightCrafting";
        return true;
    }

    /// <summary>Sets any unset Blight QuestFlags on <paramref name="player"/> (ignoring flags missing from QuestFlags.dat).</summary>
    public static async Task GrantFlags(Player player, IEnumerable<string> flags)
    {
        if (player.Closed || player.LeavingArea) return;
        var toAdd = new List<string>();
        foreach (string flag in flags)
        {
            if (string.IsNullOrEmpty(flag) || ClientHasQuestFlag(player, flag)) continue;
            if (BlightTables.ValidQuestFlags.Contains(flag)) toAdd.Add(flag);
        }
        if (toAdd.Count > 0) await SetQuestFlags(player, toAdd);
    }

    /// <summary>Sends a random Cassia NPCTextAudio line of <paramref name="voiceId"/> to every player in the instance.</summary>
    public static async Task Say(Player player, string voiceId, Func<Player, IReadOnlyList<Player>> membersOf, uint delayMs = 0)
    {
        if (!BlightTables.CassiaVoices.TryGetValue(voiceId, out var rows) || rows.Length == 0) return;
        ushort row = rows[Random.Shared.Next(rows.Length)];
        foreach (var member in membersOf(player))
            await Send(member, InstanceNpcs.TextAudio(0, BlightTables.CassiaNpcRow, row, delayMs),
                $"0x01c7 Sister Cassia {voiceId} (row {row})");
    }
}
