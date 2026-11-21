using System.Buffers.Binary;
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
/// <c>S→C 0x001f</c> <c>CrucibleRules</c> (<c>0x89</c>), <c>S→C 0x02c9</c> sub-messages <c>0x00</c>–<c>0x05</c>,
/// <c>CrucibleDevice</c> (<c>activated</c>, <c>channelling</c>, <c>in_zone</c>), <c>Fissure</c> (<c>crucible_fissure</c>,
/// <c>channelling</c>), <c>MoltenPool</c> (<c>crucible_pool</c>), the monsters' <c>crucible_frozen</c> buff and
/// <c>broken_out</c> state, and <c>ActiveSkills 1815</c> (<c>crucible_channel</c>).
/// The 3.29 client never tells the server which item or skill the player picked in the Forge popup (its page events
/// reach empty handlers, <c>0x1400f1180</c>), so <see cref="SelectTarget"/> takes the pick from a ruleset action a
/// changed client sends, and the server's default stands until one comes. No helper NPC stands beside the Forge:
/// creating <c>Metadata/NPC/Info/Crucible_GlyphRegular</c> stops the client ("Unknown object type serialized by server").
/// </para>
/// </summary>
public static class CrucibleEncounter
{
    const ushort CrucibleChannelSkillId = InstanceCrucible.CrucibleChannelSkillHash;

    // Our own numbers: GGG never published them, and no client table holds them.
    const double ZoneEnterRadius = 18.0;
    const double ZoneLeaveRadius = 22.0;
    /// <summary>One player's channel from nothing to full charge; each further channeller adds as much.</summary>
    const double FullChargeSeconds = 10.0;
    /// <summary>The popup's click-and-hold repeats the skill use every frame and sends nothing on release: this long
    /// without one is the release.</summary>
    const int StreamEndMs = 750;
    /// <summary>Steps of a full channel: each lengthens one fissure and forms a monster at a pool at its end.</summary>
    const int ChargeSteps = 32;
    /// <summary>The fissures: how many, where they start and how far a step takes the pools along one. Only the first
    /// step of each is drawn, as two Fissure objects: the model is about 4.5 cells long, and none showed further out
    /// (seen live).</summary>
    const int FissureArms = 8, FissurePieces = 2;
    const double FissureStart = 14.0, FissureStep = 7.0;
    /// <summary>This many held monsters of one tier become one of the next.</summary>
    const int MergeCount = 3;
    /// <summary>The tier of a new monster at full charge (its first is tier 1); merging reaches the second boss, tier 10.</summary>
    const int TopSpawnTier = 9;
    const int BossTier = 9, MaxTier = 10;
    /// <summary>Percent per tier that a monster below the bosses is magic, or rare.</summary>
    const int MagicChancePerTier = 8, RareChancePerTier = 3;
    /// <summary>More damage dealt and less damage taken by a monster formed at full charge, in percent.</summary>
    const int FullChargeMoreDamage = 50, FullChargeLessDamageTaken = 30;
    /// <summary>The sum of <c>MonsterVarieties.ExperienceMultiplier</c> over the monsters killed that pays the area
    /// level's Bar1: a full channel's monsters sum to about 1830 for most themes (Chaos and Demon about 2200), a little
    /// over it. Taking the area level's row of
    /// <c>WeaponPassiveTreeBalancePerItemLevel</c> as the curve is ours too.</summary>
    const double XpReferenceSum = 1650.0;
    /// <summary>Tiers per step of a released monster's <c>crystal_size</c> (0..3).</summary>
    const int TiersPerCrystalSize = 2;
    /// <summary>The channel animation by what the target is.</summary>
    const int OneHandAnimSet = 0, TwoHandAnimSet = 1, ShieldAnimSet = 2;

    /// <summary><c>CrucibleDevice.ot</c> plays <c>breakout_wave</c> 1.0 s after <c>activated = 1</c>.</summary>
    const int BreakoutDelayMs = 1000;

    /// <summary>The module's settings.</summary>
    public static double ExperienceMultiplier = 1, GeodeDropRate = 1;

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

    /// <summary>A molten monster waiting at its pool for the release.</summary>
    sealed record Held(uint Id, int Tier, WorldObj Pool, SpawnedMonster Body);

    sealed class EncounterState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Built;
        public volatile Phase Phase = Phase.Idle;
        public int AreaLevel = 1;
        public WorldObj? Device;
        public List<WorldObj> Fissures = [];
        public List<WorldObj> Pools = [];
        public List<WorldObj> AllObjects = [];

        /// <summary>Players inside the ring.</summary>
        public int InZone;
        public float Charge;
        public int Steps;
        public CrucibleMonsters.ThemePool? Theme;
        /// <summary>Everyone who channelled: each one's own target item gets the experience.</summary>
        public HashSet<ClientState> Channellers = [];
        public List<Held> Held = [];
        public long BreakoutAtMs;

