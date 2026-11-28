using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// Monastery of the Keepers (<c>WorldAreas#1923 ChayulaLeague</c>) interactive hub objects, hideout Wombgift Locker
/// station (<c>GraftLocker_Hideout</c>), Ailith (<c>ChayulaFarmerHub</c>/<c>ChayulaFarmerHideout</c>) &amp; The Dreamer
/// (<c>Chayula</c>) dialogue/quest handlers, and the three Brequel quests (<c>ChayulaTutorial</c>,
/// <c>ChayulaFindingBodyParts</c> with <c>BodyPart1..10</c>, and <c>ChayulaJuicingTheTree</c>).
/// </summary>
public static class BreachHub
{
    /// <summary>Monastery of the Keepers (<c>WorldAreas#1923</c>, HASH16 <c>0xcb44</c>).</summary>
    public const string HubAreaId = WaypointCatalog.ChayulaHub;

    public const string TreeTreeType = "Metadata/MiscellaneousObjects/Brequel/TreeTree";
    public const string HubBackDoorBlockerType = "Metadata/MiscellaneousObjects/Brequel/HubBackDoorBlocker";

    /// <summary>Owner token recorded in <c>Player.ModuleWindow</c> while the Genesis Tree window is open.</summary>
    public const string TreeWindow = "breach-tree";

    /// <summary>S→C <c>0x7b</c> OpenUi kind for <c>brequel_skill_tree_window</c> (<c>0x62</c> = 98, args 0,0,0,0).</summary>
    public const byte TreeUiKind = 0x62;

    // NPCs.dat rows for Chayula/Ailith NPCs.
    public const int NpcAilithWildMonster = 771;
    public const int NpcAilithWild = 772;
    public const int NpcAilithFirstEncounter = 773;
    public const int NpcAilithHub = 774;
    public const int NpcAilithHideout = 775;
    public const int NpcAilithWalk = 776;
    public const int NpcDreamer = 779;
    public const int NpcWordOfDreamer = 785;

    // QuestFlags names (client.db QuestFlags rows 3323..3343).
    public const string QfInChayulaLeagueArea = "IsChayulaLeagueArea";
    public const string QfBarrierBurned = "ChayulaNPCBarrierBurned";
    public static readonly string[] QfHaveBodyPart =
    [
        "HaveChayulaBodyPart1", "HaveChayulaBodyPart2", "HaveChayulaBodyPart3", "HaveChayulaBodyPart4", "HaveChayulaBodyPart5",
        "HaveChayulaBodyPart6", "HaveChayulaBodyPart7", "HaveChayulaBodyPart8", "HaveChayulaBodyPart9", "HaveChayulaBodyPart10",
    ];

    // Binary-verified LeagueQuestFlags ids (3.29.3c client table at 0x142fcb398).
    public const int LqfGraftLockerUnlocked = 533;         // ChayulaGraftLockerUnlocked (unlocks GraftLocker & GraftLocker_Hideout)
    public const int LqfMonasteryFirstTreeOpen = 535;      // AilithMonasteryFirstTreeOpen
    public const int LqfLeagueAreaUnlocked = 636;          // ChayulaLeagueAreaUnlocked (World panel pin)
    public const int LqfBackroomTreeSeen = 638;            // ChayulaBackroomTreeSeen
    public const int LqfTutorialEncounterComplete = 694;   // ChayulaCompletedTutorialEncounter
    public const int LqfTutorialCompleted = 1586;          // ChayulaNPCTutorialCompleted
    public const int LqfTreeJuicedTurnedInFirst = 1587;    // ChayulaTreeJuicedTurnedIn1..7 (1587..1593)
    public const int LqfTreeJuicedFirst = 1594;            // ChayulaTreeJuiced1..7 (1594..1600)
    public const int LqfTurnInBodyPartFirst = 1601;        // TurnInChayulaBodyPart1..10 (1601..1610)
    public const int LqfTurnInBodyPart9 = 1609;            // Opens HubBackDoorBlocker & TreeTree door_open=1
    public const int LqfTotalBodyPartsFirst = 1611;        // ChayulaTotalBodyPartsTurnedIn1..10 (1611..1620)
    public const int LqfFirstFruitGrown = 1622;            // ChayulaFirstFruitGrown
    public const int LqfEshAndTulDefeated = 1642;          // ChayulaEshAndTulDefeated
    public const int LqfSkyHiveCompleted = 1643;           // ChayulaSkyHiveCompleted

