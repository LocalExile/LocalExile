using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static readonly (string Name, string Stat, int Value)[] JourneyTattoos =
    [
        ("Journey Tattoo of the Body", "base_maximum_life", 25),
        ("Journey Tattoo of the Mind", "base_maximum_mana", 30),
        ("Journey Tattoo of the Soul", "base_maximum_energy_shield", 40),
    ];

    /// <summary>The counting tattoos' aggregate stats: Journey Body/Mind/Soul grant their flat pool per allocated
    /// copy, and Ancestral Bloodlines grants recovery per distinct tribe among the allocated tattoos. The marker
    /// stats on each node are ignored by consumers; this pass emits the real stat keys once per character.</summary>
    public static Dictionary<uint, int> TattooCountingStats(IReadOnlyDictionary<ushort, string> tattoos,
        IReadOnlySet<ushort> allocated, IReadOnlyDictionary<ushort, int>? tattooEffect = null)
    {
        var result = new Dictionary<uint, int>();
        int PerTattoo(string name, int value)
        {
            var copies = tattoos.Where(kv => allocated.Contains(kv.Key) && kv.Value == name).ToArray();
            return copies.Sum(kv => RadiusJewelCatalogue.ScaleEffect(value, tattooEffect?.GetValueOrDefault(kv.Key) ?? 0));
        }
        void Add(string stat, int value)
        {
            if (Stats.Key(stat) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        }
        foreach (var (name, stat, value) in JourneyTattoos)
        {
            int copies = tattoos.Count(kv => allocated.Contains(kv.Key) && kv.Value == name);
            if (copies > 0) Add(stat, copies * PerTattoo(name, value));
        }
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
            int recovery = tribes * PerTattoo("Ancestral Tattoo of Bloodlines", 2);
            Add("life_recovery_rate_+%", recovery);
            Add("mana_recovery_rate_+%", recovery);
            Add("energy_shield_recovery_rate_+%", recovery);
        }
        return result;
    }
}
