using GameServer.Core;

public static partial class Game
{
    // Skill-specific stats (fx_skill_stats): a character's stat that is a general stat for one skill ("40% increased
    // Fireball Damage"). Where a skill's own stats are put together (SupportedSkillAt, after the supports) the value
    // the character has is added to the skill's general stat, so whatever reads that stat from a skill reads it.
    // Worked out once for each load of the game data, by the skill's GrantedEffects row; a skill no row names costs
    // one lookup. Only while the effect rules are on.
    sealed record SkillStatTable(object Rows, Dictionary<int, (uint Stat, uint Gives, double Scale)[]> ByEffect);
    static SkillStatTable _skillStats = new(new object(), []);

    static Dictionary<int, (uint Stat, uint Gives, double Scale)[]> SkillStatsByEffect()
    {
        var rows = GameData.FxSkillStats;
        var cached = Volatile.Read(ref _skillStats);
        if (ReferenceEquals(cached.Rows, rows)) return cached.ByEffect;
        var byEffect = new Dictionary<int, List<(uint, uint, double)>>();
        if (rows.Count > 0 && ClientTables.Has("GrantedEffects") && ClientTables.Has("ActiveSkills"))
        {
            // A skill's names: its ActiveSkills Id, and its GrantedEffects Id in lower case with underscores.
            var active = ClientTables.Table("ActiveSkills");
            var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var effect in ClientTables.Table("GrantedEffects"))
            {
                void Name(string? name)
                {
                    if (name is not { Length: > 0 }) return;
                    if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
                    if (!list.Contains(effect.Index)) list.Add(effect.Index);
                }
                Name(SnakeCase(effect.Text("Id") ?? ""));
                if (effect.IntOrNull("ActiveSkill") is int key && active.Row(key) is { } skill) Name(skill.Text("Id")?.ToLowerInvariant());
            }
            int unknown = 0;
            foreach (var row in rows)
            {
                if (Stats.Key(row.Stat) is not uint stat || Stats.Key(row.Gives) is not uint gives || !byName.TryGetValue(row.Skill, out var effects)) { unknown++; continue; }
                foreach (int effect in effects)
                {
                    if (!byEffect.TryGetValue(effect, out var list)) byEffect[effect] = list = [];
                    list.Add((stat, gives, row.Scale));
                }
            }
            Console.WriteLine($"[rules] {rows.Count - unknown} skill-specific stat(s) for {byEffect.Count} skill(s) from the fx tables"
                + (unknown > 0 ? $"; {unknown} left out (unknown stat or skill)" : ""));
        }
        var worked = new SkillStatTable(rows, byEffect.ToDictionary(p => p.Key, p => p.Value.ToArray()));
        Volatile.Write(ref _skillStats, worked);
        return worked.ByEffect;
    }

    static string SnakeCase(string id)
    {
        var text = new System.Text.StringBuilder(id.Length + 8);
        for (int i = 0; i < id.Length; i++)
        {
            if (i > 0 && char.IsUpper(id[i])) text.Append('_');
            text.Append(char.ToLowerInvariant(id[i]));
        }
        return text.ToString();
    }

    /// <summary>Adds what the character has for this skill alone to the skill's own stats.</summary>
    public static void ApplySkillSpecificStats(int effect, Dictionary<uint, int> own, Func<uint, int> character)
    {
        if (!EffectRules.Enabled || !SkillStatsByEffect().TryGetValue(effect, out var rows)) return;
        foreach (var (stat, gives, scale) in rows)
            if (character(stat) is int value and not 0)
                own[gives] = ActorScale.Saturate((long)own.GetValueOrDefault(gives) + (long)Math.Round(value * scale, MidpointRounding.AwayFromZero));
    }
}
