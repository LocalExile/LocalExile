using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    /// <summary>The counting tattoos' aggregate stats: Journey Body/Mind/Soul grant their flat pool per allocated
    /// copy, and Ancestral Bloodlines grants recovery per distinct tribe among the allocated tattoos. The marker
    /// stats on each node are ignored by consumers; this pass emits the real stat keys once per character.</summary>
    internal static Dictionary<uint, int> TattooCountingStats(IReadOnlyDictionary<ushort, string> tattoos,
        IReadOnlySet<ushort> allocated)
    {
        var result = new Dictionary<uint, int>();
        int Count(string name) => tattoos.Count(kv => allocated.Contains(kv.Key) && kv.Value == name);
        void Add(string stat, int value)
        {
            if (Stats.Key(stat) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        }
        int body = Count("Journey Tattoo of the Body");
        if (body > 0) Add("base_maximum_life", 25 * body);
        int mind = Count("Journey Tattoo of the Mind");
        if (mind > 0) Add("base_maximum_mana", 30 * mind);
        int soul = Count("Journey Tattoo of the Soul");
        if (soul > 0) Add("base_maximum_energy_shield", 40 * soul);
        bool ancestral = tattoos.Any(kv => allocated.Contains(kv.Key)
            && kv.Value == "Ancestral Tattoo of Bloodlines");
        int tribes = ancestral
            ? tattoos
                .Where(kv => allocated.Contains(kv.Key))
                .Select(kv => TattooCatalogue.Find(kv.Value)?.Tribe ?? 11)
                .Where(tribe => tribe is >= 0 and <= 10)
                .Distinct()
                .Count()
            : 0;
        if (tribes > 0)
        {
            Add("life_recovery_rate_+%", 2 * tribes);
            Add("mana_recovery_rate_+%", 2 * tribes);
            Add("energy_shield_recovery_rate_+%", 2 * tribes);
        }
        return result;
    }
}
