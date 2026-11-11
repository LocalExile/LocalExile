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
            var (remote, stream, ciphers) = conn.Wire;
            await SpawnQuestRoomObjects(remote, stream, ciphers, conn);
            await SpawnOpeningTutorial(remote, stream, ciphers, conn);
            await SpawnGlyphQuest(remote, stream, ciphers, conn);
            await SpawnTrialObjects(remote, stream, ciphers, conn);
            // The quest engine: quest items synced, entered flags, quest chests and objects, room NPCs, blockers.
            await StartQuestArea(remote, stream, ciphers, conn);
            await TickBeacon(conn);
        });
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => TickBeacon(conn));
        // The quests after a kill's flags: quest drops (Hailrake's Medicine Chest), second forms (Merveil), NPCs that appear
        // or are freed, a fight of several bosses, a cleared area (a1q8).
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.Monster.Variety is not { } variety) return;
            var (remote, stream, ciphers) = kill.Conn.Wire;
            if (kill.CommittedOutput is not null) { CommitAcceptedQuestKill(kill); return; }
            kill.Later(() => QuestKillEffects(remote, stream, ciphers, kill.Conn, kill.Monster, variety, kill.At));
        });
        // After a read that moved items, the quest items' flags (and the removal of those done with: the Medicine Chest once
        // Nessa has one), on the message path so the inventory revisions stay in order.
        GameEvents.MessagesHandled.Add(module.Manifest.Id, async (conn, ids) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            if (conn.PlayerSent && ids.Any(QuestItems.MovesItems)) await SyncQuestItems(remote, stream, ciphers, conn, removeItems: true);
        });
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.Conn.Layout.Id != TutorialObjects.Strand || kill.VarietyId != TutorialObjects.Zombie
                || CharacterSaves.Load(CharacterName(kill.Conn))?.Tutorial is not { ZombieKilled: false }) return;
            CharacterSaves.ChangeTutorial(CharacterName(kill.Conn), t => t with { ZombieKilled = true });
            var (remote, stream, ciphers) = kill.Conn.Wire;
            if (kill.CommittedOutput is not null) kill.Conn.Minions.OutputQueue.AddRange(CommitTutorialGem(kill.Conn, false, kill.At.X, kill.At.Y));
            else kill.Later(() => DropTutorialGem(remote, stream, ciphers, kill.Conn, false, kill.At.X, kill.At.Y));
        });
        // A trial dart's slow on the player's movement speed.
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, WithDartSlow);
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (conn, id, npc) =>
        {
            if (npc.Npc.Id.StartsWith(LabyrinthTrials.GlyphPrefix, StringComparison.Ordinal))
            {
                var (glyphRemote, glyphStream, glyphCiphers) = conn.Wire;
                await ReadTrialMonograph(glyphRemote, glyphStream, glyphCiphers, conn, id, npc);
                return true;
            }
            if (npc.Npc.Id != TutorialObjects.Exile) return false;
            var (remote, stream, ciphers) = conn.Wire;
            await TalkToWoundedExile(remote, stream, ciphers, conn, id, npc);
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
            var (remote, stream, ciphers) = conn.Wire;
            if (conn.TutorialChestId != 0 && conn.TutorialChest is { } chest && Dist((chest.X, chest.Y), at) <= radius)
                await OpenTutorialChestCore(remote, stream, ciphers, conn, conn.TutorialChestId, chest);
            List<(uint Id, GlyphQuest.Nest Nest)> nests;
            lock (conn.GlyphNests)
                nests = [.. conn.GlyphNests.Where(e => Dist((e.Value.At.X, e.Value.At.Y), at) <= radius).Select(e => (e.Key, e.Value))];
            foreach (var (id, nest) in nests) await OpenRhoaNestCore(remote, stream, ciphers, conn, id, nest);
        });
        // A cast near a Labyrinth smashable wall breaks it (not this module's cast: it goes on).
        GameEvents.SkillCast.Add(module.Manifest.Id, (conn, effect, skillId, from, current) =>
        {
            _ = BreakSmashableWalls(conn, from);
            return null;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, requestedAt) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            if (conn.TutorialChestId == use.TargetId && conn.TutorialChest is { } tutorialChest)
            {
                await OpenTutorialChest(remote, stream, ciphers, conn, use, tutorialChest, requestedAt);
                return true;
            }
            (Quests.ChestRule Rule, GlyphQuest.Placement At)? questChest;
            lock (conn.QuestChests) questChest = conn.QuestChests.TryGetValue(use.TargetId, out var qc) ? qc : null;
            if (questChest is var (chestRule, chestAt))
            {
                await OpenQuestChest(remote, stream, ciphers, conn, use, chestRule, chestAt, requestedAt);
                return true;
            }
            (Quests.ObjectRule Rule, GlyphQuest.Placement At, QuestObjectTypes.Info Info)? questObject;
            lock (conn.QuestObjects) questObject = conn.QuestObjects.TryGetValue(use.TargetId, out var qo) ? qo : null;
            if (questObject is var (objectRule, objectAt, objectInfo))
            {
                if (!CanUseBeacon(conn, objectRule.ObjectType)) return true;
                await UseQuestObject(remote, stream, ciphers, conn, use, objectRule, objectAt, objectInfo, requestedAt,
                    objectRule.ObjectType switch
                    {
                        Act3Objects.LabyrinthPlaque => () => QuestobjectsRules.UseSarnPlaque(remote, stream, ciphers, conn),
                        Act3Objects.LabyrinthSelect => () => QuestobjectsRules.UseLabyrinthSelect(remote, stream, ciphers, conn),
                        Act3Objects.ReturnPortal or Act3Objects.ChamberPortal => () => QuestobjectsRules.UsePlazaReturnPortal(remote, stream, ciphers, conn),
                        Act3Objects.AscendancyFont => () => QuestobjectsRules.UseAscendancyFont(remote, stream, ciphers, conn),
                        Act7Objects.Orrery => () => Send(remote, stream, ciphers,
                            InstanceWaypoints.OpenUi(InstanceWaypoints.MapDeviceUi, use.TargetId), "0x007b open the Reverie Device"),
                        _ => null,
                    });
                return true;
            }
            GlyphQuest.Nest? nest;
            lock (conn.GlyphNests) nest = conn.GlyphNests.GetValueOrDefault(use.TargetId);
            if (nest is not null)
            {
                await OpenRhoaNest(remote, stream, ciphers, conn, use, nest);
                return true;
            }
            if (conn.GlyphWallId != 0 && use.TargetId == conn.GlyphWallId)
            {
                await UseGlyphWall(remote, stream, ciphers, conn, use);
                return true;
            }
            // A Labyrinth room's stash: the one clicked is the stash the game opens (it knows one per area).
            if (QuestobjectsRules.PickLabyrinthStash(conn, use.TargetId)) return false;
            return await UseTrialObject(remote, stream, ciphers, conn, use);
        });
    }
}
