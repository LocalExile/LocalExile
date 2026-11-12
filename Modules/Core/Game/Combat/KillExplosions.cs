using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageType = GameServer.Core.World.DamageType;

public static partial class Game
{
    /// <summary>An on-kill explosion's fixed radius in cells, from the official 3.29.3b client (2026-11-05: a kill
    /// exploded monsters 22.85 and 24.35 cells away). The native radius does not follow the character's increased area
    /// of effect; scaling our 25 by a 85% AoE build produced 37 cells and hit roughly twice the native crowd, which
    /// flooded kills, loot and packets and stalled the input handlers.</summary>
    public const int KillExplosionRadius = 25;

    /// <summary>The explosion's art: the server effect's MiscAnimated row 2877, `HolyExplosion` (official 3.29.3b,
    /// 2026-11-05). It plays for about two seconds at the corpse.</summary>
    public const string KillExplosionAo = "Metadata/Effects/Spells/holy_explode/holy_explode.ao";
    /// <summary>How long each explosion art plays, read from the client's own rig for it: the .ao's single animation
    /// (`explode`, once), which is what the client plays on the EffectObject. HolyExplosion's
    /// `Art/Models/Effects/holy_explode/holy_explode.amd` is 2000 ms, and so is ChaosExplode's
    /// `Art/Models/Effects/chaos_explode/chaos_explode.amd`: the two share a length, but each keeps its own entry, so an
    /// art that plays for a different time cannot inherit the other's. A value is the object's lifetime: removing it
    /// earlier cuts the clip, later leaves it hanging at the corpse.</summary>
    static readonly Dictionary<string, int> KillExplosionArtMs = new(StringComparer.OrdinalIgnoreCase)
    {
        [KillExplosionAo] = 2000,
        [ChaosExplosionAo] = 2000,
    };

    /// <summary>That art's clip length, for an art the table does not know (two seconds, the length of the blasts it
    /// holds).</summary>
    static int ArtMs(string art) => KillExplosionArtMs.GetValueOrDefault(art, 2000);
    /// <summary>The chaos explosion the shrine stat uses instead: MiscAnimated 555/757/4312/6125
    /// `Metadata/Effects/Spells/chaos_explode/chaos_explode.ao`, the client's own chaos blast (the purple one).</summary>
    public const string ChaosExplosionAo = "Metadata/Effects/Spells/chaos_explode/chaos_explode.ao";

    /// <summary>"Enemies you Kill Explode, dealing X% of their Life as Physical Damage" (the Crusader body-armour
    /// prefix and the synthesis implicit). Every hostile living monster within the radius of the corpse takes X% of the
    /// dead monster's maximum life as physical damage, mitigated by its own resistances. The stat text and the wiki's
    /// Explode page make it secondary damage based on the killed monster's life, as the relic and Rampage explosions
    /// already are, so the player's damage increases do not scale it. The corpse is consumed at once (the official
    /// client removes it 0-6 ms after the death), and a kill by the explosion is the player's and can trigger another
    /// explosion.</summary>
    public static async Task ExplodeOnKill(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint corpseId, SpawnedMonster corpse, (int X, int Y) at)
    {
        var player = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        // Two sources share one explosion: "Enemies you Kill Explode" (a percentage of their life as physical damage,
        // always) and the shrine's "chance to Explode" (BuffDefinitions 1313 shrine_gloom: 25% of their life as
        // chaos damage, on its chance). The physical one wins when both are present, so a build's own explosion keeps
        // its type.
        int percent = Get("enemies_explode_for_%_life_as_physical_damage");
        var type = DamageType.Physical;
        if (percent <= 0)
        {
            int chance = Get("explode_enemies_for_25%_life_as_chaos_on_kill_chance_%");
            if (chance <= 0 || Random.Shared.Next(100) >= chance) return;
            percent = 25;
            type = DamageType.Chaos;
        }
        int amount = (int)Math.Min(int.MaxValue, corpse.MaxLife * (long)percent / 100);
        if (amount <= 0) return;
        int radius = KillExplosionRadius;
        var packet = DamagePacket.Of(type, amount, amount);
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
        Console.WriteLine($"[gateway] {remote} kill explosion 0x{corpseId:x} for {amount} {type.ToString().ToLowerInvariant()} within {radius} cells:" +
            $" {hits.Count} hit, {hits.Count(h => h.Hit.Life == 0)} killed");
        string art = type == DamageType.Chaos ? ChaosExplosionAo : KillExplosionAo;
        await SendWorldCreate(remote, stream, ciphers, conn, effect, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, art).Blob()),
            $"0x02bb create effect 0x{effect:x} {art} at ({at.X}, {at.Y})");
        await SendWorldRecord(remote, stream, ciphers, conn, effect, InstanceProtocol.EnterWakeZone(effect), null);
        await Game.SendWorldRemoval(remote, stream, ciphers, conn, corpseId,
            $"0x02bd corpse 0x{corpseId:x} ({corpse.Label}) explodes");
        SendAfter(ArtMs(art) / 1000.0, async () =>
        {
            if (conn.Closed || conn.LeavingArea) return;
            await Game.SendWorldRemoval(remote, stream, ciphers, conn, effect, null);
        });
        foreach (var (id, m, hit) in hits)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.LifeUpdate(id, hit.Life),
                $"0x01b1 monster 0x{id:x} hit by the 0x{corpseId:x} kill explosion for {amount} {type.ToString().ToLowerInvariant()}, life {hit.Life}/{m.MaxLife}");
            if (hit.StageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
            if (hit.Life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
