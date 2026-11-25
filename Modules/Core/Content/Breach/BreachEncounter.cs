using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using static GameServer.Modules.Breach.BreachTables;

namespace GameServer.Modules.Breach;

/// <summary>
/// An area instance's Unstable Breaches: rolled and placed once when the instance's population is placed, kept by the
/// instance (its ContentState, so a module reload carries on), stepped by the connection that runs the instance's
/// monsters, and shown to whoever enters: the Initiators are area objects (a later client is given them as they are),
/// and the ruleset row, its latest message per Breach and the environment override go to each entering client again.
/// A touch opens the Breach: the arena's radius ramp (BreachRulesets), the wave of the Breach packs at the area's level,
/// the environment override, the enrage with breach_enrage, then the close and the removal of the Initiator and what is
/// left of its wave. The Hive is not built: an area whose roll gives a Hive gets no Breach.
/// </summary>
public sealed class BreachEncounter
{
    const string StateKey = "Breach.Encounter";
    const uint RollSalt = 0x42726561;

    /// <summary>One Breach of the area.</summary>
    public sealed class Breach
    {
        /// <summary>Its place in the area (the ruleset message's index byte), in placement order.</summary>
        public byte Index { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public uint Initiator { get; set; }
        public int State { get; set; }
        /// <summary>When the state was entered (area time).</summary>
        public double Since { get; set; }
        public double? TouchedAt { get; set; }
        /// <summary>How many of the ramp's lines are due and sent (the latest one goes out).</summary>
        public int Ramp { get; set; }
        /// <summary>The state of the latest ruleset line sent (-1 before the first); a state-1 or state-2 line is the
        /// ramp's line <see cref="Ramp"/> - 1.</summary>
        public int Line { get; set; } = -1;
        /// <summary>How many wave members are accounted for, and how many packs were drawn (the draws' seed).</summary>
        public int Spawned { get; set; }
        public int Packs { get; set; }
        public List<uint> Monsters { get; set; } = [];
        public bool Gone { get; set; }
        [JsonIgnore] public bool Open => State is Expanding or Enraged;
    }

    /// <summary>The instance's Breaches.</summary>
    public sealed class Encounter
    {
        public List<Breach> Breaches { get; set; } = [];
        public bool EnvironmentOn { get; set; }
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>The cells a Breach's wave may stand on: reachable from its cell. Not kept: learnt again after a reload.</summary>
        [JsonIgnore] internal Dictionary<byte, bool[]?> Reach { get; } = [];
    }

    readonly ConditionalWeakTable<InstanceState, Encounter> encounters = new();
    readonly ConcurrentDictionary<(string Area, byte Level), AreaInfo?> areaInfos = new();
    readonly JsonSerializerOptions serialization = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    static readonly ConditionalWeakTable<MonsterCatalog.Variety, MonsterCatalog.Variety> Transitioned = new();

    /// <summary>The module unloads: the instances keep their Breaches as text, for the next load.</summary>
    public void Clear()
    {
        encounters.Clear();
        areaInfos.Clear();
    }

    static bool Enrage => SystemSettings.Default.Get("breach_enrage") != 0;

