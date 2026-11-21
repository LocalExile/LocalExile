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
    /// <summary>The points the Atlas's completed maps are worth: one per map, up to the 100 the tree's map half has.
    /// Bonus objectives and the Maven's invitations are not counted yet (#382 item 3).</summary>
    internal const int MapCompletionPoints = 100;

    /// <summary>Gives the character <paramref name="maps"/> Atlas passive points for maps first completed, saves them and
    /// sends the three presets again so the tree's counter follows (S→C 0x3d, the message a click in the tree gets).</summary>
    static async Task AwardMapPoints(ClientState conn, IPlayer player, int maps, string label)
    {
        if (maps <= 0) return;
        if (conn.AtlasPassivePoints < MapCompletionPoints)
        {
            int before = conn.AtlasPassivePoints;
            conn.AtlasPassivePoints = (ushort)Math.Min(MapCompletionPoints, before + maps);
            if (CharacterName(conn) is { } character)
                CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints);
            await global::Game.SendAtlasSlots(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, label);
            await player.Tell($"Atlas: {conn.AtlasPassivePoints - before} passive point(s) for the maps completed " +
                $"({conn.AtlasPassivePoints}/{MapCompletionPoints}).");
        }
        // Trees II and III unlock at 50 and 100 completed maps.
        await AtlasFeature.SyncTreeFlags(conn);
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        ExtraPlayerMessages = conn => AtlasFeature.Restore(conn);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn => AtlasFeature.EnsureUnlocked(conn));
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => AtlasFeature.EnsureUnlocked(conn));
        GameEvents.Command(module.Manifest.Id, "atlas_unlock", "/atlas_unlock (dev: grant Atlas beacons, invitations and primordial keys)", async (conn, args) =>
        {
            int n = await AtlasFeature.EnsureUnlocked(conn);
            await PlayerOf(conn).Tell(n == 0 ? "Atlas: beacons and invitations already unlocked." : $"Atlas: granted {n} beacon/invitation flag(s).");
        });
        GameEvents.MonsterKilled.Add(module.Manifest.Id, async kill =>
        {
            if (AtlasProgress.OfArea(kill.Player.AreaId) is not { } node || !AtlasProgress.CompletesMap(node, kill.Monster)
                || !AtlasProgress.Complete(WorldOf(kill.Player).CharacterData, [node])) return;
            var conn = ConnOf(kill.Player);
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, AtlasProgress.NodeMessage(node),
                $"0x01a4 Atlas map completed: {node.Name} ({node.Id}) by {kill.Monster.Name}");
            // The Atlas panel and the map device's portal panel draw their "completed" marks from the completed set of
            // the last 0x1a3; without this they keep what the area entry sent and the map that just completed still
            // reads as unfinished there.
            await AtlasFeature.Refresh(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
            await kill.Player.Tell($"Atlas: {node.Name} completed.");
            await AwardMapPoints(conn, kill.Player, 1, "Atlas map completed");
        });
        GameEvents.Command(module.Manifest.Id, "atlas_complete", "/atlas_complete <map name or AtlasNode id | all> (dev: mark Atlas maps completed, saved)",
            async (conn, args) =>
            {
                string what = string.Join(' ', args);
                IReadOnlyList<AtlasProgress.Node> nodes = what.Equals("all", StringComparison.OrdinalIgnoreCase) ? AtlasProgress.Maps
                    : AtlasProgress.Find(what) is { } found ? [found] : [];
                if (nodes.Count == 0)
                {
                    await PlayerOf(conn).Tell($"No Atlas map '{what}'. Usage: /atlas_complete <map name or AtlasNode id | all>");
                    return;
                }
                var data = WorldOf(conn).CharacterData;
                var before = AtlasProgress.Completed(data);
                AtlasProgress.Complete(data, nodes);
                int earned = nodes.Count(n => !before.Contains(n.Id));
                if (nodes.Count == 1)
                    await PlayerOf(conn).Send(AtlasProgress.NodeMessage(nodes[0]),
                        $"0x01a4 /atlas_complete {nodes[0].Name} ({nodes[0].Id})");
                await PlayerOf(conn).Tell(nodes.Count == 1 ? $"Atlas: {nodes[0].Name} completed." : $"Atlas: {nodes.Count} maps completed.");
                await AwardMapPoints(conn, PlayerOf(conn), earned, "/atlas_complete");
            });
        GameEvents.Command(module.Manifest.Id, "atlas_reset", "/atlas_reset (dev: clear the Atlas's completed maps)", async (conn, args) =>
        {
            AtlasProgress.Reset(WorldOf(conn).CharacterData);
            await AtlasFeature.SyncTreeFlags(conn);
            var slots = AtlasFeature.SlotsOf(PlayerOf(conn));
            await PlayerOf(conn).Send(AtlasProgress.EmptyMessage(AtlasProgress.VoidstoneMask(slots), (byte)slots.Count),
                "0x01a3 /atlas_reset");
            await PlayerOf(conn).Tell("Atlas: progress cleared.");
        });
        GameEvents.Packet(InstanceEntities.AtlasAllocateId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (preset, node) = InstanceEntities.ReadAtlasAllocate(bytes);
            await AtlasFeature.ChangeAtlasPassive(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, preset, node, allocate: true);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasRefundId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (preset, node, goldFlag) = InstanceEntities.ReadAtlasRefund(bytes);
            await AtlasFeature.ChangeAtlasPassive(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, preset, node,
                allocate: false, gold: goldFlag != 0);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasRefundAllId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await AtlasFeature.RefundAllAtlasPassives(conn, bytes[2]);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasResetAllId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await AtlasFeature.ResetAtlasPassivesFree(conn, bytes[2]);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasTreeTabId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await AtlasFeature.SelectPreset(conn, bytes[2]);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasTreeRenameId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await AtlasFeature.RenameAtlasPreset(conn, InstanceEntities.ReadAtlasTreeRename(bytes));
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasWindowRequestId).Add(module.Manifest.Id, async (conn, _) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await Send(remote, stream, ciphers, InstanceEntities.AtlasWindowReply(), "0x0091 Atlas window reply");
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.SocketId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await AtlasFeature.SocketVoidstone(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
                InstanceMapDevice.ReadSocket(bytes).ItemId);
            return true;
        });
        // Taking a socketed stone off is C→S 0x28 from inventory 40, not 0xf6. Clicking a Voidstone in the bag
        // (without Ctrl) is also 0x28; the Atlas window does not send 0xf6 unless Ctrl is held, so that click
        // has to socket the stone itself. Dropping onto a socket is C→S 0x2a into inventory 40.
        GameEvents.Packet(InstanceItems.PickUpItemId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            return await AtlasFeature.PickVoidstone(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
                InstanceItems.ReadPickUpItem(bytes));
        });
        GameEvents.InventoryPlace.Add(module.Manifest.Id, async (conn, place) =>
        {
            if (place.InventoryId != PlayerInventories.InventoryId(PlayerInventories.AtlasUpgrades)) return false;
            var held = ItemsOf(conn).Items(PlayerInventories.Cursor).FirstOrDefault();
            if (held is null || ItemsFor(conn).ByHash(held.Item.TypeHash) is not { } type
                || MapBossFights.Current.StoneByItem(type.Id) is null)
                return false;
            var (remote, stream, ciphers) = conn.Wire;
            await AtlasFeature.SocketVoidstone(remote, stream, ciphers, conn, held.Id);
            return true;
        });
        // Kirac's invitation rows FinishNPCChat + DispatchRulesetEvent("open_maven_invitation …"). The chat end ran
        // with no item. Hand the matching MavenFights invitation into the bag first; the empty FinishNPCChat still closes.
        GameEvents.NpcOptionPicked.Add(module.Manifest.Id, async (conn, talk) =>
        {
            if (AtlasFeature.KiracHeardFlags(conn, talk) is { Count: > 0 } heard)
            {
                var (remote, stream, ciphers) = conn.Wire;
                await SetLeagueQuestFlags(remote, stream, ciphers, conn, heard);
            }
            await AtlasFeature.GiveMavenInvitation(conn, talk.Script);
            return true;
        });
    }
}

