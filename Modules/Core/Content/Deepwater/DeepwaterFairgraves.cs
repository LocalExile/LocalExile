using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Act 1 Deepwater Fairgraves in The Ship Graveyard (<c>1_1_9</c>, league category 59): spawns
/// <c>SkeletonBossFairgravesDeepwater</c> (<c>MonsterVarieties#551</c>) at <c>Transitionable</c> state 4 (visible and
/// targetable; states 1–3 hide/disable targetable in <c>SkeletonBossEmergeDeepwater.ot</c>) and <c>FlameBarrier1</c>
/// (<c>Transitionable</c> state 1 blocked → state 2 open on kill), sets <c>LQF 1856</c> on approach, sets <c>QF 47</c>
/// (<c>a1q6-KilledFairgraves</c>) and drops <c>AllFlameLanternDeepwater</c> (<c>BaseItemTypes#1536</c>,
/// <c>QF 3583 HaveDeepwaterAllflame</c>) on defeat. Leaves vanilla <c>a1q6</c> (category 60) completely untouched.</summary>
public static class DeepwaterFairgraves
{
    public const string ShipGraveyard = "1_1_9";
    public const string FairgravesVariety = "Metadata/Monsters/Skeletons/SkeletonBossFairgravesDeepwater";
    public const string FlameBarrierType = "Metadata/Terrain/Leagues/Deepwater/Act1_ShipGraveyard/FlameBarrier1";
    public const string FlameBarrierModel = "Metadata/Effects/Environment/League_Deepwater/FairgravesBarrier/fairgravesBarrier.ao";
    public const string AllflameItemId = "Metadata/Items/QuestItems/AllFlameLanternDeepwater";

    public static uint AllflameItemHash => InstanceEntities.ObjectTypeHash(AllflameItemId);

    /// <summary>QuestFlags 47 (<c>a1q6-KilledFairgraves</c>): advances Q210 to Order 4 and grants Act 1 <c>a1q6</c> credit.</summary>
    public const string KilledFairgravesFlag = "a1q6-KilledFairgraves";

    /// <summary>QuestFlags 3583 (<c>HaveDeepwaterAllflame</c>): triggered while carrying <see cref="AllflameItemId"/>.</summary>
    public const string HaveAllflameFlag = "HaveDeepwaterAllflame";

    /// <summary>LeagueQuestFlags 1856 (<c>AllflameQuestFairgravesSeen</c>): set when approaching Fairgraves in <c>1_1_9</c>.</summary>
    public const int FairgravesSeenLeagueFlag = 1856;

    /// <summary>LeagueQuestFlags 1859 (<c>DeepwaterValVesperFlameGiven</c>): Q210 complete (Allflame handed to Vesper).</summary>
    public const int FlameGivenLeagueFlag = 1859;

    /// <summary>Transitionable state 4 on <c>SkeletonBossEmergeDeepwater.ot</c> (states 1–3 disable targetable / state 1 hides rendering).</summary>
    public const byte FairgravesActiveTransitionState = 4;

    /// <summary><c>FlameBarrier1</c> Transitionable states: 1 = closed/blocked (<c>PlayAnimationByIndex(1)</c>),
    /// 2 = open/unblocked (<c>PlayAnimationByIndex(0)</c>).</summary>
    public const byte BarrierClosedState = 1, BarrierOpenState = 2;

    // G: approach radius (cells) to set LQF 1856, and server walk-map blocker radius for fairgravesBarrier.ao while closed.
    public const int ApproachRange = 50;
    const int BarrierBlockSize = 4;

