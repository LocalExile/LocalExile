using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>
/// Handles Rogue Harbour planning screens, C→S <c>0x279</c> (sign contract), <c>0x27d</c> (reveal blueprint),
/// <c>0x27e</c> (confirm blueprint plans), <c>0x27f</c> (assign crew), and Heist inventory placement guards.
/// </summary>
public static class HeistPlanning
{
    public const byte MissionUi = 0x34;
    public const byte BlueprintUi = 0x35;
    public const byte NpcInventoryUi = 0x36;

    static readonly HashSet<string> HeistEquipmentClasses = new(StringComparer.Ordinal)
    {
        "HeistEquipmentWeapon", "HeistEquipmentTool", "HeistEquipmentUtility", "HeistEquipmentReward",
    };

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    /// <summary>Inventory placement guards for rows 44 (Contract), 45..53 (Rogue Ally Equipment), and 55 (Blueprint).</summary>
    public static string? Refusal(Player player, byte row, Item item)
    {
        var baseType = ItemsFor(player).ByHash(item.TypeHash);
        if (row == HeistTables.ContractMissionRow)
            return baseType?.Class == "HeistContract" ? null : "Only a Heist Contract can be placed here.";
        if (row == HeistTables.BlueprintMissionRow)
            return baseType?.Class == "HeistBlueprint" ? null : "Only a Heist Blueprint can be placed here.";
        if (row is >= HeistTables.FirstNpcEquipmentRow and <= HeistTables.LastNpcEquipmentRow)
        {
            if (baseType is null || !HeistEquipmentClasses.Contains(baseType.Class))
                return "Only Rogue Equipment can be equipped on a Heist ally.";
            byte rogueRow = (byte)(row - HeistTables.FirstNpcEquipmentRow);
            if (HeistTables.EquipmentByBaseRow.TryGetValue(baseType.Row, out var req) && req.RequiredJob is int reqJob)
            {
                var progress = HeistProgress.Load(player);
                int level = HeistProgress.EffectiveJobLevel(player, progress, rogueRow, (byte)reqJob);
                if (level < req.RequiredLevel)
                    return $"Requires job level {req.RequiredLevel} for this rogue.";
            }
            return null;
        }
        return null;
    }

    // ---- C→S 0x027d: Reveal Blueprint Details ------------------------------------------------------------------------

