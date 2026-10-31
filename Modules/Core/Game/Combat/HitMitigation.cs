using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageType = GameServer.Core.World.DamageType;
using HitResistances = GameServer.Core.World.HitResistances;


/// <summary>
/// The mitigation of the player's outgoing hits. Every damage type of a rolled hit is reduced by the target monster's
/// own base resistance (MonsterResistanceCatalog: MonsterTypes.Resistances → MonsterResistances, the level's
/// DefaultMonsterStats.Difficulty columns), the same per-type pass the monster swings use against the player's
/// resistances. Without the extracted tables every type reads 0, so a fresh clone's hits land unchanged.
/// Penetration subtracts from the capped base in <see cref="DamageModel"/>; damage over time and the physical part
/// have no penetration (physical has no base resistance here at all). A hit's critical-strike elemental rules
/// (Inevitable Judgement's ignore and non-critical penetration) resolve their per-type final resistance through
/// <see cref="HitResistances"/> before the same pass applies it.
/// </summary>
static class HitMitigation
{
    /// <summary>The resistance one type of a hit on this monster faces: the variety's base resistance at the level,
    /// or 0 without the tables or a malformed row (which leaves the type unmitigated).</summary>
    public static DamageModel.ResistanceValue Resistance(MonsterCatalog monsters, MonsterCatalog.Variety? variety, int level, DamageType type) =>
        variety is not null && monsters.BaseResistance(variety, level, type) is int value
            ? new DamageModel.ResistanceValue(value)
            : default;

    /// <summary>One roll per type of the hit, then each type's own resistance, kept fractional per type and summed;
    /// the total rounds once, exactly the rounding the unmitigated <c>RollTotal</c> path had. The critical strike
    /// multiplier applies to this total afterwards. The hit paths roll first and then use <see cref="Mitigated"/>,
    /// so the critical draw can keep its original place after the per-type rolls.</summary>
    public static int RollMitigated(DamagePacket hit, Func<DamageType, DamageModel.ResistanceValue> resistance, Random random) =>
        (int)Math.Round(DamageModel.Mitigate(hit.Roll(random), resistance).MinTotal);

    /// <summary>The mitigation of an already-rolled hit: each type's own resistance, kept fractional per type and
    /// summed, the total rounded once. It draws nothing — the packet was rolled before — so the critical-strike
    /// elemental rules resolve with the hit's own critical boolean without a second roll (even a fixed range
    /// consumes a draw when rolled).</summary>
    public static int Mitigated(DamagePacket rolled, Func<DamageType, DamageModel.ResistanceValue> resistance) =>
        (int)Math.Round(DamageModel.Mitigate(rolled, resistance).MinTotal);
}
