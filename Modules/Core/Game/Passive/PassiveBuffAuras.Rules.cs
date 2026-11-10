using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using DamageConversion = GameServer.Core.World.DamageConversion;

public static partial class Game
{
    // The conversion the player's passive enemy auras give this monster's hit when it stands within their radius of the
    // player at contact (Born under Fire: base_physical_damage_%_to_convert_to_fire), by the player's own conversion rules
    // (DamageStats.Conversion); null when none applies. Shaper of Flames' "Enemies Ignited by you have 40% of Physical
    // Damage converted to Fire Damage" joins it while the monster burns with the player's ignite (<paramref name="stat"/>:
    // the player's merged stats). Call under the Monsters lock.
    public static DamageConversion? EnemyAuraConversionLocked(ClientState conn, SpawnedMonster m, (int X, int Y) player, double now,
        Func<string, int>? stat = null)
    {
        if (!m.Hostile || conn.PlayerLife == 0) return null;
        var at = m.Walk.Position(now);
        var stats = new Dictionary<uint, int>();
        foreach (var aura in PassiveBuffAuras.EnemyAurasFor(PassiveEffectNodes(conn), LeagueFor(conn).HardMode, EnemyAuraActs))
            if (aura.Stats.Count > 0 && Dist(at, player) <= aura.Radius)
                foreach (var (key, value) in aura.Stats) stats[key] = stats.GetValueOrDefault(key) + value;
        int ignited = stat?.Invoke("enemies_ignited_by_you_have_physical_damage_%_converted_to_fire") ?? 0;
        if (ignited > 0 && m.Ignites.Burning && Stats.Key("base_physical_damage_%_to_convert_to_fire") is uint fireKey)
        {
            stats[fireKey] = stats.GetValueOrDefault(fireKey) + ignited;
            Console.WriteLine($"[gateway] monster ({m.Label}) is ignited: {ignited}% of its physical damage converts to fire");
        }
        var conversion = DamageStats.Conversion(stats, Stats, attack: true);
        return conversion.IsEmpty ? null : conversion;
    }

    // The sum of one stat of the player's passive enemy auras on this monster at contact, by the radius at that moment
    // (Overwhelm's base_critical_strike_multiplier_+ for its critical strikes on the player). Call under the Monsters lock.
    public static int EnemyAuraStatLocked(ClientState conn, SpawnedMonster m, (int X, int Y) player, double now, string stat)
    {
        if (!m.Hostile || conn.PlayerLife == 0 || Stats.Key(stat) is not uint key) return 0;
        var at = m.Walk.Position(now);
        long sum = 0;
        foreach (var aura in PassiveBuffAuras.EnemyAurasFor([.. conn.Passives, .. conn.MainAscendancy.Allocated], LeagueFor(conn).HardMode, EnemyAuraActs))
            if (Dist(at, player) <= aura.Radius)
                foreach (var (s, value) in aura.Stats) if (s == key) sum += value;
        return (int)Math.Clamp(sum, int.MinValue, int.MaxValue);
    }

    // Whether the server applies this enemy aura: by name (Light of Divinity, the blind) or because every stat it has is a
    // hit conversion (Born under Fire). The others' buffs would show effects that do not happen, so they are not sent
    // until their stats have consumers.
    public static bool EnemyAuraActs(PassiveBuffAuras.Aura aura) => aura.Enemies && aura.Radius > 0
        && (PassiveBuffAuras.AppliedByName(aura) || aura.Stats.Count > 0 && aura.Stats.All(s =>
            !DamageStats.Conversion(new Dictionary<uint, int> { [s.Key] = s.Value }, Stats, attack: true).IsEmpty));
}
