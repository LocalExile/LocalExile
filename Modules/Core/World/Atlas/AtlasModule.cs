using System.Net.Sockets;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>The Atlas passive tree's choices and the Atlas's completed maps.</summary>
public sealed class AtlasModule : IModule
{
    /// <summary>Maximum passive points from map bonus objectives in the current Atlas tree.</summary>
    internal const int MapCompletionPoints = 100;

    internal static int ApplyBonusPoints(Player player, ISavedData data)
    {
        int before = player.Character.AtlasPassivePoints;
        int total = Math.Min(MapCompletionPoints, AtlasProgress.BonusCompleted(data).Count);
        if (total <= before) return 0;
        player.Character.AtlasPassivePoints = (ushort)total;
        if (CharacterName(player) is { } character)
            CharacterSaves.SetAtlasPassives(character, player.Character.AtlasPassives, player.Character.AtlasPassivePoints);
        return total - before;
    }

    static async Task PublishBonusPoints(Player player, IPlayer modPlayer, int earned, string label)
    {
        await AtlasFeature.SyncTreeFlags(player);
        if (earned == 0) return;
        await global::Game.SendAtlasSlots(player, label);
        await Send(player, InstanceProtocol.GameMessage(
            InstanceProtocol.GameMessageSource.BackendError,
            earned == 1 ? InstanceProtocol.BackendErrorId.ReceivedAtlasPassivePoint1 : InstanceProtocol.BackendErrorId.ReceivedAtlasPassivePointN,
            numericArgs: earned == 1 ? null : [(0, earned)]),
            $"0x0018 Atlas passive point(s) earned: +{earned} ({player.Character.AtlasPassivePoints}/{MapCompletionPoints})");
    }

    static IEnumerable<Player> InstanceClients(Player player)
    {
        var peers = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance)
            && c.Connection.PlayerSent && !c.LeavingArea && !c.Closed).ToList();
        return peers.Contains(player) ? peers : [player, .. peers];
    }

    static async Task CompleteMapOnBossKill(Player player, MonsterInfo monster)
    {
        if (AtlasProgress.OfArea(PlayerOf(player).AreaId) is not { } node
            || !AtlasProgress.CompletesMap(node, monster, player.Area)) return;
        await AtlasFeature.DropScryingOrbOnCompletion(player, monster, node);
        foreach (var peer in InstanceClients(player))
        {
            try
            {
                var modPeer = PlayerOf(peer);
                var data = WorldOf(modPeer).CharacterData;
                bool completed = AtlasProgress.Complete(data, [node]);
                bool bonus = global::Game.MapRunOf(peer) is { Atlas: true } run && AtlasProgress.MeetsBonusObjective(run)
                    && AtlasProgress.CompleteBonus(data, [node]);
                int earned = bonus ? ApplyBonusPoints(peer, data) : 0;
                if (completed)
                {
                    await Send(peer, AtlasProgress.NodeMessage(node, bonus),
                        $"0x01a4 Atlas map completed: {node.Name} ({node.Id}) by {monster.Name}");
                    await AtlasFeature.Refresh(peer);
                    await modPeer.Tell($"Atlas: {node.Name} completed.");
                }
                else if (bonus)
                    await AtlasFeature.Refresh(peer);
                if (bonus) await PublishBonusPoints(peer, modPeer, earned, "Atlas bonus objective completed");
                else if (completed) await AtlasFeature.SyncTreeFlags(peer);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        AtlasProgress.LoadAtlasProgressData();
        AtlasFeature.LoadAtlasFeatureData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        ExtraPlayerMessages = player => AtlasFeature.Restore(player);
        GameEvents.MapOpening.Add(module.Manifest.Id, AtlasProgress.ApplyScry);
        GameEvents.EnterMessages.Add(module.Manifest.Id, player => AtlasFeature.PrimordialEnterMessages(player));
        GameEvents.PlayerEntered.Add(module.Manifest.Id, player => AtlasFeature.EnsureUnlocked(player));
        // Once the player is in the area (AreaGenerated may come before the player is sent).
        GameEvents.AreaTick.Add(module.Manifest.Id, _ =>
        {
            bool done = false;
            return async (player, _) => { if (done || !player.Connection.PlayerSent) return; done = true; await AtlasFeature.EnsureUnlocked(player); };
        });
        GameEvents.Command(module.Manifest.Id, "atlas_unlock", "/atlas_unlock (dev: grant Atlas beacons, invitations and primordial keys)", async (player, args) =>
        {
            int n = await AtlasFeature.EnsureUnlocked(player);
            await PlayerOf(player).Tell(n == 0 ? "Atlas: beacons and invitations already unlocked." : $"Atlas: granted {n} beacon/invitation flag(s).");
        });
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill => CompleteMapOnBossKill(kill.Player, kill.Monster));
        // Minion, party, and non-playerKill deaths emit GameEvents.Kill without MonsterKilled.
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (kill.PlayerKill) return;
            if (AtlasProgress.OfArea(kill.Player.Layout.Id) is not { } node
                || !node.FinalBosses.Contains(kill.VarietyId)) return;
            var monster = global::Game.ModMonster(kill.Monster, kill.At);
            if (!AtlasProgress.CompletesMap(node, monster, kill.Player.Area)) return;
            kill.Later(() => CompleteMapOnBossKill(kill.Player, monster));
        });
        GameEvents.Command(module.Manifest.Id, "atlas_complete", "/atlas_complete <map name or AtlasNode id | all> (dev: mark Atlas maps completed, saved)",
            async (player, args) =>
            {
                string what = string.Join(' ', args);
                IReadOnlyList<AtlasProgress.Node> nodes = what.Equals("all", StringComparison.OrdinalIgnoreCase) ? AtlasProgress.Maps
                    : AtlasProgress.Find(what) is { } found ? [found] : [];
                if (nodes.Count == 0)
                {
                    await PlayerOf(player).Tell($"No Atlas map '{what}'. Usage: /atlas_complete <map name or AtlasNode id | all>");
                    return;
                }
                var data = WorldOf(player).CharacterData;
                AtlasProgress.Complete(data, nodes);
                AtlasProgress.CompleteBonus(data, nodes);
                int earned = ApplyBonusPoints(player, data);
                if (nodes.Count == 1)
                    await PlayerOf(player).Send(AtlasProgress.NodeMessage(nodes[0]),
                        $"0x01a4 /atlas_complete {nodes[0].Name} ({nodes[0].Id})");
                await PlayerOf(player).Tell(nodes.Count == 1 ? $"Atlas: {nodes[0].Name} completed." : $"Atlas: {nodes.Count} maps completed.");
                await PublishBonusPoints(player, PlayerOf(player), earned, "/atlas_complete");
            });
        GameEvents.Command(module.Manifest.Id, "atlas_reset", "/atlas_reset (dev: clear the Atlas's completed maps)", async (player, args) =>
        {
            AtlasProgress.Reset(WorldOf(player).CharacterData);
            await AtlasFeature.SyncTreeFlags(player);
            var slots = AtlasFeature.SlotsOf(PlayerOf(player));
            await PlayerOf(player).Send(AtlasProgress.EmptyMessage(AtlasProgress.VoidstoneMask(slots), (byte)slots.Count),
                "0x01a3 /atlas_reset");
            await PlayerOf(player).Tell("Atlas: progress cleared.");
        });
        GameEvents.Packet(InstanceEntities.AtlasAllocateId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            var (preset, node) = InstanceEntities.ReadAtlasAllocate(bytes);
            await AtlasFeature.ChangeAtlasPassive(player, preset, node, allocate: true);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasRefundId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            var (preset, node, goldFlag) = InstanceEntities.ReadAtlasRefund(bytes);
            await AtlasFeature.ChangeAtlasPassive(player, preset, node, allocate: false, gold: goldFlag != 0);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasRefundAllId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await AtlasFeature.RefundAllAtlasPassives(player, bytes[2]);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasResetAllId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await AtlasFeature.ResetAtlasPassivesFree(player, bytes[2]);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasTreeTabId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await AtlasFeature.SelectPreset(player, bytes[2]);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasTreeRenameId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await AtlasFeature.RenameAtlasPreset(player, InstanceEntities.ReadAtlasTreeRename(bytes));
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasWindowRequestId).Add(module.Manifest.Id, async (player, _) =>
        {
            await Send(player, InstanceEntities.AtlasWindowReply(), "0x0091 Atlas window reply");
            if (AtlasFeature.VaultPassMessage(player) is { } pass)
                await Send(player, pass, "0x012a Atlas Vault Pass");
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.AtlasScryId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceMapDevice.AtlasScryLength) return false;
            await AtlasFeature.ScrySwap(player, InstanceMapDevice.ReadAtlasScry(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.UseItemOnAtlasNodeId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceMapDevice.UseItemOnAtlasNodeLength) return false;
            await AtlasFeature.UseItemOnNode(player, InstanceMapDevice.ReadUseItemOnAtlasNode(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.UseItemTypeOnAtlasNodeId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceMapDevice.UseItemTypeOnAtlasNodeLength) return false;
            await AtlasFeature.UseItemTypeOnNode(player, InstanceMapDevice.ReadUseItemTypeOnAtlasNode(bytes));
            return true;
        });
        GameEvents.Command(module.Manifest.Id, "atlas_scry", "/atlas_scry (opens the Atlas in scrying mode)",
            async (player, _) => await AtlasFeature.OpenScrying(player, 0));
        GameEvents.Packet(InstanceMapDevice.SocketId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await AtlasFeature.SocketVoidstone(player,
                InstanceMapDevice.ReadSocket(bytes).ItemId);
            return true;
        });
        // Taking a socketed stone off is C→S 0x28 from inventory 40, not 0xf6. Clicking a Voidstone in the bag
        // (without Ctrl) is also 0x28; the Atlas window does not send 0xf6 unless Ctrl is held, so that click
        // has to socket the stone itself. Dropping onto a socket is C→S 0x2a into inventory 40.
        GameEvents.Packet(InstanceItems.PickUpItemId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            return await AtlasFeature.PickVoidstone(player,
                InstanceItems.ReadPickUpItem(bytes));
        });
        GameEvents.InventoryPlace.Add(module.Manifest.Id, async (player, place) =>
        {
            if (place.InventoryId != PlayerInventories.InventoryId(PlayerInventories.AtlasUpgrades)) return false;
            var held = ItemsOf(player).Items(PlayerInventories.Cursor).FirstOrDefault();
            if (held is null || ItemsFor(player).ByHash(held.Item.TypeHash) is not { } type
                || MapBossFights.Current.StoneByItem(type.Id) is null)
                return false;
            await AtlasFeature.SocketVoidstone(player, held.Id);
            return true;
        });
        GameEvents.NpcQuestConversation.Add(module.Manifest.Id, AtlasFeature.KiracStarterConversation);
        // Kirac's invitation rows FinishNPCChat + DispatchRulesetEvent("open_maven_invitation …"). The chat end ran
        // with no item. Hand the matching MavenFights invitation into the bag first; the empty FinishNPCChat still closes.
        GameEvents.NpcOptionPicked.Add(module.Manifest.Id, async (player, talk) =>
        {
            if (AtlasFeature.KiracHeardFlags(player, talk) is { Count: > 0 } heard)
                await SetLeagueQuestFlags(player, heard);
            await AtlasFeature.GiveMavenInvitation(player, talk.Script);
            if ((talk.Script is not null && talk.Script.Contains("scry", StringComparison.OrdinalIgnoreCase))
                || string.Equals(talk.Option, "Scry a Map", StringComparison.OrdinalIgnoreCase))
                await AtlasFeature.OpenScrying(player, player.TalkingTo);
            return true;
        });
    }
}

