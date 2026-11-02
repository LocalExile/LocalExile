/// <summary>
/// Ice Nova's ring: from its release the nova expands to its radius over 0.2 s (0.25 s with skill_art_variation 3) and
/// hits each target when the ring reaches it. The IceNova action's update (0x141c00180, vtable 0x14353b5b0 slot 0x118)
/// grows the ring by elapsed / duration × active_skill_area_of_effect_radius and hits every target whose distance is
/// within it, so a monster at the edge takes its hit one ring duration after one at the centre.
/// </summary>
static class IceNovaRing
{
    /// <summary>ActionTypes "IceNova" HASH16 (row 34): the action whose constructor 0x141bff6a0 takes it.</summary>
    public const ushort Action = 0x2748;

    public static double Seconds(int artVariation) => artVariation == 3 ? 0.25 : 0.2;

    /// <summary>Seconds after the release the ring reaches a target <paramref name="distance"/> cells from the centre.</summary>
    public static double Reach(double distance, int radius, int artVariation) =>
        Seconds(artVariation) * Math.Clamp(distance / Math.Max(1, radius), 0, 1);
}
