using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.NpcDialogue;

/// <summary>What every NPC's dialogue options do: their text, quest rewards, hideout selection, respec and identifying.</summary>
public sealed class NpcDialogueModule : IModule
{
    // The Settlers (Kingsmarch) screens: the S→C 0x7b kind of each SendOpenScreen(<name>) in an NPCTalk row's script, from
    // the client's screen table at 0x142fd89f0 (0x54 Recombinator, 0x55 Recruitment, 0x56 TownMap, 0x57 Mine, 0x58 Port,
    // 0x59 Farm, 0x5a BlackSmith, 0x5b Disenchanting, 0x5c MapRunners, 0x5d Fishing, 0x5e CurrencyExchange). No row opens
    // KalguurFarm: that panel comes from the town map (0x56), not from an NPC.
    private static readonly Dictionary<string, byte> Screens = new(StringComparer.Ordinal)
    {
        ["KalguurRecruitmentWindow"] = 0x55,   // Raulf, the Recruiter: Town Recruitment (NPCTalk category 54).
        ["KalguurTownMapWindow"] = 0x56,       // the King's Hand: Manage Town (category 57), and the three noticeboards.
        ["KalguurDisenchantingWindow"] = 0x5b, // Rog, the Disenchanter (category 55).
        ["KalguurPortWindow"] = 0x58,          // Tujen, the Harbourmaster (category 56). The client's window for this kind is an
                                               // empty stub (0x1400f1180), so sending it shows nothing: logged as known.
        ["CurrencyExchangeWindow"] = 0x5e,     // Faustus, the Financier (category 53).
    };

    private static readonly Regex ScreenScript = new(@"SendOpenScreen\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    /// <summary>The Kingsmarch screens this client cannot build (its village UI element is never created, so the 0x7b that
    /// opens them shows nothing), and what to do instead of standing in front of an empty screen.</summary>
    private static readonly Dictionary<byte, string> Hints = new()
    {
        [0x55] = "The recruitment window does not open in this client yet: /village hire hires a worker, /village assign <worker> <plot> puts one on a plot.",
        [0x56] = "The town map does not open in this client yet: /village reports the farm, its crops and its workers.",
        [0x58] = "The port window does not open in this client yet (shipping is not written).",
    };

    /// <summary>The screen a script's <c>SendOpenScreen( Name )</c> names.</summary>
    private static string? OpenedScreen(string? script) =>
        script is not null && ScreenScript.Match(script) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Dialogue options whose action lives in another module, so this one declines them instead of only showing
    /// their text.</summary>
    private static bool Settled(string? option) => option is "Fund the Treasury";

    /// <inheritdoc/>
    public void Load(IModuleContext module) => GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
    {
        var world = WorldOf(talk.Player);
        switch (talk.Category)
        {
            case "SelectHideout":
                await world.NpcClose("0x0084 close NPC dialogue for hideout selection");
                await talk.ModPlayer.Send(InstanceWaypoints.OpenUi(InstanceNpcs.HideoutSelectionUi, talk.OpensGuildHideouts ? 1u : 0u),
                    $"0x007b open hideout selection ({(talk.OpensGuildHideouts ? "guild" : "character")})");
                return true;
            case "Quests" when !talk.HasText && talk.Script.Contains("OpenMenageriePortal", StringComparison.Ordinal):
                await world.NpcClose("0x0084 close NPC dialogue for Menagerie travel");
                await world.TravelTo(WaypointCatalog.MenagerieHub, $"{talk.Npc}: {talk.Option}");
                return true;
            case "QuestReward" when talk.RewardOffer is { } offer && await world.NpcOfferReward(offer):
                return true;
            case "Gossip" or "Quests" or "QuestReward" when talk.HasText && Settled(talk.Option):
                // Another module owns this option (Johan's "Fund the Treasury" moves gold into Kingsmarch's treasury):
                // declining leaves it for that module's hook instead of only showing the line's text.
                return false;
            case "Gossip" or "Quests" or "QuestReward" when talk.HasText:
                // 0x84 with the row's index: the client opens the dialogue window on that NPCTalk row (its text and audio).
                await world.NpcSay(talk.Npc, talk.Option, talk.Index, talk.Row);
                return true;
            // Bestel's Respecialisation (script: SendOpenScreen( CharacterPassiveTreeScreen, 1 )): the character's passive tree,
            // S→C 0x7b kind 0x5f with 1. Its refunds come back as C→S 0x36 (per node) and 0x37 (Refund All), paid in gold.
            case "RespecPassivePointsForGold":
                world.NpcEndTalk();
                await talk.ModPlayer.Send(InstanceWaypoints.OpenUi(InstanceNpcs.PassiveTreeUi, 1), "0x007b open the passive tree (respec)");
                return true;
            // Nessa's Identify Items (script: triggerer.IdentifyItems(), then the IdentifyAll sound): every unidentified item
            // in the main inventory, free. The sound effect is not sent (its message is not mapped).
            case "IdentifyItems":
                await world.NpcIdentifyItems();
                return true;
            // A Kingsmarch screen (script: SendOpenScreen( KalguurTownMapWindow ); triggerer.FinishNPCChat(); …): the dialogue
            // ends and the panel opens. What the panel shows is the village state the server must send (not written yet).
            case var _ when OpenedScreen(talk.Script) is { } screen && Screens.TryGetValue(screen, out byte ui):
                await world.NpcClose($"0x0084 close {talk.Npc}'s dialogue for {screen}");
                await talk.Player.Send(InstanceWaypoints.OpenUi(ui), $"0x007b 0x{ui:x2} open {screen} (NPCTalk {talk.Row})");
                // Some of those windows are never built by this client (its village UI element stays null, so 0x7b is a
                // no-op): say what to use instead, so the dialogue does not just end with nothing on screen.
                if (Hints.TryGetValue(ui, out string? hint)) await talk.Player.Tell(hint);
                return true;
            default:
                return false;
        }
    });
}
