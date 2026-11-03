using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

/// <summary>One ability of a Survivors gate boss.</summary>
internal enum SurvivorsBossMove { Slam, Burst, Fan, Ring, Blink }

/// <summary>
/// A gate boss: the in-game model it borrows (the client's own rig and clips), its display name, how much bigger than
/// the model's own size it stands, the damage type of its hits, its ability cycle and its servants.
/// </summary>
internal sealed record SurvivorsBossKit(string Model, string Name, int ScalePct, DamageType Damage,
    IReadOnlyList<SurvivorsBossMove> Moves, int Adds, double SummonSeconds, double Pace = 1);

static partial class SurvivorsRunRules
{
    /// <summary>
    /// The seven gate bosses, one per stage: a new boss on a real model that fits the area (the model's own boss bar
    /// makes it a unique), each with its own damage type, move cycle and servants. Moves are the shared repertoire:
    /// a slam around itself, a delayed burst under the player, a fan or a ring of shots, and a blink.
    /// </summary>
    internal static SurvivorsBossKit SurvivorsBossFor(int stage) => Math.Clamp(stage, 0, SurvivorsLevels.Length - 1) switch
    {
        0 => new("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal", "The Drowned Colossus", 80,
            DamageType.Physical, [SurvivorsBossMove.Slam, SurvivorsBossMove.Burst], 5, 14, 1.05),
        1 => new("Metadata/Monsters/seawitchillusion/BossMerveil", "Tidewrack, the Devourer", 60,
            DamageType.Cold, [SurvivorsBossMove.Fan, SurvivorsBossMove.Ring], 4, 15, 1.0),
        2 => new("Metadata/Monsters/AbyssTwoLeague/MonolithicSlug", "The Mirefather", 50,
            DamageType.Chaos, [SurvivorsBossMove.Slam, SurvivorsBossMove.Burst], 5, 13, 1.1),
        3 => new("Metadata/Monsters/Abberath/Abberath", "Cragbreaker", 60,
            DamageType.Fire, [SurvivorsBossMove.Slam, SurvivorsBossMove.Burst, SurvivorsBossMove.Fan], 4, 12, 0.95),
        4 => new("Metadata/Monsters/Spiders/SpiderBossWeaver", "The Widow of the Climb", 70,
            DamageType.Chaos, [SurvivorsBossMove.Burst, SurvivorsBossMove.Fan, SurvivorsBossMove.Blink], 5, 12, 0.9),
        5 => new("Metadata/Monsters/LeagueHeist/Boss/AdmiralDarnaw", "The Hollow Captain", 70,
            DamageType.Cold, [SurvivorsBossMove.Fan, SurvivorsBossMove.Ring, SurvivorsBossMove.Blink], 5, 12, 0.95),
        _ => new("Metadata/Monsters/AtlasMemory/AngerBoss", "The Ember of Wrath", 50,
            DamageType.Fire, [SurvivorsBossMove.Ring, SurvivorsBossMove.Burst, SurvivorsBossMove.Slam], 5, 11, 0.85),
    };

