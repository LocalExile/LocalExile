using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    // What every warcry does at its release besides its own effect, within its own radius around where it was used: the
    // client's warcry action (0x141abd120) scans that radius for Chest objects and corpses when Deathrattle's stats are set.
    internal const string CorpseExplosionStat = "nearby_corpses_explode_dealing_%_maximum_life_physical_damage_on_warcry",
        OpenChestsStat = "open_nearby_chests_on_warcry";

    // Path of Building's EnemyExplode radius (skill("radius", 22)), grown by area of effect.
    const int CorpseExplosionRadius = 22;

    internal static async Task WarcryReleased(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius, int epoch)
    {
        var stats = AreaPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get(CorpseExplosionStat) is int percent and > 0)
            await ExplodeCorpses(remote, stream, ciphers, conn, at, radius, epoch, percent, Get);
        bool current;
        lock (conn.Monsters) current = conn.ProjectileActive(epoch) && conn.PlayerLife > 0;
        if (current && Get(OpenChestsStat) != 0) await OpenChestsInRadius(remote, stream, ciphers, conn, at, radius);
    }

    // A corpse the engine has: a monster killed in this area visit whose Life type allows corpse use, not yet consumed.
    static bool IsCorpse(SpawnedMonster m) =>
        m.Dead && m.Life == 0 && !m.CorpseConsumed && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true;

    // The explosion's hit before the target's mitigation: the corpse's maximum Life x percent as physical damage, with the
    // owner's global increases and more multipliers (poewiki Explode: global, not attack or spell, stats apply).
    internal static DamagePacket CorpseExplosionHit(uint corpseMaxLife, int percent, Func<string, int> get)
    {
        double damage = corpseMaxLife * (double)percent / 100;
        long increased = (long)get("damage_+%") + get("physical_damage_+%") + get("area_damage_+%");
        return DamagePacket.Of(DamageType.Physical, damage, damage)
            .Scale(Math.Max(0, 1 + increased / 100.0) * ChargeEffects.DamageMore(get));
    }

    // Deathrattle: every corpse within the warcry's radius explodes and is consumed; each explosion hits the living hostile
    // monsters around its corpse (not an attack or a spell: no block, no evasion, no crit at 0% base).
    static async Task ExplodeCorpses(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius, int epoch, int percent, Func<string, int> get)
    {
        int blast = SkillArea.Radius(CorpseExplosionRadius, get, spell: false);
        var consumed = new List<(uint Id, string Label)>();
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit, int Damage)>();
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea || !conn.ProjectileActive(epoch) || conn.PlayerLife == 0) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, corpse) in conn.Monsters.Where(e => IsCorpse(e.Value) && Dist(e.Value.Walk.Position(now), at) <= radius).ToList())
            {
                corpse.CorpseConsumed = true;
                consumed.Add((id, corpse.Label));
                var hit = CorpseExplosionHit(corpse.MaxLife, percent, get);
                var centre = corpse.Walk.Position(now);
                foreach (var (targetId, m) in conn.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0
                    && Dist(e.Value.Walk.Position(now), centre) <= blast).ToList())
                {
                    int damage = MonsterDamage(m, hit, Random.Shared, default);
                    if (ApplyMonsterHitLocked(conn, m, damage) is not { } applied) continue;
                    if (applied.StageStarts)
                    {
                        CommitBossStage(remote, stream, ciphers, conn, targetId, m, now);
                        applied = applied with { StageStarts = false };
                    }
                    hits.Add((targetId, m, applied, damage));
                }
            }
        }
        foreach (var (id, label) in consumed)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb corpse 0x{id:x} ({label}) explodes (Deathrattle)");
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        foreach (var (id, m, hit, damage) in hits)
            await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, hit, damage, "a corpse explosion", deaths: deaths);
        foreach (var (id, m) in deaths) await MonsterKilled(remote, stream, ciphers, conn, id, m);
    }

    // Deathrattle: the warcry opens the chests within its radius, those the engine has (the quest chests).
    static async Task OpenChestsInRadius(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius)
    {
        if (conn.TutorialChestId != 0 && conn.TutorialChest is { } chest && Dist((chest.X, chest.Y), at) <= radius)
            await OpenTutorialChestCore(remote, stream, ciphers, conn, conn.TutorialChestId, chest);
        List<(uint Id, GlyphQuest.Nest Nest)> nests;
        lock (conn.GlyphNests)
            nests = [.. conn.GlyphNests.Where(e => Dist((e.Value.At.X, e.Value.At.Y), at) <= radius).Select(e => (e.Key, e.Value))];
        foreach (var (id, nest) in nests) await OpenRhoaNestCore(remote, stream, ciphers, conn, id, nest);
    }
}