public static class AtlasFeature
{
    /// <summary>Saved QuestFlags only. SessionQuestFlags (PickUpMavenAlteration, HaveTangleMapDeviceAlteration, …)
    /// are never written to the save: granting them on every area re-fires the client's "beacon complete" toast.</summary>
    internal static readonly string[] UnlockFlags =
    [
        "HaveMavenMapDeviceAlteration",
        "HaveMavenMapVoid1", "HaveMavenMapVoid2", "HaveMavenMapVoid3", "HaveMavenMapVoid4", "HaveMavenMapVoid5",
        "HaveMavenMapVoid6",
        "HaveMavenMapAtlas1", "HaveMavenMapAtlas2", "HaveMavenMapAtlas3", "HaveMavenMapAtlas4", "HaveMavenMapAtlas5",
        "MavenFirstMapBossCapture", "MavenFirstInvitation", "MavenFirstVoidBossCapture",
        "SeenMavenBoss", "MavenMapDropped", "Met-EnvoyMaven", "Met-EnvoyEater", "Met-EnvoyExarch",
        "HaveTangleKey1", "HaveTangleKey2",
        "HaveCleansingFireKey1", "HaveCleansingFireKey2",
        "CleansingBossDefeated", "ConsumeBossDefeated", "MavenEnragedDefeated",
        "KiracOnTheMapDeviceSeen", "KiracOnTangleLureSeen", "KiracOnCleansingFireLureSeen",
        "KiracOnCleansingFireKey1Seen", "KiracOnCleansingFireKey2Seen",
        "KiracOnTangleKey1Seen", "KiracOnTangleKey2Seen",
        "ShaperDefeated", "ElderDefeated", "SirusDefeated",
    ];

