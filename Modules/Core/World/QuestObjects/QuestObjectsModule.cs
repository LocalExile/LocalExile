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
    /// <summary>Every act of this assembly (each class deriving <see cref="Quests.Act"/>, one per file in Acts/), in order.</summary>
    internal static IReadOnlyList<Quests.Act> Acts() =>
        [.. typeof(QuestObjectsModule).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Quests.Act)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (Quests.Act)Activator.CreateInstance(t)!).OrderBy(a => a.Number)];

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var acts = Acts();
        // The tables run for the leagues that list this module (Quests.For): they are registered under its id.
        module.Lifetime.Own(Quests.Register(module.Manifest.Id, acts));
        Console.WriteLine($"[gateway] quest engine: {string.Join(", ", acts)}");
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            await SpawnQuestRoomObjects(conn);
            await SpawnOpeningTutorial(conn);
            await SpawnGlyphQuest(conn);
            await SpawnTrialObjects(conn);
            // The quest engine: quest items synced, entered flags, quest chests and objects, room NPCs, blockers.
            await StartQuestArea(conn);
            await TickBeacon(conn);
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.InArea(Act6Areas.Beacon, TickBeacon));
        // The quests after a kill's flags: quest drops (Hailrake's Medicine Chest), second forms (Merveil), NPCs that appear
        // or are freed, a fight of several bosses, a cleared area (a1q8).
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.Monster.Variety is not { } variety) return;
            var conn = kill.Conn;
            if (kill.Conn.Layout.Id == Act5Areas.ChamberOfInnocence && variety.Id == Act5Monsters.Innocence
                && kill.Conn.Instance.SetState(Act5Objects.ShortcutOpen))
            {
                var instance = kill.Conn.Instance;
                foreach (var player in instance.PlayersExcept(kill.Conn.PeerId).Select(p => p.Owner).OfType<ClientState>().Prepend(kill.Conn))
                    player.KillShares.Enqueue(async _ =>
                    {
                        if (player.Closed || player.LeavingArea || player.Instance != instance) return;
                        lock (player.Monsters) UpdateQuestBlockers(player);
                        await UpdateQuestObjectStates(player, [Act5Objects.ShortcutOpen]);
                        await UpdateShownTransitions(player, "Innocence defeated");
                    });
            }
            if (kill.CommittedOutput is not null) { CommitAcceptedQuestKill(kill); return; }
            kill.Later(() => QuestKillEffects(kill.Conn, kill.Monster, variety, kill.At));
        });
        // After a read that moved items, the quest items' flags (and the removal of those done with: the Medicine Chest once
        // Nessa has one), on the message path so the inventory revisions stay in order.
        GameEvents.MessagesHandled.Add(module.Manifest.Id, async (conn, ids) =>
        {
            if (conn.PlayerSent && ids.Any(QuestItems.MovesItems)) await SyncQuestItems(conn, removeItems: true);
        });
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.Conn.Layout.Id != TutorialObjects.Strand || kill.VarietyId != TutorialObjects.Zombie
                || CharacterSaves.Load(CharacterName(kill.Conn))?.Tutorial is not { ZombieKilled: false }) return;
            CharacterSaves.ChangeTutorial(CharacterName(kill.Conn), t => t with { ZombieKilled = true });
            var conn = kill.Conn;
            if (kill.CommittedOutput is not null) kill.Conn.Minions.OutputQueue.AddRange(CommitTutorialGem(kill.Conn, false, kill.At.X, kill.At.Y));
            else kill.Later(() => DropTutorialGem(kill.Conn, false, kill.At.X, kill.At.Y));
        });
        // A trial dart's slow on the player's movement speed.
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, WithDartSlow);
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (conn, id, npc) =>
        {
            if (LabyrinthTrials.IsGlyph(npc.Npc.Id))
            {
                await ReadTrialMonograph(conn, id, npc);
                return true;
            }
            if (npc.Npc.Id != TutorialObjects.Exile) return false;
            await TalkToWoundedExile(conn, id, npc);
            return true;
        });
        GameEvents.NpcDialogueClosing.Add(module.Manifest.Id, conn =>
        {
            TutorialObjects.SkipDialogue(conn);
            return Task.CompletedTask;
        });
        // Deathrattle: a warcry opens the quest chests within its radius.
        GameEvents.ChestsOpened.Add(module.Manifest.Id, async (conn, at, radius) =>
        {
            if (conn.TutorialChestId != 0 && conn.TutorialChest is { } chest && Dist((chest.X, chest.Y), at) <= radius)
                await OpenTutorialChestCore(conn, conn.TutorialChestId, chest);
            List<(uint Id, GlyphQuest.Nest Nest)> nests;
            lock (conn.GlyphNests)
                nests = [.. conn.GlyphNests.Where(e => Dist((e.Value.At.X, e.Value.At.Y), at) <= radius).Select(e => (e.Key, e.Value))];
            foreach (var (id, nest) in nests) await OpenRhoaNestCore(conn, id, nest);
        });
        // A cast near a Labyrinth smashable wall breaks it (not this module's cast: it goes on).
        GameEvents.SkillCast.Add(module.Manifest.Id, (conn, effect, skillId, from, current) =>
        {
            _ = BreakSmashableWalls(conn, from);
            return null;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, requestedAt) =>
        {
            if (conn.TutorialChestId == use.TargetId && conn.TutorialChest is { } tutorialChest)
            {
                await OpenTutorialChest(conn, use, tutorialChest, requestedAt);
                return true;
            }
            (Quests.ChestRule Rule, GlyphQuest.Placement At)? questChest;
            lock (conn.QuestChests) questChest = conn.QuestChests.TryGetValue(use.TargetId, out var qc) ? qc : null;
            if (questChest is var (chestRule, chestAt))
            {
                await OpenQuestChest(conn, use, chestRule, chestAt, requestedAt);
                return true;
            }
            (Quests.ObjectRule Rule, GlyphQuest.Placement At, QuestObjectTypes.Info Info)? questObject;
            lock (conn.QuestObjects) questObject = conn.QuestObjects.TryGetValue(use.TargetId, out var qo) ? qo : null;
            if (questObject is var (objectRule, objectAt, objectInfo))
            {
                if (!CanUseBeacon(conn, objectRule.ObjectType)) return true;
                await UseQuestObject(conn, use, objectRule, objectAt, objectInfo, requestedAt,
                    objectRule.ObjectType switch
                    {
                        Act3Objects.LabyrinthPlaque => () => QuestobjectsRules.UseSarnPlaque(conn),
                        Act3Objects.LabyrinthSelect => () => QuestobjectsRules.UseLabyrinthSelect(conn),
                        Act3Objects.ReturnPortal or Act3Objects.ChamberPortal => () => QuestobjectsRules.UsePlazaReturnPortal(conn),
                        Act3Objects.AscendancyFont => () => QuestobjectsRules.UseAscendancyFont(conn),
                        Act7Objects.Orrery => () => Send(conn,
                            InstanceWaypoints.OpenUi(InstanceWaypoints.MapDeviceUi, use.TargetId), "0x007b open the Reverie Device"),
                        _ => null,
                    });
                return true;
            }
            GlyphQuest.Nest? nest;
            lock (conn.GlyphNests) nest = conn.GlyphNests.GetValueOrDefault(use.TargetId);
            if (nest is not null)
            {
                await OpenRhoaNest(conn, use, nest);
                return true;
            }
            if (conn.GlyphWallId != 0 && use.TargetId == conn.GlyphWallId)
            {
                await UseGlyphWall(conn, use);
                return true;
            }
            // A Labyrinth room's stash: the one clicked is the stash the game opens (it knows one per area).
            if (QuestobjectsRules.PickLabyrinthStash(conn, use.TargetId)) return false;
            return await UseTrialObject(conn, use);
        });
    }
}
