using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageType = GameServer.Core.World.DamageType;

static partial class Game
{
    /// <summary>An on-kill explosion's fixed radius in cells, from the official 3.29.3b client (2026-11-05: a kill
    /// exploded monsters 22.85 and 24.35 cells away). The native radius does not follow the character's increased area
    /// of effect; scaling our 25 by a 85% AoE build produced 37 cells and hit roughly twice the native crowd, which
    /// flooded kills, loot and packets and stalled the input handlers.</summary>
    internal const int KillExplosionRadius = 25;

    /// <summary>The explosion's art: the server effect's MiscAnimated row 2877, `HolyExplosion` (official 3.29.3b,
    /// 2026-11-05). It plays for about two seconds at the corpse.</summary>
    internal const string KillExplosionAo = "Metadata/Effects/Spells/holy_explode/holy_explode.ao";
    const int KillExplosionArtMs = 2000;

    /// <summary>"Enemies you Kill Explode, dealing X% of their Life as Physical Damage" (the Crusader body-armour
    /// prefix and the synthesis implicit). Every hostile living monster within the radius of the corpse takes X% of the
    /// dead monster's maximum life as physical damage, mitigated by its own resistances. The stat text and the wiki's
    /// Explode page make it secondary damage based on the killed monster's life, as the relic and Rampage explosions
    /// already are, so the player's damage increases do not scale it. The corpse is consumed at once (the official
    /// client removes it 0-6 ms after the death), and a kill by the explosion is the player's and can trigger another
    /// explosion.</summary>
    internal static async Task ExplodeOnKill(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint corpseId, SpawnedMonster corpse, (int X, int Y) at)
    {
        var player = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int percent = Get("enemies_explode_for_%_life_as_physical_damage");
        if (percent <= 0) return;
        int amount = (int)Math.Min(int.MaxValue, corpse.MaxLife * (long)percent / 100);
        if (amount <= 0) return;
        int radius = KillExplosionRadius;
        var packet = DamagePacket.Of(DamageType.Physical, amount, amount);
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit)>();
        uint effect;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (id, m) in conn.Monsters.ToArray())
            {
                if (!m.Hostile || m.Dead || m.Life == 0 || ReferenceEquals(m, corpse)) continue;
                if (Dist(m.Walk.Position(now), at) > radius) continue;
                int damage = MonsterTakesRolled(m, packet, spell: false);
                if (ApplyMonsterHitLocked(conn, m, damage, allyAt: at) is not { } hit) continue;
                hits.Add((id, m, hit));
            }
            // The explosion consumes the killed monster: the native client removes its corpse at once (0x2bd), and a
            // warcry may not use a corpse that an explosion already took.
            corpse.CorpseConsumed = true;
            conn.Monsters.Remove(corpseId);
            effect = conn.NextObjectId++;
        }
        Console.WriteLine($"[gateway] {remote} kill explosion 0x{corpseId:x} for {amount} physical within {radius} cells:" +
            $" {hits.Count} hit, {hits.Count(h => h.Hit.Life == 0)} killed");
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, KillExplosionAo).Blob()),
            $"0x02bb create effect 0x{effect:x} {KillExplosionAo} at ({at.X}, {at.Y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(effect), null);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(corpseId),
            $"0x02bd corpse 0x{corpseId:x} ({corpse.Label}) explodes");
        SendAfter(KillExplosionArtMs / 1000.0, async () =>
        {
            if (conn.Closed || conn.LeavingArea) return;
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(effect), null);
        });
        foreach (var (id, m, hit) in hits)
        {
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, hit.Life),
                $"0x01b1 monster 0x{id:x} hit by the 0x{corpseId:x} kill explosion for {amount}, life {hit.Life}/{m.MaxLife}");
            if (hit.StageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
            if (hit.Life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
