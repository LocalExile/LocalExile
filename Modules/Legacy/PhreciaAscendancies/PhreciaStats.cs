using GameServer.Core.World;

namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>The stats only this family's nodes give, at the game's hand-overs. The game's rules do not name them: each
/// stays in the node's stats (the client is sent it) and is given its effect here.</summary>
internal static class PhreciaStats
{
    /// <summary>The Polytheist's Devout of Ryslatha, "30% more Maximum Attack Damage"
    /// (<c>maximum_attack_damage_+%_final_from_ascendancy</c>): the maximum of every final component of an attack's hit.
    /// PoB CalcOffence applies MaxDamage MORE to each type's own maximum and carries it through conversion, which is the
    /// same. The client folds its sources into <c>combined_maximum_attack_damage_+%_final</c> as a product
    /// (FUN_1425d9160).</summary>
    internal static DamagePacket MaximumAttackDamage(DamagePacket hit, Func<uint, int> get)
    {
        int percent = get(Stat.MaximumAttackDamagePlusPctFinalFromAscendancy);
        if (percent == 0) return hit;
        double factor = Math.Max(0, 1 + percent / 100.0);
        return hit.Select((_, range) => new(range.Min, range.Max * factor));
    }

    /// <summary>The Antiquarian's Whaletooth Bangle (<c>skill_effect_duration_+%_per_10_strength</c>): increased skill
    /// effect duration per complete ten of final Strength.</summary>
    internal static void DurationPerTenStrength(Player player, StatMap stats)
    {
        int perTen = stats.GetValueOrDefault(Stat.SkillEffectDurationPlusPctPer10Strength);
        if (perTen == 0) return;
        stats[Stat.SkillEffectDurationPlusPct] = (int)Math.Clamp(stats.GetValueOrDefault(Stat.SkillEffectDurationPlusPct)
            + (long)(Math.Max(0, stats.GetValueOrDefault(Stat.Strength)) / 10) * perTen, int.MinValue, int.MaxValue);
    }

    /// <summary>The Whisperer's "+# to Maximum Mana per Dexterity" (<c>X_mana_per_dexterity</c>): flat maximum Mana, which
    /// the pools take as <c>base_maximum_mana</c>.</summary>
    internal static void ManaPerDexterity(Player player, Dictionary<uint, int> sources,
        (int Strength, int Dexterity, int Intelligence) attributes)
    {
        int perDexterity = sources.GetValueOrDefault(Stat.XManaPerDexterity);
        if (perDexterity == 0) return;
        sources[Stat.BaseMaximumMana] = (int)Math.Clamp(sources.GetValueOrDefault(Stat.BaseMaximumMana)
            + (long)attributes.Dexterity * perDexterity, int.MinValue, int.MaxValue);
    }

    /// <summary>The Surfcaster's Screaming Reels (<c>cast_speed_+%_final_while_holding_fishing_rod</c>): more cast speed
    /// while the active main hand is a Fishing Rod, which the client's calculator tests as main_hand_weapon_type 15.</summary>
    internal static int CastSpeedWithFishingRod(Player player, Func<uint, int> get) =>
        get(Stat.CastSpeedPlusPctFinalWhileHoldingFishingRod) is int more and not 0
            && global::Game.ActiveMainHand(player)?.Type?.Class == "FishingRod" ? more : 0;
}
