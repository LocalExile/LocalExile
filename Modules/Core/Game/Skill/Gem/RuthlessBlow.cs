using System.Runtime.CompilerServices;

// Ruthless Support (poedb): "Every third Attack with Supported Melee Attacks deals a Ruthless Blow", with 70% more melee
// damage at level 1 and 98% at 20 (support_ruthless_big_hit_damage_+%_final); the third is
// support_ruthless_big_hit_max_count (3). Each supported skill keeps its own count and a use counts whether or not it
// hits (the wiki), so the count is taken as the use starts.
static partial class Game
{
    static readonly ConditionalWeakTable<ClientState, Dictionary<ushort, int>> RuthlessCounts = new();

    // This use's more melee hit damage: 1 for a skill without Ruthless or a use that is not its count's last.
    internal static double RuthlessBlowMore(Dictionary<ushort, int> counts, ushort skillId, Func<string, int> skill)
    {
        int max = skill("support_ruthless_big_hit_max_count");
        if (max <= 0) return 1;
        int count = counts.GetValueOrDefault(skillId) + 1;
        bool blow = count >= max;
        counts[skillId] = blow ? 0 : count;
        return blow ? Math.Max(0, 1 + skill("support_ruthless_big_hit_damage_+%_final") / 100.0) : 1;
    }

    internal static double RuthlessBlowMore(ClientState conn, ushort skillId, Func<string, int> skill)
    {
        lock (conn.Monsters) return RuthlessBlowMore(RuthlessCounts.GetValue(conn, _ => []), skillId, skill);
    }
}
