using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using DamageConversion = GameServer.Core.World.DamageConversion;

public static partial class Game
{
    // The conversion the player's passive enemy auras give this monster's hit when it stands within their radius of the
    // player at contact (Born under Fire: base_physical_damage_%_to_convert_to_fire), by the player's own conversion rules
    // (DamageStats.Conversion); null when none applies. Shaper of Flames' "Enemies Ignited by you have 40% of Physical
    // Damage converted to Fire Damage" joins it while the monster burns with the player's ignite (<paramref name="stat"/>:
    // the player's merged stats). Call in the area's frame.
    public static DamageConversion? EnemyAuraConversionLocked(Player player, SpawnedMonster m, (int X, int Y) playerAt, double now,
        Func<uint, int>? stat = null)
    {
        if (!m.Hostile || player.PlayerLife == 0) return null;
        var at = m.Walk.Position(now);
        var stats = new Dictionary<uint, int>();
        foreach (var aura in PassiveBuffAuras.EnemyAurasFor([.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated], LeagueFor(player).HardMode, EnemyAuraActs))
            if (aura.Stats.Count > 0 && Dist(at, playerAt) <= aura.Radius)
                foreach (var (key, value) in aura.Stats) stats[key] = stats.GetValueOrDefault(key) + value;
        int ignited = stat?.Invoke(Stat.EnemiesIgnitedByYouHavePhysicalDamagePctConvertedToFire) ?? 0;
        if (ignited > 0 && m.Ignites.Burning && Stat.BasePhysicalDamagePctToConvertToFire is var fireKey)
        {
            stats[fireKey] = stats.GetValueOrDefault(fireKey) + ignited;
            Console.WriteLine($"[gateway] monster ({m.Label}) is ignited: {ignited}% of its physical damage converts to fire");
        }
        var conversion = DamageStats.Conversion(stats, Stats, attack: true);
        return conversion.IsEmpty ? null : conversion;
    }

    // The sum of one stat of the player's passive enemy auras on this monster at contact, by the radius at that moment
    // (Overwhelm's base_critical_strike_multiplier_+ for its critical strikes on the player). Call in the area's frame.
    public static int EnemyAuraStatLocked(Player player, SpawnedMonster m, (int X, int Y) playerAt, double now, uint stat)
    {
        if (!m.Hostile || player.PlayerLife == 0 || !(stat is var key)) return 0;
        var at = m.Walk.Position(now);
        long sum = 0;
        foreach (var aura in PassiveBuffAuras.EnemyAurasFor([.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated], LeagueFor(player).HardMode, EnemyAuraActs))
            if (Dist(at, playerAt) <= aura.Radius)
                foreach (var (s, value) in aura.Stats) if (s == key) sum += value;
        return (int)Math.Clamp(sum, int.MinValue, int.MaxValue);
    }

    // Whether the server applies this enemy aura: by name (a module's own, the blind) or because every stat it has is a
    // hit conversion (Born under Fire). The others' buffs would show effects that do not happen, so they are not sent
    // until their stats have consumers.
    public static bool EnemyAuraActs(PassiveBuffAuras.Aura aura) => aura.Enemies && aura.Radius > 0
        && (PassiveBuffAuras.AppliedByName(aura) || aura.Stats.Count > 0 && aura.Stats.All(s =>
            !DamageStats.Conversion(new Dictionary<uint, int> { [s.Key] = s.Value }, Stats, attack: true).IsEmpty));
}
