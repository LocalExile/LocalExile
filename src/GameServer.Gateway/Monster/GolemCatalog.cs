using System.Text.Json;
using GameServer.Core;

/// <summary>Immutable identities from the installed client's tables; levels and quality stay with SkillCatalog.</summary>
public sealed class GolemCatalog
{
    readonly string folder;

    public GolemCatalog(string folder = "data")
    {
        this.folder = folder;
        definitions = Load(folder);
        scaling = new(folder);
    }

    public sealed record Definition(int Effect, string Id, string Kind, string Variety, ushort Buff,
        IReadOnlyList<(uint Source, uint Target)> BuffStats, bool Hordes, bool Special, bool IsGolem = true);

    readonly IReadOnlyDictionary<string, Definition> definitions;
    readonly Scaling scaling;
    public IReadOnlyCollection<Definition> All => definitions.Values.ToArray();

    /// <summary>Nothing left to build: the constructor reads the definitions and the level scaling. Kept for the game
    /// module's startup warm-up.</summary>
    public void Warm() { }
    public Definition? Find(string? id) => id is not null ? definitions.GetValueOrDefault(id) : null;

    public (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Skill(SkillCatalog skills, int effect, int actorLevel)
        => scaling.Skill(skills, effect, actorLevel);

    sealed class Scaling
    {
        readonly ClientTable effects, sets, levels;
        readonly Dictionary<int, ClientRow[]> rows;
        readonly double basis, increment;
        public Scaling(string folder)
        {
            effects = ClientTables.Table("GrantedEffects");
            sets = ClientTables.Table("GrantedEffectStatSets");
            levels = ClientTables.Table("GrantedEffectStatSetsPerLevel");
            rows = levels.GroupBy(r => r.Int("StatSet"))
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Int("PlayerLevelReq")).ToArray());
            double Constant(string id) { var r = ClientTables.Table("GameConstants").First(r => r.Text("Id") == id);
                return r.Real("Value") / r.Real("Divisor"); }
            basis = Constant("SkillDamageBaseEffectiveness"); increment = Constant("SkillDamageIncrementalEffectiveness");
        }

        public (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Skill(SkillCatalog skills, int effect, int actorLevel)
        {
            int set = effects[effect].Int("StatSet1");
            var candidates = rows[set];
            var row = candidates.LastOrDefault(r => r.Int("PlayerLevelReq") <= actorLevel, candidates[0]);
            int level = row.Int("GemLevel");
            var stats = skills.EffectStats(effect, level).ToDictionary(p => p.Item1, p => p.Item2);
            var definition = sets[set];
            double effectiveness = (basis + increment * (actorLevel - 1)) * definition.Real("BaseEffectiveness")
                * Math.Pow(1 + definition.Real("IncrementalEffectiveness"), actorLevel - 1);
            var keys = row.Ints("FloatStats"); var interpolation = row.Ints("StatInterpolations");
            var values = row.Reals("FloatStatsValues"); var bases = row.Ints("InterpolationBases");
            for (int i = 0; i < keys.Length; i++)
                if (interpolation[i] == 3)
                    stats[(uint)keys[i] + 1] = ActorScale.Saturate((long)Math.Round(effectiveness * values[i]
                        * (bases[i] == 1 ? 1000.0 / 60 : 1), MidpointRounding.AwayFromZero));
            return (level, stats, 1 + row.Real("BaseMultiplier") / 10000,
                Math.Max(row.Int("SpellCritChance"), row.Int("AttackCritChance")));
        }
    }

