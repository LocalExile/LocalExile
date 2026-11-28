using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// Complete retail 3.29 Mercenaries of Trarthus system:
/// <list type="bullet">
/// <item>Encounter spawning (maps, campaign Act 3+, Warrants, plus <see cref="GameEvents.EncounterSpawn"/> hook for mods)</item>
/// <item>Full <c>MercenaryEncounterRules</c> (<c>0xa1</c>) flow: visuals, full record + manager-1 gear &amp; rucksack inventories,
/// opt-in, wager cost, Take Item, Rematch (Warrant), Hire (Join/Reserve), and Should Recruit temporary allies</item>
/// <item>Full <c>MercenaryAreaRules</c> (<c>0xa2</c>) permanent (keys 0..2) &amp; temporary (key 3) roster with atomic persistence,
/// Town/Hideout reserved NPCs, Loadout UI (<c>0x7b 0x60</c>), equipment validation (<c>CanMercenaryEquipItem</c>), and <c>C→S 0x010c</c> quick-move</item>
/// <item>Live Companion AI, combat skills &amp; supports, non-curse aura sharing (<c>Bestowed Knighthood</c>), taunt, damage/recoup/death, and Link integration</item>
/// </list>
/// </summary>
public sealed partial class MercenaryModule : IModule
{
    const string StateKey = "Mercenary.Encounter";
    const string RosterSaveKey = "mercenary.roster";

    /// <summary>The draws of the encounter, apart from the packs' (the same instance gets the same mercenary).</summary>
    const int RollTag = 0x4d657263, PlanTag = 0x4d657232;

    /// <summary>Where the encounter is, in this instance: kept in <c>Instance.ContentState</c> too, so a module reload does not lose it.</summary>
    public enum Stage { None, Met, Talk, Countdown, Duel, Hired, Failed }

    /// <summary>The duel outcome chosen in the encounter window before the countdown starts.</summary>
    public enum DuelOfferKind { None, TakeGearItem, TakeRucksack, Rematch, Hire }

    public sealed class Run
    {
        public bool Rolled { get; set; }
        public Stage Stage { get; set; }
        public int Class { get; set; } = -1;
        public int Build { get; set; } = -1;
        public int X { get; set; }
        public int Y { get; set; }
        public byte Level { get; set; }
        public int Seed { get; set; }
        /// <summary>The mercenary's body now (the met one, then the duel opponent) and, at the end, the hired one.</summary>
        public uint Body { get; set; }
        public uint Hired { get; set; }
        public double Clock { get; set; }
        public int Steps { get; set; }

        // Wire identity (verified HASH16/HASH32)
        public uint FirstNameHash32 { get; set; }
        public uint SecondNameHash32 { get; set; }
        public ushort FlavourHash16 { get; set; }
        public List<MercenaryTables.RolledSkill> Skills { get; set; } = [];

        // Encounter offer & ownership
        public string? OwnerName { get; set; }
        public uint WagerCost { get; set; }
        public bool IsWarrantCall { get; set; }
        /// <summary>A campaign zone's guaranteed encounter (100% chance): no level gate and an empty Rucksack.</summary>
        public bool Guaranteed { get; set; }
        public DuelOfferKind ChosenOffer { get; set; }
        public uint ChosenItemId { get; set; }
        public bool ShouldRecruit { get; set; }
        public bool HireJoinImmediately { get; set; }
        public string? DuelInitiator { get; set; }
        public uint WagerPaidByInitiator { get; set; }
        public List<string> OptedInPlayers { get; set; } = [];

        // Encounter gear (placed in manager-1 inventory 0x640) and optional rucksack (0x641)
        public List<MercenaryEquipment.Worn> Equipment { get; set; } = [];
        public List<InventoryItem> GearItems { get; set; } = [];
        public List<InventoryItem> RucksackItems { get; set; } = [];

        public List<Wild> Wilds { get; set; } = [];
        public bool WildsPlanned { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool Busy;
    }

    sealed record PlayerMercenarySnapshot(bool CanHaveFollower, bool AllowAllUniqueSlots, LuminaryBonuses Bonuses);
    sealed record EntitySyncSnapshot(IReadOnlyList<VisibleItem> Looks, IReadOnlyList<(uint Key, int Value)> ClientStats, uint Life);

