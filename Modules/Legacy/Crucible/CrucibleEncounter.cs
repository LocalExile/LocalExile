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
    const double StreamEndSeconds = 0.75;
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
    const double BreakoutDelaySeconds = 1.0;

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
        public HashSet<Player> Channellers = [];
        public List<Held> Held = [];
        public double BreakoutAt;

        // The released monsters still alive, and the experience multipliers of those killed.
        public HashSet<uint> Monsters = [];
        public Dictionary<uint, SpawnedMonster> Bodies = [];
        public int KilledExperience;
    }

    sealed class PlayerView
    {
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
        public double LastUseAt;
    }

    static readonly ConditionalWeakTable<InstanceState, EncounterState> Instances = new();
    static readonly ConditionalWeakTable<Player, PlayerView> Views = new();

    public static void Clear()
    {
        Instances.Clear();
        Views.Clear();
    }

    internal static IEnumerable<Player> Members(Player player) =>
        player.Instance.PlayersExcept(player.Id)
            .Prepend(player)
            .Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, player.Instance)).Distinct();

    internal static bool IsInAreaFrame(Player player) =>
        player.Area.Owned && player.Connection.WorldOutput.Started;

    static async Task SendShared(Player player, Func<Player, Task> send)
    {
        if (IsInAreaFrame(player))
        {
            await send(player);
            return;
        }
        bool sentFramed = false;
        var targets = new List<Player>();
        foreach (var member in Members(player))
        {
            if (IsInAreaFrame(member))
            {
                if (sentFramed) continue;
                sentFramed = true;
            }
            targets.Add(member);
        }
        await Settle(Task.WhenAll(targets.Select(send)));
    }

    internal static Task SendSharedCreate(Player player, uint id, byte[] blob, string? log) =>
        SendShared(player, member => SendWorldCreate(member, id, () => [.. blob], log, wake: true, awaitBirth: false));

    internal static Task SendSharedRecord(Player player, uint id, byte[] record, string? log) =>
        SendShared(player, member => SendWorldRecord(member, id, [.. record], log));

    internal static Task SendSharedRemoval(Player player, uint id, string? log) =>
        SendShared(player, member => SendWorldRemoval(member, id, log));

    /// <summary>True when the player's current area layout placed a <c>CrucibleDevice</c> (not Forge of the Titans).</summary>
    public static bool HasForgePlacement(Player player) =>
        !CrucibleTables.IsForgeArea(player.Layout.Id)
        && player.Layout.Level is { } level
        && level.RoomObjects.Any(o => o.Type.Equals(CrucibleTables.DeviceType, StringComparison.OrdinalIgnoreCase));

    static bool HasForge(Player player, out EncounterState encounter)
    {
        encounter = null!;
        return player.Instance is not null && Instances.TryGetValue(player.Instance, out encounter!) && encounter.Device is not null;
    }

    /// <summary>Spawns the <c>CrucibleDevice</c> once per area on <see cref="GameEvents.AreaGenerated"/>.</summary>
    public static async Task AreaGenerated(Player player)
    {
        if (player.Instance is null || CrucibleTables.IsForgeArea(player.Layout.Id) || player.Layout.Level is not { } level)
            return;

        var devicePlacement = level.RoomObjects.FirstOrDefault(o =>
            o.Type.Equals(CrucibleTables.DeviceType, StringComparison.OrdinalIgnoreCase));
        if (devicePlacement is null) return;

        var encounter = Instances.GetOrCreateValue(player.Instance);
        WorldObj[] objects;
        await encounter.Gate.WaitAsync();
        try
        {
            if (!encounter.Built)
            {
                Build(player, encounter, devicePlacement);
                encounter.Built = true;
            }
            objects = [.. encounter.AllObjects];
        }
        finally { encounter.Gate.Release(); }

        await Task.WhenAll(objects.Select(obj => Present(player, obj)));
    }

    /// <summary>Applies the Forge blocker and syncs phase state for every player entering the area (<see cref="GameEvents.PlayerEntered"/>).</summary>
    public static async Task PlayerEntered(Player player)
    {
        if (!HasForge(player, out var encounter)) return;
        foreach (var obj in encounter.AllObjects)
            ApplyBlocker(player, obj);
        // Who arrives after the release missed the state's one send.
        if (encounter.Phase is Phase.Spawning or Phase.Active or Phase.Completed)
            await Send(player, InstanceCrucible.ForgeState((byte)(encounter.Phase == Phase.Completed ? 3 : 2)),
                "0x02c9 CrucibleRules state for a late arrival");
    }

    static void Build(Player player, EncounterState encounter, GameServer.Data.Terrain.Generation.PlacedTileObject deviceAt)
    {
        int worldLevel = ServerLevels.AreaLevel(player.Layout.Id);
        encounter.AreaLevel = Math.Clamp(MapRunOf(player)?.Area.Level is > 0 ? MapRunOf(player)!.Area.Level : worldLevel, 1, 100);

        var device = new WorldObj
        {
            Id = player.TakeObjectId(),
            Type = CrucibleTables.DeviceType,
            X = deviceAt.X,
            Y = deviceAt.Y,
            Rotation = deviceAt.Rotation,
            Blocked = true,
            States = new() { ["activated"] = 0, ["channelling"] = 0, ["in_zone"] = 0 },
        };
        encounter.Device = device;
        encounter.AllObjects.Add(device);

        Console.WriteLine($"[crucible] In-zone Crucible Forge placed in {player.Layout.Id} (lvl {encounter.AreaLevel}) at ({device.X},{device.Y}), device=0x{device.Id:x}");
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

    static void ApplyBlocker(Player player, WorldObj obj)
    {
        if (!CrucibleTables.Current.Objects.TryGetValue(obj.Type, out var info) || !info.Blocking) return;
        if (!info.HasStationaryCollision)
        {
            player.SetBlocker(obj.Id, obj.X, obj.Y, Math.Max(0, info.Size - 1), null);
        }
        else
        {
            var (size, hull) = StationaryCollision.Resolve(obj.Type);
            if (size >= 0) player.SetBlocker(obj.Id, obj.X, obj.Y, size, hull);
        }
    }

    static async Task Present(Player player, WorldObj obj)
    {
        if (Describe(obj) is not var (quest, _, blocking)) return;
        byte[] blob = InstanceEntities.CreateObject(obj.Id, InstanceEntities.ObjectTypeHash(obj.Type), quest.Blob());
        await SendSharedCreate(player, obj.Id, blob, $"crucible {obj.Type.Split('/')[^1]} 0x{obj.Id:x} at ({obj.X},{obj.Y})");
        if (blocking)
            foreach (var member in Members(player))
                ApplyBlocker(member, obj);
    }

    /// <summary>The objects' states to every player in the area, as one batch: no send waits for the one before.</summary>
    static Task SendStates(Player player, IReadOnlyList<WorldObj> objects)
    {
        var sends = new List<Task>(objects.Count);
        foreach (var obj in objects)
        {
            if (Describe(obj) is not var (quest, components, _) || quest.StateMachine is null) continue;
            int smIndex = components.ToList().IndexOf("StateMachine");
            if (smIndex < 0) continue;
            sends.Add(SendSharedRecord(player, obj.Id,
                InstanceEntities.StateMachineUpdate(obj.Id, (byte)smIndex, quest.StateMachine), null));
        }
        return Task.WhenAll(sends);
    }

    static Task BroadcastRules(Player player, byte[] message, string? log = null) =>
        Task.WhenAll(Members(player).Select(member =>
            // A copy each: the wire encrypts a packet in place, so the second player would get noise.
            Send(member, [.. message], log)));

    /// <summary>Provides <c>in_crucible_zone</c> and channeling weapon visual/animation stats on <see cref="GameEvents.Stats"/>.</summary>
    public static void OnStats(StatSheet sheet)
    {
        var player = sheet.Player;
        if (!HasForge(player, out _) || !Views.TryGetValue(player, out var view) || !view.InZone) return;

        sheet.Add(CrucibleTables.InZoneStat, 1);
        if (view.VisualHash != 0)
            sheet.Add(CrucibleTables.ChannelWeaponVisualStat, view.VisualHash);
        sheet.Add(CrucibleTables.ChannelWeaponAnimStat, view.AnimSet);
    }

    /// <summary>Grants <c>ActiveSkills 1815</c> (<c>crucible_channel</c>) while in the Forge ring before release.</summary>
    public static IReadOnlyList<GrantedSkill> GrantedSkills(Player player)
    {
        if (!HasForge(player, out var encounter) || encounter.Phase is not (Phase.Idle or Phase.Channeling)
            || !Views.TryGetValue(player, out var view) || !view.InZone) return [];
        var skill = SkillsFor(player).Find(CrucibleChannelSkillId, "CrucibleChannel", 1);
        return skill is not null ? [skill] : [];
    }

    /// <summary>Binds <c>crucible_channel</c> onto the first open skill-bar slot while in the Forge ring. Ours: the
    /// real game channels by click-and-hold in the popup, which the 3.29 client's empty popup cannot start.</summary>
    public static void OnSkillBarBuilding(Player player, ushort[] ids)
    {
        if (!HasForge(player, out var encounter) || encounter.Phase is not (Phase.Idle or Phase.Channeling)
            || !Views.TryGetValue(player, out var view) || !view.InZone) return;
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
    public static async Task<bool> HandleRulesetAction(Player player, byte[] bytes)
    {
        var (rules, payload) = InstanceRulesets.ReadAction(bytes);
        if (rules != InstanceCrucible.CrucibleRulesetRow) return false;
        if (Views.TryGetValue(player, out var view) && ReadSelection(payload, view) is var (inventory, item, column, row)
            && PlayerInventories.Row(inventory) is byte itemRow)
            await SelectTarget(player, itemRow, item, column, row);
        return true;
    }

    static bool Eligible(Player player, byte itemRow, uint itemId, out InventoryItem found, out ItemCatalog.BaseType type)
    {
        var catalog = ItemsFor(player);
        found = ItemsOf(player).Items(itemRow).FirstOrDefault(i => i.Id == itemId)!;
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
    public static async Task<bool> SelectTarget(Player player, byte itemRow, uint itemId, int column, int row)
    {
        if (!HasForge(player, out var encounter) || encounter.Phase is not (Phase.Idle or Phase.Channeling)) return false;
        if (!Views.TryGetValue(player, out var view) || !view.InZone) return false;
        // The helmet only passes CanChannelInForge as the unique that may have a tree (stat 18439).
        if (itemRow is not (PlayerInventories.Weapon1 or PlayerInventories.Offhand1 or PlayerInventories.Weapon2
            or PlayerInventories.Offhand2 or PlayerInventories.Helmet)
            || !Eligible(player, itemRow, itemId, out var found, out var type))
            return false;

        var catalog = ItemsFor(player);
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
        else if (ItemsOf(player).Change(itemRow, itemId, i => i with { WeaponPassiveTree = chosen }) is { } updated)
        {
            item = updated.Item;
            await Send(player,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(itemRow), ItemsOf(player).NextRevision(itemRow), [itemId], [updated]),
                $"0x00bf inventory {PlayerInventories.InventoryId(itemRow)}: Crucible skill ({column},{row}) chosen on {type.Name}");
        }

        view.TargetRow = itemRow;
        view.TargetItemId = itemId;
        view.VisualHash = item.VisualIdentity != 0 ? item.VisualIdentity : type.VisualIdentity;
        view.AnimSet = CrucibleTreeEngine.CategoryOf(item, type, catalog.Mods) switch
        {
            CrucibleTreeEngine.WeaponCategory.TwoHand => TwoHandAnimSet,
            CrucibleTreeEngine.WeaponCategory.Shield => ShieldAnimSet,
            _ => OneHandAnimSet,
        };
        view.ThemeTag = ThemeTagOf(item, catalog);
        await WorldOf(player).RefreshStats();
        Console.WriteLine($"[crucible] {CharacterName(player)} target row={itemRow} item={itemId} ({type.Name}) node=" +
            (column < 0 ? "none (the tree's default)" : $"({column},{row})"));
        return true;
    }

    /// <summary>The player's target if it still stands, else the server's default: the first eligible of the main
    /// hand, the off hand, the second set's two and the helmet (our own order), with no skill chosen.</summary>
    static async Task<bool> EnsureTarget(Player player, PlayerView view)
    {
        if (view.TargetItemId != 0 && Eligible(player, view.TargetRow, view.TargetItemId, out _, out _)) return true;
        view.TargetItemId = 0;
        view.VisualHash = 0;
        foreach (byte itemRow in (ReadOnlySpan<byte>)[PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Weapon2, PlayerInventories.Offhand2, PlayerInventories.Helmet])
            foreach (var item in ItemsOf(player).Items(itemRow))
                if (Eligible(player, itemRow, item.Id, out _, out _))
                    return await SelectTarget(player, itemRow, item.Id, -1, 0);
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
    public static async Task<bool> HandleChannelSkill(Player player, ushort skillId, ushort sequence, ushort flags)
    {
        if (skillId != CrucibleChannelSkillId)
        {
            // Another action ends the client's channel, so it is a release here too; the game then gets the action.
            await HandleChannelEnd(player);
            return false;
        }
        if (!HasForge(player, out var encounter) || encounter.Device is not { } device) return true;
        var view = Views.GetOrCreateValue(player);
        double now = AreaTime(player);
        if (view.Holding)
        {
            view.Stream = true;
            view.LastUseAt = now;
            return true;
        }

        bool began = false, started = false;
        // Once the Forge is released a use does nothing more: a click-and-hold's last frame can come after its release.
        if (encounter.Phase is Phase.Idle or Phase.Channeling && view.InZone && await EnsureTarget(player, view))
        {
            await encounter.Gate.WaitAsync();
            try
            {
                if (encounter.Phase == Phase.Idle)
                {
                    encounter.Phase = Phase.Channeling;
                    encounter.Theme = CrucibleMonsters.ThemeFor(view.ThemeTag, AreaRng(player));
                    device.States["channelling"] = 1;
                    began = true;
                }
                if (encounter.Phase == Phase.Channeling)
                {
                    lock (encounter.Channellers) encounter.Channellers.Add(player);
                    (view.Holding, view.Stream, view.Sequence, view.LastUseAt) = (true, false, sequence, AreaTime(player));
                    started = true;
                }
            }
            finally { encounter.Gate.Release(); }
        }
        if (!started)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }

        // The client stands to channel. The walk the server still runs for it ends here, as for any skill used in
        // place, and nothing held in lockstep (a Move, an attack, an object click) starts again: else the server's
        // player walks on alone, out of the ring, or attacks under the channel.
        now = AreaTime(player);
        var pos = player.Walk.Position(now);
        player.Walk.Order(pos.X, pos.Y, now);
        Interlocked.Increment(ref player.ActionCount);
        player.LockstepHold.Clear();
        player.LockstepSkill.Clear(finishChannel: true);
        player.LockstepUse = null;

        await SendWorldRecord(player, player.Id,
            InstanceProtocol.ActionStarted(player.Id, pos.X, pos.Y, device.X, device.Y, skillId, sequence, flags),
            "0x01ab CrucibleChannel start");
        await Send(player, InstanceCrucible.ForgeLockWeapon(), "0x02c9 CrucibleRules lockWeapon");
        if (began)
        {
            await SendStates(player, [device]);
            await BroadcastRules(player, InstanceCrucible.ForgeChannelling(true), "0x02c9 CrucibleRules channelling=1");
            await BroadcastRules(player, InstanceCrucible.ForgeState(1), "0x02c9 CrucibleRules state=1");
            await BroadcastRules(player, InstanceCrucible.ForgeCharge(0));
        }
        return true;
    }

    public static bool IsHolding(Player player) =>
        HasForge(player, out _) && Views.TryGetValue(player, out var view) && view.Holding;

    /// <summary>Handles C→S <c>0x0199</c> (the skill bar key was let go).</summary>
    public static async Task<bool> HandleChannelEnd(Player player)
    {
        if (!HasForge(player, out var encounter) || !Views.TryGetValue(player, out var view) || !view.Holding) return false;
        await Stop(player, view, encounter);
        return true;
    }

    /// <summary>A channeller lets go: the others channel on, and the last one's release starts the wave. Ours: GGG only
    /// says that a channeller leaving the ring starts it and that a second of channelling is enough to take part.</summary>
    static async Task Stop(Player player, PlayerView view, EncounterState encounter)
    {
        // Under the gate: two who let go at once must not each take the other for still holding.
        bool last;
        await encounter.Gate.WaitAsync();
        try
        {
            if (!view.Holding) return;
            last = !Members(player).Any(m => !ReferenceEquals(m, player) && Views.TryGetValue(m, out var other) && other.Holding);
            if (!last) view.Holding = false;
        }
        finally { encounter.Gate.Release(); }
        if (last) await Release(player, encounter);
        else await EndChannel(player, view);
    }

    static async Task EndChannel(Player player, PlayerView view)
    {
        if (player.Connection.LockstepClient && view.Sequence != 0)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ChannelFinished(player.Id, view.Sequence), $"0x01ae CrucibleChannel end (seq 0x{view.Sequence:x4})");
        await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), "0x01ac CrucibleChannel end");
    }

    /// <summary>Waits for sends to several players: one player's dead connection does not stop the others'.</summary>
    static async Task Settle(Task sends)
    {
        try { await sends; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>Proximity, the channel's charge, the breakout and the wave's end (<see cref="GameEvents.AreaTick"/>).
    /// Runs only in areas with a Crucible Forge; far from an idle one, it only compares.</summary>
    public static Task Tick(Player player, double dt)
    {
        if (player.Instance is null || !Instances.TryGetValue(player.Instance, out var encounter)
            || encounter.Device is not { } device || encounter.Phase == Phase.Completed)
            return Task.CompletedTask;
        var pos = player.Walk.Position(AreaTime(player));
        long dx = pos.X - device.X, dy = pos.Y - device.Y;
        double distanceSquared = dx * dx + dy * dy;
        if (encounter.Phase == Phase.Idle && encounter.InZone == 0 && distanceSquared > ZoneEnterRadius * ZoneEnterRadius)
            return Task.CompletedTask;
        return TickNear(player, encounter, device, distanceSquared, dt);
    }

    static async Task TickNear(Player player, EncounterState encounter, WorldObj device, double distanceSquared, double dt)
    {
        var view = Views.GetOrCreateValue(player);
        double now = AreaTime(player);
        // Who dies gets no experience, also when resurrected before the last kill. Ours: GGG only says "if you
        // complete the encounter" (Q&A).
        if (player.PlayerLife == 0)
            lock (encounter.Channellers) encounter.Channellers.Remove(player);

        if (encounter.Phase is Phase.Idle or Phase.Channeling)
        {
            if (!view.InZone && distanceSquared <= ZoneEnterRadius * ZoneEnterRadius)
            {
                view.InZone = true;
                if (Interlocked.Increment(ref encounter.InZone) == 1 && encounter.Phase == Phase.Idle)
                {
                    device.States["in_zone"] = 1;
                    await SendStates(player, [device]);
                }
                await Send(player, InstanceCrucible.ForgeEnterZone(), "0x02c9 CrucibleRules inZone=1");
                // Who walks into a channel missed its start.
                if (encounter.Phase == Phase.Channeling)
                {
                    await Send(player, InstanceCrucible.ForgeChannelling(true), "0x02c9 CrucibleRules channelling=1");
                    await Send(player, InstanceCrucible.ForgeState(1), "0x02c9 CrucibleRules state=1");
                    await Send(player, InstanceCrucible.ForgeCharge(encounter.Charge), null);
                }
                await EnsureTarget(player, view);
                await WorldOf(player).RefreshStats();
            }
            else if (view.InZone && distanceSquared > ZoneLeaveRadius * ZoneLeaveRadius)
            {
                // "Once anyone who has channelled steps outside of the molten ring" the wave starts (GGG Q&A).
                bool channelled;
                lock (encounter.Channellers) channelled = encounter.Channellers.Contains(player);
                if (channelled) await Release(player, encounter);
                else
                {
                    view.InZone = false;
                    if (Interlocked.Decrement(ref encounter.InZone) == 0 && encounter.Phase == Phase.Idle)
                    {
                        device.States["in_zone"] = 0;
                        await SendStates(player, [device]);
                    }
                    await Send(player, InstanceCrucible.ForgeLeaveZone(), "0x02c9 CrucibleRules inZone=0");
                    await WorldOf(player).RefreshStats();
                }
            }
        }

        if (encounter.Phase == Phase.Channeling)
        {
            bool release = false, stop = false;
            float charge = -1;
            var sends = new List<Task>();
            // Who only watches does not queue behind a channeller's step.
            if (view.Holding) await encounter.Gate.WaitAsync();
            else if (!encounter.Gate.Wait(0)) return;
            try
            {
                if (encounter.Phase != Phase.Channeling) return;
                if (view.Holding)
                {
                    now = AreaTime(player);
                    // The end of a click-and-hold, or a channeller who died.
                    if (view.Stream && now - view.LastUseAt >= StreamEndSeconds) stop = true;
                    else if (player.PlayerLife == 0) release = true;
                    else
                    {
                        charge = encounter.Charge = Math.Min(1f, encounter.Charge + (float)(dt / FullChargeSeconds));
                        while (encounter.Steps < ChargeSteps && encounter.Steps <= (int)(charge * ChargeSteps))
                            await Grow(player, encounter, device, sends);
                        // The client's action ends itself at 1.0 (0x141341390).
                        release = charge >= 1f;
                    }
                }
            }
            finally { encounter.Gate.Release(); }

            // Sent after the gate: a player's stalled connection must not hold it.
            await Settle(Task.WhenAll(sends));
            if (charge >= 0) await Settle(BroadcastRules(player, InstanceCrucible.ForgeCharge(charge)));
            if (stop) await Stop(player, view, encounter);
            else if (release) await Release(player, encounter);
        }
        else if (encounter.Phase == Phase.Spawning)
        {
            if (now >= encounter.BreakoutAt) await Breakout(player, encounter);
        }
        else if (encounter.Phase == Phase.Active)
        {
            // A kill OnKill did not see (a corpse consumed, a monster gone with its runner).
            bool allDead;
            lock (encounter.Monsters)
            {
                foreach (uint id in encounter.Monsters.ToArray())
                {
                    var body = player.Area.Monsters.GetValueOrDefault(id) ?? encounter.Bodies.GetValueOrDefault(id);
                    if (body is { Dead: false, Life: > 0 } && player.Area.Monsters.ContainsKey(id)) continue;
                    if (body is not null && (body.Dead || body.Life == 0)) encounter.KilledExperience += body.Variety?.ExperienceMultiplier ?? 0;
                    encounter.Monsters.Remove(id);
                    encounter.Bodies.Remove(id);
                }
                allDead = encounter.Monsters.Count == 0;
            }
            if (allDead) await Complete(player, encounter);
        }
    }

    /// <summary>One more step of the channel: a fissure grows outward from the Forge, a pool opens at its end and a
    /// molten monster forms there, held until the release (GGG's reveal video, 1:04). Under the gate: what goes to
    /// the players is collected in <paramref name="sends"/> for the caller to wait for after it.</summary>
    static async Task Grow(Player player, EncounterState encounter, WorldObj device, List<Task> sends)
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
                Id = player.TakeObjectId(),
                Type = CrucibleTables.FissureType,
                X = at.X,
                Y = at.Y,
                // The model lies across its facing (seen live).
                Rotation = (float)(angle + Math.PI / 2),
                States = new() { ["crucible_fissure"] = 1, ["channelling"] = 1 },
            });
        }
        encounter.Fissures.AddRange(created);
        var end = player.Layout.Near(At(start + FissureStep));
        var pool = new WorldObj
        {
            Id = player.TakeObjectId(),
            Type = CrucibleTables.MoltenPoolType,
            X = end.X,
            Y = end.Y,
            States = new() { ["crucible_pool"] = 1 },
        };
        encounter.Pools.Add(pool);
        created.Add(pool);
        encounter.AllObjects.AddRange(created);
        foreach (var obj in created) sends.Add(Present(player, obj));
        sends.Add(SendStates(player, grown));

        int tier = 1 + step * TopSpawnTier / ChargeSteps;
        await Hold(player, encounter, pool, tier);
        // "As you spawn additional monsters, they combine together to create larger and more fearsome foes."
        for (; tier < MaxTier; tier++)
        {
            var same = encounter.Held.Where(h => h.Tier == tier).Take(MergeCount).ToList();
            if (same.Count < MergeCount) break;
            foreach (var merged in same)
            {
                encounter.Held.Remove(merged);
                Remove(player, merged.Id, sends);
                if (!ReferenceEquals(merged.Pool, same[^1].Pool)) merged.Pool.States["crucible_pool"] = 2;
            }
            sends.Add(SendStates(player, [.. same.Take(MergeCount - 1).Select(h => h.Pool)]));
            await Hold(player, encounter, same[^1].Pool, tier + 1);
        }
    }

    /// <summary>Creates a monster of the theme's tier at a pool and holds it: out of the AI and taking no hits.</summary>
    static async Task Hold(Player player, EncounterState encounter, WorldObj pool, int tier)
    {
        if (encounter.Theme is not { } theme || CrucibleMonsters.VarietyForTier(theme, tier) is not { } variety) return;
        // Higher tiers are more often magic or rare ("internal tiering system", GGG Q&A); the two bosses are unique.
        int roll = AreaRng(player).Next(100);
        var rarity = tier >= BossTier ? MonsterRarity.Unique
            : roll < tier * RareChancePerTier ? MonsterRarity.Rare
            : roll < tier * (RareChancePerTier + MagicChancePerTier) ? MonsterRarity.Magic : MonsterRarity.Normal;
        // Its damage is fixed when it is created, so it has the charge it formed at.
        int percent = (int)(encounter.Charge * 100);
        var scaling = CrucibleMonsters.ScalingMods(encounter.AreaLevel,
            percent * FullChargeMoreDamage / 100, -percent * FullChargeLessDamageTaken / 100);
        SpawnedMonster? body = null;
        // Held from birth, under the lock that adds it: the AI never has it free (one held a moment later swung, live).
        uint id = await SpawnMonster(player, variety, (byte)encounter.AreaLevel, rarity,
            pool.X, pool.Y, unrisen: false, alone: false, extraMods: scaling, owned: (_, born) =>
            {
                (born.BusyUntil, born.Aggro, born.EntranceGrace) = (double.PositiveInfinity, false, true);
                body = born;
            });
        if (id == 0 || body is null) return;
        encounter.Held.Add(new(id, tier, pool, body));
        // AreaBuffs owns the instance id, broadcasts to every player in the area, and replays to late joiners.
        if (CrucibleTables.Current.FrozenBuff is ushort frozen)
            player.Area.Buffs.Set(id, new Buff(frozen, Visual: CrucibleTables.Current.StasisVisuals[(int)rarity]));
    }

    /// <summary>Takes a held monster out of the area without a death: it merged into a bigger one.</summary>
    static void Remove(Player player, uint id, List<Task> sends)
    {
        if (player.Area.Monsters.Remove(id, out var body)) { body.Dead = true; body.Life = 0; ++body.ActionGeneration; }
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        player.Area.Buffs.Removed(id);
        sends.Add(SendSharedRemoval(player, id, $"[crucible] held monster 0x{id:x} merged"));
    }

    /// <summary>A release, a channeller leaving the ring, or full charge: the channel ends for everyone and the
    /// monsters break out after <see cref="BreakoutDelaySeconds"/>.</summary>
    static async Task Release(Player player, EncounterState encounter)
    {
        if (encounter.Device is not { } device) return;
        var sends = new List<Task>();
        var ended = new List<(Player Member, PlayerView View, bool Held, bool InZone)>();
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Channeling) return;
            // The shortest channel still forms its first monster.
            if (encounter.Steps == 0) await Grow(player, encounter, device, sends);
            encounter.Phase = Phase.Spawning;
            encounter.BreakoutAt = AreaTime(player) + BreakoutDelaySeconds;
            encounter.InZone = 0;
            device.States["channelling"] = 0;
            device.States["activated"] = 1;
            foreach (var f in encounter.Fissures) { f.States["channelling"] = 0; f.States["crucible_fissure"] = 3; }
            // Everyone's state first: a send that fails must leave nobody holding.
            foreach (var member in Members(player))
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
            await Send(end.Member, InstanceCrucible.ForgeLeaveZone(), "0x02c9 CrucibleRules inZone=0");
            await WorldOf(end.Member).RefreshStats();
        }));
        await Settle(Task.WhenAll(sends));
        await Settle(SendStates(player, [device, .. encounter.Fissures]));
        await Settle(BroadcastRules(player, InstanceCrucible.ForgeChannelling(false), "0x02c9 CrucibleRules channelling=0"));
        await Settle(BroadcastRules(player, InstanceCrucible.ForgeState(2), "0x02c9 CrucibleRules state=2 (combat)"));

        Console.WriteLine($"[crucible] {CharacterName(player)} released Forge at charge {encounter.Charge:0.00} (theme={encounter.Theme?.Tag}): " +
            $"{encounter.Held.Count} monsters, tiers {string.Join(",", encounter.Held.Select(h => h.Tier).Order())}");
    }

    /// <summary>The held monsters break out: the buff goes, <c>broken_out = 1</c> puts the crystals of their
    /// <c>crystal_size</c> on them (<c>CrucibleMonsterBase.ot</c>), and they fight.</summary>
    static async Task Breakout(Player player, EncounterState encounter)
    {
        var sends = new List<Task>();
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Spawning) return;
            double now = AreaTime(player);
            ushort? frozen = CrucibleTables.Current.FrozenBuff;
            lock (encounter.Monsters)
            {
                foreach (var held in encounter.Held)
                {
                    // crystals_0..3 are the small to large crystal packs of the theme's base type: ours by tier.
                    held.Body.States = held.Body.States?.Set("crystal_size", Math.Min(3, (held.Tier - 1) / TiersPerCrystalSize)).Set("broken_out", 1);
                    if (held.Body.States is { } updatedStates && player.Instance.World.MonsterOrigins.TryGetValue(held.Id, out var origin))
                        player.Instance.World.MonsterOrigins[held.Id] = origin with { Object = origin.Object with { StateMachine = updatedStates } };
                    (held.Body.BusyUntil, held.Body.Aggro, held.Body.EntranceGrace) = (now, true, false);
                    encounter.Monsters.Add(held.Id);
                    encounter.Bodies[held.Id] = held.Body;
                }
            }
            foreach (var held in encounter.Held)
            {
                if (frozen is ushort buffRow) player.Area.Buffs.Clear(held.Id, buffRow);
                int machine = held.Body.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") ?? -1;
                if (machine >= 0 && held.Body.States is { } states)
                    sends.Add(SendSharedRecord(player, held.Id, InstanceEntities.StateMachineUpdate(held.Id, (byte)machine, states), null));
            }
            encounter.Held.Clear();
            foreach (var pool in encounter.Pools) pool.States["crucible_pool"] = 2;
            encounter.Phase = Phase.Active;
        }
        finally { encounter.Gate.Release(); }
        await Settle(Task.WhenAll(sends));
        await Settle(SendStates(player, encounter.Pools));
    }

    /// <summary>Tracks encounter monster kills synchronously inside <see cref="GameEvents.Kill"/>.</summary>
    public static void OnKill(KillContext kill)
    {
        if (!HasForge(kill.Player, out var encounter) || encounter.Phase != Phase.Active) return;
        bool finished;
        lock (encounter.Monsters)
        {
            uint id = kill.Monster.ObjectId is > 0 and var oid && encounter.Monsters.Contains(oid) ? oid
                : encounter.Monsters.FirstOrDefault(mid =>
                    (encounter.Bodies.TryGetValue(mid, out var b) && ReferenceEquals(b, kill.Monster))
                    || (kill.Player.Area.Monsters.TryGetValue(mid, out var m) && ReferenceEquals(m, kill.Monster)));
            if (id == 0) return;
            encounter.Monsters.Remove(id);
            encounter.Bodies.Remove(id);
            // "Larger and more fearsome foes that grant even more experience" (GGG's reveal video, 1:13).
            encounter.KilledExperience += kill.Monster.Variety?.ExperienceMultiplier ?? 0;
            finished = encounter.Monsters.Count == 0;
        }
        if (finished)
            kill.Later(() => Complete(kill.Player, encounter));
    }

    /// <summary>A channeller who leaves the area gets no experience; one who was channelling ends the channel for those
    /// who stay, or resets the Forge cleanly back to idle when the last player leaves.</summary>
    public static async Task OnPlayerLeaving(Player player)
    {
        if (!Views.TryGetValue(player, out var view)) return;
        Views.Remove(player);
        if (player.Instance is null || !Instances.TryGetValue(player.Instance, out var encounter)) return;
        lock (encounter.Channellers) encounter.Channellers.Remove(player);
        if (encounter.Phase is not (Phase.Idle or Phase.Channeling)) return;

        int inZone = encounter.InZone;
        if (view.InZone)
        {
            inZone = Interlocked.Decrement(ref encounter.InZone);
            if (inZone < 0)
            {
                Interlocked.CompareExchange(ref encounter.InZone, 0, inZone);
                inZone = 0;
            }
        }

        var other = Members(player).FirstOrDefault(m => !ReferenceEquals(m, player));
        if (encounter.Phase == Phase.Channeling)
        {
            // Released by one who stays: the one leaving can no longer form the channel's first monster.
            if (other is not null)
            {
                if (view.Holding) await Release(other, encounter);
                return;
            }
            Player? joined = null;
            await encounter.Gate.WaitAsync();
            try
            {
                if (encounter.Phase != Phase.Channeling) return;
                joined = Members(player).FirstOrDefault(m => !ReferenceEquals(m, player));
                if (joined is null)
                {
                    encounter.Phase = Phase.Idle;
                    encounter.InZone = 0;
                    encounter.Charge = 0;
                    encounter.Steps = 0;
                    encounter.Theme = null;
                    encounter.BreakoutAt = 0;
                    lock (encounter.Channellers) encounter.Channellers.Clear();
                    foreach (var held in encounter.Held)
                    {
                        if (player.Area.Monsters.Remove(held.Id, out var body))
                        {
                            body.Dead = true;
                            body.Life = 0;
                            ++body.ActionGeneration;
                        }
                        player.Instance.World.MonsterOrigins.TryRemove(held.Id, out _);
                        player.Area.Buffs.Removed(held.Id);
                    }
                    encounter.Held.Clear();
                    encounter.Fissures.Clear();
                    encounter.Pools.Clear();
                    encounter.AllObjects.Clear();
                    if (encounter.Device is { } dev)
                    {
                        dev.States["activated"] = 0;
                        dev.States["channelling"] = 0;
                        dev.States["in_zone"] = 0;
                        encounter.AllObjects.Add(dev);
                    }
                }
            }
            finally { encounter.Gate.Release(); }
            if (joined is not null && view.Holding) await Release(joined, encounter);
            return;
        }

        if (view.InZone && inZone == 0 && encounter.Phase == Phase.Idle && encounter.Device is { } device)
        {
            device.States["in_zone"] = 0;
            if (other is not null) await Settle(SendStates(other, [device]));
        }
    }

    /// <summary>Every monster is dead: each channeller's target item gets the experience ("only on completion", GGG
    /// Q&amp;A), and the tree engine reveals the tree and allocates what the new total reaches.</summary>
    static async Task Complete(Player player, EncounterState encounter)
    {
        Player[] channellers;
        uint experience;
        int killedExperience;
        await encounter.Gate.WaitAsync();
        try
        {
            if (encounter.Phase != Phase.Active) return;
            encounter.Phase = Phase.Completed;
            killedExperience = encounter.KilledExperience;
            experience = (uint)Math.Round(CrucibleTables.Current.BalanceFor(encounter.AreaLevel).Bar1
                * (killedExperience / XpReferenceSum) * ExperienceMultiplier);
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

        if (encounter.Device is { } forge) await Settle(SendStates(player, [forge]));
        await Settle(BroadcastRules(player, InstanceCrucible.ForgeState(3), "0x02c9 CrucibleRules state=3 (complete)"));

        var rng = AreaRng(player);
        foreach (var channeller in channellers)
        {
            if (channeller.Closed || channeller.LeavingArea || !ReferenceEquals(channeller.Instance, player.Instance)
                || !Views.TryGetValue(channeller, out var view)) continue;
            var items = ItemsOf(channeller);
            var catalog = ItemsFor(channeller);
            byte itemRow = view.TargetRow;
            uint itemId = view.TargetItemId;
            if (experience == 0 || items.Items(itemRow).FirstOrDefault(i => i.Id == itemId) is not { } found
                || catalog.ByHash(found.Item.TypeHash) is not { } type
                || !CrucibleTreeEngine.CanChannelInForge(found.Item, type, catalog.Mods)) continue;

            // On the item as it is now: this does not run on the channeller's own loop.
            bool revealedNow = false;
            int newlyAllocated = 0;
            bool awarded = false;
            if (items.Change(itemRow, itemId, current =>
                {
                    if (!CrucibleTreeEngine.CanChannelInForge(current, type, catalog.Mods)) return current;
                    awarded = true;
                    return CrucibleTreeEngine.AwardExperience(current, type, catalog.Mods,
                        experience, rng, out revealedNow, out newlyAllocated);
                }) is not { } updated || !awarded) continue;
            await Settle(Send(channeller,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(itemRow), items.NextRevision(itemRow), [itemId], [updated]),
                $"0x00bf inventory {PlayerInventories.InventoryId(itemRow)}: Crucible Forge +{experience} XP on {type.Name}"));
            Console.WriteLine($"[crucible] {CharacterName(channeller)} completed Forge: +{experience} XP on {type.Name} " +
                $"(killed {killedExperience}, revealed={revealedNow}, allocated={newlyAllocated})");
        }

        var allItems = ItemsFor(player);
        if (killedExperience > 0
            && CrucibleRemnants.RollGeodeDrop(allItems, encounter.AreaLevel, encounter.Charge >= 1f, GeodeDropRate, rng) is { } geode
            && encounter.Device is { } dev)
        {
            var dropCell = player.Layout.Near((dev.X + 3, dev.Y + 3));
            var worldItem = new WorldItemObject(dropCell.X, dropCell.Y, geode);
            uint dropId = player.Instance.Ground.Add(worldItem, player);
            await SendGroundItem(player, dropId, worldItem, null, justDropped: true);
        }

        // The channel skill goes with the completed Forge, and the items' new skills count.
        foreach (var member in Members(player))
            await Settle(WorldOf(member).RefreshStats());
    }
}
