using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Rage's fire runes (<c>EssenceDaemonFireRuneTrapCluster</c>): the rare scatters a cluster of fire rune traps on the
/// ground around its target (Malachai's fire traps). Each rune arms, then bursts into a fire explosion when a player steps on
/// it, or fizzles when its duration ends. Kept apart from the other essence spells so a change here cannot disturb them.</summary>
public static partial class MapEssences
{
    const string RuneArt = "Metadata/Effects/Spells/ground_effects/fire/fire1.ao";
    const string RuneExplosionArt = "Metadata/Effects/Spells/monsters_effects/act4/Malachai/rune_circle/explosion.ao";

    /// <summary>A placed fire rune: its cell, the radius that sets it off, when it arms and expires, its fire damage, art.</summary>
    sealed record Rune((int X, int Y) At, int TriggerRadius, double Armed, double Until, DamagePacket Damage, uint Art);

    static Task CastRunes(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return Task.CompletedTask;
        var catalog = SkillsFor(conn);
        var damage = catalog.HitDamage(spell.Effect, monster.Level);
        if (damage.IsZero) return Task.CompletedTask;
        int count = ScaledSpellCount(spell.Effect, spell.Tier, 6);
        var stats = catalog.EffectStats(spell.Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double duration = Math.Max(1, Get("base_trap_duration") / 1000.0);
        double now = Now;
        var at = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
        for (int i = 0; i < count; i++)
        {
            double angle = Math.Tau * i / count + Random.Shared.NextDouble() * .5;
            double d = 3 + Random.Shared.NextDouble() * 5;
            var spot = conn.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * d), at.Y + (int)Math.Round(Math.Sin(angle) * d)));
            uint art = SpawnEffect(conn, spot, RuneArt, 1.2f);
            lock (area.Runes) area.Runes.Add(new(spot, 4, now + .5, now + duration, damage, art));
        }
        Console.WriteLine($"[map-content] essence rage: {count} fire runes by 0x{monster.ObjectId:x} at ({at.X}, {at.Y})");
        return Task.CompletedTask;
    }

    /// <summary>Called from the essence tick: fizzles the expired runes and sets off any armed rune a player steps on.</summary>
    static void TickRunes(ClientState conn)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        double now = Now;
        Rune[] runes;
        lock (area.Runes)
        {
            foreach (var gone in area.Runes.Where(r => r.Until <= now).ToArray())
            {
                area.Runes.Remove(gone);
                RemoveEffect(conn, gone.Art);
            }
            runes = [.. area.Runes];
        }
        if (runes.Length == 0) return;
        foreach (var player in Members(conn).Where(c => c.PlayerSent && c.PlayerLife > 0 && !c.Immune && !InGracePeriod(c)).ToArray())
        {
            var here = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            foreach (var rune in runes)
            {
                if (now < rune.Armed || Dist(here, rune.At) > rune.TriggerRadius) continue;
                lock (area.Runes)
                    if (!area.Runes.Remove(rune)) continue; // another player's tick already set it off
                RemoveEffect(conn, rune.Art);
                _ = ExplodeRune(conn, rune);
            }
        }
    }

    static async Task ExplodeRune(ClientState conn, Rune rune)
    {
        uint boom = SpawnEffect(conn, rune.At, RuneExplosionArt, 1.5f);
        _ = RemovePulseLater(conn, boom, 1.0);
        foreach (var player in PlayersNear(conn, rune.At, 6).ToArray())
        {
            if (player.Immune || InGracePeriod(player)) continue;
            int amount = (int)Math.Clamp(rune.Damage.Roll(Random.Shared).MinTotal, 0, int.MaxValue);
            if (amount <= 0 || !TrySyntheticEnemyHit(player, DamageType.Fire, amount, block: true, spell: true, suppress: true, out var hit)) continue;
            var (remote, stream, ciphers) = player.Wire;
            await SendWorldRecord(remote, stream, ciphers, player, PlayerId,
                InstanceProtocol.LifeUpdate(PlayerId, player.PlayerLife, player.PlayerReservedLife), "essence fire rune");
            await SendPlayerEnergyShield(remote, stream, ciphers, player);
            if (player.PlayerLife == 0) await PlayerDied(remote, stream, ciphers, player, "death from an essence fire rune");
        }
    }
}