static class AtlasFeature
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
    /// <see cref="ClientState.LeagueQuestFlags"/> before the player object is built so a later 0x02bc does not toast.
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

    // Kirac's rows (NPCs.dat 410, also the hideout Kirac's) mark their conversation heard with league quest flags
    // (NPCTalk column 15), which open his next ones. Reward rows are left to their reward offer.
    internal static List<int> KiracHeardFlags(ClientState conn, NpcCatalog.Talk talk)
    {
        if (talk.Npc != 410 || talk.RewardOffer is not null) return [];
        lock (conn.Monsters) return [.. talk.HeardLeagueFlags.Where(f => !conn.LeagueQuestFlags.Contains(f))];
    }

    internal static async Task<int> EnsureUnlocked(ClientState conn)
    {
        var missing = UnlockFlags.Where(f => !ClientHasQuestFlag(conn, f)).ToList();
        bool leagueChanged = ApplyLeagueFlags(conn);
        if (!conn.PlayerSent) return missing.Count + (leagueChanged ? 1 : 0);
        int n = 0;
        var (remote, stream, ciphers) = conn.Wire;
        if (missing.Count > 0)
        {
            await SetQuestFlags(remote, stream, ciphers, conn, missing);
            n += missing.Count;
        }
        if (leagueChanged)
        {
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(conn.LeagueQuestFlags)),
                "0x02bc Atlas league flags");
            n++;
        }
        return n;
    }

    /// <summary>One 0x1a3 before 0x21: the sets replace without a completion event. 0x1a4 is only for a new completion.
    /// League flags are put on the connection here, before <c>PlayerForCreation</c> reads them.</summary>
    internal static IEnumerable<(string Log, byte[] Message)> Restore(ClientState conn)
    {
        ApplyLeagueFlags(conn);
        ApplyTreeFlags(conn);
        var player = PlayerOf(conn);
        var data = WorldOf(player).CharacterData;
        var completed = AtlasProgress.Completed(data);
        // One Atlas passive point per map completed, up to the 100 the tree's map half has (#382 item 3). The completed set
        // is read from the save. Entering an area does not complete any new maps.
        bool pointsChanged = false;
        if (Math.Min(AtlasModule.MapCompletionPoints, completed.Count) is int earned and > 0 && earned > conn.AtlasPassivePoints)
        {
            conn.AtlasPassivePoints = (ushort)earned;
            if (CharacterName(conn) is { } character)
                CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints);
            pointsChanged = true;
        }
        var slots = SlotsOf(player);
        byte stones = AtlasProgress.VoidstoneMask(slots);
        yield return ("0x003c Atlas tree names", global::Game.AtlasTreeNamesMessage(conn));
        yield return ($"0x01a3 Atlas progress, {completed.Count} map(s) saved, {AtlasProgress.OnAtlasRows.Count} on-atlas row(s), voidstones 0x{stones:X2} ({slots.Count})",
            AtlasProgress.FullMessage(completed, stones, (byte)slots.Count));
        // The presets in the player batch were built before the budget above changed: send them again so the tree's
        // counter follows on this login.
        if (pointsChanged)
            foreach (var message in global::Game.AtlasPresetMessages(conn, "after the map points"))
                yield return message;
    }

    /// <summary>Completed maps needed for Atlas trees I, II and III. The client's text says "bonus Atlas objectives"; the
    /// server has no bonus-objective count (S→C 0x1a3 carries only the completed-map masks), so completed maps stand in.</summary>
    internal static readonly int[] TreeUnlockMaps = [0, 50, 100];

    /// <summary>AtlasTrees' flag columns for trees II and III: Unlocked (329, 331) and Active (330, 332).</summary>
    static readonly (int Unlocked, int Active)[] TreeFlags = [(0, 0), (329, 330), (331, 332)];

    /// <summary>LeagueQuestFlags id 295 (wire bit 294): zooms the Atlas out to scale 0.5 once a map is completed (0x1407b2500).</summary>
    internal const int CompletedMapFlag = 295;

    /// <summary>A tree is unlocked by the character's completed maps, or by holding nodes (an old save's nodes in a tree that
    /// is now locked stay reachable).</summary>
    internal static bool TreeUnlocked(ClientState conn, int tree) =>
        tree == 0 || (tree < TreeUnlockMaps.Length && tree < conn.AtlasPassives.Length
            && (conn.AtlasPassives[tree].Count > 0
                || AtlasProgress.Completed(WorldOf(PlayerOf(conn)).CharacterData).Count >= TreeUnlockMaps[tree]));

    /// <summary>Sets per-character Atlas league flags (CompletedMap and trees II/III) on the connection (never saved to
    /// the account: they follow each character's own maps and active preset, so a shared save would leak one character's
    /// Atlas state to another). The client shows the tree selector when more than one tree is unlocked and starts on the
    /// last tree with Unlocked and Active.</summary>
    internal static bool ApplyTreeFlags(ClientState conn)
    {
        bool changed = false;
        int completedCount = AtlasProgress.Completed(WorldOf(PlayerOf(conn)).CharacterData).Count;
        changed |= completedCount > 0
            ? conn.LeagueQuestFlags.Add(CompletedMapFlag)
            : conn.LeagueQuestFlags.Remove(CompletedMapFlag);
        int active = AtlasActivePreset(conn);
        for (int tree = 1; tree < TreeFlags.Length; tree++)
        {
            bool unlocked = tree < TreeUnlockMaps.Length && tree < conn.AtlasPassives.Length
                && (conn.AtlasPassives[tree].Count > 0 || completedCount >= TreeUnlockMaps[tree]);
            changed |= unlocked ? conn.LeagueQuestFlags.Add(TreeFlags[tree].Unlocked) : conn.LeagueQuestFlags.Remove(TreeFlags[tree].Unlocked);
            bool on = unlocked && active == tree;
            changed |= on ? conn.LeagueQuestFlags.Add(TreeFlags[tree].Active) : conn.LeagueQuestFlags.Remove(TreeFlags[tree].Active);
        }
        return changed;
    }

    /// <summary>Re-evaluates the tree flags and sends the league flags again (S→C 0x2bc) when they changed.</summary>
    internal static async Task SyncTreeFlags(ClientState conn)
    {
        if (!ApplyTreeFlags(conn) || !conn.PlayerSent) return;
        var (remote, stream, ciphers) = conn.Wire;
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
            InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(conn.LeagueQuestFlags)), "0x02bc Atlas tree flags");
    }

    static bool ApplyLeagueFlags(ClientState conn)
    {
        bool changed = false;
        foreach (int id in UnlockLeagueFlags) if (conn.LeagueQuestFlags.Add(id)) changed = true;
        foreach (int id in StripLeagueFlags) if (conn.LeagueQuestFlags.Remove(id)) changed = true;
        return changed;
    }

    /// <summary>Kirac <c>DispatchRulesetEvent("open_maven_invitation …")</c>: put that MavenFights invitation in the bag.</summary>
    internal static async Task GiveMavenInvitation(ClientState conn, string script)
    {
        var match = OpenMavenInvitation.Match(script ?? "");
        if (!match.Success || !MavenInvitationItems.TryGetValue(match.Groups[1].Value, out int row)
            || ItemsFor(conn).Row(row) is not { } type)
            return;
        var items = ItemsOf(conn);
        if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
        {
            await PlayerOf(conn).Tell($"Atlas: no room for {type.Name}.");
            return;
        }
        var added = items.Put(PlayerInventories.MainInventory,
            new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type)));
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], [added]),
            $"0x00bf inventory 1: Kirac invitation {type.Name}");
        await PlayerOf(conn).Tell($"Atlas: {type.Name}.");
    }

    // C→S 0x2a8: a tab of the Atlas tree picked. The client switches only on a 0x3d with that preset alone.
    internal static async Task SelectPreset(ClientState conn, byte preset)
    {
        var (remote, stream, ciphers) = conn.Wire;
        if (preset >= conn.AtlasPassives.Length)
        {
            Console.WriteLine($"[gateway] {remote} <= Atlas tree tab {preset} refused: no such preset");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas tab refused");
            return;
        }
        if (!TreeUnlocked(conn, preset))
        {
            Console.WriteLine($"[gateway] {remote} <= Atlas tree tab {preset} refused: locked (needs {TreeUnlockMaps[preset]} completed maps)");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas tab locked");
            return;
        }
        SetAtlasActivePreset(conn, preset);
        await SyncTreeFlags(conn);
        await Send(remote, stream, ciphers, AtlasSlots(conn, preset),
            $"0x003d Atlas tree tab: preset {preset} active, {conn.AtlasPassives[preset].Count}/{conn.AtlasPassivePoints} points");
    }

    const int MaxTreeNameLength = 64;

    /// <summary>Gold cost per Atlas passive node refund (GameConstants.AtlasPassiveRespecCost = 2000; 0x141ef5ad0).</summary>
    static readonly Lazy<uint> AtlasGoldRespecCost = new(() =>
    {
        if (GameServer.Core.ClientTables.Has("GameConstants"))
            foreach (var row in GameServer.Core.ClientTables.Table("GameConstants"))
                if (row.Text("Id") == "AtlasPassiveRespecCost" && row.IntOrNull("Value") is > 0 and int cost)
                    return (uint)cost;
        return 2000u;
    });

    // C→S 0x2a7: rename one Atlas passive tree preset (0..2), persisted in module data and echoed via S→C 0x3c.
    internal static async Task RenameAtlasPreset(ClientState conn, (byte Preset, string Name) rename)
    {
        var (remote, stream, ciphers) = conn.Wire;
        if (rename.Preset >= conn.AtlasPassives.Length)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x02a7 Atlas tree rename preset {rename.Preset} refused: no such preset");
            return;
        }
        string trimmed = rename.Name.Trim();
        if (trimmed.Length > MaxTreeNameLength) trimmed = trimmed[..MaxTreeNameLength];
        SetAtlasTreeName(conn, rename.Preset, trimmed);
        Console.WriteLine($"[gateway] {remote} <= 0x02a7 Atlas tree preset {rename.Preset} renamed to \"{trimmed}\"");
        await Send(remote, stream, ciphers, AtlasTreeNamesMessage(conn), $"0x003c Atlas tree names (preset {rename.Preset})");
    }

    // C→S 0x40: refund every allocated node in one Atlas preset for gold (GameConstants.AtlasPassiveRespecCost each).
    internal static async Task RefundAllAtlasPassives(ClientState conn, byte preset)
    {
        var (remote, stream, ciphers) = conn.Wire;
        if (preset >= conn.AtlasPassives.Length)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0040 Atlas refund all preset {preset} refused: no such preset");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refund all refused");
            return;
        }
        var nodes = conn.AtlasPassives[preset];
        if (nodes.Count == 0)
        {
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refund all (empty)");
            return;
        }
        var links = AtlasPassiveNodes.Links;
        uint perNode = AtlasGoldRespecCost.Value;
        long totalCost = 0;
        foreach (ushort id in nodes)
        {
            var passive = AtlasPassiveNodes.Find(id);
            bool stale = passive is null || links.Count > 0 && (passive.IsJustIcon || !links.ContainsKey(id)
                || !AtlasPassives.IsReached(nodes, id, links, AtlasPassives.StartGraphId));
            if (!stale) totalCost += perNode;
        }
        var character = CharacterName(conn);
        uint goldLeft = 0;
        var nextPresets = conn.AtlasPassives.Select((p, i) => i == preset ? new List<ushort>() : p).ToArray();
        string? error = Saves.Atomically(() =>
        {
            if (totalCost > 0)
            {
                if (PersonalStashOf(conn).TrySpendGold(totalCost) is not uint left)
                    return $"not enough gold ({totalCost} needed, {PersonalStashOf(conn).Gold} held)";
                goldLeft = left;
            }
            if (character is not null)
                CharacterSaves.SetAtlasPassives(character, nextPresets, conn.AtlasPassivePoints);
            return null;
        });
        if (error is not null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0040 Atlas refund all preset {preset} refused: {error}");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refund all refused");
            return;
        }
        int refunded = nodes.Count;
        nodes.Clear();
        Console.WriteLine($"[gateway] {remote} <= 0x0040 Atlas refund all preset {preset}: {refunded} node(s), {totalCost} gold paid");
        if (totalCost > 0)
            await Send(remote, stream, ciphers, InstanceEntities.Gold(goldLeft), $"0x02aa gold: {goldLeft} (Atlas refund all, {totalCost} paid)");
        await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refund all");
    }

    // C→S 0x41: consume an unused one-off free full reset on an Atlas preset.
    internal static async Task ResetAtlasPassivesFree(ClientState conn, byte preset)
    {
        var (remote, stream, ciphers) = conn.Wire;
        if (preset >= conn.AtlasPassives.Length || !HasAtlasFreeReset(conn))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0041 Atlas free reset preset {preset} refused: " +
                (preset >= conn.AtlasPassives.Length ? "no such preset" : "none available"));
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas free reset refused");
            return;
        }
        var nodes = conn.AtlasPassives[preset];
        var character = CharacterName(conn);
        var nextPresets = conn.AtlasPassives.Select((p, i) => i == preset ? new List<ushort>() : p).ToArray();
        Saves.Atomically(() =>
        {
            if (character is not null)
                CharacterSaves.SetAtlasPassives(character, nextPresets, conn.AtlasPassivePoints);
            SetAtlasFreeReset(conn, false);
        });
        int refunded = nodes.Count;
        nodes.Clear();
        Console.WriteLine($"[gateway] {remote} <= 0x0041 Atlas free reset preset {preset}: {refunded} node(s) cleared");
        await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas free reset");
    }

    // C→S 0x3e/0x3f: a click in the Atlas passive tree. Validate the node, the budget and the respec points (or gold
    // when the gold flag is set), save the preset and send all three presets back so the client and the save agree. The
    // server holds the Atlas graph (client.db) and refuses a node that is off it, an icon-only node, an allocation not
    // touching the start or an allocated node, and a refund that would cut allocated nodes off from the start.
    internal static async Task ChangeAtlasPassive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        byte preset, uint node, bool allocate, bool gold = false)
    {
        if (preset >= conn.AtlasPassives.Length || node > ushort.MaxValue
            || AtlasPassiveNodes.Find((ushort)node) is not { } passive || passive.Id == "atlas_start_node")
        {
            Console.WriteLine($"[gateway] {remote} <= Atlas passive {(allocate ? "allocate" : "refund")} {node} refused: " +
                "preset or node unknown");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refused");
            return;
        }
        var nodes = conn.AtlasPassives[preset];
        var character = CharacterName(conn);
        var links = AtlasPassiveNodes.Links;
        bool hardMode = LeagueFor(conn).HardMode;
        ushort respec = AtlasRespecPoints(conn);
        if (links.Count > 0 && (allocate
                ? !nodes.Contains((ushort)node) && (passive.IsJustIcon
                    || !AtlasPassives.CanAllocate(nodes, (ushort)node, links, AtlasPassives.StartGraphId))
                : nodes.Contains((ushort)node) && !AtlasPassives.CanRefund(nodes, (ushort)node, links, AtlasPassives.StartGraphId)))
        {
            Console.WriteLine($"[gateway] {remote} <= Atlas passive {(allocate ? "allocate" : "refund")} {passive.Name} ({node}) " +
                "refused: an icon, off the graph or not connected to the start, or it would disconnect the tree");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refused");
            return;
        }
        uint goldCost = 0;
        uint goldLeft = 0;
        if (allocate)
        {
            if (nodes.Contains((ushort)node))
            {
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas already allocated");
                return;
            }
            if (nodes.Count >= AtlasPassiveNodes.Budget(conn.AtlasPassivePoints, nodes, hardMode))
            {
                Console.WriteLine($"[gateway] {remote} <= Atlas allocate {passive.Name} ({node}) refused: no points left");
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas no points");
                return;
            }
            var updated = new List<ushort>(nodes) { (ushort)node };
            updated.Sort();
            var nextPresets = conn.AtlasPassives.Select((p, i) => i == preset ? updated : p).ToArray();
            Saves.Atomically(() =>
            {
                if (character is not null)
                    CharacterSaves.SetAtlasPassives(character, nextPresets, conn.AtlasPassivePoints);
                if (HasAtlasFreeReset(conn)) SetAtlasFreeReset(conn, false);
            });
            nodes.Add((ushort)node);
            nodes.Sort();
        }
        else
        {
            if (!nodes.Contains((ushort)node))
            {
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas not allocated");
                return;
            }
            // A refund spends one respec point (0x141ef3710) or gold (0x141ef5ad0), and may not take away the points
            // that the remaining nodes use (a node granting points, such as Unwavering Vision).
            // A stale node (icon-only, off the graph or cut off from the start) is refunded free.
            var rest = nodes.Where(n => n != node).ToList();
            bool stale = links.Count > 0 && (passive.IsJustIcon || !links.ContainsKey((ushort)node)
                || !AtlasPassives.IsReached(nodes, (ushort)node, links, AtlasPassives.StartGraphId));
            if ((!gold && respec == 0 && !stale) || rest.Count > AtlasPassiveNodes.Budget(conn.AtlasPassivePoints, rest, hardMode))
            {
                Console.WriteLine($"[gateway] {remote} <= Atlas refund {passive.Name} ({node}) refused: " +
                    (!gold && respec == 0 && !stale ? "no respec points" : "the remaining nodes need its points"));
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refund refused");
                return;
            }
            var nextPresets = conn.AtlasPassives.Select((p, i) => i == preset ? rest : p).ToArray();
            if (gold && !stale)
            {
                goldCost = AtlasGoldRespecCost.Value;
                string? error = Saves.Atomically(() =>
                {
                    if (PersonalStashOf(conn).TrySpendGold(goldCost) is not uint left)
                        return $"not enough gold ({goldCost} needed, {PersonalStashOf(conn).Gold} held)";
                    goldLeft = left;
                    if (character is not null)
                        CharacterSaves.SetAtlasPassives(character, nextPresets, conn.AtlasPassivePoints);
                    return null;
                });
                if (error is not null)
                {
                    Console.WriteLine($"[gateway] {remote} <= Atlas gold refund {passive.Name} ({node}) refused: {error}");
                    await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas gold refund refused");
                    return;
                }
            }
            else
            {
                ushort nextRespec = stale ? respec : (ushort)(respec - 1);
                Saves.Atomically(() =>
                {
                    if (character is not null)
                        CharacterSaves.SetAtlasPassives(character, nextPresets, conn.AtlasPassivePoints);
                    if (!stale) SetAtlasRespecPoints(conn, nextRespec);
                });
                respec = nextRespec;
            }
            nodes.Remove((ushort)node);
        }
        Console.WriteLine($"[gateway] {remote} <= Atlas preset {preset} {(allocate ? "allocate" : "refund")} " +
            $"{passive.Name} ({node}), {nodes.Count}/{AtlasPassiveNodes.Budget(conn.AtlasPassivePoints, nodes, hardMode)} " +
            $"points, {(goldCost > 0 ? $"{goldCost} gold paid" : $"{respec} respec")}");
        if (goldCost > 0)
            await Send(remote, stream, ciphers, InstanceEntities.Gold(goldLeft), $"0x02aa gold: {goldLeft} (Atlas respec, {goldCost} paid)");
        await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas changed");
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
    internal static async Task<bool> SocketVoidstone(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint itemId)
    {
        var items = ItemsOf(conn);
        InventoryItem? held = items.Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == itemId)
            ?? items.Items(PlayerInventories.AtlasUpgrades).FirstOrDefault(i => i.Id == itemId)
            ?? items.Items(PlayerInventories.Cursor).FirstOrDefault(i => i.Id == itemId);
        var type = held is { } item ? ItemsFor(conn).ByHash(item.Item.TypeHash) : null;
        var stone = type is { } t ? MapBossFights.Current.StoneByItem(t.Id) : null;
        if (held is null || stone is null
            || PlaceVoidstone(items, itemId, (byte)stone.Slot) is not { } move)
        {
            Console.WriteLine($"[gateway] {remote} <= Voidstone item {itemId}: refused (not there, not a stone or no room)");
            return false;
        }
        bool into = move.To == PlayerInventories.AtlasUpgrades;
        if (CharacterName(conn) is { } name) MapBossFights.Current.SetSocketed(name, stone.Slot, into);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.From),
            items.NextRevision(move.From), [itemId], []),
            $"0x00f6 Voidstone: item {itemId} leaves row {move.From}");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.To),
            items.NextRevision(move.To), [], [move.Placed]),
            $"0x00f6 Voidstone: item {itemId} into row {move.To} slot {move.Placed.X}");
        if (into && stone.Flag.Length > 0 && !ClientHasQuestFlag(conn, stone.Flag))
            await SetQuestFlags(remote, stream, ciphers, conn, [stone.Flag]);
        await Refresh(remote, stream, ciphers, conn);
        return true;
    }

    // C→S 0x28: inventory 40 unsockets onto the cursor; a Voidstone in the bag sockets into its Atlas slot (the
    // window only sends 0xf6 on Ctrl + click, so a plain click would otherwise do nothing).
    internal static async Task<bool> PickVoidstone(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceItems.PickUpItem pick)
    {
        uint atlas = PlayerInventories.InventoryId(PlayerInventories.AtlasUpgrades);
        uint main = PlayerInventories.InventoryId(PlayerInventories.MainInventory);
        uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        if (pick.InventoryId == atlas)
        {
            var items = ItemsOf(conn);
            if (items.PickUp(PlayerInventories.AtlasUpgrades, pick.ItemId) is not { } held)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0028 Voidstone pick {pick.ItemId}: refused (not in Atlas upgrades)");
                return true;
            }
            if (ItemsFor(conn).ByHash(held.Item.TypeHash) is { } type
                && MapBossFights.Current.StoneByItem(type.Id) is { } stone
                && CharacterName(conn) is { } name)
                MapBossFights.Current.SetSocketed(name, stone.Slot, false);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(pick.InventoryId,
                items.NextRevision(PlayerInventories.AtlasUpgrades), [held.Id], []),
                $"0x0028 Voidstone: item {held.Id} leaves Atlas upgrades");
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(cursor,
                items.NextRevision(PlayerInventories.Cursor), [], [held]),
                $"0x00bf cursor: add item {held.Id} (unsocketed Voidstone)");
            await Refresh(remote, stream, ciphers, conn);
            return true;
        }
        if (pick.InventoryId != main) return false;
        var bag = ItemsOf(conn).Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == pick.ItemId);
        if (bag is null || ItemsFor(conn).ByHash(bag.Item.TypeHash) is not { } bagType
            || MapBossFights.Current.StoneByItem(bagType.Id) is null)
            return false;
        return await SocketVoidstone(remote, stream, ciphers, conn, pick.ItemId);
    }

    internal static async Task Refresh(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        await EnsureUnlocked(conn);
        var player = PlayerOf(conn);
        var completed = AtlasProgress.Completed(WorldOf(player).CharacterData);
        var slots = SlotsOf(player);
        byte stones = AtlasProgress.VoidstoneMask(slots);
        await Send(remote, stream, ciphers, AtlasProgress.FullMessage(completed, stones, (byte)slots.Count),
            $"0x01a3 Atlas voidstones 0x{stones:X2} ({slots.Count} socketed), {completed.Count} map(s)");
    }

    /// <summary>Stones actually in AtlasUpgrades1, not the save (a missing item still left the old 4-slot mask and
    /// the client then refused 0xf6 on the empty socket).</summary>
    internal static HashSet<int> SlotsOf(IPlayer player)
    {
        var conn = ConnOf(player);
        var slots = new HashSet<int>();
        foreach (var item in ItemsOf(conn).Items(PlayerInventories.AtlasUpgrades))
            if (ItemsFor(conn).ByHash(item.Item.TypeHash) is { } type
                && MapBossFights.Current.StoneByItem(type.Id) is { } stone)
                slots.Add(stone.Slot);
        return slots;
    }

    internal static byte MaskOf(IPlayer player) => AtlasProgress.VoidstoneMask(SlotsOf(player));
}
