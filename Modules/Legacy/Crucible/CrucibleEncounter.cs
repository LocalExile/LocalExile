using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// In-zone Crucible Forge encounter (placed in areas that rolled <c>ExtraTerrainFeatures</c> <c>"Crucible"</c>,
/// row 45, <c>crucible_encounter.arm</c>).
/// <para>
/// Drives native 3.29.3c client visuals, audio, and rulesets:
/// <c>S→C 0x001f</c> <c>CrucibleRules</c> (<c>0x89</c>), <c>S→C 0x02c9</c> sub-messages <c>0x00</c>–<c>0x06</c>,
/// <c>CrucibleDevice</c> (<c>activated</c>, <c>channelling</c>, <c>in_zone</c>), <c>Fissure</c> (<c>crucible_fissure</c>,
/// <c>channelling</c>), <c>MoltenPool</c> (<c>crucible_pool</c>), and <c>ActiveSkills 1815</c> (<c>crucible_channel</c>).
/// Because 3.29.3c compiled out <c>CrucibleForgePopup</c> <c>SelectPage</c> C→S clicks (<c>0x1400f1180</c> <c>ret 0</c>)
/// and <c>CrucibleDevice</c> sets <c>targetable = false</c> on KB/M, players channel with the native
/// <c>CrucibleChannel</c> skill; a gamepad can also click the Forge. No helper NPC stands beside it: creating
/// <c>Metadata/NPC/Info/Crucible_GlyphRegular</c> stops the client ("Unknown object type serialized by server").
/// </para>
/// </summary>
public static class CrucibleEncounter
{
    /// <summary>Masked granted skill id (&lt;= 0x3fff) matching SkillCatalog's ActionTypes.HASH16 &amp; 0x3fff convention.</summary>
    public const ushort CrucibleChannelSkillId = (ushort)(InstanceCrucible.CrucibleChannelSkillHash & 0x3fff);
    const double ZoneEnterRadius = 18.0;
    const double ZoneLeaveRadius = 22.0;
    const double FullChargeSeconds = 5.0;

    enum Phase { Idle, Channeling, Spawning, Active, Completed }

    sealed class WorldObj
    {
        public uint Id;
        public string Type = "";
        public int X, Y;
        public float Rotation;
        public bool Blocked;
        public Dictionary<string, int> States = [];
    }

    sealed class EncounterState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Built;
        public Phase Phase = Phase.Idle;
        public int AreaLevel = 1;
        public WorldObj? Device;
        public List<WorldObj> Fissures = [];
        public List<WorldObj> Pools = [];
        public List<WorldObj> AllObjects = [];

        // Selected weapon/shield and channeling progress
        public byte SelectedRow;
        public uint SelectedItemId;
        public string SelectedItemName = "";
        public ushort SelectedVisualHash;
        public int SelectedAnimSet;
        public float Charge;
        public bool AutoChannel;
        /// <summary>The player who started the channel: only their packets and tick drive it, and their item gets the experience.</summary>
        public ClientState? Channeler;
        public bool ChannelActionActive;
        public ushort ChannelSequence;
        public int ThemeTagIndex = -1;

