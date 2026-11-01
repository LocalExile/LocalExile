using GameServer.Core.World;

// "Deal up to X% more Melee Damage to Enemies, based on proximity" (stat descriptions): Slayer's Impact
// (slayer_damage_+%_final_from_distance), Close Combat (support_slashing_damage_+%_final_from_distance) and Shield Crush
// (shield_crush_damage_+%_final_from_distance), each its own more multiplier. The ramp is PoB ModStore's MeleeProximity
// {1,0}: all of it up to 15 units, falling linearly to none at 40. The distance is centre to centre at the hit, as for
// close range (TargetAccuracy).
static partial class Game
{
    static readonly string[] MeleeProximityStats = ["slayer_damage_+%_final_from_distance",
        "support_slashing_damage_+%_final_from_distance", "shield_crush_damage_+%_final_from_distance"];

    internal static double MeleeProximityMore(Func<string, int> get, double distance)
    {
        double ramp = distance <= 15 ? 1 : distance >= 40 ? 0 : (40 - distance) / 25;
        return MeleeProximityStats.Aggregate(1.0, (more, name) => more * Math.Max(0, 1 + get(name) * ramp / 100.0));
    }

    // One melee hit on this target, with the player's and the skill's proximity stats. Call under the Monsters lock.
    internal static DamagePacket WithMeleeProximityLocked(ClientState conn, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill, SpawnedMonster target, DamagePacket hit, double at)
    {
        int Get(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) + (skill?.GetValueOrDefault(k) ?? 0) : 0;
        double more = MeleeProximityMore(Get, Dist(conn.Walk.Position(at), target.Walk.Position(at)));
        return more == 1 ? hit : hit.Scale(more);
    }
}