    /// <summary>BaseItemTypes Ids for the 10 Hiveborn Body Part quest items (rows 1629..1638).</summary>
    public static readonly string[] BodyPartItemIds =
    [
        "Metadata/Items/QuestItems/Chayula/BodyPart1",  // Horned Skull
        "Metadata/Items/QuestItems/Chayula/BodyPart2",  // Clasped Skull
        "Metadata/Items/QuestItems/Chayula/BodyPart3",  // Handmouth
        "Metadata/Items/QuestItems/Chayula/BodyPart4",  // Claw Bramble
        "Metadata/Items/QuestItems/Chayula/BodyPart5",  // Severed Limb
        "Metadata/Items/QuestItems/Chayula/BodyPart6",  // Strange Limb
        "Metadata/Items/QuestItems/Chayula/BodyPart7",  // Foul Hand
        "Metadata/Items/QuestItems/Chayula/BodyPart8",  // The Head of Vruun
        "Metadata/Items/QuestItems/Chayula/BodyPart9",  // Breachlord Limbs
        "Metadata/Items/QuestItems/Chayula/BodyPart10", // Spiked Limb
    ];

    static readonly uint[] BodyPartHashes = [.. BodyPartItemIds.Select(InstanceEntities.ObjectTypeHash)];

    // Exact hub.arm coordinates (Metadata/Terrain/Leagues/Breach/Hub/Rooms/hub.arm, rotation I).
    static readonly (int X, int Y, float Rot, float Z, string Model) TreePlacement =
        (402, 221, -1.309f, -250f, "Metadata/Terrain/Leagues/Breach/Doodads/BreachHandTreeBase.ao");
    static readonly (int X, int Y, float Rot, float Z, string Model) LockerPlacement =
        (345, 257, 0.508459f, -246f, "Metadata/Terrain/Leagues/Breach/Doodads/BreachLocker.ao");
    static readonly (int X, int Y)[] BackDoorBlockerCells =
        [(384, 279), (388, 279), (392, 279), (396, 279), (400, 279), (404, 279)];

    static readonly byte[] QuestScanRows =
    [
        PlayerInventories.MainInventory,
        PlayerInventories.ExpandedInventory,
        PlayerInventories.Cursor,
    ];

    sealed class HubAreaState
    {
        public uint TreeId;
        public (int X, int Y) TreeAt;
        public int TreeReach;
        public uint LockerId;
        public (int X, int Y) LockerAt;
        public int LockerReach;
        public readonly List<uint> BackDoorBlockerIds = [];
    }

    sealed class HubState
    {
        public uint TreeId;
        public (int X, int Y) TreeAt;
        public int TreeReach;
        public uint LockerId;
        public (int X, int Y) LockerAt;
        public int LockerReach;
        public bool LockerOpen;
        public bool IsHideoutLocker;
        public readonly List<uint> BackDoorBlockerIds = [];
    }

    sealed class HideoutState { public uint LockerId; }

    static readonly ConditionalWeakTable<GameArea, HubAreaState> HubAreas = new();
    static readonly ConditionalWeakTable<Player, HubState> States = new();
    static readonly ConditionalWeakTable<GameArea, HideoutState> HideoutLockers = new();

    static StateMachineData? TreeStates, LockerStates, HideoutLockerStates;

    /// <summary>Loads StateMachine metadata for <c>TreeTree</c>, <c>GraftLocker</c>, and <c>GraftLocker_Hideout</c>.</summary>
    public static void LoadGameData()
    {
        TreeStates = LoadStateMachine(TreeTreeType);
        LockerStates = LoadStateMachine(BreachLocker.GraftLockerType);
        HideoutLockerStates = LoadStateMachine(BreachLocker.HideoutLockerType);
    }

