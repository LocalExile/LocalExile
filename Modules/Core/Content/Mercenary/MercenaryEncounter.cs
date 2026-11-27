using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Mercenary;

public sealed partial class MercenaryModule
{
    static readonly string[] OwnerQuestFlags = [MercenaryWire.OwnerQuestFlag];

    // ---- Quest flag (IsMapOwner / 0xbd0b7d78) ---------------------------------------------------------------------------------

    IReadOnlyList<string> EncounterQuestFlags(Player player)
    {
        if (Peek(player) is not { Stage: not Stage.None } run) return [];
        return IsEncounterOwner(player, run) ? OwnerQuestFlags : [];
    }

    static bool IsEncounterOwner(Player player, Run run)
    {
        if (string.IsNullOrEmpty(run.OwnerName)) return true;
        return string.Equals(CharacterName(player), run.OwnerName, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The Roll & Campaign / Map Spawning -----------------------------------------------------------------------------------

    /// <summary>The percent chance a map area holds a mercenary. CoreLeagues row 28 is not rolled by <see cref="LeagueChances.Count"/>,
    /// so the area's own WorldAreaLeagueChances column is read (as Delve's sulphite does) and the map's stats are added.</summary>
    public static long ChanceOf(string areaId, bool hardMode, IReadOnlyDictionary<uint, int> stats)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if ((S(Stat.MapDisableLeagues) & (1 << LeagueChances.Mercenaries)) != 0 || S(Stat.AtlasKeystoneNoExtraContent) != 0
            || S(Stat.MapDisableMercenary) > 0) return 0;
        if (S(Stat.MapMercenaryLeague) > 0) return 100;
        if (S(Stat.MapDisableMercenaryFromChance) > 0) return 0;
        int baseChance = S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 ? 0
            : LeagueChances.Current.ChanceOf(areaId, LeagueChances.Mercenaries, alternate: hardMode).Chance;
        long chance = baseChance * Math.Max(0L, 100L + S(Stat.MapLeagueBaseChancePlusPctFinal)) / 100
            + S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapMercenaryAdditionalChancePct);
        return Math.Max(0L, chance) * Math.Max(0L, 100L + S(Stat.MapMercenaryChancePlusPct)) / 100;
    }

    /// <summary>Evaluates whether a new combat instance (map or campaign) rolls a mercenary encounter, honoring retail 3.29
    /// Act 3+ campaign rules, Warrant calls, map chances, and <see cref="GameEvents.EncounterSpawn"/> overrides (for Phase 2 mods).</summary>
    async Task Place(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (layout.Walkable is null || IsTownOrHideout(player)) return;

        var run = State(player);
        lock (run)
        {
            if (run.Rolled) return;
            run.Rolled = true;
            run.Level = area.Level;
            run.Seed = area.Seed;
            run.OwnerName = CharacterName(player);
        }

        bool isMap = MapDevices.IsMapArea(layout.Hash);
        var stats = AreaLeagueStats(player);

        // Check if this map was opened with a Mercenary Warrant rematch blueprint
        var warrant = ConsumePendingWarrant(player);

        bool allowed;
        long chance;
        bool force = warrant is not null;

        if (isMap)
        {
            allowed = true;
            chance = force ? 100 : ChanceOf(layout.Id, LeagueFor(player).HardMode, stats);
        }
        else
        {
            // Retail 3.29 campaign spawning: Act 3 onward when playerLevel <= areaLevel + 10; guaranteed in 5 key zones.
            var meta = MercenaryTables.AreaOf(layout.Id);
            allowed = meta is { IsTown: false, IsHideout: false, Act: >= 3 }
                && player.Pools.Level <= area.Level + 10;
            bool guaranteed = allowed && MercenaryTables.GuaranteedCampaignAreas.Contains(layout.Id);
            chance = guaranteed ? 100 : allowed ? 15 : 0;
            force |= guaranteed;
        }

        // Allow Phase 2 mods (e.g. 3.26 Mercenaries mod) to enable encounters from Mud Flats onward and scale level
        var spawnRequest = new EncounterSpawnRequest(player, "mercenary", area.Level, chance, allowed) { Force = force };
        GameEvents.ResolveEncounterSpawn(spawnRequest);

        bool placed = spawnRequest.Allowed && (spawnRequest.Force || spawnRequest.Chance >= 100
            || spawnRequest.Chance > 0 && new Random(area.Seed ^ RollTag).Next(100) < spawnRequest.Chance);

        var rng = new Random(area.Seed ^ PlanTag);
        if (placed)
        {
            (MercenaryTables.Class Kind, MercenaryTables.Build Build)? pick = null;
            byte spawnLevel = spawnRequest.Level;
            if (warrant is not null
                && MercenaryTables.ClassOf(warrant.ClassRow) is { } wKind
                && MercenaryTables.BuildOf(warrant.BuildRow) is { } wBuild)
            {
                pick = (wKind, wBuild);
                spawnLevel = warrant.Level;
                lock (run)
                {
                    run.IsWarrantCall = true;
                    run.FirstNameHash32 = warrant.FirstNameHash32;
                    run.SecondNameHash32 = warrant.SecondNameHash32;
                    run.FlavourHash16 = warrant.FlavourHash16;
                    run.Skills = warrant.Skills;
                }
            }
            else
            {
                bool infamous = stats.GetValueOrDefault(Stat.MapMercenariesAreInfamous) > 0
                    || (spawnLevel >= 68 && rng.Next(100) < 5 + stats.GetValueOrDefault(Stat.MapMercenaryInfamousChancePlusPct));
                pick = MercenaryTables.Pick(rng, infamous) ?? MercenaryTables.Pick(rng, false);
            }

            if (pick is { } chosen && AreaPopulation.EncounterSpot(area, area.Taken, rng) is { } at)
                await Spawn(player, run, chosen.Kind, chosen.Build, at, spawnLevel, area.Seed);
        }
        Save(player, run);
    }

