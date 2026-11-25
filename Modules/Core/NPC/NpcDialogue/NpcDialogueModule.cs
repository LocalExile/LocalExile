using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.NpcDialogue;

/// <summary>What every NPC's dialogue options do: their text, quest rewards, hideout selection, respec and identifying.</summary>
public sealed class NpcDialogueModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module) => GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
    {
        var world = WorldOf(talk.Player);
        switch (talk.Category)
        {
            case "ManageAsyncTrade":
                await global::Game.OpenAsyncTradeStash(talk.Player);
                return true;
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
            case "BanditRespec":
                // Eramir's BanditRespec (Offer 33, QF5=NEVER_SET) is GGG's one-time patch migration respec, not a free
                // dispenser; retail 3.29 bandit respec uses the 20 Orbs of Regret + Amulet vendor recipe for a Book of Reform.
                await talk.ModPlayer.Send(InstanceChat.ChatLine(
                    "To change your bandit favour, vendor 20 Orbs of Regret with an Amber (Oak), Jade (Kraityn), Lapis (Alira), or Onyx (Eramir) Amulet for a Book of Reform."),
                    "0x0017 bandit respec vendor recipe guidance");
                await world.NpcClose("0x0084 close NPC dialogue after bandit respec guidance");
                return true;
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
            // Kirac's Atlas Respecialisation (script: SendOpenScreen( AtlasPassiveTreeScreen, 1 )): the Atlas passive tree
            // in gold-respec mode, S→C 0x7b kind 0x11 with 1. Refunds come back as C→S 0x3f (gold flag) and 0x40 (Refund All).
            case "RespecAtlasPassivePointsForGold":
                world.NpcEndTalk();
                await talk.ModPlayer.Send(InstanceWaypoints.OpenUi(InstanceWaypoints.AtlasUi, 1), "0x007b open the Atlas passive tree (respec)");
                return true;
            // Nessa's Identify Items (script: triggerer.IdentifyItems(), then the IdentifyAll sound): every unidentified item
            // in the main inventory, free. The sound effect is not sent (its message is not mapped).
            case "IdentifyItems":
                await world.NpcIdentifyItems();
                return true;
            default:
                return false;
        }
    });
}