        // The released monsters still alive, and the experience multipliers of those killed.
        public HashSet<uint> Monsters = [];
        public Dictionary<uint, SpawnedMonster> Bodies = [];
        public int KilledExperience;
    }

    sealed class PlayerView
    {
        public HashSet<uint> Presented = [];
        /// <summary>The held monsters this client shows frozen, with its buff instance for each.</summary>
        public Dictionary<uint, ushort> Frozen = [];
        public bool InZone;

        // The item this player channels for.
        public byte TargetRow;
        public uint TargetItemId;
        public ushort VisualHash;
        public int AnimSet;
        public int ThemeTag = -1;

        // The player's channel: started by a skill use, repeated every frame by the popup's click-and-hold.
        public bool Holding, Stream;
        public ushort Sequence;
        public long LastUseMs;
    }

    static readonly ConditionalWeakTable<InstanceState, EncounterState> Instances = new();
    static readonly ConditionalWeakTable<ClientState, PlayerView> Views = new();

    public static void Clear()
    {
        Instances.Clear();
        Views.Clear();
    }

    internal static IEnumerable<ClientState> Members(ClientState conn) =>
        conn.Instance.PlayersExcept(conn.PeerId)
            .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
            .Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, conn.Instance)).Distinct();

    static bool HasForge(ClientState conn, out EncounterState encounter)
    {
        encounter = null!;
        return conn.Instance is not null && Instances.TryGetValue(conn.Instance, out encounter!) && encounter.Device is not null;
    }

    /// <summary>Spawns the <c>CrucibleDevice</c> on <see cref="GameEvents.AreaGenerated"/>.</summary>
    public static async Task Enter(ClientState conn)
    {
        if (conn.Instance is null || CrucibleTables.IsForgeArea(conn.Layout.Id) || conn.Layout.Level is not { } level)
            return;

        var devicePlacement = level.RoomObjects.FirstOrDefault(o =>
            o.Type.Equals(CrucibleTables.DeviceType, StringComparison.OrdinalIgnoreCase));
        if (devicePlacement is null) return;

        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceProtocol.AddRuleset(InstanceCrucible.CrucibleRulesetRow),
            "0x001f ruleset CrucibleRules");

        var encounter = Instances.GetOrCreateValue(conn.Instance);
        WorldObj[] objects;
        await encounter.Gate.WaitAsync();
        try
        {
            if (!encounter.Built)
            {
                Build(conn, encounter, devicePlacement);
                encounter.Built = true;
            }
            objects = [.. encounter.AllObjects];
        }
        finally { encounter.Gate.Release(); }

        await Task.WhenAll(objects.Select(obj => Present(conn, obj)));
        // Who arrives after the release missed the state's one send.
        if (encounter.Phase is Phase.Spawning or Phase.Active or Phase.Completed)
            await Send(remote, stream, ciphers, InstanceCrucible.ForgeState((byte)(encounter.Phase == Phase.Completed ? 3 : 2)),
                "0x02c9 CrucibleRules state for a late arrival");
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
        lock (view.Presented)
            if (!view.Presented.Add(obj.Id)) return;
        if (Describe(obj) is not var (quest, _, blocking)) return;
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
    }

    /// <summary>The objects' states to every player who has them, as one batch: no send waits for the one before.</summary>
    static Task SendStates(ClientState conn, IReadOnlyList<WorldObj> objects)
    {
        var sends = new List<Task>();
        foreach (var member in Members(conn))
        {
            if (!Views.TryGetValue(member, out var view)) continue;
            var (remote, stream, ciphers) = member.Wire;
            foreach (var obj in objects)
            {
                lock (view.Presented)
                    if (!view.Presented.Contains(obj.Id)) continue;
                if (Describe(obj) is not var (quest, components, _) || quest.StateMachine is null) continue;
                int smIndex = components.ToList().IndexOf("StateMachine");
                if (smIndex < 0) continue;
                sends.Add(SendWorldRecord(remote, stream, ciphers, member, obj.Id,
                    InstanceEntities.StateMachineUpdate(obj.Id, (byte)smIndex, quest.StateMachine), null));
            }
        }
        return Task.WhenAll(sends);
    }

    static Task BroadcastRules(ClientState conn, byte[] message, string? log = null) =>
        Task.WhenAll(Members(conn).Select(member =>
        {
            var (remote, stream, ciphers) = member.Wire;
            // A copy each: the wire encrypts a packet in place, so the second player would get noise.
            return Send(remote, stream, ciphers, [.. message], log);
        }));

    /// <summary>Provides <c>in_crucible_zone</c> and channeling weapon visual/animation stats on <see cref="GameEvents.Stats"/>.</summary>
    public static void OnStats(StatSheet sheet)
    {
        var conn = sheet.Conn;
        if (!HasForge(conn, out _) || !Views.TryGetValue(conn, out var view) || !view.InZone) return;

        sheet.Add(CrucibleTables.InZoneStat, 1);
        if (view.VisualHash != 0)
            sheet.Add(CrucibleTables.ChannelWeaponVisualStat, view.VisualHash);
        sheet.Add(CrucibleTables.ChannelWeaponAnimStat, view.AnimSet);
    }

    /// <summary>Grants <c>ActiveSkills 1815</c> (<c>crucible_channel</c>) while in an area with an active Crucible Forge.</summary>
    public static IReadOnlyList<GrantedSkill> GrantedSkills(ClientState conn)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase is Phase.Completed) return [];
        var skill = SkillsFor(conn).Find(CrucibleChannelSkillId, "CrucibleChannel", 1);
        return skill is not null ? [skill] : [];
    }

    /// <summary>Binds <c>crucible_channel</c> onto the first open skill-bar slot when granted. Ours: the real game
    /// channels by click-and-hold in the popup, which the 3.29 client's empty popup cannot start.</summary>
    public static void OnSkillBarBuilding(ClientState conn, ushort[] ids)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase is Phase.Completed) return;
        if (Array.IndexOf(ids, CrucibleChannelSkillId) >= 0) return;
        for (int i = 3; i < ids.Length; i++)
        {
            if (ids[i] == 0) { ids[i] = CrucibleChannelSkillId; return; }
        }
    }

    // ---- the player's target -------------------------------------------------------------------------------------------

    /// <summary>The pick of a changed client's Forge popup, as the action of ruleset 0x89 (little-endian). The 3.21
    /// client sent two: u8 0, u32 item id, u32 inventory id for the item (both 0 for Back: no item, so nothing changes),
    /// and u8 1, u8 column, u8 row for a skill of the item sent last.</summary>
    static (uint InventoryId, uint ItemId, int Column, int Row)? ReadSelection(ReadOnlySpan<byte> payload, PlayerView view) =>
        payload.Length switch
        {
            9 when payload[0] == 0 =>
                (BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]), BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]), -1, 0),
            3 when payload[0] == 1 && view.TargetItemId != 0 =>
                (PlayerInventories.InventoryId(view.TargetRow), view.TargetItemId, payload[1], payload[2]),
            _ => null,
        };

    /// <summary>Handles C→S <c>0x02ca</c> for <c>CrucibleRules</c>.</summary>
    public static async Task<bool> HandleRulesetAction(ClientState conn, byte[] bytes)
    {
        var (rules, payload) = InstanceRulesets.ReadAction(bytes);
        if (rules != InstanceCrucible.CrucibleRulesetRow) return false;
        if (Views.TryGetValue(conn, out var view) && ReadSelection(payload, view) is var (inventory, item, column, row)
            && PlayerInventories.Row(inventory) is byte itemRow)
            await SelectTarget(conn, itemRow, item, column, row);
        return true;
    }

    static bool Eligible(ClientState conn, byte itemRow, uint itemId, out InventoryItem found, out ItemCatalog.BaseType type)
    {
        var catalog = ItemsFor(conn);
        found = ItemsOf(conn).Items(itemRow).FirstOrDefault(i => i.Id == itemId)!;
        type = found is null ? null! : catalog.ByHash(found.Item.TypeHash)!;
        // The popup marks an item whose last bar is full "Max" (ClientStrings 6802).
        return type is not null && CrucibleTreeEngine.CanChannelInForge(found!.Item, type, catalog.Mods)
            && !(found.Item.WeaponPassiveTree is { HasTree: true } tree
                && tree.Experience >= (uint)CrucibleTables.Current.BalanceFor((int)Math.Max(1, tree.BalanceItemLevel)).Bar5);
    }

    /// <summary>
    /// Makes an equipped item (ClientStrings 6749: "Select an equipped item:"; both weapon sets) this player's target at
    /// the Forge, and <c>(column, row)</c> the skill its experience goes towards. A negative column, or an item without
    /// a revealed tree, means "unlock the tree".
    /// </summary>
    public static async Task<bool> SelectTarget(ClientState conn, byte itemRow, uint itemId, int column, int row)
    {
        if (!HasForge(conn, out var encounter) || encounter.Phase is not (Phase.Idle or Phase.Channeling)) return false;
        if (!Views.TryGetValue(conn, out var view) || !view.InZone) return false;
        // The helmet only passes CanChannelInForge as the unique that may have a tree (stat 18439).
        if (itemRow is not (PlayerInventories.Weapon1 or PlayerInventories.Offhand1 or PlayerInventories.Weapon2
            or PlayerInventories.Offhand2 or PlayerInventories.Helmet)
            || !Eligible(conn, itemRow, itemId, out var found, out var type))
            return false;

        var catalog = ItemsFor(conn);
        var item = found.Item;
        if (column < 0 || item.WeaponPassiveTree is not { HasTree: true } tree)
            column = -1;
        else if (CrucibleTreeEngine.SelectOrAllocateNode(tree, column, row) is not { } chosen)
        {
            // A skill that cannot be chosen changes nothing on the target the player has.
            if (view.TargetItemId == itemId && view.TargetRow == itemRow) return false;
            column = -1;
        }
        // The same pick again (the popup repeats it) is no change to save.
        else if (chosen.Equals(tree))
        {
            if (view.TargetItemId == itemId && view.TargetRow == itemRow) return true;
        }
        else if (ItemsOf(conn).Change(itemRow, itemId, i => i with { WeaponPassiveTree = chosen }) is { } updated)
        {
            item = updated.Item;
            var (remote, stream, ciphers) = conn.Wire;
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(itemRow), ItemsOf(conn).NextRevision(itemRow), [itemId], [updated]),
                $"0x00bf inventory {PlayerInventories.InventoryId(itemRow)}: Crucible skill ({column},{row}) chosen on {type.Name}");
        }

        view.TargetRow = itemRow;
        view.TargetItemId = itemId;
        view.VisualHash = type.VisualIdentity;
        view.AnimSet = CrucibleTreeEngine.CategoryOf(item, type, catalog.Mods) switch
        {
            CrucibleTreeEngine.WeaponCategory.TwoHand => TwoHandAnimSet,
            CrucibleTreeEngine.WeaponCategory.Shield => ShieldAnimSet,
            _ => OneHandAnimSet,
        };
        view.ThemeTag = ThemeTagOf(item, catalog);
        await WorldOf(conn).RefreshStats("Crucible Forge target");
        Console.WriteLine($"[crucible] {CharacterName(conn)} target row={itemRow} item={itemId} ({type.Name}) node=" +
            (column < 0 ? "none (the tree's default)" : $"({column},{row})"));
        return true;
    }

    /// <summary>The player's target if it still stands, else the server's default: the first eligible of the main
    /// hand, the off hand and the second set's two (our own order), with no skill chosen.</summary>
    static async Task<bool> EnsureTarget(ClientState conn, PlayerView view)
    {
        if (view.TargetItemId != 0 && Eligible(conn, view.TargetRow, view.TargetItemId, out _, out _)) return true;
        view.TargetItemId = 0;
        view.VisualHash = 0;
        foreach (byte itemRow in (ReadOnlySpan<byte>)[PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Weapon2, PlayerInventories.Offhand2])
            foreach (var item in ItemsOf(conn).Items(itemRow))
                if (Eligible(conn, itemRow, item.Id, out _, out _))
                    return await SelectTarget(conn, itemRow, item.Id, -1, 0);
        return false;
    }

    /// <summary>The <c>CrucibleTags</c> row most of the tree's skills carry ("light correlation" with the item, GGG
    /// Q&amp;A), -1 without a tree.</summary>
    static int ThemeTagOf(Item item, ItemCatalog catalog)
    {
        if (item.WeaponPassiveTree is not { HasTree: true } tree) return -1;
        Span<int> counts = stackalloc int[CrucibleMonsters.Pools.Count];
        int best = -1;
        foreach (var node in tree.Nodes)
            if (catalog.Mods.ByHash(node.ModHash) is { } mod && CrucibleTables.Current.SkillByModRow.TryGetValue(mod.Row, out var skill))
                foreach (int tag in skill.CrucibleTagRows)
                    if ((uint)tag < (uint)counts.Length && ++counts[tag] > (best < 0 ? 0 : counts[best])) best = tag;
        return best;
    }

    // ---- channelling ---------------------------------------------------------------------------------------------------

    /// <summary>Handles C→S skill uses of <c>crucible_channel</c> (<c>0x1f29</c>): the start of a channel, or one more
    /// frame of the popup's click-and-hold.</summary>
    public static async Task<bool> HandleChannelSkill(ClientState conn, ushort skillId, ushort sequence, ushort flags)
    {
        if (skillId != CrucibleChannelSkillId)
        {
            // Another action ends the client's channel, so it is a release here too; the game then gets the action.
            await HandleChannelEnd(conn);
            return false;
        }
        if (!HasForge(conn, out var encounter) || encounter.Device is not { } device) return true;
        var view = Views.GetOrCreateValue(conn);
        if (view.Holding)
        {
            view.Stream = true;
            view.LastUseMs = Environment.TickCount64;
            return true;
        }

        var (remote, stream, ciphers) = conn.Wire;
        bool began = false, started = false;
        // Once the Forge is released a use does nothing more: a click-and-hold's last frame can come after its release.
        if (encounter.Phase is Phase.Idle or Phase.Channeling && view.InZone && await EnsureTarget(conn, view))
        {
            await encounter.Gate.WaitAsync();
            try
            {
                if (encounter.Phase == Phase.Idle)
                {
                    encounter.Phase = Phase.Channeling;
                    encounter.Theme = CrucibleMonsters.ThemeFor(view.ThemeTag, Random.Shared);
                    device.States["channelling"] = 1;
                    began = true;
                }
                if (encounter.Phase == Phase.Channeling)
                {
                    lock (encounter.Channellers) encounter.Channellers.Add(conn);
                    (view.Holding, view.Stream, view.Sequence, view.LastUseMs) = (true, false, sequence, Environment.TickCount64);
                    started = true;
                }
            }
            finally { encounter.Gate.Release(); }
        }
        if (!started)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
            return true;
        }

        // The client stands to channel. The walk the server still runs for it ends here, as for any skill used in
        // place, and nothing held in lockstep (a Move, an attack, an object click) starts again: else the server's
        // player walks on alone, out of the ring, or attacks under the channel.
        (int X, int Y) pos;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            pos = conn.Walk.Position(now);
            conn.Walk.Order(pos.X, pos.Y, now);
            Interlocked.Increment(ref conn.ActionCount);
            conn.LockstepHold.Clear();
            conn.LockstepSkill.Clear(finishChannel: true);
            conn.LockstepUse = null;
        }
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
            InstanceProtocol.ActionStarted(PlayerId, pos.X, pos.Y, device.X, device.Y, skillId, sequence, flags),
            "0x01ab CrucibleChannel start");
        await Send(remote, stream, ciphers, InstanceCrucible.ForgeLockWeapon(), "0x02c9 CrucibleRules lockWeapon");
        if (began)
        {
            await SendStates(conn, [device]);
            await BroadcastRules(conn, InstanceCrucible.ForgeChannelling(true), "0x02c9 CrucibleRules channelling=1");
            await BroadcastRules(conn, InstanceCrucible.ForgeState(1), "0x02c9 CrucibleRules state=1");
            await BroadcastRules(conn, InstanceCrucible.ForgeCharge(0));
        }
        return true;
    }

    public static bool IsHolding(ClientState conn) =>
        HasForge(conn, out _) && Views.TryGetValue(conn, out var view) && view.Holding;

    /// <summary>Handles C→S <c>0x0199</c> (the skill bar key was let go).</summary>
    public static async Task<bool> HandleChannelEnd(ClientState conn)
    {
        if (!HasForge(conn, out var encounter) || !Views.TryGetValue(conn, out var view) || !view.Holding) return false;
        await Stop(conn, view, encounter);
        return true;
    }

    /// <summary>A channeller lets go: the others channel on, and the last one's release starts the wave. Ours: GGG only
    /// says that a channeller leaving the ring starts it and that a second of channelling is enough to take part.</summary>
    static async Task Stop(ClientState conn, PlayerView view, EncounterState encounter)
    {
        // Under the gate: two who let go at once must not each take the other for still holding.
        bool last;
        await encounter.Gate.WaitAsync();
        try
        {
            if (!view.Holding) return;
            last = !Members(conn).Any(m => !ReferenceEquals(m, conn) && Views.TryGetValue(m, out var other) && other.Holding);
            if (!last) view.Holding = false;
        }
        finally { encounter.Gate.Release(); }
        if (last) await Release(conn, encounter);
        else await EndChannel(conn, view);
    }

    static async Task EndChannel(ClientState conn, PlayerView view)
    {
        var (remote, stream, ciphers) = conn.Wire;
        if (conn.LockstepClient && view.Sequence != 0)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ChannelFinished(PlayerId, view.Sequence), $"0x01ae CrucibleChannel end (seq 0x{view.Sequence:x4})");
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), "0x01ac CrucibleChannel end");
    }

    /// <summary>Waits for sends to several players: one player's dead connection does not stop the others'.</summary>
    static async Task Settle(Task sends)
    {
        try { await sends; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>Proximity, the channel's charge, the breakout and the wave's end (<see cref="GameEvents.Tick"/>). It runs
    /// for every player of the league: without a Forge, or far from an idle one, it only compares.</summary>
    public static Task Tick(ClientState conn, double dt)
    {
        if (conn.Instance is null || !Instances.TryGetValue(conn.Instance, out var encounter)
            || encounter.Device is not { } device || encounter.Phase == Phase.Completed)
            return Task.CompletedTask;
        (int X, int Y) pos;
        lock (conn.Monsters) pos = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        long dx = pos.X - device.X, dy = pos.Y - device.Y;
        double distanceSquared = dx * dx + dy * dy;
        if (encounter.Phase == Phase.Idle && encounter.InZone == 0 && distanceSquared > ZoneEnterRadius * ZoneEnterRadius)
            return Task.CompletedTask;
        return TickNear(conn, encounter, device, distanceSquared, dt);
    }

    static async Task TickNear(ClientState conn, EncounterState encounter, WorldObj device, double distanceSquared, double dt)
    {
        var view = Views.GetOrCreateValue(conn);
        var (remote, stream, ciphers) = conn.Wire;
        // Who dies gets no experience, also when resurrected before the last kill. Ours: GGG only says "if you
        // complete the encounter" (Q&A).
        if (conn.PlayerLife == 0)
            lock (encounter.Channellers) encounter.Channellers.Remove(conn);

        if (encounter.Phase is Phase.Idle or Phase.Channeling)
        {
            if (!view.InZone && distanceSquared <= ZoneEnterRadius * ZoneEnterRadius)
            {
                view.InZone = true;
                if (Interlocked.Increment(ref encounter.InZone) == 1 && encounter.Phase == Phase.Idle)
                {
                    device.States["in_zone"] = 1;
                    await SendStates(conn, [device]);
                }
                await Send(remote, stream, ciphers, InstanceCrucible.ForgeEnterZone(), "0x02c9 CrucibleRules inZone=1");
                // Who walks into a channel missed its start.
                if (encounter.Phase == Phase.Channeling)
                {
                    await Send(remote, stream, ciphers, InstanceCrucible.ForgeChannelling(true), "0x02c9 CrucibleRules channelling=1");
                    await Send(remote, stream, ciphers, InstanceCrucible.ForgeState(1), "0x02c9 CrucibleRules state=1");
                    await Send(remote, stream, ciphers, InstanceCrucible.ForgeCharge(encounter.Charge), null);
                }
                await EnsureTarget(conn, view);
                await WorldOf(conn).RefreshStats("entered Crucible Forge zone");
            }
            else if (view.InZone && distanceSquared > ZoneLeaveRadius * ZoneLeaveRadius)
            {
                // "Once anyone who has channelled steps outside of the molten ring" the wave starts (GGG Q&A).
                bool channelled;
                lock (encounter.Channellers) channelled = encounter.Channellers.Contains(conn);
                if (channelled) await Release(conn, encounter);
                else
                {
                    view.InZone = false;
                    if (Interlocked.Decrement(ref encounter.InZone) == 0 && encounter.Phase == Phase.Idle)
                    {
                        device.States["in_zone"] = 0;
                        await SendStates(conn, [device]);
                    }
                    await Send(remote, stream, ciphers, InstanceCrucible.ForgeLeaveZone(), "0x02c9 CrucibleRules inZone=0");
                    await WorldOf(conn).RefreshStats("left Crucible Forge zone");
                }
            }
        }

        if (encounter.Phase == Phase.Channeling)
        {
            bool release = false, stop = false;
            float charge = -1;
            var sends = new List<Task>();
            // Who only watches does not queue behind a channeller's step: the next tick shows the new monsters frozen.
            if (view.Holding) await encounter.Gate.WaitAsync();
            else if (!encounter.Gate.Wait(0)) return;
            try
            {
                if (encounter.Phase != Phase.Channeling) return;
                if (view.Holding)
                {
                    // The end of a click-and-hold, or a channeller who died.
                    if (view.Stream && Environment.TickCount64 - view.LastUseMs >= StreamEndMs) stop = true;
                    else if (conn.PlayerLife == 0) release = true;
                    else
                    {
                        charge = encounter.Charge = Math.Min(1f, encounter.Charge + (float)(dt / FullChargeSeconds));
                        while (encounter.Steps < ChargeSteps && encounter.Steps <= (int)(charge * ChargeSteps))
                            await Grow(conn, encounter, device, sends);
                        // The client's action ends itself at 1.0 (0x141341390).
                        release = charge >= 1f;
                    }
                }
                ShowFrozen(conn, view, encounter, sends);
            }
            finally { encounter.Gate.Release(); }

            // Sent after the gate: a player's stalled connection must not hold it.
            await Settle(Task.WhenAll(sends));
            if (charge >= 0) await Settle(BroadcastRules(conn, InstanceCrucible.ForgeCharge(charge)));
            if (stop) await Stop(conn, view, encounter);
            else if (release) await Release(conn, encounter);
        }
        else if (encounter.Phase == Phase.Spawning)
        {
            if (Environment.TickCount64 >= encounter.BreakoutAtMs) await Breakout(conn, encounter);
        }
        else if (encounter.Phase == Phase.Active)
        {
            // A kill OnKill did not see (a corpse consumed, a monster gone with its runner).
            bool allDead;
            lock (conn.Monsters)
            {
                foreach (uint id in encounter.Monsters.ToArray())
                {
                    var body = conn.Monsters.GetValueOrDefault(id) ?? encounter.Bodies.GetValueOrDefault(id);
                    if (body is { Dead: false, Life: > 0 } && conn.Monsters.ContainsKey(id)) continue;
                    if (body is not null && (body.Dead || body.Life == 0)) encounter.KilledExperience += body.Variety?.ExperienceMultiplier ?? 0;
                    encounter.Monsters.Remove(id);
                    encounter.Bodies.Remove(id);
                }
                allDead = encounter.Monsters.Count == 0;
            }
            if (allDead) await Complete(conn, encounter);
        }
    }

    /// <summary>One more step of the channel: a fissure grows outward from the Forge, a pool opens at its end and a
    /// molten monster forms there, held until the release (GGG's reveal video, 1:04). Under the gate: what goes to
    /// the players is collected in <paramref name="sends"/> for the caller to wait for after it.</summary>
    static async Task Grow(ClientState conn, EncounterState encounter, WorldObj device, List<Task> sends)
    {
        int step = encounter.Steps++;
        // The arms take turns (3 and the arm count share no factor).
        int arm = step * 3 % FissureArms, ring = step / FissureArms;
        double angle = arm * (2 * Math.PI / FissureArms);
        (int X, int Y) At(double radius) =>
            (device.X + (int)Math.Round(Math.Cos(angle) * radius), device.Y + (int)Math.Round(Math.Sin(angle) * radius));
        double start = FissureStart + ring * FissureStep;

        // fissure.ot: 1 plays "start" at three times its speed, 2 the channelling loop, 3 "end".
        var grown = encounter.Fissures.Where(f => f.States["crucible_fissure"] == 1).ToList();
        foreach (var f in grown) f.States["crucible_fissure"] = 2;
        var created = new List<WorldObj>(FissurePieces + 1);
        for (int piece = 0; piece < FissurePieces && ring == 0; piece++)
        {
            var at = At(start + piece * FissureStep / FissurePieces);
            created.Add(new WorldObj
            {
                Id = conn.Instance.World.NewMonsterId(),
                Type = CrucibleTables.FissureType,
                X = at.X,
                Y = at.Y,
                // The model lies across its facing (seen live).
                Rotation = (float)(angle + Math.PI / 2),
                States = new() { ["crucible_fissure"] = 1, ["channelling"] = 1 },
            });
        }
        encounter.Fissures.AddRange(created);
        var end = conn.Layout.Near(At(start + FissureStep));
        var pool = new WorldObj
        {
            Id = conn.Instance.World.NewMonsterId(),
            Type = CrucibleTables.MoltenPoolType,
            X = end.X,
            Y = end.Y,
            States = new() { ["crucible_pool"] = 1 },
        };
        encounter.Pools.Add(pool);
        created.Add(pool);
        encounter.AllObjects.AddRange(created);
        sends.AddRange(Members(conn).SelectMany(member => created.Select(obj => Present(member, obj))));
        sends.Add(SendStates(conn, grown));

        int tier = 1 + step * TopSpawnTier / ChargeSteps;
        await Hold(conn, encounter, pool, tier, sends);
        // "As you spawn additional monsters, they combine together to create larger and more fearsome foes."
        for (; tier < MaxTier; tier++)
        {
            var same = encounter.Held.Where(h => h.Tier == tier).Take(MergeCount).ToList();
            if (same.Count < MergeCount) break;
            foreach (var merged in same)
            {
                encounter.Held.Remove(merged);
                Remove(conn, merged.Id, sends);
                if (!ReferenceEquals(merged.Pool, same[^1].Pool)) merged.Pool.States["crucible_pool"] = 2;
            }
            sends.Add(SendStates(conn, [.. same.Take(MergeCount - 1).Select(h => h.Pool)]));
            await Hold(conn, encounter, same[^1].Pool, tier + 1, sends);
        }
    }

    /// <summary>Creates a monster of the theme's tier at a pool and holds it: out of the AI and taking no hits.</summary>
    static async Task Hold(ClientState conn, EncounterState encounter, WorldObj pool, int tier, List<Task> sends)
    {
        if (encounter.Theme is not { } theme || CrucibleMonsters.VarietyForTier(theme, tier) is not { } variety) return;
        // Higher tiers are more often magic or rare ("internal tiering system", GGG Q&A); the two bosses are unique.
        int roll = Random.Shared.Next(100);
        var rarity = tier >= BossTier ? MonsterRarity.Unique
            : roll < tier * RareChancePerTier ? MonsterRarity.Rare
            : roll < tier * (RareChancePerTier + MagicChancePerTier) ? MonsterRarity.Magic : MonsterRarity.Normal;
        // Its damage is fixed when it is created, so it has the charge it formed at.
        int percent = (int)(encounter.Charge * 100);
        var scaling = CrucibleMonsters.ScalingMods(encounter.AreaLevel,
            percent * FullChargeMoreDamage / 100, -percent * FullChargeLessDamageTaken / 100);
        var (remote, stream, ciphers) = conn.Wire;
        SpawnedMonster? body = null;
        // Held from birth, under the lock that adds it: the AI never has it free (one held a moment later swung, live).
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)encounter.AreaLevel, rarity,
            pool.X, pool.Y, unrisen: false, alone: false, extraMods: scaling, owned: (_, born) =>
            {
                (born.BusyUntil, born.Aggro, born.EntranceGrace) = (double.PositiveInfinity, false, true);
                body = born;
            });
        if (id == 0 || body is null) return;
        lock (conn.Monsters) encounter.Held.Add(new(id, tier, pool, body));
        foreach (var member in Members(conn))
            if (Views.TryGetValue(member, out var view)) ShowFrozen(member, view, encounter, sends);
    }

    /// <summary>The <c>crucible_frozen</c> buff, with its molten art by rarity, on every held monster this client has
    /// and does not show frozen yet. Under the gate.</summary>
    static void ShowFrozen(ClientState conn, PlayerView view, EncounterState encounter, List<Task> sends)
    {
        if (CrucibleTables.Current.FrozenBuff is not ushort frozen) return;
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var held in encounter.Held)
        {
            if (view.Frozen.ContainsKey(held.Id)) continue;
            ushort buff;
            lock (conn.Monsters)
            {
                if (!conn.MonsterViews.ContainsKey(held.Id)) continue;
                buff = conn.NextBuff++;
            }
            view.Frozen[held.Id] = buff;
            sends.Add(SendWorldRecord(remote, stream, ciphers, conn, held.Id,
                InstanceEntities.AddBuff(held.Id, buff, frozen, visual: CrucibleTables.Current.StasisVisuals[(int)held.Body.Rarity]),
                $"0x01c1 monster 0x{held.Id:x} crucible_frozen (tier {held.Tier})"));
        }
    }

    /// <summary>Takes a held monster out of the area without a death: it merged into a bigger one.</summary>
    static void Remove(ClientState conn, uint id, List<Task> sends)
    {
        lock (conn.Monsters)
        {
            if (conn.Monsters.Remove(id, out var body)) { body.Dead = true; body.Life = 0; ++body.ActionGeneration; }
            conn.Instance.World.MonsterOrigins.TryRemove(id, out _);
        }
        foreach (var member in Members(conn))
        {
            bool shown;
            lock (member.Monsters) { shown = member.MonsterViews.Remove(id); member.AsleepMonsters.Remove(id); }
            if (Views.TryGetValue(member, out var view)) view.Frozen.Remove(id);
            var (remote, stream, ciphers) = member.Wire;
            if (shown) sends.Add(SendWorldRemoval(remote, stream, ciphers, member, id, $"[crucible] held monster 0x{id:x} merged"));
        }
    }

    /// <summary>A release, a channeller leaving the ring, or full charge: the channel ends for everyone and the
    /// monsters break out after <see cref="BreakoutDelayMs"/>.</summary>
    static async Task Release(ClientState conn, EncounterState encounter)
    {
        if (encounter.Device is not { } device) return;
        var sends = new List<Task>();
        var ended = new List<(ClientState Member, PlayerView View, bool Held, bool InZone)>();
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Channeling) return;
            // The shortest channel still forms its first monster.
            if (encounter.Steps == 0) await Grow(conn, encounter, device, sends);
            encounter.Phase = Phase.Spawning;
            encounter.BreakoutAtMs = Environment.TickCount64 + BreakoutDelayMs;
            encounter.InZone = 0;
            device.States["channelling"] = 0;
            device.States["activated"] = 1;
            foreach (var f in encounter.Fissures) { f.States["channelling"] = 0; f.States["crucible_fissure"] = 3; }
            // Everyone's state first: a send that fails must leave nobody holding.
            foreach (var member in Members(conn))
                if (Views.TryGetValue(member, out var view) && (view.Holding || view.InZone))
                {
                    ended.Add((member, view, view.Holding, view.InZone));
                    (view.Holding, view.InZone) = (false, false);
                }
        }
        finally { encounter.Gate.Release(); }

        sends.AddRange(ended.Select(async end =>
        {
            if (end.Held) await EndChannel(end.Member, end.View);
            if (!end.InZone) return;
            // The popup goes as the fight starts, and with in_crucible_zone the channel skill.
            var (remote, stream, ciphers) = end.Member.Wire;
            await Send(remote, stream, ciphers, InstanceCrucible.ForgeLeaveZone(), "0x02c9 CrucibleRules inZone=0");
            await WorldOf(end.Member).RefreshStats("Crucible Forge released");
        }));
        await Settle(Task.WhenAll(sends));
        await Settle(SendStates(conn, [device, .. encounter.Fissures]));
        await Settle(BroadcastRules(conn, InstanceCrucible.ForgeChannelling(false), "0x02c9 CrucibleRules channelling=0"));
        await Settle(BroadcastRules(conn, InstanceCrucible.ForgeState(2), "0x02c9 CrucibleRules state=2 (combat)"));

        Console.WriteLine($"[crucible] {CharacterName(conn)} released Forge at charge {encounter.Charge:0.00} (theme={encounter.Theme?.Tag}): " +
            $"{encounter.Held.Count} monsters, tiers {string.Join(",", encounter.Held.Select(h => h.Tier).Order())}");
    }

    /// <summary>The held monsters break out: the buff goes, <c>broken_out = 1</c> puts the crystals of their
    /// <c>crystal_size</c> on them (<c>CrucibleMonsterBase.ot</c>), and they fight.</summary>
    static async Task Breakout(ClientState conn, EncounterState encounter)
    {
        var sends = new List<Task>();
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Spawning) return;
            lock (conn.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                foreach (var held in encounter.Held)
                {
                    // crystals_0..3 are the small to large crystal packs of the theme's base type: ours by tier.
                    held.Body.States = held.Body.States?.Set("crystal_size", Math.Min(3, (held.Tier - 1) / TiersPerCrystalSize)).Set("broken_out", 1);
                    (held.Body.BusyUntil, held.Body.Aggro, held.Body.EntranceGrace) = (now, true, false);
                    encounter.Monsters.Add(held.Id);
                    encounter.Bodies[held.Id] = held.Body;
                }
            }
            foreach (var member in Members(conn))
            {
                if (!Views.TryGetValue(member, out var view)) continue;
                var (remote, stream, ciphers) = member.Wire;
                foreach (var held in encounter.Held)
                {
                    lock (member.Monsters)
                        if (!member.MonsterViews.ContainsKey(held.Id)) continue;
                    var records = new List<byte[]>(2);
                    if (view.Frozen.Remove(held.Id, out ushort buff)) records.Add(InstanceEntities.RemoveBuff(held.Id, buff));
                    int machine = held.Body.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") ?? -1;
                    if (machine >= 0 && held.Body.States is { } states)
                        records.Add(InstanceEntities.StateMachineUpdate(held.Id, (byte)machine, states));
                    sends.Add(SendWorldRecords(remote, stream, ciphers, member, held.Id, () => [.. records]));
                }
            }
            encounter.Held.Clear();
            foreach (var pool in encounter.Pools) pool.States["crucible_pool"] = 2;
            encounter.Phase = Phase.Active;
        }
        finally { encounter.Gate.Release(); }
        await Task.WhenAll(sends);
        await SendStates(conn, encounter.Pools);
    }

    /// <summary>Tracks encounter monster kills synchronously inside <see cref="GameEvents.Kill"/>.</summary>
    public static void OnKill(KillContext kill)
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
            // "Larger and more fearsome foes that grant even more experience" (GGG's reveal video, 1:13).
            encounter.KilledExperience += kill.Monster.Variety?.ExperienceMultiplier ?? 0;
            finished = encounter.Monsters.Count == 0;
        }
        if (finished)
            kill.Later(() => Complete(kill.Conn, encounter));
    }

    /// <summary>A channeller who leaves the area gets no experience; one who was channelling ends the channel for those
    /// who stay.</summary>
    public static async Task OnPlayerLeaving(ClientState conn)
    {
        if (!Views.TryGetValue(conn, out var view)) return;
        Views.Remove(conn);
        if (conn.Instance is null || !Instances.TryGetValue(conn.Instance, out var encounter)) return;
        lock (encounter.Channellers) encounter.Channellers.Remove(conn);
        if (encounter.Phase is not (Phase.Idle or Phase.Channeling)) return;
        if (view.InZone) Interlocked.Decrement(ref encounter.InZone);
        // Released by one who stays: the one leaving can no longer form the channel's first monster.
        if (view.Holding && Members(conn).FirstOrDefault(m => !ReferenceEquals(m, conn)) is { } other) await Release(other, encounter);
    }

    /// <summary>Every monster is dead: each channeller's target item gets the experience ("only on completion", GGG
    /// Q&amp;A), and the tree engine reveals the tree and allocates what the new total reaches.</summary>
    static async Task Complete(ClientState conn, EncounterState encounter)
    {
        ClientState[] channellers;
        uint experience;
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Active) return;
            encounter.Phase = Phase.Completed;
            experience = (uint)Math.Round(CrucibleTables.Current.BalanceFor(encounter.AreaLevel).Bar1
                * (encounter.KilledExperience / XpReferenceSum) * ExperienceMultiplier);
            lock (encounter.Channellers)
            {
                channellers = [.. encounter.Channellers];
                encounter.Channellers.Clear();
            }
            if (encounter.Device is { } device)
            {
                device.States["activated"] = 2;
                device.States["channelling"] = 0;
                device.States["in_zone"] = 0;
            }
        }
        finally { encounter.Gate.Release(); }

        if (encounter.Device is { } forge) await Settle(SendStates(conn, [forge]));
        await Settle(BroadcastRules(conn, InstanceCrucible.ForgeState(3), "0x02c9 CrucibleRules state=3 (complete)"));

        foreach (var channeller in channellers)
        {
            if (channeller.Closed || channeller.LeavingArea || !ReferenceEquals(channeller.Instance, conn.Instance)
                || !Views.TryGetValue(channeller, out var view)) continue;
            var items = ItemsOf(channeller);
            var catalog = ItemsFor(channeller);
            byte itemRow = view.TargetRow;
            uint itemId = view.TargetItemId;
            if (experience == 0 || items.Items(itemRow).FirstOrDefault(i => i.Id == itemId) is not { } found
                || catalog.ByHash(found.Item.TypeHash) is not { } type) continue;

            // On the item as it is now: this does not run on the channeller's own loop.
            bool revealedNow = false;
            int newlyAllocated = 0;
            if (items.Change(itemRow, itemId, current => CrucibleTreeEngine.AwardExperience(current, type, catalog.Mods,
                    experience, Random.Shared, out revealedNow, out newlyAllocated)) is not { } updated) continue;
            var (remote, stream, ciphers) = channeller.Wire;
            await Settle(Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(itemRow), items.NextRevision(itemRow), [itemId], [updated]),
                $"0x00bf inventory {PlayerInventories.InventoryId(itemRow)}: Crucible Forge +{experience} XP on {type.Name}"));
            Console.WriteLine($"[crucible] {CharacterName(channeller)} completed Forge: +{experience} XP on {type.Name} " +
                $"(killed {encounter.KilledExperience}, revealed={revealedNow}, allocated={newlyAllocated})");
        }

        var allItems = ItemsFor(conn);
        if (CrucibleRemnants.RollGeodeDrop(allItems, encounter.AreaLevel, encounter.Charge >= 1f, GeodeDropRate, Random.Shared) is { } geode
            && encounter.Device is { } dev)
        {
            var dropCell = conn.Layout.Near((dev.X + 3, dev.Y + 3));
            var worldItem = new WorldItemObject(dropCell.X, dropCell.Y, geode);
            uint dropId = conn.Instance.Ground.Add(worldItem, conn);
            var wire = conn.Wire;
            await SendGroundItem(wire.Remote, wire.Stream, wire.Ciphers, conn, dropId, worldItem, null, justDropped: true);
        }

        // The channel skill goes with the completed Forge, and the items' new skills count.
        foreach (var member in Members(conn))
            await Settle(WorldOf(member).RefreshStats("Crucible Forge complete"));
    }
}
