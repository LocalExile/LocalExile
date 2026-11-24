using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>World rules bind an actor stat on kill, or a worn skill's own stat on hit, to an existing effect.</summary>
sealed class ItemEffectRules
{
    internal sealed record MarkRule(uint Stat, bool RareOrUnique, bool RequiresNoMark);
    sealed record OnslaughtRule(uint Stat, bool Roll, int Milliseconds, uint? DurationStat);
    readonly List<OnslaughtRule> onKill = [];
    readonly List<MarkRule> onHit = [];
    readonly HashSet<uint> blockSupportStats = [];
    readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level), MarkRule[]> grantedMarks = new();
    internal readonly bool NeedsRareHit;

    static readonly RuleTable<GameData.Snapshot, ItemEffectRules> Tables = new();
    internal static ItemEffectRules Current => Tables.GetValue(GameData.Current, static snapshot => new(snapshot));
    // Restart with Game_EFFECT_RULES=0 to run the original Onslaught path for a comparison.
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("Game_EFFECT_RULES") != "0";

    ItemEffectRules(GameData.Snapshot snapshot)
    {
        foreach (var row in snapshot.ItemEffectRules)
        {
            string name = (string)row["stat"]!;
            string? duration = row["duration_stat"] as string;
            uint? durationKey = duration is null ? null : Program.Stats.Key(duration);
            if (Program.Stats.Key(name) is not uint stat || duration is not null && durationKey is null)
            {
                Console.WriteLine($"[gateway] skipping item effect rule {row["id"]}, unknown stat '{(duration is not null && durationKey is null ? duration : name)}'");
                continue;
            }
            switch (row["event"], row["action"])
            {
                case ("kill", "onslaught"):
                    onKill.Add(new(stat, (long)row["chance_from_stat"]! != 0, checked((int)(long)row["duration_ms"]!),
                        durationKey));
                    break;
                case ("hit", "cast_granted_mark"):
                    onHit.Add(new(stat, (long)row["rare_or_unique"]! != 0, (long)row["requires_no_mark"]! != 0));
                    break;
                case ("block", "cast_supported_socketed_spell"):
                    blockSupportStats.Add(stat);
                    break;
                default:
                    throw new InvalidDataException($"Unknown item effect rule {row["id"]}");
            }
        }
        NeedsRareHit = onHit.All(rule => rule.RareOrUnique);
    }

    internal bool HasBlockRules => blockSupportStats.Count != 0;
    internal bool TriggersOnBlock(IReadOnlyList<(uint Key, int Value)> stats) =>
        stats.Any(s => s.Value != 0 && blockSupportStats.Contains(s.Key));

    internal IReadOnlyList<(int Chance, double Seconds)> OnslaughtRolls(Func<uint, int> get)
    {
        List<(int, double)>? rolls = null;
        foreach (var rule in onKill)
        {
            int own = get(rule.Stat);
            if (own <= 0) continue;
            int ms = rule.DurationStat is uint duration ? get(duration) : 0;
            (rolls ??= []).Add((rule.Roll ? own : 100, (ms > 0 ? ms : rule.Milliseconds) / 1000d));
        }
        return rolls is null ? [] : rolls;
    }

    internal MarkRule[] Marks(SkillCatalog skills, WornGrantedSkills.Grant grant)
    {
        if (MarkFamily(grant.EffectId) is null) return [];
        return grantedMarks.GetOrAdd((skills, grant.Effect, grant.Level), key =>
        {
            var stats = key.Skills.EffectStats(key.Effect, key.Level);
            return [.. onHit.Where(rule => stats.Any(s => s.Item1 == rule.Stat && s.Item2 != 0))];
        });
    }

    internal static string? MarkFamily(string effect)
    {
        const string suffix = "Triggered";
        if (!effect.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string family = effect[..^suffix.Length];
        return CurseCatalog.All.TryGetValue(family, out var curse) && curse.Mark ? family : null;
    }
}

