using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

// The Sovereign's deck: the Chart Locker and the Bathysphere (ChartPortal) where hub.arm places them, the hub's map owner,
// Valerie's chart device and voyage planner, and the deck's ruleset 172 ops (DeepwaterHubRuleset, DeepwaterPlanner).
public sealed partial class DeepwaterModule
{
    /// <summary>The Bathysphere on the deck (<c>ChartPortal.ot</c>, MainBathysphere01.ao): targetable only with IsMapOwner and
    /// its shared state <c>ready</c> 1 (the opening) or 2 (open).</summary>
    internal const string ChartPortal = "Metadata/Terrain/Leagues/Deepwater/Objects/ChartPortal";
    // NPCs rows whose NPCTalk rows carry the chart device and the planner: ValerieTutorial (Valerie 789 and ValeriePostStory 794
    // use its rows) and ValerieVesperRevive.
    static readonly int[] ValerieNpcs = [790, 793];
    /// <summary>S→C 0x7b kinds: DeepwaterChartWindow (the 1 × 1 device over inventory 103) and DeepwaterVoyageWindow (the
    /// board over 104). Both ignore the u32s and need ruleset 172 before 0x21 (03 §8).</summary>
    const byte ChartUi = 0x67, VoyageUi = 0x6a;
    const string DeviceEvent = "open_deepwater_device", DeviceEvent1x1 = "open_deepwater_device_1x1";
    /// <summary>QuestFlags 3554, offer 130's taken flag.</summary>
    const string TutorialChartTaken = "DeepwaterNPCQuestItemTaken";

    // The hub instance's map owner: its first character (G; the 3.29.1 notes say only the owner gets the device's options).
    readonly ConditionalWeakTable<InstanceState, string> _hubOwners = new();

