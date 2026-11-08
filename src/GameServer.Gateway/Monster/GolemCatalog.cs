using System.Text.Json;

/// <summary>Immutable identities from the installed client's tables; levels and quality stay with SkillCatalog.</summary>
sealed class GolemCatalog(string folder = "data")
{
    internal sealed record Definition(int Effect, string Id, string Kind, string Variety, ushort Buff,
        IReadOnlyList<(string Source, string Target)> BuffStats, bool Hordes, bool Special, bool IsGolem = true);

    readonly Lazy<IReadOnlyDictionary<string, Definition>> definitions = new(() => Load(folder));
    readonly Lazy<Scaling> scaling = new(() => new(folder));
    public IReadOnlyCollection<Definition> All => definitions.Value.Values.ToArray();
    public Definition? Find(string? id) => id is not null ? definitions.Value.GetValueOrDefault(id) : null;

    internal (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Skill(SkillCatalog skills, int effect, int actorLevel)
        => scaling.Value.Skill(skills, effect, actorLevel);

    sealed class Scaling
    {
        readonly GameTables.Lease effects, sets, levels;
        readonly Dictionary<int, JsonElement[]> rows;
        readonly double basis, increment;
        public Scaling(string folder)
        {
            effects = GameTables.Open(Path.Combine(folder, "tables", "GrantedEffects.json"));
            sets = GameTables.Open(Path.Combine(folder, "tables", "GrantedEffectStatSets.json"));
            levels = GameTables.Open(Path.Combine(folder, "tables", "GrantedEffectStatSetsPerLevel.json"));
            rows = levels.RootElement.EnumerateArray().GroupBy(r => r.GetProperty("StatSet").GetInt32())
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.GetProperty("PlayerLevelReq").GetInt32()).ToArray());
            using var constants = GameTables.Open(Path.Combine(folder, "tables", "GameConstants.json"));
            double Constant(string id) { var r = constants.RootElement.EnumerateArray().First(r => r.GetProperty("Id").GetString() == id);
                return r.GetProperty("Value").GetDouble() / r.GetProperty("Divisor").GetDouble(); }
            basis = Constant("SkillDamageBaseEffectiveness"); increment = Constant("SkillDamageIncrementalEffectiveness");
        }

        public (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Skill(SkillCatalog skills, int effect, int actorLevel)
        {
            int set = effects.RootElement[effect].GetProperty("StatSet1").GetInt32();
            var candidates = rows[set];
            var row = candidates.LastOrDefault(r => r.GetProperty("PlayerLevelReq").GetInt32() <= actorLevel);
            if (row.ValueKind == JsonValueKind.Undefined) row = candidates[0];
            int level = row.GetProperty("GemLevel").GetInt32();
            var stats = skills.EffectStats(effect, level).ToDictionary(p => p.Item1, p => p.Item2);
            var definition = sets.RootElement[set];
            double effectiveness = (basis + increment * (actorLevel - 1)) * definition.GetProperty("BaseEffectiveness").GetDouble()
                * Math.Pow(1 + definition.GetProperty("IncrementalEffectiveness").GetDouble(), actorLevel - 1);
            var keys = row.GetProperty("FloatStats"); var interpolation = row.GetProperty("StatInterpolations");
            var values = row.GetProperty("FloatStatsValues"); var bases = row.GetProperty("InterpolationBases");
            for (int i = 0; i < keys.GetArrayLength(); i++)
                if (interpolation[i].GetInt32() == 3)
                    stats[(uint)keys[i].GetInt32() + 1] = ActorScale.Saturate((long)Math.Round(effectiveness * values[i].GetDouble()
                        * (bases[i].GetInt32() == 1 ? 1000.0 / 60 : 1), MidpointRounding.AwayFromZero));
            return (level, stats, 1 + row.GetProperty("BaseMultiplier").GetDouble() / 10000,
                Math.Max(row.GetProperty("SpellCritChance").GetInt32(), row.GetProperty("AttackCritChance").GetInt32()));
        }
    }

