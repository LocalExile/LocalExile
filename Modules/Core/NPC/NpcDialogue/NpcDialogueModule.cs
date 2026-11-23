using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.NpcDialogue;

/// <summary>What every NPC's dialogue options do: their text, quest rewards, hideout selection, respec and identifying.</summary>
public sealed class NpcDialogueModule : IModule
{
    // The Settlers (Kingsmarch) screens: the S->C 0x7b kind of each SendOpenScreen(<name>) in an NPCTalk row's script.
    // The kinds are the client's own screen table (0x142fd89f0: entries of a UTF-16 name, a length and a kind), which the
    // panel dispatcher 0x142169dd0 switches on: 0x54 recombinator, 0x55 recruitment, 0x56 town map, 0x57 mine,
    // 0x58 port, 0x59 farm, 0x5a blacksmith, 0x5b disenchanting, 0x5c map runners, 0x5d fishing, 0x5e currency exchange.
    // 0x56 (town map) and 0x58 (port) were also confirmed on screen. 0x57 opens the MINING panel, not the port.
    private static readonly Dictionary<string, byte> Screens = new(StringComparer.Ordinal)
    {
        ["KalguurTownMapWindow"] = 0x56,       // the King's Hand: Manage Town (category 57), and the three noticeboards.
        ["KalguurRecruitmentWindow"] = 0x55,   // Raulf, the Recruiter: Town Recruitment (NPCTalk category 54).
        ["KalguurDisenchantingWindow"] = 0x5b, // Rog, the Disenchanter (category 55).
        ["KalguurPortWindow"] = 0x58,          // Tujen, the Harbourmaster: Shipping (category 56).
        ["CurrencyExchangeWindow"] = 0x5e,     // Faustus, the Financier (category 53).
    };

    private static readonly Regex ScreenScript = new(@"SendOpenScreen\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    /// <summary>Kingsmarch screens whose panel still shows nothing beyond its frame: tells the player what the dev
    /// commands do instead. Empty once a panel draws its own state.</summary>
    private static readonly Dictionary<byte, string> Hints = new();

    /// <summary>The screen a script's <c>SendOpenScreen( Name )</c> names.</summary>
    private static string? OpenedScreen(string? script) =>
        script is not null && ScreenScript.Match(script) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Dialogue options whose action lives in another module, so this one declines them instead of only showing
    /// their text.</summary>
    private static bool Settled(string? option) => option is "Fund the Treasury";

    /// <inheritdoc/>
    public void Load(IModuleContext module) => GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
    {
        var player = Program.PlayerOf(talk.Conn);
        var world = Program.WorldOf(player);
        switch (talk.Category)
        {
            case "SelectHideout":
                await world.NpcClose("0x0084 close NPC dialogue for hideout selection");
                await player.Send(InstanceWaypoints.OpenUi(InstanceNpcs.HideoutSelectionUi, talk.OpensGuildHideouts ? 1u : 0u),
                    $"0x007b open hideout selection ({(talk.OpensGuildHideouts ? "guild" : "character")})");
                return true;
            case "Quests" when !talk.HasText && talk.Script.Contains("OpenMenageriePortal", StringComparison.Ordinal):
                await world.NpcClose("0x0084 close NPC dialogue for Menagerie travel");
                await world.TravelTo(WaypointCatalog.MenagerieHub, $"{talk.Npc}: {talk.Option}");
                return true;
            case "QuestReward" when talk.RewardOffer is { } offer && await world.NpcOfferReward(offer):
                return true;
            // The King's Hand's "Invest in Recruitment" (NPCTalk 3613, category Quests): the row carries no script, so the
            // recruitment panel is opened here. The client draws the row's own text, so the option reads "Invest in
            // Recruitment" and the panel it opens is the RECRUITMENT one (kind 0x55).
            case "Quests" when talk.Option == "Invest in Recruitment":
                await world.NpcClose($"0x0084 close {talk.Npc}'s dialogue for the recruitment panel");
                await player.Send(InstanceWaypoints.OpenUi(Screens["KalguurRecruitmentWindow"]),
                    $"0x007b 0x{Screens["KalguurRecruitmentWindow"]:x2} open the recruitment panel (NPCTalk {talk.Row})");
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
            // S->C 0x7b kind 0x5f with 1. Its refunds come back as C->S 0x36 (per node) and 0x37 (Refund All), paid in gold.
            case "RespecPassivePointsForGold":
                world.NpcEndTalk();
                await player.Send(InstanceWaypoints.OpenUi(InstanceNpcs.PassiveTreeUi, 1), "0x007b open the passive tree (respec)");
                return true;
            // Nessa's Identify Items (script: triggerer.IdentifyItems(), then the IdentifyAll sound): every unidentified item
            // in the main inventory, free. The sound effect is not sent (its message is not mapped).
            case "IdentifyItems":
                await world.NpcIdentifyItems();
                return true;
            // A Kingsmarch screen (script: SendOpenScreen( KalguurTownMapWindow ); triggerer.FinishNPCChat(); ): the dialogue
            // ends and the panel opens. What the panel shows is the village state the server must send (not written yet).
            case var _ when OpenedScreen(talk.Script) is { } screen && Screens.TryGetValue(screen, out byte ui):
                await world.NpcClose($"0x0084 close {talk.Npc}'s dialogue for {screen}");
                await player.Send(InstanceWaypoints.OpenUi(ui), $"0x007b 0x{ui:x2} open {screen} (NPCTalk {talk.Row})");
                // Some of those windows are never built by this client (its village UI element stays null, so 0x7b is a
                // no-op): say what to use instead, so the dialogue does not just end with nothing on screen.
                if (Hints.TryGetValue(ui, out string? hint)) await player.Tell(hint);
                return true;
            default:
                return false;
        }
    });
}