    public static async Task<bool> HandleRevealBlueprint(IModuleContext module, Player player, byte[] packet)
    {
        if (!Current(player) || player.PlayerLife == 0 || !HeistTables.IsRogueHarbour(player.Layout.Id)) return false;
        if (!InstanceHeist.TryReadRevealBlueprint(packet, out var req)) return true;
        if (req.RevealerRow >= HeistTables.Revealers.Length) return true;

        var items = ItemsOf(player);
        uint coinHash = InstanceEntities.ObjectTypeHash(HeistTables.HeistCoinPath);
        int discount = HeistTables.Revealers[req.RevealerRow].DiscountPct;

        string? failure = null;
        int markerCost = 0;
        uint removedBpId = 0;
        PlayerInventories.Payment? mainPay = null, lockerPay = null;
        InventoryItem? replaced = null;
        HeistProgress.ProgressData? updatedProgress = null;

        Saves.Atomically(() =>
        {
            var bpEntry = items.Items(HeistTables.BlueprintMissionRow).FirstOrDefault();
            if (bpEntry?.Item.HeistBlueprint is not { Confirmed: false } bp) return;
            if (req.WingIndex >= bp.Wings.Count) return;

            var wing = bp.Wings[req.WingIndex];
            if (!TryApplyReveal(wing, req.RoomTarget, out var updatedWing, out int baseCost))
            {
                failure = "That blueprint detail is already revealed or invalid.";
                return;
            }

            var progress = HeistProgress.Load(player);
            if (HeistProgress.RevealsFor(progress, req.RevealerRow) == 0)
            {
                failure = "No blueprint reveals remaining with this rogue.";
                return;
            }

            markerCost = Math.Max(1, baseCost * (100 - discount) / 100);
            if (!TryComputeMarkerSplit(items, coinHash, markerCost, out int fromMain, out int fromLocker))
            {
                failure = $"Requires {markerCost} Rogue's Markers to reveal this detail.";
                return;
            }

            var nextWings = bp.Wings.ToList();
            nextWings[req.WingIndex] = updatedWing;
            var nextBpItem = bpEntry.Item with { HeistBlueprint = bp with { Wings = nextWings } };

            // All preconditions verified under Saves.Atomically's lock; any mid-transaction failure throws to roll back.
            if (fromMain > 0)
                mainPay = items.Pay(PlayerInventories.MainInventory, coinHash, fromMain)
                    ?? throw new InvalidOperationException("Main inventory Rogue's Markers changed during blueprint reveal");
            if (fromLocker > 0)
                lockerPay = items.Pay(HeistTables.HeistLockerRow, coinHash, fromLocker)
                    ?? throw new InvalidOperationException("Heist Locker Rogue's Markers changed during blueprint reveal");
            if (items.Remove(HeistTables.BlueprintMissionRow, bpEntry.Id) is null)
                throw new InvalidOperationException("Blueprint disappeared during reveal");
            removedBpId = bpEntry.Id;
            replaced = items.Add(HeistTables.BlueprintMissionRow, bpEntry.X, bpEntry.Y, nextBpItem);
            updatedProgress = HeistProgress.Update(player, p => HeistProgress.DecrementReveal(p, req.RevealerRow));
        });

        if (failure is not null)
        {
            await PlayerOf(player).Tell(failure);
            return true;
        }
        if (replaced is null || updatedProgress is null) return true;

        await SendMarkerPayments(player, items, mainPay, lockerPay);
        await PlayerOf(player).Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(HeistTables.BlueprintMissionRow),
                items.NextRevision(HeistTables.BlueprintMissionRow), [removedBpId], [replaced]),
            $"0x00bf update revealed blueprint {replaced.Id} (wing {req.WingIndex}, target 0x{req.RoomTarget:x2})");
        await PlayerOf(player).Send(
            InstanceHeist.RevealResult(req.WingIndex, req.RoomTarget, req.RevealerRow),
            $"0x023b reveal blueprint ack (wing {req.WingIndex}, target 0x{req.RoomTarget:x2}, revealer {req.RevealerRow})");
        await HeistProgress.SendState(player, updatedProgress, "after blueprint reveal");
        module.Log($"{PlayerOf(player).Character}: revealed blueprint wing {req.WingIndex} target 0x{req.RoomTarget:x2} for {markerCost} Markers");
        return true;
    }

    static bool TryApplyReveal(HeistWing wing, byte target, out HeistWing updated, out int baseCost)
    {
        updated = wing;
        baseCost = 0;
        if (target == InstanceHeist.RevealTargetWing)
        {
            if (wing.Revealed) return false;
            updated = wing with { Revealed = true };
            baseCost = 2500;
            return true;
        }
        if (!wing.Revealed) return false;
        if (target == InstanceHeist.RevealTargetEscapeRoute)
        {
            var rooms = wing.Rooms.ToList();
            int idx = rooms.FindIndex(r => !r.Revealed && (HeistTables.EscapeRoomRow == 0xff || r.Row == HeistTables.EscapeRoomRow));
            if (idx < 0) return false;
            rooms[idx] = rooms[idx] with { Revealed = true };
            updated = wing with { Rooms = rooms };
            baseCost = 600;
            return true;
        }
        if (target >= wing.Rooms.Count || wing.Rooms[target].Revealed) return false;
        var nextRooms = wing.Rooms.ToList();
        nextRooms[target] = nextRooms[target] with { Revealed = true };
        updated = wing with { Rooms = nextRooms };
        baseCost = 1000;
        return true;
    }

    // ---- C→S 0x027f: Assign Blueprint Wing Crew ----------------------------------------------------------------------

    public static async Task<bool> HandleSetBlueprintCrew(IModuleContext module, Player player, byte[] packet)
    {
        if (!Current(player) || player.PlayerLife == 0 || !HeistTables.IsRogueHarbour(player.Layout.Id)) return false;
        if (!InstanceHeist.TryReadSetBlueprintCrew(packet, out var req)) return true;

        var items = ItemsOf(player);
        uint removedBpId = 0;
        InventoryItem? replaced = null;

        Saves.Atomically(() =>
        {
            var bpEntry = items.Items(HeistTables.BlueprintMissionRow).FirstOrDefault();
            if (bpEntry?.Item.HeistBlueprint is not { Confirmed: false } bp) return;
            if (req.WingIndex >= bp.Wings.Count) return;

            var wing = bp.Wings[req.WingIndex];
            if (!wing.Revealed || req.Crew.Length != wing.Jobs.Count) return;

            var progress = HeistProgress.Load(player);
            var assigned = new HashSet<byte>();
            for (int i = 0; i < req.Crew.Length; i++)
            {
                byte rogue = req.Crew[i];
                if (rogue == InstanceHeist.EmptyRogueSlot) continue;
                if (rogue >= InstanceHeist.RogueCount || !assigned.Add(rogue)) return;
                var job = wing.Jobs[i];
                if (HeistProgress.EffectiveJobLevel(player, progress, rogue, job.Row) < job.Level) return;
            }

            var nextWings = bp.Wings.ToList();
            nextWings[req.WingIndex] = wing with { Crew = req.Crew };
            var nextBpItem = bpEntry.Item with { HeistBlueprint = bp with { Wings = nextWings } };

            if (items.Remove(HeistTables.BlueprintMissionRow, bpEntry.Id) is null)
                throw new InvalidOperationException("Blueprint disappeared during crew update");
            removedBpId = bpEntry.Id;
            replaced = items.Add(HeistTables.BlueprintMissionRow, bpEntry.X, bpEntry.Y, nextBpItem);
        });

        if (replaced is null) return true;

        await PlayerOf(player).Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(HeistTables.BlueprintMissionRow),
                items.NextRevision(HeistTables.BlueprintMissionRow), [removedBpId], [replaced]),
            $"0x00bf update blueprint {replaced.Id} wing {req.WingIndex} crew");
        module.Log($"{PlayerOf(player).Character}: set blueprint wing {req.WingIndex} crew [{string.Join(", ", req.Crew)}]");
        return true;
    }

    // ---- C→S 0x027e: Confirm Blueprint Plans -------------------------------------------------------------------------

    public static async Task<bool> HandleConfirmBlueprint(IModuleContext module, Player player)
    {
        if (!Current(player) || player.PlayerLife == 0 || !HeistTables.IsRogueHarbour(player.Layout.Id)) return false;

        var items = ItemsOf(player);
        uint coinHash = InstanceEntities.ObjectTypeHash(HeistTables.HeistCoinPath);

        string? failure = null;
        int revealedWingCount = 0;
        int markerCost = 0;
        uint removedBpId = 0;
        PlayerInventories.Payment? mainPay = null, lockerPay = null;
        InventoryItem? replaced = null;

        Saves.Atomically(() =>
        {
            var bpEntry = items.Items(HeistTables.BlueprintMissionRow).FirstOrDefault();
            if (bpEntry?.Item.HeistBlueprint is not { Confirmed: false } bp) return;

            var revealedWings = bp.Wings.Where(w => w.Revealed).ToArray();
            var progress = HeistProgress.Load(player);
            if (revealedWings.Length == 0 || !AreRevealedWingsQualified(player, progress, revealedWings))
            {
                failure = "Assign qualified rogues to every job slot in your revealed wings first.";
                return;
            }

            revealedWingCount = revealedWings.Length;
            markerCost = Math.Max(200, revealedWingCount * (250 + bp.AreaLevel * 8));
            if (!TryComputeMarkerSplit(items, coinHash, markerCost, out int fromMain, out int fromLocker))
            {
                failure = $"Requires {markerCost} Rogue's Markers to confirm this Grand Heist.";
                return;
            }

            var confirmedItem = bpEntry.Item with { HeistBlueprint = bp with { Confirmed = true } };
            if (fromMain > 0)
                mainPay = items.Pay(PlayerInventories.MainInventory, coinHash, fromMain)
                    ?? throw new InvalidOperationException("Main inventory Rogue's Markers changed during blueprint confirm");
            if (fromLocker > 0)
                lockerPay = items.Pay(HeistTables.HeistLockerRow, coinHash, fromLocker)
                    ?? throw new InvalidOperationException("Heist Locker Rogue's Markers changed during blueprint confirm");
            if (items.Remove(HeistTables.BlueprintMissionRow, bpEntry.Id) is null)
                throw new InvalidOperationException("Blueprint disappeared during confirmation");
            removedBpId = bpEntry.Id;
            replaced = items.Add(HeistTables.BlueprintMissionRow, bpEntry.X, bpEntry.Y, confirmedItem);
        });

        if (failure is not null)
        {
            await PlayerOf(player).Tell(failure);
            return true;
        }
        if (replaced is null) return true;

        await SendMarkerPayments(player, items, mainPay, lockerPay);
        await PlayerOf(player).Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(HeistTables.BlueprintMissionRow),
                items.NextRevision(HeistTables.BlueprintMissionRow), [removedBpId], [replaced]),
            $"0x00bf confirm blueprint {replaced.Id}");
        await HeistProgress.SendState(player, reason: "after blueprint confirmed");

        if (!LevelGenerator.PortsPlugin(HeistTables.HeistPluginId))
            await PlayerOf(player).Tell($"Grand Heist plans confirmed ({revealedWingCount} wing(s), {markerCost} Markers). Grand Heist wing portal entry requires the unported HeistPlugin terrain generator.");
        else
            await PlayerOf(player).Tell($"Grand Heist plans confirmed ({revealedWingCount} wing(s)).");
        module.Log($"{PlayerOf(player).Character}: confirmed blueprint {replaced.Id} ({revealedWingCount} wings, {markerCost} Markers)");
        return true;
    }

    static bool AreRevealedWingsQualified(Player player, HeistProgress.ProgressData progress, HeistWing[] revealedWings)
    {
        foreach (var wing in revealedWings)
        {
            if (wing.Crew.Count != wing.Jobs.Count) return false;
            var seen = new HashSet<byte>();
            for (int i = 0; i < wing.Jobs.Count; i++)
            {
                byte rogue = wing.Crew[i];
                if (rogue >= InstanceHeist.RogueCount || !seen.Add(rogue)) return false;
                var job = wing.Jobs[i];
                if (HeistProgress.EffectiveJobLevel(player, progress, rogue, job.Row) < job.Level) return false;
            }
        }
        return true;
    }

    // ---- C→S 0x0279: Sign Contract -----------------------------------------------------------------------------------

    public static async Task<bool> HandleSignContract(IModuleContext module, Player player, byte[] packet)
    {
        if (!Current(player) || player.PlayerLife == 0 || !HeistTables.IsRogueHarbour(player.Layout.Id)) return false;
        if (!InstanceHeist.TryReadSignContract(packet, out var rogues)) return true;

        var items = ItemsOf(player);
        var contractEntry = items.Items(HeistTables.ContractMissionRow).FirstOrDefault();
        if (contractEntry is null || ItemsFor(player).ByHash(contractEntry.Item.TypeHash) is not { Class: "HeistContract" } contractType)
        {
            await PlayerOf(player).Tell("Place a Heist Contract into Adiyah's contract slot first.");
            return true;
        }
        var seen = new HashSet<byte>();
        if (rogues.Length == 0 || rogues.Any(r => r >= InstanceHeist.RogueCount || !seen.Add(r)))
        {
            await PlayerOf(player).Tell("Select valid, distinct rogues for this contract.");
            return true;
        }

        // Heist biome .dgr graphs (HeistBunker*, HeistMines*, etc.) have 0 nodes and are built dynamically by HeistPlugin.
        // Never fake a broken area transition or destroy the player's contract/Markers while HeistPlugin is unported.
        if (!LevelGenerator.PortsPlugin(HeistTables.HeistPluginId))
        {
            await HeistProgress.SendState(player, reason: "contract signed (terrain plugin pending)");
            await PlayerOf(player).Tell($"{contractType.Name} validated with {RogueNames(rogues)}. Contract area portals require the unported HeistPlugin terrain generator; your contract and Markers were kept.");
            module.Log($"{PlayerOf(player).Character}: 0x0279 signed {contractType.Name} with [{string.Join(", ", rogues)}] (HeistPlugin unported; kept item)");
            return true;
        }

        await HeistProgress.SendState(player, reason: "contract signed");
        return true;
    }

    static string RogueNames(byte[] rogues) =>
        string.Join(", ", rogues.Select(r => r < HeistTables.Rogues.Length ? HeistTables.Rogues[r].Name : $"Rogue {r}"));

    // ---- Rogue's Marker payment helpers ------------------------------------------------------------------------------

    static bool TryComputeMarkerSplit(PlayerInventories items, uint coinHash, int cost, out int fromMain, out int fromLocker)
    {
        int mainHave = items.Count(PlayerInventories.MainInventory, coinHash);
        int lockerHave = items.Count(HeistTables.HeistLockerRow, coinHash);
        if ((long)mainHave + lockerHave < cost)
        {
            fromMain = fromLocker = 0;
            return false;
        }
        fromMain = Math.Min(mainHave, cost);
        fromLocker = cost - fromMain;
        return true;
    }

    static async Task SendMarkerPayments(Player player, PlayerInventories items,
        PlayerInventories.Payment? mainPay, PlayerInventories.Payment? lockerPay)
    {
        if (mainPay is not null)
            await PlayerOf(player).Send(
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), mainPay.Removed, mainPay.Changed),
                "0x00bf pay Rogue's Markers from main inventory");
        if (lockerPay is not null)
            await PlayerOf(player).Send(
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(HeistTables.HeistLockerRow),
                    items.NextRevision(HeistTables.HeistLockerRow), lockerPay.Removed, lockerPay.Changed),
                "0x00bf pay Rogue's Markers from Heist Locker");
    }
}