    /// <summary>Deserialized components of <c>flamebarrier1.ot</c> (in exact wire order; fallback because
    /// <c>FlameBarrier1</c> is not in <c>client.db</c>'s <c>object_types</c> table).</summary>
    public static readonly string[] FlameBarrierComponents =
        ["Positioned", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage"];

    /// <summary>Components for <see cref="FlameBarrierType"/>: from <see cref="QuestObjectTypes"/> when present, else
    /// <see cref="FlameBarrierComponents"/>.</summary>
    public static IReadOnlyList<string> BarrierComponents() =>
        QuestObjectTypes.Find(FlameBarrierType)?.Components ?? FlameBarrierComponents;

    sealed class FairgravesState
    {
        public readonly object Gate = new();
        public uint FairgravesId;
        public int BossX, BossY;
        public float BossRotation;
        public bool HasBossPoint;
        public uint BarrierId;
        public int BarrierX, BarrierY;
        public float BarrierRotation;
        public float? BarrierZ;
        public string? BarrierModel;
        public bool Open;
        public bool KilledInInstance;
        public bool CreatorAlreadyDone;
    }

    static readonly ConditionalWeakTable<InstanceState, FairgravesState> States = new();

    /// <summary>Builds the <see cref="QuestObject"/> for <see cref="FlameBarrierType"/> in closed (<c>open = false</c>)
    /// or open (<c>open = true</c>) state (also callable directly in tests).</summary>
    public static QuestObject BuildBarrierObject(int x, int y, float rotation, float? height, string? model, bool open) =>
        new(x, y, BarrierComponents(), rotation, height is float h && !float.IsNaN(h) ? h : null,
            string.IsNullOrEmpty(model) ? FlameBarrierModel : model,
            TransitionState: open ? BarrierOpenState : BarrierClosedState,
            Blocked: !open);

    /// <summary><c>GameEvents.AreaGenerated</c> handler for <c>1_1_9</c> in Allflame leagues: spawns <c>FlameBarrier1</c>
    /// and <c>SkeletonBossFairgravesDeepwater</c> (unless the entering character already has <c>QF 47</c>), or syncs the
    /// existing instance barrier for late-joining / re-entering players. No-op outside <c>1_1_9</c> or on vanilla
    /// category 60 topologies that lack Deepwater Fairgraves placements.</summary>
    public static async Task Enter(IModuleContext module, ClientState conn)
    {
        if (PlayerOf(conn).AreaId != ShipGraveyard || conn.Instance is not { } instance) return;

        await DeepwaterProgression.SyncQuestItemFlags(conn);

        FairgravesState? state;
        bool created = false;
        bool alreadyDone = false;

        lock (States)
        {
            if (States.TryGetValue(instance, out state))
            {
                created = false;
            }
            else
            {
                var lines = conn.Layout.Level?.RoomObjects ?? [];
                var bossAt = lines.FirstOrDefault(o => o.Type == FairgravesVariety);
                var barrierAt = lines.FirstOrDefault(o => o.Type == FlameBarrierType);
                // Only Deepwater category 59 topologies place these; vanilla a1q6 (category 60) exits here untouched.
                if (bossAt is null && barrierAt is null) return;

                // Character-scoped QF 47 only (never league-wide LQF 1859), so 2nd+ characters still get a1q6 & QF 47.
                alreadyDone = ClientHasQuestFlag(conn, KilledFairgravesFlag);
                state = new FairgravesState
                {
                    HasBossPoint = bossAt is not null,
                    BossX = bossAt?.X ?? 0,
                    BossY = bossAt?.Y ?? 0,
                    BossRotation = bossAt?.Rotation ?? 0f,
                    BarrierId = barrierAt is not null ? conn.TakeObjectId() : 0,
                    BarrierX = barrierAt?.X ?? 0,
                    BarrierY = barrierAt?.Y ?? 0,
                    BarrierRotation = barrierAt?.Rotation ?? 0f,
                    BarrierZ = barrierAt is not null && !float.IsNaN(barrierAt.Z) ? barrierAt.Z : null,
                    BarrierModel = barrierAt is not null && !string.IsNullOrEmpty(barrierAt.Model) ? barrierAt.Model : FlameBarrierModel,
                    Open = alreadyDone,
                    KilledInInstance = false,
                    CreatorAlreadyDone = alreadyDone,
                };
                States.Add(instance, state);
                created = true;
            }
        }

        bool spawnForJoiner = false;
        bool open, killedInInstance, creatorAlreadyDone;
        lock (state.Gate)
        {
            if (!created && state.CreatorAlreadyDone && !state.KilledInInstance && state.HasBossPoint
                && state.FairgravesId == 0 && !ClientHasQuestFlag(conn, KilledFairgravesFlag))
            {
                // Instance was created by a character who already had QF 47, and a party member lacking QF 47 just joined.
                state.CreatorAlreadyDone = false;
                state.Open = false;
                spawnForJoiner = true;
            }
            (open, killedInInstance, creatorAlreadyDone) = (state.Open, state.KilledInInstance, state.CreatorAlreadyDone);
        }

        if (state.BarrierId != 0)
            await CreateBarrier(conn, state, open);

        if (spawnForJoiner)
        {
            // Close the barrier for any players who were already inside with it open.
            await CloseBarrierForOthers(conn, state);
            await SpawnFairgraves(module, conn, state);
            return;
        }

        if (!created)
        {
            // Joining or re-entering an instance where Fairgraves already fell: recover Allflame if this player still needs it (G).
            if ((killedInInstance || (creatorAlreadyDone && ClientHasQuestFlag(conn, KilledFairgravesFlag)))
                && state.HasBossPoint && NeedsAllflameDrop(conn))
                await DropAllflame(module, conn, state.BossX, state.BossY, AreaLevel(conn));
            return;
        }

        if (alreadyDone)
        {
            // G: if player killed Fairgraves previously (QF 47) but left without looting Allflame (#1536) and has not
            // turned it in (LQF 1859), drop a recovery copy so Q210 Order 4 cannot soft-lock.
            if (state.HasBossPoint && NeedsAllflameDrop(conn))
                await DropAllflame(module, conn, state.BossX, state.BossY, AreaLevel(conn));
            module.Log($"{CharacterName(conn)}: 1_1_9 Deepwater Fairgraves already defeated; FlameBarrier1 spawned open");
            return;
        }

        if (!state.HasBossPoint) return;
        await SpawnFairgraves(module, conn, state);
    }

    static async Task SpawnFairgraves(IModuleContext module, ClientState conn, FairgravesState state)
    {
        if (Monsters.Find(FairgravesVariety) is not { Supported: true } variety)
        {
            module.Log($"{CharacterName(conn)}: {FairgravesVariety} is not supported; opening FlameBarrier1");
            await OpenBarrierForAll(conn, state);
            return;
        }

        byte level = AreaLevel(conn);
        var (remote, stream, ciphers) = conn.Wire;
        // SkeletonBossEmergeDeepwater has TransitionStates = 4; Emerges.Kind returns None for Unique with >1 states,
        // so SpawnMonster creates Fairgraves directly at TransitionState = 4 (visible & targetable).
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique,
            state.BossX, state.BossY, unrisen: false, rotation: state.BossRotation, instanceOwned: true);

        if (id == 0)
        {
            module.Log($"{CharacterName(conn)}: failed to spawn {FairgravesVariety}; opening FlameBarrier1");
            await OpenBarrierForAll(conn, state);
            return;
        }

        lock (state.Gate) state.FairgravesId = id;
        module.Log($"{CharacterName(conn)}: spawned Deepwater Captain Fairgraves 0x{id:x} ({variety.Id}, TransitionStates={variety.TransitionStates}) at ({state.BossX}, {state.BossY}) level {level}");
    }