    readonly ConditionalWeakTable<InstanceState, Run> runs = new();
    readonly ConditionalWeakTable<Player, object> toldEncounter = new();
    readonly ConditionalWeakTable<Player, object> sentEncounterData = new();
    readonly ConditionalWeakTable<Player, object> toldAreaRules = new();
    readonly ConditionalWeakTable<Player, CompanionRuntime> companions = new();
    readonly ConditionalWeakTable<Player, Dictionary<uint, byte>> loadoutNpcs = new();
    readonly ConditionalWeakTable<Player, PlayerMercenarySnapshot> lastPlayerSnapshot = new();
    readonly ConditionalWeakTable<Player, Dictionary<uint, EntitySyncSnapshot>> lastEntitySync = new();
    readonly ConditionalWeakTable<Player, object> refreshingPlayer = new();
    IModuleContext context = null!;

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads (before the Gateway listens); a test that uses the module calls it once.</summary>
    public static void LoadData() => MercenaryTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        context = module;
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(() =>
        {
            foreach (var (p, comp) in companions)
            {
                comp.Closed = true;
                ClearCompanionSharedAuras(p, comp.EntityId);
                ClearCompanionTauntsLocked(p, comp.EntityId);
                if (p.Companion?.EntityId == comp.EntityId) p.Companion = null;
            }
            PendingWarrants.Clear();
            runs.Clear();
            toldEncounter.Clear();
            sentEncounterData.Clear();
            toldAreaRules.Clear();
            companions.Clear();
            loadoutNpcs.Clear();
            lastPlayerSnapshot.Clear();
            lastEntitySync.Clear();
            refreshingPlayer.Clear();
        }));
        GameEvents.AreaRuleset.Add(id, player =>
        {
            if (MercenaryTables.AreaRulesRow is not { } row) return null;
            lock (toldAreaRules) toldAreaRules.AddOrUpdate(player, new object());
            return (row, null);
        });
        GameEvents.AreaRuleset.Add(id, player =>
        {
            if (IsTownOrHideout(player) || MercenaryTables.EncounterRulesRow is not { } row) return null;
            lock (toldEncounter) toldEncounter.AddOrUpdate(player, new object());
            return (row, null);
        });
        GameEvents.AreaPopulated.Add(id, Place);
        GameEvents.PlayerEntered.Add(id, Enter);
        GameEvents.AreaTick.Add(id, _ => (player, seconds) => Tick(player, seconds));
        GameEvents.CompanionTick.Add(id, TickCompanion);
        GameEvents.AreaQuestFlags.Add(id, EncounterQuestFlags);
        GameEvents.NpcTalk.Add(id, Talk);
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, HandleRulesetAction);
        GameEvents.Packet(InstanceItems.MercenaryQuickMoveId).Add(id, HandleQuickMove);
        GameEvents.InventoryPlacementRefusal.Add(id, ValidateMercenaryPlacement);
        GameEvents.InventoryItemAction.Add(id, HandleMercenaryItemAction);
        GameEvents.InventoryChanged.Add(id, OnInventoryChanged);
        GameEvents.PlayerStatsChanged.Add(id, OnPlayerStatsChanged);
        GameEvents.PlayerLeaving.Add(id, OnPlayerLeaving);
        GameEvents.TransientClosing.Add(id, CloseCompanionTransient);
        GameEvents.PlayerDied.Add(id, OnPlayerDied);
        GameEvents.Kill.Add(id, OnKill);
        GameEvents.Kill.Add(id, WildKilled);
        GameEvents.MapOpening.Add(id, Opening);
        GameEvents.MapDeviceAcceptsItem.Add(id, AcceptsMapDeviceItem);
    }

    static byte AreaLevelOf(Player player) =>
        (byte)Math.Clamp(player.Instance.Map?.Area.Level
            ?? MercenaryTables.AreaOf(player.Layout.Id)?.AreaLevel
            ?? player.Pools.Level, 1, 100);

    Run? Peek(Player player)
    {
        if (runs.TryGetValue(player.Instance, out var run)) return run;
        if (!player.Instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return runs.GetValue(player.Instance, _ => JsonSerializer.Deserialize<Run>(text) ?? new());
    }

    Run State(Player player) => Peek(player) ?? runs.GetValue(player.Instance, _ => new());

    void Save(Player player, Run run)
    {
        string json;
        lock (run) json = JsonSerializer.Serialize(run);
        player.Instance.ContentState[StateKey] = json;
    }

    static bool CanHavePermanentFollower(Player player)
    {
        bool native = PlayerStatValues(player).GetValueOrDefault(Stat.CanHaveActiveMercenaryFollower) > 0;
        return GameEvents.ResolveCompanionRules(player, native).CanHavePermanentFollower;
    }

    static bool IsTownOrHideout(Player player) => InTown(player) || MercenaryTables.IsTownOrHideout(player.Layout.Id);
}
