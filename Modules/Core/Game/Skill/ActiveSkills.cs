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
        // A spell's cast time (SpellTimingFor), by effect and skill.
        public readonly Dictionary<(int Effect, ushort Skill), SpellCastTiming?> SpellTimings = [];
        // A gem's skill effect cosmetic (SkillArtVariation), by gem.
        public readonly Dictionary<uint, int> ArtVariations = [];
        // Why a gem's requirements are not met (GemRequirementsUnmet), by gem item.
        public readonly Dictionary<Item, string?> Requirements = new(ReferenceEqualityComparer.Instance);
        // Each skill's own graph (SupportedStats): kept while its gem and supports are, across the player's stat changes.
        public readonly Dictionary<ushort, SkillGraph> Graphs = [];
        // The player's final stats as a map (what the skills' graphs read), and the list it was made from.
        public Dictionary<uint, int>? FinalMap;
        public IReadOnlyList<(uint, int)>? FinalMapOf;

        public IReadOnlyDictionary<uint, int> SkillGraphOf(ushort skillId, SupportedGem supported, bool channelled, double costMore,
            IReadOnlyDictionary<uint, int> player)
        {
            if (!Graphs.TryGetValue(skillId, out var skill) || !ReferenceEquals(skill.Supported, supported) || skill.Channelled != channelled)
                Graphs[skillId] = skill = new(supported, channelled);
            return skill.Stats(player, costMore);
        }
        // A skill's cooldown (CooldownForSkill), by skill.
        public readonly Dictionary<ushort, SingleUseCooldown?> Cooldowns = [];
        // A skill's stats over the area stats (AreaSkillStats), by skill.
        public readonly Dictionary<ushort, Dictionary<uint, int>> AreaMerged = [];
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
    /// final stats. Its rule reads each player stat as an input (StatGraph.ReadInput with the stat's id), so a change of the
    /// player's stats runs it again only when a stat it read changed (<see cref="Changed"/>); its values are only the
    /// stats it wrote (its own and its costs), the rest are the player's.
    /// </summary>
    sealed class SkillGraph
    {
        readonly StatGraph graph = new();
        readonly int merge;
        public readonly SupportedGem Supported;
        public readonly bool Channelled;
        double costMore = double.NaN;
        IReadOnlyDictionary<uint, int> player = new Dictionary<uint, int>();
        SkillStatValues? stats;

        public SkillGraph(SupportedGem supported, bool channelled)
        {
            (Supported, Channelled) = (supported, channelled);
            merge = graph.AddRule(view =>
            {
                var written = new Dictionary<uint, int>();
                int Read(uint key)
                {
                    if (written.TryGetValue(key, out int own)) return own;
                    StatGraph.ReadInput((int)key);
                    return player.GetValueOrDefault(key);
                }
                MergeSkillStatsInto(Read, (key, value) => written[key] = value, Supported.Stats, Channelled, costMore);
                foreach (var (key, value) in written) view[key] = value;
            });
        }

        /// <summary>The player stats that changed (the player graph's step): the skill runs again for those it read.</summary>
        public void Changed(IEnumerable<uint> ids) { foreach (uint id in ids) graph.MarkInput((int)id); }

        public IReadOnlyDictionary<uint, int> Stats(IReadOnlyDictionary<uint, int> playerStats, double cost)
        {
            player = playerStats;
            if (cost != costMore) { costMore = cost; graph.MarkRule(merge); }
            if (graph.Step() || stats is null || !ReferenceEquals(stats.Player, playerStats)) stats = new(graph.Values, playerStats);
            return stats;
        }
    }

    /// <summary>A skill's stats: what its graph wrote, over the player's.</summary>
    sealed class SkillStatValues(IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> player) : IReadOnlyDictionary<uint, int>
    {
        public readonly IReadOnlyDictionary<uint, int> Player = player;
        public int this[uint key] => TryGetValue(key, out int value) ? value : throw new KeyNotFoundException();
        public bool TryGetValue(uint key, out int value) => own.TryGetValue(key, out value) || Player.TryGetValue(key, out value);
        public bool ContainsKey(uint key) => own.ContainsKey(key) || Player.ContainsKey(key);
        public IEnumerable<uint> Keys => this.Select(p => p.Key);
        public IEnumerable<int> Values => this.Select(p => p.Value);
        public int Count => Player.Count + own.Keys.Count(k => !Player.ContainsKey(k));
        public IEnumerator<KeyValuePair<uint, int>> GetEnumerator()
        {
            foreach (var pair in own) yield return pair;
            foreach (var pair in Player) if (!own.ContainsKey(pair.Key)) yield return pair;
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
        return set.AreaStats ??= SkillArea.WithoutPlayerBase(
            (set.Final ??= [.. FinalPlayerStatsCore(player)]).ToDictionary(kv => kv.Item1, kv => kv.Item2), SkillArea.BaseIncreased);
    }

    /// <summary>A skill's own stats over the area stats (AreaPlayerStatValues), not to be changed: what its area and
    /// burning read; kept with the skills (ActiveSkills).</summary>
    public static IReadOnlyDictionary<uint, int> AreaSkillStats(Player player, ushort skillId, SupportedGem supported)
    {
        if (CurrentActiveSkills(player) is not { } set) return MergeSkillStats(AreaPlayerStats(player), supported.Stats);
        if (!set.AreaMerged.TryGetValue(skillId, out var known))
            set.AreaMerged[skillId] = known = MergeSkillStats(AreaPlayerStatValues(player), supported.Stats);
        return known;
    }
}
