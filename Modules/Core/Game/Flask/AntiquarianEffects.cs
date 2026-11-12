using System.Text.Json;
using GameServer.Core;


public static class AntiquarianEffects
{
    public const string AdrenalineSource = "gain_adrenaline_when_ward_breaks_ms";
    public const string StunRecoverySource = "regenerate_%_life_over_1_second_when_stunned";
    public const ushort AdrenalineDefinition = 1090, WardBrokenDefinition = 1878;
    static readonly Lazy<IReadOnlyDictionary<uint, int>> adrenaline = new(() =>
    {
        var templates = ClientTables.Table("BuffTemplates");
        var definitions = ClientTables.Table("BuffDefinitions");
        var template = templates[503];
        if (template.Int("BuffDefinitionsKey") != AdrenalineDefinition)
            throw new InvalidDataException("Target Adrenaline template mismatch");
        var definition = definitions[AdrenalineDefinition];
        return definition.Ints("StatsKeys").Select(k => (uint)(k + 1))
            .Zip(template.Ints("Buff_StatValues"))
            .ToDictionary(p => p.First, p => p.Second);
    });
    public static IReadOnlyDictionary<uint, int> AdrenalineStats => adrenaline.Value;

    // PoB CalcOffence 16de4b8: attack-projectile Far Shot ramp, in the server's combat-distance units.
    // Use cumulative projectile travel, never the owner's live distance; spells and melee never use this ramp.
    public static double FarShot(double distance) => distance <= 10 ? .8 : distance < 25
        ? .8 + (distance - 10) * .2 / 15 : distance < 70 ? 1 + (distance - 25) * .6 / 45 : 1.6;

    // PoB CalcOffence: the Point Blank keystone's 30% more, ramped {{10,1},{35,0},{120,-1}} over the same travel; it
    // multiplies with Far Shot when both apply.
    public static double PointBlank(double distance) => distance <= 10 ? 1.3 : distance < 35
        ? 1.3 - (distance - 10) * .3 / 25 : distance < 120 ? 1 - (distance - 35) * .3 / 85 : .7;

    public static int RangedPhysical(int mitigated, bool rangedAttack, Func<string, int> get) =>
        rangedAttack ? (int)Math.Clamp((long)mitigated + get("physical_ranged_attack_damage_taken_+"), 0, int.MaxValue)
            : mitigated;
}
