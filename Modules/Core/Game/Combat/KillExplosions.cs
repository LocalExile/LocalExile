using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageType = GameServer.Core.World.DamageType;

public static partial class Game
{
    const uint CursedCorpseBlast = Stat.EnemiesNearCursedCorpsesAreBlindedAndExplodeOnDeathForPctLifeAsPhysicalDamage;

    static readonly ConditionalWeakTable<GameArea, SortedDictionary<uint, SpawnedMonster>> AreaCursedCorpses = new();

    static SortedDictionary<uint, SpawnedMonster> CursedCorpsesOf(GameArea area) =>
        AreaCursedCorpses.GetValue(area, static world =>
        {
            var corpses = new SortedDictionary<uint, SpawnedMonster>();
            foreach (var (id, corpse) in world.Monsters)
                if ((corpse.Dead || corpse.Life == 0) && !corpse.CorpseConsumed
                    && corpse.Curses.All.Values.Any(c => c.RemainsOnDeath))
                    corpses.Add(id, corpse);
            return corpses;
        });

    static void RememberCursedCorpse(Player player, uint id, SpawnedMonster corpse)
    {
        if (!corpse.CorpseConsumed && corpse.Curses.All.Values.Any(c => c.RemainsOnDeath))
        {
            var corpses = CursedCorpsesOf(player.Area);
            lock (corpses) corpses[id] = corpse;
        }
    }

    static List<(uint Id, (int X, int Y) At)> OwnCursedCorpses(Player player, double now, SpawnedMonster? except = null)
    {
        var area = player.Area;
        var corpses = CursedCorpsesOf(area);
        var result = new List<(uint Id, (int X, int Y) At)>();
        lock (corpses)
        {
            List<uint>? expired = null;
            foreach (var (id, corpse) in corpses)
            {
                if (corpse.CorpseConsumed || !ReferenceEquals(area.Monsters.GetValueOrDefault(id), corpse)
                    || !corpse.Curses.All.Values.Any(c => c.RemainsOnDeath && c.Until > now))
                {
                    (expired ??= []).Add(id);
                    continue;
                }
                if (!ReferenceEquals(corpse, except) && corpse.Curses.All.Values.Any(c => c.Caster == player.Id
                        && c.RemainsOnDeath && c.Until > now))
                    result.Add((id, corpse.Walk.Position(now)));
            }
            if (expired is not null)
                foreach (uint id in expired) corpses.Remove(id);
        }
        return result;
    }

    public readonly record struct KillEquipmentSnapshot(int CursedCorpsePhysicalPct, int PrideChance, int CursedVictimChaosChance)
    {
        public bool Any => CursedCorpsePhysicalPct > 0 || PrideChance > 0 || CursedVictimChaosChance > 0;
    }

    static KillEquipmentSnapshot CaptureKillEquipment(Player player, SpawnedMonster victim, (int X, int Y) at, double now,
        IReadOnlyDictionary<uint, int>? stats = null)
    {
        stats ??= PlayerStatValues(player);
        int corpsePercent = stats.GetValueOrDefault(CursedCorpseBlast);
        if (corpsePercent > 0 && !OwnCursedCorpses(player, now, victim).Any(other => Dist(other.At, at) <= KillExplosionRadius))
            corpsePercent = 0;
        int prideChance = stats.GetValueOrDefault(Stat.ExplodeEnemiesFor10PctLifeAsPhysicalOnKillChancePctWhileUsingPride);
        if (prideChance != 0 && ReservationAura(player, "Pride") is null) prideChance = 0;
        return new(corpsePercent, prideChance,
            victim.CursedAtDeath ? stats.GetValueOrDefault(Stat.ExplodeCursedEnemiesFor25PctLifeAsChaosOnKillChancePct) : 0);
    }

    static void CaptureEquippedHitExplosionLocked(SpawnedMonster target, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (target.Life == 0 && target.Hostile && playerStats.GetValueOrDefault(Stat.ExplodeOnKillPctFireDamageToDeal) is > 0 and var percent)
            target.HitFireExplosionPercent = percent;
    }