    static IReadOnlyDictionary<string, Definition> Load(string folder)
    {
        var effects = ClientTables.Table("GrantedEffects");
        var active = ClientTables.Table("ActiveSkills");
        var rows = ClientTables.Table("BuffDefinitions").Select((r, i) => (Id: r.Text("Id")!, Row: (ushort)i))
            .ToDictionary(r => r.Id, r => r.Row, StringComparer.Ordinal);
        var species = new (string Kind, string Variety, string Buff, (uint, uint)[] Stats)[]
        {
            ("Chaos", "Metadata/Monsters/ChaosElemental/ChaosElementalSummoned", "chaos_elemental_buff",
                [(Stat.ChaosGolemGrantsDotMultiplierPlus, Stat.DotMultiplierPlus), (Stat.ChaosGolemGrantsChaosResistancePct, Stat.BaseChaosDamageResistancePct)]),
            ("Ice", "Metadata/Monsters/IceElemental/IceElementalSummoned", "ice_elemental_buff",
                [(Stat.IceGolemGrantsAccuracyRatingPlus, Stat.AccuracyRating), (Stat.IceGolemGrantsCriticalStrikeChancePlusPct, Stat.CriticalStrikeChancePlusPct)]),
            ("Flame", "Metadata/Monsters/FireElemental/FireElementalSummoned", "fire_elemental_buff",
                [(Stat.FireGolemGrantsDamagePlusPct, Stat.DamagePlusPct), (Stat.FireGolemGrantsAreaOfEffectPlusPct, Stat.BaseSkillAreaOfEffectPlusPct)]),
            ("Stone", "Metadata/Monsters/RockGolem/RockGolemSummoned", "rock_golem_buff",
                [(Stat.StoneGolemGrantsBaseLifeRegenerationRatePerMinute, Stat.BaseLifeRegenerationRatePerMinute), (Stat.StoneGolemGrantsDefencesPlusPct, Stat.GlobalDefencesPlusPct)]),
            ("Lightning", "Metadata/Monsters/LightningGolem/LightningGolemSummoned", "lightning_elemental_buff",
                [(Stat.LightningGolemGrantsAttackAndCastSpeedPlusPct, Stat.AttackAndCastSpeedPlusPct), (Stat.LightningGolemGrantsBaseManaRegenerationRatePerMinute, Stat.BaseManaRegenerationRatePerMinute)]),
            ("Carrion", "Metadata/Monsters/BoneGolem/BoneGolem", "bone_golem_buff",
                [(Stat.BoneGolemGrantsMinionMinimumAddedPhysicalDamage, Stat.NonGolemMinionGlobalMinimumAddedPhysicalDamage), (Stat.BoneGolemGrantsMinionMaximumAddedPhysicalDamage, Stat.NonGolemMinionGlobalMaximumAddedPhysicalDamage)]),
        };
        var result = new Dictionary<string, Definition>(StringComparer.Ordinal);
        int index = 0;
        foreach (var effect in effects)
        {
            int row = index++;
            string id = effect.Text("Id")!;
            foreach (var kind in species)
            {
                string stem = "Summon" + kind.Kind + "Golem";
                if (id != stem && id != stem + "AltX" && id != stem + "AltY") continue;
                var skill = active[effect.Int("ActiveSkill")];
                if ((skill.Text("DisplayedName") ?? "").StartsWith("[DNT]", StringComparison.Ordinal)) continue;
                bool special = id.EndsWith("AltY", StringComparison.Ordinal), hordes = id.EndsWith("AltX", StringComparison.Ordinal);
                var stats = special && kind.Kind == "Stone"
                    ? new (uint, uint)[] { (Stat.StoneGolemGrantsMeleeDamageRemovedFromStoneGolemBeforeLifeOrEsPct, Stat.MeleeDamageRemovedFromStoneGolemBeforeLifeOrEsPct) }
                    : kind.Stats;
                ushort buff = hordes || special && kind.Kind != "Stone" ? (ushort)0
                    : rows[special ? "rock_golem_alt_buff" : kind.Buff];
                result.Add(id, new(row, id, kind.Kind, kind.Variety, buff, stats, hordes, special));
                break;
            }
        }
        return result;
    }

    public static int Limit(int skillBase, Func<uint, int> player)
    {
        long value = Math.Max(1, skillBase) + (long)player(Stat.BaseNumberOfGolemsAllowed) + player(Stat.NumberOfGolemsAllowed);
        if (player(Stat.PrimordialJewelCount) >= 3) value += player(Stat.NumberOfGolemsAllowedWith3PrimordialJewels);
        if (player(Stat.DoubleGolemLimit) != 0) value *= 2;
        return (int)Math.Clamp(value, 0, 100);
    }

    public static int BuffEffect(Definition definition, Func<uint, int> player, Func<uint, int> own, int count)
    {
        string family = definition.Kind switch { "Flame" => "fire", "Carrion" => "bone", _ => definition.Kind.ToLowerInvariant() };
        int effect = player(Stat.GolemBuffEffectPlusPct) + own(Stat.GolemBuffEffectPlusPct)
            + player(Stat.Of("base_" + family + "_golem_granted_buff_effect_+%"))
            + player(Stat.GolemBuffEffectPlusPctPerSummonedGolem) * count;
        if (definition.Kind is "Flame" or "Ice" or "Lightning") effect += player(Stat.ElementalGolemGrantedBuffEffectPlusPct);
        return Math.Max(-100, effect);
    }
}
