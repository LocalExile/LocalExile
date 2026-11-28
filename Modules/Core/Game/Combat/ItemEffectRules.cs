using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>World rules bind a stat to an existing effect: charges on every charge event, Onslaught on kill, a worn
/// skill's mark on hit and its socketed spell on block.</summary>
sealed partial class ItemEffectRules
{
    internal sealed record MarkRule(uint Stat, bool RareOrUnique, bool RequiresNoMark);
    sealed record OnslaughtRule(uint[] Stats, bool Roll, double Seconds, uint? DurationStat, bool FromRoll);
    readonly List<OnslaughtRule> onKill = [];
    readonly List<MarkRule> onHit = [];
    readonly HashSet<uint> blockSupportStats = [];
    readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level), MarkRule[]> grantedMarks = new();
    readonly IReadOnlyList<ChargeGains.Rule> killCharges, hitCharges, whenHitCharges, blockCharges, stunCharges, skillUseCharges;
    internal readonly IReadOnlyList<ChargeGains.Rule> MinionKillCharges, MinionHitCharges;
    internal readonly (uint Stat, ChargeType Type)[] MonsterHitCharges, MonsterWhenHitCharges;
    internal readonly bool NeedsRareHit;

    /// <summary>Whose event a rule listens to: the player's, a supported minion's, or a hostile monster's.</summary>
    internal enum Actor { Player, Minion, Monster }

    sealed class ChargeRuleLists
    {
        internal readonly Dictionary<(string Event, Actor Actor), SortedDictionary<long, ChargeGains.Rule[]>> ByEvent = [];
        internal readonly HashSet<(string Event, Actor Actor, uint Stat)> Stats = [];

        internal ChargeGains.Rule[] Of(string trigger, Actor actor) =>
            ByEvent.TryGetValue((trigger, actor), out var rules) ? [.. rules.Values.SelectMany(r => r)] : [];
        internal (uint Stat, ChargeType Type)[] Pairs(string trigger, Actor actor) =>
            [.. Of(trigger, actor).Select(r => (r.Stat, r.Type!.Value))];
    }

    /// <summary>The player's charge rules for an event, in roll order.</summary>
    internal IReadOnlyList<ChargeGains.Rule> PlayerCharges(ChargeEvent e) => e switch
    {
        ChargeEvent.Kill => killCharges,
        ChargeEvent.Hit => hitCharges,
        ChargeEvent.WhenHit => whenHitCharges,
        ChargeEvent.Block => blockCharges,
        ChargeEvent.Stun => stunCharges,
        ChargeEvent.SkillUse => skillUseCharges,
        _ => [],
    };

    static readonly RuleTable<GameData.Snapshot, ItemEffectRules> Tables = new();
    internal static ItemEffectRules Current => Tables.GetValue(GameData.Current, static snapshot => new(snapshot));
    // Restart with Game_EFFECT_RULES=0 to run the original Onslaught path for a comparison.
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("Game_EFFECT_RULES") != "0";

    ItemEffectRules(GameData.Snapshot snapshot)
    {
        var charges = new ChargeRuleLists();
        foreach (var row in snapshot.FxRules) Compile(row, charges);
        killCharges = charges.Of("kill", Actor.Player);
        hitCharges = charges.Of("hit", Actor.Player);
        whenHitCharges = charges.Of("when_hit", Actor.Player);
        blockCharges = charges.Of("block", Actor.Player);
        stunCharges = charges.Of("stun", Actor.Player);
        skillUseCharges = charges.Of("skill_use", Actor.Player);
        MinionKillCharges = charges.Of("kill", Actor.Minion);
        MinionHitCharges = charges.Of("hit", Actor.Minion);
        MonsterHitCharges = charges.Pairs("hit", Actor.Monster);
        MonsterWhenHitCharges = charges.Pairs("when_hit", Actor.Monster);
        NeedsRareHit = onHit.All(rule => rule.RareOrUnique);
    }

    internal static void Validate(GameData.Snapshot snapshot) => Tables.GetValue(snapshot, static data => new(data));

    internal bool HasBlockRules => blockSupportStats.Count != 0;
    internal bool TriggersOnBlock(IReadOnlyList<(uint Key, int Value)> stats) =>
        stats.Any(s => s.Value != 0 && blockSupportStats.Contains(s.Key));

    internal IReadOnlyList<(int Chance, double Seconds)> OnslaughtRolls(Func<uint, int> get)
    {
        List<(int, double)>? rolls = null;
        foreach (var rule in onKill)
        {
            int own = 0;
            foreach (uint stat in rule.Stats) own = Math.Max(own, get(stat));
            if (own <= 0) continue;
            int ms = rule.FromRoll ? own : rule.DurationStat is uint duration ? get(duration) : 0;
            (rolls ??= []).Add((rule.Roll ? own : 100, ms > 0 ? ms / 1000d : rule.Seconds));
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

    static void ItemMarksOnHitLocked(Player player, SpawnedMonster target, double now)
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
            if (source.Rule.RequiresNoMark && ActiveMarkTarget(player, now) is not null) continue;
            var gem = SupportedSkill(player, source.Skill);
            if (!gem.RequirementsMet || gem.WornGrant != source.Grant || GemSkillInstanceDisabled(player, effect, source.Skill, triggered: true) is not null)
                continue;
            var cooldown = CooldownForSkill(player, source.Skill);
            if (!PayMana(player, source.Skill, out uint mana, out uint life)) continue;
            int ms = cooldown?.BaseMilliseconds ?? SkillsFor(player).CooldownMs(effect, gem.Level);
            double rate = cooldown?.RecoveryRate ?? 1;
            player.SkillReadyAt[effect] = ms == 0 ? now : rate > 0 ? now + ms / (1000d * rate) : double.PositiveInfinity;
            ApplyCurseDataLocked(player, target, source.Skill, ItemEffectRules.MarkFamily(source.Grant.EffectId)!,
                gem.Stats, now, false, null, 0, 1);
            if (mana != 0 || life != 0)
            {
                player.Frames.Area?.Life.Resend(player);
                if (player.ProjectileRuntime is { Active: true } session)
                    session.Enqueue(() => SendSkillCost(player, life, "item mark cost"));
            }
        }
    }
}
