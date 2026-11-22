using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The Allflame league (the client's "Deepwater"): its league category, the league's area stat, the ways to The Sovereign
/// (the World panel pin, /boat, the rowboat on The Coast), the hub's ruleset and its quest table (DeepwaterQuests), and the
/// encounter areas' grid (DeepwaterGrid), ruleset and run (DeepwaterRun: the lanterns and their light, the monsters, the
/// Sulphur nodes, chests and interactables of DeepwaterLoot, the collapse, and the capsules and Bathysphere of
/// DeepwaterExtraction), charts as items (DeepwaterCharts), The Sovereign's deck (DeepwaterHub: the Chart Locker, the chart
/// device and the Voyage planner) where a chart or a board starts a run (DeepwaterStart), Vesper's Allflame crafting and
/// Ducats (DeepwaterCrafting, DeepwaterDucats), Cursed Ducats (DeepwaterCursedDucats), and Messages in a Bottle
/// (DeepwaterBottles).</summary>
public sealed partial class DeepwaterModule : IModule
{
    /// <summary>The Sovereign (<c>WorldAreas</c> DeepwaterHub, area type 53).</summary>
    public const string HubId = "DeepwaterHub";
    /// <summary>The Coast's rowboat to The Sovereign, which only the Deepwater variant of The Coast places (toisland.arm). The
    /// client makes it targetable once the character has <see cref="IntroductionSeen"/>.</summary>
    public const string Rowboat = "Metadata/Terrain/Leagues/Deepwater/Act1_Coast/DeepwaterTutorialBoat";
    internal const string Coast = "1_1_2";
    /// <summary>QuestFlags 3552, set by Valerie's "Ready for Adventure?" (NPCTalk 3932).</summary>
    internal const string IntroductionSeen = "DeepwaterNPCIntroductionSeen";
    /// <summary>QuestFlags 3556, set on the first arrival on The Sovereign (DeepwaterQuests).</summary>
    internal const string HubVisited = "DeepwaterHubVisited";
    // LeagueNames' Id of the Allflame league.
    const string LeagueName = "Deepwater";

    /// <summary>The league category of the Allflame leagues: LeagueCategoryToDisplayName's Id for LeagueNames "Deepwater"
    /// (59). The client picks The Coast's and The Ship Graveyard's Deepwater topologies by it and enables the World panel's
    /// Deepwater pin (SpecialMapPins <c>__col5</c>) only when it is sent.</summary>
    static int? Category;

    static void LoadModuleData()
    {
        int? name = ClientTables.TableOrEmpty("LeagueNames").FirstOrDefault(r => r.Text("Id") == LeagueName) is { Table: not null } row ? row.Index : null;
        if (name is null) { Category = null; return; }
        foreach (var r in ClientTables.TableOrEmpty("LeagueCategoryToDisplayName"))
            if (r.LongOrNull("LeagueName") == name) { Category = r.IntOrNull("Id"); return; }
        Category = null;
    }

    /// <summary>Builds all Deepwater game data eagerly at startup (before the Gateway listens) and on module load.</summary>
    public static void LoadData()
    {
        LoadModuleData();
        DeepwaterConstants.LoadGameData();
        DeepwaterBoard.LoadGameData();
        DeepwaterBoardRules.LoadGameData();
        DeepwaterGrid.LoadGameData();
        DeepwaterHubRuleset.LoadGameData();
        DeepwaterRuleset.LoadGameData();
        DeepwaterCharts.LoadGameData();
        DeepwaterChartStats.LoadGameData();
        DeepwaterBottles.LoadGameData();
        DeepwaterLanterns.LoadGameData();
        DeepwaterLocker.LoadGameData();
        DeepwaterMonsters.LoadGameData();
        DeepwaterLoot.LoadGameData();
        DeepwaterEncounters.LoadGameData();
        DeepwaterExtraction.LoadGameData();
        DeepwaterCrafting.LoadGameData();
        DeepwaterCursedDucats.LoadGameData();
        DeepwaterProgression.LoadGameData();
        DeepwaterUpgrades.LoadGameData();
        DeepwaterVelka.LoadGameData();
    }

    // Rulesets row (S→C 0x1f) DeepwaterHubRules (ClientDeepwaterHub; the hub's windows look it up by row and use it without
    // a null check): DeepwaterHubRuleset.
    // DeepwaterRules (ClientDeepwater, DeepwaterRuleset.Row): its per-frame update (0x1416851b0) looks the DeepwaterPlugin
    // terrain plugin up (0x141685030) without a null check, so it goes only to the areas whose WorldAreas TerrainPlugin is
    // that plugin: the client creates it there at every generation (0x141eca930). On The Sovereign and The Twilight Strand
    // it closed the client (Agents/docs/protocol/03 §8).

    /// <summary>The pin and /boat reach The Sovereign once Valerie's Coast introduction sets <c>LQF 1834</c>
    /// (<c>DeepwaterBoatUnlocked</c>), or for legacy saves that already visited (<see cref="HubVisited"/>).</summary>
    static bool Unlocked(Player player) => DeepwaterProgression.BoatUnlocked(player);

    sealed class State
    {
        public uint Boat;
        public (int X, int Y) BoatAt;
        public int BoatReach;
        // The deck's Chart Locker and Bathysphere (DeepwaterHub).
        public uint Locker, Portal;
        public (int X, int Y) LockerAt, PortalAt;
        public int LockerReach, PortalReach;
        public bool LockerOpen;
        // Valerie's "Take Chart": the chart device opens with its reward window, and again once the reward is taken.
        public bool DeviceAfterReward, DeviceWithReward;
        // A started Chart or Voyage waiting at the deck's Bathysphere (DeepwaterStart), when its opening ends (instance
        // seconds, 0 once open), and a start waiting for the collection's confirmation (op 8).
        public MapDevice.Run? Armed;
        public DeepwaterPlan? ArmedPlan;
        public double PortalOpenAt;
        /// <summary>When the deck next checks whether this player became its map owner (DeepwaterHub.TickDeck).</summary>
        public double NextOwnerCheck;
        public Func<Task>? Confirm;
        /// <summary>True while this connection is reloading the same encounter instance in place (e.g., choosing a Bloodline).</summary>
        public bool SameInstanceReload;
    }

    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    // One run per encounter instance (a party and a re-entry see the same lanterns).
    readonly ConditionalWeakTable<InstanceState, DeepwaterRun> _runs = new();
    readonly DeepwaterLight _light = new();

    sealed class UnloadCleanup(Action onDispose) : IDisposable { public void Dispose() => onDispose(); }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(Quests.Register(id, [new DeepwaterQuests()]));
        // The league monsters' more life (DeepwaterLifeScalingPerLevel) and Cursed Ducat toughness more life, as Harvest's grove monsters.
        module.Lifetime.Own(MonsterModEffects.HandOverMore([DeepwaterMonsters.LevelMoreLife, DeepwaterCursedDucats.CursedMoreLife], []));
        // Clear weak tables on hot-reload so live Gateway objects never pin this collectible ALC.
        module.Lifetime.Own(new UnloadCleanup(() =>
        {
            _states.Clear();
            _runs.Clear();
            _plans.Clear();
            _hubOwners.Clear();
            _light.Clear();
            DeepwaterBuffs.ClearCache();
            DeepwaterDredge.ClearCache();
            DeepwaterLanterns.ClearCache();
        }));
        Func<Player, string, Task> failRun = (c, why) => RunFailed(module, c, why);
        LoadHub(module);
        // Right-clicking a Message in a Bottle (BaseItemTypes#5437) opens BottledItemRevealWindow (0x7b kind 0x6d).
        GameEvents.ItemUse.Add(id, (player, row, item, type) => DeepwaterBottles.Use(module, player, row, item, type));
        // Every area of the league is played in its category and carries map_deepwater_league (what the client's and the
        // server's extra terrain feature roll for Deepwater encounters read).
        InstanceEvents.LeagueCategory.Add(id, _ => Category);
        InstanceEvents.AreaStats.Add(id, _ => new Dictionary<uint, int> { [Stat.MapDeepwaterLeague] = 1 });
        // An encounter area's grid (0x1d extra data), chosen once per instance.
        // A run started from the deck gets its chart's or board's grid; any other (a /goto) a random one.
        InstanceEvents.TerrainPluginStates.Add(id, request => PlanOf(request.Map) is { } plan ? DeepwaterGrid.For(plan) : DeepwaterGrid.For(request.Area));
        // Row 172 DeepwaterHubRules on the hub and in the encounter areas, before 0x21: the in-game UI looks it up once as it
        // attaches to the area (0x140dba5d0), and the deck's Loot window (0x7b kind 0x6f, shown without attaching again)
        // builds its grid over inventory 114 only with it (0x1404244b0); sent after 0x21 the window stayed empty (live
        // 2026-11-19). ClientDeepwaterHub has no per-frame code (03 §8).
        GameEvents.BeforeEnterMessages.Add(id, player =>
        {
            string area = PlayerOf(player).AreaId;
            return (area == HubId || DeepwaterGrid.IsEncounter(area)) && DeepwaterHubRuleset.Row is { } hub
                ? [($"0x001f ruleset DeepwaterHubRules (row {hub})", InstanceProtocol.AddRuleset(hub))] : [];
        });
        // Row 171 DeepwaterRules in an encounter area, after 0x21 and before the area's objects.
        GameEvents.EnterMessages.Add(id, player =>
            DeepwaterGrid.IsEncounter(PlayerOf(player).AreaId) && DeepwaterRuleset.Row is { } rules
                ? [($"0x001f ruleset DeepwaterRules (row {rules})", InstanceProtocol.AddRuleset(rules))] : []);
        // C→S 0x183: the World panel's Deepwater pin (enabled by the league category, disabled on The Sovereign itself).
        GameEvents.Packet(InstanceWaypoints.VisitDeepwaterHubId).Add(id, async (player, bytes) =>
        {
            uint from = bytes.Length >= 6 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) : 0;
            byte flag = bytes.Length >= 7 ? bytes[6] : (byte)0;
            module.Log($"{PlayerOf(player).Character} <= 0x0183 World panel Deepwater pin (from object 0x{from:x}, u8 {flag})");
            // The u8 (1 with Ctrl held, which skips the client's "already there" test) is ignored, as for the other hub pins:
            // TravelTo opens no new instance, so on The Sovereign itself the click does nothing.
            if (!Unlocked(player) || PlayerOf(player).AreaId == HubId) return true;
            await WorldOf(player).TravelTo(HubId, "World panel Deepwater pin");
            return true;
        });
        // /boat (Commands WarpToDeepwaterHub): every other command goes on to the Game module.
        GameEvents.Packet(InstanceItems.CommandId).Add(id, async (player, bytes) =>
        {
            if (InstanceItems.ReadCommand(bytes).Id != InstanceItems.WarpToDeepwaterHubCommand) return false;
            if (!Unlocked(player) || PlayerOf(player).AreaId == HubId) return true;
            await WorldOf(player).TravelTo(HubId, "/boat");
            return true;
        });
        GameEvents.PlayerEntered.Add(id, async player =>
        {
            var previousView = Program.ViewOf.Value;
            Program.ViewOf.Value = player;
            try
            {
                await SpawnRowboat(module, player);
                await SpawnDeck(module, player);
                await StartRun(module, player);
                await DeepwaterFairgraves.Enter(module, player);
                // The Coral Nest, Cursed Treasure and Brinerot Plunder rooms of an ordinary area.
                string area = PlayerOf(player).AreaId;
                if (area != HubId && !DeepwaterGrid.IsEncounter(area)) await DeepwaterEncounters.Enter(module, player);
            }
            finally { Program.ViewOf.Value = previousView; }
        });
        // C→S 0x2ca: the encounter ruleset's actions (a lantern placed, a capsule's buttons) and the hub ruleset's (the
        // Loot window's Take All, the planner's ops: DeepwaterHub).
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, async (player, bytes) =>
        {
            return await HubAction(module, player, bytes) || await RulesetAction(module, player, bytes);
        });
        // C→S 0x0113: Ctrl + click an item out of the Loot window (inventory 114).
        GameEvents.Packet(InstanceItems.CollectionTakeId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceItems.CollectionTakeLength) return false;
            await DeepwaterExtraction.TakeFromCollection(module, player, InstanceItems.ReadCollectionTake(bytes));
            return true;
        });
        GameEvents.AreaTick.Add(id, GameEvents.Always(async (player, _) =>
        {
            await DeepwaterBuffs.Tick(module, player);
            await DeepwaterUpgrades.TickAdrenaline(module, player);
            await DeepwaterProgression.TickQuestPickups(player);
            await DeepwaterFairgraves.Tick(module, player);
            await DeepwaterEncounters.Tick(module, player);
            await TickDeck(player);
            if (RunOf(player) is not { } run) return;
            await _light.Tick(module, player, run);
            await DeepwaterCollapse.Tick(module, player, run);
            await DeepwaterExtraction.Tick(module, player, run, failRun);
            await DeepwaterVelka.Tick(module, player, run);
            await TickAutoLantern(module, player, run);
            await DeepwaterDredge.Tick(module, player);
        }));
        // Dredge: a pickup from the ground during a run, or in a failed run's dead instance, is noted (never for quest items,
        // upgrade pieces, Cursed Ducats, or Fairgraves'/Velka's Allflame); the Game module picks it up. What is noted after
        // a failure goes at the next entry elsewhere (StartRun). The first chart picked up sets DeepwaterChartFound
        // (QuestFlags 3560, the tracker's "found a Chart"; G: the trigger is unsourced).
        GameEvents.GroundItemUse.Add(id, async (player, groundId, ground, _, _) =>
        {
            var catalog = ItemsFor(player);
            bool questPickup = DeepwaterVelka.IsQuestDrop(catalog, ground.Item)
                || DeepwaterUpgrades.IsUpgradePiece(ground.Item)
                || DeepwaterCursedDucats.IsCursedDucat(ground.Item)
                || ground.Item.TypeHash == DeepwaterFairgraves.AllflameItemHash;
            if (!questPickup && RunOf(player) is { } run && (run.Phase != DeepwaterRun.RunPhase.Ended || run.Failed))
                DeepwaterDredge.Noted(player, groundId, ground);
            if (questPickup) DeepwaterProgression.NoteQuestPickup(player, groundId);
            if (!player.Character.HasQuestFlag(DeepwaterCharts.ChartFound) && DeepwaterCharts.IsChart(catalog, ground.Item))
            {
                await SetQuestFlags(player, [DeepwaterCharts.ChartFound]);
                module.Log($"{PlayerOf(player).Character}: first Allflame chart picked up, {DeepwaterCharts.ChartFound} set");
            }
            return false;
        });
        // Quest reward items: Valerie's tutorial chart component + Offers#131–138 tutorial rings & Sulphur stacks.
        GameEvents.RewardItem.Add(id, (player, offer, item, type) =>
            DeepwaterProgression.ResolveRewardItem(player, offer, DeepwaterCharts.RewardItem(player, offer, item, type), type));
        // The encounter areas are not maps, so "Resurrect at Checkpoint" revives in the instance: after a failed run the
        // player goes to The Sovereign instead (G).
        GameEvents.Resurrected.Add(id, async player =>
        {
            if (RunOf(player) is { Failed: true } && PlayerOf(player).AreaId != HubId)
                await WorldOf(player).TravelTo(HubId, "resurrect after a failed Allflame run (G)");
        });
        GameEvents.PlayerLeaving.Add(id, async (player, reason) =>
        {
            _light.Forget(player);
            DeepwaterBuffs.Forget(player);
            DeepwaterUpgrades.ForgetAdrenaline(player);
            DeepwaterProgression.ForgetQuestPickups(player);
            DeepwaterLanterns.ForgetAuto(player);
            var state = _states.GetOrCreateValue(PlayerOf(player));
            if (player.PendingTransfer is { } transfer)
                state.SameInstanceReload = !transfer.New
                    && transfer.Area == player.Layout.Hash
                    && (transfer.Instance == player.InstanceNumber || transfer.Instance == 0);
            // Leaving a run any way but the Bathysphere ends it as a failure, except a same-instance reload (e.g., choosing a Bloodline).
            if (state.SameInstanceReload && player.LeavingArea) return;
            state.SameInstanceReload = false;
            if (RunOf(player) is { Phase: not DeepwaterRun.RunPhase.Ended }) await RunFailed(module, player, $"left ({reason})");
        });
        // Inventory 113 takes only Dredge items, through an open capsule window; a Dredge item goes nowhere else but the
        // main and expanded inventories (the client's own gate).
        GameEvents.InventoryRefusal.Add(id, (player, row, item) => row == DeepwaterExtraction.CapsuleRow
            ? DeepwaterExtraction.CapsuleRefusal(player, RunOf(player), row, item) : DeepwaterExtraction.DredgeRefusal(player, row, item));
        // C→S 0x0112: Ctrl + click into or out of an open capsule.
        GameEvents.Packet(InstanceItems.ExtractionMoveId).Add(id, async (player, bytes) =>
        {
            if (RunOf(player) is not { } run || bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            await DeepwaterExtraction.QuickMove(module, player, run, InstanceItems.ReadLeagueWindowMove(bytes));
            return true;
        });
        // The Drowning slow; the encounter buffs' chances (Craiceann's Call, Sailor's Lantern); final-lantern Adrenaline.
        GameEvents.PlayerStatSources.Add(id, (player, sources) =>
            DeepwaterUpgrades.PlayerStats(player, DeepwaterBuffs.PlayerStats(player, _light.WithSlow(player, sources))));
        // Craiceann's Call crabs; Act 1 Deepwater Fairgraves (1_1_9); Kishara's Rest Velka & Cressie (#30);
        // chart_rare_monsters_drop_dead_mans_sulphur (0-alloc when inactive).
        GameEvents.MonsterKilled.Add(id, async kill =>
        {
            await DeepwaterBuffs.Killed(kill);
            await DeepwaterFairgraves.OnMonsterKilled(module, kill);
            if (RunOf(kill.Player) is { } run)
            {
                await DeepwaterVelka.OnMonsterKilled(module, kill, run);
                if (kill.Monster.Rarity >= MonsterRarity.Rare && run.HasAnyRareSulphur && run.CellRules is { } rules)
                {
                    int cell = run.CellIndex(kill.Monster.X, kill.Monster.Y);
                    if ((uint)cell < (uint)rules.Length && rules[cell].RareDropSulphur)
                        await DeepwaterLoot.AwardRareSulphur(module, kill.Player, run, cell);
                }
            }
        });
        // Per-cell map_experience_gain_+% on Charts and Voyages (0-alloc when inactive).
        GameEvents.Experience.Add(id, gain =>
        {
            if (RunOf(gain.Player) is { HasAnyExpBonus: true, CellRules: { } rules } run)
            {
                int cell = run.CellIndex(gain.Monster.X, gain.Monster.Y);
                if ((uint)cell < (uint)rules.Length && rules[cell].ExpFactor != 1.0)
                    gain.Experience = (uint)Math.Clamp(Math.Round(gain.Experience * rules[cell].ExpFactor), 0, uint.MaxValue);
            }
        });
        GameEvents.StateBuilding.Add(id, (player, json) =>
        {
            RunState(player, json);
            DeepwaterEncounters.State(player, json);
        });
        // The league's monsters' map-related drops, Cursed Ducat IIQ/IIR and unlocks, upgrade pieces, special-room Ducats
        // and the Golden Lantern stacks in a run; a Brinerot Plunder guard's Ducat; Sailor's Lantern's duplicated gold.
        GameEvents.Drops.Add(id, roll =>
        {
            if (RunOf(roll.Player) is { } run)
            {
                DeepwaterLoot.Drops(roll, run);
                DeepwaterCursedDucats.Drops(module, roll);
                if (roll.Monster.Rarity >= MonsterRarity.Rare)
                {
                    int cell = run.CellIndex(roll.Monster.X, roll.Monster.Y);
                    int roomId = cell >= 0 && cell < run.Board.Cells.Count ? run.Board.Cells[cell].Room : -1;
                    DeepwaterUpgrades.Drops(module, roll, run.Level, run.Voyage, roomId, DeepwaterLoot.IsSpecialRoom(roomId));
                }
            }
            DeepwaterEncounters.Drops(roll);
            DeepwaterBuffs.Drops(module, roll);
        });
        // A warcry or the caster mastery opens a cleared encounter chest (3.29.1).
        GameEvents.ChestsOpened.Add(id, (player, at, radius) => DeepwaterEncounters.OpenNear(module, player, at, radius));
        // Sovereign below-deck stash click-swap (returns false so Game.OpenStash/OpenGuildStash opens it natively);
        // the run's chests, interactables, capsules, ArenaTeleporter & VelkaDescensionObject; encounters; deck; rowboat.
        GameEvents.ObjectUse.Add(id, async (player, use, at) =>
        {
            if (PlayerOf(player).AreaId == HubId && DeepwaterProgression.PickHubStash(player, use.TargetId)) return false;
            if (RunOf(player) is { } run && (await DeepwaterLoot.Use(module, player, run, use, at)
                || await DeepwaterExtraction.Use(module, player, run, use, at)
                || await DeepwaterVelka.Use(module, player, run, use, at))) return true;
            if (await DeepwaterEncounters.Use(module, player, use, at) || await UseDeck(module, player, use, at)) return true;
            var modPlayer = PlayerOf(player);
            if (!_states.TryGetValue(modPlayer, out var state) || state.Boat == 0 || use.TargetId != state.Boat) return false;
            // The client offers the boat only after Valerie's introduction; an early click is refused quietly.
            if (!ClientHasQuestFlag(player, IntroductionSeen)) return true;
            var world = WorldOf(player);
            if (await world.WalkUpTo(state.BoatAt.X, state.BoatAt.Y, state.BoatReach, at))
                await world.TravelTo(HubId, "The Coast's rowboat");
            return true;
        });
    }

    // The rowboat where the Deepwater Coast's toisland.arm places it, as the real server's object (Transitionable state 1;
    // its StateMachine's defaults). The quest engine does not write StateMachine objects, so the module creates it.
    async Task SpawnRowboat(IModuleContext module, Player player)
    {
        var modPlayer = PlayerOf(player);
        var state = _states.GetOrCreateValue(modPlayer);
        state.Boat = 0;
        if (modPlayer.AreaId != Coast) return;
        var at = new QuestObjectTypes.AreaPlacements(Coast, player.Seed, []).Of(Rowboat).FirstOrDefault();
        if (at is null) return;   // the Coast's other variants
        var states = ObjectTypes.All().Where(t => t.Type == Rowboat).Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault();
        if (QuestObjectTypes.Find(Rowboat) is not { } info || states is null || !QuestObject.CanSerialize(info.Components, true))
        {
            module.Log($"{modPlayer.Character}: the rowboat's components are not written ({string.Join(", ", QuestObjectTypes.Find(Rowboat)?.Components ?? [])})");
            return;
        }
        uint id = player.TakeObjectId();
        var blob = new QuestObject(at.X, at.Y, info.Components, at.Rotation, at.Z, info.Model ?? (info.OwnModel ? null : at.Model)) { StateMachine = states }.Blob();
        if (!await global::Game.SendWorldCreate(modPlayer, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(Rowboat), blob),
            $"0x02bb Deepwater rowboat 0x{id:x} at ({at.X}, {at.Y})")) return;
        await global::Game.SendWorldRecord(modPlayer, id, InstanceProtocol.EnterWakeZone(id));
        (state.Boat, state.BoatAt, state.BoatReach) = (id, (at.X, at.Y), info.Size + 2);
    }
}
