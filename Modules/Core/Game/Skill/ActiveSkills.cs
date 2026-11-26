using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>
    /// The player's skills as the client holds them (its ActiveSkillManager, 11-skills.md §1): each skill's gem with its
    /// supports (SupportedGem) and the stats it is cast with. They are worked out by the Stats phase's skill build
    /// (PlayerSkills) when what they are made from changed, and a use reads them instead of working them out again, as the
    /// client's own use reads its skill's state. Current while the stats are the graph's last step's (its Values) and the
    /// equipment is unchanged; otherwise a use works them out as before.
    /// </summary>
    sealed class ActiveSkillSet(IReadOnlyDictionary<uint, int> values, PlayerInventories items, long itemChanges,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)[] gems, Dictionary<ushort, SupportedGem> supported,
        IReadOnlyList<(uint, int)>? final)
    {
        public readonly IReadOnlyDictionary<uint, int> Values = values;
        public readonly PlayerInventories Items = items;
        public readonly long ItemChanges = itemChanges;
        public readonly (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)[] Gems = gems;
        public readonly Dictionary<ushort, SupportedGem> Supported = supported;
        // Where the skills come from (PlayerSkillSourcesLocked): made on the first use, kept while the gems are.
        public IReadOnlyList<PlayerSkillSource>? Sources;
        // The final stats (FinalPlayerStats) and those a skill's area and hits start from (AreaPlayerStats), made on the
        // first use when the build did not leave them.
        public IReadOnlyList<(uint, int)>? Final = final;
        public Dictionary<uint, int>? AreaStats;
        // Why the actor may not use a skill now (ActorSkillDisabledCore), by effect and skill: the client's usable flag.
        public readonly Dictionary<(int Effect, ushort? Skill), string?> ActorDisabled = [];
        // A cast time without a skill (SpellTimingFor of an effect alone), by effect; a skill's is its graph's.
        public readonly Dictionary<int, SpellCastTiming?> SpellTimings = [];
        // A gem's skill effect cosmetic (SkillArtVariation), by gem.
        public readonly Dictionary<uint, int> ArtVariations = [];
        // Why a gem's requirements are not met (GemRequirementsUnmet), by gem item.
        public readonly Dictionary<Item, string?> Requirements = new(ReferenceEqualityComparer.Instance);
        // Each skill's own graph (SupportedStats): kept while its gem and supports are, across the player's stat changes.
        public readonly Dictionary<ushort, SkillGraph> Graphs = [];
        // The player's final stats as a map (what the skills' graphs read), and the list it was made from.
        public Dictionary<uint, int>? FinalMap;
        public IReadOnlyList<(uint, int)>? FinalMapOf;

        // The skill's graph, stepped for the player's stats now.
        public SkillGraph SkillGraphOf(Player owner, ushort skillId, SupportedGem supported)
        {
            bool channelled = SkillsFor(owner).IsChannelled(supported.Effect);
            if (!Graphs.TryGetValue(skillId, out var skill) || !ReferenceEquals(skill.Supported, supported) || skill.Channelled != channelled)
                Graphs[skillId] = skill = new(owner, supported, channelled);
            var final = FinalPlayerStatMap(owner, this);
            skill.Step(final, AreaStats ??= SkillArea.WithoutPlayerBase(new(final), SkillArea.BaseIncreased), SkillCostMore(owner, supported.Stats));
            return skill;
        }
        // The same gems with the stats of a later step (a build that found its skills unchanged).
        public ActiveSkillSet WithValues(IReadOnlyDictionary<uint, int> next)
        {
            var later = new ActiveSkillSet(next, Items, ItemChanges, Gems, Supported, null) { Sources = Sources };
            foreach (var (gem, variation) in ArtVariations) later.ArtVariations[gem] = variation;
            // The skills' graphs go on: each one runs again only for the player stats it read that changed (Changed).
            foreach (var (skill, graph) in Graphs) later.Graphs[skill] = graph;
            later.FinalMap = FinalMap;
            return later;
        }
    }

    /// <summary>
    /// A skill's own stat graph (the client's skill in its ActiveSkillManager): its supported gem's stats over the player's
    /// final stats, the same over the area stats (what its area and burning read), its cooldown and its cast time. Each
    /// rule reads the player's stats as inputs (StatGraph.ReadInput with the stat's id), so a change of the player's stats
    /// runs a rule again only when a stat it read changed (<see cref="Changed"/>).
    /// </summary>
    sealed class SkillGraph
    {
        readonly StatGraph graph = new();
        readonly Player owner;
        readonly int merge, area, cooldown, timing;
        public readonly SupportedGem Supported;
        public readonly bool Channelled;
        double costMore = double.NaN;
        (int Support, uint Maximum) manaCostPool = (-1, 0);
        IReadOnlyDictionary<uint, int> costStats;
        object? mapMods;
        IReadOnlyDictionary<uint, int> player = new Dictionary<uint, int>(), areaPlayer = new Dictionary<uint, int>();
        Dictionary<uint, int> areaWritten = [];
        SkillStatValues? stats, areaStats;
        public SingleUseCooldown? Cooldown { get; private set; }
        public SpellCastTiming? Timing { get; private set; }

        public SkillGraph(Player owner, SupportedGem supported, bool channelled)
        {
            (this.owner, Supported, Channelled) = (owner, supported, channelled);
            costStats = supported.Stats;
            int Tracked(IReadOnlyDictionary<uint, int> from, uint key)
            {
                StatGraph.ReadInput((int)key);
                return from.GetValueOrDefault(key);
            }
            // The skill's own stats and costs over the player's (Merged): written into the graph's values.
            var scope = SkillCostScopeFor(SkillsFor(owner), supported);
            Dictionary<uint, int> Merge(IReadOnlyDictionary<uint, int> over, double cost)
            {
                var written = new Dictionary<uint, int>();
                MergeSkillStatsInto(key => written.TryGetValue(key, out int own) ? own : Tracked(over, key),
                    (key, value) => written[key] = value, costStats, Channelled, cost, scope);
                return written;
            }
            merge = graph.AddRule(view => { foreach (var (key, value) in Merge(player, costMore)) view[key] = value; });
            area = graph.AddRule(_ => areaWritten = Merge(areaPlayer, 1));
            cooldown = graph.AddRule(_ => Cooldown = SkillsFor(owner).EffectId(Supported.Effect) == MadnessEffect ? null
                : CooldownPlan(owner, Supported, new TrackedStats(player)));
            timing = graph.AddRule(_ =>
            {
                var merged = Merge(player, 1);
                int Own(uint key) => Supported.Stats.GetValueOrDefault(key);
                Timing = SpellTiming(owner, Supported.Effect, key => Tracked(player, key), Own,
                    key => merged.TryGetValue(key, out int value) ? value : Tracked(player, key));
            });
        }

        /// <summary>The player stats that changed (the player graph's step): the rules run again for those they read.</summary>
        public void Changed(IEnumerable<uint> ids) { foreach (uint id in ids) graph.MarkInput((int)id); }

        public void Step(IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int> areaPlayerStats, double cost)
        {
            (player, areaPlayer) = (playerStats, areaPlayerStats);
            if (cost != costMore) { costMore = cost; graph.MarkRule(merge); }
            var nextPool = (Support: ManaCostPool(owner, Supported),
                Maximum: Supported.Stats.GetValueOrDefault(Stat.BaseManaCostPct) > 0 ? owner.Pools.Mana : 0u);
            if (manaCostPool != nextPool)
            {
                manaCostPool = nextPool;
                costStats = WithManaPoolCost(owner, Supported, Supported.Stats, nextPool.Support);
                graph.MarkRule(merge);
                graph.MarkRule(area);
                graph.MarkRule(timing);
            }
            // The map's cooldown mod (WithMapCooldown) is the area's, not a stat.
            var mods = MapModsOf(owner);
            if (!ReferenceEquals(mods, mapMods)) { mapMods = mods; graph.MarkRule(cooldown); }
            if (graph.Step() || stats is null || !ReferenceEquals(stats.Player, playerStats)) stats = new(graph.Values, playerStats);
            if (areaStats is null || !ReferenceEquals(areaStats.Player, areaPlayerStats) || !ReferenceEquals(areaStats.Own, areaWritten))
                areaStats = new(areaWritten, areaPlayerStats);
        }

        public IReadOnlyDictionary<uint, int> Stats => stats!;
        public IReadOnlyDictionary<uint, int> AreaStats => areaStats!;
    }

    /// <summary>A stat map whose reads are a skill graph rule's inputs (StatGraph.ReadInput); a rule that enumerates it reads
    /// every stat it has.</summary>
    sealed class TrackedStats(IReadOnlyDictionary<uint, int> stats) : IReadOnlyDictionary<uint, int>
    {
        public int this[uint key] { get { StatGraph.ReadInput((int)key); return stats[key]; } }
        public bool TryGetValue(uint key, out int value) { StatGraph.ReadInput((int)key); return stats.TryGetValue(key, out value); }
        public bool ContainsKey(uint key) { StatGraph.ReadInput((int)key); return stats.ContainsKey(key); }
        public IEnumerable<uint> Keys => this.Select(p => p.Key);
        public IEnumerable<int> Values => this.Select(p => p.Value);
        public int Count => stats.Count;
        public IEnumerator<KeyValuePair<uint, int>> GetEnumerator()
        {
            foreach (var pair in stats) { StatGraph.ReadInput((int)pair.Key); yield return pair; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A skill's stats: what its graph wrote, over the player's.</summary>
    sealed class SkillStatValues(IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> player) : IReadOnlyDictionary<uint, int>
    {
        public readonly IReadOnlyDictionary<uint, int> Player = player, Own = own;
        public int this[uint key] => TryGetValue(key, out int value) ? value : throw new KeyNotFoundException();
        public bool TryGetValue(uint key, out int value) => Own.TryGetValue(key, out value) || Player.TryGetValue(key, out value);
        public bool ContainsKey(uint key) => Own.ContainsKey(key) || Player.ContainsKey(key);
        public IEnumerable<uint> Keys => this.Select(p => p.Key);
        public IEnumerable<int> Values => this.Select(p => p.Value);
        public int Count => Player.Count + Own.Keys.Count(k => !Player.ContainsKey(k));
        public IEnumerator<KeyValuePair<uint, int>> GetEnumerator()
        {
            foreach (var pair in Own) yield return pair;
            foreach (var pair in Player) if (!Own.ContainsKey(pair.Key)) yield return pair;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // The player's final stats as a map for the skills' graphs; a new step's tells each graph which of them changed.
    static IReadOnlyDictionary<uint, int> FinalPlayerStatMap(Player player, ActiveSkillSet set)
    {
        var final = set.Final ??= [.. FinalPlayerStatsCore(player)];
        if (set.FinalMap is { } known && ReferenceEquals(set.FinalMapOf, final)) return known;
        var next = final.ToDictionary(p => p.Item1, p => p.Item2);
        if (set.FinalMap is { } before)
        {
            var changed = next.Where(p => !before.TryGetValue(p.Key, out int was) || was != p.Value).Select(p => p.Key)
                .Concat(before.Keys.Where(k => !next.ContainsKey(k))).ToList();
            foreach (var graph in set.Graphs.Values) graph.Changed(changed);
        }
        set.FinalMapOf = final;
        return set.FinalMap = next;
    }

    static readonly Store<Player, ActiveSkillSet> ActiveSkillSets = new();

    // The skill build's results, kept for the uses until the next build.
    static void KeepActiveSkills(Player player, SkillBuildScope build, IReadOnlyList<(uint, int)> final)
    {
        if (!FrameStatsOf.TryGetValue(player, out var frame) || !frame.ByFrame || frame.Graph is not { } graph || build.Gems is null) return;
        var items = ItemsOf(player);
        ActiveSkillSets.AddOrUpdate(player, new ActiveSkillSet(graph.Values, items, items.Changes, build.Gems,
            new(build.Supported), [.. final]));
    }

    // A build found the skills unchanged (PlayerSkillsInputs): their gems stay, with the stats of this step.
    static void KeepActiveSkills(Player player)
    {
        if (!ActiveSkillSets.TryGetValue(player, out var set) || !FrameStatsOf.TryGetValue(player, out var frame) || !frame.ByFrame
            || frame.Graph is not { } graph || ReferenceEquals(set.Values, graph.Values)) return;
        var items = ItemsOf(player);
        if (ReferenceEquals(items, set.Items) && items.Changes == set.ItemChanges) ActiveSkillSets.AddOrUpdate(player, set.WithValues(graph.Values));
    }

    // The player's skills as the last build left them, while still current.
    static ActiveSkillSet? CurrentActiveSkills(Player player)
    {
        if (!ActiveSkillSets.TryGetValue(player, out var set)) return null;
        if (!FrameStatsOf.TryGetValue(player, out var frame) || !frame.ByFrame || frame.Graph is not { } graph
            || !ReferenceEquals(graph.Values, set.Values)) return null;
        var items = ItemsOf(player);
        return ReferenceEquals(items, set.Items) && items.Changes == set.ItemChanges ? set : null;
    }

    // In the Stats phase, after the skill build: what a use of each skill reads, worked out now (the client sets its skills'
    // usable flags when their stats change, 0x141ef1aa0), so the use only reads it.
    internal static void ReadyActiveSkills(Player player)
    {
        if (CurrentActiveSkills(player) is not { } set) return;
        PlayerSkillSourcesLocked(player);
        foreach (var gem in set.Gems)
        {
            ActorSkillInstanceDisabled(player, gem.Effect, gem.Id);
            GemRequirementsUnmet(player, SupportedSkill(player, gem.Id).SourceGem);
            SupportedStats(player, gem.Id);
            CooldownForSkill(player, gem.Id);
            SkillArtVariation(player, gem.Gem);
            if (SkillsFor(player).IsSpell(gem.Effect) && !SkillsFor(player).IsAttack(gem.Effect)) CastMs(player, gem.Effect, gem.Id);
        }
    }

    /// <summary>The final stats a skill's area and hits start from (AreaPlayerStats), not to be changed: the skills'
    /// current ones, or worked out now.</summary>
    public static IReadOnlyDictionary<uint, int> AreaPlayerStatValues(Player player)
    {
        if (CurrentActiveSkills(player) is not { } set) return AreaPlayerStats(player);
        return set.AreaStats ??= SkillArea.WithoutPlayerBase(new(FinalPlayerStatMap(player, set)), SkillArea.BaseIncreased);
    }

    /// <summary>A skill's own stats over the area stats (AreaPlayerStatValues), not to be changed: what its area and
    /// burning read; kept with the skills (ActiveSkills).</summary>
    public static IReadOnlyDictionary<uint, int> AreaSkillStats(Player player, ushort skillId, SupportedGem supported)
    {
        if (BuildScopeOf(player) is { Building: true } || CurrentActiveSkills(player) is not { } set)
            return MergeSkillStats(AreaPlayerStats(player), supported.Stats);
        return set.SkillGraphOf(player, skillId, supported).AreaStats;
    }
}
