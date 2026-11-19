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
        GameEvents.EnterMessages.Add(id, conn =>
            PlayerOf(conn).AreaId == HubId && DeepwaterPlanner.Message(conn, DeepwaterPlanner.Current(conn)) is { } border
                ? [("0x02c9 DeepwaterHubRules mode 1 (border modifiers)", border)] : []);
        GameEvents.NpcOptionPicked.Add(id, (conn, talk) => ValerieOption(module, conn, talk));
        // Offer 130 (Valerie's tutorial chart) taken: the chart device opens, as her 3930's script says after the reward.
        GameEvents.MessagesHandled.Add(id, async (conn, _) =>
        {
            if (!_states.TryGetValue(PlayerOf(conn), out var state) || !state.DeviceAfterReward || conn.NpcRewardOffer is not null) return;
            state.DeviceAfterReward = false;
            if (!ClientHasQuestFlag(conn, TutorialChartTaken)) return;
            // LQF 1820 DeepwaterQuestChartTaken once the chart is taken (G: the trigger).
            if (!HasLeagueFlag(conn, DeepwaterConstants.ChartTakenLeagueFlag))
            {
                var (remote, stream, ciphers) = conn.Wire;
                await SetLeagueQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.ChartTakenLeagueFlag]);
            }
            await PlayerOf(conn).Send(InstanceWaypoints.OpenUi(ChartUi), $"0x007b open DeepwaterChartWindow (kind 0x{ChartUi:x2}) after Valerie's chart");
        });
        GameEvents.InventoryRefusal.Add(id, DeepwaterLocker.Refusal);
        GameEvents.InventoryPlace.Add(id, (conn, place) => Task.FromResult(DeepwaterLocker.RefusedPlace(module, conn, place)));
        // C→S 0x0111: Ctrl + click into or out of the open Chart Locker.
        GameEvents.Packet(InstanceItems.LockerMoveId).Add(id, async (conn, bytes) =>
        {
            if (bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            await DeepwaterLocker.QuickMove(module, conn, InstanceItems.ReadLeagueWindowMove(bytes));
            return true;
        });
    }

    // ---- the deck's objects ----

    async Task SpawnDeck(IModuleContext module, ClientState conn)
    {
        var player = PlayerOf(conn);
        var state = _states.GetOrCreateValue(player);
        (state.Locker, state.Portal, state.LockerOpen) = (0, 0, false);
        (state.Armed, state.ArmedPlan, state.PortalOpenAt, state.Confirm) = (null, null, 0, null);
        if (player.AreaId != HubId) return;
        var lines = conn.Layout.Level?.RoomObjects ?? [];
        if (lines.FirstOrDefault(o => o.Type == DeepwaterLocker.ChartLocker) is { } locker
            && await SpawnStateObject(module, conn, locker, "open", 0) is { } l)
            (state.Locker, state.LockerAt, state.LockerReach) = (l.Id, (locker.X, locker.Y), l.Reach);
        if (lines.FirstOrDefault(o => o.Type == ChartPortal) is { } portal
            && await SpawnStateObject(module, conn, portal, "ready", 0) is { } p)
            (state.Portal, state.PortalAt, state.PortalReach) = (p.Id, (portal.X, portal.Y), p.Reach);
    }

    // A server object of the deck with its StateMachine's defaults and one state set, as the rowboat.
    static async Task<(uint Id, int Reach)?> SpawnStateObject(IModuleContext module, ClientState conn, GameServer.Data.Terrain.Generation.PlacedTileObject at, string name, int value)
    {
        var player = PlayerOf(conn);
        var states = ObjectTypes.All().Where(t => t.Type == at.Type).Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault();
        if (QuestObjectTypes.Find(at.Type) is not { } info || states is null || !QuestObject.CanSerialize(info.Components, true))
        {
            module.Log($"{player.Character}: {at.Type} is not an object the server writes ({string.Join(", ", QuestObjectTypes.Find(at.Type)?.Components ?? [])})");
            return null;
        }
        uint id = conn.TakeObjectId();
        var blob = new QuestObject(at.X, at.Y, info.Components, at.Rotation, at.Z, string.IsNullOrEmpty(at.Model) || at.Model == info.AnimatedModel ? null : at.Model)
        { StateMachine = states.Set(name, value) }.Blob();
        if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(at.Type), blob),
            $"0x02bb {at.Type.Split('/')[^1]} 0x{id:x} at ({at.X}, {at.Y}), {name} {value}")) return null;
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        if (info.Blocking) conn.SetBlocker(id, at.X, at.Y, Math.Max(1, info.Size));
        return (id, info.Size + 2);
    }

    static async Task SetState(ClientState conn, uint id, string type, string name, int value)
    {
        var states = ObjectTypes.All().Where(t => t.Type == type).Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault();
        if (id == 0 || states is null || QuestObjectTypes.Find(type) is not { } info) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        await global::Game.SendWorldRecord(PlayerOf(conn), id, InstanceEntities.StateMachineUpdate(id, (byte)index, states.Set(name, value)),
            $"0x02bc {type.Split('/')[^1]} 0x{id:x} {name} = {value}");
    }

    /// <summary>The deck's Bathysphere <c>ready</c>: 0 closed, 1 opening, 2 open (G for 2's use).</summary>
    internal async Task SetPortalReady(ClientState conn, int ready)
    {
        if (_states.TryGetValue(PlayerOf(conn), out var state) && state.Portal != 0)
            await SetState(conn, state.Portal, ChartPortal, "ready", ready);
    }

    /// <summary>A click on the deck's Chart Locker or Bathysphere; false for another object.</summary>
    async Task<bool> UseDeck(IModuleContext module, ClientState conn, InstanceTransfer.UseSkillOnObject use, double at)
    {
        var player = PlayerOf(conn);
        if (player.AreaId != HubId || !_states.TryGetValue(player, out var state)) return false;
        if (state.Locker != 0 && use.TargetId == state.Locker)
        {
            if (!await WorldOf(conn).WalkUpTo(state.LockerAt.X, state.LockerAt.Y, state.LockerReach, at)) return true;
            state.LockerOpen = true;
            conn.ModuleWindow = DeepwaterLocker.LockerWindow;
            await SetState(conn, state.Locker, DeepwaterLocker.ChartLocker, "open", 1);
            await player.Send(InstanceWaypoints.OpenUi(DeepwaterLocker.LockerUi, state.Locker),
                $"0x007b open DeepwaterLockerWindow (kind 0x{DeepwaterLocker.LockerUi:x2}, inventory {DeepwaterLocker.LockerRow}) at the ChartLocker 0x{state.Locker:x} (M: the u32)");
            return true;
        }
        if (state.Portal != 0 && use.TargetId == state.Portal) return await UsePortal(module, conn, state, at);
        return false;
    }

    /// <summary>The locker's lid closes when its window was replaced or the player walked away (the client sends nothing
    /// when the window closes).</summary>
    async Task TickDeck(ClientState conn)
    {
        if (!_states.TryGetValue(PlayerOf(conn), out var state)) return;
        await TickPortal(conn, state);
        if (!state.LockerOpen) return;
        var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        long dx = here.X - state.LockerAt.X, dy = here.Y - state.LockerAt.Y, far = state.LockerReach + 30;
        if (conn.ModuleWindow == DeepwaterLocker.LockerWindow && dx * dx + dy * dy <= far * far) return;
        state.LockerOpen = false;
        if (conn.ModuleWindow == DeepwaterLocker.LockerWindow) conn.ModuleWindow = null;
        await SetState(conn, state.Locker, DeepwaterLocker.ChartLocker, "open", 0);
    }

    // ---- the hub's map owner ----

    /// <summary>The player owns this hub instance (the first character in it, G).</summary>
    bool HubOwner(ClientState conn)
    {
        if (conn.Instance is not { } instance) return false;
        string me = PlayerOf(conn).Character;
        string owner;
        lock (_hubOwners) owner = _hubOwners.GetValue(instance, _ => me);
        return owner.Equals(me, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>On The Sovereign the owner holds IsMapOwner (QuestFlags 2020): Valerie's "Chart Area" and "Plan Voyage" and
    /// the Bathysphere need it (H, the client's gates). DeepwaterBoatUnlocked (league quest flag 1834, the ChartLocker's gate)
    /// is set for a character aboard before Valerie's rows applied it (G). The Maiden Voyage's 1822 catches up here too.</summary>
    async Task SyncHub(IModuleContext module, ClientState conn, List<string> clearing)
    {
        var (remote, stream, ciphers) = conn.Wire;
        bool owner = HubOwner(conn);
        clearing.Remove(DeepwaterConstants.MapOwner);
        if (!owner && conn.HasQuestFlag(DeepwaterConstants.MapOwner)) clearing.Add(DeepwaterConstants.MapOwner);
        if (owner && !conn.HasQuestFlag(DeepwaterConstants.MapOwner)) await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.MapOwner]);
        bool unlocked;
        lock (conn.Monsters) unlocked = conn.LeagueQuestFlags.Contains(DeepwaterConstants.BoatUnlockedLeagueFlag);
        if (!unlocked) await SetLeagueQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.BoatUnlockedLeagueFlag]);
        await FourCharts(module, conn, live: true);
    }

    // ---- Valerie ----

    /// <summary>GameEvents.NpcOptionPicked for Valerie's rows: the row's league quest flag columns (NPCTalk <c>__col15/16/18</c>,
    /// which the server applies only for its own NPCs), and her device rows: "Chart Area" opens the chart device (0x7b
    /// 0x67) and "Plan Voyage" the planner (mode 1, then 0x7b 0x6a) after FinishNPCChat (M: the ruleset event names are
    /// server-side). "Take Chart" (3930) opens the device once its reward was taken. <c>CallOnClient( ChartCallout )</c> (a
    /// voice line) has no known message and is skipped.</summary>
    async Task<bool> ValerieOption(IModuleContext module, ClientState conn, NpcCatalog.Talk talk)
    {
        if (Array.IndexOf(ValerieNpcs, talk.Npc) < 0) return true;
        var player = PlayerOf(conn);
        var (remote, stream, ciphers) = conn.Wire;
        string? missing;
        lock (conn.Monsters)
            missing = talk.LeagueFlagsRequired.FirstOrDefault(f => !conn.LeagueQuestFlags.Contains(f)) is int need and > 0 ? $"needs league flag {need}"
                : talk.LeagueFlagsRefused.FirstOrDefault(conn.LeagueQuestFlags.Contains) is int refused and > 0 ? $"refused by league flag {refused}" : null;
        if (missing is not null)
        {
            module.Log($"{player.Character} <= Valerie NPCTalk {talk.Row} \"{talk.Option}\": refused ({missing})");
            return false;
        }
        if (talk.LeagueFlagsSet.Count > 0) await SetLeagueQuestFlags(remote, stream, ciphers, conn, talk.LeagueFlagsSet);
        bool device = talk.Script.Contains(DeviceEvent, StringComparison.Ordinal);
        bool chart = talk.Script.Contains(DeviceEvent1x1, StringComparison.Ordinal);
        if (talk.Script.Contains("ChartCallout", StringComparison.Ordinal))
            module.Log($"{player.Character}: Valerie NPCTalk {talk.Row} CallOnClient( ChartCallout ) skipped (no known message)");
        if (device && talk.RewardOffer is not null)
        {
            // 3930 "Take Chart": the device (and LQF 1820) once the reward is taken.
            _states.GetOrCreateValue(player).DeviceAfterReward = true;
            return true;
        }
        if (!device) return true;
        conn.TalkingTo = 0;
        conn.InNpcTalk = false;
        conn.NpcFinishFlag = null;
        conn.NpcTalkScript = null;
        await player.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: Valerie's dialogue ends (FinishNPCChat, NPCTalk {talk.Row})");
        if (chart)
            await player.Send(InstanceWaypoints.OpenUi(ChartUi), $"0x007b open DeepwaterChartWindow (kind 0x{ChartUi:x2}, NPCTalk {talk.Row} {DeviceEvent1x1})");
        else
        {
            await DeepwaterPlanner.Send(conn, DeepwaterPlanner.Current(conn));
            await player.Send(InstanceWaypoints.OpenUi(VoyageUi), $"0x007b open DeepwaterVoyageWindow (kind 0x{VoyageUi:x2}, NPCTalk {talk.Row} {DeviceEvent})");
        }
        return false;
    }

    // ---- ruleset 172 ----

    /// <summary>C→S 0x2ca on DeepwaterHubRules; false for another ruleset's action.</summary>
    async Task<bool> HubAction(IModuleContext module, ClientState conn, byte[] bytes)
    {
        (DeepwaterHubRuleset.Op Op, byte[] Args)? action;
        try { action = DeepwaterHubRuleset.Read(bytes); }
        catch (InvalidDataException) { return false; }
        if (action is not { } a) return false;
        string who = PlayerOf(conn).Character;
        switch (a.Op)
        {
            case DeepwaterHubRuleset.Op.TakeAllLoot:
                await DeepwaterExtraction.TakeFromCollection(module, conn, null);
                break;
            case DeepwaterHubRuleset.Op.MoveStoredChart when a.Args.Length >= 2:
                await DeepwaterPlanner.Move(module, conn, a.Args[0], a.Args[1]);
                break;
            case DeepwaterHubRuleset.Op.RotateStoredChart when a.Args.Length >= 1:
                await DeepwaterPlanner.Rotate(module, conn, a.Args[0]);
                break;
            case DeepwaterHubRuleset.Op.DeleteStoredChart when a.Args.Length >= 1:
                await DeepwaterPlanner.Delete(module, conn, a.Args[0]);
                break;
            case DeepwaterHubRuleset.Op.Open1x1Mission:
                await StartChart(module, conn);
                break;
            case DeepwaterHubRuleset.Op.OpenMission:
                await StartVoyage(module, conn, a.Args);
                break;
            case DeepwaterHubRuleset.Op.ConfirmEnterMission:
                await ConfirmStart(module, conn);
                break;
            case DeepwaterHubRuleset.Op.RerollBorderModifiers:
                await DeepwaterPlanner.Reroll(module, conn);
                break;
            default:
                module.Log($"{who} <= 0x02ca DeepwaterHubRules {a.Op} ({Convert.ToHexString(a.Args)}): not handled");
                break;
        }
        return true;
    }
}
