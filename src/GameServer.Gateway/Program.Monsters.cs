using System.Diagnostics;
using System.Net;
using System.Text.Json;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using Progression = GameServer.Core.World.Progression;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamageRange = GameServer.Core.World.DamageRange;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

partial class Program
{
    // Creates one monster: registers its type at this level (0x20, once per connection), creates it (0x2b9) and puts it in
    // the wake zone (0x1a9), and keeps it for the AI. /spawn_mob and the area's packs (PopulateArea) use it.
    static async Task<uint> SpawnMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MonsterCatalog.Variety variety,
        byte level, MonsterRarity rarity, int x, int y, string? name = null, bool friendly = false, bool log = true, uint? damagedLife = null,
        SummonRoster.Member? summon = null, HarbingerCompanionCatalog.Companion? companion = null, Func<bool>? accept = null,
        MonsterModSet? mods = null, bool alone = true, bool? unrisen = null)
    {
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        bool owned = summon is not null || companion is not null;
        // Owned actors use their own profile, never hostile rarity rolls or buried/emerge scheduling.
        var emerge = owned ? EmergeKind.None : Emerges.Kind(variety, rarity);
        bool waits = emerge != EmergeKind.None && (unrisen ?? true);
        mods = owned ? MonsterModSet.None : MonsterModsFor(conn, variety, level, rarity, mods, alone);
        int baseLife = Monsters.BaseLife(variety, level) ?? 100;
        uint life = (uint)mods.Effects.MaxLife(baseLife);
        // The type supplies the animated object; Game_MONSTER_AO=1 also sends the variety's first AOFiles path (Animated
        // bit0), which is not needed.
        string? ao = Environment.GetEnvironmentVariable("Game_MONSTER_AO") == "1" ? variety.AnimatedObject : null;
        ushort team = friendly ? (ushort)0 : (ushort)1;
        // Its attack: the variety's first granted effect (Hungry Corpse: Melee) at its level, with the variety's attack time,
        // reach and the level's damage as the skill's final stats (11-skills.md §7).
        // The mods' damage and speed: the rarity's more damage per hit and faster attacks with that much less damage, the
        // affixes' increased damage, attack speed and extra elemental / chaos damage (MonsterModEffects.Hit).
        var (baseMin, baseMax) = Monsters.BaseDamage(variety, level) ?? (1, 1);
        var attack = new MonsterAttack(SkillCatalog.FirstSkillId, mods.Effects.AttackSeconds(variety.AttackMs / 1000.0), variety.AttackRange,
            mods.Effects.Hit(DamagePacket.Of(DamageType.Physical, baseMin, baseMax)));
        if (summon is not null)
        {
            var profile = WolfProfile(conn, variety, level);
            life = profile.Life;
            attack = profile.Attack;
        }
        if (companion is not null) life = 1; // HarbingerMinionBuffStartsOnCooldown.ot: set_maximum_life=1.
        var skill = variety.GrantedEffects is [int effect, ..]
            ? Skills.FindByRow(attack.SkillId, effect, level, attack.SkillStats()) : null;
        // A boss with stages also has the skill that changes them (Hillock's sword extract, BossFights).
        var stage = BossFights.For(variety.Id);
        // Its stats as the attack's (skill_is_non_pathing above all, and the stage's own time): without them the client sent
        // the action through its pathing step, played nothing and kept the monster in it (live 2026-10-21).
        var stageSkill = skill is not null && stage is not null
            ? Skills.FindByRow(StageSkillId, stage.SkillEffect, level, (attack with { SkillId = StageSkillId, Seconds = stage.CastSeconds }).SkillStats())
            : null;
        List<(uint, int)>? stats = skill is null ? null : [.. attack.Stats()];
        // The final maximum too: the client applies no modifier of its own (as for the player's maximum_life), so without it
        // the life bars kept the base maximum and a unique showed full until its life fell below it (live 2026-10-21).
        if (!owned)
            foreach (var stat in MonsterModClientStats(mods.Effects, life, baseLife)) (stats ??= []).Add(stat);
        if (summon is not null)
        {
            (stats ??= []).Add((StatKeys.MaximumLife, (int)life));
            int movement = Stats.Key("minion_movement_speed_+%") is uint movementKey
                ? CharacterPlayerStats(conn).GetValueOrDefault(movementKey) : 0;
            stats.Add((StatKeys.MovementVelocityPct, movement));
        }
        IReadOnlyList<GrantedSkill>? objectSkills = skill is null ? null : stageSkill is null ? [skill] : [skill, stageSkill];
        if (companion is not null)
        {
            stats = [.. companion.MinionStats.Select(kv => (kv.Key, kv.Value)), (StatKeys.MaximumLife, 1)];
            foreach (string flag in new[] { "base_cannot_be_damaged", "base_cannot_be_stunned", "untargetable_by_monster_ai" })
                if (Stats.Key(flag) is uint key) stats.Add((key, 1));
            var effects = new[] { companion.WarpEffect }.Concat(companion.Buffs.Select(b => b.Effect)).ToArray();
            var ids = Skills.SkillIds().Take(effects.Length).ToArray();
            objectSkills = effects.Select((effect, index) => Skills.FindByRow(ids[index], effect, 1,
                    [.. Skills.EffectStats(effect, 1), (StatKeys.SkillIsNonPathing, 1)]))
                .OfType<GrantedSkill>().ToArray();
        }
        var monster = new MonsterObject(x, y, life, variety.ComponentsOrBase, ao, rarity, name, variety.StateMachine,
            MonsterEmerge.CreateState(variety, emerge, waits), team,
            objectSkills, stats)
        {
            Mods = mods.Mods, NameWords = mods.NameWords,
            LifeData = owned ? variety.LifeData with { CorpseUsable = false } : variety.LifeData,
        };
        SpawnedMonster State() => new($"{variety.Name}@{level}", conn.NewWalk(x, y, mods.Effects.MovementSpeed(variety.MovementSpeed)),
            variety.AggroRange, Math.Max(2, variety.ObjectSize + 1), life)
        {
            Attack = skill is null || companion is not null ? null : attack, Hostile = !friendly, Variety = variety, Level = level,
            Rarity = rarity, Summon = summon, Companion = companion,
            Life = Math.Min(damagedLife ?? life, life), Stage = stageSkill is null ? null : stage, States = variety.StateMachine,
            HitAnimations = Monsters.TakeHit(variety.Id), Mods = mods,
        };
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        if (companion is not null)
        {
            // A source can disappear while this task waits behind another sender. Register the actor and accept all
            // three creation messages together, only after checking the current lease/source/area inside that gate.
            bool sent = await SendGenerated(remote, stream, ciphers, () =>
            {
                lock (conn.Monsters)
                {
                    if (accept is not null && !accept()) return null;
                    var packets = new List<byte>();
                    if (conn.MonsterTypes.Add((variety.Row, level)))
                        packets.AddRange(InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]));
                    packets.AddRange(InstanceEntities.CreateObject(id, type, monster.Blob()));
                    packets.AddRange(InstanceProtocol.EnterWakeZone(id));
                    conn.Monsters[id] = State();
                    return packets.ToArray();
                }
            }, !log ? null : $"Harbinger create 0x{id:x} {variety.Id}@{level}");
            return sent ? id : 0;
        }
        // A monster type needs a level: 0x20 registers "<Id>@<level>" (once per connection), the 0x2b9 type is its hash.
        if (conn.MonsterTypes.Add((variety.Row, level)))
            await Send(remote, stream, ciphers, InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]),
                $"0x0020 monster type {variety.Id}@{level} (row {variety.Row})");
        // The world is active by now, so 0x2b9 is created at once; 0x1a9 makes it drawn and updated.
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, monster.Blob()), !log ? null :
            $"0x02b9 create {rarity}{(friendly ? " friendly" : "")}{(waits ? $" {emerge.ToString().ToLowerInvariant()}" : "")} monster 0x{id:x} {variety}{(name is null ? "" : $" \"{name}\"")} type=0x{type:x8} at ({x}, {y}), life {life}, components {string.Join(", ", variety.ComponentsOrBase)}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), !log ? null : $"0x01a9 monster 0x{id:x} enters wake zone");
        lock (conn.Monsters) MonsterEmerge.AddLocked(conn, id, State(), emerge, waits);
        if (waits) WatchBuried(remote, stream, ciphers, conn);
        // A kept monster that was hit: its life as a hit sets it (0x1b1).
        if (damagedLife < life)
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, damagedLife.Value), !log ? null : $"0x01b1 monster 0x{id:x} life {damagedLife}/{life}");
        return id;
    }

    // The area's monsters, once the player is in (Agents/docs/protocol/14-monster-packs.md): MonsterPacks scattered over the
    // walk map, the room points' packs and the unique boss at "mapboss" (AreaPopulation). Towns have none; neither does an
    // area without a walk map. Game_MONSTERS=0 turns it off.
    static async Task PopulateArea(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var layout = conn.Layout;
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
        // An instance entered before keeps its monsters as the last connection left them: where they were, with their
        // damage; the dead stay dead (#12).
        if (conn.Instance.Monsters is { } saved)
        {
            conn.MonstersPlaced = true;
            foreach (var s in saved)
                if (Monsters.Find(s.Variety.ToString()) is { } kept)
                    await SpawnMonster(remote, stream, ciphers, conn, kept, s.Level, s.Rarity, s.X, s.Y, friendly: !s.Hostile, log: false, damagedLife: s.Life, mods: s.Mods, unrisen: s.Unrisen);
            Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: {saved.Count} kept from the instance");
            return;
        }
        // The packs need the walk map; until it exists nothing is placed, so nothing is kept either.
        if (layout.Walkable is not { } walkable) return;
        conn.MonstersPlaced = true;
        if (Packs.For(layout.Id) is not { } area || (area.Scattered.Count == 0 && area.Bosses.Count == 0)) return;
        if (Monsters.Error is { } error) { Console.WriteLine($"[gateway] {remote} area monsters: {error}"); return; }
        // Emerge monsters (Gravel Eater, Tide Strider …) are placed waiting and rise near the player (MonsterEmerge.cs).
        bool Spawnable(int row) => Monsters.Find(row.ToString()) is { Supported: true } v && Emerges.CanPlace(v);
        var notes = new List<string>();
        var points = AreaPopulation.LoadPoints(layout.Id, conn.Seed, notes);
        var spawns = AreaPopulation.Plan(area, layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY),
            [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))], points,
            HashCode.Combine(layout.Hash, conn.Seed), Spawnable, notes);
        foreach (var n in notes) Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: {n}");
        byte level = (byte)Math.Clamp(area.AreaLevel, 1, 100);
        foreach (var s in spawns)
        {
            var variety = Monsters.Find(s.Variety.ToString())!;
            bool notable = s.Rarity is MonsterRarity.Unique or MonsterRarity.Rare;
            await SpawnMonster(remote, stream, ciphers, conn, variety, level, s.Rarity, s.X, s.Y, log: notable, alone: false);
        }
        Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: created {spawns.Count} at level {level}: " +
            string.Join(", ", spawns.GroupBy(s => s.Pack).Select(g => $"{g.Key} × {g.Count()}")) +
            $"; waiting to rise: {MonsterEmerge.Unrisen(conn).Count} ({MonsterEmerge.Buried(conn).Count} buried)" +
            $"; unique: {string.Join(", ", spawns.Where(s => s.Rarity == MonsterRarity.Unique).Select(s => $"{Monsters.Find(s.Variety.ToString())!.Name} at ({s.X}, {s.Y})").DefaultIfEmpty("none"))}");
    }

    static async Task RunMonsterAi(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        const ushort Move = 0x2909;
        try
        {
            while (!conn.Closed)
            {
                await Task.Delay(500);
                await RefreshPassivePresences(remote, stream, ciphers, conn);
                await RefreshSummons(remote, stream, ciphers, conn);
                await RefreshCompanions(remote, stream, ciphers, conn);
                if (!conn.AiEnabled || conn.Paused) continue;
                await RunSummonAi(remote, stream, ciphers, conn);
                var orders = new List<(uint Id, (int X, int Y) From, (int X, int Y) To, string Why)>();
                var swings = new List<(uint Id, SpawnedMonster M, (int X, int Y) From)>();
                var creatureSwings = new List<(uint Id, SpawnedMonster M, uint TargetId, SpawnedMonster Target, (int X, int Y) From)>();
                var bossBars = new List<(uint Id, SpawnedMonster M)>();
                double now = conn.Clock.Elapsed.TotalSeconds;
                var (px, py) = conn.Walk.Position(now);
                lock (conn.Monsters)
                {
                    foreach (var (id, m) in conn.Monsters)
                    {
                        if (m.Dead || m.Owned) continue;
                        var (mx, my) = m.Walk.Position(now);
                        var targetAt = (X: px, Y: py);
                        var ownedTarget = m.Hostile && !conn.FlaskGrounds.Taunted(id)
                            ? conn.Monsters.Where(kv => kv.Value is { Summon: not null, Dead: false, Life: > 0 })
                            .OrderBy(kv => Dist((mx, my), kv.Value.Walk.Position(now))).FirstOrDefault() : default;
                        if (ownedTarget.Value is not null
                            && Dist((mx, my), ownedTarget.Value.Walk.Position(now)) < Dist((mx, my), targetAt))
                            targetAt = ownedTarget.Value.Walk.Position(now);
                        else ownedTarget = default;
                        double d = Dist((mx, my), targetAt);
                        if (!m.Aggro && d <= m.AggroRange) { m.Aggro = true; m.LastTarget = null; }
                        if (m.Aggro && !m.BossBarShown && m.Variety is { BossHealthBar: true } && m.States?.IndexOf("boss_life_bar") >= 0)
                        {
                            m.BossBarShown = true;
                            bossBars.Add((id, m));
                        }
                        else if (m.Aggro && d > 2 * m.AggroRange)
                        {
                            m.Aggro = false;
                            orders.Add((id, m.Walk.Order(mx, my, now), (mx, my), "gives up"));
                            continue;
                        }
                        if (!m.Aggro || now < m.BusyUntil) continue;
                        // In reach: stop and swing at the player (hostile monsters with an attack, while the player lives).
                        if (m.Hostile && m.Attack is { } a && d <= a.Range && conn.PlayerLife > 0)
                        {
                            m.BusyUntil = now + a.Seconds;
                            m.LastTarget = null;
                            m.Walk.Order(mx, my, now);
                            if (ownedTarget.Value is not null) creatureSwings.Add((id, m, ownedTarget.Key, ownedTarget.Value, (mx, my)));
                            else swings.Add((id, m, (mx, my)));
                            continue;
                        }
                        if (d <= m.StopRange) continue;
                        // The cell StopRange - 1 from the player on the line toward the monster (the player's cell if blocked).
                        double f = (m.StopRange - 1) / d;
                        (int X, int Y) to = ((int)Math.Round(targetAt.X + (mx - targetAt.X) * f), (int)Math.Round(targetAt.Y + (my - targetAt.Y) * f));
                        if (conn.Layout.Walkable is { } w && !w(to.X, to.Y)) to = targetAt;
                        if (m.LastTarget is { } last && Math.Abs(last.X - to.X) + Math.Abs(last.Y - to.Y) < 3) continue;
                        m.LastTarget = to;
                        orders.Add((id, m.Walk.Order(to.X, to.Y, now), to, $"chases (distance {d:F0})"));
                    }
                }
                foreach (var (id, from, to, why) in orders)
                    await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, Move, 0xffff, 0x0408),
                        $"0x01ab monster 0x{id:x} {why}: move from {from} to {to}");
                foreach (var (id, m) in bossBars)
                    await ShowBossLifeBar(remote, stream, ciphers, id, m, conn);
                foreach (var (id, m, from) in swings)
                    _ = MonsterSwing(remote, stream, ciphers, conn, id, m, from, (px, py));
                foreach (var (id, m, targetId, target, from) in creatureSwings)
                    _ = CreatureSwing(remote, stream, ciphers, conn, id, m, targetId, target, from);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} monster AI stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // A monster swings at the player: its skill as an action aimed at the player (0x1ab, flags | 0x80 + target id, sequence
    // 0xffff), then at the contact point (half the attack time) the hit lands if the monster still lives and the player is
    // still in reach (walking away dodges it): the player's life goes down by the monster's damage (0x1b1 on the player).
    // At 0 the client runs the player's death.
    // PoE's evasion entropy (wiki: Evasion): shared by every attack against the player, reset after 3.33 s. Call under
    // the Monsters lock.
    static bool EvadesLocked(ClientState conn, int hitChance, double now)
    {
        if (conn.EvasionEntropy < 0 || now - conn.LastEvasionAt > 3.33)
            conn.EvasionEntropy = Random.Shared.Next(100);
        conn.LastEvasionAt = now;
        conn.EvasionEntropy += hitChance;
        if (conn.EvasionEntropy < 100) return true;
        conn.EvasionEntropy -= 100;
        return false;
    }

    static async Task MonsterSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, (int X, int Y) from, (int X, int Y) player)
    {
        var a = m.Attack!;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(id, from.X, from.Y, player.X, player.Y, a.SkillId, 0xffff, 0x0408, targetObjectId: PlayerId),
            $"0x01ab monster 0x{id:x} ({m.Label}) attacks the player, attack time {a.Seconds:0.00} s");
        await Task.Delay(TimeSpan.FromSeconds(a.Seconds / 2));
        await WhilePaused(conn);   // a swing started just before the pause lands after it
        double now = conn.Clock.Elapsed.TotalSeconds;
        uint life, mana, manaBefore;
        int rawDamage = 0, damage = 0, chaosDamage = 0;
        bool blocked, monsterCritical = false;
        lock (conn.Monsters)
        {
            if (m.Dead || conn.PlayerLife == 0 || conn.Closed) return;
            var here = conn.Walk.Position(now);
            if (Dist(here, m.Walk.Position(now)) > a.Range + 2)
            {
                Console.WriteLine($"[gateway] {remote} monster 0x{id:x} missed: the player is {Dist(here, m.Walk.Position(now)):0.0} away");
                return;
            }
            // The monster's accuracy comes from DefaultMonsterStats at its level. Evasion uses PoE's shared entropy
            // counter: every attack adds its hit chance, and reaching 100 consumes 100 and hits (evasion cannot streak
            // indefinitely). A new counter starts after 3.33 s without an attack.
            int monsterAccuracy = conn.FlaskGrounds.Accuracy(id, m.Mods.Effects.Accuracy(Monsters.BaseAccuracy(m.Level) ?? 0));   // blinded in smoke
            int hitChance = DamageModel.AccuracyHitChance(conn.Pools.Evasion, monsterAccuracy);
            if (EvadesLocked(conn, hitChance, now))
            {
                Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) evaded: accuracy {monsterAccuracy} " +
                    $"vs evasion {conn.Pools.Evasion}, {hitChance}% chance to hit, entropy {conn.EvasionEntropy:0}");
                return;
            }
            // Roll each damage type separately, apply its resistance, then armour to the physical part. The attack is
            // currently physical only (MonsterAttack), but the packet path keeps future monster skills correct.
            var playerAtHit = CharacterPlayerStats(conn);
            var rolls = GamblerFor(playerAtHit);
            var rolled = rolls.RollIncomingHit(a.Damage, Random.Shared);
            rolled = MonsterModHit(m, rolled, conn.PlayerLife, conn.PlayerMaxLife, Random.Shared, out monsterCritical,
                () => SelfCriticalReduction(conn));
            rolled = IncomingPassiveConversion(conn, m, rolled);
            var mitigated = rolls.MitigateIncomingHit(rolled, type =>
                new DamageModel.ResistanceValue(Resistance(conn, type), MaxResistance(conn, type)), Random.Shared);
            var immune = PlayerImmunities(conn);
            rawDamage = (int)Math.Round(rolled[DamageType.Physical].Min);
            damage = 0;
            foreach (var (type, range) in mitigated.Components)
            {
                int value = immune.Contains(type) ? 0 : (int)Math.Round(range.Min);
                value = ApplyPlayerArmour(conn, type, value);
                if (type == DamageType.Chaos) chaosDamage += value;
                damage += value;
            }
            blocked = DamageModel.Blocks(conn.Pools.BlockChance, Random.Shared.NextDouble() * 100,
                conn.Pools.AttackBlockLucky, conn.Pools.AttackBlockUnlucky);
            if (blocked) { damage = 0; chaosDamage = 0; }
            RageOf(conn).EnemyHit(DateTimeOffset.UtcNow, RageInputs(conn, CharacterStatSources(conn)), Random.Shared.Next(100));
            manaBefore = conn.PlayerMana;
            int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock : 0;
            int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock : 0;
            conn.PlayerLife = (uint)Math.Min(UnreservedLife(conn), Math.Max(0, (long)conn.PlayerLife
                - DamagePlayerHit(conn, damage - chaosDamage, chaosDamage, now) + gainedLife));
            conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
            ChargeIncomingCommitted(conn, blocked, spell: false, playerAtHit);
            life = conn.PlayerLife;
            mana = conn.PlayerMana;
        }
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
        await SendWardState(remote, stream, ciphers, conn);
        if (!blocked && life > 0 && a.Damage.Components.All(c => c.Type == DamageType.Physical))
            await SendPlayerHitRecovery(remote, stream, ciphers, conn, damage);
        // At 0 the Life tick stops refreshing the overhead life bar (see Attack): hide it.
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1");
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionHit(id, 0xffff, PlayerId, flags: 0x4601, kind: 0x02, u34: 450,
                angle: HitAngle(player.X - from.X, player.Y - from.Y)),
            $"0x01af monster 0x{id:x} hit on the player (sequence 0xffff)");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life, (uint)ReservedLife(conn)),
            $"0x01b1 player hit by monster 0x{id:x} for {damage}" +
            $"{(rawDamage != damage ? $" ({rawDamage} physical before {DamageModel.ArmourReduction(conn.Pools.Armour, rawDamage)}% armour)" : "")}" +
            $"{(blocked ? " (blocked)" : "")}, life {life}/{conn.PlayerMaxLife}{(life == 0 ? " (dies)" : "")}");
        if (mana != manaBefore)
            await SendPlayerMana(remote, stream, ciphers, conn,
                $"0x01b1 player mana {mana} ({(blocked ? $"+{conn.Pools.ManaGainedOnBlock} on block" : "no block gain")})");
        if (life == 0)
            await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)");
        else if (!blocked) await GainFlaskChargesWhenHit(remote, stream, ciphers, conn);
    }

    // A spawned monster's id from the argument if it is 0x hex, else the last spawned one.
    static uint? MonsterArg(ClientState conn, string[] args, int i)
    {
        if (args.Length > i && (args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
        {
            uint id = Convert.ToUInt32(args[i], 16);
            return conn.Monsters.ContainsKey(id) ? id : null;
        }
        return conn.Monsters.Count == 0 ? null : conn.Monsters.Keys.Last();
    }

    // A cell 3 cells from the server's estimate of the player, walkable if the walk map says so.
    static (int X, int Y) NextToPlayer(ClientState conn)
    {
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        foreach (var (dx, dy) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, -2) })
            if (conn.Layout.Walkable is not { } w || w(px + dx, py + dy)) return (px + dx, py + dy);
        return (px, py);
    }
}