    /// <summary>Clears cached weak-table state on hot-reload.</summary>
    public static void Clear()
    {
        HubAreas.Clear();
        States.Clear();
        HideoutLockers.Clear();
    }

    static StateMachineData? LoadStateMachine(string type) =>
        ObjectTypes.All().Where(t => t.Type == type)
            .Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates()))
            .FirstOrDefault();

    /// <summary><c>GameEvents.NpcUsesLeagueFlags</c>: opts Chayula/Ailith NPCs into automatic league-flag
    /// <c>NPCTalk</c> filtering and <c>LeagueFlagsSet</c> application.</summary>
    public static bool UsesLeagueFlags(Player player, NpcCatalog.Npc npc) =>
        npc.Row is (>= NpcAilithWildMonster and <= NpcAilithWalk) or NpcDreamer or NpcWordOfDreamer;

    /// <summary>Spawns <c>GraftLocker_Hideout</c> on <c>GameEvents.HideoutReady</c> in a reserved small hideout slot.</summary>
    public static async Task OnHideoutReadyAsync(IModuleContext module, Player player)
    {
        // Ensure locker is targetable in hideout (client Targetable requires LQF 533 ChayulaGraftLockerUnlocked).
        if (!player.Character.LeagueQuestFlags.Contains(LqfGraftLockerUnlocked))
            await SetLeagueQuestFlags(player, [LqfGraftLockerUnlocked]);

        if (await WorldOf(PlayerOf(player)).SpawnHideoutStation(
                HideoutSlot.Small, BreachLocker.HideoutLockerType, "breach-locker", "Wombgift Locker") is { } lockerId)
        {
            HideoutLockers.GetOrCreateValue(player.Area).LockerId = lockerId;
            module.Log($"{PlayerOf(player).Character}: spawned hideout Wombgift Locker 0x{lockerId:x}");
        }
    }

    static uint? HideoutLockerOf(Player player)
    {
        if (HideoutLockers.TryGetValue(player.Area, out var state) && state.LockerId != 0)
            return state.LockerId;
        foreach (var (id, entry) in player.HideoutEditor.All())
        {
            if (entry.Key == "breach-locker")
            {
                HideoutLockers.GetOrCreateValue(player.Area).LockerId = id;
                return id;
            }
        }
        return null;
    }

    static readonly string[] HubAreaFlag = [QfInChayulaLeagueArea];
    static readonly string[] EmptyAreaFlags = [];

    /// <summary><c>GameEvents.AreaQuestFlags</c>: grants <c>IsChayulaLeagueArea</c> transiently while in the Monastery
    /// without mutating saved character quest flags (zero allocation on combat hot paths).</summary>
    public static IReadOnlyList<string> AreaQuestFlags(Player player) =>
        player.Layout.Id == HubAreaId ? HubAreaFlag : EmptyAreaFlags;

    /// <summary>Called on <c>GameEvents.PlayerEntered</c>: syncs Hiveblood, body-part quest flags, Genesis Tree
    /// state, and spawns the Monastery's interactive objects when in <c>ChayulaLeague</c>.</summary>
    public static async Task OnPlayerEnteredAsync(IModuleContext module, Player player)
    {
        await SyncBodyPartQuestFlagsAsync(player);
        await BreachHiveblood.SyncFruitQuestFlagsAsync(player);
        await BreachHiveblood.SyncAsync(player, "area entered");

        if (PlayerOf(player).AreaId != HubAreaId) return;

        // Unlock the Wombgift Locker and Monastery quest tracker flags for characters arriving at the Monastery.
        if (!player.Character.HasQuestFlag(QfBarrierBurned))
            await SetQuestFlags(player, [QfBarrierBurned]);
        var ensureLeagueFlags = new List<int>();
        if (!player.Character.LeagueQuestFlags.Contains(LqfGraftLockerUnlocked))
            ensureLeagueFlags.Add(LqfGraftLockerUnlocked);
        if (!player.Character.LeagueQuestFlags.Contains(LqfLeagueAreaUnlocked))
            ensureLeagueFlags.Add(LqfLeagueAreaUnlocked);
        if (ensureLeagueFlags.Count > 0)
            await SetLeagueQuestFlags(player, ensureLeagueFlags);

        var areaState = HubAreas.GetOrCreateValue(player.Area);
        bool backDoorOpen = player.Character.LeagueQuestFlags.Contains(LqfTurnInBodyPart9);

        // Spawn Monastery world objects once per GameArea instance so re-entering the same area does not duplicate them.
        if (areaState.TreeId == 0 && areaState.LockerId == 0)
        {
            var roomObjects = player.Layout.Level?.RoomObjects ?? [];

            // 1. Genesis Tree (TreeTree) at (402, 221).
            var treeObj = roomObjects.FirstOrDefault(o => o.Type == TreeTreeType);
            var (tx, ty, trot, tz, tmodel) = treeObj is not null
                ? (treeObj.X, treeObj.Y, treeObj.Rotation, treeObj.Z, treeObj.Model)
                : TreePlacement;
            if (await SpawnObjectAsync(module, player, TreeTreeType, tx, ty, trot, tz, tmodel, TreeStates, "door_open", backDoorOpen ? 1 : 0) is { } tree)
                (areaState.TreeId, areaState.TreeAt, areaState.TreeReach) = (tree.Id, (tx, ty), tree.Reach);

            // 2. Wombgift Locker (GraftLocker) at (345, 257).
            var lockerObj = roomObjects.FirstOrDefault(o => o.Type == BreachLocker.GraftLockerType);
            var (lx, ly, lrot, lz, lmodel) = lockerObj is not null
                ? (lockerObj.X, lockerObj.Y, lockerObj.Rotation, lockerObj.Z, lockerObj.Model)
                : LockerPlacement;
            if (await SpawnObjectAsync(module, player, BreachLocker.GraftLockerType, lx, ly, lrot, lz, lmodel, LockerStates, "open", 0) is { } locker)
                (areaState.LockerId, areaState.LockerAt, areaState.LockerReach) = (locker.Id, (lx, ly), locker.Reach);

            // 3. HubBackDoorBlocker (6 cells along y=279): client instantiates no_serialise=true from hub.arm and unblocks
            //    with TurnInChayulaBodyPart9 (LQF 1609); mirror walk blockers on the server until LQF 1609 is set.
            if (!backDoorOpen)
            {
                var blockerCells = roomObjects.Where(o => o.Type == HubBackDoorBlockerType).Select(o => (o.X, o.Y)).ToArray();
                if (blockerCells.Length == 0) blockerCells = BackDoorBlockerCells;
                foreach (var (bx, by) in blockerCells)
                {
                    uint bid = player.TakeObjectId();
                    player.SetObjectBlocker(bid, bx, by, 5, false);
                    areaState.BackDoorBlockerIds.Add(bid);
                }
            }
        }

        var state = States.GetOrCreateValue(player);
        state.TreeId = areaState.TreeId;
        state.TreeAt = areaState.TreeAt;
        state.TreeReach = areaState.TreeReach;
        state.LockerId = areaState.LockerId;
        state.LockerAt = areaState.LockerAt;
        state.LockerReach = areaState.LockerReach;
        state.LockerOpen = false;
        state.IsHideoutLocker = false;
        state.BackDoorBlockerIds.Clear();
        state.BackDoorBlockerIds.AddRange(areaState.BackDoorBlockerIds);

        await BreachGenesisTree.SyncStateAsync(player, "entered Monastery");
    }

    static async Task<(uint Id, int Reach)?> SpawnObjectAsync(
        IModuleContext module, Player player, string type, int x, int y, float rotation, float? z, string? model,
        StateMachineData? states, string stateName, int stateValue)
    {
        var modPlayer = PlayerOf(player);
        if (QuestObjectTypes.Find(type) is not { } info || states is null || !QuestObject.CanSerialize(info.Components, true))
        {
            module.Log($"{modPlayer.Character}: {type} cannot be serialized ({string.Join(", ", QuestObjectTypes.Find(type)?.Components ?? [])})");
            return null;
        }
        uint id = player.TakeObjectId();
        string? overrideModel = string.IsNullOrEmpty(model) || model == info.AnimatedModel ? null : model;
        var blob = new QuestObject(x, y, info.Components, rotation, z, overrideModel)
        {
            StateMachine = states.Set(stateName, stateValue),
        }.Blob();
        if (!await SendWorldCreate(modPlayer, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), blob),
                $"0x02bb {type.Split('/')[^1]} 0x{id:x} at ({x}, {y}), {stateName}={stateValue}"))
            return null;
        await SendWorldRecord(modPlayer, id, InstanceProtocol.EnterWakeZone(id));
        if (info.Blocking) player.SetObjectBlocker(id, x, y, Math.Max(1, info.Size), info.ActorOrChest);
        return (id, info.Size + 4);
    }

    static async Task SetStateAsync(Player player, uint id, string type, StateMachineData? states, string name, int value)
    {
        if (id == 0 || states is null || QuestObjectTypes.Find(type) is not { } info) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        await SendWorldRecord(PlayerOf(player), id, InstanceEntities.StateMachineUpdate(id, (byte)index, states.Set(name, value)),
            $"0x02bc {type.Split('/')[^1]} 0x{id:x} {name}={value}");
    }

    /// <summary>Handles clicks on the Monastery's <c>TreeTree</c> &amp; <c>GraftLocker</c> and the Hideout's
    /// <c>GraftLocker_Hideout</c>.</summary>
    public static async Task<bool> OnObjectUseAsync(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double at)
    {
        var modPlayer = PlayerOf(player);
        var world = WorldOf(player);

        // 1. Hideout Wombgift Locker.
        if (HideoutLockerOf(player) is uint hideoutLocker && use.TargetId == hideoutLocker)
        {
            if (!await world.WalkUpToStation(hideoutLocker, at)) return true;
            var hs = States.GetOrCreateValue(player);
            hs.LockerId = hideoutLocker;
            hs.LockerAt = world.StationAt(hideoutLocker) ?? player.Walk.Position(AreaTime(player));
            hs.LockerReach = 10;
            hs.LockerOpen = true;
            hs.IsHideoutLocker = true;
            player.ModuleWindow = BreachLocker.LockerWindow;
            await SetStateAsync(player, hideoutLocker, BreachLocker.HideoutLockerType, HideoutLockerStates, "open", 1);
            await OpenLockerUiAsync(module, player, hideoutLocker);
            return true;
        }

        if (modPlayer.AreaId != HubAreaId || !States.TryGetValue(player, out var state)) return false;

        // 2. Monastery Wombgift Locker.
        if (state.LockerId != 0 && use.TargetId == state.LockerId)
        {
            if (!await world.WalkUpTo(state.LockerAt.X, state.LockerAt.Y, state.LockerReach, at)) return true;
            state.LockerOpen = true;
            state.IsHideoutLocker = false;
            player.ModuleWindow = BreachLocker.LockerWindow;
            await SetStateAsync(player, state.LockerId, BreachLocker.GraftLockerType, LockerStates, "open", 1);
            await OpenLockerUiAsync(module, player, state.LockerId);
            return true;
        }

        // 3. Monastery Genesis Tree.
        if (state.TreeId != 0 && use.TargetId == state.TreeId)
        {
            if (!await world.WalkUpTo(state.TreeAt.X, state.TreeAt.Y, state.TreeReach, at)) return true;
            if (!player.Character.LeagueQuestFlags.Contains(LqfMonasteryFirstTreeOpen))
                await SetLeagueQuestFlags(player, [LqfMonasteryFirstTreeOpen]);
            await BreachHiveblood.SyncAsync(player, "open Genesis Tree");
            await BreachGenesisTree.SyncStateAsync(player, "open Genesis Tree");
            player.ModuleWindow = TreeWindow;
            await modPlayer.Send(InstanceWaypoints.OpenUi(TreeUiKind),
                $"0x007b open Genesis Tree (kind 0x{TreeUiKind:x2}) at TreeTree 0x{state.TreeId:x}");
            return true;
        }

        return false;
    }

    /// <summary>Where the Monastery's Genesis Tree stands for spawning grown Wombgift rewards, or the player's current
    /// position outside the Monastery.</summary>
    public static (int X, int Y) TreeDropOrigin(Player player) =>
        States.TryGetValue(player, out var state) && state.TreeId != 0
            ? state.TreeAt
            : player.Walk.Position(AreaTime(player));

    static Task OpenLockerUiAsync(IModuleContext module, Player player, uint lockerId) =>
        PlayerOf(player).Send(InstanceWaypoints.OpenUi(BreachLocker.LockerUiKind, lockerId),
            $"0x007b open Wombgift Locker (kind 0x{BreachLocker.LockerUiKind:x2}, inventory {BreachLocker.LockerRow}) at 0x{lockerId:x}");

    /// <summary>Closes the Wombgift Locker lid when the player walks away or opens another window.</summary>
    public static async Task TickAsync(Player player)
    {
        if (!States.TryGetValue(player, out var state) || !state.LockerOpen) return;
        var here = player.Walk.Position(AreaTime(player));
        long dx = here.X - state.LockerAt.X, dy = here.Y - state.LockerAt.Y, far = state.LockerReach + 30;
        if (player.ModuleWindow == BreachLocker.LockerWindow && dx * dx + dy * dy <= far * far) return;

        state.LockerOpen = false;
        if (player.ModuleWindow == BreachLocker.LockerWindow) player.ModuleWindow = null;
        string type = state.IsHideoutLocker ? BreachLocker.HideoutLockerType : BreachLocker.GraftLockerType;
        var sm = state.IsHideoutLocker ? HideoutLockerStates : LockerStates;
        await SetStateAsync(player, state.LockerId, type, sm, "open", 0);
    }

    /// <summary><c>GameEvents.NpcOptionPicked</c> for Ailith (Hub #774, Hideout #775, Wild #771..773) and The Dreamer
    /// (#779). Returns <see langword="false"/> when this handler consumed the dialogue transition (e.g. area transfer),
    /// <see langword="true"/> to let default NPC talk continue.</summary>
    public static async Task<bool> OnNpcOptionPickedAsync(IModuleContext module, Player player, NpcCatalog.Talk talk)
    {
        if (!UsesLeagueFlags(player, new(talk.Npc, "", "", null, null, 1))) return true;
        var modPlayer = PlayerOf(player);

        // 1. Ailith Hideout: "Visit the Monastery" (NPCTalk 3822, GoToBrequelHub()).
        if (talk.Script.Contains("GoToBrequelHub", StringComparison.Ordinal))
        {
            WorldOf(player).NpcEndTalk();
            await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 end Ailith chat (NPCTalk {talk.Row} GoToBrequelHub)");
            await WorldOf(player).TravelTo(HubAreaId, "Ailith Visit the Monastery");
            return false;
        }

        // 2. Ailith Hub: "Fertilising the Genesis Tree" (NPCTalk 3793..3799 -> ChayulaTreeJuicedTurnedIn1..7 = 1587..1593).
        if (talk.Npc == NpcAilithHub && talk.Row is >= 3793 and <= 3799)
        {
            int milestoneIndex = talk.Row - 3793; // 0..6
            int turnedInLqf = LqfTreeJuicedTurnedInFirst + milestoneIndex;
            if (!player.Character.LeagueQuestFlags.Contains(turnedInLqf))
            {
                await SetLeagueQuestFlags(player, [turnedInLqf]);
                await BreachGenesisTree.GrantMilestonePointsAsync(module, player, $"Fertilising milestone {milestoneIndex + 1}");
            }
            return true;
        }

        // 3. Ailith Hub/Hideout: "Gifts for the Genesis Tree" (BodyPart1..10 turn-ins: Hub 3800..3809, Hideout 3823..3832).
        int? bodyPartIndex = talk.Row switch
        {
            >= 3800 and <= 3809 => talk.Row - 3800,
            >= 3823 and <= 3832 => talk.Row - 3823,
            _ => null,
        };
        if (bodyPartIndex is int bpIdx)
        {
            await TurnInBodyPartAsync(module, player, bpIdx);
            return true;
        }

        // 4. Ailith Hub: "The Genesis Tree" (NPCTalk 3810 -> ChayulaNPCTutorialCompleted = 1586).
        if (talk.Row == 3810 && !player.Character.LeagueQuestFlags.Contains(LqfTutorialCompleted))
        {
            await SetLeagueQuestFlags(player, [LqfTutorialCompleted, LqfGraftLockerUnlocked]);
            await BreachGenesisTree.SyncStateAsync(player, "completed Genesis Tree tutorial");
            return true;
        }

        // 5. Ailith Hub: "Introduction" (NPCTalk 3811 -> unlocks GraftLocker LQF 533).
        if (talk.Row == 3811 && !player.Character.LeagueQuestFlags.Contains(LqfGraftLockerUnlocked))
        {
            await SetLeagueQuestFlags(player, [LqfGraftLockerUnlocked]);
            return true;
        }

        // 6. The Dreamer (NPC #779) backroom lore -> marks ChayulaBackroomTreeSeen (LQF 638).
        if (talk.Npc == NpcDreamer && !player.Character.LeagueQuestFlags.Contains(LqfBackroomTreeSeen))
        {
            await SetLeagueQuestFlags(player, [LqfBackroomTreeSeen]);
            return true;
        }

        return true;
    }

    static async Task TurnInBodyPartAsync(IModuleContext module, Player player, int index)
    {
        if (index < 0 || index >= BodyPartItemIds.Length) return;
        int turnInLqf = LqfTurnInBodyPartFirst + index;
        if (player.Character.LeagueQuestFlags.Contains(turnInLqf)) return;

        // Consume BodyPart{n} from player's inventory if carried.
        var items = ItemsOf(player);
        uint hash = BodyPartHashes[index];
        byte? removedRow = null;
        uint removedId = 0;
        Saves.Atomically(() =>
        {
            foreach (byte row in QuestScanRows)
            {
                if (items.Items(row).FirstOrDefault(i => i.Item.TypeHash == hash) is { } found
                    && items.Remove(row, found.Id) is not null)
                {
                    removedRow = row;
                    removedId = found.Id;
                    break;
                }
            }
        });
        if (removedRow is byte r && removedId != 0)
        {
            await PlayerOf(player).Send(
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(r), items.NextRevision(r), [removedId], []),
                $"0x00bf inventory {PlayerInventories.InventoryId(r)}: turned in BodyPart{index + 1}");
        }

        // Clear the per-character HaveChayulaBodyPart{n} flag.
        if (player.Character.HasQuestFlag(QfHaveBodyPart[index]))
            await SetQuestFlags(player, [QfHaveBodyPart[index]], on: false);

        // Set TurnInChayulaBodyPart{n} and ChayulaTotalBodyPartsTurnedIn{total}.
        var newLeagueFlags = new List<int> { turnInLqf };
        int turnedInCount = CountTurnedInBodyParts(player) + 1;
        for (int c = 1; c <= Math.Min(turnedInCount, BodyPartItemIds.Length); c++)
        {
            int totalFlag = LqfTotalBodyPartsFirst + c - 1;
            if (!player.Character.LeagueQuestFlags.Contains(totalFlag))
                newLeagueFlags.Add(totalFlag);
        }
        await SetLeagueQuestFlags(player, newLeagueFlags);

        // BodyPart9 (Breachlord Limbs) opens the hidden backroom door to The Dreamer!
        if (turnInLqf == LqfTurnInBodyPart9 && States.TryGetValue(player, out var state))
        {
            foreach (uint bid in state.BackDoorBlockerIds)
                player.RemoveBlocker(bid);
            state.BackDoorBlockerIds.Clear();
            if (state.TreeId != 0)
                await SetStateAsync(player, state.TreeId, TreeTreeType, TreeStates, "door_open", 1);
            module.Log($"{PlayerOf(player).Character}: BodyPart9 turned in — Monastery hidden backroom opened");
        }

        await BreachGenesisTree.GrantMilestonePointsAsync(module, player, $"BodyPart{index + 1} turned in");
    }

    /// <summary>How many of the 10 Hiveborn Body Parts this account/league has turned in to Ailith.</summary>
    public static int CountTurnedInBodyParts(Player player)
    {
        int count = 0;
        for (int i = 0; i < BodyPartItemIds.Length; i++)
            if (player.Character.LeagueQuestFlags.Contains(LqfTurnInBodyPartFirst + i)) count++;
        return count;
    }

    /// <summary>How many of the 7 <c>BrequelFruitQuests</c> Hiveblood milestones have been turned in to Ailith.</summary>
    public static int CountTurnedInFruitQuests(Player player)
    {
        int count = 0;
        for (int i = 0; i < 7; i++)
            if (player.Character.LeagueQuestFlags.Contains(LqfTreeJuicedTurnedInFirst + i)) count++;
        return count;
    }

    /// <summary>Synchronizes <c>HaveChayulaBodyPart1..10</c> character quest flags with items currently in inventory.</summary>
    public static async Task SyncBodyPartQuestFlagsAsync(Player player)
    {
        var items = ItemsOf(player);
        var heldHashes = new HashSet<uint>();
        foreach (byte row in QuestScanRows)
            foreach (var inv in items.Items(row))
                heldHashes.Add(inv.Item.TypeHash);

        List<string>? toSet = null;
        List<string>? toClear = null;
        for (int i = 0; i < BodyPartHashes.Length; i++)
        {
            bool carries = heldHashes.Contains(BodyPartHashes[i]);
            bool hasFlag = player.Character.HasQuestFlag(QfHaveBodyPart[i]);
            if (carries && !hasFlag) (toSet ??= []).Add(QfHaveBodyPart[i]);
            else if (!carries && hasFlag) (toClear ??= []).Add(QfHaveBodyPart[i]);
        }
        if (toSet is { Count: > 0 }) await SetQuestFlags(player, toSet, on: true);
        if (toClear is { Count: > 0 }) await SetQuestFlags(player, toClear, on: false);
    }

    /// <summary>Drop helper called by <c>@breach-world</c> encounters/bosses: if the player still needs any of
    /// <c>BodyPart1..10</c> (neither turned in nor already carried), creates the next eligible quest item; otherwise
    /// returns <see langword="null"/>.</summary>
    public static Item? TryRollBodyPart(Player player, int areaLevel, Random rng)
    {
        var catalog = ItemsFor(player);
        var items = ItemsOf(player);
        var carried = new HashSet<uint>();
        foreach (byte row in QuestScanRows)
            foreach (var inv in items.Items(row))
                carried.Add(inv.Item.TypeHash);

        for (int i = 0; i < BodyPartItemIds.Length; i++)
        {
            if (player.Character.LeagueQuestFlags.Contains(LqfTurnInBodyPartFirst + i)) continue;
            if (carried.Contains(BodyPartHashes[i])) continue;

            // Higher body parts gate gently on map progression (BodyPart8 = Vruun, BodyPart9 = Breachlords).
            int minLevel = i switch { 0 => 1, <= 3 => 20, <= 6 => 50, <= 8 => 68, _ => 75 };
            if (areaLevel < minLevel) continue;
            if (catalog.ById(BodyPartItemIds[i]) is not { Supported: true } baseType) continue;
            return ItemCatalog.Create(baseType, random: rng) with { ItemLevel = (byte)Math.Clamp(areaLevel, 1, 100) };
        }
        return null;
    }
}