    void LoadHub(IModuleContext module)
    {
        string id = module.Manifest.Id;
        // Mode 1 (the border modifiers and the reroll cost) after 0x1f, so the Voyage window has them whenever it opens.
        GameEvents.EnterMessages.Add(id, player =>
            PlayerOf(player).AreaId == HubId && DeepwaterPlanner.Message(player, DeepwaterPlanner.Current(player)) is { } border
                ? [("0x02c9 DeepwaterHubRules mode 1 (border modifiers)", border)] : []);
        GameEvents.NpcUsesLeagueFlags.Add(id, DeepwaterProgression.UsesLeagueFlags);
        GameEvents.NpcOptionPicked.Add(id, async (player, talk) =>
            await DeepwaterUpgrades.TryTurnIn(module, player, talk)
            && await DeepwaterCrafting.TryOpenOption(module, player, talk, HubOwner(player))
            && await DeepwaterProgression.OnNpcOption(module, player, talk, HubOwner(player),
                onVesperAwoke: c => DeepwaterUpgrades.UpdateStage1Bench(c, disableTargetable: true))
            && await ValerieOption(module, player, talk));
        // Offer 130 (Valerie's tutorial chart): the chart device opens with the reward window, as her 3930's script says
        // (FinishNPCChat, open_deepwater_device_1x1). The client lets the chart be picked from the reward window only while
        // the device's window is open (live 2026-11-19: no C→S 0x88 or 0x8a without it, and the pick and 0x88 with it).
        // Once the reward is taken the device opens again, where the chart goes (tracker 1976, QF 3554).
        GameEvents.MessagesHandled.Add(id, async (player, ids) =>
        {
            await DeepwaterCursedDucats.OnMessagesHandled(player, ids);
            await DeepwaterProgression.OnMessagesHandled(module, player, ids);
            if (!_states.TryGetValue(PlayerOf(player), out var state) || !state.DeviceAfterReward) return;
            if (player.NpcRewardOffer is not null)
            {
                if (player.NpcRewardOffer != DeepwaterCharts.TutorialOfferRow || state.DeviceWithReward) return;
                state.DeviceWithReward = true;
                await PlayerOf(player).Send(InstanceWaypoints.OpenUi(ChartUi), $"0x007b open DeepwaterChartWindow (kind 0x{ChartUi:x2}) with Valerie's chart offer");
                return;
            }
            (state.DeviceAfterReward, state.DeviceWithReward) = (false, false);
            if (!ClientHasQuestFlag(player, TutorialChartTaken)) return;
            // LQF 1820 DeepwaterQuestChartTaken once the chart is taken (G: the trigger).
            if (!HasLeagueFlag(player, DeepwaterConstants.ChartTakenLeagueFlag))
                await SetLeagueQuestFlags(player, [DeepwaterConstants.ChartTakenLeagueFlag]);
            await PlayerOf(player).Send(InstanceWaypoints.OpenUi(ChartUi), $"0x007b open DeepwaterChartWindow (kind 0x{ChartUi:x2}) after Valerie's chart");
        });
        GameEvents.InventoryRefusal.Add(id, (player, row, item) =>
            DeepwaterLocker.Refusal(player, row, item)
            ?? DeepwaterCrafting.Refusal(player, row, item)
            ?? DeepwaterCursedDucats.Refusal(player, row, item));
        GameEvents.InventoryPlace.Add(id, (player, place) => Task.FromResult(
            DeepwaterLocker.RefusedPlace(module, player, place)
            || DeepwaterCursedDucats.RefusedPlace(module, player, place)));
        // Refuse 0x0028 pickups from server-managed Deepwater grids (ActiveRow 109, ChartedRow 104, OutputRow 106).
        GameEvents.InventoryPick.Add(id, (player, pick) => Task.FromResult(
            PlayerInventories.Row(pick.InventoryId) is DeepwaterLocker.ActiveRow or DeepwaterLocker.ChartedRow or DeepwaterCrafting.OutputRow));
        // C→S 0x010e: Ctrl + click into or out of the open Chart Window slot (103).
        GameEvents.Packet(InstanceItems.MissionSlotMoveId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            await DeepwaterLocker.QuickMoveChart(module, player, InstanceItems.ReadLeagueWindowMove(bytes));
            return true;
        });
        // C→S 0x010f: Ctrl + click into or out of Vesper's Allflame Crafting input slot (105).
        GameEvents.Packet(InstanceItems.CraftingSlotMoveId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            await DeepwaterCrafting.QuickMove(module, player, InstanceItems.ReadLeagueWindowMove(bytes));
            return true;
        });
        // C→S 0x0111: Ctrl + click into or out of the open Chart Locker (112).
        GameEvents.Packet(InstanceItems.LockerMoveId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            await DeepwaterLocker.QuickMove(module, player, InstanceItems.ReadLeagueWindowMove(bytes));
            return true;
        });
        // C→S 0x0115: Ctrl + click Cursed Ducats between MainInventory (0), active sockets (115), and storage (116).
        GameEvents.Packet(InstanceItems.SocketStorageMoveId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            await DeepwaterCursedDucats.QuickMove(module, player, InstanceItems.ReadLeagueWindowMove(bytes));
            return true;
        });
    }

    // ---- the deck's objects ----

    async Task SpawnDeck(IModuleContext module, Player player)
    {
        var modPlayer = PlayerOf(player);
        var state = _states.GetOrCreateValue(modPlayer);
        (state.Locker, state.Portal, state.LockerOpen) = (0, 0, false);
        (state.Armed, state.ArmedPlan, state.PortalOpenAt, state.Confirm) = (null, null, 0, null);
        if (modPlayer.AreaId != HubId) return;
        var lines = player.Layout.Level?.RoomObjects ?? [];
        if (lines.FirstOrDefault(o => o.Type == DeepwaterLocker.ChartLocker) is { } locker
            && await SpawnStateObject(module, player, locker, "open", 0) is { } l)
            (state.Locker, state.LockerAt, state.LockerReach) = (l.Id, (locker.X, locker.Y), l.Reach);
        if (lines.FirstOrDefault(o => o.Type == ChartPortal) is { } portal
            && await SpawnStateObject(module, player, portal, "ready", 0) is { } p)
            (state.Portal, state.PortalAt, state.PortalReach) = (p.Id, (portal.X, portal.Y), OffFloorReach(player, (portal.X, portal.Y), p.Reach));
        await DeepwaterCrafting.SpawnVats(module, player, HubOwner(player));
        await DeepwaterProgression.SpawnHub(module, player);
        await DeepwaterUpgrades.SpawnBenchProps(module, player);
    }

    /// <summary>The Bathysphere hangs over the water, about 32 cells from the nearest deck cell (live 2026-11-19): a walk-up
    /// size whose range (InWalkUpRange: the distance within twice the size + 3) takes in the nearest walkable cell, plus a
    /// little slack for the walk's stop.</summary>
    static int OffFloorReach(Player player, (int X, int Y) at, int reach, int search = 48)
    {
        var layout = player.Layout;
        double nearest = double.MaxValue;
        for (int y = at.Y - search; y <= at.Y + search; y++)
            for (int x = at.X - search; x <= at.X + search; x++)
                if (layout.WalkableFor(x, y, 1))
                    nearest = Math.Min(nearest, Math.Sqrt((double)(x - at.X) * (x - at.X) + (double)(y - at.Y) * (y - at.Y)));
        return nearest == double.MaxValue ? reach : Math.Max(reach, (int)Math.Ceiling((nearest + 2) / 2));
    }

    // A server object of the deck with its StateMachine's defaults and one state set, as the rowboat.
    static async Task<(uint Id, int Reach)?> SpawnStateObject(IModuleContext module, Player player, GameServer.Data.Terrain.Generation.PlacedTileObject at, string name, int value)
    {
        var modPlayer = PlayerOf(player);
        var states = ObjectTypes.All().Where(t => t.Type == at.Type).Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault();
        if (QuestObjectTypes.Find(at.Type) is not { } info || states is null || !QuestObject.CanSerialize(info.Components, true))
        {
            module.Log($"{modPlayer.Character}: {at.Type} is not an object the server writes ({string.Join(", ", QuestObjectTypes.Find(at.Type)?.Components ?? [])})");
            return null;
        }
        uint id = player.TakeObjectId();
        var blob = new QuestObject(at.X, at.Y, info.Components, at.Rotation, at.Z, string.IsNullOrEmpty(at.Model) || at.Model == info.AnimatedModel ? null : at.Model)
        { StateMachine = states.Set(name, value) }.Blob();
        if (!await global::Game.SendWorldCreate(modPlayer, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(at.Type), blob),
            $"0x02bb {at.Type.Split('/')[^1]} 0x{id:x} at ({at.X}, {at.Y}), {name} {value}")) return null;
        await global::Game.SendWorldRecord(modPlayer, id, InstanceProtocol.EnterWakeZone(id));
        if (info.Blocking) player.SetBlocker(id, at.X, at.Y, Math.Max(1, info.Size));
        return (id, info.Size + 2);
    }

    static async Task SetState(Player player, uint id, string type, string name, int value)
    {
        var states = ObjectTypes.All().Where(t => t.Type == type).Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault();
        if (id == 0 || states is null || QuestObjectTypes.Find(type) is not { } info) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        await global::Game.SendWorldRecord(PlayerOf(player), id, InstanceEntities.StateMachineUpdate(id, (byte)index, states.Set(name, value)),
            $"0x02bc {type.Split('/')[^1]} 0x{id:x} {name} = {value}");
    }

    /// <summary>The deck's Bathysphere <c>ready</c>: 0 closed, 1 opening, 2 open (G for 2's use).</summary>
    internal async Task SetPortalReady(Player player, int ready)
    {
        if (_states.TryGetValue(PlayerOf(player), out var state) && state.Portal != 0)
            await SetState(player, state.Portal, ChartPortal, "ready", ready);
    }

    /// <summary>A click on the deck's Chart Locker or Bathysphere; false for another object.</summary>
    async Task<bool> UseDeck(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double at)
    {
        var modPlayer = PlayerOf(player);
        if (modPlayer.AreaId != HubId || !_states.TryGetValue(modPlayer, out var state)) return false;
        if (state.Locker != 0 && use.TargetId == state.Locker)
        {
            if (!await WorldOf(player).WalkUpTo(state.LockerAt.X, state.LockerAt.Y, state.LockerReach, at)) return true;
            state.LockerOpen = true;
            player.ModuleWindow = DeepwaterLocker.LockerWindow;
            await SetState(player, state.Locker, DeepwaterLocker.ChartLocker, "open", 1);
            await modPlayer.Send(InstanceWaypoints.OpenUi(DeepwaterLocker.LockerUi, state.Locker),
                $"0x007b open DeepwaterLockerWindow (kind 0x{DeepwaterLocker.LockerUi:x2}, inventory {DeepwaterLocker.LockerRow}) at the ChartLocker 0x{state.Locker:x} (M: the u32)");
            return true;
        }
        if (state.Portal != 0 && use.TargetId == state.Portal) return await UsePortal(module, player, state, at);
        if (await DeepwaterCrafting.UseVat(module, player, use, at, HubOwner(player))) return true;
        if (await DeepwaterUpgrades.UseStage1Bench(module, player, use, at, HubOwner(player))) return true;
        return false;
    }

    /// <summary>The locker's lid closes when its window was replaced or the player walked away (the client sends nothing
    /// when the window closes).</summary>
    async Task TickDeck(Player player)
    {
        if (!_states.TryGetValue(PlayerOf(player), out var state)) return;
        await TickPortal(player, state);
        // A map owner who left hands the deck to a player still on it (every 2 s, not every tick).
        double now = AreaTime(player);
        if (PlayerOf(player).AreaId == HubId && now >= state.NextOwnerCheck)
        {
            state.NextOwnerCheck = now + 2;
            if (!player.Character.HasQuestFlag(DeepwaterConstants.MapOwner) && HubOwner(player))
                await SetQuestFlags(player, [DeepwaterConstants.MapOwner]);
        }
        if (!state.LockerOpen) return;
        var here = player.Walk.Position(AreaTime(player));
        long dx = here.X - state.LockerAt.X, dy = here.Y - state.LockerAt.Y, far = state.LockerReach + 30;
        if (player.ModuleWindow == DeepwaterLocker.LockerWindow && dx * dx + dy * dy <= far * far) return;
        state.LockerOpen = false;
        if (player.ModuleWindow == DeepwaterLocker.LockerWindow) player.ModuleWindow = null;
        await SetState(player, state.Locker, DeepwaterLocker.ChartLocker, "open", 0);
    }

    // ---- the hub's map owner ----

    /// <summary>The player owns this hub instance: its first character, and when that one has left it (another area, a
    /// logout, a party that broke up), the next one who asks while there (G: the real game's hand-over is not known).</summary>
    bool HubOwner(Player player)
    {
        if (player.Instance is not { } instance) return false;
        string me = PlayerOf(player).Character;
        bool OwnerHere(string owner) => DeepwaterLoot.Members(player).Any(m => CharacterName(m).Equals(owner, StringComparison.OrdinalIgnoreCase));
        lock (_hubOwners)
        {
            string owner = _hubOwners.GetValue(instance, _ => me);
            if (owner.Equals(me, StringComparison.OrdinalIgnoreCase)) return true;
            if (OwnerHere(owner)) return false;
            _hubOwners.AddOrUpdate(instance, me);
            return true;
        }
    }

    /// <summary>On The Sovereign the owner holds IsMapOwner (QuestFlags 2020): Valerie's "Chart Area" and "Plan Voyage" and
    /// the Bathysphere need it (H, the client's gates). DeepwaterBoatUnlocked (league quest flag 1834, the ChartLocker's gate)
    /// is set for a character aboard before Valerie's rows applied it (G). The Maiden Voyage's 1822 catches up here too.</summary>
    async Task SyncHub(IModuleContext module, Player player, List<string> clearing)
    {
        bool owner = HubOwner(player);
        clearing.Remove(DeepwaterConstants.MapOwner);
        if (!owner && player.Character.HasQuestFlag(DeepwaterConstants.MapOwner)) clearing.Add(DeepwaterConstants.MapOwner);
        if (owner && !player.Character.HasQuestFlag(DeepwaterConstants.MapOwner)) await SetQuestFlags(player, [DeepwaterConstants.MapOwner]);
        if (!player.Character.LeagueQuestFlags.Contains(DeepwaterConstants.BoatUnlockedLeagueFlag))
            await SetLeagueQuestFlags(player, [DeepwaterConstants.BoatUnlockedLeagueFlag]);
        await FourCharts(module, player, live: true);
        await DeepwaterCursedDucats.SyncFlags(player);
        await DeepwaterUpgrades.TryGrantHubSulphurPurifier(module, player);
        await DeepwaterProgression.SyncQuestItemFlags(player);
    }

    // ---- Valerie ----

    /// <summary>GameEvents.NpcOptionPicked for Valerie's device rows ("Chart Area" opens 0x7b 0x67, "Plan Voyage" opens
    /// 0x7b 0x6a after FinishNPCChat; "Take Chart" 3930 opens the device alongside its reward window and again once taken).
    /// League-flag filtering and <c>LeagueFlagsSet</c> application are handled by <c>Npcs.RunNpcOption</c> via
    /// <see cref="DeepwaterProgression.UsesLeagueFlags"/>. <c>CallOnClient( ChartCallout )</c> (a voice line) has no known
    /// message and is skipped.</summary>
    async Task<bool> ValerieOption(IModuleContext module, Player player, NpcCatalog.Talk talk)
    {
        if (Array.IndexOf(ValerieNpcs, talk.Npc) < 0) return true;
        var modPlayer = PlayerOf(player);
        bool device = talk.Script.Contains(DeviceEvent, StringComparison.Ordinal);
        bool chart = talk.Script.Contains(DeviceEvent1x1, StringComparison.Ordinal);
        if (talk.Script.Contains("ChartCallout", StringComparison.Ordinal))
            module.Log($"{modPlayer.Character}: Valerie NPCTalk {talk.Row} CallOnClient( ChartCallout ) skipped (no known message)");
        if (device && talk.RewardOffer is not null)
        {
            // 3930 "Take Chart": the device with the reward window, and again (with LQF 1820) once the reward is taken.
            var deck = _states.GetOrCreateValue(modPlayer);
            (deck.DeviceAfterReward, deck.DeviceWithReward) = (true, false);
            return true;
        }
        if (!device) return true;
        if (talk.LeagueFlagsSet.Count > 0)
            await SetLeagueQuestFlags(player, talk.LeagueFlagsSet);
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcTalkScript = null;
        await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: Valerie's dialogue ends (FinishNPCChat, NPCTalk {talk.Row})");
        if (chart)
            await modPlayer.Send(InstanceWaypoints.OpenUi(ChartUi), $"0x007b open DeepwaterChartWindow (kind 0x{ChartUi:x2}, NPCTalk {talk.Row} {DeviceEvent1x1})");
        else
        {
            await DeepwaterPlanner.Send(player, DeepwaterPlanner.Current(player));
            await modPlayer.Send(InstanceWaypoints.OpenUi(VoyageUi), $"0x007b open DeepwaterVoyageWindow (kind 0x{VoyageUi:x2}, NPCTalk {talk.Row} {DeviceEvent})");
        }
        return false;
    }

    // ---- ruleset 172 ----

    /// <summary>C→S 0x2ca on DeepwaterHubRules; false for another ruleset's action.</summary>
    async Task<bool> HubAction(IModuleContext module, Player player, byte[] bytes)
    {
        (DeepwaterHubRuleset.Op Op, byte[] Args)? action;
        try { action = DeepwaterHubRuleset.Read(bytes); }
        catch (InvalidDataException) { return false; }
        if (action is not { } a) return false;
        string who = PlayerOf(player).Character;
        bool onHub = PlayerOf(player).AreaId == HubId;
        switch (a.Op)
        {
            case DeepwaterHubRuleset.Op.TakeAllLoot:
                await DeepwaterExtraction.TakeFromCollection(module, player, null);
                break;
            case DeepwaterHubRuleset.Op.MoveStoredChart when onHub && a.Args.Length >= 2:
                await DeepwaterPlanner.Move(module, player, a.Args[0], a.Args[1]);
                break;
            case DeepwaterHubRuleset.Op.RotateStoredChart when onHub && a.Args.Length >= 1:
                await DeepwaterPlanner.Rotate(module, player, a.Args[0]);
                break;
            case DeepwaterHubRuleset.Op.DeleteStoredChart when onHub && a.Args.Length >= 1:
                await DeepwaterPlanner.Delete(module, player, a.Args[0]);
                break;
            case DeepwaterHubRuleset.Op.Open1x1Mission:
                await StartChart(module, player);
                break;
            case DeepwaterHubRuleset.Op.OpenMission:
                await StartVoyage(module, player, a.Args);
                break;
            case DeepwaterHubRuleset.Op.DoCraft:
                await DeepwaterCrafting.DoCraft(module, player, a.Args, HubOwner(player));
                break;
            case DeepwaterHubRuleset.Op.ConfirmEnterMission:
                await ConfirmStart(module, player);
                break;
            case DeepwaterHubRuleset.Op.RerollBorderModifiers when onHub:
                if (_states.TryGetValue(PlayerOf(player), out var armedState) && armedState.ArmedPlan is { Voyage: true })
                {
                    module.Log($"{who} <= 0x02ca DeepwaterHubRules RerollBorderModifiers: refused (a Voyage is armed at the Bathysphere)");
                    break;
                }
                await DeepwaterPlanner.Reroll(module, player);
                break;
            default:
                module.Log($"{who} <= 0x02ca DeepwaterHubRules {a.Op} ({Convert.ToHexString(a.Args)}): not handled");
                break;
        }
        return true;
    }
}
