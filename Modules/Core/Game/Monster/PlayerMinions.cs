using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    // Summon Elemental Relic's relics by element (MonsterVarieties 7185–7187, MonsterTypes ElementalRelic: IsPlayerMinion,
    // so MonsterCatalog.BaseLife, as the client, gives them the MinionLife column × LifeMultiplier 400). A RandomElement
    // skill: one of the three, a third each. Their granted effects are RelicTeleport and the element's aura.
    static readonly (DamageType Element, string Variety)[] RelicVarieties =
    [
        (DamageType.Fire, "Metadata/Monsters/AnimatedItem/ElementalLivingRelicFire"),
        (DamageType.Cold, "Metadata/Monsters/AnimatedItem/ElementalLivingRelicCold"),
        (DamageType.Lightning, "Metadata/Monsters/AnimatedItem/ElementalLivingRelicLightning"),
    ];

    /// <summary>The relics' death explosion radius in cells. Unverified: the data has no explosion skill for the relic; 22
    /// is Path of Building's radius for Minion Instability, the other minion death explosion.</summary>
    public const int RelicExplosionRadius = 22;

    // Every 100 ms of game time from area entry (a pause holds it): PlayerMinionsTick, then LinkSkillsTick (a link to a
    // minion that just ended sees it gone). A failed step is logged and the next one runs; a closed connection ends the loop.
    static async Task RunPlayerMinions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState updates)
    {
        // Loads the relic trigger's tables now, so that no step or hit loads them under the Monsters lock.
        _ = RelicTriggerOf(conn);
        while (PlayerUpdatesCurrent(conn, updates))
        {
            await updates.Schedule.Wait(PlayerUpdateRole.Minions, 100, updates.Stop.Token);
            if (!PlayerUpdatesCurrent(conn, updates)) break;
            if (conn.Paused) continue;
            try
            {
                double now = updates.Schedule.Now(conn);
                await PlayerMinionsTick(remote, stream, ciphers, conn, now);
                if (!PlayerUpdatesCurrent(conn, updates)) return;
                await LinkSkillsTick(remote, stream, ciphers, conn, now);
            }
            catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
            {
                if (!conn.Closed) Console.WriteLine($"[gateway] {remote} minions: {ex.Message}");
            }
            catch (Exception) { break; } // the connection has gone
        }
    }

    /// <summary>
    /// One step of the player's minions at game time <paramref name="now"/>. A minion whose time is up (or that died)
    /// dies and explodes; one whose owner died or lost the granting passive dies without exploding (our rule: the wiki
    /// does not say), and a living owner's links to it end without killing him (our rule, see paladin.md). Then the
    /// queued relic summons are made, and each living minion's aura goes on the owner while he
    /// is within its radius and off when he leaves it or it ends; the player's stats are sent again when that changes.
    /// </summary>
    public static async Task PlayerMinionsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        var rules = CombatLifetime;
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules);
        foreach (var tick in GameEvents.CompanionTick.For(conn))
        {
            if (!Current()) return;
            await tick(remote, stream, ciphers, conn, now);
        }
        await GolemsTick(remote, stream, ciphers, conn, now);
        await SummonsTick(remote, stream, ciphers, conn, now);
        await ReservationSkillsTick(remote, stream, ciphers, conn, now);
        await PlacedSkillsTick(remote, stream, ciphers, conn, now);
        var ended = new List<PlayerMinions.Minion>();
        var bytes = new List<byte>();
        int summons;
        bool aurasChanged = false, linksEnded = false;
        using (ProjectileMutation(conn))
        {
            if (!Current() || conn.Closed || conn.LeavingArea) return;
            bool keep = conn.PlayerLife > 0 && RelicTriggerOf(conn) is not null;
            summons = keep ? conn.Minions.PendingRelics : 0;
            if (!keep) conn.Minions.PendingRelics = 0;
            RemoveMinionBodiesLocked(conn, now);
            // The relics; the Spirit chieftains end in SpiritChieftainMinion.
            foreach (var minion in conn.Minions.All.Where(m => IsRelicSkill(m.Skill)).ToArray())
            {
                var m = minion.Monster;
                bool current = conn.Monsters.TryGetValue(minion.Id, out var held) && ReferenceEquals(held, m);
                if (current && keep && !m.Dead && m.Life > 0 && now < minion.Until) continue;
                conn.Minions.Remove(minion.Id);
                if (minion.OwnerBuff != 0)
                {
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, minion.OwnerBuff), "minion owner aura ends", WorldId: PlayerId));
                    aurasChanged = true;
                }
                if (!current) continue;
                if (keep) ended.Add(minion);
                else if (conn.PlayerLife > 0 && conn.Links.On(minion.Id) is { } link)
                {
                    // A refund is not the minion expiring (the wiki's death rule): the link ends, the player lives.
                    EndLinkLocked(conn, link, bytes);
                    linksEnded = true;
                }
                m.Aggro = false; m.Dead = true; m.Life = 0;
                conn.Minions.Bodies.Add((minion.Id, m, now + 1));
                conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(minion.Id, [(StatKeys.HideMiniLifeBar, 1)]), "minion ends", minion.Id, m, WorldId: minion.Id));
                conn.Minions.OutputQueue.Add(new([], "minion life 0", minion.Id, m, PlayerMinions.OutputKind.Life));
            }
            if (linksEnded) { LinksChangedLocked(conn); aurasChanged = true; }
            if (bytes.Count > 0) conn.Minions.OutputQueue.Add(new([.. bytes], "links to refunded minions end"));
            if (aurasChanged) conn.Minions.StatRevision++;
            foreach (var minion in ended) CommitMinionExplosionLocked(remote, stream, ciphers, conn, minion, now);
        }
        await PublishMinionOutcomesFor(remote, stream, ciphers, conn, rules);
        if (!Current()) return;
        for (int i = 0; i < summons && Current(); i++) await SummonElementalRelic(remote, stream, ciphers, conn, now);

        bytes.Clear(); aurasChanged = false;
        lock (conn.Monsters)
        {
            if (!Current() || conn.Closed || conn.LeavingArea) return;
            var here = conn.Walk.Position(now);
            foreach (var minion in conn.Minions.All)
            {
                var m = minion.Monster;
                bytes.Clear();
                // RelicTeleport: the AI follows within the aggro range only (MonsterVarieties MaxAgroRange, 150 for the
                // relics), so from beyond it the minion jumps to him (as /mob_tp). shortcut: the threshold is the aggro
                // range, the game's is not in the data (RelicTeleport has no stats).
                if (minion.Teleports && conn.PlayerLife > 0 && Dist(m.Walk.Position(now), here) > m.AggroRange)
                {
                    var (x, y) = NextToPlayer(conn);
                    m.Walk = conn.NewMonsterWalk(minion.Id, x, y, m.Walk.Speed, LivingCollision.Size(m), false);
                    m.LastTarget = null;
                    bytes.AddRange(InstanceProtocol.Teleport(minion.Id, x, y));
                }
                if (bytes.Count > 0) conn.Minions.OutputQueue.Add(new([.. bytes], "minion teleport", minion.Id, m));
                bytes.Clear();
                if (minion.OwnerAura is not { } aura) continue;
                bool inside = conn.PlayerLife > 0 && Dist(minion.Monster.Walk.Position(now), here) <= aura.Radius;
                if (inside == (minion.OwnerBuff != 0)) continue;
                aurasChanged = true;
                if (inside) bytes.AddRange(InstanceEntities.AddBuff(PlayerId, minion.OwnerBuff = conn.NextBuff++, aura.BuffRow, minion.Id));
                else { bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, minion.OwnerBuff)); minion.OwnerBuff = 0; }
                conn.Minions.OutputQueue.Add(new([.. bytes], "minion aura changed", minion.Id, m));
            }
            if (aurasChanged) conn.Minions.StatRevision++;
        }
        await PublishMinionOutcomesFor(remote, stream, ciphers, conn, rules);
    }

    /// <summary>The auras the player's minions cast on him, as raw stats. The strongest of one aura applies (wiki Aura):
    /// his own aura of the same family wins when its stats add up to at least as much.</summary>
    static IEnumerable<(uint Key, int Value)> MinionAuraStats(ClientState conn, IReadOnlyList<AuraCatalog.Aura> own)
    {
        List<PlayerMinions.Aura> auras;
        lock (conn.Monsters) auras = [.. conn.Minions.All.Where(m => m.OwnerBuff != 0).Select(m => m.OwnerAura).OfType<PlayerMinions.Aura>()];
        foreach (var aura in auras)
        {
            var same = own.Where(a => AuraCatalog.Family(a.EffectId) == AuraCatalog.Family(aura.EffectId)).ToList();
            if (same.Count > 0 && AuraCatalog.RawStats(same, conn.Pools.AuraEffectPct, SkillsFor(conn), Stats).Sum(s => (long)s.Value)
                >= aura.Stats.Sum(s => (long)s.Value)) continue;
            foreach (var stat in aura.Stats) yield return stat;
        }
    }

    // A triggered Summon Elemental Relic: a relic of a random element next to the player at his level up to the skill's
    // minion_actor_level_is_user_level_up_to_maximum (85, Ruthless 80), for base_skill_effect_duration with his skill
    // duration modifiers and his base_minion_duration_+% (Path of Building's SkillStatMap: increased Duration for
    // CreatesMinion skills, which Summon Elemental Relic is); a relic of that element already there has its duration
    // refreshed instead (at most one each).
    // shortcut: Combat Focus's element exclusions (RandomElement skills) are not modelled; every element can come.
    static async Task SummonElementalRelic(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
        var player = CharacterPlayerStats(conn);
        int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;
        MonsterCatalog.Variety? variety;
        DamageType element;
        string skill;
        byte level;
        double seconds;
        int lifePct;
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Minions.PendingRelics == 0 || RelicTriggerOf(conn) is not { } relic) return;
            var own = SkillsFor(conn).EffectStats(relic.Grant.Effect, relic.Grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
            int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
            (element, string id) = RelicVarieties[conn.Minions.Random.Next(RelicVarieties.Length)];
            skill = relic.Grant.EffectId;
            seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player,
                name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? Player("base_minion_duration_+%") : 0));
            if (conn.Minions.All.FirstOrDefault(m => m.Skill == skill && m.Kind == element.ToString()) is { } same)
            {
                same.Until = now + seconds;
                conn.Minions.PendingRelics--;
                Console.WriteLine($"[gateway] {remote} {element} Relic 0x{same.Id:x}: duration refreshed to {seconds:0.##} s");
                return;
            }
            level = (byte)Math.Clamp(Math.Min(conn.Pools.Level, Own("minion_actor_level_is_user_level_up_to_maximum")), 1, 100);
            lifePct = Own("guardian_relic_explode_on_death_for_%_life_as_element_damage");
            variety = Monsters.Find(id);
            at = NextToPlayer(conn);
        }
        if (variety is null)
        {
            lock (conn.Monsters)
                if (CurrentGameWork && conn.Minions.PendingRelics > 0) conn.Minions.PendingRelics--;
            Console.WriteLine($"[gateway] {remote} {element} Relic: no MonsterVarieties row"); return;
        }
        var aura = RelicAura(conn, variety, level);
        bool teleports = variety.GrantedEffects?.Any(e => SkillsFor(conn).EffectId(e) == "RelicTeleport") == true;
        // The player's increased minion life raises its life (on its Stats as maximum_life) and so its explosion.
        var mods = Player("minion_maximum_life_+%") is int moreLife and not 0
            ? MonsterModSet.None with { Effects = new(new Dictionary<string, int> { ["maximum_life_+%"] = moreLife }) }
            : MonsterModSet.None;
        await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y, friendly: true,
            mods: mods, extraStats: MinionStats, owned: (id, m) =>
            {
                conn.Minions.PendingRelics--;
                conn.Minions.Add(new(id, m, skill, element.ToString())
                {
                    Until = now + seconds, OwnerAura = aura, Explodes = new(element, lifePct, RelicExplosionRadius), Teleports = teleports,
                });
            }, ownedCurrent: () => conn.PlayerLife > 0 && conn.Minions.PendingRelics > 0 && RelicTriggerOf(conn) is not null);
        if (!CurrentGameWork) return;
        Console.WriteLine($"[gateway] {remote} {element} Relic summoned at level {level} for {seconds:0.##} s" +
            (aura is null ? "" : $", {aura.EffectId} within {aura.Radius} cells: {string.Join(", ", aura.Stats.Select(s => $"{Stats.Name(s.Key)} {s.Value}"))}"));
    }

    // The stats that mark a monster as the player's minion for the client (0x141bfaf20 reads both).
    static IReadOnlyList<(uint Key, int Value)> MinionStats =>
        [.. new[] { "is_player_minion", "is_semantic_minion" }.Select(Stats.Key).OfType<uint>().Select(k => (k, 1))];

    // A relic's aura: its variety's aura effect at the relic's level (PlayerMinions.AuraStatsAt), with that level's radius:
    // AuraCatalog.BaseRadius and the level's active_skill_base_radius_+ (Anger at gem level 20: 59).
    static PlayerMinions.Aura? RelicAura(ClientState conn, MonsterCatalog.Variety variety, int level)
    {
        var skills = SkillsFor(conn);
        foreach (int effect in variety.GrantedEffects ?? [])
        {
            if (skills.EffectId(effect) is not { } id || !AuraCatalog.Supported(id)) continue;
            var own = skills.EffectStats(effect, skills.MaxLevelAt(effect, level)).ToDictionary(s => s.Item1, s => s.Item2);
            int Get(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
            return new(id, AuraCatalog.BuffRow(id), SkillArea.Radius(AuraCatalog.BaseRadius, Get, spell: false),
                PlayerMinions.AuraStatsAt(skills, Stats, effect, id, level));
        }
        return null;
    }

    // A minion's death explosion (PlayerMinions.Explosion): its share of its maximum life as its element to every hostile
    // living monster within the radius, mitigated as any hit (resistances, the player's debuffs on the monster). It is the
    // minion's hit, not the player's: no hit-recently, no on-kill charges; the kills give the player their experience and
    // items, and count as a nearby ally's for the relic trigger.
    static void CommitMinionExplosionLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlayerMinions.Minion minion, double now)
    {
        if (minion.Explodes is not { } blast) return;
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit, int Damage)>();
        int amount;
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            var at = minion.Monster.Walk.Position(now);
            amount = (int)Math.Min(int.MaxValue, minion.Monster.MaxLife * (long)blast.LifePct / 100);
            var packet = DamagePacket.Of(blast.Type, amount, amount);
            foreach (var (id, m) in conn.Monsters.ToArray())
            {
                if (!m.Hostile || m.Dead || m.Life == 0 || Dist(m.Walk.Position(now), at) > blast.Radius) continue;
                int damage = MonsterTakesRolled(m, packet, spell: false);
                if (ApplyMonsterHitLocked(conn, m, damage, allyAt: at) is not { } hit) continue;
                if (hit.StageStarts)
                {
                    CommitBossStage(remote, stream, ciphers, conn, id, m, ProjectileWorldTime(conn));
                    hit = hit with { StageStarts = false };
                }
                hits.Add((id, m, hit, damage));
                CommitMinionHitLocked(conn, id, m, hit, damage, now, $"{minion.Kind} Relic explosion");
            }
        }
        Console.WriteLine($"[gateway] {remote} {minion.Kind} Relic 0x{minion.Id:x} explodes for {amount} {blast.Type} within {blast.Radius} cells: {hits.Count} hit, {hits.Count(h => h.Hit.Life == 0)} killed");
    }

    // A cell 3 cells from the server's estimate of the player, walkable if the walk map says so.
    public static (int X, int Y) NextToPlayer(ClientState conn)
    {
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        foreach (var (dx, dy) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, -2) })
            if (conn.Layout.Walkable is not { } w || w(px + dx, py + dy)) return (px + dx, py + dy);
        return (px, py);
    }
}