    sealed record AttackKillExplosion(int Chance);
    static readonly RuleTable<SpawnedMonster, AttackKillExplosion> AttackKillExplosions = new();

    static void CaptureAttackKillExplosionLocked(SpawnedMonster victim, IReadOnlyDictionary<uint, int> stats)
    {
        if (victim.Life == 0 && victim.Hostile
            && stats.GetValueOrDefault(Stat.EnemiesExplodeOnDeathByAttackFor10PctLifeAsPhysicalDamage) is > 0 and var chance)
            AttackKillExplosions.AddOrUpdate(victim, new(chance));
    }

    // Oriath's End is secondary damage: global/elemental/area modifiers and conversion apply, but attack/spell
    // modifiers and their added damage do not. Keep the older physical/chaos explosion policy separate.
    internal static DamagePacket OriathExplosionPacket(uint maximumLife, DamageType element,
        IReadOnlyDictionary<uint, int> player) => ElementalKillExplosionPacket(maximumLife, element, 10, player);

    static DamagePacket ElementalKillExplosionPacket(uint maximumLife, DamageType element, int percent,
        IReadOnlyDictionary<uint, int> player)
    {
        int Get(uint key) => player.GetValueOrDefault(key);
        double raw = (double)maximumLife * percent / 100;
        return PlayerHitDamage.Resolve(DamagePacket.Of(element, raw, raw), Get,
            DamageStats.Conversion(player, Stats, attack: null), spell: false, area: true, secondary: true);
    }

