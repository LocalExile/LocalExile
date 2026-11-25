using GameServer.Core.Protocol.Instance;

// What the official servers send when a buff, an aura or a flask changes what a skill costs or how fast it swings: one S->C
// 0x1b4 per affected skill, carrying only that skill's changed stats (11-skills.md 3.1). Ours resent the whole skill list
// (0x2bc, about 32 KB) at every such change.
//
// The gate below compares what PlayerSkills produced (the stat lists its stat graph built) against what was last sent
// (SentSkillList.Skills, which keeps those lists by reference): it reads no stat sheet and computes no stat itself. It
// answers null whenever the whole list must go out, and the caller then runs the unchanged whole-list path.
public static partial class Game
{
    /// <summary>
    /// The changed skill stats as per-skill 0x1b4 records (empty: the client is already up to date), or null when the
    /// whole list has to go out. A record goes out only when all of these hold; each other case is null:
    /// (1) something was sent before; (2) the skill id set equals the one last sent; (3) every skill's record shape is
    /// unchanged; (4) the mine-detonation flag is unchanged; (5) no guard skill is held; (6) no native cooldown refresh
    /// is pending; (7) no stat left a skill's set; (8) only stat values changed; (9) Momentum is not deferring.
    /// The caller records what it sends with <see cref="RecordSkillDeltas"/> once it has the records.
    /// </summary>
    static byte[][]? SkillStatDeltasAtGate(Player player, IReadOnlyList<GrantedSkill> skills, out bool detonate)
    {
        detonate = false;
        if (!SentSkillLists.TryGetValue(player, out var sent)) return null;                 // 1
        if (TriggeredGuards.TryGetValue(player, out _)) return null;                         // 5
        foreach (var cooldown in player.SingleUseCooldowns.Values)
            if (cooldown.NeedsNativeRefresh) return null;                                    // 6
        detonate = CanDetonateMines(player);
        List<byte[]>? records = null;
        lock (sent)
        {
            if (sent.Skills is not { } last || sent.Detonate != detonate) return null;        // 1, 4
            if (ReferenceEquals(last, skills)) return [];                                     // the same lists: nothing changed
            if (last.Count != skills.Count) return null;                                      // 2
            for (int i = 0; i < skills.Count; i++)
            {
                var before = last[i];
                var now = skills[i];
                if (before.Id != now.Id) return null;                                         // 2 (and an order we do not match)
                if (!SameSkillShape(before, now)) return null;                                // 3
                for (int set = 0; set <= now.ExtraStatSets.Count; set++)
                {
                    var was = set == 0 ? before.Stats : before.ExtraStatSets[set - 1].Stats;
                    var then = set == 0 ? now.Stats : now.ExtraStatSets[set - 1].Stats;
                    if (ReferenceEquals(was, then)) continue;
                    if (!ChangedStatPairs(was, then, out var changed)) return null;           // 7
                    if (changed is null) continue;
                    (records ??= []).Add(InstanceEntities.SkillStatsUpdate(player.Id, now.Id, (byte)set, changed)); // 8
                }
            }
        }
        if (records is null) return [];
        if (DeferMomentumChannelSkills(player)) return null;                                 // 9
        return [.. records];
    }

    /// <summary>Records that the client now has these skills (and the detonation flag) after the deltas were built. The
    /// list is kept by reference, as the whole-list path keeps it; the whole-list bytes are no longer what the client
    /// has, so they are dropped.</summary>
    static void RecordSkillDeltas(Player player, IReadOnlyList<GrantedSkill> skills, bool detonate)
    {
        var sent = SentSkillLists.GetOrCreateValue(player);
        lock (sent) (sent.Bytes, sent.Skills, sent.Detonate) = (null, skills, detonate);
    }

    // Everything a skill record carries except its stat lists (InstanceEntities WriteSkillRecord): what a 0x1b4 cannot say.
    static bool SameSkillShape(GrantedSkill a, GrantedSkill b)
    {
        if (a.Id != b.Id || a.EffectsPerLevelRow != b.EffectsPerLevelRow || a.StatSetsPerLevelRow != b.StatSetsPerLevelRow
            || a.CooldownMs != b.CooldownMs || a.CooldownBypassType != b.CooldownBypassType
            || a.VaalResourcesEnabled != b.VaalResourcesEnabled || a.Parent != b.Parent
            || a.ExtraStatSets.Count != b.ExtraStatSets.Count || a.Supports.Count != b.Supports.Count) return false;
        for (int i = 0; i < a.ExtraStatSets.Count; i++)
            if (a.ExtraStatSets[i].Row != b.ExtraStatSets[i].Row) return false;
        if (ReferenceEquals(a.Supports, b.Supports)) return true;
        for (int i = 0; i < a.Supports.Count; i++)
        {
            var x = a.Supports[i];
            var y = b.Supports[i];
            if (x.EffectsPerLevelRow != y.EffectsPerLevelRow || x.StatSetsPerLevelRow != y.StatSetsPerLevelRow
                || x.Source != y.Source || !x.ExtraStatSets.SequenceEqual(y.ExtraStatSets)) return false;
        }
        return true;
    }

    // The pairs of one stat list whose value changed, ordered by key (null: none). False when a stat left the list: a 0x1b4
    // can only set a value, so that change needs the whole list.
    static bool ChangedStatPairs(IReadOnlyList<(uint Key, int Value)>? before, IReadOnlyList<(uint Key, int Value)>? now,
        out List<(uint Stat, int Value)>? changed)
    {
        changed = null;
        var was = before ?? [];
        var then = now ?? [];
        bool aligned = was.Count == then.Count;
        for (int i = 0; aligned && i < was.Count; i++) aligned = was[i].Key == then[i].Key;
        if (aligned)
        {
            for (int i = 0; i < then.Count; i++)
                if (was[i].Value != then[i].Value) (changed ??= []).Add((then[i].Key, then[i].Value));
        }
        else
        {
            var old = new Dictionary<uint, int>(was.Count);
            foreach (var (key, value) in was) old[key] = value;
            var keys = new HashSet<uint>(then.Count);
            foreach (var (key, value) in then)
            {
                keys.Add(key);
                if (!old.TryGetValue(key, out int had) || had != value) (changed ??= []).Add((key, value));
            }
            foreach (var (key, _) in was) if (!keys.Contains(key)) return false;
        }
        changed?.Sort((x, y) => x.Stat.CompareTo(y.Stat));
        return true;
    }

    // The soul budgets alone, for a refresh that publishes the skills as 0x1b4 records: those carry no budget, and
    // VaalSoulChanges must be consumed exactly once per refresh because it records what was sent.
    public static byte[]? VaalSoulRecordAtGate(Player player)
    {
        var changes = VaalSoulChanges(player);
        return changes.Seeds.Length == 0 && changes.Updates.Length == 0 ? null
            : InstanceEntities.PlayerSkillsChange(player.Id, [], [], changes.Seeds, changes.Updates, CanDetonateMines(player));
    }
}
