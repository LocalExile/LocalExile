using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using static Game;

/// <summary>
/// Ordinary clickable and breakable campaign dungeon doors placed by tiles (.tmd) and rooms (.arm):
/// <c>Metadata/MiscellaneousObjects/Door</c>, <c>Door_grate_temp</c>, <c>IncaDoorLight</c>, <c>ScepterDoorLight</c>,
/// <c>Smashable</c>, <c>SmashableNoHighlight</c>, and Act 8's StateMachine <c>GoldenDoor</c>. Every type is verified
/// against <c>client.db</c> <c>object_types</c> and <see cref="QuestObject.CanSerialize"/> at startup; boss-controlled
/// non-interactive doors (e.g. <c>BanditDoor</c>) and unsupported types are skipped.
/// State is owned per <see cref="InstanceState"/> so re-entries and party members see consistent doors.
/// </summary>
public static class CampaignDoors
{
    const string SmashableType = "Metadata/MiscellaneousObjects/Smashable";
    const string SmashableNoHighlightType = "Metadata/MiscellaneousObjects/SmashableNoHighlight";
    const string GoldenDoorType = "Metadata/Terrain/Act8/Area3/Objects/GoldenDoor";
    const int SmashReach = 18;

    static readonly string[] ProminadeGardenFlags = ["HiddenSolarisButton", "HiddenLunarisButton"];

