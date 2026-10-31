using System.Text.Json;


static class AntiquarianEffects
{
    public const string AdrenalineSource = "gain_adrenaline_when_ward_breaks_ms";
    public const string StunRecoverySource = "regenerate_%_life_over_1_second_when_stunned";
    public const ushort AdrenalineDefinition = 1090, WardBrokenDefinition = 1878;
    static readonly Lazy<IReadOnlyDictionary<uint, int>> adrenaline = new(() =>
    {
        using var templates = GameTables.Open("data/tables/BuffTemplates.json");
        using var definitions = GameTables.Open("data/tables/BuffDefinitions.json");
        var template = templates.RootElement[503];
        if (template.GetProperty("BuffDefinitionsKey").GetInt32() != AdrenalineDefinition)
            throw new InvalidDataException("Target Adrenaline template mismatch");
        var definition = definitions.RootElement[AdrenalineDefinition];
        return definition.GetProperty("StatsKeys").EnumerateArray().Select(k => (uint)(k.GetInt32() + 1))
            .Zip(template.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32()))
            .ToDictionary(p => p.First, p => p.Second);
    });
    public static IReadOnlyDictionary<uint, int> AdrenalineStats => adrenaline.Value;

    // PoB CalcOffence 16de4b8: attack-projectile Far Shot ramp, in the server's combat-distance units.
    // Use cumulative projectile travel, never the owner's live distance; spells and melee never use this ramp.
    public static double FarShot(double distance) => distance <= 10 ? .8 : distance < 25
        ? .8 + (distance - 10) * .2 / 15 : distance < 70 ? 1 + (distance - 25) * .6 / 45 : 1.6;

    public static int RangedPhysical(int mitigated, bool rangedAttack, Func<string, int> get) =>
        rangedAttack ? (int)Math.Clamp((long)mitigated + get("physical_ranged_attack_damage_taken_+"), 0, int.MaxValue)
            : mitigated;
}