    static async Task CloseBarrierForOthers(ClientState conn, FairgravesState state)
    {
        if (state.BarrierId == 0) return;
        int transIndex = QuestObject.TransitionableIndex(BarrierComponents());
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            if (ReferenceEquals(member, conn)) continue;
            member.SetBlocker(state.BarrierId, state.BarrierX, state.BarrierY, BarrierBlockSize);
            if (transIndex >= 0)
                await global::Game.SendWorldRecord(PlayerOf(member), state.BarrierId,
                    InstanceEntities.TransitionableUpdate(state.BarrierId, (byte)transIndex, BarrierClosedState),
                    $"0x02bc FlameBarrier1 0x{state.BarrierId:x} closes (state {BarrierClosedState})");
            await global::Game.SendWorldRecord(PlayerOf(member), state.BarrierId,
                InstanceEntities.BlockageUpdate(state.BarrierId, true),
                $"0x01c6 FlameBarrier1 0x{state.BarrierId:x} blocked");
        }
    }

    /// <summary><c>GameEvents.Tick</c> handler: sets <c>LQF 1856</c> (<c>AllflameQuestFairgravesSeen</c>) when the
    /// player approaches within <see cref="ApproachRange"/> cells of Deepwater Fairgraves in <c>1_1_9</c>. Zero
    /// allocations outside <c>1_1_9</c> or once seen/defeated.</summary>
    public static async Task Tick(IModuleContext module, ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea || conn.Instance is not { } instance || !States.TryGetValue(instance, out var state)) return;

        uint bossId;
        int bx, by;
        lock (state.Gate)
        {
            if (state.KilledInInstance || state.FairgravesId == 0) return;
            bossId = state.FairgravesId;
            bx = state.BossX;
            by = state.BossY;
        }

        if (HasLeagueFlag(conn, FairgravesSeenLeagueFlag)) return;

        double now = conn.Clock.Elapsed.TotalSeconds;
        var playerAt = conn.Walk.Position(now);
        lock (conn.Monsters)
        {
            if (conn.Monsters.TryGetValue(bossId, out var boss) && !boss.Dead)
                (bx, by) = boss.Walk.Position(now);
        }

        long dx = playerAt.X - bx, dy = playerAt.Y - by;
        if (dx * dx + dy * dy > (long)ApproachRange * ApproachRange) return;

        var (remote, stream, ciphers) = conn.Wire;
        await SetLeagueQuestFlags(remote, stream, ciphers, conn, [FairgravesSeenLeagueFlag]);
        module.Log($"{CharacterName(conn)}: approached Deepwater Fairgraves (LQF {FairgravesSeenLeagueFlag} set)");
    }

    /// <summary><c>GameEvents.MonsterKilled</c> handler: on Deepwater Fairgraves's death, sets <c>QF 47</c>
    /// (<c>a1q6-KilledFairgraves</c>) and <c>LQF 1856</c>, transitions <c>FlameBarrier1</c> from state 1 → 2 (unblocking
    /// both client <c>TriggerableBlockage</c> and server walk map), and drops <see cref="AllflameItemId"/>
    /// (<c>#1536</c>) for each member who lacks <c>LQF 1859</c> and does not carry it. Zero allocations for every
    /// non-Unique kill or kills outside <c>1_1_9</c>.</summary>
    public static async Task OnMonsterKilled(IModuleContext module, MonsterKill kill)
    {
        if (kill.Monster.Rarity != MonsterRarity.Unique) return;
        if (kill.Conn.Instance is not { } instance || !States.TryGetValue(instance, out var state)) return;

        lock (state.Gate)
        {
            if (state.KilledInInstance) return;
            bool match = (state.FairgravesId != 0 && kill.Monster.ObjectId == state.FairgravesId)
                || kill.Monster.VarietyId.Equals(FairgravesVariety, StringComparison.Ordinal);
            if (!match) return;
            state.KilledInInstance = true;
            state.Open = true;
        }

        int transIndex = QuestObject.TransitionableIndex(BarrierComponents());
        byte level = (byte)Math.Clamp(kill.Monster.Level > 0 ? kill.Monster.Level : AreaLevel(kill.Conn), 1, 100);

        foreach (var member in DeepwaterLoot.Members(kill.Conn))
        {
            var (remote, stream, ciphers) = member.Wire;
            if (!HasLeagueFlag(member, FairgravesSeenLeagueFlag))
                await SetLeagueQuestFlags(remote, stream, ciphers, member, [FairgravesSeenLeagueFlag]);
            if (!member.HasQuestFlag(KilledFairgravesFlag))
                await SetQuestFlags(remote, stream, ciphers, member, [KilledFairgravesFlag]);

            if (state.BarrierId != 0)
            {
                member.RemoveBlocker(state.BarrierId);
                if (transIndex >= 0)
                    await global::Game.SendWorldRecord(PlayerOf(member), state.BarrierId,
                        InstanceEntities.TransitionableUpdate(state.BarrierId, (byte)transIndex, BarrierOpenState),
                        $"0x02bc FlameBarrier1 0x{state.BarrierId:x} opens (state {BarrierOpenState})");
                await global::Game.SendWorldRecord(PlayerOf(member), state.BarrierId,
                    InstanceEntities.BlockageUpdate(state.BarrierId, false),
                    $"0x01c6 FlameBarrier1 0x{state.BarrierId:x} unblocked");
            }

            if (NeedsAllflameDrop(member))
                await DropAllflame(module, member, kill.Monster.X, kill.Monster.Y, level);
        }

        module.Log($"{CharacterName(kill.Conn)}: defeated Deepwater Captain Fairgraves; {KilledFairgravesFlag} set, FlameBarrier1 opened");
    }

    /// <summary>Whether the player currently carries <see cref="AllflameItemId"/> in Main, Expanded, Cursor, or Collection.</summary>
    public static bool CarriesAllflame(ClientState conn) =>
        DeepwaterProgression.CarriesQuestItem(conn, AllflameItemHash);

    static bool NeedsAllflameDrop(ClientState conn) =>
        !HasLeagueFlag(conn, FlameGivenLeagueFlag)
        && !CarriesAllflame(conn)
        && !OnGroundFor(conn, AllflameItemHash);

    static bool OnGroundFor(ClientState conn, uint typeHash)
    {
        if (conn.Instance is not { } instance) return false;
        var ground = instance.Ground;
        foreach (var (id, item) in ground.All())
        {
            if (item.Item.TypeHash != typeHash) continue;
            var (by, shared) = ground.Origin(id);
            if (shared || ReferenceEquals(by, conn)) return true;
            if (by is ClientState other
                && string.Equals(CharacterName(other), CharacterName(conn), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static async Task CreateBarrier(ClientState conn, FairgravesState state, bool open)
    {
        var obj = BuildBarrierObject(state.BarrierX, state.BarrierY, state.BarrierRotation, state.BarrierZ, state.BarrierModel, open);
        var player = PlayerOf(conn);
        if (!await global::Game.SendWorldCreate(player, state.BarrierId,
            InstanceEntities.CreateObject(state.BarrierId, InstanceEntities.ObjectTypeHash(FlameBarrierType), obj.Blob()),
            $"0x02bb FlameBarrier1 0x{state.BarrierId:x} at ({state.BarrierX}, {state.BarrierY}) ({(open ? "open" : "blocked")})"))
            return;
        await global::Game.SendWorldRecord(player, state.BarrierId, InstanceProtocol.EnterWakeZone(state.BarrierId));
        if (!open) conn.SetBlocker(state.BarrierId, state.BarrierX, state.BarrierY, BarrierBlockSize);
    }

    static async Task OpenBarrierForAll(ClientState conn, FairgravesState state)
    {
        lock (state.Gate) state.Open = true;
        if (state.BarrierId == 0) return;
        int transIndex = QuestObject.TransitionableIndex(BarrierComponents());
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            member.RemoveBlocker(state.BarrierId);
            if (transIndex >= 0)
                await global::Game.SendWorldRecord(PlayerOf(member), state.BarrierId,
                    InstanceEntities.TransitionableUpdate(state.BarrierId, (byte)transIndex, BarrierOpenState),
                    $"0x02bc FlameBarrier1 0x{state.BarrierId:x} opens (state {BarrierOpenState})");
            await global::Game.SendWorldRecord(PlayerOf(member), state.BarrierId,
                InstanceEntities.BlockageUpdate(state.BarrierId, false),
                $"0x01c6 FlameBarrier1 0x{state.BarrierId:x} unblocked");
        }
    }

    static async Task DropAllflame(IModuleContext module, ClientState conn, int x, int y, byte level)
    {
        if (!GameEvents.AllowsLoot(conn)) return;
        if (ItemsFor(conn).Find(AllflameItemId) is not { Supported: true } type)
        {
            module.Log($"{CharacterName(conn)}: {AllflameItemId} is not supported; Fairgraves cannot drop Allflame");
            return;
        }
        var at = conn.Layout.Near((x, y));
        var ground = new WorldItemObject(at.X, at.Y, ItemCatalog.Create(type) with { ItemLevel = level });
        // Quest items stay private to each eligible player (SharedGround.cs).
        uint id = conn.Instance.Ground.Add(ground, conn, shared: false);
        var (remote, stream, ciphers) = conn.Wire;
        await SendGroundItem(remote, stream, ciphers, conn, id, ground,
            $"Deepwater Fairgraves drops {type.Name}", justDropped: true);
    }

    static bool HasLeagueFlag(ClientState conn, int flag)
    {
        lock (conn.Monsters) return conn.LeagueQuestFlags.Contains(flag);
    }

    static byte AreaLevel(ClientState conn) =>
        (byte)Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
}