    sealed record DoorDef(string Type, IReadOnlyList<string> Components, int States, int Size, int TransitionableIndex,
        int StateMachineIndex, StateMachineData? StateMachine, bool OwnModel, bool IsSmashable, bool HasTriggerableBlockage);
    sealed record Door(uint Id, string Type, string? Model, int X, int Y, float Rotation, float? Z);

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Door>? Doors;
        public readonly Dictionary<uint, bool> Shown = [];
    }

    static Dictionary<string, DoorDef> Supported = new(StringComparer.OrdinalIgnoreCase);
    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<Player, HashSet<uint>> Presented = new();

    static bool IsSmashableType(string type) =>
        type.Equals(SmashableType, StringComparison.OrdinalIgnoreCase)
        || type.Equals(SmashableNoHighlightType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the verified set of interactive ordinary door types from client.db object_types.</summary>
    public static void LoadGameData()
    {
        var map = new Dictionary<string, DoorDef>(StringComparer.OrdinalIgnoreCase);
        var sharedStates = ObjectTypes.SharedStates();
        foreach (var (type, row) in ObjectTypes.All())
        {
            if (!ServerLevels.IsOrdinaryDoorType(type)) continue;
            var components = ObjectTypes.Components(row);
            bool smashable = IsSmashableType(type);
            // Non-interactive, non-smashable gates (such as BanditDoor, owned by EarlyActBossPolish) must not be
            // spawned closed by CampaignDoors or players would be locked out without a way to open them.
            if (!components.Contains("InteractionAction") && !smashable) continue;
            int transIdx = QuestObject.TransitionableIndex(components);
            int smIdx = components.ToList().IndexOf("StateMachine");
            StateMachineData? sm = smIdx >= 0 ? ObjectTypes.StateMachine(row, sharedStates) : null;
            if (smIdx >= 0)
            {
                if (sm is null || !QuestObject.CanSerialize(components, haveStateMachine: true)) continue;
                if (sm.Names is null || !sm.Names.Contains("open", StringComparer.Ordinal)) continue;
            }
            else
            {
                if (!QuestObject.CanSerialize(components, haveStateMachine: false) || transIdx < 0) continue;
            }
            int states = Math.Max(2, row.IntOrNull("transitionable_num_states") ?? 2);
            int size = row.TextOrNull("positioned_object_size") is { } os && int.TryParse(os, out int parsed)
                ? Math.Max(1, parsed - 1) : 3;
            bool ownModel = !components.Contains("Animated") || row.TextOrNull("animated_object") is not null;
            bool hasBlockage = components.Contains("TriggerableBlockage");
            map[type] = new DoorDef(type, components, states, size, transIdx, smIdx, sm, ownModel, smashable, hasBlockage);
        }
        Supported = map;
    }

    static bool Enabled() => SystemSettings.Default.CampaignDoorsEnabled;

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance).Distinct();

    public static Task OnAreaGenerated(Player player) => EnsureAndPresent(player);
    public static Task OnPlayerEntered(Player player) => EnsureAndPresent(player);

    public static void Leave(Player player) => Presented.Remove(player);

    static async Task EnsureAndPresent(Player player)
    {
        if (!Enabled() || player.Instance is null || InTown(player)
            || HideoutCatalog.ByArea(player.Layout.Hash) is not null
            || player.Layout.Id == TutorialObjects.Strand
            || MapDevices.IsMapArea(player.Layout.Hash)
            || player.Layout.Level is not { } level
            || LabyrinthTrials.InLabyrinth(player.Layout.Id))
            return;
        var area = Areas.GetOrCreateValue(player.Instance);
        await area.Gate.WaitAsync();
        try
        {
            area.Doors ??= Plan(player, level);
            foreach (var door in area.Doors)
                await Present(player, area, door);
        }
        finally { area.Gate.Release(); }
    }

    static List<Door> Plan(Player player, ServerLevels.Level level)
    {
        var result = new List<Door>();
        if (Supported.Count == 0) return result;
        string areaId = player.Layout.Id;
        var quests = QuestsOf(player);
        var questDoorTypes = quests.Doors(areaId).Select(r => r.Type)
            .Concat(quests.Objects(areaId).Select(r => r.ObjectType))
            .Concat(quests.Blockers(areaId).Select(r => r.ObjectType))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exclude Labyrinth trial rooms completely (including Crypt BarDoor inside Trial of Ascendancy rooms).
        bool hasTrial = LabyrinthTrials.For(areaId) is not null;
        var trialBounds = new List<(int X0, int Y0, int X1, int Y1)>();
        var trialPoints = new List<(int X, int Y)>();
        if (hasTrial)
        {
            foreach (var rec in level.Records)
                foreach (var r in rec.Rooms)
                {
                    if (!r.Room.Doodads.Any(d => d.Type is LabyrinthTrials.Plaque or LabyrinthTrials.IntroDoor
                        || d.Type.StartsWith("Metadata/Terrain/Labyrinth/", StringComparison.OrdinalIgnoreCase)
                        || LabyrinthTrials.Handles(d.Type) && d.Type != LabyrinthTrials.BarDoor))
                        continue;
                    var (rw, rh) = Orientation.Size(r.Orientation, r.Room.Width, r.Room.Height);
                    trialBounds.Add((r.X * 23, r.Y * 23, (r.X + rw) * 23, (r.Y + rh) * 23));
                }
            foreach (var o in level.TileLabyrinthObjects)
                trialPoints.Add((o.X, o.Y));
            foreach (var o in level.RoomObjects)
                if (o.Type.StartsWith("Metadata/Terrain/Labyrinth/", StringComparison.OrdinalIgnoreCase)
                    || o.Type is LabyrinthTrials.Plaque or LabyrinthTrials.IntroDoor)
                    trialPoints.Add((o.X, o.Y));
        }

        bool InExcluded(int x, int y, string type)
        {
            foreach (var (x0, y0, x1, y1) in trialBounds)
                if (x >= x0 && y >= y0 && x < x1 && y < y1) return true;
            if (hasTrial && type.Equals(LabyrinthTrials.BarDoor, StringComparison.OrdinalIgnoreCase)
                && trialPoints.Any(tp => TooClose(tp, (x, y), 18)))
                return true;
            foreach (var (x0, y0, x1, y1) in level.FeatureAreas)
                if (x >= x0 && y >= y0 && x < x1 && y < y1) return true;
            return false;
        }
        var reserved = new List<(int X, int Y)> { (player.Layout.SpawnX, player.Layout.SpawnY) };
        foreach (var t in player.Layout.AreaTransitions)
            reserved.Add((t.Transition.GridX, t.Transition.GridY));
        if (player.Layout.Waypoint is { } wp)
            reserved.Add((wp.GridX, wp.GridY));

        static bool TooClose((int X, int Y) a, (int X, int Y) b, int d) =>
            (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y) < (long)d * d;

        void TryAdd(string type, string model, int x, int y, float rotation, float? z)
        {
            if (!Supported.TryGetValue(type, out var def) || questDoorTypes.Contains(type)) return;
            if (InExcluded(x, y, def.Type)) return;
            if (reserved.Any(p => TooClose(p, (x, y), 6))) return;
            if (result.Any(d => TooClose((d.X, d.Y), (x, y), 3))) return;
            string? resolvedModel = z is not null ? (string.IsNullOrEmpty(model) ? null : model)
                : (def.OwnModel || string.IsNullOrEmpty(model) ? null : model);
            // Non-monster 0x04xxxxxx object id so AreaObjectRecords replays it to late joiners and re-entries.
            result.Add(new Door(player.Instance.World.TakeObjectId(), def.Type, resolvedModel, x, y, rotation, z));
        }

        foreach (var o in level.TileDoors)
            TryAdd(o.Type, o.Model, o.X, o.Y, o.Rotation, float.IsNaN(o.Z) ? null : o.Z);
        foreach (var o in level.RoomObjects)
            if (ServerLevels.IsOrdinaryDoorType(o.Type))
                TryAdd(o.Type, o.Model, o.X, o.Y, o.Rotation, float.IsNaN(o.Z) ? null : o.Z);

        return result;
    }

    static HashSet<(int X, int Y)> BarrierCells(Player player, Door door, DoorDef def)
    {
        if (!string.IsNullOrEmpty(door.Model))
        {
            var hull = ServerLevels.ModelWalkCells(door.Model, door.X, door.Y, door.Rotation);
            if (hull.Count > 0) return hull;
        }
        if (player.Layout.Walkable is { } walk)
        {
            var (halfX, halfY) = LabyrinthTrials.DoorHull(door.Model ?? "");
            var cells = LabyrinthTrials.HullCells(door.X, door.Y, door.Rotation, halfX, halfY)
                .Where(c => walk(c.X, c.Y)).ToHashSet();
            if (cells.Count > 0) return cells;
        }
        return [];
    }

    static void ApplyBlocking(Player player, Door door, DoorDef def, bool opened)
    {
        if (opened)
        {
            player.RemoveBlocker(door.Id);
            if (player.DoorBarriers.Remove(door.Id)) player.UpdateBarrier();
            return;
        }
        player.SetBlocker(door.Id, door.X, door.Y, def.Size);
        var barrier = BarrierCells(player, door, def);
        if (barrier.Count > 0)
        {
            player.DoorBarriers[door.Id] = barrier;
            player.UpdateBarrier();
        }
    }

    static byte[] DoorBlob(Door door, DoorDef def, bool opened)
    {
        if (def.StateMachine is { } sm)
        {
            // StateMachine doors (e.g. Act 8 GoldenDoor using Thaumaturgy_SlidingDoor.ao) have a walk Hull and no
            // RemoveCollidability() or TriggerableBlockage: send dynamic Positioned + blocksNoWalker so the client
            // never permanently stamps the closed Hull, while server DoorBarriers blocks movement until opened.
            var w = new BlobWriter().U8(0).U8(0);
            foreach (string c in def.Components)
                switch (c)
                {
                    case "Positioned":
                        w.Positioned(door.X, door.Y, door.Rotation, scale: 1f,
                            fixedHeight: door.Z is float z && !float.IsNaN(z) ? BlobWriter.FixedHeight(z) : null,
                            dynamic: true, blocksNoWalker: true);
                        break;
                    case "Animated":
                        if (door.Model is { Length: > 0 } model && model.EndsWith(".ao", StringComparison.OrdinalIgnoreCase))
                            w.U8(1).WStr32(model);
                        else w.U8(0);
                        break;
                    case "InteractionAction": w.U8(0); break;
                    case "StateMachine": sm.Set("open", opened ? 1 : 0).Write(w); break;
                    case "Stats": w.Stats(null).U32(0).U32(0).U8(0); break;
                    case "Buffs": w.U16(0); break;
                }
            return w.U8(0).ToArray();
        }
        // Door / Door_grate_temp / IncaDoorLight / ScepterDoorLight have TriggerableBlockage and NO RemoveCollidability()
        // on transition: send dynamic Positioned + blocksNoWalker (matching LabyrinthTrials.Blob) so only
        // TriggerableBlockage closes cells and opening never leaves a permanently stamped Hull blocking client walking.
        // Smashable and SmashableNoHighlight keep static Positioned because their on_transition calls RemoveCollidability().
        bool blockage = !def.IsSmashable;
        byte state = (byte)(opened ? def.States : 1);
        return LabyrinthTrials.Blob(def.Components, door.Model ?? "", door.X, door.Y, door.Rotation, 1f, state,
            door.Z, blockage: blockage, blocked: !opened);
    }

    static async Task Present(Player player, Area area, Door door)
    {
        var shown = Presented.GetOrCreateValue(player);
        if (shown.Contains(door.Id)) return;
        if (!Supported.TryGetValue(door.Type, out var def)) return;
        bool opened = player.Instance.Transitioned(door.Type, door.X, door.Y);
        if (!area.Shown.ContainsKey(door.Id))
        {
            var blob = DoorBlob(door, def, opened);
            await SendWorldCreate(player, door.Id,
                () => InstanceEntities.CreateObject(door.Id, InstanceEntities.ObjectTypeHash(door.Type), blob),
                $"campaign door 0x{door.Id:x} {door.Type.Split('/')[^1]} at ({door.X}, {door.Y}), opened={opened}",
                wake: true, awaitBirth: false);
            area.Shown[door.Id] = opened;
        }
        ApplyBlocking(player, door, def, opened);
        if (opened && door.Type.Equals(GoldenDoorType, StringComparison.OrdinalIgnoreCase)
            && ProminadeGardenFlags.Any(f => !player.Character.HasQuestFlag(f)))
            await SetQuestFlags(player, ProminadeGardenFlags);
        shown.Add(door.Id);
    }

    static async Task OpenDoorLocked(Player player, Area area, Door door, DoorDef def)
    {
        if (!player.Instance.Transition(door.Type, door.X, door.Y)) return;
        if (def.StateMachine is { } sm)
        {
            await SendWorldRecord(player, door.Id,
                InstanceEntities.StateMachineUpdate(door.Id, (byte)def.StateMachineIndex, sm.Set("open", 1)),
                $"campaign door 0x{door.Id:x} {door.Type.Split('/')[^1]} opens at ({door.X}, {door.Y})");
        }
        else
        {
            await SendWorldRecord(player, door.Id,
                InstanceEntities.TransitionableUpdate(door.Id, (byte)def.TransitionableIndex, (byte)def.States),
                $"campaign door 0x{door.Id:x} {door.Type.Split('/')[^1]} opens at ({door.X}, {door.Y})");
            if (def.HasTriggerableBlockage)
                await SendWorldRecord(player, door.Id, InstanceEntities.BlockageUpdate(door.Id, false), null);
        }
        area.Shown[door.Id] = true;
        foreach (var member in Members(player))
        {
            ApplyBlocking(member, door, def, opened: true);
            if (door.Type.Equals(GoldenDoorType, StringComparison.OrdinalIgnoreCase))
                await SetQuestFlags(member, ProminadeGardenFlags);
        }
    }

    /// <summary>GameEvents.SkillCast: attacks/casts within reach break nearby Smashable and SmashableNoHighlight doors (transition_on_damage_taken).</summary>
    public static async Task BreakSmashablesNear(Player player, (int X, int Y) from)
    {
        if (player.Instance is null || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !Areas.TryGetValue(player.Instance, out var area) || area.Doors is not { Count: > 0 } doors)
            return;
        List<(Door Door, DoorDef Def)>? candidates = null;
        foreach (var d in doors)
        {
            if (!Supported.TryGetValue(d.Type, out var ddef) || !ddef.IsSmashable) continue;
            if (player.Instance.Transitioned(d.Type, d.X, d.Y)) continue;
            long dx = d.X - from.X, dy = d.Y - from.Y;
            if (dx * dx + dy * dy <= (long)SmashReach * SmashReach)
                (candidates ??= []).Add((d, ddef));
        }
        if (candidates is null) return;
        await area.Gate.WaitAsync();
        try
        {
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
            foreach (var (door, def) in candidates)
                await OpenDoorLocked(player, area, door, def);
        }
        finally { area.Gate.Release(); }
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area)
            || area.Doors?.FirstOrDefault(d => d.Id == use.TargetId) is not { } door)
            return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (!Supported.TryGetValue(door.Type, out var def)) return true;
        if (player.Instance.Transitioned(door.Type, door.X, door.Y))
        {
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, current));
            return true;
        }
        if (!TryBeginObjectWalk(player, requestedAt, door.X, door.Y, def.Size, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, door.X, door.Y, use.SkillId, use.Sequence, use.Flags,
                    targetObjectId: door.Id), null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await area.Gate.WaitAsync();
        try
        {
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (door.X, door.Y), def.Size))
                return true;
            await OpenDoorLocked(player, area, door, def);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }
}