    static IReadOnlyDictionary<string, Definition> Load(string folder)
    {
        using var effects = GameTables.Open(Path.Combine(folder, "tables", "GrantedEffects.json"));
        using var active = GameTables.Open(Path.Combine(folder, "tables", "ActiveSkills.json"));
        using var buffs = GameTables.Open(Path.Combine(folder, "tables", "BuffDefinitions.json"));
        var rows = buffs.RootElement.EnumerateArray().Select((r, i) => (Id: r.GetProperty("Id").GetString()!, Row: (ushort)i))
            .ToDictionary(r => r.Id, r => r.Row, StringComparer.Ordinal);
        var species = new (string Kind, string Variety, string Buff, (string, string)[] Stats)[]
        {
            ("Chaos", "Metadata/Monsters/ChaosElemental/ChaosElementalSummoned", "chaos_elemental_buff",
                [("chaos_golem_grants_dot_multiplier_+", "dot_multiplier_+"), ("chaos_golem_grants_chaos_resistance_%", "base_chaos_damage_resistance_%")]),
            ("Ice", "Metadata/Monsters/IceElemental/IceElementalSummoned", "ice_elemental_buff",
                [("ice_golem_grants_accuracy_rating_+", "accuracy_rating"), ("ice_golem_grants_critical_strike_chance_+%", "critical_strike_chance_+%")]),
            ("Flame", "Metadata/Monsters/FireElemental/FireElementalSummoned", "fire_elemental_buff",
                [("fire_golem_grants_damage_+%", "damage_+%"), ("fire_golem_grants_area_of_effect_+%", "base_skill_area_of_effect_+%")]),
            ("Stone", "Metadata/Monsters/RockGolem/RockGolemSummoned", "rock_golem_buff",
                [("stone_golem_grants_base_life_regeneration_rate_per_minute", "base_life_regeneration_rate_per_minute"), ("stone_golem_grants_defences_+%", "global_defences_+%")]),
            ("Lightning", "Metadata/Monsters/LightningGolem/LightningGolemSummoned", "lightning_elemental_buff",
                [("lightning_golem_grants_attack_and_cast_speed_+%", "attack_and_cast_speed_+%"), ("lightning_golem_grants_base_mana_regeneration_rate_per_minute", "base_mana_regeneration_rate_per_minute")]),
            ("Carrion", "Metadata/Monsters/BoneGolem/BoneGolem", "bone_golem_buff",
                [("bone_golem_grants_minion_minimum_added_physical_damage", "non_golem_minion_global_minimum_added_physical_damage"), ("bone_golem_grants_minion_maximum_added_physical_damage", "non_golem_minion_global_maximum_added_physical_damage")]),
        };
        var result = new Dictionary<string, Definition>(StringComparer.Ordinal);
        int index = 0;
        foreach (var effect in effects.RootElement.EnumerateArray())
        {
            int row = index++;
            string id = effect.GetProperty("Id").GetString()!;
            foreach (var kind in species)
            {
                string stem = "Summon" + kind.Kind + "Golem";
                if (id != stem && id != stem + "AltX" && id != stem + "AltY") continue;
                var skill = active.RootElement[effect.GetProperty("ActiveSkill").GetInt32()];
                if ((skill.GetProperty("DisplayedName").GetString() ?? "").StartsWith("[DNT]", StringComparison.Ordinal)) continue;
                bool special = id.EndsWith("AltY", StringComparison.Ordinal), hordes = id.EndsWith("AltX", StringComparison.Ordinal);
                var stats = special && kind.Kind == "Stone"
                    ? new (string, string)[] { ("stone_golem_grants_melee_damage_removed_from_stone_golem_before_life_or_es_%", "melee_damage_removed_from_stone_golem_before_life_or_es_%") }
                    : kind.Stats;
                ushort buff = hordes || special && kind.Kind != "Stone" ? (ushort)0
                    : rows[special ? "rock_golem_alt_buff" : kind.Buff];
                result.Add(id, new(row, id, kind.Kind, kind.Variety, buff, stats, hordes, special));
                break;
            }
        }
        return result;
    }

    internal static int Limit(int skillBase, Func<string, int> player)
    {
        long value = Math.Max(1, skillBase) + (long)player("base_number_of_golems_allowed") + player("number_of_golems_allowed");
        if (player("primordial_jewel_count") >= 3) value += player("number_of_golems_allowed_with_3_primordial_jewels");
        if (player("double_golem_limit") != 0) value *= 2;
        return (int)Math.Clamp(value, 0, 100);
    }

    internal static int BuffEffect(Definition definition, Func<string, int> player, Func<string, int> own, int count)
    {
        string family = definition.Kind switch { "Flame" => "fire", "Carrion" => "bone", _ => definition.Kind.ToLowerInvariant() };
        int effect = player("golem_buff_effect_+%") + own("golem_buff_effect_+%")
            + player("base_" + family + "_golem_granted_buff_effect_+%")
            + player("golem_buff_effect_+%_per_summoned_golem") * count;
        if (definition.Kind is "Flame" or "Ice" or "Lightning") effect += player("elemental_golem_granted_buff_effect_+%");
        return Math.Max(-100, effect);
    }
}
