using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Suffering's Frost Bomb (<c>EssenceFrostBomb</c>): a frost crystal forms at the target, then discharges after its
/// windup — cold damage and −30% cold resistance (cold exposure) to the players still in its radius. Kept apart from the other
/// essence spells so a change here cannot disturb them. (Its on-death Arctic Golem summon is a separate mechanic.)</summary>
public static partial class MapEssences
{
    const string FrostBombCrystalArt = "Metadata/Effects/Spells/frost_bomb/ice_crystal_loop.ao";
    const string FrostBombExplosionArt = "Metadata/Effects/Spells/frost_bomb/ice_discharge.ao";

    static async Task CastFrostBomb(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell)
    {
        var catalog = SkillsFor(conn);
        var stats = catalog.EffectStats(spell.Effect, catalog.MaxLevelAt(spell.Effect, monster.Level)).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double radius = Math.Max(1, SkillArea.Radius(Get("active_skill_base_area_of_effect_radius"), Get, true));
        double windup = Math.Max(.5, Get("base_secondary_skill_effect_duration") / 1000.0);
        double exposureSeconds = Math.Max(1, Get("base_skill_effect_duration") / 1000.0);
        int exposure = Get("base_cold_damage_resistance_%");
        var damage = catalog.HitDamage(spell.Effect, monster.Level);
        var at = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
        uint crystal = SpawnEffect(conn, at, FrostBombCrystalArt, 2f);
        await Task.Delay(TimeSpan.FromSeconds(windup));
        RemoveEffect(conn, crystal);
        if (conn.Closed || conn.LeavingArea) return;
        // The discharge reads at the skill's own area (24 base, +50% final = 36 cells); the art is scaled to match it, so
        // the burst covers what it hits instead of looking smaller than its reach.
        uint boom = SpawnEffect(conn, at, FrostBombExplosionArt, 1.5f);
        _ = RemovePulseLater(conn, boom, 1.0);
        foreach (var player in PlayersNear(conn, at, radius).ToArray())
        {
            if (player.Immune || InGracePeriod(player)) continue;
            var (remote, stream, ciphers) = player.Wire;
            int amount = (int)Math.Clamp(damage.Roll(Random.Shared).MinTotal, 0, int.MaxValue);
            if (amount > 0 && TrySyntheticEnemyHit(player, DamageType.Cold, amount, block: true, spell: true, suppress: true, out _))
            {
                await SendWorldRecord(remote, stream, ciphers, player, PlayerId,
                    InstanceProtocol.LifeUpdate(PlayerId, player.PlayerLife, player.PlayerReservedLife), "essence frost bomb");
                await SendPlayerEnergyShield(remote, stream, ciphers, player);
                if (player.PlayerLife == 0) { await PlayerDied(remote, stream, ciphers, player, "death from an essence frost bomb"); continue; }
            }
            if (exposure != 0 && Stats.Key("base_cold_damage_resistance_%") is uint key)
            {
                var effects = Effects.GetOrCreateValue(player);
                ushort buff = player.NextBuff++;
                lock (effects)
                {
                    effects.Debuffs.RemoveAll(d => d.Until <= Now || d.Key == key);
                    effects.Debuffs.Add((new Dictionary<uint, int> { [key] = exposure }, Now + exposureSeconds, buff, key));
                }
                await SendWorldRecord(remote, stream, ciphers, player, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 746,
                    u68: BitConverter.SingleToUInt32Bits((float)exposureSeconds)), $"essence frost bomb cold exposure {exposure}%");
                await RefreshStats(player);
            }
        }
        Console.WriteLine($"[map-content] essence frost bomb by 0x{monster.ObjectId:x} at ({at.X}, {at.Y}), radius {radius:0}");
    }
}