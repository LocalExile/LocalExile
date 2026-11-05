using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageType = GameServer.Core.World.DamageType;

static partial class Game
{
    /// <summary>An on-kill explosion's radius in cells. No table gives one for the item stat; the Rampage corpse
    /// explosions use the same 20 (<see cref="RampageExecution.CorpseRadius"/>), and increased area of effect works on
    /// every explosion, so <see cref="SkillArea.Radius"/> scales it.</summary>
    internal const int KillExplosionRadius = 20;

    /// <summary>"Enemies you Kill Explode, dealing X% of their Life as Physical Damage" (the Crusader body-armour
    /// prefix and the synthesis implicit). Every hostile living monster within the radius of the corpse takes X% of the
    /// dead monster's maximum life as physical damage, mitigated by its own resistances. The stat text and the wiki's
    /// Explode page make it secondary damage based on the killed monster's life, as the relic and Rampage explosions
    /// already are, so the player's damage increases do not scale it. A kill by the explosion is the player's and can
    /// trigger another explosion.</summary>
    internal static async Task ExplodeOnKill(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint corpseId, SpawnedMonster corpse, (int X, int Y) at)
    {
        var player = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int percent = Get("enemies_explode_for_%_life_as_physical_damage");
        if (percent <= 0) return;
        int amount = (int)Math.Min(int.MaxValue, corpse.MaxLife * (long)percent / 100);
        if (amount <= 0) return;
        int radius = SkillArea.Radius(KillExplosionRadius, Get, spell: false);
        var packet = DamagePacket.Of(DamageType.Physical, amount, amount);
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit)>();
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
        }
        Console.WriteLine($"[gateway] {remote} kill explosion 0x{corpseId:x} for {amount} physical within {radius} cells:" +
            $" {hits.Count} hit, {hits.Count(h => h.Hit.Life == 0)} killed");
        foreach (var (id, m, hit) in hits)
        {
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, hit.Life),
                $"0x01b1 monster 0x{id:x} hit by the 0x{corpseId:x} kill explosion for {amount}, life {hit.Life}/{m.MaxLife}");
            if (hit.StageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
            if (hit.Life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