    // ---- The Encounter Mercenary ----------------------------------------------------------------------------------------------

    static IReadOnlyList<VisibleItem> GearOf(MercenaryTables.Class kind, MercenaryTables.Build build, byte level, int seed, Run? run = null)
    {
        var looks = MercenaryGear.Of(kind, build, level, new Random(unchecked(seed * 397 ^ build.Row)));
        return run is { Equipment.Count: > 0 } ? MercenaryEquipment.Looks(run.Equipment, looks) : looks;
    }

    static StateMachineData? States(MonsterCatalog.Variety variety, IReadOnlyList<int> values) =>
        variety.StateMachine is { } machine && machine.SharedStates.Count == values.Count
            ? machine with { SharedStates = values, Overridden = Enumerable.Range(0, values.Count).ToHashSet() } : null;

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player).Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, player.Instance)).Distinct();

    static async Task Everyone(Player player, Func<Player, Task> send)
    {
        if (player.Area.Owned && player.Connection.WorldOutput.Started) { await send(player); return; }
        foreach (var member in Members(player)) await send(member);
    }

    static async Task Privately(Player player, Func<Task> send)
    {
        var previous = ViewOf.Value;
        ViewOf.Value = player;
        try { await send(); }
        finally { ViewOf.Value = previous; }
    }

    static bool Alive(Player player) => !player.Closed && !player.LeavingArea && player.PlayerLife > 0;

    async Task<bool> Spawn(Player player, Run run, MercenaryTables.Class kind, MercenaryTables.Build build, (int X, int Y) at,
        byte level, int seed)
    {
        if (Monsters.Find(kind.Path) is not { Supported: true } variety || States(variety, MercenaryWire.MetStates) is not { } met)
            return false;

        var rng = new Random(unchecked(seed * 397 ^ build.Row));
        var stats = AreaLeagueStats(player);

        if (run.FirstNameHash32 == 0 || run.SecondNameHash32 == 0 || run.FlavourHash16 == 0 || run.Skills.Count == 0)
        {
            var identity = MercenaryTables.RollIdentity(build, level, rng,
                stats.GetValueOrDefault(Stat.MapMercenaryAdditionalSupportChancePct));
            run.FirstNameHash32 = identity.FirstNameHash32;
            run.SecondNameHash32 = identity.SecondNameHash32;
            run.FlavourHash16 = identity.FlavourHash16;
            run.Skills = identity.Skills;
        }

        if (run.Equipment.Count == 0)
        {
            run.Equipment = MercenaryEquipment.Roll(kind, build, level, rng,
                stats.GetValueOrDefault(Stat.MapMercenaryAllUniqueItems) > 0, UniquesFor(player));
        }

        if (run.GearItems.Count == 0)
            run.GearItems = MercenaryEquipment.ToInventoryItems(run.Equipment, startingId: 1);

        if (run.RucksackItems.Count == 0 && stats.GetValueOrDefault(Stat.MapMercenaryRucksackIsEmpty) <= 0)
            run.RucksackItems = RollRucksack(level, rng, forceFull: stats.GetValueOrDefault(Stat.MapMercenaryRucksackIsFull) > 0);

        run.WagerCost = MercenaryTables.WagerGoldCost(level, build.Infamous, stats.GetValueOrDefault(Stat.MapMercenaryWagerGoldAmountPlusPct));

        var gear = GearOf(kind, build, level, seed, run);
        string displayName = MercenaryTables.FormatName(run.FirstNameHash32, run.SecondNameHash32) ?? build.Name;
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Unique, at.X, at.Y, friendly: true, instanceOwned: true,
            scripted: true, ownedLooks: gear, shape: m => m with
            {
                Name = displayName, Team = MercenaryWire.MercenaryTeam, StateMachine = met, MercenaryBuild = build.Hash16,
                HasDialogue = true, NpcFlags = 1, InteractionFlags = 1,
            });
        if (id == 0) return false;
        if (player.Area.Monsters.TryGetValue(id, out var body))
        {
            body.States = met;
            at = body.Walk.Position(AreaTime(player));
        }
        lock (run)
        {
            run.Stage = Stage.Met; run.Class = kind.Row; run.Build = build.Row; run.X = at.X; run.Y = at.Y;
            run.Level = level; run.Seed = seed; run.Body = id; run.Hired = 0; run.Clock = 0; run.Steps = 0;
        }
        Register(player, run);
        Save(player, run);

        // Flush transient IsMapOwner quest flag to owner & send full 0xa1 encounter ruleset + manager-1 inventories
        if (IsEncounterOwner(player, run))
            await SendPlayerQuestFlags(player, "0x02bc player quest flags (Mercenary encounter owner)");
        await SendEncounterState(player, run, kind, build, gear);

        context.Log($"{player.Layout.Id}: mercenary {build.Name} ({kind.House}, {kind.Path.Split('/')[^1]}) at ({at.X}, {at.Y}), level {level}, id {id}, wager {run.WagerCost}g");
        return true;
    }

    static List<InventoryItem> RollRucksack(byte level, Random rng, bool forceFull)
    {
        if (!forceFull && rng.Next(100) >= 55) return [];
        // Roll 1-2 currency items into the 2x2 rucksack slot (placed at (0,0) / (1,0))
        string[] pool = level >= 68
            ? ["Metadata/Items/Currency/CurrencyRerollRare", "Metadata/Items/Currency/CurrencyAddModToRare",
               "Metadata/Items/Currency/CurrencyRerollSocketLinks", "Metadata/Items/Currency/CurrencyUpgradeToRare",
               "Metadata/Items/Currency/CurrencyIdentification"]
            : ["Metadata/Items/Currency/CurrencyUpgradeToRare", "Metadata/Items/Currency/CurrencyRerollMagic",
               "Metadata/Items/Currency/CurrencyUpgradeRandomly", "Metadata/Items/Currency/CurrencyIdentification"];
        var result = new List<InventoryItem>();
        int count = forceFull ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            string chosenId = pool[rng.Next(pool.Length)];
            if (Program.Items.Find(chosenId) is not { Supported: true } bt) continue;
            ushort stack = (ushort)Math.Clamp(rng.Next(1, level >= 68 ? 5 : 3), 1, Math.Max(1, (int)bt.MaxStack));
            var item = ItemCatalog.Create(bt) with { StackSize = stack, Identified = true };
            result.Add(new InventoryItem((uint)(100 + i), (byte)i, 0, item));
        }
        return result;
    }

    /// <summary>Sends ruleset <c>0xa1</c> (and <c>0xa2</c>), manager-1 encounter gear &amp; rucksack inventories,
    /// and <c>0xa1</c> subs 0, 1, 2, 3 to <paramref name="player"/>.</summary>
    async Task SendEncounterState(Player player, Run run, MercenaryTables.Class kind, MercenaryTables.Build build,
        IReadOnlyList<VisibleItem> gear)
    {
        if (MercenaryTables.EncounterRulesRow is not { } encounter) return;
        lock (sentEncounterData)
        {
            if (sentEncounterData.TryGetValue(player, out _)) return;
            sentEncounterData.Add(player, new object());
        }
        await EnsureAreaRulesSent(player);

        bool firstForPlayer;
        lock (toldEncounter)
        {
            firstForPlayer = !toldEncounter.TryGetValue(player, out _);
            if (firstForPlayer) toldEncounter.Add(player, new object());
        }
        if (firstForPlayer)
            await Send(player, InstanceProtocol.AddRuleset(encounter), $"0x001f ruleset MercenaryEncounterRules (row {encounter})");

        // 1. Register manager-1 encounter gear inventory (0x640) and optional rucksack (0x641) BEFORE sub 1 references them
        await Send(player, InstanceInventories.AddInventory(
            MercenaryWire.EncounterGearInventoryId,
            InstanceMercenaries.EncounterGearSlot,
            InstanceMercenaries.EncounterInventoryManager,
            run.GearItems),
            $"0x00c0 encounter gear inventory 0x{MercenaryWire.EncounterGearInventoryId:x} ({run.GearItems.Count} items)");

        uint rucksackId = 0;
        if (run.RucksackItems.Count > 0)
        {
            rucksackId = MercenaryWire.EncounterRucksackInventoryId;
            await Send(player, InstanceInventories.AddInventory(
                rucksackId,
                InstanceMercenaries.EncounterRucksackSlot,
                InstanceMercenaries.EncounterInventoryManager,
                run.RucksackItems),
                $"0x00c0 encounter rucksack inventory 0x{rucksackId:x} ({run.RucksackItems.Count} items)");
        }

        // 2. Sub 0: visual overrides (7 bytes per entry)
        var visuals = MercenaryGear.Visuals(gear).Select(v => new InstanceMercenaries.MercenaryVisualOverride(v)).ToArray();
        await Send(player, InstanceMercenaries.EncounterVisuals(MercenaryGear.Attributes(kind), visuals),
            $"0x02c9 ruleset 0xa1 sub 0 ({visuals.Length} visuals)");

        // 3. Sub 1: full BaseMercenaryRecord + wager + warrant flag + gear/rucksack inventory IDs
        var baseRec = MercenaryWire.BaseRecord(build.Hash16, run.Level, run.FirstNameHash32, run.SecondNameHash32,
            run.FlavourHash16, run.Skills);
        await Send(player, InstanceMercenaries.EncounterDetails(baseRec, run.WagerCost, run.IsWarrantCall,
            MercenaryWire.EncounterGearInventoryId, rucksackId),
            $"0x02c9 ruleset 0xa1 sub 1 ({build.Name} lvl {run.Level}, wager {run.WagerCost}, warrant={run.IsWarrantCall})");

        // 4. Sub 2: opted-in status for this player
        bool optedIn = run.OptedInPlayers.Contains(CharacterName(player), StringComparer.OrdinalIgnoreCase);
        await Send(player, InstanceMercenaries.EncounterOptIn(optedIn), $"0x02c9 ruleset 0xa1 sub 2 (optedIn={optedIn})");

        // 5. Sub 3: wager cost
        await Send(player, InstanceMercenaries.EncounterWagerCost(run.WagerCost), $"0x02c9 ruleset 0xa1 sub 3 (wager {run.WagerCost})");
    }

    void Register(Player player, Run run)
    {
        if (run.Body == 0 || run.Stage is not (Stage.Met or Stage.Talk) || MercenaryTables.ClassOf(run.Class) is not { } kind
            || Npcs.Find(kind.Path) is not { } npc || npc.Id != kind.Path || Monsters.Find(kind.Path) is not { } variety) return;
        lock (player.Npcs) player.Npcs.TryAdd(run.Body, new SpawnedNpc(npc, run.X, run.Y)
            { MonsterBodySize = Math.Clamp(variety.ObjectSize - 1, 0, 127) });
    }

    async Task Enter(Player player)
    {
        // Always initialize 0xa2 roster, Town/Hideout NPCs, or active combat companion on every area entry
        await EnterRoster(player);

        if (Peek(player) is { } existing) RebindWilds(player, existing);
        if (Peek(player) is not { Stage: not Stage.None } run
            || MercenaryTables.ClassOf(run.Class) is not { } kind || MercenaryTables.BuildOf(run.Build) is not { } build) return;

        if (run.Body != 0 && run.Stage is Stage.Met or Stage.Talk or Stage.Countdown)
        {
            var host = player.Instance.World.Runner ?? player;
            if (!host.Area.Monsters.ContainsKey(run.Body) && !host.Area.DormantMonsters.ContainsKey(run.Body))
            {
                lock (run)
                {
                    if (run.Busy) return;
                    run.Busy = true;
                }
                try
                {
                    foreach (var (id, monster) in player.Area.Monsters.ToArray())
                        if (monster.Variety?.Id == kind.Path && !monster.Hostile) await Remove(player, id);
                    if (!await Spawn(player, run, kind, build, (run.X, run.Y), run.Level, run.Seed)) return;
                }
                finally { lock (run) run.Busy = false; }
            }
            Register(player, run);
        }
        if (run.Stage is Stage.Met or Stage.Talk or Stage.Countdown or Stage.Duel)
            await SendEncounterState(player, run, kind, build, GearOf(kind, build, run.Level, run.Seed, run));
    }

    async Task Remove(Player player, uint id)
    {
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        foreach (var member in Members(player))
        {
            member.Area.Monsters.Remove(id);
            lock (member.Npcs) member.Npcs.Remove(id);
        }
        await Everyone(player, member => SendWorldRemoval(member, id, $"0x02bd remove mercenary 0x{id:x}"));
    }

    static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i] == value) return i;
        return -1;
    }

    async Task SetStates(Player player, uint id, int[] values, string what)
    {
        if (!player.Area.Monsters.TryGetValue(id, out var body) || body.Variety is not { } variety
            || States(variety, values) is not { } states) return;
        int component = IndexOf(variety.ComponentsOrBase, "StateMachine");
        if (component < 0) return;
        if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
        foreach (var member in Members(player))
            if (member.Area.Monsters.TryGetValue(id, out var shown)) shown.States = states;
        await Everyone(player, member => !member.Area.Monsters.TryGetValue(id, out var shown) || shown.Asleep ? Task.CompletedTask
            : SendWorldRecord(member, id, InstanceEntities.StateMachineUpdate(id, (byte)component, states),
                $"0x02bc mercenary 0x{id:x} StateMachine {what}"));
    }

    // ---- Talk (0x61 Encounter or 0x60 Hired Loadout) --------------------------------------------------------------------------

    async Task<bool> Talk(Player player, uint id, SpawnedNpc npc)
    {
        // 1. Check if clicking a hired/reserved mercenary (town, hideout, or active companion) -> open MercenaryLoadout (0x7b 0x60)
        if (loadoutNpcs.TryGetValue(player, out var map))
        {
            byte key;
            bool found;
            lock (map) found = map.TryGetValue(id, out key);
            if (found)
            {
                await Send(player, InstanceWaypoints.OpenUi(MercenaryWire.LoadoutPanelKind, id, key),
                    $"0x007b mercenary loadout 0x{id:x} key {key}");
                return true;
            }
        }

        // 2. Otherwise check if clicking the area's encounter mercenary -> open EncounterMercenaryLoadout (0x7b 0x61)
        if (Peek(player) is not { } run || run.Body != id || run.Stage is not (Stage.Met or Stage.Talk)) return false;
        if (!Alive(player)) return true;
        bool first;
        lock (run)
        {
            first = run.Stage == Stage.Met;
            run.Stage = Stage.Talk;
        }
        await Send(player, InstanceWaypoints.OpenUi(MercenaryWire.TalkPanelKind, id), $"0x007b mercenary 0x{id:x} panel");
        await Privately(player, () => SendWorldRecord(player, id,
            MercenaryWire.Mark(id, 0, 0, MercenaryWire.TalkValue), $"0x0285 mercenary 0x{id:x} talk mark"));
        if (first)
        {
            await SetStates(player, id, MercenaryWire.TalkStates, "(1, 0, 1, 1): its panel is open");
            Save(player, run);
        }
        return true;
    }

    // ---- C->S 0x2ca Ruleset 0xa1 (MercenaryEncounterRules) Actions ------------------------------------------------------------

    async Task<bool> Choose(Player player, byte[] payload)
    {
        if (!InstanceMercenaries.TryReadEncounterAction(payload, out var action)) return false;
        if (Peek(player) is not { Stage: Stage.Talk } run) return false;
        if (!Alive(player)) return true;

        // Opt-In is sent by a non-owner party member
        if (action.Kind == InstanceMercenaries.EncounterActionKind.OptIn)
        {
            if (IsEncounterOwner(player, run)) return true;
            string name = CharacterName(player);
            lock (run)
            {
                if (run.Stage != Stage.Talk || run.OptedInPlayers.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
                run.OptedInPlayers.Add(name);
            }
            if (run.WagerCost > 0)
            {
                uint? left = PersonalStashOf(player).TrySpendGold(run.WagerCost);
                if (left is null)
                {
                    lock (run) run.OptedInPlayers.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    return true;
                }
                await Send(player, InstanceEntities.Gold(left.Value), $"0x026e gold {left.Value} after mercenary opt-in wager");
            }
            Save(player, run);
            await Send(player, InstanceMercenaries.EncounterOptIn(true), "0x02c9 ruleset 0xa1 sub 2 opted in");
            return true;
        }

        // Take Item, Rematch, Hire: owner-only in retail 3.29 (IsMapOwner / 0xbd0b7d78)
        if (!IsEncounterOwner(player, run)) return true;

        if (action.Kind == InstanceMercenaries.EncounterActionKind.Rematch && (run.Level < 68 || run.IsWarrantCall))
            return true;
        if (action.Kind == InstanceMercenaries.EncounterActionKind.TakeRucksack && run.RucksackItems.Count == 0)
            return true;
        if (action.Kind == InstanceMercenaries.EncounterActionKind.TakeGearItem
            && run.GearItems.Count > 0 && !run.GearItems.Any(i => i.Id == action.ItemId))
            return true;
        if (action.Kind == InstanceMercenaries.EncounterActionKind.Hire)
        {
            if (!CanHavePermanentFollower(player)) return true;
            var roster = LoadRoster(player);
            if (FirstFreePermanentKey(roster) is null) return true;
        }

        // Atomically claim Stage.Talk before deducting wager gold so rapid duplicate 0x2ca packets cannot double-charge
        lock (run)
        {
            if (run.Stage != Stage.Talk || run.Busy) return true;
            run.Busy = true;
        }
        uint wagerPaid = 0;
        uint? remainingGold = null;
        try
        {
            if (run.WagerCost > 0)
            {
                remainingGold = PersonalStashOf(player).TrySpendGold(run.WagerCost);
                if (remainingGold is null) return true;
                wagerPaid = run.WagerCost;
            }
            lock (run)
            {
                run.Stage = Stage.Countdown;
                run.Clock = 0;
                run.Steps = 0;
                run.DuelInitiator = CharacterName(player);
                run.WagerPaidByInitiator = wagerPaid;
                run.ShouldRecruit = action.ShouldRecruit;
                run.HireJoinImmediately = action.JoinImmediately;
                run.ChosenItemId = action.ItemId;
                run.ChosenOffer = action.Kind switch
                {
                    InstanceMercenaries.EncounterActionKind.TakeGearItem => DuelOfferKind.TakeGearItem,
                    InstanceMercenaries.EncounterActionKind.TakeRucksack => DuelOfferKind.TakeRucksack,
                    InstanceMercenaries.EncounterActionKind.Rematch => DuelOfferKind.Rematch,
                    InstanceMercenaries.EncounterActionKind.Hire => DuelOfferKind.Hire,
                    _ => DuelOfferKind.None,
                };
            }
        }
        finally
        {
            lock (run) run.Busy = false;
        }
        if (remainingGold is uint goldLeft)
            await Send(player, InstanceEntities.Gold(goldLeft),
                $"0x026e gold {goldLeft} after mercenary duel wager ({wagerPaid})");
        await SetStates(player, run.Body, MercenaryWire.CountdownStates, "(7, 0, 1, 1): the duel was chosen");
        Save(player, run);
        return true;
    }

    // ---- Countdown & Duel -----------------------------------------------------------------------------------------------------

    async Task Tick(Player player, double seconds)
    {
        if (!player.Paused && IsMonsterRunner(player) && Peek(player) is { WildsPlanned: false } pending
            && MapDevices.IsMapArea(player.Layout.Hash))
        {
            await PlaceWilds(player, pending, new AreaPopulated(player, pending.Level, pending.Seed, []),
                AreaLeagueStats(player), new Random(pending.Seed ^ PlanTag));
            Save(player, pending);
        }
        if (player.Paused || Peek(player) is not { Stage: Stage.Countdown } run || !IsMonsterRunner(player)) return;
        lock (run)
        {
            if (run.Stage != Stage.Countdown || run.Busy) return;
            run.Busy = true;
        }
        try
        {
            run.Clock += seconds;
            while (run.Steps < MercenaryWire.CountdownFrames.Length
                && run.Clock >= MercenaryWire.CountdownFrames[run.Steps] * MercenaryWire.FrameSeconds)
            {
                int step = run.Steps++;
                uint id = run.Body;
                if (step == 0)
                    await Everyone(player, member => SendWorldRecord(member, id,
                        MercenaryWire.Mark(id, 0, 0, MercenaryWire.CountdownValue),
                        $"0x0285 mercenary 0x{id:x} countdown mark"));
                else if (step == 1) await SetStates(player, id, MercenaryWire.QuietStates, "(7, 0, 0, 1): the countdown");
                else if (!await StartDuel(player, run))
                {
                    run.Steps = step;
                    break;
                }
            }
        }
        finally { lock (run) run.Busy = false; }
    }

    static IReadOnlyList<MonsterKitSkill>? KitOf(Player player, MonsterCatalog.Variety variety, MercenaryTables.Build build,
        byte level, int seed, IReadOnlyList<MercenaryTables.RolledSkill>? rolled = null)
    {
        var effects = rolled is { Count: > 0 }
            ? MercenaryTables.EffectsFromRolled(rolled, encounter: true)
            : MercenaryTables.SkillsOf(build, level, new Random(unchecked(seed * 397 ^ build.Row)));
        var kit = BossKits.KitFromEffects(player, variety, level, effects);
        if (kit.Any(k => k.Attack.Use is null && k.TriggerThreshold == 0)) return kit;
        if (MercenaryTables.MeleeFallbackEffect >= 0)
        {
            var fallback = BossKits.KitFromEffects(player, variety, level, [MercenaryTables.MeleeFallbackEffect, .. effects]);
            if (fallback.Count > 0) return fallback;
        }
        return kit.Count > 0 ? kit : null;
    }

    async Task<bool> StartDuel(Player player, Run run)
    {
        if (MercenaryTables.ClassOf(run.Class) is not { } kind || MercenaryTables.BuildOf(run.Build) is not { } build
            || Monsters.Find(kind.Path) is not { Supported: true } variety || States(variety, MercenaryWire.DuelStates) is not { } duel
            || !Alive(player)) return false;
        uint old = run.Body;
        string displayName = MercenaryTables.FormatName(run.FirstNameHash32, run.SecondNameHash32) ?? build.Name;
        uint id = await SpawnMonster(player, variety, run.Level, MonsterRarity.Unique, run.X, run.Y,
            ownedLooks: GearOf(kind, build, run.Level, run.Seed, run), extraMods: MercenaryEquipment.Effects(run.Equipment), shape: m => m with
            {
                Name = displayName, StateMachine = duel, MercenaryBuild = build.Hash16, HasDialogue = false, NpcFlags = 0,
            }, ownedKit: KitOf(player, variety, build, run.Level, run.Seed, run.Skills));
        if (id == 0) return false;
        if (player.Area.Monsters.TryGetValue(id, out var body)) body.States = duel;
        await Remove(player, old);
        lock (run)
        {
            run.Stage = Stage.Duel;
            run.Body = id;
        }
        Save(player, run);
        context.Log($"{player.Layout.Id}: mercenary 0x{old:x} duels as 0x{id:x} (offer={run.ChosenOffer})");
        return true;
    }

    // ---- Duel Resolution (Win & Loss) -----------------------------------------------------------------------------------------

    async Task OnPlayerDied(Player player, uint experienceLost)
    {
        // Despawn active companion on player death (stays alive in roster, just leaves area)
        await DespawnCompanion(player);

        // If player died during a mercenary duel, wager is forfeited and encounter fails
        if (Peek(player) is not { Stage: Stage.Duel } run) return;
        if (!string.IsNullOrEmpty(run.DuelInitiator)
            && !string.Equals(run.DuelInitiator, CharacterName(player), StringComparison.OrdinalIgnoreCase))
            return;
        uint duelId;
        lock (run)
        {
            if (run.Stage != Stage.Duel) return;
            run.Stage = Stage.Failed;
            duelId = run.Body;
        }
        Save(player, run);
        if (duelId != 0) await Remove(player, duelId);
    }

    void OnKill(KillContext kill)
    {
        if (Peek(kill.Player) is not { Stage: Stage.Duel } run || MercenaryTables.ClassOf(run.Class) is not { } kind
            || kill.VarietyId != kind.Path) return;
        uint bodyId;
        lock (run) bodyId = run.Body;
        if (bodyId != 0 && kill.Player.Area.Monsters.TryGetValue(bodyId, out var body) && !ReferenceEquals(body, kill.Monster)) return;
        lock (run)
        {
            if (run.Stage != Stage.Duel || run.Body != bodyId) return;
            run.Stage = Stage.Hired;
        }
        Save(kill.Player, run);
        kill.Later(() => CompleteDuelWin(kill.Player, run, kill.At));
    }

    async Task CompleteDuelWin(Player player, Run run, (int X, int Y) at)
    {
        if (MercenaryTables.ClassOf(run.Class) is not { } kind || MercenaryTables.BuildOf(run.Build) is not { } build) return;

        // 1. Refund wager gold (+ any map wager return bonus) to duel initiator and any opted-in party members
        int bonusPct = AreaLeagueStats(player).GetValueOrDefault(Stat.MapMercenaryWagerGoldReturnedPlusPct);
        if (run.WagerPaidByInitiator > 0)
        {
            uint refund = (uint)Math.Max(0, Math.Round(run.WagerPaidByInitiator * (1.0 + bonusPct / 100.0)));
            var initiator = Members(player).FirstOrDefault(m =>
                string.Equals(CharacterName(m), run.DuelInitiator, StringComparison.OrdinalIgnoreCase)) ?? player;
            uint total = PersonalStashOf(initiator).AddGold(refund);
            await Send(initiator, InstanceEntities.Gold(total), $"0x026e refund mercenary wager {refund}g (total {total})");
        }
        if (run.WagerCost > 0 && run.OptedInPlayers.Count > 0)
        {
            uint refund = (uint)Math.Max(0, Math.Round(run.WagerCost * (1.0 + bonusPct / 100.0)));
            foreach (var opted in Members(player).Where(m => run.OptedInPlayers.Contains(CharacterName(m), StringComparer.OrdinalIgnoreCase)))
            {
                uint total = PersonalStashOf(opted).AddGold(refund);
                await Send(opted, InstanceEntities.Gold(total), $"0x026e refund opt-in mercenary wager {refund}g (total {total})");
            }
        }

        // Clean up encounter manager-1 inventories on all clients in the instance
        await Everyone(player, async member =>
        {
            await Send(member, InstanceInventories.RemoveInventory(MercenaryWire.EncounterGearInventoryId, InstanceMercenaries.EncounterInventoryManager),
                "0x00c1 remove encounter gear inventory");
            if (run.RucksackItems.Count > 0)
                await Send(member, InstanceInventories.RemoveInventory(MercenaryWire.EncounterRucksackInventoryId, InstanceMercenaries.EncounterInventoryManager),
                    "0x00c1 remove encounter rucksack inventory");
        });

        var rng = AreaRng(player);

        // 2. Grant offer reward
        switch (run.ChosenOffer)
        {
            case DuelOfferKind.TakeGearItem:
                if (run.GearItems.FirstOrDefault(i => i.Id == run.ChosenItemId) is { } chosen)
                    await DropOrGiveReward(player, chosen.Item with { Identified = true }, at, rng, "Mercenary Take Item reward");
                break;

            case DuelOfferKind.TakeRucksack:
                foreach (var entry in run.RucksackItems)
                    await DropOrGiveReward(player, entry.Item, at, rng, "Mercenary Rucksack reward");
                break;

            case DuelOfferKind.Rematch:
                if (CreateWarrantItem(run, build) is { } warrantItem)
                    await DropOrGiveReward(player, warrantItem, at, rng, "Mercenary Warrant rematch reward");
                break;

            case DuelOfferKind.Hire:
                await CompletePermanentHire(player, run, kind, build, at);
                return;
        }

        // 3. For non-Hire duels: if ShouldRecruit was checked, recruit as temporary ally (key 3) AND spawn allied body
        if (run.ShouldRecruit)
        {
            await CompleteTemporaryRecruit(player, run, kind, build, at);
            return;
        }

        // Otherwise still spawn the allied body at the duel spot (for non-recruit completions / legacy test expectations)
        await SpawnLegacyAlliedBody(player, run, kind, build, at);
    }

    async Task CompletePermanentHire(Player player, Run run, MercenaryTables.Class kind, MercenaryTables.Build build, (int X, int Y) at)
    {
        var inv = ItemsOf(player);
        byte? assignedKey = null;
        HiredMercenary? hiredRecord = null;
        IReadOnlyList<InventoryItem> placedItems = [];
        IReadOnlyList<uint> oldIds = [];

        Saves.Atomically(() =>
        {
            MutateRoster(player, roster =>
            {
                assignedKey = FirstFreePermanentKey(roster);
                if (assignedKey is not byte key) return;
                hiredRecord = new HiredMercenary
                {
                    Key = key,
                    ClassRow = run.Class,
                    BuildRow = run.Build,
                    Level = run.Level,
                    Seed = run.Seed,
                    FirstNameHash32 = run.FirstNameHash32,
                    SecondNameHash32 = run.SecondNameHash32,
                    FlavourHash16 = run.FlavourHash16,
                    Skills = run.Skills,
                    Dead = false,
                    ReviveCost = MercenaryTables.ReviveGoldCost(run.Level, build.Infamous),
                };
                roster.Slots[key] = hiredRecord;
                if (run.HireJoinImmediately) roster.InvitedKey = key;
            });

            if (assignedKey is byte k && PlayerInventories.MercenaryRowForKey(k) is byte row)
            {
                var toPlace = run.GearItems.Count > 0 ? run.GearItems : MercenaryEquipment.ToInventoryItems(run.Equipment);
                (oldIds, placedItems) = inv.ReplaceRow(row, [.. toPlace.Select(i => (i.X, i.Y, i.Item))]);
            }
        });

        if (assignedKey is not byte slotKey || hiredRecord is null) return;

        if (PlayerInventories.MercenaryRowForKey(slotKey) is byte mercRow)
        {
            await Send(player, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(mercRow), inv.NextRevision(mercRow), oldIds, placedItems),
                $"0x0041 populate hired mercenary inventory row {mercRow} ({placedItems.Count} items)");
        }

        await Send(player, InstanceMercenaries.AreaInsert(hiredRecord.ToWireRecord()),
            $"0x02c9 ruleset 0xa2 sub 3 insert permanent mercenary key {slotKey}");

        if (run.HireJoinImmediately)
        {
            await Send(player, InstanceMercenaries.AreaSetInvited(slotKey),
                $"0x02c9 ruleset 0xa2 sub 6 invited key {slotKey}");
            await SpawnActiveCompanionIfEligible(player, LoadRoster(player), at);
            if (companions.TryGetValue(player, out var comp))
            {
                lock (run) run.Hired = comp.EntityId;
                Save(player, run);
            }
        }
    }

    async Task CompleteTemporaryRecruit(Player player, Run run, MercenaryTables.Class kind, MercenaryTables.Build build, (int X, int Y) at)
    {
        bool isMap = MapDevices.IsMapArea(player.Layout.Hash);
        var inv = ItemsOf(player);
        HiredMercenary tempRecord = null!;
        bool hadOldTemp = false;
        IReadOnlyList<uint> oldIds = [];
        IReadOnlyList<InventoryItem> placedItems = [];

        Saves.Atomically(() =>
        {
            MutateRoster(player, roster =>
            {
                hadOldTemp = roster.Slots.ContainsKey(3);
                tempRecord = new HiredMercenary
                {
                    Key = 3,
                    ClassRow = run.Class,
                    BuildRow = run.Build,
                    Level = run.Level,
                    Seed = run.Seed,
                    FirstNameHash32 = run.FirstNameHash32,
                    SecondNameHash32 = run.SecondNameHash32,
                    FlavourHash16 = run.FlavourHash16,
                    Skills = run.Skills,
                    Dead = false,
                    ReviveCost = MercenaryTables.ReviveGoldCost(run.Level, build.Infamous),
                    RemainingAreas = isMap ? 0 : 3, // Current map only (expires on leaving map), or 3 campaign zones
                    TemporaryMapOnly = isMap,
                    LastAreaId = player.Layout.Id,
                    LastAreaSeed = player.Seed,
                };
                roster.Slots[3] = tempRecord;
                // Only set invited to temporary if no permanent mercenary is currently invited
                if (roster.InvitedKey is null || !roster.Slots.ContainsKey(roster.InvitedKey.Value))
                    roster.InvitedKey = 3;
            });

            var toPlace = run.GearItems.Count > 0 ? run.GearItems : MercenaryEquipment.ToInventoryItems(run.Equipment);
            (oldIds, placedItems) = inv.ReplaceRow(PlayerInventories.MercenaryTemporary, [.. toPlace.Select(i => (i.X, i.Y, i.Item))]);
        });

        await Send(player, InstanceItems.InventoryChange(
            PlayerInventories.InventoryId(PlayerInventories.MercenaryTemporary),
            inv.NextRevision(PlayerInventories.MercenaryTemporary), oldIds, placedItems),
            $"0x0041 populate MercenaryTemporary ({placedItems.Count} items)");

        if (hadOldTemp)
            foreach (var pkt in InstanceMercenaries.AreaReplacePackets(tempRecord.ToWireRecord()))
                await Send(player, pkt, "0x02c9 ruleset 0xa2 replace temporary mercenary key 3 (sub 1+3)");
        else
            await Send(player, InstanceMercenaries.AreaInsert(tempRecord.ToWireRecord()),
                "0x02c9 ruleset 0xa2 sub 3 insert temporary mercenary key 3");

        var latest = LoadRoster(player);
        if (latest.InvitedKey == 3)
        {
            await Send(player, InstanceMercenaries.AreaSetInvited(3), "0x02c9 ruleset 0xa2 sub 6 invited temporary key 3");
            await SpawnActiveCompanionIfEligible(player, latest, at);
            if (companions.TryGetValue(player, out var comp))
            {
                // Keep MonsterOrigins entry too so instance-level checks see AlliedTeam / HiredStates
                if (player.Area.Monsters.TryGetValue(comp.EntityId, out var m) && m.Variety is { } v)
                {
                    string displayName = MercenaryTables.FormatName(run.FirstNameHash32, run.SecondNameHash32) ?? build.Name;
                    player.Instance.World.MonsterOrigins[comp.EntityId] = new(
                        InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(v.Id, run.Level)),
                        new MonsterObject(at.X, at.Y, m.Life, v.ComponentsOrBase, Rarity: MonsterRarity.Unique,
                            StateMachine: m.States, Team: MercenaryWire.AlliedTeam)
                        { Name = displayName, MercenaryBuild = build.Hash16 },
                        (ushort)v.Row, run.Level, MonsterRarity.Unique);
                }
                lock (run) run.Hired = comp.EntityId;
                Save(player, run);
                return;
            }
        }

        await SpawnLegacyAlliedBody(player, run, kind, build, at);
    }

    async Task SpawnLegacyAlliedBody(Player player, Run run, MercenaryTables.Class kind, MercenaryTables.Build build, (int X, int Y) at)
    {
        if (Monsters.Find(kind.AlliedPath) is not { Supported: true } allied
            || States(allied, MercenaryWire.HiredStates) is not { } hired) return;
        string displayName = MercenaryTables.FormatName(run.FirstNameHash32, run.SecondNameHash32) ?? build.Name;
        uint id = await SpawnMonster(player, allied, run.Level, MonsterRarity.Unique, at.X, at.Y, friendly: true, instanceOwned: true,
            scripted: true, ownedLooks: GearOf(kind, build, run.Level, run.Seed, run), shape: m => m with
            {
                Name = displayName, Team = MercenaryWire.AlliedTeam, StateMachine = hired, MercenaryBuild = build.Hash16, NpcFlags = 0, InteractionFlags = 1,
            });
        if (id == 0) return;
        if (player.Area.Monsters.TryGetValue(id, out var body)) body.States = hired;
        lock (run) run.Hired = id;
        Save(player, run);
    }

    static async Task DropOrGiveReward(Player player, Item item, (int X, int Y) at, Random rng, string log)
    {
        var dropAt = ScatterDropPosition(player, at, rng);
        var ground = new WorldItemObject(dropAt.X, dropAt.Y, item);
        uint id = player.Instance.Ground.Add(ground, player);
        await SendGroundItem(player, id, ground, log, justDropped: true);
    }
}
