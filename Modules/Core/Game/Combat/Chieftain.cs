using GameServer.Core.Protocol.Instance;

/// <summary>
/// Chieftain notables whose stats are not ordinary sums (Agents/docs/game/ascendancies.md). A stat from any source
/// uses the same rule. Ngamahu, Tawhoa, Ramako and Sione are not handled here.
/// </summary>
public static class Chieftain
{
    public const string FireResistanceShare =
        "modifiers_to_fire_resistance_also_apply_to_cold_lightning_resistance_at_%_value";
    public const string MaxFireResistanceShare =
        "modifiers_to_maximum_fire_resistance_apply_to_maximum_cold_and_lightning_resistance";
    public const string FireExplodeChance = "explode_enemies_for_500%_life_as_fire_on_kill_%_chance";
    public const string AncestralCall = "chieftain_body_armour_supported_by_level_x_ancestral_call";
    public const string FistOfWar = "chieftain_body_armour_supported_by_level_x_fist_of_war";

    public const string AncestralCallEffect = "SupportAncestralCall";
    public const string FistOfWarEffect = "SupportFistofWar";

    /// <summary>Hinekora, Death's Fury: the percent is in the stat id, the value is the chance.</summary>
    public const int FireExplodeLifePercent = 500;

    /// <summary>Socket index above real sockets and item-granted supports, so the injected gems do not collide.</summary>
    public const int BodyArmourSupportSocket = 2000;

    /// <summary>The fire blast already used for Herald of Ash. Its clip length is not measured; the kill-explosion
    /// default of two seconds removes the object.</summary>
    public const string FireExplosionAo = "Metadata/Effects/Spells/herald/fire/flameherald_explode.ao";

    /// <summary>Percent of the fire-resistance modifiers, truncating toward zero, added to cold and lightning.</summary>
    public static int Shared(int modifiers, int percent) =>
        percent == 0 || modifiers == 0 ? 0 : (int)(modifiers * (long)percent / 100);

    /// <summary>The whole maximum-fire-resistance modifier total, when the flag is set.</summary>
    public static int SharedMaximum(int fireMaximumModifiers, int flag) => flag == 0 ? 0 : fireMaximumModifiers;

    public static int LifeAsFire(uint maximumLife) =>
        (int)Math.Clamp(maximumLife * (long)FireExplodeLifePercent / 100, 0, int.MaxValue);

    /// <summary>Ancestral Call and Fist of War at the stat's level, for skills socketed in body armour.</summary>
    public static IEnumerable<GemSupports.Candidate> BodyArmourSupports(Func<string, int> get)
    {
        int socket = BodyArmourSupportSocket;
        foreach (var (id, level) in new[] { (AncestralCallEffect, get(AncestralCall)), (FistOfWarEffect, get(FistOfWar)) })
        {
            if (level <= 0) continue;
            var effect = Game.Supports.Effects.FirstOrDefault(e => e.Id == id);
            if (effect is null) continue;
            yield return new GemSupports.Candidate(effect.Row, level, 0, socket++);
        }
    }
}
