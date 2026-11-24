using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;
using static QuestObjectsFeature;

namespace GameServer.Modules.M_questobjects;

/// <summary>The campaign's quests (Agents/docs/game/campaign-quests.md): the quest engine's tables, one per act (Acts/), which
/// it registers while loaded, the engine's hooks on entering an area, a kill, a click on a quest object and a read of item
/// moves (the engine itself is the Game module's World/Quest/), the opening tutorial, its gems, the Glyph quest and the
/// Trials of Ascendancy.</summary>
public sealed class QuestObjectsModule : IModule
{
    static readonly string[] LegacyClarissaFlags =
    [
        "a3q1-QuestRewardTaken", "a3q1-Congratulated", "a3q2-DeliveredItem", "a3q2-Congratulated",
        "a3q2-QuestRewardSeen", "ClarissaChatTolman", "ClarissaChatTolmanItem",
        "a3q11-HaveSewerKeys", "a3q11-UsedSewerKeys", "a3q3-DiallaIntroduced",
        "a3q5-UsedTalc", "a3q9-KilledPiety", "a3q10-KilledDominus",
    ];

    /// <summary>Every act of this assembly (each class deriving <see cref="Quests.Act"/>, one per file in Acts/), in order.</summary>
    internal static IReadOnlyList<Quests.Act> Acts() =>
        [.. typeof(QuestObjectsModule).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Quests.Act)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (Quests.Act)Activator.CreateInstance(t)!).OrderBy(a => a.Number)];

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.NpcUsesLeagueFlags.Add(module.Manifest.Id, (_, npc) => npc.Id is
            "Metadata/NPC/Missions/Hideout/Helena" or "Metadata/NPC/League/Delve/DelveMinerHub"
            or "Metadata/NPC/League/Bestiary/EinharMenagerie");
        var acts = Acts();
        // The tables run for the leagues that list this module (Quests.For): they are registered under its id.
        module.Lifetime.Own(Quests.Register(module.Manifest.Id, acts));
        GameEvents.Packet(InstanceNpcs.VoteAnswerId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            if (bytes.Length >= InstanceNpcs.VoteAnswerLength && bytes[2] <= 1) await BanditChoices.VoteAnswer(conn, bytes[2] == 1);
            return true;
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) => BanditChoices.OnPlayerLeaving(conn, why));
        Console.WriteLine($"[gateway] quest engine: {string.Join(", ", acts)}");
        // The player's quest view: what their quest state shows them (the walls and blockers they have not opened, quest
        // objects and chests as they left them, NPCs, the opening tutorial) is theirs alone to see and to bump into.
        GameEvents.PlayerEntered.Add(module.Manifest.Id, async player =>
        {
            Program.ViewOf.Value = player;
            try
            {
                await SpawnQuestRoomObjects(player);
                await SpawnOpeningTutorial(player);
                await SpawnGlyphQuest(player);
                await SpawnTrialObjects(player);
                // Existing saves that spoke to HelenaTown before she was gated have HelenaIntroduced without a2q6-HelenaInTown,
                // leaving Greust's reward unavailable after a2q9-PoisonedTree blocked Baleful Gem drops.
                if (player.Layout.Id == Act2Areas.ForestEncampment && ClientHasQuestFlag(player, "HelenaIntroduced")
                    && !ClientHasQuestFlag(player, "a2q6-HelenaInTown"))
                    await SetQuestFlags(player, ["a2q6-HelenaInTown"]);
                // Existing saves that entered Sarn Encampment or progressed past a3q1 before ClarissaTown was gated on
                // a3q1-RescuedGirl still need her present in town so they cannot soft-lock on Sewer Keys or Tolman.
                if (player.Layout.Id == Act3Areas.SarnEncampment && !ClientHasQuestFlag(player, "a3q1-RescuedGirl")
                    && LegacyClarissaFlags.Any(f => ClientHasQuestFlag(player, f)))
                    await SetQuestFlags(player, ["a3q1-RescuedGirl"]);
                // The quest engine: quest items synced, entered flags, quest chests and objects, room NPCs, blockers.
                await StartQuestArea(player);
                await SpawnLoreObjects(player);
                await SpawnTrickChests(player);
                await TickBeacon(player);
            }
            finally { Program.ViewOf.Value = null; }
            await BanditChoices.OnAreaGenerated(player);
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.InArea(Act6Areas.Beacon, TickBeacon));
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.InArea(TutorialObjects.Strand, SyncTutorialWalls));
        // The quests after a kill's flags: quest drops (Hailrake's Medicine Chest), second forms (Merveil), NPCs that appear
        // or are freed, a fight of several bosses, a cleared area (a1q8).
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.Monster.Variety is not { } variety) return;
            var player = kill.Player;
            if (kill.Player.Layout.Id == Act5Areas.ChamberOfInnocence && variety.Id == Act5Monsters.Innocence
                && kill.Player.Instance.SetState(Act5Objects.ShortcutOpen))
            {
                var instance = kill.Player.Instance;
                foreach (var peer in instance.PlayersExcept(kill.Player.Id).Prepend(kill.Player))
                    peer.KillShares.Enqueue(async _ =>
                    {
                        if (peer.Closed || peer.LeavingArea || peer.Instance != instance) return;
                        UpdateQuestBlockers(peer);
                        await UpdateQuestObjectStates(peer, [Act5Objects.ShortcutOpen]);
                        await UpdateShownTransitions(peer, "Innocence defeated");
                    });
            }
            if (kill.CommittedOutput is not null) { CommitAcceptedQuestKill(kill); return; }
            kill.Later(() => QuestKillEffects(kill.Player, kill.Monster, variety, kill.At));
        });
        // After a read that moved items, the quest items' flags (and the removal of those done with: the Medicine Chest once
        // Nessa has one), on the message path so the inventory revisions stay in order.
        GameEvents.MessagesHandled.Add(module.Manifest.Id, async (player, ids) =>
        {
            if (player.Connection.PlayerSent && ids.Any(QuestItems.MovesItems)) await SyncQuestItems(player, removeItems: true);
            await SyncTutorialWalls(player);
        });
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.Player.Layout.Id != TutorialObjects.Strand || kill.VarietyId != TutorialObjects.Zombie
                || CharacterSaves.Load(CharacterName(kill.Player))?.Tutorial is not { ZombieKilled: false }) return;
            CharacterSaves.ChangeTutorial(CharacterName(kill.Player), t => t with { ZombieKilled = true });
            var player = kill.Player;
            if (kill.CommittedOutput is not null) kill.Player.Minions.OutputQueue.AddRange(CommitTutorialGem(kill.Player, false, kill.At.X, kill.At.Y));
            else kill.Later(() => DropTutorialGem(kill.Player, false, kill.At.X, kill.At.Y));
        });
        // A trial dart's slow on the player's movement speed.
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, WithDartSlow);
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (player, id, npc) =>
        {
            if (IsLoreType(npc.Npc.Id))
            {
                return await ReadLoreObject(player, id, npc);
            }
            if (npc.Npc.Id != TutorialObjects.Exile) return false;
            await TalkToWoundedExile(player, id, npc);
            return true;
        });
        GameEvents.NpcDialogueClosing.Add(module.Manifest.Id, player =>
        {
            TutorialObjects.SkipDialogue(player);
            return Task.CompletedTask;
        });
        // Deathrattle: a warcry opens the quest chests within its radius.
        GameEvents.ChestsOpened.Add(module.Manifest.Id, async (player, at, radius) =>
        {
            if (player.TutorialChestId != 0 && player.TutorialChest is { } chest && Dist((chest.X, chest.Y), at) <= radius)
                await OpenTutorialChestCore(player, player.TutorialChestId, chest);
            List<(uint Id, GlyphQuest.Nest Nest)> nests;
            lock (player.GlyphNests)
                nests = [.. player.GlyphNests.Where(e => Dist((e.Value.At.X, e.Value.At.Y), at) <= radius).Select(e => (e.Key, e.Value))];
            foreach (var (id, nest) in nests) await OpenRhoaNestCore(player, id, nest);
            await OpenTrickChestsNear(player, at, radius);
        });
        // A cast near a Labyrinth smashable wall breaks it (not this module's cast: it goes on).
        GameEvents.SkillCast.Add(module.Manifest.Id, (player, effect, skillId, from, current) =>
        {
            _ = BreakSmashableWalls(player, from);
            return null;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, requestedAt) =>
        {
            if (player.TutorialChestId == use.TargetId && player.TutorialChest is { } tutorialChest)
            {
                await OpenTutorialChest(player, use, tutorialChest, requestedAt);
                return true;
            }
            (Quests.ChestRule Rule, GlyphQuest.Placement At)? questChest;
            lock (player.QuestChests) questChest = player.QuestChests.TryGetValue(use.TargetId, out var qc) ? qc : null;
            if (questChest is var (chestRule, chestAt))
            {
                await OpenQuestChest(player, use, chestRule, chestAt, requestedAt);
                return true;
            }
            (Quests.ObjectRule Rule, GlyphQuest.Placement At, QuestObjectTypes.Info Info)? questObject;
            lock (player.QuestObjects) questObject = player.QuestObjects.TryGetValue(use.TargetId, out var qo) ? qo : null;
            if (questObject is var (objectRule, objectAt, objectInfo))
            {
                if (!CanUseBeacon(player, objectRule.ObjectType)) return true;
                await UseQuestObject(player, use, objectRule, objectAt, objectInfo, requestedAt,
                    objectRule.ObjectType switch
                    {
                        Act3Objects.LabyrinthPlaque => () => QuestobjectsRules.UseSarnPlaque(player),
                        Act3Objects.LabyrinthSelect => () => QuestobjectsRules.UseLabyrinthSelect(player),
                        Act3Objects.ReturnPortal or Act3Objects.ChamberPortal => () => QuestobjectsRules.UsePlazaReturnPortal(player),
                        Act3Objects.AscendancyFont => () => QuestobjectsRules.UseAscendancyFont(player),
                        Act7Objects.Orrery => () => Send(player,
                            InstanceWaypoints.OpenUi(InstanceWaypoints.MapDeviceUi, use.TargetId), "0x007b open the Reverie Device"),
                        _ => null,
                    });
                return true;
            }
            GlyphQuest.Nest? nest;
            lock (player.GlyphNests) nest = player.GlyphNests.GetValueOrDefault(use.TargetId);
            if (nest is not null)
            {
                await OpenRhoaNest(player, use, nest);
                return true;
            }
            if (player.GlyphWallId != 0 && use.TargetId == player.GlyphWallId)
            {
                await UseGlyphWall(player, use);
                return true;
            }
            // A Labyrinth room's stash: the one clicked is the stash the game opens (it knows one per area).
            if (QuestobjectsRules.PickLabyrinthStash(player, use.TargetId)) return false;
            if (await UseTrickChest(player, use, requestedAt)) return true;
            return await UseTrialObject(player, use);
        });
    }
}