    /// <summary>The instance's Breaches, or null when it placed none.</summary>
    public Encounter? Of(InstanceState instance)
    {
        if (encounters.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return encounters.GetValue(instance, _ => JsonSerializer.Deserialize<Encounter>(text, serialization) ?? new());
    }

    void Save(InstanceState instance, Encounter encounter) =>
        instance.ContentState[StateKey] = JsonSerializer.Serialize(encounter, serialization);

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> Members(Player player) => [.. player.Instance.PlayersExcept(player.Id).Prepend(player)
        .Where(c => Current(c) && ReferenceEquals(c.Instance, player.Instance)).Distinct()];

    static byte AreaLevel(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    /// <summary>
    /// The instance's population is placed (GameEvents.AreaPopulated, once per instance): rolls its Breaches and places
    /// them at <see cref="AreaPopulation.EncounterSpot"/>, then creates their Initiators. Returns why none was placed,
    /// null when one was.
    /// </summary>
    public async Task<string?> Populate(AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance) return "no instance";
        if (Of(instance) is not null) return "the instance has its Breaches already";
        if (RulesetRow is not { } row || Initiator is not { } initiator) return "the client data has no Breach ruleset or Initiator";
        var (components, states, _) = initiator;
        var layout = player.Layout;
        if (!MapDevices.IsMapArea(layout.Hash)) return "not a map";
        if (layout.Walkable is not { } walkable) return "no walk map";
        var (count, roll) = Count(area);
        if (count <= 0) return $"the roll gave none ({roll})";
        var rng = new Random(unchecked(area.Seed ^ (int)RollSalt));
        var taken = area.Taken.ToList();
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        var encounter = new Encounter();
        double now = AreaTime(player);
        for (int i = 0; i < Math.Min(count, 32); i++)
        {
            if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY), transitions, taken, rng)
                is not { } site) break;
            taken.Add(site);
            encounter.Breaches.Add(new Breach
            {
                Index = (byte)i, X = site.X, Y = site.Y, Initiator = instance.World.TakeObjectId(), Since = now, Line = Inactive,
            });
        }
        if (encounter.Breaches.Count == 0) return "no open ground for a Breach";
        encounters.AddOrUpdate(instance, encounter);
        Save(instance, encounter);
        var members = Members(player);
        if (!members.Contains(player)) members.Insert(0, player);
        // The ruleset first: the 0x2c9 messages for its row find no ruleset on the client without it.
        await Broadcast(members, InstanceProtocol.AddRuleset(row), $"0x001f ruleset ExpandingBrequelEncounterRules (row {row})");
        uint type = InstanceEntities.ObjectTypeHash(InitiatorType);
        foreach (var breach in encounter.Breaches)
        {
            var blob = new QuestObject(breach.X, breach.Y, components) { StateMachine = states.Set("encounter_state", Inactive) }.Blob();
            await SendWorldCreate(player, breach.Initiator, () => InstanceEntities.CreateObject(breach.Initiator, type, blob),
                $"0x02bb Breach {breach.Index} Initiator 0x{breach.Initiator:x} at ({breach.X}, {breach.Y})", wake: true, awaitBirth: false);
            await Broadcast(members, LineFor(row, breach, breach.Line), $"0x02c9 Breach {breach.Index} ruleset idle");
        }
        Console.WriteLine($"[breach] {layout.Id}: {roll} gave {count}; placed {encounter.Breaches.Count}: " +
            string.Join(", ", encounter.Breaches.Select(b => $"({b.X}, {b.Y})")));
        return null;
    }

    /// <summary>How many Unstable Breaches the area gets: Game_LEAGUE_CHANCES's test chance when it names Breach, else
    /// the client's own roll over the area's stats (<see cref="UnstableBreaches"/>).</summary>
    (int Count, string Roll) Count(AreaPopulated area)
    {
        var player = area.Player;
        if (LeagueChances.Current.LeagueOf(LeagueChances.Breach) is { } league && LeagueChances.Forced(league.Id) is int forced)
            return (LeagueChances.Current.Count(player.Layout.Id, LeagueChances.Breach, new Random(unchecked(area.Seed ^ (int)RollSalt))),
                $"Game_LEAGUE_CHANCES {forced}%");
        var info = areaInfos.GetOrAdd((player.Layout.Id, AreaLevel(player)), static k =>
        {
            try { return AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level); }
            catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException) { return null; }
        });
        if (info is null) return (0, "area row not read");
        var stats = player.Instance.KeptAreaStats?.ToDictionary(s => (int)s.Key, s => s.Value) ?? [];
        return (UnstableBreaches(info with { Stats = stats }, new LevelRandom(unchecked((uint)area.Seed ^ RollSalt), false)), "the area's roll");
    }

    /// <summary>
    /// The client's Breach count for an area (the level generator's CoreLeagues 7 step, 0x142050dc0, as
    /// ExtraTerrainFeatures.Roll mirrors it), on the encounter's own draws: the outer area and league exclusions, the
    /// league roll over the area's stats (ExtraTerrainFeatures.LeagueCount: map_disable_leagues,
    /// atlas_keystone_no_extra_content, map_nuke_everything, the row's disable and chance stats …); a rolled one is a
    /// Hive (none here) by the Hive stats or half the time, else one Unstable Breach; then
    /// map_contains_additional_unstable_breaches and the scarabs' 1 / 2 / 3 / 10 more.
    /// </summary>
    public static int UnstableBreaches(AreaInfo area, LevelRandom rng)
    {
        if (area.IsTown || area.RandomnessDisabled || area.AreaType is 0x13 or 0x20 or 0xb || area.Hash16 is 0x8f53 or 0x7757
            || area.LeagueChance is null) return 0;
        int S(uint id) => area.Stat((int)id);
        if (S(Stat.IsBlightedMap) != 0 || S(Stat.MapNukeEverything) != 0 || S(Stat.GoAwayOtherLeagues) != 0
            || S(Stat.MapDisableBreachAbyss) != 0 || S(Stat.AtlasKeystoneNoExtraContent) != 0
            || (S(Stat.MapDisableLeagues) & (1 << LeagueChances.Breach)) != 0) return 0;
        bool Roll(int chance) => chance > 0 && (chance >= 100 || rng.Next(100u) < (uint)chance);
        int extra = Math.Max(0, S(Stat.MapContainsAdditionalUnstableBreaches));
        bool rolled = ExtraTerrainFeatures.LeagueCount(area, rng, LeagueChances.Breach) != 0;
        if (rolled)
        {
            bool first = S(Stat.MapFirstBrequelEncounter) != 0 && S(Stat.MapOnlyUnstableBreaches) == 0;
            bool onlyHives = !(S(Stat.MapOnlyBreachHives) == 0 || S(Stat.MapOnlyUnstableBreaches) != 0);
            bool hive = S(Stat.MapContainsBreachHive) != 0 || first
                || (extra == 0 && (S(Stat.MapOnlyUnstableBreaches) == 0 || S(Stat.MapOnlyBreachHives) != 0)
                    && (onlyHives || rng.Next(100u) < 50));
            if (hive) return 0;
        }
        int count = (rolled ? 1 : 0) + extra;
        if (extra != 0 || rolled)
        {
            if (Roll(S(Stat.MapBreachPctChanceFor10AdditionalBreach))) count += 10;
            if (Roll(S(Stat.MapBreachPctChanceFor3AdditionalBreach))) count += 3;
            if (Roll(S(Stat.MapBreachPctChanceFor2AdditionalBreach))) count += 2;
            if (Roll(S(Stat.MapBreachPctChanceFor1AdditionalBreach))) count += 1;
        }
        return count;
    }

    /// <summary>The ruleset row for an entering client before the area's objects (GameEvents.EnterMessages): the
    /// Initiators it is given as area objects read it.</summary>
    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player) =>
        player.Instance is { } instance && RulesetRow is { } row && Of(instance) is { } encounter && encounter.Breaches.Any(b => !b.Gone)
            ? [($"0x001f ruleset ExpandingBrequelEncounterRules (row {row})", InstanceProtocol.AddRuleset(row))] : [];

    /// <summary>A player entered the area (GameEvents.PlayerEntered: a party member, a re-entry through a portal or
    /// after a death): the ruleset row, each standing Breach's latest ruleset message and the environment override
    /// while one is open. The Initiators come with the area's objects.</summary>
    public async Task Enter(Player player)
    {
        if (player.Instance is not { } instance || RulesetRow is not { } row || Of(instance) is not { } encounter
            || encounter.Breaches.All(b => b.Gone)) return;
        await encounter.Gate.WaitAsync();
        try
        {
            await Send(player, InstanceProtocol.AddRuleset(row), $"0x001f ruleset ExpandingBrequelEncounterRules (row {row})");
            foreach (var breach in encounter.Breaches.Where(b => !b.Gone && b.Line >= 0))
                await Send(player, LineFor(row, breach, breach.Line), $"0x02c9 Breach {breach.Index} ruleset (state {breach.State})");
            if (encounter.EnvironmentOn && Override(true) is { } on)
                await Send(player, on, "0x00df Breach environment override");
        }
        finally { encounter.Gate.Release(); }
    }

    /// <summary>One frame of the area (GameEvents.AreaTick), on the connection that runs the instance's monsters.</summary>
    public async Task Step(Player runner)
    {
        if (runner.Instance is not { } instance || Of(instance) is not { } encounter || !Current(runner) || runner.Paused
            || !IsMonsterRunner(runner)) return;
        if (!encounter.EnvironmentOn && encounter.Breaches.All(b => b.Gone)) return;
        if (!await encounter.Gate.WaitAsync(0)) return;
        try
        {
            if (await Advance(runner, encounter)) Save(instance, encounter);
        }
        finally { encounter.Gate.Release(); }
    }

    /// <summary>
    /// Advances every Breach: a living player out of the Grace Period within <see cref="TouchRadius"/> of an idle one
    /// opens it (at most <see cref="MaxOpen"/> open at once); an open one sends its ramp and its wave, enrages at the end
    /// of the ramp with breach_enrage (then closes <see cref="EnragedSeconds"/> later) or closes at
    /// <see cref="OpenSeconds"/> without it; a closed one goes <see cref="RemoveAfter"/> later with what is left of its
    /// wave. True when a Breach changed phase or the environment changed.
    /// </summary>
    async Task<bool> Advance(Player runner, Encounter encounter)
    {
        double now = AreaTime(runner);
        var members = Members(runner);
        byte row = RulesetRow!.Value;
        bool changed = false;
        foreach (var breach in encounter.Breaches)
        {
            if (breach.Gone) continue;
            switch (breach.State)
            {
                case Inactive:
                    if (encounter.Breaches.Count(b => !b.Gone && b.Open) >= MaxOpen || !members.Any(m => Touches(m, breach))) break;
                    breach.TouchedAt = now;
                    // The environment first, then the state: the order Delirium's start sends them in.
                    if (!encounter.EnvironmentOn && Override(true) is { } on)
                    {
                        await Broadcast(members, on, $"0x00df Breach environment override [{EnvRow} slot 0], {EnvSeconds:0.0} s");
                        encounter.EnvironmentOn = true;
                    }
                    await SetState(runner, breach, Expanding, now);
                    changed = true;
                    break;
                case Expanding:
                    await Ramp(members, row, breach, now);
                    await SpawnWave(runner, encounter, breach, now);
                    double age = now - breach.TouchedAt!.Value;
                    if (age < ClipActivate) break;
                    // The enrage: the ramp has run out (the radius at its 150 cap), as both touched Breaches of the capture
                    // enrage there.
                    if (Enrage && breach.Ramp >= BreachRulesets.Steps)
                    {
                        await SetState(runner, breach, Enraged, now);
                        await SendLine(members, row, breach, Enraged, "enraged");
                        changed = true;
                    }
                    else if (age >= OpenSeconds)
                    {
                        await SetState(runner, breach, Closing, now);
                        await SendLine(members, row, breach, Closing, "collapsed");
                        changed = true;
                    }
                    break;
                case Enraged:
                    await SpawnWave(runner, encounter, breach, now);
                    if (now - breach.Since < EnragedSeconds) break;
                    await SetState(runner, breach, ClosingEnraged, now);
                    await SendLine(members, row, breach, ClosingEnraged, "closing enraged");
                    changed = true;
                    break;
                default:
                    if (now - breach.Since < RemoveAfter) break;
                    await RemoveWave(runner, breach);
                    await SendWorldRemoval(runner, breach.Initiator, $"0x02bd Breach {breach.Index} Initiator 0x{breach.Initiator:x} removed");
                    breach.Gone = true;
                    changed = true;
                    break;
            }
        }
        if (encounter.EnvironmentOn && !encounter.Breaches.Any(b => !b.Gone && b.Open))
        {
            await Broadcast(members, InstanceEnvironments.Data(), "0x00df Breach environment override cleared");
            encounter.EnvironmentOn = false;
            changed = true;
        }
        return changed;
    }

    static bool Touches(Player player, Breach breach)
    {
        if (player.PlayerLife == 0 || InGracePeriod(player)) return false;
        var (x, y) = player.Walk.Position(AreaTime(player));
        return double.Hypot(breach.X - x, breach.Y - y) <= TouchRadius;
    }

    static async Task SetState(Player runner, Breach breach, int state, double now)
    {
        breach.State = state;
        breach.Since = now;
        var (_, states, index) = Initiator!.Value;
        await SendWorldRecord(runner, breach.Initiator, InstanceEntities.StateMachineUpdate(breach.Initiator, index, states.Set("encounter_state", state)),
            $"0x02bc Breach {breach.Index} Initiator 0x{breach.Initiator:x} encounter_state {state}");
    }

    /// <summary>The ramp's latest due line (one every <see cref="RulesetGap"/> seconds from the touch): each line is the
    /// arena as it stands, so a late tick sends only the newest.</summary>
    static async Task Ramp(List<Player> members, byte row, Breach breach, double now)
    {
        if (breach.TouchedAt is not { } touched) return;
        int due = Math.Min(BreachRulesets.Steps, (int)Math.Floor((now - touched) / RulesetGap) + 1);
        if (due <= breach.Ramp) return;
        breach.Ramp = due;
        await SendLine(members, row, breach, Expanding, $"ramp {due}/{BreachRulesets.Steps}");
    }

    static Task SendLine(List<Player> members, byte row, Breach breach, int state, string what)
    {
        breach.Line = state;
        return Broadcast(members, LineFor(row, breach, state), $"0x02c9 Breach {breach.Index} ruleset {what} at ({breach.X}, {breach.Y})");
    }

    /// <summary>This Breach's ruleset line in <paramref name="state"/>, as 0x2c9.</summary>
    static byte[] LineFor(byte row, Breach breach, int state) =>
        InstanceRulesets.Data(row, BreachRulesets.Body(breach.Index, breach.X, breach.Y, state, Math.Max(0, breach.Ramp - 1)));

    /// <summary>The environment override (on: the BreachArtVariations row, slot 0) or the empty list that clears it.</summary>
    static byte[]? Override(bool on) => !on ? InstanceEnvironments.Data() : EnvRow is { } env ? InstanceEnvironments.Data((env, EnvSeconds)) : null;

    /// <summary>A message that is about no object goes to every client of the area by itself, each its own copy: the
    /// writer encrypts the buffer it is given in place.</summary>
    static async Task Broadcast(List<Player> members, byte[] message, string log)
    {
        for (int i = 0; i < members.Count; i++) await Send(members[i], message.ToArray(), i == 0 ? log : null);
    }

    /// <summary>
    /// The wave members due by now: the measured per-second curve (<see cref="WaveRates"/>) integrated to the age, at
    /// most <see cref="MaxWavePerTick"/> a tick. Each draw is a whole pack of the Breach packs (their members, counts and
    /// bosses as the area's packs place them), at the area's monster level with the Breach life scaling, around a cell
    /// up to <see cref="WaveMaxDistance"/> from the Breach that its cell walks to. The members are created transitioned:
    /// every Breach variety inherits BrequelMonster.ot, whose create_untransitioned removes collision and rendering.
    /// </summary>
    static async Task SpawnWave(Player runner, Encounter encounter, Breach breach, double now)
    {
        if (breach.TouchedAt is not { } touched || BreachTables.Packs.Count == 0) return;
        double age = now - touched;
        if (age < 0 || age >= WaveSeconds) return;
        int due = (int)Math.Floor(ExpectedWaveMembers(age));
        if (breach.Spawned >= due) return;
        var layout = runner.Layout;
        if (!encounter.Reach.TryGetValue(breach.Index, out var reach) && layout.Walkable is { } walkable)
            encounter.Reach[breach.Index] = reach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, (breach.X, breach.Y));
        bool Open(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH && layout.WalkableFor(x, y, 1)
            && (reach is null || reach[y * layout.GridW + x]);
        byte level = AreaLevel(runner);
        int scale = LifeScale(level);
        var extra = scale > 0 ? MonsterModSet.None with { Effects = MonsterModEffects.None.Plus(MonsterModCatalog.EncounterLifeStat, scale) } : null;
        int budget = MaxWavePerTick;
        using (HoldMonstersRemaining(runner))
        {
            while (breach.Spawned < due && budget > 0)
            {
                var rng = new Random(unchecked((int)(runner.Seed * 0x9e3779b1u ^ (uint)(breach.Index * 7919) ^ (uint)(breach.Packs * 104729))));
                breach.Packs++;
                var pack = BreachTables.Packs[rng.Next(BreachTables.Packs.Count)];
                double u = rng.NextDouble();
                var rarity = u < NormalShare ? MonsterRarity.Normal : u < NormalShare + MagicShare ? MonsterRarity.Magic : MonsterRarity.Rare;
                (int X, int Y) centre = (breach.X, breach.Y);
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    double angle = rng.NextDouble() * Math.Tau, radius = rng.NextDouble() * WaveMaxDistance;
                    (int X, int Y) at = ((int)Math.Round(breach.X + radius * Math.Cos(angle)), (int)Math.Round(breach.Y + radius * Math.Sin(angle)));
                    if (Open(at.X, at.Y)) { centre = at; break; }
                }
                var spawns = AreaPopulation.AtPacks([(pack, centre.X, centre.Y, rarity)], Open,
                    v => Monsters.Find(v.ToString()) is { Supported: true }, rng.Next(), MapModsOf(runner)?.PackSizeFactor ?? 1);
                breach.Spawned += Math.Max(1, spawns.Count);
                budget -= Math.Max(1, spawns.Count);
                foreach (var spawn in spawns)
                {
                    if (Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) continue;
                    uint id = await SpawnMonster(runner, Risen(variety), level, spawn.Rarity, spawn.X, spawn.Y,
                        alone: false, extraMods: extra, bulk: true);
                    if (id == 0) continue;
                    breach.Monsters.Add(id);
                    // It goes with the Breach that brought it: an instance kept with nobody in it does not keep it
                    // (SavedMonster.Save), so it is never re-created untransitioned.
                    if (runner.Area.Monsters.TryGetValue(id, out var monster)) monster.SummonerId = breach.Initiator;
                }
            }
        }
        await SendRemainingMonsters(runner);
    }

    /// <summary>The variety created in its last Transitionable state: the client's Transitionable defaults num_states to 2
    /// (the ctor 0x141e46340) and runs create_transitioned (EnableRendering, MakeCollidable) only for a create in that
    /// state, while object_types lists 1 for a .ot that gives none (<see cref="MonsterEmerge.RisenState"/>).</summary>
    static MonsterCatalog.Variety Risen(MonsterCatalog.Variety variety) =>
        Transitioned.GetValue(variety, v => v with { TransitionStates = MonsterEmerge.RisenState(v) });

    /// <summary>The close takes the wave's living members with it (no death, no drops, no experience); a corpse is left
    /// to the corpse's own way.</summary>
    static async Task RemoveWave(Player runner, Breach breach)
    {
        var area = runner.Area;
        using (HoldMonstersRemaining(runner))
        {
            foreach (uint id in breach.Monsters)
            {
                bool dormant = false;
                if (!area.Monsters.TryGetValue(id, out var monster))
                {
                    if (!area.DormantMonsters.TryGetValue(id, out monster)) continue;
                    dormant = true;
                }
                if (monster.Dead || monster.Life == 0) continue;
                monster.Life = 0;
                monster.Dead = true;
                monster.Aggro = false;
                monster.ActionGeneration++;
                if (dormant) area.DormantMonsters.Remove(id);
                else area.Monsters.Remove(id);
                runner.Instance.World.MonsterOrigins.TryRemove(id, out _);
                await SendWorldRemoval(runner, id, $"0x02bd Breach {breach.Index} wave monster 0x{id:x} removed");
            }
        }
        breach.Monsters.Clear();
        await SendRemainingMonsters(runner);
    }
}
