using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Dread's chaos Firestorm (<c>EssenceChaosstorm</c>): a storm over the target that drops a chaos ball every
/// <c>fire_storm_fireball_delay_ms</c> for its duration, each an area hit where it lands — the chaos version of Firestorm the
/// wiki describes. The skill carries <c>firestorm_use_server_effects</c>, so the server places the impacts itself.
/// <para>The hit kit cannot represent it: the effect does carry spell damage (BaseResolvedValues paired with FloatStats,
/// 64–96 chaos at its top gem level), so <c>BossKits.AttackFor</c> kept it — but as one instant hit at the action's contact,
/// which is not a storm. A Firestorm is a series of delayed impacts over its duration, so it is cast here instead.
/// Its Guardian Golem is the same daemon's other effect (<c>EssenceDaemonSummonChaosGolem</c>, alternate_minion 165 →
/// <c>Metadata/Monsters/ChaosElemental/EssenceChaosElemental</c>, which fights with EssenceChaosCascade and
/// EssenceAbyssalCry) and stays in the hit kit.</para></summary>
public static partial class MapEssences
{
    const string ChaosstormArt = "Metadata/Effects/Spells/chaosstorm/rig.ao";
    const string ChaosImpactArt = "Metadata/Effects/Spells/monsters_effects/League_Incursion/diversefx/projectiles/chaos/chaosimpact.ao";

    static Task CastChaosstorm(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell)
        => CastStorm(conn, monster, target, spell, DamageType.Chaos, ChaosImpactArt, ChaosstormArt, "chaosstorm");

    /// <summary>A Firestorm-shaped essence spell: a storm over the target within the skill's own action distance, dropping
    /// one ball every <c>fire_storm_fireball_delay_ms</c> for its duration, each an area hit where it lands with its own
    /// impact art. Shared by Dread's chaos Firestorm and Anguish's meteor Firestorm — the same skill shape, a different
    /// element and art. The hit kit cannot represent either: it would land one instant hit at the action's contact.</summary>
    static async Task CastStorm(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell,
        DamageType type, string impactArt, string stormArt, string label)
    {
        var catalog = SkillsFor(conn);
        var stats = catalog.EffectStats(spell.Effect, catalog.MaxLevelAt(spell.Effect, monster.Level)).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // The storm's own area, then Firestorm's separate area modifier on top (the strongbox storms read it the same way).
        double area = SkillArea.Radius(SkillArea.BaseRadius(catalog.EffectId(spell.Effect), Get), Get, true)
            * Math.Sqrt(Math.Max(0, 1 + Get("firestorm_base_area_of_effect_+%") / 100.0));
        int impact = Math.Max(4, (int)Math.Round(area / 2));
        double delay = Math.Max(0.05, Get("fire_storm_fireball_delay_ms") / 1000.0);
        double seconds = Math.Max(delay, Get("base_skill_effect_duration") / 1000.0);
        var damage = catalog.HitDamage(spell.Effect, monster.Level);
        // The storm is aimed where the target stands, pulled into the skill's own reach: its
        // spell_maximum_action_distance_+% is -50, half the rare's normal casting distance.
        var from = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var aim = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
        double reach = Math.Max(8, (monster.Variety?.AggroRange ?? 60) * Math.Max(0, 1 + Get("spell_maximum_action_distance_+%") / 100.0));
        double dx = aim.X - from.X, dy = aim.Y - from.Y, dist = Math.Sqrt(dx * dx + dy * dy);
        (int X, int Y) at = dist <= reach || dist < 1 ? aim
            : ((int)Math.Round(from.X + dx / dist * reach), (int)Math.Round(from.Y + dy / dist * reach));
        uint storm = SpawnEffect(conn, at, stormArt);
        _ = RemovePulseLater(conn, storm, seconds + 0.5);
        int balls = 0;
        for (double t = 0; t < seconds && !conn.Closed && !conn.LeavingArea; t += delay)
        {
            double angle = Random.Shared.NextDouble() * Math.Tau, d = Math.Sqrt(Random.Shared.NextDouble()) * area;
            var spot = conn.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * d), at.Y + (int)Math.Round(Math.Sin(angle) * d)));
            uint hit = SpawnEffect(conn, spot, impactArt);
            _ = RemovePulseLater(conn, hit, 0.8);
            foreach (var player in PlayersNear(conn, spot, impact).ToArray())
            {
                if (player.Immune || InGracePeriod(player)) continue;
                int amount = (int)Math.Clamp(damage.Roll(Random.Shared).MinTotal, 0, int.MaxValue);
                if (amount <= 0 || !TrySyntheticEnemyHit(player, type, amount, block: true, spell: true, suppress: true, out _)) continue;
                var (remote, stream, ciphers) = player.Wire;
                await SendWorldRecord(remote, stream, ciphers, player, PlayerId,
                    InstanceProtocol.LifeUpdate(PlayerId, player.PlayerLife, player.PlayerReservedLife), $"essence {label}");
                await SendPlayerEnergyShield(remote, stream, ciphers, player);
                if (player.PlayerLife == 0) await PlayerDied(remote, stream, ciphers, player, $"death from an essence {label}");
            }
            balls++;
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
        Console.WriteLine($"[map-content] essence {label} by 0x{monster.ObjectId:x} at ({at.X}, {at.Y}): {balls} balls every {delay:0.##}s, area {area:0}, impact {impact}");
    }
}