    /// <summary>Spawns the stage's gate boss where the player can see it, with its own servants and controller.</summary>
    internal static async Task<SurvivorsBossFight?> SpawnSurvivorsGateBoss(string remote, NetworkStream stream,
        ConnectionCiphers ciphers, ClientState conn, SurvivorsRoster roster, SurvivorsRun run, int stage, HashSet<uint> horde,
        CancellationToken token)
    {
        if (SurvivorsSpawnCell(conn, 24) is not { } cell)
        {
            Console.WriteLine($"[gateway] {remote} survivors: no cell for the gate boss of stage {stage + 1}");
            return null;
        }
        var kit = SurvivorsBossFor(stage);
        var variety = Monsters.Find(kit.Model) is { Supported: true } found ? found
            : roster.Bosses.FirstOrDefault() ?? roster.Varieties.LastOrDefault();
        if (variety is null) return null;
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, MonsterRarity.Unique,
            cell.X, cell.Y, name: kit.Name, mods: SurvivorsGateBossMods(roster, variety, run, stage, kit), scripted: true);
        horde.Add(id);
        // A scripted boss skips the AI pass that would show its bar; show it as an aggroed boss at once.
        if (variety.BossHealthBar)
        {
            SpawnedMonster? spawned;
            lock (conn.Monsters) conn.Monsters.TryGetValue(id, out spawned);
            if (spawned is not null)
            {
                spawned.Aggro = true;
                spawned.BossBarShown = true;
                await ShowBossLifeBar(remote, stream, ciphers, id, spawned, conn);
            }
        }
        string area = SurvivorsAreaName(conn.Layout.Id);
        await conn.Commands!.Reply($"--- Gate {stage + 1} ---\n{kit.Name} rises in {area}. Its servants answer the call.");
        Console.WriteLine($"[gateway] {remote} survivors gate boss {kit.Name} ({variety.Id}) 0x{id:x} at {cell} in {area}");
        var fight = new SurvivorsBossFight(remote, stream, ciphers, conn, run, roster, stage, kit, id, token);
        _ = fight.RunAsync();
        return fight;
    }

    /// <summary>
    /// The gate boss's rolled mods: the unique's own affixes kept (their rarity life factor solved out), the life target
    /// for the stage's average build and the model's extra size.
    /// </summary>
    internal static MonsterModSet SurvivorsGateBossMods(SurvivorsRoster roster, MonsterCatalog.Variety boss,
        SurvivorsRun run, int stage, SurvivorsBossKit kit)
    {
        var rolled = MonsterModRolls.Value.Roll(boss, roster.Level, MonsterRarity.Unique, Random.Shared, alone: true);
        int baseLife = Math.Max(1, Monsters.BaseLife(boss, roster.Level) ?? 100);
        int target = SurvivorsRules.BossGateLife(SurvivorsPoolTop(roster, stage), stage,
            stage * SurvivorsRules.StageSeconds);
        double factor = (1 + rolled.Effects["monster_life_+%_final_from_rarity"] / 100.0)
            * (1 + rolled.Effects["monster_life_+%_final_from_rarity_table"] / 100.0);
        int lifePct = Math.Clamp((int)Math.Round((target / (baseLife * Math.Max(0.01, factor)) - 1) * 100), -99, 1_000_000);
        return rolled with
        {
            Effects = rolled.Effects.Plus("maximum_life_+%", lifePct).Plus("base_actor_scale_+%", kit.ScalePct),
        };
    }

    /// <summary>
    /// One gate fight: the controller owns the boss's actions (the ordinary AI skips a scripted monster), cycles its
    /// moves, summons servants and enrages it below half life. The player's own skills still cast from the run loop.
    /// </summary>
    internal sealed class SurvivorsBossFight(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, SurvivorsRoster roster, int stage, SurvivorsBossKit kit, uint bossId,
        CancellationToken token)
    {
        double _nextMove, _nextSummon;
        int _moveIndex;
        bool _enraged;

        internal uint BossId => bossId;
        internal bool Defeated { get; private set; }

        internal async Task RunAsync()
        {
            try
            {
                double start = conn.Clock.Elapsed.TotalSeconds;
                _nextMove = start + 4;
                _nextSummon = start + 6;
                Console.WriteLine($"[gateway] {remote} survivors gate fight {kit.Name} begins (0x{bossId:x})");
                while (!token.IsCancellationRequested && !conn.Closed && !conn.LeavingArea
                    && conn.PendingTransfer is null && ReferenceEquals(StateOf(conn).Survivors, run))
                {
                    if (conn.Paused)
                    {
                        await Task.Delay(SurvivorsStepMs, token);
                        continue;
                    }
                    SpawnedMonster? boss;
                    lock (conn.Monsters) conn.Monsters.TryGetValue(bossId, out boss);
                    if (boss is null || boss.Dead || boss.Life == 0)
                    {
                        Defeated = true;
                        return;
                    }
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    if (!_enraged && boss.Life <= boss.MaxLife / 2) await Enrage();
                    if (now >= _nextSummon) { await SummonAdds(boss); _nextSummon = now + kit.SummonSeconds; }
                    if (now >= _nextMove)
                    {
                        var move = kit.Moves[_moveIndex++ % kit.Moves.Count];
                        await Use(move, boss);
                        _nextMove = conn.Clock.Elapsed.TotalSeconds + Cooldown(move);
                    }
                    await Task.Delay(SurvivorsStepMs, token);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"[gateway] {remote} survivors gate boss {kit.Name} stopped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        async Task Use(SurvivorsBossMove move, SpawnedMonster boss)
        {
            Console.WriteLine($"[gateway] {remote} survivors gate boss {kit.Name} uses {move}");
            switch (move)
            {
                case SurvivorsBossMove.Slam:
                    if (Attack(boss, 1.0, seconds: 1.7, range: 9 + stage, spell: true) is { } slam)
                        await MonsterSwing(remote, stream, ciphers, conn, bossId, boss, NextAction(boss), slam);
                    break;
                case SurvivorsBossMove.Burst:
                    if (Attack(boss, 0.9, seconds: 1.9, range: 30, spell: true) is { } burst)
                        await MonsterAreaContact(remote, stream, ciphers, conn, bossId, boss, burst,
                            NextAction(boss), 6 + stage * 0.4);
                    break;
                case SurvivorsBossMove.Fan:
                    if (Projectile(boss, count: 5, spread: Math.PI / 3) is { } fan
                        && Attack(boss, 0.45, seconds: 1.5, range: 30, spell: true, projectile: fan) is { } fanAttack)
                        await MonsterSwing(remote, stream, ciphers, conn, bossId, boss, NextAction(boss), fanAttack);
                    break;
                case SurvivorsBossMove.Ring:
                    if (Projectile(boss, count: 12, spread: Math.Tau) is { } ring
                        && Attack(boss, 0.35, seconds: 1.7, range: 30, spell: true, projectile: ring) is { } ringAttack)
                        await MonsterSwing(remote, stream, ciphers, conn, bossId, boss, NextAction(boss), ringAttack);
                    break;
                case SurvivorsBossMove.Blink:
                    await Blink(boss);
                    break;
            }
        }

        int NextAction(SpawnedMonster boss)
        {
            lock (conn.Monsters) return ++boss.ActionGeneration;
        }

        MonsterAttack? Attack(SpawnedMonster boss, double multiplier, double seconds, double range, bool spell,
            SkillCatalog.MonsterProjectileInfo? projectile = null)
        {
            var attack = boss.Attack ?? new MonsterAttack(SkillCatalog.FirstSkillId, 1.5, 8, DamagePacket.Of(kit.Damage, 1, 1));
            return attack with
            {
                Seconds = seconds * kit.Pace, Range = range, Spell = spell, Scripted = true,
                Damage = Hit(multiplier), Release = null, Summon = null, Projectile = projectile,
                Ranged = projectile is not null,
            };
        }

        DamagePacket Hit(double multiplier)
        {
            int pool = (int)Math.Min(int.MaxValue, conn.PlayerMaxLife + (long)conn.EnergyShield.Maximum);
            int damage = SurvivorsRules.BossHitDamage(pool, Resistance(conn, kit.Damage), kit.Damage,
                multiplier * (_enraged ? 1.2 : 1));
            return DamagePacket.Of(kit.Damage, damage, damage);
        }

        SkillCatalog.MonsterProjectileInfo? Projectile(SpawnedMonster boss, int count, double spread)
        {
            var catalog = SkillsFor(conn);
            IEnumerable<MonsterCatalog.Variety> pool = roster.Ranged is { Count: > 0 } ranged ? ranged : roster.Varieties;
            foreach (var variety in pool)
            {
                int effect = MavenNucleus.CombatEffect(catalog, variety);
                if (effect < 0 || catalog.MonsterProjectile(effect) is not { } shot) continue;
                // A lobbed shot lands on one aim and cannot fan or ring; the generic straight projectile can.
                if (shot.Lobbed) continue;
                return shot with { Count = count, SpreadRadians = spread };
            }
            return GenericProjectile(kit.Damage, count, spread);
        }

        static SkillCatalog.MonsterProjectileInfo GenericProjectile(DamageType type, int count, double spread) =>
            new(type switch
            {
                DamageType.Fire => "Metadata/Projectiles/MonsterGenericProjectileFire",
                DamageType.Cold => "Metadata/Projectiles/MonsterGenericProjectileCold",
                DamageType.Lightning => "Metadata/Projectiles/MonsterGenericProjectileLightning",
                DamageType.Chaos => "Metadata/Projectiles/MonsterGenericProjectilePoison",
                _ => "Metadata/Projectiles/MonsterGenericProjectilePhysical",
            }, 90, false, 1.2)
            { Count = count, SpreadRadians = spread };

        async Task Blink(SpawnedMonster boss)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (px, py) = conn.Walk.Position(now);
            var (bx, by) = boss.Walk.Position(now);
            double dx = bx - px, dy = by - py, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.01) { dx = 1; dy = 0; length = 1; }
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double distance = 22 + Random.Shared.NextDouble() * 10;
                int x = (int)Math.Round(px + dx / length * distance), y = (int)Math.Round(py + dy / length * distance);
                if (conn.Layout.GridW > 0 && (x < 0 || y < 0 || x >= conn.Layout.GridW || y >= conn.Layout.GridH)) continue;
                if (!conn.Layout.WalkableFor(x, y, 0) || conn.Barrier.Contains((x, y))) continue;
                lock (conn.Monsters) boss.Walk.Teleport(x, y, now);
                await Send(remote, stream, ciphers, InstanceProtocol.Teleport(bossId, x, y),
                    $"0x0054 survivors boss {kit.Name} blinks to ({x}, {y})");
                return;
            }
        }

        async Task SummonAdds(SpawnedMonster boss)
        {
            int alive;
            lock (conn.Monsters)
                alive = conn.Monsters.Values.Count(m => m.SummonerId == bossId && !m.Dead && m.Life > 0);
            int count = Math.Min(kit.Adds, Math.Max(0, 12 - alive));
            if (count <= 0 || roster.Varieties.Count == 0) return;
            var (bx, by) = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                var variety = roster.Varieties[Random.Shared.Next(roster.Varieties.Count)];
                if (Near(bx, by) is not { } cell) continue;
                await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, MonsterRarity.Normal,
                    cell.X, cell.Y, log: false, extraMods: SurvivorsMonsterMods(run.Elapsed),
                    owned: (_, servant) => { servant.SummonerId = bossId; servant.NoRewards = true; });
                spawned++;
            }
            if (spawned > 0) await conn.Commands!.Reply($"{kit.Name} calls {spawned} servant(s)!");
        }

        (int X, int Y)? Near(int x, int y)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double angle = Random.Shared.NextDouble() * Math.Tau, distance = 4 + Random.Shared.NextDouble() * 7;
                int sx = (int)Math.Round(x + Math.Cos(angle) * distance), sy = (int)Math.Round(y + Math.Sin(angle) * distance);
                if (conn.Layout.GridW > 0 && (sx < 0 || sy < 0 || sx >= conn.Layout.GridW || sy >= conn.Layout.GridH)) continue;
                if (!conn.Layout.WalkableFor(sx, sy, 0) || conn.Barrier.Contains((sx, sy))) continue;
                return (sx, sy);
            }
            return null;
        }

        async Task Enrage()
        {
            _enraged = true;
            await conn.Commands!.Reply($"{kit.Name} is enraged!");
            Console.WriteLine($"[gateway] {remote} survivors gate boss {kit.Name} enrages at half life");
        }

        double Cooldown(SurvivorsBossMove move) =>
            (move switch
            {
                SurvivorsBossMove.Slam => 6.0,
                SurvivorsBossMove.Burst => 7.0,
                SurvivorsBossMove.Fan => 5.5,
                SurvivorsBossMove.Ring => 8.0,
                _ => 9.0,
            } + stage * 0.2) * kit.Pace * (_enraged ? 0.7 : 1);
    }
}
