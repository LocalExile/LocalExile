using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using DamageConversion = GameServer.Core.World.DamageConversion;

static partial class Game
{
    // The conversion the player's passive enemy auras give this monster's hit when it stands within their radius of the
    // player at contact (Born under Fire: base_physical_damage_%_to_convert_to_fire), by the player's own conversion rules
    // (DamageStats.Conversion); null when none applies. Call under the Monsters lock.
    internal static DamageConversion? EnemyAuraConversionLocked(ClientState conn, SpawnedMonster m, (int X, int Y) player, double now)
    {
        if (!m.Hostile || conn.PlayerLife == 0) return null;
        var at = m.Walk.Position(now);
        var stats = new Dictionary<uint, int>();
        foreach (var aura in PassiveBuffAuras.EnemyAurasFor([.. conn.Passives, .. conn.MainAscendancy.Allocated], LeagueFor(conn).HardMode, EnemyAuraActs))
            if (aura.Stats.Count > 0 && Dist(at, player) <= aura.Radius)
                foreach (var (stat, value) in aura.Stats) stats[stat] = stats.GetValueOrDefault(stat) + value;
        var conversion = DamageStats.Conversion(stats, Stats, attack: true);
        return conversion.IsEmpty ? null : conversion;
    }

    // Whether the server applies this enemy aura: by name (Light of Divinity, the blind) or because every stat it has is a
    // hit conversion (Born under Fire). The others' buffs would show effects that do not happen, so they are not sent
    // until their stats have consumers.
    internal static bool EnemyAuraActs(PassiveBuffAuras.Aura aura) => aura.Enemies && aura.Radius > 0
        && (PassiveBuffAuras.AppliedByName(aura) || aura.Stats.Count > 0 && aura.Stats.All(s =>
            !DamageStats.Conversion(new Dictionary<uint, int> { [s.Key] = s.Value }, Stats, attack: true).IsEmpty));
}