public static partial class Game
{
    sealed record ItemMarkSource(ushort Skill, WornGrantedSkills.Grant Grant, ItemEffectRules.MarkRule Rule);
    sealed record ItemMarks(PlayerInventories Items, long Changes, LeagueRuntime League, ItemEffectRules Rules, ItemMarkSource[] Sources);
    static readonly RuleTable<Player, ItemMarks> ItemMarksByPlayer = new();
    sealed record MarkedTarget(uint Id);
    static readonly RuleTable<Player, MarkedTarget> LastMarkedTarget = new();

    static ItemMarkSource[] MarksForHit(Player player, ItemEffectRules rules)
    {
        var items = ItemsOf(player);
        var league = LeagueFor(player);
        if (ItemMarksByPlayer.TryGetValue(player, out var known) && ReferenceEquals(items, known.Items)
            && items.Changes == known.Changes && ReferenceEquals(league, known.League) && rules == known.Rules)
            return known.Sources;
        var skills = SkillsFor(player);
        ItemMarkSource[] sources = [];
        if (AllWornGrantedSkillsOf(player).Any(g => rules.Marks(skills, g).Length != 0))
        {
            List<ItemMarkSource> equipped = [];
            foreach (var source in PlayerSkillSourcesNow(player))
                if (source.WornGrant is { } grant)
                    foreach (var rule in rules.Marks(skills, grant))
                        equipped.Add(new(source.Id, grant, rule));
            sources = [.. equipped];
        }
        ItemMarksByPlayer.AddOrUpdate(player, new(items, items.Changes, league, rules, sources));
        return sources;
    }

    static bool HasLivingMark(Player player)
    {
        bool Marked(SpawnedMonster monster)
        {
            if (!monster.Dead && monster.Life != 0 && monster.HasCurses)
                foreach (var curse in monster.Curses.All.Values)
                    if (curse.Caster == player.Id && curse.Mark) return true;
            return false;
        }
        var monsters = player.Area.Monsters;
        if (LastMarkedTarget.TryGetValue(player, out var previous)
            && monsters.TryGetValue(previous.Id, out var target) && Marked(target)) return true;
        // A manual mark or a mark present across a module reload counts too. Once found, repeated hits cost one lookup.
        foreach (var (id, monster) in monsters)
            if (Marked(monster))
            {
                LastMarkedTarget.AddOrUpdate(player, new(id));
                return true;
            }
        return false;
    }

    static void ItemMarksOnHitLocked(Player player, uint targetId, SpawnedMonster target, double now)
    {
        if (!ItemEffectRules.Enabled || target.Dead || target.Life == 0 || !target.Hostile
            || player.PlayerLife == 0 || player.Closed || player.LeavingArea || player.Paused) return;
        var rules = ItemEffectRules.Current;
        bool rare = target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique;
        if (rules.NeedsRareHit && !rare) return;
        foreach (var source in MarksForHit(player, rules))
        {
            int effect = source.Grant.Effect;
            if (source.Rule.RareOrUnique && !rare || now < player.SkillReadyAt.GetValueOrDefault(effect)) continue;
            if (source.Rule.RequiresNoMark && HasLivingMark(player)) continue;
            var gem = SupportedSkill(player, source.Skill);
            if (gem.WornGrant != source.Grant || GemSkillInstanceDisabled(player, effect, source.Skill, triggered: true) is not null)
                continue;
            var cooldown = CooldownForSkill(player, source.Skill);
            if (!PayMana(player, source.Skill, out uint mana, out uint life)) continue;
            int ms = cooldown?.BaseMilliseconds ?? SkillsFor(player).CooldownMs(effect, gem.Level);
            double rate = cooldown?.RecoveryRate ?? 1;
            player.SkillReadyAt[effect] = ms == 0 ? now : rate > 0 ? now + ms / (1000d * rate) : double.PositiveInfinity;
            if (ApplyCurseDataLocked(player, target, source.Skill, ItemEffectRules.MarkFamily(source.Grant.EffectId)!,
                gem.Stats, now, false, null, 0, 1))
                LastMarkedTarget.AddOrUpdate(player, new(targetId));
            if (mana != 0 || life != 0)
            {
                player.Frames.Area?.Life.Resend(player);
                if (player.ProjectileRuntime is { Active: true } session)
                    session.Enqueue(() => SendSkillCost(player, life, "item mark cost"));
            }
        }
    }
}
