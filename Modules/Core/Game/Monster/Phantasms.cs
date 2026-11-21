using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static string SummonSupportStamp(SupportedGem gem) => string.Join(";", gem.Supports.Select(s =>
        $"{s.Gem.Effect}:{s.Gem.Level}:{s.Gem.Quality}:{s.Gem.Socket}"));

    static SummonCatalog.Definition PhantasmDefinition(string effect) => new(effect, "Phantasm",
        "Metadata/Monsters/SummonedPhantasm/SummonedPhantasm", Stat.BaseNumberOfSupportGhostsAllowed, 0);

    static SupportedGem PhantasmGem(Player player, SupportedGem source)
    {
        var support = source.Supports.FirstOrDefault(s => s.Effect.Id == "SupportSummonPhantasm");
        var stats = new Dictionary<uint, int>(source.Stats);
        void Set(uint name, int value) { if (name is var key) stats[key] = value; }
        Set(Stat.BaseSkillEffectDuration, GolemStat(stats, Stat.SupportGhostBaseDuration));
        Set(Stat.BaseDisplayMinionActorLevel, support is null ? 0 : SkillsFor(player).PlayerLevelReq(support.Gem.Effect, support.Gem.Level));
        return source with { Stats = stats };
    }

    internal static void PhantasmsOnHitLocked(Player player, SpawnedMonster victim, ushort source, double now,
        Random random, GolemState.Golem? minion = null, IReadOnlyDictionary<uint, int>? skill = null)
    {
        if (source == 0 || player.PlayerLife == 0 || !victim.Hostile || minion?.Definition.Kind == "Phantasm"
            || minion?.Monster.Effects[Stat.CannotSpawnSupportGhosts] > 0) return;
        bool rareOrUnique = victim.Rarity is MonsterRarity.Rare or MonsterRarity.Unique;
        bool killed = victim.Dead || victim.Life == 0;
        if (!rareOrUnique && !killed) return;
        var knownStats = minion?.Own ?? skill;
        if (knownStats is not null
            && (!rareOrUnique || GolemStat(knownStats, Stat.ChanceToSummonSupportGhostOnHittingRareOrUniquePct) <= 0)
            && (!killed || GolemStat(knownStats, Stat.ChanceToSummonSupportGhostOnKillingBlowPct) <= 0)) return;
        var gem = SupportedSkill(player, source);
        if (!gem.RequirementsMet || !gem.Supports.Any(s => s.Effect.Id == "SupportSummonPhantasm")) return;
        var target = player.Area.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, victim));
        if (target.Value is null) return;
        void Roll(uint stat)
        {
            int chance = GolemStat(gem.Stats, stat);
            if (chance > 0 && random.Next(100) < chance)
                player.Summons.HitSummons.Add(new(target.Key, victim, source, gem.Effect, gem.SourceGem, false,
                    "Phantasm", SummonSupportStamp(gem)));
        }
        if (rareOrUnique)
            Roll(Stat.ChanceToSummonSupportGhostOnHittingRareOrUniquePct);
        if (killed) Roll(Stat.ChanceToSummonSupportGhostOnKillingBlowPct);
    }
}