        // Spawned encounter monsters
        public HashSet<uint> Monsters = [];
        public Dictionary<uint, SpawnedMonster> Bodies = [];
    }

    sealed class PlayerView
    {
        public HashSet<uint> Presented = [];
        public bool InZone;
        public bool Greeted;
    }

    static readonly ConditionalWeakTable<InstanceState, EncounterState> Instances = new();
    static readonly ConditionalWeakTable<ClientState, PlayerView> Views = new();

    public static void Clear()
    {
        Instances.Clear();
        Views.Clear();
    }

    static IEnumerable<ClientState> Members(ClientState conn) =>
        conn.Instance.PlayersExcept(conn.PeerId)
            .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
            .Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, conn.Instance)).Distinct();

    static double Distance(int x1, int y1, int x2, int y2) =>
        Math.Sqrt((double)(x1 - x2) * (x1 - x2) + (double)(y1 - y2) * (y1 - y2));

    static bool HasForge(ClientState conn, out EncounterState encounter)
    {
        encounter = null!;
        if (conn.Instance is null || CrucibleTables.IsForgeArea(conn.Layout.Id)) return false;
        return Instances.TryGetValue(conn.Instance, out encounter!) && encounter.Device is not null;
    }

    /// <summary>Spawns <c>CrucibleDevice</c>, fissures, molten pools, and clickable glyph on <see cref="GameEvents.AreaGenerated"/>.</summary>
    public static async Task Enter(ClientState conn)
    {
        if (conn.Instance is null || CrucibleTables.IsForgeArea(conn.Layout.Id) || conn.Layout.Level is not { } level)
            return;

        var devicePlacement = level.RoomObjects.FirstOrDefault(o =>
            o.Type.Equals(CrucibleTables.DeviceType, StringComparison.OrdinalIgnoreCase));
        if (devicePlacement is null) return;

        var (remote, stream, ciphers) = conn.Wire;
        byte ruleset = CrucibleTables.Current.CrucibleRulesRow ?? InstanceCrucible.CrucibleRulesetRow;
        await Send(remote, stream, ciphers, InstanceProtocol.AddRuleset(ruleset),
            $"0x001f ruleset CrucibleRules (row {ruleset})");

        var encounter = Instances.GetOrCreateValue(conn.Instance);
        await encounter.Gate.WaitAsync();
        try
        {
            if (!encounter.Built)
            {
                Build(conn, encounter, devicePlacement);
                encounter.Built = true;
            }
        }
        finally { encounter.Gate.Release(); }

        foreach (var obj in encounter.AllObjects)
            await Present(conn, obj);
    }

    static void Build(ClientState conn, EncounterState encounter, GameServer.Data.Terrain.Generation.PlacedTileObject deviceAt)
    {
        int worldLevel = ClientTables.TableOrEmpty("WorldAreas")
            .FirstOrDefault(r => r.Text("Id") == conn.Layout.Id) is { Table: not null } wa ? wa.Int("AreaLevel") : 1;
        encounter.AreaLevel = Math.Clamp(MapRunOf(conn)?.Area.Level is > 0 ? MapRunOf(conn)!.Area.Level : worldLevel, 1, 100);

        var device = new WorldObj
        {
            Id = conn.Instance.World.NewMonsterId(),
            Type = CrucibleTables.DeviceType,
            X = deviceAt.X,
            Y = deviceAt.Y,
            Rotation = deviceAt.Rotation,
            Blocked = true,
            States = new() { ["activated"] = 0, ["channelling"] = 0, ["in_zone"] = 0 },
        };
        encounter.Device = device;
        encounter.AllObjects.Add(device);

        // 4 radiating Fissures + 3 MoltenPools around the CrucibleDevice.
        (int dx, int dy, float rot)[] fissureOffsets = [(7, 0, 0f), (-7, 0, MathF.PI), (0, 7, MathF.PI / 2f), (0, -7, -MathF.PI / 2f)];
        foreach (var (dx, dy, rot) in fissureOffsets)
        {
            var pos = conn.Layout.Near((device.X + dx, device.Y + dy));
            var f = new WorldObj
            {
                Id = conn.Instance.World.NewMonsterId(),
                Type = CrucibleTables.FissureType,
                X = pos.X,
                Y = pos.Y,
                Rotation = rot,
                States = new() { ["crucible_fissure"] = 0, ["channelling"] = 0 },
            };
            encounter.Fissures.Add(f);
            encounter.AllObjects.Add(f);
        }

        (int dx, int dy)[] poolOffsets = [(11, 6), (-11, 5), (2, -11)];
        foreach (var (dx, dy) in poolOffsets)
        {
            var pos = conn.Layout.Near((device.X + dx, device.Y + dy));
            var p = new WorldObj
            {
                Id = conn.Instance.World.NewMonsterId(),
                Type = CrucibleTables.MoltenPoolType,
                X = pos.X,
                Y = pos.Y,
                Rotation = 0f,
                States = new() { ["crucible_pool"] = 0 },
            };
            encounter.Pools.Add(p);
            encounter.AllObjects.Add(p);
        }

        Console.WriteLine($"[crucible] In-zone Crucible Forge placed in {conn.Layout.Id} (lvl {encounter.AreaLevel}) at ({device.X},{device.Y}), device=0x{device.Id:x}");
    }

    static (QuestObject Object, IReadOnlyList<string> Components, bool Blocking)? Describe(WorldObj obj)
    {
        if (CrucibleTables.Current.Objects.TryGetValue(obj.Type, out var info))
        {
            var states = info.States;
            if (states is not null)
            {
                foreach (var (k, v) in obj.States) states = states.Set(k, v);
            }
            var quest = new QuestObject(obj.X, obj.Y, info.Components, obj.Rotation, Blocked: obj.Blocked)
            {
                StateMachine = states,
            };
            return QuestObject.CanSerialize(info.Components, states is not null) ? (quest, info.Components, info.Blocking) : null;
        }

        return null;
    }

    static async Task Present(ClientState conn, WorldObj obj)
    {
        var view = Views.GetOrCreateValue(conn);
        if (view.Presented.Contains(obj.Id) || Describe(obj) is not var (quest, _, blocking)) return;
        byte[] blob = InstanceEntities.CreateObject(obj.Id, InstanceEntities.ObjectTypeHash(obj.Type), quest.Blob());
        await SendWorldCreate(conn, obj.Id, () => blob, $"crucible {obj.Type.Split('/')[^1]} 0x{obj.Id:x} at ({obj.X},{obj.Y})",
            wake: true, awaitBirth: false);
        if (blocking)
        {
            if (CrucibleTables.Current.Objects.TryGetValue(obj.Type, out var info) && !info.HasStationaryCollision)
            {
                conn.SetBlocker(obj.Id, obj.X, obj.Y, Math.Max(0, info.Size - 1), null);
            }
            else
            {
                var (size, hull) = StationaryCollision.Resolve(obj.Type);
                if (size >= 0) conn.SetBlocker(obj.Id, obj.X, obj.Y, size, hull);
            }
        }
        view.Presented.Add(obj.Id);
    }

    static async Task SendStates(ClientState conn, IEnumerable<WorldObj> objects)
    {
        var list = objects.ToList();
        foreach (var member in Members(conn))
        {
            if (!Views.TryGetValue(member, out var view)) continue;
            var (remote, stream, ciphers) = member.Wire;
            foreach (var obj in list)
            {
                if (!view.Presented.Contains(obj.Id) || Describe(obj) is not var (quest, components, _) || quest.StateMachine is null)
                    continue;
                int smIndex = components.ToList().IndexOf("StateMachine");
                if (smIndex < 0) continue;
                await SendWorldRecord(remote, stream, ciphers, member, obj.Id,
                    InstanceEntities.StateMachineUpdate(obj.Id, (byte)smIndex, quest.StateMachine), null);
            }
        }
    }

    static async Task BroadcastRules(ClientState conn, byte[] message, string? log = null)
    {
        foreach (var member in Members(conn))
        {
            var (remote, stream, ciphers) = member.Wire;
            await Send(remote, stream, ciphers, message, log);
        }
    }

    /// <summary>Provides <c>in_crucible_zone</c> and channeling weapon visual/animation stats on <see cref="GameEvents.Stats"/>.</summary>
    public static void OnStats(StatSheet sheet)
    {
        var conn = sheet.Conn;
        if (!HasForge(conn, out var encounter) || encounter.Phase is Phase.Completed) return;
        if (!Views.TryGetValue(conn, out var view) || !view.InZone) return;

        sheet.Add(CrucibleTables.InZoneStat, 1);
        if (encounter.SelectedVisualHash != 0)
            sheet.Add(CrucibleTables.ChannelWeaponVisualStat, encounter.SelectedVisualHash);
        sheet.Add(CrucibleTables.ChannelWeaponAnimStat, encounter.SelectedAnimSet);
    }

    /// <summary>Grants <c>ActiveSkills 1815</c> (<c>crucible_channel</c>) while in an area with an active Crucible Forge.</summary>
    public static IReadOnlyList<GrantedSkill> GrantedSkills(ClientState conn)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase is Phase.Completed) return [];
        var skill = SkillsFor(conn).Find(CrucibleChannelSkillId, "CrucibleChannel", 1);
        return skill is not null ? [skill] : [];
    }

    /// <summary>Binds <c>crucible_channel</c> onto the first open skill-bar slot when granted.</summary>
    public static void OnSkillBarBuilding(ClientState conn, ushort[] ids)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase is Phase.Completed) return;
        if (Array.IndexOf(ids, CrucibleChannelSkillId) >= 0) return;
        for (int i = 3; i < ids.Length; i++)
        {
            if (ids[i] == 0) { ids[i] = CrucibleChannelSkillId; return; }
        }
    }

    /// <summary>Handles C→S skill casts of <c>crucible_channel</c> (<c>0x1f29</c>) to start/sustain channeling.</summary>
    public static async Task<bool> HandleChannelSkill(ClientState conn, ushort skillId, ushort sequence, ushort flags)
    {
        if (skillId != CrucibleChannelSkillId && skillId != InstanceCrucible.CrucibleChannelSkillHash) return false;
        if (!HasForge(conn, out var encounter) || encounter.Device is not { } device) return true;
        var (remote, stream, ciphers) = conn.Wire;
        if (encounter.Phase is Phase.Spawning or Phase.Active or Phase.Completed)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
            return true;
        }

        var pos = PlayerOf(conn).Position;
        double dist = Distance(pos.X, pos.Y, device.X, device.Y);
        if (dist > ZoneLeaveRadius)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
            await PlayerOf(conn).Tell("Move closer to the Crucible Forge to channel.");
            return true;
        }

        bool startedAction = false;
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase == Phase.Idle)
            {
                if (!SelectBestWeapon(conn, encounter))
                {
                    await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
                    await PlayerOf(conn).Tell("Equip or carry an eligible weapon or shield to forge at the Crucible.");
                    return true;
                }
                encounter.AutoChannel = false;
                encounter.ChannelActionActive = true;
                encounter.ChannelSequence = sequence;
                startedAction = true;
                await BeginChannelingLocked(conn, encounter);
            }
            else if (encounter.Phase == Phase.Channeling && ReferenceEquals(encounter.Channeler, conn))
                encounter.AutoChannel = false;
        }
        finally { encounter.Gate.Release(); }

        if (startedAction)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, pos.X, pos.Y, device.X, device.Y, skillId, sequence, flags),
                "0x01ab CrucibleChannel start");
        return true;
    }

    /// <summary>Takes the channeler's C→S <c>0x0197</c>/<c>0x0198</c> aim updates while they hold <c>CrucibleChannel</c>.</summary>
    public static Task<bool> HandleChannelUpdate(ClientState conn)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase != Phase.Channeling || encounter.AutoChannel
            || !ReferenceEquals(encounter.Channeler, conn))
            return Task.FromResult(false);
        return Task.FromResult(true);
    }

    /// <summary>Handles C→S <c>0x0199</c> channel end to immediately release the Forge wave.</summary>
    public static async Task<bool> HandleChannelEnd(ClientState conn, double xpMultiplier, double geodeDropRate)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase != Phase.Channeling || encounter.AutoChannel
            || !ReferenceEquals(encounter.Channeler, conn))
            return false;
        await ReleaseEncounter(conn, encounter, xpMultiplier, geodeDropRate);
        return true;
    }

    /// <summary>
    /// Handles clicks on <c>CrucibleDevice</c> (gamepad; it is not targetable with a mouse).
    /// Idle → selects best weapon + next connected node and starts smooth channeling ramp.
    /// Channeling → immediately releases the Forge at the current charge tier!
    /// </summary>
    public static async Task<bool> UseObject(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt, double xpMultiplier, double geodeDropRate)
    {
        if (!HasForge(conn, out var encounter) || encounter.Device is not { } device) return false;
        if (use.TargetId != device.Id) return false;

        if (encounter.Phase == Phase.Completed)
        {
            await PlayerOf(conn).Tell("This Crucible Forge has already been completed.");
            return true;
        }
        if (encounter.Phase is Phase.Spawning or Phase.Active)
        {
            await PlayerOf(conn).Tell("Defeat the molten Crucible monsters to complete the Forge.");
            return true;
        }

        if (!await WorldOf(conn).WalkUpTo(device.X, device.Y, 14, requestedAt))
            return true;

        bool shouldRelease = false;
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase == Phase.Idle)
            {
                if (!SelectBestWeapon(conn, encounter))
                {
                    await PlayerOf(conn).Tell("Equip or carry an eligible weapon or shield to channel at the Crucible Forge.");
                    return true;
                }
                encounter.AutoChannel = true;
                await BeginChannelingLocked(conn, encounter);
                await PlayerOf(conn).Tell($"Channeling {encounter.SelectedItemName}... Click the Forge again at any time to release at your desired difficulty (or wait for Lethal).");
            }
            else if (encounter.Phase == Phase.Channeling)
            {
                shouldRelease = true;
            }
        }
        finally { encounter.Gate.Release(); }

        if (shouldRelease)
            await ReleaseEncounter(conn, encounter, xpMultiplier, geodeDropRate);
        return true;
    }

    /// <summary>Periodic proximity, channeling progress, and wave-completion checks (<see cref="GameEvents.Tick"/>).</summary>
    public static async Task Tick(ClientState conn, double dt, double xpMultiplier, double geodeDropRate)
    {
        if (!HasForge(conn, out var encounter) || encounter.Device is not { } device) return;
        if (conn.Paused || conn.Closed || conn.LeavingArea) return;

        var view = Views.GetOrCreateValue(conn);
        var pos = PlayerOf(conn).Position;
        double dist = Distance(pos.X, pos.Y, device.X, device.Y);

        // 1. Proximity enter/leave transitions for CrucibleDevice & CrucibleRules (0x02c9).
        if (encounter.Phase is Phase.Idle or Phase.Channeling)
        {
            if (!view.InZone && dist <= ZoneEnterRadius)
            {
                view.InZone = true;
                device.States["in_zone"] = 1;
                await SendStates(conn, [device]);
                var (remote, stream, ciphers) = conn.Wire;
                await Send(remote, stream, ciphers, InstanceCrucible.ForgeEnterZone(), "0x02c9 CrucibleRules inZone=1");
                if (encounter.SelectedItemId == 0) SelectBestWeapon(conn, encounter);
                await WorldOf(conn).RefreshStats("entered Crucible Forge zone");
                if (!view.Greeted && encounter.Phase == Phase.Idle)
                {
                    view.Greeted = true;
                    string target = encounter.SelectedItemName.Length > 0 ? $"Selected: {encounter.SelectedItemName}. " : "";
                    await PlayerOf(conn).Tell($"Crucible Forge: {target}Hold Crucible Channel to charge the Forge.");
                }
            }
            else if (view.InZone && dist > ZoneLeaveRadius)
            {
                view.InZone = false;
                if (encounter.Phase == Phase.Channeling && ReferenceEquals(encounter.Channeler, conn))
                {
                    await ReleaseEncounter(conn, encounter, xpMultiplier, geodeDropRate);
                }
                else if (encounter.Phase == Phase.Idle)
                {
                    device.States["in_zone"] = 0;
                    await SendStates(conn, [device]);
                    var (remote, stream, ciphers) = conn.Wire;
                    await Send(remote, stream, ciphers, InstanceCrucible.ForgeLeaveZone(), "0x02c9 CrucibleRules inZone=0");
                }
                await WorldOf(conn).RefreshStats("left Crucible Forge zone");
            }
        }

        // 2. Channeling charge ramp & release check: on the channeler's tick and clock only.
        if (encounter.Phase == Phase.Channeling)
        {
            if (!ReferenceEquals(encounter.Channeler, conn)) return;
            bool releaseNow = false;
            float newCharge = 0f;
            await encounter.Gate.WaitAsync();
            try
            {
                if (encounter.Phase == Phase.Channeling)
                {
                    encounter.Charge = Math.Clamp(encounter.Charge + (float)(Math.Max(0.05, dt) / FullChargeSeconds), 0.05f, 1.0f);
                    newCharge = encounter.Charge;
                    // A held channel sends nothing between its start and its end (0x0199, or the lockstep stop) while
                    // the cursor rests, so only a full charge releases here.
                    if (encounter.Charge >= 1.0f) releaseNow = true;
                }
            }
            finally { encounter.Gate.Release(); }

            if (newCharge > 0f)
                await BroadcastRules(conn, InstanceCrucible.ForgeCharge(newCharge));

            if (releaseNow)
                await ReleaseEncounter(conn, encounter, xpMultiplier, geodeDropRate);
            return;
        }

        // 3. Active combat wave completion check (also catches corpse-consumed kills).
        if (encounter.Phase == Phase.Active)
        {
            bool allDead = false;
            lock (conn.Monsters)
            {
                foreach (uint id in encounter.Monsters.ToArray())
                {
                    if (conn.Monsters.TryGetValue(id, out var live))
                    {
                        encounter.Bodies.TryAdd(id, live);
                        if (live.Dead || live.Life == 0) { encounter.Monsters.Remove(id); encounter.Bodies.Remove(id); }
                    }
                    else if (encounter.Bodies.TryGetValue(id, out var body) && (body.Dead || body.Life == 0))
                    {
                        encounter.Monsters.Remove(id);
                        encounter.Bodies.Remove(id);
                    }
                    else
                    {
                        encounter.Monsters.Remove(id);
                    }
                }
                allDead = encounter.Monsters.Count == 0;
            }

            if (allDead)
                await CompleteEncounter(conn, encounter, xpMultiplier, geodeDropRate);
        }
    }

    /// <summary>Tracks encounter monster kills synchronously inside <see cref="GameEvents.Kill"/>.</summary>
    public static void OnKill(KillContext kill, double xpMultiplier, double geodeDropRate)
    {
        if (!HasForge(kill.Conn, out var encounter) || encounter.Phase != Phase.Active) return;
        bool finished;
        lock (kill.Conn.Monsters)
        {
            uint id = encounter.Monsters.FirstOrDefault(mid =>
                (encounter.Bodies.TryGetValue(mid, out var b) && ReferenceEquals(b, kill.Monster))
                || (kill.Conn.Monsters.TryGetValue(mid, out var m) && ReferenceEquals(m, kill.Monster)));
            if (id == 0) return;
            encounter.Monsters.Remove(id);
            encounter.Bodies.Remove(id);
            finished = encounter.Monsters.Count == 0;
        }
        if (finished)
            kill.Later(() => CompleteEncounter(kill.Conn, encounter, xpMultiplier, geodeDropRate));
    }

    public static void OnPlayerLeaving(ClientState conn)
    {
        Views.Remove(conn);
        // A channeler who leaves mid-channel frees the Forge for the players who stay.
        if (conn.Instance is not null && Instances.TryGetValue(conn.Instance, out var encounter)
            && ReferenceEquals(encounter.Channeler, conn) && encounter.Phase == Phase.Channeling)
        {
            encounter.Phase = Phase.Idle;
            encounter.Channeler = null;
            encounter.ChannelActionActive = false;
        }
    }

    // Must be called under encounter.Gate.
    static async Task BeginChannelingLocked(ClientState conn, EncounterState encounter)
    {
        encounter.Phase = Phase.Channeling;
        encounter.Channeler = conn;
        encounter.Charge = 0.08f;

        if (encounter.Device is { } device)
        {
            device.States["in_zone"] = 1;
            device.States["channelling"] = 1;
        }
        foreach (var f in encounter.Fissures) { f.States["crucible_fissure"] = 2; f.States["channelling"] = 1; }
        foreach (var p in encounter.Pools) p.States["crucible_pool"] = 1;

        await SendStates(conn, encounter.AllObjects);
        await BroadcastRules(conn, InstanceCrucible.ForgeLockWeapon(), "0x02c9 CrucibleRules lockWeapon");
        await BroadcastRules(conn, InstanceCrucible.ForgeChannelling(true), "0x02c9 CrucibleRules channelling=1");
        await BroadcastRules(conn, InstanceCrucible.ForgeState(1), "0x02c9 CrucibleRules state=1");
        await BroadcastRules(conn, InstanceCrucible.ForgeCharge(encounter.Charge));
    }

    static async Task ReleaseEncounter(ClientState conn, EncounterState encounter, double xpMultiplier, double geodeDropRate)
    {
        int chargePct;
        int areaLevel;
        int themeTag;
        bool endChannelAction;
        ushort channelSequence;
        List<WorldObj> spawnPoints;
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Channeling) return;
            encounter.Phase = Phase.Spawning;
            endChannelAction = encounter.ChannelActionActive;
            channelSequence = encounter.ChannelSequence;
            encounter.ChannelActionActive = false;
            encounter.ChannelSequence = 0;
            chargePct = Math.Clamp((int)Math.Round(encounter.Charge * 100f), 10, 100);
            areaLevel = encounter.AreaLevel;
            themeTag = encounter.ThemeTagIndex;
            spawnPoints = [.. encounter.Pools];
            if (encounter.Device is { } device)
            {
                device.States["channelling"] = 0;
                device.States["activated"] = 1;
            }
            foreach (var f in encounter.Fissures) { f.States["channelling"] = 0; f.States["crucible_fissure"] = 3; }
        }
        finally { encounter.Gate.Release(); }

        conn = encounter.Channeler is { Closed: false, LeavingArea: false } channeler ? channeler : conn;
        var (remote, stream, ciphers) = conn.Wire;
        if (endChannelAction)
        {
            if (conn.LockstepClient && channelSequence != 0)
                await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                    InstanceProtocol.ChannelFinished(PlayerId, channelSequence), $"0x01ae CrucibleChannel end (seq 0x{channelSequence:x4})");
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), "0x01ac CrucibleChannel end");
        }

        await SendStates(conn, encounter.AllObjects);
        await BroadcastRules(conn, InstanceCrucible.ForgeChannelling(false), "0x02c9 CrucibleRules channelling=0");
        await BroadcastRules(conn, InstanceCrucible.ForgeState(2), "0x02c9 CrucibleRules state=2 (combat)");

        var rng = Random.Shared;
        var theme = CrucibleMonsters.ThemeFor(themeTag, rng);
        var plan = CrucibleMonsters.PlanForgeWave(chargePct, areaLevel);
        var scaling = CrucibleMonsters.ScalingMods(areaLevel, plan.ExtraMoreLife, plan.ExtraMoreDamage);
        byte monsterLevel = (byte)Math.Clamp(areaLevel, 1, 100);

        int difficultyIdx = CrucibleTables.Current.DifficultyFor(chargePct);
        string diffName = difficultyIdx < CrucibleTables.Current.Difficulties.Count
            ? CrucibleTables.Current.Difficulties[difficultyIdx].Name : $"Tier {difficultyIdx + 1}";

        var spawnList = new List<(string VarietyId, MonsterRarity Rarity)>();
        for (int i = 0; i < plan.NormalCount && theme.Normal.Count > 0; i++)
            spawnList.Add((theme.Normal[rng.Next(theme.Normal.Count)], MonsterRarity.Normal));
        for (int i = 0; i < plan.MagicCount && theme.Normal.Count > 0; i++)
            spawnList.Add((theme.Normal[rng.Next(theme.Normal.Count)], MonsterRarity.Magic));
        for (int i = 0; i < plan.RareCount && theme.Normal.Count > 0; i++)
            spawnList.Add((theme.Normal[rng.Next(theme.Normal.Count)], MonsterRarity.Rare));
        for (int i = 0; i < plan.UniqueCount && theme.Bosses.Count > 0; i++)
            spawnList.Add((theme.Bosses[rng.Next(theme.Bosses.Count)], MonsterRarity.Unique));

        int spawned = 0;
        for (int i = 0; i < spawnList.Count; i++)
        {
            if (conn.Closed || conn.LeavingArea) break;
            var (varietyId, rarity) = spawnList[i];
            var variety = CrucibleMonsters.ResolveVariety(varietyId, theme);
            if (variety is null) continue;
            var anchor = spawnPoints.Count > 0 ? spawnPoints[i % spawnPoints.Count] : encounter.Device!;
            var cell = conn.Layout.Near((anchor.X + (i % 3 - 1) * 3, anchor.Y + ((i / 3) % 3 - 1) * 3));
            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, monsterLevel, rarity,
                cell.X, cell.Y, unrisen: false, alone: false, extraMods: scaling);
            if (id != 0)
            {
                lock (conn.Monsters)
                {
                    encounter.Monsters.Add(id);
                    if (conn.Monsters.TryGetValue(id, out var m)) encounter.Bodies[id] = m;
                }
                spawned++;
            }
        }

        bool alreadyCleared;
        await encounter.Gate.WaitAsync();
        try
        {
            encounter.Phase = Phase.Active;
            lock (conn.Monsters) alreadyCleared = encounter.Monsters.Count == 0;
        }
        finally { encounter.Gate.Release(); }

        Console.WriteLine($"[crucible] {CharacterName(conn)} released Forge at {chargePct}% ({diffName}, theme={theme.Tag}): spawned {spawned} monsters");
        await PlayerOf(conn).Tell($"Crucible Forge unleashed ({diffName}, {chargePct}%): defeat {spawned} molten {theme.Tag} monsters!");

        if (spawned == 0 || alreadyCleared)
            await CompleteEncounter(conn, encounter, xpMultiplier, geodeDropRate);
    }

    static async Task CompleteEncounter(ClientState conn, EncounterState encounter, double xpMultiplier, double geodeDropRate)
    {
        int chargePct;
        int areaLevel;
        byte itemRow;
        uint itemId;
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Active) return;
            encounter.Phase = Phase.Completed;
            chargePct = Math.Clamp((int)Math.Round(encounter.Charge * 100f), 10, 100);
            areaLevel = encounter.AreaLevel;
            itemRow = encounter.SelectedRow;
            itemId = encounter.SelectedItemId;

            if (encounter.Device is { } device)
            {
                device.States["activated"] = 2;
                device.States["channelling"] = 0;
                device.States["in_zone"] = 0;
            }
            foreach (var p in encounter.Pools) p.States["crucible_pool"] = 2;
        }
        finally { encounter.Gate.Release(); }

        await SendStates(conn, encounter.AllObjects);
        await BroadcastRules(conn, InstanceCrucible.ForgeState(3), "0x02c9 CrucibleRules state=3 (complete)");
        await BroadcastRules(conn, InstanceCrucible.ForgeLeaveZone(), "0x02c9 CrucibleRules inZone=0");

        // Award Weapon Passive Tree XP to the selected item, which is the channeler's.
        if (encounter.Channeler is { Closed: false } owner && ReferenceEquals(owner.Instance, conn.Instance)) conn = owner;
        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
        var found = items.Items(itemRow).FirstOrDefault(i => i.Id == itemId);
        if (found is not null && catalog.ByHash(found.Item.TypeHash) is { } type)
        {
            int ilvl = Math.Clamp(found.Item.ItemLevel == 0 ? areaLevel : found.Item.ItemLevel, 1, 100);
            var balance = CrucibleTables.Current.BalanceFor(ilvl);
            uint xpGrant = ComputeEncounterXp(balance, chargePct, xpMultiplier);

            // Auto-queue the next connected branch node if none is pending yet so XP allocates cleanly.
            Item prepared = EnsurePendingBranch(found.Item, Random.Shared);
            Item awarded = CrucibleTreeEngine.AwardExperience(prepared, type, catalog.Mods, xpGrant, Random.Shared,
                out bool revealedNow, out int newlyAllocated);

            if (items.Change(itemRow, itemId, _ => awarded) is { } updated)
            {
                var (remote, stream, ciphers) = conn.Wire;
                await Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(itemRow), items.NextRevision(itemRow), [itemId], [updated]),
                    $"0x00bf inventory {PlayerInventories.InventoryId(itemRow)}: Crucible Forge +{xpGrant} XP on {type.Name}");

                if (CrucibleItemActions.IsEquipmentRow(itemRow))
                    await WorldOf(conn).RefreshStats("Crucible Forge XP");

                string summary = revealedNow
                    ? $"Crucible Forge complete! Revealed a Crucible Passive Skill Tree on {type.Name} (+{xpGrant:N0} XP)."
                    : newlyAllocated > 0
                        ? $"Crucible Forge complete! +{xpGrant:N0} XP on {type.Name} ({newlyAllocated} passive node(s) allocated)."
                        : $"Crucible Forge complete! +{xpGrant:N0} XP on {type.Name}.";
                await PlayerOf(conn).Tell(summary);
                Console.WriteLine($"[crucible] {CharacterName(conn)} completed Forge: +{xpGrant} XP on {type.Name} (revealed={revealedNow}, allocated={newlyAllocated})");
            }
        }

        // Roll Igneous / Crystalline Geode drop in maps (areaLevel >= 68).
        if (CrucibleRemnants.RollGeodeDrop(catalog, areaLevel, geodeDropRate, Random.Shared) is { } geode && encounter.Device is { } dev)
        {
            var dropCell = conn.Layout.Near((dev.X + 3, dev.Y + 3));
            var worldItem = new WorldItemObject(dropCell.X, dropCell.Y, geode);
            uint dropId = conn.Instance.Ground.Add(worldItem, conn);
            var wire = conn.Wire;
            await SendGroundItem(wire.Remote, wire.Stream, wire.Ciphers, conn, dropId, worldItem, null, justDropped: true);
        }

        await WorldOf(conn).RefreshStats("Crucible Forge complete");
    }

    /// <summary>
    /// Computes Crucible XP awarded for a completed Forge encounter:
    /// Easy (~45% Bar1), Moderate (~115% Bar1, reveals a fresh weapon), Hard (~200% Bar1), Brutal (~340% Bar1), Lethal (~550% Bar1).
    /// </summary>
    static uint ComputeEncounterXp(CrucibleTables.LevelBalance balance, int chargePct, double multiplier)
    {
        double ratio = chargePct switch
        {
            <= 30 => 0.45 * (chargePct / 30.0),
            <= 55 => 0.45 + 0.70 * ((chargePct - 30) / 25.0),
            <= 75 => 1.15 + 0.85 * ((chargePct - 55) / 20.0),
            <= 90 => 2.00 + 1.40 * ((chargePct - 75) / 15.0),
            _ => 3.40 + 2.10 * ((chargePct - 90) / 10.0),
        };
        double raw = balance.Bar1 * Math.Max(0.25, ratio) * Math.Max(0.1, multiplier);
        return (uint)Math.Clamp(Math.Round(raw), 1, balance.Bar5);
    }

    /// <summary>If the weapon has a revealed tree with no pending target for its next unallocated column, queues a valid connected child.</summary>
    static Item EnsurePendingBranch(Item item, Random random)
    {
        if (item.WeaponPassiveTree is not { HasTree: true } tree) return item;
        int deepest = CrucibleTreeTopology.DeepestAllocatedColumn(tree.Allocation);
        int nextCol = deepest + 1;
        if (nextCol >= CrucibleTreeTopology.Columns) return item;
        if (tree.PendingRow(nextCol) >= 0) return item;

        int chosenRow = -1;
        if (nextCol == 0)
        {
            if (tree.HasNode(0, 0)) chosenRow = 0;
        }
        else
        {
            int parentRow = CrucibleTreeTopology.AllocatedRowInColumn(tree.Allocation, deepest);
            if (parentRow >= 0)
            {
                var children = CrucibleTreeTopology.ConnectedChildren(tree.Layout, tree.LeftParentConnections, tree.RightParentConnections, deepest, parentRow);
                if (children.Count > 0) chosenRow = children[random.Next(children.Count)];
            }
        }

        return chosenRow >= 0 ? item with { WeaponPassiveTree = tree.WithPendingRow(nextCol, (sbyte)chosenRow) } : item;
    }

    /// <summary>Selects the best eligible weapon/shield from active equipment or main inventory for the Forge.</summary>
    static bool SelectBestWeapon(ClientState conn, EncounterState encounter)
    {
        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
        var tables = CrucibleTables.Current;

        byte[] searchRows = [PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Weapon2, PlayerInventories.Offhand2, PlayerInventories.MainInventory];
        (byte Row, InventoryItem Item, ItemCatalog.BaseType Type)? best = null;

        foreach (byte row in searchRows)
        {
            foreach (var invItem in items.Items(row))
            {
                if (catalog.ByHash(invItem.Item.TypeHash) is not { } type) continue;
                if (!CrucibleTreeEngine.CanChannelInForge(invItem.Item, type, catalog.Mods)) continue;

                // Prefer an item that still needs XP / tree reveal over a fully maxed tree.
                int ilvl = Math.Clamp(invItem.Item.ItemLevel == 0 ? 1 : invItem.Item.ItemLevel, 1, 100);
                var bal = tables.BalanceFor(ilvl);
                bool maxed = invItem.Item.WeaponPassiveTree is { HasTree: true } t && t.Experience >= (uint)bal.Bar5;
                if (!maxed)
                {
                    best = (row, invItem, type);
                    break;
                }
                best ??= (row, invItem, type);
            }
            if (best is not null && best.Value.Item.Item.WeaponPassiveTree is not { HasTree: true } bt)
                break;
            if (best is not null)
            {
                int ilvl = Math.Clamp(best.Value.Item.Item.ItemLevel == 0 ? 1 : best.Value.Item.Item.ItemLevel, 1, 100);
                if (best.Value.Item.Item.WeaponPassiveTree!.Experience < (uint)tables.BalanceFor(ilvl).Bar5)
                    break;
            }
        }

        if (best is not var (chosenRow, chosenItem, chosenType)) return false;

        encounter.SelectedRow = chosenRow;
        encounter.SelectedItemId = chosenItem.Id;
        encounter.SelectedItemName = chosenType.Name;
        encounter.SelectedVisualHash = chosenType.VisualIdentity;
        encounter.SelectedAnimSet = CrucibleTreeEngine.CategoryOf(chosenItem.Item, chosenType, catalog.Mods) switch
        {
            CrucibleTreeEngine.WeaponCategory.TwoHand => 1,
            CrucibleTreeEngine.WeaponCategory.Shield => 2,
            _ => 0,
        };

        // Pick monster theme from the weapon's tree CrucibleTags when revealed.
        encounter.ThemeTagIndex = -1;
        if (chosenItem.Item.WeaponPassiveTree is { HasTree: true } tree)
        {
            foreach (var node in tree.Nodes)
            {
                if (catalog.Mods.ByHash(node.ModHash) is { } mod
                    && tables.SkillByModRow.TryGetValue(mod.Row, out var skill)
                    && skill.CrucibleTagRows.Length > 0)
                {
                    encounter.ThemeTagIndex = skill.CrucibleTagRows[0];
                    break;
                }
            }
        }

        return true;
    }
}
