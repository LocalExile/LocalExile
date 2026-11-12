using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // What every warcry does at its release besides its own effect, within its own radius around where it was used: the
    // client's warcry action (0x141abfb30) scans that radius for Chest objects and corpses when Deathrattle's stats are set.
    public const string CorpseExplosionStat = "nearby_corpses_explode_dealing_%_maximum_life_physical_damage_on_warcry",
        OpenChestsStat = "open_nearby_chests_on_warcry",
        CorpseLootStat = "corpses_drop_loot_again_on_warcry_chance_%";

    // Path of Building's EnemyExplode radius (skill("radius", 22)), grown by area of effect.
    const int CorpseExplosionRadius = 22;

    // shortcut: the generic gore burst (MiscAnimated SuicideExplodeNoGibs) at its own size; which art the client's warcry
    // wave (0x141abfea0) plays for an exploding corpse is not found yet. Upgrade once that consumer is traced.
    public const string CorpseExplosionAo = "Metadata/Effects/Spells/monsters_effects/gore_explode/gore_explosion_no_gibs.ao";
    const int CorpseExplosionArtMs = 2000;

    public static async Task WarcryReleased(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius, int epoch)
    {
        lock (conn.Monsters)
        {
            if (!conn.ProjectileActive(epoch) || conn.PlayerLife == 0) return;
            conn.RecentEvents.Record(StatConditions.UsedWarcry, ProjectileWorldTime(conn));
        }
        var stats = AreaPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get(CorpseExplosionStat) is int percent and > 0)
            await ExplodeCorpses(remote, stream, ciphers, conn, at, radius, epoch, percent, Get,
                DamageStats.Conversion(stats, Stats, attack: null));
        bool current;
        lock (conn.Monsters) current = conn.ProjectileActive(epoch) && conn.PlayerLife > 0;
        if (current && Get(OpenChestsStat) != 0) await OpenChestsInRadius(conn, at, radius);
        if (current && Get(CorpseLootStat) is int lootChance and > 0)
            await DropCorpseLoot(remote, stream, ciphers, conn, at, radius, epoch, lootChance);
    }

    // Howl of the Wolf: each corpse within the warcry's radius drops its loot again with that chance. The corpse stays
    // usable (its explosion, consumption or removal is another effect's).
    static async Task DropCorpseLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius, int epoch, int chance)
    {
        List<(SpawnedMonster Monster, (int X, int Y) At)> corpses;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || !conn.ProjectileActive(epoch) || conn.PlayerLife == 0) return;
            double now = ProjectileWorldTime(conn);
            corpses = [];
            foreach (var m in conn.Monsters.Values)
            {
                if (!IsCorpse(m) || m.CorpseLooted || m.NoRewards || Dist(m.Walk.Position(now), at) > radius) continue;
                // One reroll per corpse: a later warcry sees the flag, so the same corpses cannot be farmed.
                m.CorpseLooted = true;
                corpses.Add((m, m.Walk.Position(now)));
            }
        }
        Console.WriteLine($"[gateway] warcry corpse loot: {corpses.Count} new corpse(s) within {radius} cells, {Math.Min(100, chance)}% each");
        foreach (var (monster, corpseAt) in corpses)
            if (Random.Shared.Next(100) < Math.Min(100, chance))
                await DropLoot(remote, stream, ciphers, conn, monster, corpseAt);
    }

    // A corpse the engine has: a monster killed in this area visit whose Life type allows corpse use, not yet consumed.
    static bool IsCorpse(SpawnedMonster m) =>
        m.Dead && m.Life == 0 && !m.CorpseConsumed && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true;

    // The explosion's hit before the target's mitigation: the corpse's maximum Life x percent as physical damage, with the
    // owner's global increases and more multipliers, then his global conversion and gain as extra (poewiki Explode: global,
    // not attack or spell, stats apply). Increases apply before conversion, as for every hit on the server.
    public static DamagePacket CorpseExplosionHit(uint corpseMaxLife, int percent, Func<string, int> get, DamageConversion conversion)
    {
        double damage = corpseMaxLife * (double)percent / 100;
        long increased = (long)get("damage_+%") + get("physical_damage_+%") + get("area_damage_+%");
        return DamagePacket.Of(DamageType.Physical, damage, damage)
            .Scale(Math.Max(0, 1 + increased / 100.0) * ChargeEffects.DamageMore(get)).Convert(conversion);
    }

    // Deathrattle: every corpse within the warcry's radius explodes and is consumed; each explosion hits the living hostile
    // monsters around its corpse (not an attack or a spell: no block, no evasion; 0% base crit, and flat crit chance and
    // global added damage, which the wiki's Explode page says apply, are not modelled).
    static async Task ExplodeCorpses(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) at, int radius, int epoch, int percent, Func<string, int> get, DamageConversion conversion)
    {
        int blast = SkillArea.Radius(CorpseExplosionRadius, get, spell: false);
        var consumed = new List<(uint Id, string Label, (int X, int Y) At)>();
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit, int Damage)>();
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea || !conn.ProjectileActive(epoch) || conn.PlayerLife == 0) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, corpse) in conn.Monsters.Where(e => IsCorpse(e.Value) && Dist(e.Value.Walk.Position(now), at) <= radius).ToList())
            {
                // Gone for the client (0x2bd below), so gone here too: a later removal (a projectile kill's) must not send it again.
                corpse.CorpseConsumed = true;
                conn.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
                conn.Monsters.Remove(id);
                var hit = CorpseExplosionHit(corpse.MaxLife, percent, get, conversion);
                var centre = corpse.Walk.Position(now);
                consumed.Add((id, corpse.Label, centre));
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
        var art = new List<uint>();
        foreach (var (id, label, centre) in consumed)
        {
            uint effect;
            lock (conn.Monsters) effect = conn.TakeObjectId();
            art.Add(effect);
            await SendWorldCreate(remote, stream, ciphers, conn, effect, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(centre.X, centre.Y, CorpseExplosionAo).Blob()), $"0x02bb create effect 0x{effect:x} {CorpseExplosionAo} at ({centre.X}, {centre.Y})");
            await SendWorldRecord(remote, stream, ciphers, conn, effect, InstanceProtocol.EnterWakeZone(effect), null);
            await Game.SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd corpse 0x{id:x} ({label}) explodes (Deathrattle)");
        }
        if (art.Count > 0)
            _ = Task.Run(async () =>
            {
                await Task.Delay(CorpseExplosionArtMs);
                foreach (uint effect in art)
                    if (conn.ProjectileActive(epoch)) await Game.SendWorldRemoval(remote, stream, ciphers, conn, effect, null);
            });
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        foreach (var (id, m, hit, damage) in hits)
            await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, hit, damage, "a corpse explosion", deaths: deaths);
        foreach (var (id, m) in deaths) await MonsterKilled(remote, stream, ciphers, conn, id, m);
    }

    // Deathrattle: the warcry opens the chests within its radius, those the engine has (the quest chests, the QuestObjects
    // module's).
    static Task OpenChestsInRadius(ClientState conn, (int X, int Y) at, int radius) => GameEvents.RunChestsOpened(conn, at, radius);
}
