using GameServer.Modding;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Incursion;

/// <summary>
/// Incursion: Alva's incursions in the maps whose Atlas passives or mods give her master mission, and the character's
/// Temple of Atzoatl plan they build. The encounter is <see cref="IncursionEncounter"/>, one per area instance; this file
/// hooks it: a new instance's population (the roll and the spots), a player's entry, the tick (the reveal and the
/// timer), Alva's dialogue option, a click on her portal or a cursed treasure, and the kills.
/// </summary>
public sealed class IncursionModule : IModule
{
    readonly IncursionEncounter encounter = new();

    /// <summary>This module's game data, built once when it loads.</summary>
    public static void LoadData() => IncursionTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        GameEvents.MapOpening.Add(id, Opening);
        GameEvents.GroundItemPreparing.Add(id, Prepare);
        module.Lifetime.Own(new Cleanup(encounter.Clear));
        GameEvents.AreaPopulated.Add(id, encounter.Populated);
        GameEvents.BeforeEnterMessages.Add(id, encounter.EntryMessages);
        GameEvents.PlayerEntered.Add(id, encounter.Entered);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, seconds) => encounter.Has(player) ? encounter.Advance(player, seconds) : Task.CompletedTask));
        GameEvents.PlayerLeaving.Add(id, (player, _) => encounter.Leaving(player));
        GameEvents.Kill.Add(id, encounter.Killed);
        // "Enter Incursion": NPCTalkCategory OpenIncursionPortal on Metadata/NPC/League/Incursion/TreasureHunterWild,
        // whose script ends the chat (FinishNPCChat) before the portal opens. Only the instance's own Alva.
        GameEvents.NpcOption.Add(id, async talk =>
        {
            // "View Temple" (ViewIncursionTemple) opens the character's temple plan; its script is a ruleset event, not a chat end.
            if (talk.Category == "ViewIncursionTemple") return await encounter.ViewTemple(talk.Player);
            if (talk.Category != "OpenIncursionPortal") return false;
            var player = talk.Player;
            uint alva = encounter.AlvaOf(player);
            if (alva == 0 || alva != player.TalkingTo) return false;
            await WorldOf(player).NpcClose($"0x0084 object 0: Alva's dialogue ends (NPCTalk {talk.Row} \"{talk.Option}\")");
            return await encounter.Talk(player);
        });
        GameEvents.ObjectUse.Add(id, encounter.OnUse);
    }

    public static Item Prepare(Player player, Item item)
    {
        if (item.IncursionTemple is not { } temple || MapModsOf(player)?.Get(Stat.MapFinalIncursionArchitectCopiesTemple) is not > 0) return item;
        var rng = AreaRng(player);
        return item with { IncursionTemple = temple with { Rooms = [.. temple.Rooms.Select(hash =>
        {
            if (IncursionTables.AllRooms.FirstOrDefault(r => r.Hash16 == hash)?.ArchitectKey is not { } family
                || !IncursionTables.TempleFamilies.TryGetValue(family, out var tiers)) return hash;
            var choices = tiers.Where(t => t.Key is >= 1 and <= 3).OrderBy(t => t.Key).Select(t => t.Value).ToArray();
            return choices.Length == 0 ? hash : IncursionTables.RoomHash16(choices[rng.Next(choices.Length)]);
        })] } };
    }

    public static void Opening(MapOpening request)
    {
        if (!request.Campaign && request.Opening is { } opening && MapDevices.IsMapArea(opening.Area.Hash16)
            && IncursionTables.RulesetRow is not null && IncursionTemple.TempleGraph is not null) request.HandledContent.Add("Incursion");
    }

    sealed class Cleanup(Action clean) : IDisposable
    {
        public void Dispose() => clean();
    }
}
