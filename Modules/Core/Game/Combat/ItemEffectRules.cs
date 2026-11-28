using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>World rules bind an actor stat on kill, or a worn skill's own stat on hit, to an existing effect.</summary>
sealed partial class ItemEffectRules
{
    internal sealed record MarkRule(uint Stat, bool RareOrUnique, bool RequiresNoMark);
    sealed record OnslaughtRule(uint[] Stats, bool Roll, double Seconds, uint? DurationStat, bool FromRoll);
    readonly List<OnslaughtRule> onKill = [];
    readonly List<MarkRule> onHit = [];
    readonly HashSet<uint> blockSupportStats = [];
    readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level), MarkRule[]> grantedMarks = new();
    internal readonly IReadOnlyList<ChargeGains.Rule> ChargeRules;
    internal readonly IReadOnlyList<ChargeGains.Rule> MinionKillCharges;
    internal readonly IReadOnlyList<ChargeGains.Rule> HitChargeRules;
    internal readonly (uint Stat, ChargeType Type)[] MonsterHitCharges;
    internal readonly bool NeedsRareHit;

    sealed class ChargeRuleLists
    {
        internal readonly SortedDictionary<long, ChargeGains.Rule[]> PlayerKills = [], MinionKills = [];
        internal readonly SortedDictionary<long, (uint Stat, ChargeType Type)[]> PlayerHits = [], MonsterHits = [];
    }

    static readonly RuleTable<GameData.Snapshot, ItemEffectRules> Tables = new();
    internal static ItemEffectRules Current => Tables.GetValue(GameData.Current, static snapshot => new(snapshot));
    // Restart with Game_EFFECT_RULES=0 to run the original Onslaught path for a comparison.
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("Game_EFFECT_RULES") != "0";

    ItemEffectRules(GameData.Snapshot snapshot)
    {
        var charges = new ChargeRuleLists();
        foreach (var row in snapshot.FxRules) Compile(row, charges);
        MinionKillCharges = [.. charges.MinionKills.Values.SelectMany(rules => rules)];
        var playerHits = charges.PlayerHits.Values.SelectMany(rules => rules)
            .Select(hit => new ChargeGains.Rule(hit.Stat, hit.Type, static (e, _) => e is ChargeEvent.Hit));
        HitChargeRules = [.. playerHits, .. ChargeGains.OtherRules];
        MonsterHitCharges = [.. charges.MonsterHits.Values.SelectMany(rules => rules)];
        // Keep the original roll order, including the random charge type drawn between the two sets.
        ChargeRules = [.. charges.PlayerKills.Where(r => r.Key < 0).SelectMany(r => r.Value), .. ChargeGains.AdditionalKillRules,
            .. charges.PlayerKills.Where(r => r.Key > 0).SelectMany(r => r.Value), .. ChargeGains.OtherRules];
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