    /// <summary>Map-device beacons and Maven invitation gates (Player +0x1d0, bit = id − 1). Applied onto
    /// <see cref="Player.LeagueQuestFlags"/> before the player object is built so a later 0x02bc does not toast.
    /// AtlasMavenFightComplete1–4 (979–982) make Kirac's "Invitation: The Atlas" the fifth fight; 885 MavenBossComplete
    /// is omitted so the Nucleus still has its later phases.</summary>
    internal static readonly int[] UnlockLeagueFlags =
    [
        6, 7,                         // Unlocked5thMapDeviceSlot, Unlocked6thMapDeviceSlot
        225, 351, 352,                // MapDeviceMavenUnlocked, MapDeviceTangleUnlocked, MapDeviceCleansingFireUnlocked
        219, 220, 221, 222, 708, 709, // PrimordialWatchstoneUnlocked Maven / UberElder / Tangled / CleansingFire + QuestRewards#111 Eldritch / Originator
        290, 607,                     // VoidstoneSocketed, PickedUpVoidstone
        1019,                         // AtlasMavenCollectedEnoughMaps
        1100, 1101, 1102, 1103, 1104, 1161, // VoidMavenCollectedEnoughMaps1–6
        979, 980, 981, 982,           // AtlasMavenFightComplete1–4 → Kirac offers Atlas5
        1075, 426,                    // MavenBossUnlocked, MavenVoidQuestUnlocked
    ];

    /// <summary>Previously granted in error: they skip Maven/Kirac phases or toast on every area.</summary>
    internal static readonly int[] StripLeagueFlags = [885, 374, 212, 328, 317, 318, 347, 319];

