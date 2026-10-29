using System.Text.Json;

/// <summary>
/// Buffs a passive applies around the player: PassiveSkills.dat PassiveSkillBuffs (BuffsHardmode in Ruthless) →
/// BuffTemplates → BuffDefinitions. A template's Buff_StatValues are the values of its definition's GrantedStats, in
/// order (Stats row + 1 is the client key); AuraRadius is the "nearby" radius in cells (60: 6 m); __col4 lists who gets
/// it, read as 1 the player, 2 allies, 3 enemies (every Augury of Penitence, Occultist and Saboteur enemy aura has [3],
/// the monster auras that buff their pack [1, 2] or [2]; not confirmed in the client).
/// Only the templates listed here are applied, because their stats have a consumer: the enemy auras in
/// <see cref="EnemyAuraTemplates"/> (Program.EnemyDebuffsTick puts them on the monsters, Program.MonsterTakesMitigated
/// reads them) and the player-and-ally auras in <see cref="AllyAuraTemplates"/> (Program.TriggerElementalRelicLocked).
/// </summary>
static class PassiveBuffAuras
{
    /// <summary>An aura a passive puts around the player: its template, BuffDefinitions row, radius (cells) and stats.</summary>
    internal sealed record Aura(string Template, ushort Definition, int Radius, IReadOnlyList<(uint Key, int Value)> Stats);

    // On every hostile living monster within the radius while the player lives.
    static readonly HashSet<string> EnemyAuraTemplates =
    [
        "CustomAscendancyNearbyEnemiesIncreasedElementalDamageTaken", // Paladin Light of Divinity: 2551 Augury of Penitence
    ];

    // On the player and his allies within the radius.
    static readonly HashSet<string> AllyAuraTemplates =
    [
        "GuardianAlliesSpawnRelicsForYouOnKill", // Paladin Elemental Valiance: 2258 trigger_allied_guardian_relic_on_kill
    ];

    const int PlayerTarget = 1, AlliesTarget = 2, EnemiesTarget = 3;

    static readonly Lazy<IReadOnlyDictionary<ushort, Aura[]>> enemyAuras = new(() => LoadAuras("data", "PassiveSkillBuffs", EnemyAuraTemplates, [EnemiesTarget]));
    static readonly Lazy<IReadOnlyDictionary<ushort, Aura[]>> ruthlessEnemyAuras = new(() => LoadAuras("data", "BuffsHardmode", EnemyAuraTemplates, [EnemiesTarget]));
    static readonly Lazy<IReadOnlyDictionary<ushort, Aura[]>> allyAuras = new(() => LoadAuras("data", "PassiveSkillBuffs", AllyAuraTemplates, [PlayerTarget, AlliesTarget]));
    static readonly Lazy<IReadOnlyDictionary<ushort, Aura[]>> ruthlessAllyAuras = new(() => LoadAuras("data", "BuffsHardmode", AllyAuraTemplates, [PlayerTarget, AlliesTarget]));

    /// <summary>The enemy auras of these nodes (PassiveSkillGraphIds), one per template however many nodes grant it.</summary>
    internal static IReadOnlyList<Aura> EnemyAurasFor(IEnumerable<ushort> nodes, bool ruthless) => For(nodes, ruthless ? ruthlessEnemyAuras : enemyAuras);

    /// <summary>The player-and-ally auras of these nodes, one per template.</summary>
    internal static IReadOnlyList<Aura> AllyAurasFor(IEnumerable<ushort> nodes, bool ruthless) => For(nodes, ruthless ? ruthlessAllyAuras : allyAuras);

    static IReadOnlyList<Aura> For(IEnumerable<ushort> nodes, Lazy<IReadOnlyDictionary<ushort, Aura[]>> auras) =>
        [.. nodes.SelectMany(n => auras.Value.GetValueOrDefault(n) ?? []).DistinctBy(a => a.Template)];

    static IReadOnlyDictionary<ushort, Aura[]> LoadAuras(string folder, string column, HashSet<string> allowed, int[] targets)
    {
        string Table(string name) => Path.Combine(folder, "tables", $"{name}.json");
        var result = new Dictionary<ushort, Aura[]>();
        if (new[] { "PassiveSkills", "BuffTemplates", "BuffDefinitions" }.Any(t => !File.Exists(Table(t)))) return result;
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkills")));
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Table("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Table("BuffDefinitions")));
        Aura? Template(int row)
        {
            if (row < 0 || row >= templates.RootElement.GetArrayLength()) return null;
            var t = templates.RootElement[row];
            if (t.GetProperty("Id").GetString() is not { } id || !allowed.Contains(id)
                || t.GetProperty("AuraRadius").GetInt32() is not (> 0 and var radius)
                || !t.GetProperty("__col4").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(targets)
                || t.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } key) return null;
            int definition = key.GetInt32();
            if (definition < 0 || definition > ushort.MaxValue || definition >= definitions.RootElement.GetArrayLength()) return null;
            var granted = definitions.RootElement[definition].GetProperty("GrantedStats").EnumerateArray().Select(s => s.GetInt32()).ToList();
            var values = t.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32()).ToList();
            // A template whose values do not line up with its definition's stats is left out rather than misapplied.
            if (granted.Count != values.Count) return null;
            return new(id, (ushort)definition, radius, [.. granted.Zip(values, (stat, value) => ((uint)(stat + 1), value))]);
        }
        foreach (var row in passives.RootElement.EnumerateArray())
            if (row.GetProperty("PassiveSkillGraphId").GetInt32() is > 0 and <= ushort.MaxValue and var node
                && row.TryGetProperty(column, out var buffs) && buffs.ValueKind == JsonValueKind.Array
                && buffs.EnumerateArray().Select(b => Template(b.GetInt32())).OfType<Aura>().ToArray() is { Length: > 0 } auras)
                result.TryAdd((ushort)node, auras);
        return result;
    }
}
