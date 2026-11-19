using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static string SummonSupportStamp(SupportedGem gem) => string.Join(";", gem.Supports.Select(s =>
        $"{s.Gem.Effect}:{s.Gem.Level}:{s.Gem.Quality}:{s.Gem.Socket}"));

    static SummonCatalog.Definition PhantasmDefinition(string effect) => new(effect, "Phantasm",
        "Metadata/Monsters/SummonedPhantasm/SummonedPhantasm", "base_number_of_support_ghosts_allowed", "");

    static SupportedGem PhantasmGem(Player conn, SupportedGem source)
    {
        var support = source.Supports.FirstOrDefault(s => s.Effect.Id == "SupportSummonPhantasm");
        var stats = new Dictionary<uint, int>(source.Stats);
        void Set(string name, int value) { if (Stats.Key(name) is uint key) stats[key] = value; }
        Set("base_skill_effect_duration", GolemStat(stats, "support_ghost_base_duration"));
        Set("base_display_minion_actor_level", support is null ? 0 : SkillsFor(conn).PlayerLevelReq(support.Gem.Effect, support.Gem.Level));
        return source with { Stats = stats };
    }

    internal static void PhantasmsOnHitLocked(Player conn, SpawnedMonster victim, ushort source, double now,
        Random random, GolemState.Golem? minion = null, IReadOnlyDictionary<uint, int>? skill = null)
    {
        if (source == 0 || conn.PlayerLife == 0 || !victim.Hostile || minion?.Definition.Kind == "Phantasm"
            || minion?.Monster.Effects["cannot_spawn_support_ghosts"] > 0) return;
        bool rareOrUnique = victim.Rarity is MonsterRarity.Rare or MonsterRarity.Unique;
        bool killed = victim.Dead || victim.Life == 0;
        if (!rareOrUnique && !killed) return;
        var knownStats = minion?.Own ?? skill;
        if (knownStats is not null
            && (!rareOrUnique || GolemStat(knownStats, "chance_to_summon_support_ghost_on_hitting_rare_or_unique_%") <= 0)
            && (!killed || GolemStat(knownStats, "chance_to_summon_support_ghost_on_killing_blow_%") <= 0)) return;
        var gem = SupportedSkill(conn, source);
        if (!gem.RequirementsMet || !gem.Supports.Any(s => s.Effect.Id == "SupportSummonPhantasm")) return;
        var target = conn.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, victim));
        if (target.Value is null) return;
        void Roll(string stat)
        {
            int chance = GolemStat(gem.Stats, stat);
            if (chance > 0 && random.Next(100) < chance)
                conn.Summons.HitSummons.Add(new(target.Key, victim, source, gem.Effect, gem.SourceGem, false,
                    "Phantasm", SummonSupportStamp(gem)));
        }
        if (rareOrUnique)
            Roll("chance_to_summon_support_ghost_on_hitting_rare_or_unique_%");
        if (killed) Roll("chance_to_summon_support_ghost_on_killing_blow_%");
    }
}