    static readonly Dictionary<string, int> MavenInvitationItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Void1"] = 4125, ["Void2"] = 4126, ["Void3"] = 4127, ["Void4"] = 4128, ["Void5"] = 4129, ["Void6"] = 4130,
        ["Atlas1"] = 4131, ["Atlas2"] = 4132, ["Atlas3"] = 4133, ["Atlas4"] = 4134, ["Atlas5"] = 4136,
    };

    static readonly Regex OpenMavenInvitation = new(@"open_maven_invitation\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async Task<bool?> KiracStarterConversation(Player player, uint id, SpawnedNpc npc)
    {
        const int tookStarterMap = 216, enteredMap = 1037;
        const int introductionOption = 3;
        var flags = player.Character.LeagueQuestFlags;
        if (player.Layout.Id != "2_11_endgame_town" || npc.Npc.Id != "Metadata/NPC/Epilogue/Kirac"
            || !player.Character.HasQuestFlag(Quests.EndGameAvailable)
            || flags.Contains(tookStarterMap) || flags.Contains(enteredMap)) return null;

        // NPCTalk 2257 has a start flag but is categorized as Gossip. The next rows, 2258 and 2297,
        // have the manual-start sentinel. Leave their choices visible before the pre-granted Atlas
        // flags can advance unrelated automatic Elder and Maven conversations.
        if (global::Game.Npcs.TalkOf(npc.Npc, introductionOption) is { StartLeagueFlag: int introduced } introduction
            && !flags.Contains(introduced))
        {
            await global::Game.ExecuteQuestConversation(player, id, npc, introduction);
            if (player.InNpcTalk) return true;
        }
        return false;
    }

    // Kirac's rows (NPCs.dat 410, also the hideout Kirac's) mark their conversation heard with league quest flags
    // (NPCTalk column 15), which open his next ones. Reward rows are left to their reward offer.
    internal static List<int> KiracHeardFlags(Player player, NpcCatalog.Talk talk)
    {
        if (talk.Npc != 410 || talk.RewardOffer is not null) return [];
        return [.. talk.HeardLeagueFlags.Where(f => !player.Character.LeagueQuestFlags.Contains(f))];
    }

    internal static async Task<int> EnsureUnlocked(Player player)
    {
        var missing = UnlockFlags.Where(f => !ClientHasQuestFlag(player, f)).ToList();
        bool leagueChanged = ApplyLeagueFlags(player);
        if (!player.Connection.PlayerSent) return missing.Count + (leagueChanged ? 1 : 0);
        int n = 0;
        if (missing.Count > 0)
        {
            await SetQuestFlags(player, missing);
            n += missing.Count;
        }
        if (leagueChanged)
        {
            await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.PlayerLeagueQuestFlagsUpdate(player.Id, Quests.League.Bits(player.Character.LeagueQuestFlags)),
                "0x02bc Atlas league flags");
            n++;
        }
        return n;
    }

    /// <summary>One 0x1a3 before 0x21: the sets replace without a completion event. 0x1a4 is only for a new completion.
    /// League flags are put on the connection here, before <c>PlayerForCreation</c> reads them.</summary>
    internal static IEnumerable<(string Log, byte[] Message)> Restore(Player player)
    {
        ApplyLeagueFlags(player);
        ApplyTreeFlags(player);
        var modPlayer = PlayerOf(player);
        var data = WorldOf(modPlayer).CharacterData;
        var completed = AtlasProgress.Completed(data);
        var bonusCompleted = AtlasProgress.BonusCompleted(data);
        bool pointsChanged = AtlasModule.ApplyBonusPoints(player, data) > 0;
        var slots = SlotsOf(modPlayer);
        byte stones = AtlasProgress.VoidstoneMask(slots);
        yield return ("0x003c Atlas tree names", global::Game.AtlasTreeNamesMessage(player));
        yield return ($"0x01a3 Atlas progress, {completed.Count} map(s) completed, {bonusCompleted.Count} bonus, voidstones 0x{stones:X2} ({slots.Count})",
            AtlasProgress.FullMessage(completed, bonusCompleted, stones, (byte)slots.Count, AtlasProgress.ScryPairs(data)));
        // The presets in the player batch were built before the budget above changed: send them again so the tree's
        // counter follows on this login.
        if (pointsChanged)
            foreach (var message in global::Game.AtlasPresetMessages(player, "after the map points"))
                yield return message;
    }

    /// <summary>Completed bonus objectives needed for Atlas trees I, II and III.</summary>
    internal static readonly int[] TreeUnlockMaps = [0, 50, 100];

    /// <summary>AtlasTrees' flag columns for trees II and III: Unlocked (329, 331) and Active (330, 332).</summary>
    static readonly (int Unlocked, int Active)[] TreeFlags = [(0, 0), (329, 330), (331, 332)];

    /// <summary>LeagueQuestFlags id 295 (wire bit 294): zooms the Atlas out to scale 0.5 once a map is completed (0x1407b2500).</summary>
    internal const int CompletedMapFlag = 295;

    /// <summary>The bonus count unlocks a tree. Existing allocations keep their tree available.</summary>
    public static bool TreeUnlocked(Player player, int tree) =>
        tree == 0 || (tree < TreeUnlockMaps.Length && tree < player.Character.AtlasPassives.Length
            && (player.Character.AtlasPassives[tree].Count > 0
                || AtlasProgress.BonusCompleted(WorldOf(PlayerOf(player)).CharacterData).Count >= TreeUnlockMaps[tree]));

    /// <summary>Sets per-character Atlas league flags (CompletedMap and trees II/III) on the connection (never saved to
    /// the account: they follow each character's own maps and active preset, so a shared save would leak one character's
    /// Atlas state to another). The client shows the tree selector when more than one tree is unlocked and starts on the
    /// last tree with Unlocked and Active.</summary>
    public static bool ApplyTreeFlags(Player player)
    {
        bool changed = false;
        var data = WorldOf(PlayerOf(player)).CharacterData;
        int completedCount = AtlasProgress.Completed(data).Count;
        int bonusCount = AtlasProgress.BonusCompleted(data).Count;
        changed |= completedCount > 0
            ? player.Character.LeagueQuestFlags.Add(CompletedMapFlag)
            : player.Character.LeagueQuestFlags.Remove(CompletedMapFlag);
        int active = AtlasActivePreset(player);
        for (int tree = 1; tree < TreeFlags.Length; tree++)
        {
            bool unlocked = tree < TreeUnlockMaps.Length && tree < player.Character.AtlasPassives.Length
                && (player.Character.AtlasPassives[tree].Count > 0 || bonusCount >= TreeUnlockMaps[tree]);
            changed |= unlocked ? player.Character.LeagueQuestFlags.Add(TreeFlags[tree].Unlocked) : player.Character.LeagueQuestFlags.Remove(TreeFlags[tree].Unlocked);
            bool on = unlocked && active == tree;
            changed |= on ? player.Character.LeagueQuestFlags.Add(TreeFlags[tree].Active) : player.Character.LeagueQuestFlags.Remove(TreeFlags[tree].Active);
        }
        return changed;
    }

    /// <summary>Re-evaluates the tree flags and sends the league flags again (S→C 0x2bc) when they changed, emitting
    /// S→C 0x01d5 award toasts when Atlas trees II or III unlock.</summary>
    internal static async Task SyncTreeFlags(Player player)
    {
        bool hadTree2 = player.Character.LeagueQuestFlags.Contains(TreeFlags[1].Unlocked);
        bool hadTree3 = player.Character.LeagueQuestFlags.Contains(TreeFlags[2].Unlocked);
        if (!ApplyTreeFlags(player) || !player.Connection.PlayerSent) return;
        await global::Game.SendWorldRecord(player, player.Id,
            InstanceEntities.PlayerLeagueQuestFlagsUpdate(player.Id, Quests.League.Bits(player.Character.LeagueQuestFlags)), "0x02bc Atlas tree flags");
        if (!hadTree2 && player.Character.LeagueQuestFlags.Contains(TreeFlags[1].Unlocked))
            await Send(player, InstanceProtocol.AwardDisplay(InstanceProtocol.AwardDisplayRow.AtlasPassiveAllocationSetUnlock1),
                "0x01d5 award display: Atlas tree II unlocked");
        if (!hadTree3 && player.Character.LeagueQuestFlags.Contains(TreeFlags[2].Unlocked))
            await Send(player, InstanceProtocol.AwardDisplay(InstanceProtocol.AwardDisplayRow.AtlasPassiveAllocationSetUnlock2),
                "0x01d5 award display: Atlas tree III unlocked");
    }

    static bool ApplyLeagueFlags(Player player)
    {
        bool changed = false;
        foreach (int id in UnlockLeagueFlags) if (player.Character.LeagueQuestFlags.Add(id)) changed = true;
        foreach (int id in StripLeagueFlags) if (player.Character.LeagueQuestFlags.Remove(id)) changed = true;
        return changed;
    }

    /// <summary>Kirac <c>DispatchRulesetEvent("open_maven_invitation …")</c>: put that MavenFights invitation in the bag.</summary>
    internal static async Task GiveMavenInvitation(Player player, string script)
    {
        var match = OpenMavenInvitation.Match(script ?? "");
        if (!match.Success || !MavenInvitationItems.TryGetValue(match.Groups[1].Value, out int row)
            || ItemsFor(player).Row(row) is not { } type)
            return;
        var items = ItemsOf(player);
        if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
        {
            await PlayerOf(player).Tell($"Atlas: no room for {type.Name}.");
            return;
        }
        var added = items.Put(PlayerInventories.MainInventory,
            new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type)));
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], [added]),
            $"0x00bf inventory 1: Kirac invitation {type.Name}");
        await PlayerOf(player).Tell($"Atlas: {type.Name}.");
    }

    // C→S 0x2a8: a tab of the Atlas tree picked. The client switches only on a 0x3d with that preset alone.
    internal static async Task SelectPreset(Player player, byte preset)
    {
        if (preset >= player.Character.AtlasPassives.Length)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas tree tab {preset} refused: no such preset");
            await SendAtlasSlots(player, "Atlas tab refused");
            return;
        }
        if (!TreeUnlocked(player, preset))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas tree tab {preset} refused: locked (needs {TreeUnlockMaps[preset]} completed maps)");
            await SendAtlasSlots(player, "Atlas tab locked");
            return;
        }
        SetAtlasActivePreset(player, preset);
        await SyncTreeFlags(player);
        await Send(player, AtlasSlots(player, preset),
            $"0x003d Atlas tree tab: preset {preset} active, {player.Character.AtlasPassives[preset].Count}/{player.Character.AtlasPassivePoints} points");
    }

    const int MaxTreeNameLength = 64;

    /// <summary>Gold cost per Atlas passive node refund (GameConstants.AtlasPassiveRespecCost = 2000; 0x141ef5ad0).</summary>
    static uint AtlasGoldRespecCost = 2000u;

    /// <summary>Latest BattlePasses.dat Id for S→C 0x012a (<see cref="InstanceMapDevice.AtlasVaultPass"/>).</summary>
    static string ActiveBattlePassId = "";

    internal static void LoadAtlasFeatureData()
    {
        uint cost = 2000u;
        if (GameServer.Core.ClientTables.Has("GameConstants"))
            foreach (var row in GameServer.Core.ClientTables.Table("GameConstants"))
                if (row.Text("Id") == "AtlasPassiveRespecCost" && row.IntOrNull("Value") is > 0 and int found)
                {
                    cost = (uint)found;
                    break;
                }
        AtlasGoldRespecCost = cost;
        string passId = "";
        if (GameServer.Core.ClientTables.Has("BattlePasses"))
            foreach (var row in GameServer.Core.ClientTables.Table("BattlePasses"))
                if (row.Text("Id") is { Length: > 0 } id)
                    passId = id;
        ActiveBattlePassId = passId;
    }

    internal static byte[]? VaultPassMessage(Player player)
    {
        if (ActiveBattlePassId.Length == 0) return null;
        int bonus = AtlasProgress.BonusCompleted(WorldOf(PlayerOf(player)).CharacterData).Count;
        return InstanceMapDevice.AtlasVaultPass((ushort)Math.Clamp(bonus, 0, ushort.MaxValue), ActiveBattlePassId,
            purchased: false, active: true);
    }

    /// <summary>Attaches <c>Rulesets.dat</c> row 99 (<c>AtlasPrimordialBossInfluenceRules</c>) and sends its initial
    /// empty S→C 0x2c9 sub-id 0 payload when entering an Eldritch-influenced map so the Primordial Altar HUD tracker
    /// is active.</summary>
    internal static IReadOnlyList<(string Log, byte[] Message)> PrimordialEnterMessages(Player player)
    {
        if (global::Game.MapModsOf(player) is not { } mods
            || mods.Get(Stat.MapAtlasPrimordialBossInfluence) <= 0)
            return [];
        return
        [
            ($"0x001f ruleset AtlasPrimordialBossInfluenceRules (row {InstanceRulesets.PrimordialInfluenceRulesetRow})",
                InstanceProtocol.AddRuleset(InstanceRulesets.PrimordialInfluenceRulesetRow)),
            ("0x02c9 AtlasPrimordialBossInfluenceRules sub-id 0 (initial empty altar mods)",
                InstanceRulesets.PrimordialAltarHud([], [], [], [], [], [])),
        ];
    }

    /// <summary>Updates the active Primordial Altar HUD mod lists (S→C 0x2c9 ruleset 99 sub-id 0).</summary>
    public static Task SendPrimordialAltarHud(
        Player player,
        IReadOnlyList<InstanceRulesets.PrimordialAltarMod> playerUpsides,
        IReadOnlyList<InstanceRulesets.PrimordialAltarMod> playerDownsides,
        IReadOnlyList<InstanceRulesets.PrimordialAltarMod> minionUpsides,
        IReadOnlyList<InstanceRulesets.PrimordialAltarMod> minionDownsides,
        IReadOnlyList<InstanceRulesets.PrimordialAltarMod> bossUpsides,
        IReadOnlyList<InstanceRulesets.PrimordialAltarMod> bossDownsides) =>
        Send(player, InstanceRulesets.PrimordialAltarHud(
                playerUpsides, playerDownsides, minionUpsides, minionDownsides, bossUpsides, bossDownsides),
            "0x02c9 AtlasPrimordialBossInfluenceRules sub-id 0 (altar mods update)");

    /// <summary>Opens the Atlas in scrying mode (S→C 0x7b kind 0x13, mode 1, source object id).</summary>
    sealed record ScryOffer(InstanceState Instance, uint ObjectId, string Source);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, ScryOffer> ScryOffers = new();

    public static Task OpenScrying(Player player, uint sourceObjectId)
    {
        ScryOffers.Remove(player);
        if (sourceObjectId != 0 && player.TalkingTo == sourceObjectId && AtlasProgress.OfArea(player.Layout.Id) is { IsNormalMap: true } source)
            ScryOffers.AddOrUpdate(player, new(player.Instance, sourceObjectId, source.Id));
        return Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.AtlasScryingUi, 1, sourceObjectId),
            $"0x007b open Atlas in scrying mode (source 0x{sourceObjectId:x})");
    }

    // C→S 0x01a7: swap divination card drop pools between two Atlas maps (Atlas Scrying).
    internal static async Task ScrySwap(Player player, InstanceMapDevice.AtlasScryRequest req)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        var from = AtlasProgress.FindByAreaHash16(req.FromAreaHash);
        var to = AtlasProgress.FindByAreaHash16(req.ToAreaHash);
        if (from is null || to is null || from.Id == to.Id)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01a7 Atlas scry 0x{req.FromAreaHash:x4} -> 0x{req.ToAreaHash:x4} refused: unknown or identical map");
            return;
        }
        var data = WorldOf(PlayerOf(player)).CharacterData;
        if (!ScryOffers.TryGetValue(player, out var offer) || offer.Instance != player.Instance
            || offer.ObjectId != req.ObjectId || offer.Source != from.Id || !to.IsNormalMap || !from.IsNormalMap) return;
        const string usedKey = "Atlas.ScryUsed";
        bool accepted = Saves.Atomically(() =>
        {
            var completed = AtlasProgress.Completed(data);
            if (!completed.Contains(from.Id) || !completed.Contains(to.Id)
                || AtlasProgress.ScryTarget(data, from) == to.Id || player.Instance.ContentState.ContainsKey(usedKey)) return false;
            if (!AtlasProgress.SetScry(data, from, to)) return false;
            player.Instance.ContentState[usedKey] = "1";
            return true;
        });
        if (!accepted) return;
        ScryOffers.Remove(player);
        await SendScryPairs(player, data);
        await PlayerOf(player).Tell($"Atlas Scrying: {to.Name} now drops the divination cards from {from.Name}.");
    }

    const string ScryingOrb = "Metadata/Items/Currency/CurrencyScryingOrb";
    const string ScouringOrb = "Metadata/Items/Currency/CurrencyConvertToNormal";

    internal static async Task DropScryingOrbOnCompletion(Player player, MonsterInfo monster, AtlasProgress.Node node)
    {
        if (!node.IsNormalMap || global::Game.MapRunOf(player) is not { Atlas: true, Area.Tier: >= 14 } run
            || !GameEvents.AllowsLoot(player)) return;
        var stats = player.Instance.AreaStats(() => global::Game.AreaLeagueStats(player));
        int chance = stats.GetValueOrDefault(Stat.MapDropScryingOrbOnCompletionChancePct);
        if (chance <= 0 || !player.Instance.ContentState.TryAdd("Atlas.ScryingOrbRolled", "1")
            || Random.Shared.Next(100) >= chance || ItemsFor(player).Find(ScryingOrb) is not { Supported: true } type) return;
        var at = player.Layout.Near((monster.X, monster.Y));
        var item = ItemCatalog.Create(type) with { ScryingAreaHash = player.Layout.Hash,
            ItemLevel = (byte)Math.Clamp(run.Area.Level > 0 ? run.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100) };
        var ground = new WorldItemObject(at.X, at.Y, item);
        uint id = player.Instance.Ground.Add(ground, player);
        await global::Game.SendGroundItem(player, id, ground, "Atlas map completion drops Scrying Orb", justDropped: true);
    }

    static Task SendScryPairs(Player player, ISavedData data)
    {
        var slots = SlotsOf(PlayerOf(player));
        return Send(player, AtlasProgress.AtlasState((byte)slots.Count, AtlasProgress.VoidstoneMask(slots), 0,
            AtlasProgress.ScryPairs(data)).ToArray(), "0x01a3 Atlas scrying connections");
    }

    internal static Task UseItemTypeOnNode(Player player, InstanceMapDevice.UseItemTypeOnAtlasNode req)
    {
        // Only a stackable Scouring Orb is interchangeable by type. Scrying Orbs have individual origins.
        if (ItemsFor(player).ByHash(req.TypeHash)?.Id != ScouringOrb) return Task.CompletedTask;
        var (inventory, id) = global::Game.LocateCurrencyStackByType(player, req.TypeHash, req.InventoryId);
        return id == 0 ? Task.CompletedTask : UseItemOnNode(player, new(inventory, id, req.NodeHash16), req.TypeHash);
    }

    internal static async Task UseItemOnNode(Player player, InstanceMapDevice.UseItemOnAtlasNode req, uint? requiredType = null)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0
            || AtlasProgress.FindByHash16(req.NodeHash16) is not { IsNormalMap: true } node) return;
        var items = ItemsOf(player);
        var stash = global::Game.StashOf(player);
        var data = WorldOf(PlayerOf(player)).CharacterData;
        var inventoryUpdate = Saves.Atomically<byte[]?>(() =>
        {
            var (spot, used) = global::Game.Locate(items, stash, req.InventoryId, req.ItemId);
            if (used is null || used.Item.StackSize == 0 || (requiredType.HasValue && used.Item.TypeHash != requiredType)) return null;
            if (spot.Tab is int stashTab)
            {
                if (!(player.StashOpen || player.GuildStashOpen) || stash.IsPublic(stashTab)) return null;
            }
            else if (req.InventoryId != PlayerInventories.InventoryId(spot.Row)
                || (spot.Row != PlayerInventories.MainInventory
                    && !(spot.Row == PlayerInventories.ExpandedInventory && ExpandedInventoryGranted(player)))) return null;

            switch (ItemsFor(player).ByHash(used.Item.TypeHash)?.Id)
            {
                case ScryingOrb:
                    if (used.Item.ScryingAreaHash is not ushort origin
                        || AtlasProgress.FindByAreaHash16(origin) is not { IsNormalMap: true } source
                        || !source.AreaHashes.Contains(origin) || source.Id == node.Id) return null;
                    var completed = AtlasProgress.Completed(data);
                    if (!completed.Contains(source.Id) || !completed.Contains(node.Id) || !AtlasProgress.SetScry(data, source, node)) return null;
                    break;
                case ScouringOrb:
                    if (!AtlasProgress.RemoveScry(data, node)) return null;
                    break;
                default:
                    return null;
            }

            InventoryItem? remaining = null;
            if (used.Item.StackSize == 1)
            {
                var taken = spot.Tab is int tab ? stash.Take(tab, used.Id) : items.Remove(spot.Row, used.Id);
                if (taken is null) throw new InvalidOperationException("Atlas currency disappeared during its transaction");
            }
            else
            {
                var reduced = used.Item with { StackSize = (ushort)(used.Item.StackSize - 1) };
                remaining = spot.Tab is int tab ? stash.Change(tab, used.Id, _ => reduced) : items.Change(spot.Row, used.Id, _ => reduced);
                if (remaining is null) throw new InvalidOperationException("Atlas currency disappeared during its transaction");
            }
            uint revision = spot.Tab is int t ? stash.NextRevision(t) : items.NextRevision(spot.Row);
            return InstanceItems.InventoryChange(req.InventoryId, revision, [used.Id], remaining is null ? [] : [remaining]);
        });
        if (inventoryUpdate is null) return;
        await Send(player, inventoryUpdate, $"0x00bf consume Atlas currency on {node.Name}");
        await SendScryPairs(player, data);
    }

    // C→S 0x2a7: rename one Atlas passive tree preset (0..2), persisted in module data and echoed via S→C 0x3c.
    public static async Task RenameAtlasPreset(Player player, (byte Preset, string Name) rename)
    {
        if (rename.Preset >= player.Character.AtlasPassives.Length || !TreeUnlocked(player, rename.Preset))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x02a7 Atlas tree rename preset {rename.Preset} refused: " +
                (rename.Preset >= player.Character.AtlasPassives.Length ? "no such preset" : "locked"));
            return;
        }
        string trimmed = new string(rename.Name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (trimmed.Length > MaxTreeNameLength) trimmed = trimmed[..MaxTreeNameLength];
        SetAtlasTreeName(player, rename.Preset, trimmed);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x02a7 Atlas tree preset {rename.Preset} renamed to \"{trimmed}\"");
        await Send(player, AtlasTreeNamesMessage(player), $"0x003c Atlas tree names (preset {rename.Preset})");
    }

    // C→S 0x40: refund every allocated node in one Atlas preset for gold (GameConstants.AtlasPassiveRespecCost each).
    public static async Task RefundAllAtlasPassives(Player player, byte preset)
    {
        if (preset >= player.Character.AtlasPassives.Length || !TreeUnlocked(player, preset))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0040 Atlas refund all preset {preset} refused: " +
                (preset >= player.Character.AtlasPassives.Length ? "no such preset" : "locked"));
            await SendAtlasSlots(player, "Atlas refund all refused");
            return;
        }
        var nodes = player.Character.AtlasPassives[preset];
        if (nodes.Count == 0)
        {
            await SendAtlasSlots(player, "Atlas refund all (empty)");
            return;
        }
        var links = AtlasPassiveNodes.Links;
        uint perNode = AtlasGoldRespecCost;
        long totalCost = 0;
        foreach (ushort id in nodes)
        {
            var passive = AtlasPassiveNodes.Find(id);
            bool stale = passive is null || links.Count > 0 && (passive.IsJustIcon || !links.ContainsKey(id)
                || !AtlasPassives.IsReached(nodes, id, links, AtlasPassives.StartGraphId));
            if (!stale) totalCost += perNode;
        }
        var character = CharacterName(player);
        uint goldLeft = 0;
        var nextPresets = player.Character.AtlasPassives.Select((p, i) => i == preset ? new List<ushort>() : p).ToArray();
        string? error = Saves.Atomically(() =>
        {
            if (totalCost > 0)
            {
                if (PersonalStashOf(player).TrySpendGold(totalCost) is not uint left)
                    return $"not enough gold ({totalCost} needed, {PersonalStashOf(player).Gold} held)";
                goldLeft = left;
            }
            if (character is not null)
                CharacterSaves.SetAtlasPassives(character, nextPresets, player.Character.AtlasPassivePoints);
            return null;
        });
        if (error is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0040 Atlas refund all preset {preset} refused: {error}");
            await SendAtlasSlots(player, "Atlas refund all refused");
            return;
        }
        int refunded = nodes.Count;
        nodes.Clear();
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0040 Atlas refund all preset {preset}: {refunded} node(s), {totalCost} gold paid");
        await SyncTreeFlags(player);
        if (totalCost > 0)
            await Send(player, InstanceEntities.Gold(goldLeft), $"0x02aa gold: {goldLeft} (Atlas refund all, {totalCost} paid)");
        await SendAtlasSlots(player, "Atlas refund all");
        await Send(player, InstanceProtocol.GameMessage(InstanceProtocol.GameMessageSource.BackendError,
            InstanceProtocol.BackendErrorId.FullAtlasPassiveSkillRefund),
            "0x0018 full Atlas passive skill refund");
    }

    // C→S 0x41: consume an unused one-off free full reset on an Atlas preset.
    public static async Task ResetAtlasPassivesFree(Player player, byte preset)
    {
        if (preset >= player.Character.AtlasPassives.Length || !TreeUnlocked(player, preset) || !HasAtlasFreeReset(player))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0041 Atlas free reset preset {preset} refused: " +
                (preset >= player.Character.AtlasPassives.Length ? "no such preset" : !TreeUnlocked(player, preset) ? "locked" : "none available"));
            await SendAtlasSlots(player, "Atlas free reset refused");
            return;
        }
        var nodes = player.Character.AtlasPassives[preset];
        if (nodes.Count == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0041 Atlas free reset preset {preset} refused: no nodes allocated");
            await SendAtlasSlots(player, "Atlas free reset (empty)");
            return;
        }
        var character = CharacterName(player);
        var nextPresets = player.Character.AtlasPassives.Select((p, i) => i == preset ? new List<ushort>() : p).ToArray();
        Saves.Atomically(() =>
        {
            if (character is not null)
                CharacterSaves.SetAtlasPassives(character, nextPresets, player.Character.AtlasPassivePoints);
            SetAtlasFreeReset(player, false);
        });
        int refunded = nodes.Count;
        nodes.Clear();
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0041 Atlas free reset preset {preset}: {refunded} node(s) cleared");
        await SyncTreeFlags(player);
        await SendAtlasSlots(player, "Atlas free reset");
    }

    // C→S 0x3e/0x3f: a click in the Atlas passive tree. Validate the node, the budget and the respec points (or gold
    // when the gold flag is set), save the preset and send all three presets back so the client and the save agree. The
    // server holds the Atlas graph (client.db) and refuses a node that is off it, an icon-only node, an allocation not
    // touching the start or an allocated node, and a refund that would cut allocated nodes off from the start.
    public static async Task ChangeAtlasPassive(Player player,
        byte preset, uint node, bool allocate, bool gold = false)
    {
        if (preset >= player.Character.AtlasPassives.Length || !TreeUnlocked(player, preset) || node > ushort.MaxValue
            || AtlasPassiveNodes.Find((ushort)node) is not { } passive || passive.Id == "atlas_start_node")
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas passive {(allocate ? "allocate" : "refund")} {node} refused: " +
                (preset < player.Character.AtlasPassives.Length && !TreeUnlocked(player, preset) ? $"preset {preset} locked" : "preset or node unknown"));
            await SendAtlasSlots(player, "Atlas refused");
            return;
        }
        player.Character.AtlasPreset = preset;
        var nodes = player.Character.AtlasPassives[preset];
        var character = CharacterName(player);
        var links = AtlasPassiveNodes.Links;
        bool hardMode = LeagueFor(player).HardMode;
        ushort respec = AtlasRespecPoints(player);
        if (links.Count > 0 && (allocate
                ? !nodes.Contains((ushort)node) && (passive.IsJustIcon
                    || !AtlasPassives.CanAllocate(nodes, (ushort)node, links, AtlasPassives.StartGraphId))
                : nodes.Contains((ushort)node) && !AtlasPassives.CanRefund(nodes, (ushort)node, links, AtlasPassives.StartGraphId)))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas passive {(allocate ? "allocate" : "refund")} {passive.Name} ({node}) " +
                "refused: an icon, off the graph or not connected to the start, or it would disconnect the tree");
            await SendAtlasSlots(player, "Atlas refused");
            return;
        }
        uint goldCost = 0;
        uint goldLeft = 0;
        if (allocate)
        {
            if (nodes.Contains((ushort)node))
            {
                await SendAtlasSlots(player, "Atlas already allocated");
                return;
            }
            if (nodes.Count >= AtlasPassiveNodes.Budget(player.Character.AtlasPassivePoints, nodes, hardMode))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas allocate {passive.Name} ({node}) refused: no points left");
                await SendAtlasSlots(player, "Atlas no points");
                return;
            }
            var updated = new List<ushort>(nodes) { (ushort)node };
            updated.Sort();
            var nextPresets = player.Character.AtlasPassives.Select((p, i) => i == preset ? updated : p).ToArray();
            Saves.Atomically(() =>
            {
                if (character is not null)
                    CharacterSaves.SetAtlasPassives(character, nextPresets, player.Character.AtlasPassivePoints);
                if (HasAtlasFreeReset(player)) SetAtlasFreeReset(player, false);
            });
            nodes.Add((ushort)node);
            nodes.Sort();
        }
        else
        {
            if (!nodes.Contains((ushort)node))
            {
                await SendAtlasSlots(player, "Atlas not allocated");
                return;
            }
            // A refund spends one respec point (0x141ef3710) or gold (0x141ef5ad0), and may not take away the points
            // that the remaining nodes use (a node granting points, such as Unwavering Vision).
            // A stale node (icon-only, off the graph or cut off from the start) is refunded free.
            var rest = nodes.Where(n => n != node).ToList();
            bool stale = links.Count > 0 && (passive.IsJustIcon || !links.ContainsKey((ushort)node)
                || !AtlasPassives.IsReached(nodes, (ushort)node, links, AtlasPassives.StartGraphId));
            if ((!gold && respec == 0 && !stale) || rest.Count > AtlasPassiveNodes.Budget(player.Character.AtlasPassivePoints, rest, hardMode))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas refund {passive.Name} ({node}) refused: " +
                    (!gold && respec == 0 && !stale ? "no respec points" : "the remaining nodes need its points"));
                await SendAtlasSlots(player, "Atlas refund refused");
                return;
            }
            var nextPresets = player.Character.AtlasPassives.Select((p, i) => i == preset ? rest : p).ToArray();
            if (gold && !stale)
            {
                goldCost = AtlasGoldRespecCost;
                string? error = Saves.Atomically(() =>
                {
                    if (PersonalStashOf(player).TrySpendGold(goldCost) is not uint left)
                        return $"not enough gold ({goldCost} needed, {PersonalStashOf(player).Gold} held)";
                    goldLeft = left;
                    if (character is not null)
                        CharacterSaves.SetAtlasPassives(character, nextPresets, player.Character.AtlasPassivePoints);
                    return null;
                });
                if (error is not null)
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas gold refund {passive.Name} ({node}) refused: {error}");
                    await SendAtlasSlots(player, "Atlas gold refund refused");
                    return;
                }
            }
            else
            {
                ushort nextRespec = stale ? respec : (ushort)(respec - 1);
                Saves.Atomically(() =>
                {
                    if (character is not null)
                        CharacterSaves.SetAtlasPassives(character, nextPresets, player.Character.AtlasPassivePoints);
                    if (!stale) SetAtlasRespecPoints(player, nextRespec);
                });
                respec = nextRespec;
            }
            nodes.Remove((ushort)node);
            if (nodes.Count == 0) await SyncTreeFlags(player);
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= Atlas preset {preset} {(allocate ? "allocate" : "refund")} " +
            $"{passive.Name} ({node}), {nodes.Count}/{AtlasPassiveNodes.Budget(player.Character.AtlasPassivePoints, nodes, hardMode)} " +
            $"points, {(goldCost > 0 ? $"{goldCost} gold paid" : $"{respec} respec")}");
        if (goldCost > 0)
            await Send(player, InstanceEntities.Gold(goldLeft), $"0x02aa gold: {goldLeft} (Atlas respec, {goldCost} paid)");
        await SendAtlasSlots(player, "Atlas changed");
    }

    static ItemMoves.BenchMove? PlaceVoidstone(PlayerInventories items, uint itemId, byte slot) =>
        Saves.Atomically<ItemMoves.BenchMove?>(() =>
        {
            byte main = PlayerInventories.MainInventory, atlas = PlayerInventories.AtlasUpgrades,
                cursor = PlayerInventories.Cursor;
            byte from;
            InventoryItem item;
            if (items.Items(main).FirstOrDefault(i => i.Id == itemId) is { } inMain) { from = main; item = inMain; }
            else if (items.Items(atlas).FirstOrDefault(i => i.Id == itemId) is { } inAtlas) { from = atlas; item = inAtlas; }
            else if (items.Items(cursor).FirstOrDefault(i => i.Id == itemId) is { } held) { from = cursor; item = held; }
            else return null;
            byte to = from == atlas ? main : atlas;
            if (ShapeOf(item.Item) is not { } shape) return null;
            (byte X, byte Y)? cell = to == atlas
                ? (slot > 3 || items.Items(to).Any(i => i.X == slot) ? null : ((byte)slot, (byte)0))
                : items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
            if (cell is null || items.Remove(from, itemId) is not { } taken) return null;
            return new(from, to, items.Put(to, taken with { X = cell.Value.X, Y = cell.Value.Y }));
        });

    // C→S 0xf6 / a bag click / a drop onto inventory 40: a Voidstone into AtlasUpgrades1. GrantAtlasUpgrade is set
    // the first time a stone goes in. A filled 0x1a3 then refreshes the quadrant T16 bits while the Atlas is open.
    internal static async Task<bool> SocketVoidstone(Player player,
        uint itemId)
    {
        var items = ItemsOf(player);
        InventoryItem? held = items.Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == itemId)
            ?? items.Items(PlayerInventories.AtlasUpgrades).FirstOrDefault(i => i.Id == itemId)
            ?? items.Items(PlayerInventories.Cursor).FirstOrDefault(i => i.Id == itemId);
        var type = held is { } item ? ItemsFor(player).ByHash(item.Item.TypeHash) : null;
        var stone = type is { } t ? MapBossFights.Current.StoneByItem(t.Id) : null;
        if (held is null || stone is null
            || PlaceVoidstone(items, itemId, (byte)stone.Slot) is not { } move)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= Voidstone item {itemId}: refused (not there, not a stone or no room)");
            return false;
        }
        bool into = move.To == PlayerInventories.AtlasUpgrades;
        if (CharacterName(player) is { } name) MapBossFights.Current.SetSocketed(name, stone.Slot, into);
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.From),
            items.NextRevision(move.From), [itemId], []),
            $"0x00f6 Voidstone: item {itemId} leaves row {move.From}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.To),
            items.NextRevision(move.To), [], [move.Placed]),
            $"0x00f6 Voidstone: item {itemId} into row {move.To} slot {move.Placed.X}");
        if (into && stone.Flag.Length > 0 && !ClientHasQuestFlag(player, stone.Flag))
            await SetQuestFlags(player, [stone.Flag]);
        await Send(player, AtlasProgress.VoidstoneDeltaMessage(stone.Slot, into),
            $"0x01a5 Atlas voidstone delta: slot {stone.Slot} (region bit {AtlasProgress.RegionBit(stone.Slot)}) = {(into ? 1 : 0)}");
        await Refresh(player);
        return true;
    }

    // C→S 0x28: inventory 40 unsockets onto the cursor; a Voidstone in the bag sockets into its Atlas slot (the
    // window only sends 0xf6 on Ctrl + click, so a plain click would otherwise do nothing).
    internal static async Task<bool> PickVoidstone(Player player, InstanceItems.PickUpItem pick)
    {
        uint atlas = PlayerInventories.InventoryId(PlayerInventories.AtlasUpgrades);
        uint main = PlayerInventories.InventoryId(PlayerInventories.MainInventory);
        uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        if (pick.InventoryId == atlas)
        {
            var items = ItemsOf(player);
            if (items.PickUp(PlayerInventories.AtlasUpgrades, pick.ItemId) is not { } held)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0028 Voidstone pick {pick.ItemId}: refused (not in Atlas upgrades)");
                return true;
            }
            int? unsocketedSlot = null;
            if (ItemsFor(player).ByHash(held.Item.TypeHash) is { } type
                && MapBossFights.Current.StoneByItem(type.Id) is { } stone)
            {
                unsocketedSlot = stone.Slot;
                if (CharacterName(player) is { } name)
                    MapBossFights.Current.SetSocketed(name, stone.Slot, false);
            }
            await Send(player, InstanceItems.InventoryChange(pick.InventoryId,
                items.NextRevision(PlayerInventories.AtlasUpgrades), [held.Id], []),
                $"0x0028 Voidstone: item {held.Id} leaves Atlas upgrades");
            await Send(player, InstanceItems.InventoryChange(cursor,
                items.NextRevision(PlayerInventories.Cursor), [], [held]),
                $"0x00bf cursor: add item {held.Id} (unsocketed Voidstone)");
            if (unsocketedSlot is int slot)
                await Send(player, AtlasProgress.VoidstoneDeltaMessage(slot, false),
                    $"0x01a5 Atlas voidstone delta: slot {slot} (region bit {AtlasProgress.RegionBit(slot)}) = 0");
            await Refresh(player);
            return true;
        }
        if (pick.InventoryId != main) return false;
        var bag = ItemsOf(player).Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == pick.ItemId);
        if (bag is null || ItemsFor(player).ByHash(bag.Item.TypeHash) is not { } bagType
            || MapBossFights.Current.StoneByItem(bagType.Id) is null)
            return false;
        return await SocketVoidstone(player, pick.ItemId);
    }

    internal static async Task Refresh(Player player)
    {
        await EnsureUnlocked(player);
        var modPlayer = PlayerOf(player);
        var data = WorldOf(modPlayer).CharacterData;
        var completed = AtlasProgress.Completed(data);
        var bonusCompleted = AtlasProgress.BonusCompleted(data);
        var slots = SlotsOf(modPlayer);
        byte stones = AtlasProgress.VoidstoneMask(slots);
        await Send(player, AtlasProgress.FullMessage(completed, bonusCompleted, stones, (byte)slots.Count, AtlasProgress.ScryPairs(data)),
            $"0x01a3 Atlas voidstones 0x{stones:X2} ({slots.Count} socketed), {completed.Count} completed, {bonusCompleted.Count} bonus");
    }

    /// <summary>Stones actually in AtlasUpgrades1, not the save (a missing item still left the old 4-slot mask and
    /// the client then refused 0xf6 on the empty socket).</summary>
    internal static HashSet<int> SlotsOf(IPlayer modPlayer)
    {
        var player = PlayerOf(modPlayer);
        var slots = new HashSet<int>();
        foreach (var item in ItemsOf(player).Items(PlayerInventories.AtlasUpgrades))
            if (ItemsFor(player).ByHash(item.Item.TypeHash) is { } type
                && MapBossFights.Current.StoneByItem(type.Id) is { } stone)
                slots.Add(stone.Slot);
        return slots;
    }

    internal static byte MaskOf(IPlayer player) => AtlasProgress.VoidstoneMask(SlotsOf(player));
}