    static void ReleaseSkillCorpseExplosion(ProjectileSession session, uint corpseId, SpawnedMonster corpse,
        (int X, int Y) at, double now, int radius, DamagePacket packet, Penetration.PerType penetration, int cannotPenetrate,
        string skill)
    {
        var player = session.Player;
        if (!corpse.CorpseConsumed)
        {
            corpse.CorpseConsumed = true;
            player.Area.Monsters.Remove(corpseId);
            session.WorldRecords(corpseId, () => [InstanceProtocol.RemoveObject(corpseId)],
                $"{skill} consumes corpse", remove: true);
        }
        foreach (var (id, enemy) in player.Area.MonstersNear(at, radius))
        {
            if (!enemy.Hostile || enemy.Dead || enemy.Life == 0 || Dist(at, enemy.Walk.Position(now)) > radius)
                continue;
            var resisted = MitigateMonsterHit(enemy, packet, now, penetration, cannotPenetrate);
            int damage = MonsterTakesMitigated(enemy, resisted, false, now);
            if (ApplyTypedMonsterHitLocked(player, enemy, damage,
                MonsterHitChaosDamage(enemy, resisted, spell: false, now, damage), allyAt: at) is not { } hit)
                continue;
            if (hit.StageStarts) CommitBossStage(player, id, enemy, now);
            if (hit.Life == 0) SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life),
                $"{skill} corpse explosion target=0x{id:x} damage={damage} life={hit.Life}/{enemy.MaxLife}");
            if (hit.Life == 0) CommitProjectileKill(session, id, enemy, session.Runtime.Now, AreaRng(player));
            if (enemy.ThresholdReached) session.Enqueue(() => QuestThreshold(player, id, enemy));
        }
    }

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
    internal static bool MayExplodeOnKill(Player player, SpawnedMonster? corpse = null,
        KillEquipmentSnapshot? equipment = null, IReadOnlyDictionary<uint, int>? stats = null)
    {
        if (corpse is { HitFireExplosionPercent: > 0 } || equipment is { Any: true }
            || corpse is not null && AttackKillExplosions.TryGetValue(corpse, out _)) return true;
        stats ??= PlayerStatValues(player);
        if (stats.GetValueOrDefault(Stat.EnemiesExplodeForPctLifeAsPhysicalDamage) > 0
            || stats.GetValueOrDefault(Stat.BleedingEnemiesExplodeForPctLifeAsPhysicalDamage) > 0
            || stats.GetValueOrDefault(Stat.ExplodeEnemiesFor10PctLifeAsPhysicalOnKillChancePct) > 0
            || stats.GetValueOrDefault(Stat.ExplodeEnemiesFor25PctLifeAsChaosOnKillChancePct) > 0
            || stats.GetValueOrDefault(Stat.ExplodeEnemiesFor10PctLifeAsRandomElementOnKillChancePct) > 0
            || stats.GetValueOrDefault(Stat.ExplodeEnemiesFor10PctLifeAsFireOnKillChancePct) > 0)
            return true;
        if (equipment is not null) return false;
        return stats.GetValueOrDefault(CursedCorpseBlast) > 0
            || stats.GetValueOrDefault(Stat.ExplodeEnemiesFor10PctLifeAsPhysicalOnKillChancePctWhileUsingPride) > 0
            || stats.GetValueOrDefault(Stat.ExplodeCursedEnemiesFor25PctLifeAsChaosOnKillChancePct) > 0;
    }

    public static async Task<bool> ExplodeOnKill(Player player, uint corpseId, SpawnedMonster corpse, (int X, int Y) at,
        KillEquipmentSnapshot? equipment = null)
    {
        var playerStats = PlayerStatValues(player);
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        int generic = Get(Stat.EnemiesExplodeForPctLifeAsPhysicalDamage);
        int bleedingPercent = Get(Stat.BleedingEnemiesExplodeForPctLifeAsPhysicalDamage);
        bool bleeding;
        // The killing bleed tick clears the list before the death runs (MonsterIgnites.Clear keeps DiedBurning for this).
        bleeding = corpse.Bleeds.Burning || corpse.Bleeds.DiedBurning;
        // A bleeding kill has both explosion modifiers; their percentages add (the generic one and the bleeding one
        // are separate sources on the same kill).
        var captured = equipment ?? CaptureKillEquipment(player, corpse, at, AreaTime(player), playerStats);
        long percent = (long)generic + (bleeding ? bleedingPercent : 0) + captured.CursedCorpsePhysicalPct;
        long physicalChance = (long)Get(Stat.ExplodeEnemiesFor10PctLifeAsPhysicalOnKillChancePct) + captured.PrideChance;
        if (AttackKillExplosions.TryGetValue(corpse, out var attack))
        {
            physicalChance += attack.Chance;
            AttackKillExplosions.Remove(corpse);
        }
        if (physicalChance > 0 && AreaRng(player).Next(100) < physicalChance) percent += 10;
        // The same quarter-life chaos explosion can come from a general or a cursed-enemy source.
        long chaosChance = (long)Get(Stat.ExplodeEnemiesFor25PctLifeAsChaosOnKillChancePct) + captured.CursedVictimChaosChance;
        int chaosPercent = chaosChance > 0 && AreaRng(player).Next(100) < chaosChance ? 25 : 0;
        // Oriath's End: "(20-30)% chance for Enemies you Kill during Effect to Explode, dealing 10% of their maximum
        // Life as Damage of a Random Element" (the flask's local stat becomes this one while it runs, UtilityFlasks).
        // K'Tash's Magnificence (Demonic Power): 40% chance for killed enemies to explode for 10% of life as fire.
        int elementalChance = Get(Stat.ExplodeEnemiesFor10PctLifeAsRandomElementOnKillChancePct);
        int fireChance = Get(Stat.ExplodeEnemiesFor10PctLifeAsFireOnKillChancePct);
        DamageType? element = elementalChance > 0 && AreaRng(player).Next(100) < elementalChance
            ? (DamageType)AreaRng(player).Next((int)DamageType.Lightning, (int)DamageType.Fire + 1)
            : fireChance > 0 && AreaRng(player).Next(100) < fireChance ? DamageType.Fire : null;
        int physical = (int)Math.Clamp(corpse.MaxLife * (long)percent / 100, 0, int.MaxValue);
        int chaos = (int)Math.Clamp(corpse.MaxLife * (long)chaosPercent / 100, 0, int.MaxValue);
        int elemental = element is null ? 0 : (int)Math.Clamp((long)corpse.MaxLife / 10, 0, int.MaxValue);
        int fireOnHit = corpse.HitFireExplosionPercent;
        corpse.HitFireExplosionPercent = 0;
        int hitFire = (int)Math.Clamp((long)corpse.MaxLife * fireOnHit / 100, 0, int.MaxValue);
        if (physical == 0 && chaos == 0 && elemental == 0 && hitFire == 0) return false;
        // The modifiers can trigger on the same kill; preserve each damage type through mitigation.
        var packet = DamagePacket.Of(DamageType.Physical, physical, physical)
            .Add(DamagePacket.Of(DamageType.Chaos, chaos, chaos));
        var elementalPacket = element is { } e ? OriathExplosionPacket(corpse.MaxLife, e, playerStats) : DamagePacket.Empty;
        if (hitFire > 0)
            elementalPacket = elementalPacket.Add(ElementalKillExplosionPacket(corpse.MaxLife, DamageType.Fire, fireOnHit, playerStats));
        var elementalPenetration = Penetration.Of(playerStats, Stats, attack: false);
        long amount = (long)physical + chaos + elemental + hitFire;
        string type = string.Join(" and ", new[] { physical > 0 ? "physical" : null, chaos > 0 ? "chaos" : null,
            element is { } t ? t.ToString().ToLowerInvariant() : null, hitFire > 0 && element != DamageType.Fire ? "fire" : null }
            .Where(x => x is not null));
        int radius = KillExplosionRadius;
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit)>();
        uint effect;
        bool showArt;
        bool removeCorpse;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return false;
        double now = AreaTime(player);
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (!m.Hostile || m.Dead || m.Life == 0 || ReferenceEquals(m, corpse)) continue;
            if (Dist(m.Walk.Position(now), at) > radius) continue;
            var resisted = GameServer.Core.World.DamageModel.Mitigate(packet, t => MonsterHitResistance(m, t, default, now));
            int damage = MonsterTakesMitigated(m, resisted, false, now);
            int bypass = MonsterHitChaosDamage(m, resisted, false, now, damage);
            if (!elementalPacket.IsZero)
            {
                var mitigated = MitigateMonsterHit(m, elementalPacket, now, elementalPenetration,
                    playerStats.GetValueOrDefault(Stat.CannotPenetrateOrIgnoreElementalResistances),
                    playerStats.GetValueOrDefault(Stat.ElementalDamageResistedByLowestElementalResistance) != 0);
                int witheredElemental = WitheredElementalHitTaken(m, player.Id, now, playerStats);
                damage = (int)Math.Min(int.MaxValue, (long)damage + MonsterTakesMitigated(m, mitigated, false, now,
                    additionalElementalTakenPct: witheredElemental));
            }
            if (ApplyTypedMonsterHitLocked(player, m, damage, bypass, allyAt: at) is not { } hit) continue;
            hits.Add((id, m, hit));
        }
        // A corpse that explodes shows the explosion whether or not anyone stood in it: the art is the corpse's
        // consumption, and skipping the empty ones left about 85 % of a pack's explosions invisible (live
        // 2026-11-17: 63 of 432). The burst cap stays only as a bound on a pathological chain.
        showArt = TakeExplosionArt(player, now);
        effect = showArt ? player.TakeObjectId() : 0;
        // The explosion consumes the killed monster: the native client removes its corpse at once (0x2bd), and a
        // warcry may not use a corpse that an explosion already took. When the killing blow already consumed it
        // (the Kitava Heart Eater tattoo), the corpse is off the client's list and this is no second removal.
        removeCorpse = !corpse.CorpseConsumed;
        corpse.CorpseConsumed = true;
        player.Area.Monsters.Remove(corpseId);
        // Per corpse and per monster hit: a pack kill wrote about 900 lines a second from inside the combat lock
        // (2026-11-17). Only under Game_TRACE=combat.
        if (ServerTrace.Combat)
            Console.WriteLine($"[gateway] {player.Connection.Remote} kill explosion{(bleeding ? " (bleeding)" : "")} 0x{corpseId:x} for {amount} {type.ToString().ToLowerInvariant()} within {radius} cells:" +
                $" {hits.Count} hit, {hits.Count(h => h.Hit.Life == 0)} killed" +
                (showArt ? "" : ", no art (burst budget)"));
        string art = chaos > 0 ? ChaosExplosionAo : KillExplosionAo;
        var effectBytes = showArt ? InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, art).Blob()) : null;
        try
        {
            await SendKillExplosionBatch(player, effect, effectBytes, at, art,
                removeCorpse, corpseId, corpse.Label, hits);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // One admission problem must not end the projectile session that queued this explosion.
            Console.WriteLine($"[gateway] {player.Connection.Remote} kill explosion send failed: {ServerLog.Describe(ex)}");
        }
        if (removeCorpse) await SendRemainingMonsters(player);
        if (showArt)
            SendAfter(player, ArtMs(art) / 1000.0, async () =>
            {
                if (player.Closed || player.LeavingArea) return;
                await Game.SendWorldRemoval(player, effect, null);
            });
        foreach (var (id, m, hit) in hits)
        {
            if (ServerTrace.Combat)
                Console.WriteLine($"[gateway] {player.Connection.Remote} 0x01b1 monster 0x{id:x} hit by the 0x{corpseId:x} kill explosion for {amount} {type.ToString().ToLowerInvariant()}, life {hit.Life}/{m.MaxLife}");
            if (hit.StageStarts) _ = RunBossStage(player, id, m);
            if (hit.Life == 0)
            {
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
                await MonsterKilled(player, id, m);
            }
        }
        return removeCorpse;
    }

    // A curse explosion uses the killing minion's damage and area. Chain kills follow the same minion outcome path.
    static void ResolveCursedMinionKillLocked(Player player, uint corpseId, SpawnedMonster corpse,
        SpawnedMonster source, IReadOnlyDictionary<uint, int>? skill, double now)
    {
        if (!corpse.CursedAtDeath || !corpse.Hostile || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || HardcoreProgressBlocked(player)) return;
        int chance = CharacterPlayerStats(player).GetValueOrDefault(Stat.ExplodeCursedEnemiesFor25PctLifeAsChaosOnKillChancePct);
        if (!GameServer.Core.World.RandomRolls.Chance(chance, 100, AreaRng(player))) return;

        double factor = MinionAreaDamageFactor(source, DamageType.Chaos, skill);
        double radius = 22 * Math.Sqrt(Math.Max(0, 1 + source.Effects[Stat.BaseSkillAreaOfEffectPlusPct] / 100d));
        var pending = new Queue<(uint Id, SpawnedMonster Monster)>();
        pending.Enqueue((corpseId, corpse));
        while (pending.TryDequeue(out var entry))
        {
            var (id, dead) = entry;
            if (dead.CorpseConsumed || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), dead)) continue;
            var at = dead.Walk.Position(now);
            dead.CorpseConsumed = true;
            player.Area.Monsters.Remove(id);
            player.Minions.OutputQueue.Add(new([], "minion kill explosion removes corpse", WorldId: id, RemovesObject: true));
            if (player.Connection.Connected && TakeExplosionArt(player, now))
            {
                uint effect = player.TakeObjectId();
                var art = new EffectObject(at.X, at.Y, ChaosExplosionAo);
                player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(effect,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath), art.Blob()), "minion chaos kill explosion",
                    WorldId: effect, CreatesObject: true));
                SendAfter(player, ArtMs(ChaosExplosionAo) / 1000d, async () =>
                {
                    if (!player.Closed && !player.LeavingArea) await SendWorldRemoval(player, effect, null);
                });
            }
            if (factor <= 0) continue;
            double raw = dead.MaxLife * .25 * factor;
            var packet = DamagePacket.Of(DamageType.Chaos, raw, raw);
            foreach (var (targetId, target) in player.Area.MonstersNear(at, radius))
            {
                if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable
                    || Dist(target.Walk.Position(now), at) > radius) continue;
                int damage = MonsterTakesRolled(target, packet, spell: false, now: now);
                if (damage <= 0 || ApplyTypedMonsterHitLocked(player, target, damage, damage, allyAt: at) is not { } hit) continue;
                if (hit.StageStarts)
                {
                    CommitBossStage(player, targetId, target, now);
                    hit = hit with { StageStarts = false };
                }
                CommitMinionHitLocked(player, targetId, target, hit, damage, now, "minion chaos kill explosion");
                if (hit.Life == 0 && target.CursedAtDeath && GameServer.Core.World.RandomRolls.Chance(chance, 100, AreaRng(player)))
                    pending.Enqueue((targetId, target));
            }
        }
    }

    // A volley that kills a pack explodes every corpse at once; the previous 8 per 150 ms thinned the art out of
    // a normal chain and read as missing explosions. The queue no longer stalls per object, so the art can follow:
    // one fight peaked at 65 explosions in 150 ms and 86 art objects alive.
    const int ExplosionArtPerWindow = 64;
    const double ExplosionArtWindowSeconds = 0.15;

    static bool TakeExplosionArt(Player player, double now)
    {
        if (now - player.ExplosionArtWindow >= ExplosionArtWindowSeconds)
        {
            player.ExplosionArtWindow = now;
            player.ExplosionArtCount = 0;
        }
        if (player.ExplosionArtCount >= ExplosionArtPerWindow) return false;
        player.ExplosionArtCount++;
        return true;
    }

    // One gate turn for the effect's create/wake, the corpse removal and every life update. The client still reads
    // separate messages, but the server no longer waits behind four separate world sends for each corpse.
    static async Task SendKillExplosionBatch(Player player, uint effect, byte[]? effectBytes, (int X, int Y) at, string art, bool removeCorpse,
        uint corpseId, string corpseLabel, IReadOnlyList<(uint Id, SpawnedMonster M, MonsterHit Hit)> hits)
    {
        string effectLog = $"0x02bb create effect 0x{effect:x} {art} at ({at.X}, {at.Y})";
        string corpseLog = $"0x02bd corpse 0x{corpseId:x} ({corpseLabel}) explodes";
        if (!player.Connection.LockstepClient)
        {
            var all = new List<byte>();
            if (effect != 0 && effectBytes is not null)
            {
                all.AddRange(effectBytes);
                all.AddRange(InstanceProtocol.EnterWakeZone(effect));
            }
            if (removeCorpse) all.AddRange(InstanceProtocol.RemoveObject(corpseId));
            await Send(player, [.. all], "kill explosion", still: () => !player.Closed && !player.LeavingArea);
            return;
        }
        // Each object by itself, as the separate sends this batch replaces did: a target another producer has already
        // removed (a second explosion's victim) drops only its own record. As prerequisites of the whole batch, one
        // such target dropped every other victim's life update, and those monsters stayed alive on the client.
        var output = player.Connection.WorldOutput;
        bool corpseBorn = removeCorpse && await output.AwaitBirths([output.PublishedObject(corpseId)]);
        var born = new HashSet<uint>();
        foreach (var (id, _, _) in hits)
            if (await output.AwaitBirths([output.PublishedObject(id)])) born.Add(id);
        await SendWorldBatch(player, [], admission =>
        {
            var records = new List<OutputRecord>();
            if (effect != 0 && effectBytes is not null)
            {
                var effectObject = admission.NewObject(effect);
                var create = effectBytes.ToArray();
                BinaryPrimitives.WriteUInt32BigEndian(create.AsSpan(6), uint.MaxValue);
                BinaryPrimitives.WriteUInt16BigEndian(create.AsSpan(10), 0);
                records.Add(OutputRecord.Create(effectObject, create, effectLog));
                records.Add(OutputRecord.AfterBirth(effectObject, WorldOrder.First, InstanceProtocol.EnterWakeZone(effect), null));
            }
            if (corpseBorn)
            {
                var corpse = player.Connection.WorldOutput.PublishedObject(corpseId);
                var remove = InstanceProtocol.RemoveObject(corpseId);
                BinaryPrimitives.WriteUInt32BigEndian(remove.AsSpan(6), uint.MaxValue);
                records.Add(OutputRecord.Remove(corpse, WorldOrder.Last, remove, corpseLog));
            }
            return new OutputBatch([.. records]);
        });
    }
}
