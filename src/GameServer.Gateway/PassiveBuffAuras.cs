using System.Text.Json;

/// <summary>
/// Buffs a passive applies around the player: PassiveSkills.dat PassiveSkillBuffs → BuffTemplates → BuffDefinitions.
/// Ruthless reads BuffsHardmode only for a row with HasHardmodeAlternate, as PassiveTree does for the stats: every other
/// row's BuffsHardmode is either a copy or empty (Elemental Valiance, Unwavering Crusade, Heartstopper), and Ruthless
/// relics do trigger (3.23.0: Unwavering Crusade's relics capped at level 80 there).
/// A template's Buff_StatValues are the values of its definition's GrantedStats, in order (Stats row + 1 is the client
/// key); AuraRadius is the "nearby" radius in cells (60: 6 m); __col4 lists who gets
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
        // Paladin Elemental Valiance and the Guardian's Unwavering Crusade: 2258 trigger_allied_guardian_relic_on_kill
        "GuardianAlliesSpawnRelicsForYouOnKill",
    ];

    const int PlayerTarget = 1, AlliesTarget = 2, EnemiesTarget = 3;

    // One parse of the three tables per mode gives both maps.
    sealed record Maps(IReadOnlyDictionary<ushort, Aura[]> Enemy, IReadOnlyDictionary<ushort, Aura[]> Ally);
    static readonly Lazy<Maps> normal = new(() => LoadAuras("data", ruthless: false));
    static readonly Lazy<Maps> ruthlessMaps = new(() => LoadAuras("data", ruthless: true));

    /// <summary>The enemy auras of these nodes (PassiveSkillGraphIds), one per template however many nodes grant it.</summary>
    internal static IReadOnlyList<Aura> EnemyAurasFor(IEnumerable<ushort> nodes, bool ruthless) =>
        For(nodes, (ruthless ? ruthlessMaps : normal).Value.Enemy);

    /// <summary>The player-and-ally auras of these nodes, one per template.</summary>
    internal static IReadOnlyList<Aura> AllyAurasFor(IEnumerable<ushort> nodes, bool ruthless) =>
        For(nodes, (ruthless ? ruthlessMaps : normal).Value.Ally);

    static IReadOnlyList<Aura> For(IEnumerable<ushort> nodes, IReadOnlyDictionary<ushort, Aura[]> auras) =>
        [.. nodes.SelectMany(n => auras.GetValueOrDefault(n) ?? []).DistinctBy(a => a.Template)];

    static Maps LoadAuras(string folder, bool ruthless)
    {
        string Table(string name) => Path.Combine(folder, "tables", $"{name}.json");
        var enemy = new Dictionary<ushort, Aura[]>();
        var ally = new Dictionary<ushort, Aura[]>();
        if (new[] { "PassiveSkills", "BuffTemplates", "BuffDefinitions" }.Any(t => !File.Exists(Table(t)))) return new(enemy, ally);
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkills")));
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Table("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Table("BuffDefinitions")));
        Aura? Template(int row, HashSet<string> allowed, int[] targets)
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
        {
            if (row.GetProperty("PassiveSkillGraphId").GetInt32() is not (> 0 and <= ushort.MaxValue and var node)) continue;
            bool alternate = ruthless && row.TryGetProperty("HasHardmodeAlternate", out var hardmode) && hardmode.GetBoolean();
            if (!row.TryGetProperty(alternate ? "BuffsHardmode" : "PassiveSkillBuffs", out var buffs)
                || buffs.ValueKind != JsonValueKind.Array) continue;
            int[] rows = [.. buffs.EnumerateArray().Select(b => b.GetInt32())];
            void Add(Dictionary<ushort, Aura[]> map, HashSet<string> allowed, int[] targets)
            {
                if (rows.Select(r => Template(r, allowed, targets)).OfType<Aura>().ToArray() is { Length: > 0 } auras)
                    map.TryAdd((ushort)node, auras);
            }
            Add(enemy, EnemyAuraTemplates, [EnemiesTarget]);
            Add(ally, AllyAuraTemplates, [PlayerTarget, AlliesTarget]);
        }
        return new(enemy, ally);
    }
}
