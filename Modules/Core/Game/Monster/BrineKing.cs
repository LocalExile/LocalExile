using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The campaign Brine King. The map variants have separate encounters.</summary>
public static class BrineKing
{
    internal const string Area = "2_6_15";
    public const string Boss = "Metadata/Monsters/BrineKing/BrineKing";
    const string Wave = "Metadata/Effects/Spells/monsters_effects/act6/Brine_King/tidal_wave/tidal_wave";
    const string Art = "Metadata/Effects/Spells/monsters_effects/act6/Brine_King/";
    const string PhaseKey = "BrineKing";
    static readonly int[] PhaseThresholds = [75, 50, 25];
    public const int MeleeEffect = 406;
    static readonly int[] Effects = [6354, 6359, 6670, 6668, 6361, MeleeEffect, 4365, 6759, 6761, 6360, 6355];
    static readonly Store<InstanceState, Fight> Fights = new();
    static readonly Store<Player, Tide> Tides = new();
    // Row 856 is not IsRecovery: its GrantedStats do not travel in 0x1c1 (11-skills.md §12).
    const ushort TideRow = 856, MaimRow = 513;
    sealed class Tide
    {
        public bool Active;
        public double Degen;
        public double MaimUntil;
        public bool MaimActive;
    }
    public static Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> sources)
    {
        if (Tides.TryGetValue(player, out var tide) && tide.Active && Stat.ActionSpeedMinusPct is var key)
            sources[key] = sources.GetValueOrDefault(key) + 30;
        if (Tides.TryGetValue(player, out tide) && tide.MaimUntil > AreaTime(player) && Stat.MovementVelocityPlusPct is var movement)
            sources[movement] = sources.GetValueOrDefault(movement) - 30;
        return sources;
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
            foreach (var pair in Tides)
            {
                pair.Key.Degens[pair.Value] = 0;
                pair.Value.Degen = 0; pair.Value.Active = false;
                RemoveTideBuff(pair.Key, TideRow);
                RemoveTideBuff(pair.Key, MaimRow);
            }
        }
    }
    static void RemoveTideBuff(Player player, ushort row)
    {
        if (!player.Closed) player.Area.Buffs.Clear(player.Id, row);
    }
    public static async Task Leaving(Player player)
    {
        await TideAt(player, false, 0);
        if (Tides.TryGetValue(player, out var tide))
        { tide.MaimUntil = 0; tide.MaimActive = false; RemoveTideBuff(player, MaimRow); }
    }
    static async Task Maim(Player player)
    {
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(Stat.Of("cannot_be_maimed")) != 0 || AreaRng(player).Next(100) < Get(Stat.Of("base_avoid_maim_%"))) return;
        var tide = Tides.GetValue(player, _ => new());
        tide.MaimActive = true; tide.MaimUntil = AreaTime(player) + 4;
        player.Area.Buffs.Set(player.Id, new(MaimRow, U68: Buff.Seconds(4)));
        await RefreshPlayerStats(player);
    }
    static async Task RefreshPlayerStats(Player player)
    {
        player.Pools = PoolsFor(player, player.Pools.Level); player.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player), null);
    }
    static async Task TideAt(Player player, bool active, double rawDps)
    {
        var tide = Tides.GetValue(player, _ => new());
        bool changed;
        double dps = active ? rawDps * (1 - Math.Min(MaxResistance(player, DamageType.Cold), Resistance(player, DamageType.Cold)) / 100.0) : 0;
        player.Degens[tide] = dps;
        tide.Degen = dps;
        changed = tide.Active != active;
        tide.Active = active;
        if (!changed) return;
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        // Row 856 is not IsRecovery: its GrantedStats do not travel in 0x1c1 (11-skills.md §12).
        if (active) player.Area.Buffs.Set(player.Id, new(TideRow)); else RemoveTideBuff(player, TideRow);
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player), null);
    }
    public static bool IsBoss(MonsterCatalog.Variety? variety) => variety?.Id == Boss;
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    public static StateMachineData? StartStates(MonsterCatalog.Variety variety) =>
        IsBoss(variety) ? variety.StateMachine?.Set("rock_mode", 0).Set("tide_rush", 0) : null;

    public static IReadOnlyList<GrantedSkill>? SkillsFor(Player player, MonsterCatalog.Variety variety, byte level, MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var catalog = Game.SkillsFor(player);
        return [.. Effects.Select(effect =>
        {
            var use = Attack(player, variety, level, effect);
            return catalog.FindByRow(Skill(effect), effect, level, Game.WithBehaviourVariation(player, effect, use.SkillStats()));
        }).OfType<GrantedSkill>()];
    }

    static MonsterAttack Attack(Player player, MonsterCatalog.Variety variety, byte level, int effect)
    {
        var catalog = Game.SkillsFor(player);
        var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
        var damage = DamagePacket.Of(DamageType.Physical, min, max);
        bool spell = catalog.IsSpell(effect);
        if (spell) damage = catalog.HitDamage(effect, level);
        double seconds = effect == MeleeEffect ? variety.AttackMs / 1000.0 : Math.Max(0.5, catalog.CastTimeMs(effect) / 1000.0);
        if (effect is 6668 or 6670)
        {
            // The slam's ACT clip is slowed by its own attack-speed stat (−50% / −60%).
            seconds = effect == 6670 ? 3.1 : 2.4;
            damage = damage.Scale(2).Select((t, r) => t == DamageType.Physical ? r.Scale(0.5) : r)
                .Add(DamageType.Cold, new DamageRange(min, max));
        }
        else if (effect == 4365) // BrineKingCharge
        {
            seconds = 1.2;
            damage = damage.Scale(1.4);
        }
        else if (effect == 6359) // BrineKingUpheaval
        {
            seconds = 1.4;
            damage = DamagePacket.Of(DamageType.Physical, min * 0.8, max * 0.8)
                .Add(DamageType.Cold, new DamageRange(min * 0.8, max * 0.8));
        }
        if (effect is 6354 or 6355 or 6361) damage = DamagePacket.Empty;
        return new(Skill(effect), seconds, effect == MeleeEffect ? variety.AttackRange : 45, damage)
        { Spell = spell, Scripted = true };
    }

    public static async Task Tick(Player player)
    {
        if (Tides.TryGetValue(player, out var tide) && tide.MaimActive && (player.PlayerLife == 0 || AreaTime(player) >= tide.MaimUntil))
        { tide.MaimActive = false; tide.MaimUntil = 0; RemoveTideBuff(player, MaimRow); await RefreshPlayerStats(player); }
        if (player.Layout.Id != Area || !player.Connection.PlayerSent || player.Closed || player.LeavingArea) return;
        (uint Id, SpawnedMonster Monster)? found;
        found = player.Area.Monsters.Where(p => IsBoss(p.Value.Variety)).Select(p => ((uint, SpawnedMonster)?)new(p.Key, p.Value)).FirstOrDefault();
        if (found is not { } && !Fights.TryGetValue(player.Instance, out _)) return;
        var fight = Fights.GetValue(player.Instance, _ => new(found!.Value.Id, found.Value.Monster, player));
        await fight.Gate.WaitAsync();
        try
        {
            await fight.Bind(player);
            if (IsMonsterRunner(player)) await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1);
        readonly uint id;
        readonly SpawnedMonster boss;
        readonly GameArea area;
        readonly List<Player> players = [];
        readonly HashSet<Player> bound = [];
        readonly HashSet<Player> waveViews = [];
        readonly List<(uint Id, double Ends)> effects = [];
        readonly List<(double At, (int X, int Y) Position, double Radius, MonsterAttack Attack, int? Generation)> hits = [];
        sealed class Surf(uint id, (int X, int Y) from, (int X, int Y) to, double started, double ends)
        {
            public readonly uint Id = id;
            public readonly (int X, int Y) From = from, To = to;
            public readonly double Started = started, Ends = ends;
            public readonly HashSet<Player> Hit = [];
            public (double X, double Y) Position(double now)
            { double t = Math.Clamp((now - Started) / (Ends - Started), 0, 1); return (From.X + (To.X - From.X) * t, From.Y + (To.Y - From.Y) * t); }
        }
        readonly List<Surf> surfs = [];
        readonly (int X, int Y) center;
        readonly List<GlyphQuest.Placement> spawners;
        readonly double radius, speed;
        double floodAt = double.NaN, nextSlam, nextCharge, nextUpheaval, nextRush, rushUntil, nextMinion, nextChase, nextStorm, nextOrb, nextWaveHit, expandUntil;
        double emergeUntil = double.NaN;
        int floodStep;
        uint waveId;
        byte waveState;
        ushort? immunity;
        bool cleaned;
        bool stopped;
        LifePhaseGate Phases => boss.LifePhases!;

        public Fight(uint objectId, SpawnedMonster monster, Player player)
        {
            (id, boss, area) = (objectId, monster, player.Area);
            var placed = CampaignBossHelpers.AreaPlacements(player);
            var middle = placed.Of("Metadata/MiscellaneousObjects/ArenaMiddle").FirstOrDefault();
            center = middle is null ? boss.Walk.Position(AreaTime(player)) : (middle.X, middle.Y);
            spawners = placed.Of("Metadata/Monsters/BrineKing/Minion/BrineKingMinionSpawner");
            radius = spawners.Count == 0 ? 67 : spawners.Average(p => Distance((p.X, p.Y), center));
            speed = boss.Walk.Speed;
            // Sync SavedMonster re-entry against persisted InstanceState so completed floods & emerge never replay.
            CampaignBossHelpers.SyncSavedPhases(player.Instance, boss, PhaseKey, PhaseThresholds);
            if (!boss.EntranceGrace)
            {
                emergeUntil = 0;
                boss.States = boss.States?.Set("rock_mode", 1);
            }
            lock (player.QuestObjects)
                waveId = player.QuestObjects.Where(p => p.Value.Rule.ObjectType == Wave).Select(p => p.Key).FirstOrDefault();
            waveState = (byte)(Phases.Active ? 3 + 4 * Phases.Completed
                : Phases.Completed == 0 ? 1
                : Phases.Completed == 1 ? 5
                : Phases.Completed == 2 ? 9 : 11);
            double now = AreaTime(player);
            nextSlam = now + 2; nextCharge = now + 6; nextUpheaval = now + 5; nextRush = now + 8; nextStorm = now + 1; nextOrb = now + 2;
            Console.WriteLine($"[gateway] Brine King controller: arena {center}, radius {radius:0.0}, {spawners.Count} minion markers, completed phases {Phases.Completed}");
        }

        public async Task Bind(Player player)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(player)) players.Add(player);
            // A joining client's first tick precedes SyncInstanceMonsters. Never update an Actor it has not
            // received yet, or one outside its wake zone; defer the rebind until that client can read it.
            if (!boss.Dead && boss.Life > 0 && Awake(player) && bound.Add(player))
            {
                var skills = SkillsFor(player, boss.Variety!, boss.Level, boss.Attack!)!;
                boss.ActionGeneration++; boss.SwingGeneration = null;
                if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { Skills = skills, StateMachine = boss.States } };
                await SendWorldRecord(player, id, InstanceEntities.PlayerSkillsChange(id, skills,
                    boss.Attack is { } old && !skills.Any(s => s.Id == old.SkillId) ? [old.SkillId] : []), "[Brine King] full actor skill list");
                if (Phases.Active && floodStep >= 2) await Basic(player, "ChangeToStance2", AreaTime(player), viewer: player);
            }
            if (waveId != 0 && waveViews.Add(player))
            {
                bool exists;
                lock (player.QuestObjects) exists = player.QuestObjects.ContainsKey(waveId);
                if (exists)
                {
                    if (waveState != 1)
                        await SendWorldRecord(player, waveId, InstanceEntities.TransitionableUpdate(waveId, 3, waveState), null);
                }
                else RegisterWaveObject(player);
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            if (boss.Dead || boss.Life == 0)
            {
                if (!cleaned)
                {
                    cleaned = true;
                    CampaignBossHelpers.SetInvulnerable(runner, id, boss, false);
                    if (waveId != 0) { await WaveState(runner, 12); effects.Add((waveId, now + 3)); waveId = 0; }
                    hits.Clear(); surfs.Clear();
                    foreach (var participant in players) await TideAt(participant, false, 0);
                }
                await Expire(now);
                return;
            }
            await Expire(now);
            var pos = boss.Walk.Position(now);
            // The runner owns the update loop, not the fight's target. A party can keep fighting while its
            // first entrant waits at the entrance or lies dead; only a client with the boss awake gets actions.
            var player = players.Where(p => !p.Closed && !p.LeavingArea && p.Connection.PlayerSent && p.PlayerLife > 0
                    && !p.Paused && !InGracePeriod(p) && Awake(p))
                .MinBy(p => Distance(pos, p.Walk.Position(now)));
            if (player is null) return;
            boss.Target = player;
            await LandHits(player, now);
            var playerAt = player.Walk.Position(now);
            if (!boss.Aggro)
            {
                if (Distance(pos, playerAt) > boss.AggroRange) return;
                boss.Aggro = true;
            }
            if (boss.EntranceGrace)
            {
                if (double.IsNaN(emergeUntil))
                {
                    // BrineKingBody/rig.amd: the native Emerge clip lasts 3333 ms.
                    emergeUntil = now + 3.333;
                    await Basic(player, "Emerge", now);
                }
                if (now < emergeUntil) return;
                await State(player, "rock_mode", 1);
                boss.EntranceGrace = false;
                CampaignBossHelpers.MarkStarted(runner.Instance, PhaseKey);
            }
            if (!boss.BossBarShown && boss.Variety is { BossHealthBar: true } && boss.States?.IndexOf("boss_life_bar") >= 0)
            {
                boss.BossBarShown = true; BossBars(player).Add(id);
                await ShowBossLifeBar(id, boss, player);
            }
            bool begin = Phases.Begin(boss.Life, boss.MaxLife);
            if (begin || Phases.Active && double.IsNaN(floodAt))
            {
                Phases.DamageTakenPercent = 100; rushUntil = 0;
                boss.Walk.SetSpeed(speed, now);
                boss.ActionGeneration++; boss.SwingGeneration = null;
                immunity = 1;
                CampaignBossHelpers.SetInvulnerable(player, id, boss, true);
                player.Area.Buffs.Set(id, new(350, id, U68: Buff.Seconds(16 + Distance(pos, center) / Math.Max(1, speed))));
                await State(player, "tide_rush", 0);
                double travel = Distance(pos, center) / Math.Max(1, speed);
                await Move(player, center, now);
                floodAt = now + travel;
                floodStep = 0;
                Console.WriteLine($"[gateway] Brine King: flood {Phases.Completed + 1} at {Phases.NextPercent}% life");
            }
            if (Phases.Active)
            {
                if (now >= floodAt && floodStep == 0)
                {
                    await Cast(player, 6361, center, now);
                    await WaveState(runner, (byte)(2 + 4 * Phases.Completed));
                    floodStep = 1;
                }
                if (now >= floodAt + 3 && floodStep == 1)
                {
                    await Basic(player, "ChangeToStance2", now);
                    await WaveState(runner, (byte)(3 + 4 * Phases.Completed));
                    floodStep = 2;
                }
                if (now >= floodAt + 13)
                {
                    if (Phases.Completed < 2)
                    {
                        await WaveState(runner, (byte)(4 + 4 * Phases.Completed));
                        expandUntil = now + 3;
                    }
                    if (immunity is not null) player.Area.Buffs.Clear(id, 350, id);
                    immunity = null;
                    CampaignBossHelpers.SetInvulnerable(player, id, boss, false);
                    await Basic(player, "ChangeToStance1", now);
                    CampaignBossHelpers.MarkPhaseCompleted(runner.Instance, PhaseKey, Phases.Completed);
                    Phases.Finish();
                    floodAt = double.NaN;
                    nextSlam = now + 1; nextRush = now + 8; nextCharge = now + 5; nextUpheaval = now + 4;
                    Console.WriteLine($"[gateway] Brine King: flood {Phases.Completed} finished, damage unlocked");
                }
                await Hazards(player, now);
                return;
            }
            if (expandUntil > 0 && now >= expandUntil)
            {
                await WaveState(runner, (byte)(Phases.Completed == 1 ? 5 : 9));
                expandUntil = 0;
            }
            await Hazards(player, now);
            if (rushUntil > 0 && now >= rushUntil)
            {
                rushUntil = 0; Phases.DamageTakenPercent = 100;
                boss.Walk.SetSpeed(speed, now);
                await State(player, "tide_rush", 0);
                await Cast(player, 6355, pos, now);
                return;
            }
            if (now < boss.BusyUntil || now < boss.FlinchUntil) return;
            if (rushUntil > now)
            {
                if (now >= nextMinion) { nextMinion = now + 1; await Minion(player); }
            }
            else if (Phases.Completed < 3 && now >= nextRush)
            {
                nextRush = now + 15; rushUntil = now + 5; Phases.DamageTakenPercent = 20;
                boss.Walk.SetSpeed(speed * 0.6, now);
                await State(player, "tide_rush", 1);
                await Cast(player, 6354, pos, now);
                return;
            }
            double distance = Distance(pos, playerAt);
            if (rushUntil <= now && now >= nextCharge && distance is >= 18 and <= 65)
            {
                nextCharge = now + 10;
                var dest = player.Layout.Near(playerAt);
                if (await Cast(player, 4365, dest, now) is { } generation)
                {
                    var attack = Attack(player, boss.Variety!, boss.Level, 4365);
                    boss.Walk.SetSpeed(speed * 2.2, now);
                    boss.Walk.Order(dest.X, dest.Y, now);
                    hits.Add((now + attack.Seconds * 0.55, dest, 12, attack, generation));
                    return;
                }
            }
            if (rushUntil <= now && now >= nextUpheaval && distance <= 55)
            {
                nextUpheaval = now + 8;
                if (await Cast(player, 6359, playerAt, now) is { } generation)
                {
                    var attack = Attack(player, boss.Variety!, boss.Level, 6359);
                    double facing = Math.Atan2(playerAt.Y - pos.Y, playerAt.X - pos.X);
                    for (int step = 1; step <= 4; step++)
                    {
                        var burst = player.Layout.Near((
                            (int)Math.Round(pos.X + Math.Cos(facing) * step * 11),
                            (int)Math.Round(pos.Y + Math.Sin(facing) * step * 11)));
                        await Effect(player, burst, Art + "slam_cone/coral1.ao", now + 0.5 + step * 0.25);
                        hits.Add((now + 0.45 + step * 0.2, burst, 9, attack, generation));
                    }
                    return;
                }
            }
            if (rushUntil <= now && now >= nextSlam && distance <= 48)
            {
                nextSlam = now + 5;
                int effect = Phases.Completed >= 2 && AreaRng(player).Next(2) == 0 ? (AreaRng(player).Next(2) == 0 ? 6759 : 6761) : 6668;
                if (await Cast(player, effect, playerAt, now) is not { } generation) return;
                var attack = Attack(player, boss.Variety!, boss.Level, effect);
                hits.Add((now + attack.Seconds * 0.65, playerAt, effect == 6668 ? 12 : 18, attack, generation));
                return;
            }
            if (distance <= boss.Variety!.AttackRange + 1)
            {
                if (await Cast(player, MeleeEffect, playerAt, now) is not { } generation) return;
                var attack = Attack(player, boss.Variety!, boss.Level, MeleeEffect);
                hits.Add((now + attack.Seconds * 0.5, playerAt, boss.Variety.AttackRange + 2, attack, generation));
            }
            else if (now >= nextChase)
            {
                nextChase = now + 0.75;
                boss.Walk.SetSpeed(speed, now);
                double fraction = Math.Clamp((distance - boss.Variety.AttackRange + 1) / distance, 0, 1);
                var target = player.Layout.Near(((int)Math.Round(pos.X + (playerAt.X - pos.X) * fraction), (int)Math.Round(pos.Y + (playerAt.Y - pos.Y) * fraction)));
                await Move(player, target, now);
            }
        }

        async Task<int?> Cast(Player player, int effect, (int X, int Y) target, double now)
        {
            var attack = Attack(player, boss.Variety!, boss.Level, effect);
            // Reset any prior BrineKingCharge speed boost before starting the next action.
            boss.Walk.SetSpeed(rushUntil > now ? speed * 0.6 : speed, now);
            var p = boss.Walk.Position(now); boss.Walk.Order(p.X, p.Y, now);
            int generation = ++boss.ActionGeneration;
            boss.BusyUntil = now + attack.Seconds + 0.2;
            bool sent = await SendMonsterAction(player, id, boss, generation, () =>
            {
                var p = boss.Walk.Position(AreaTime(player));
                return InstanceProtocol.ActionStarted(id, p.X, p.Y, target.X, target.Y, Skill(effect), player.NextActionSequence(), 0x0408);
            }, $"[Brine King] skill {effect}, phase {Phases.Completed}");
            return sent ? generation : null;
        }

        async Task Move(Player player, (int X, int Y) target, double now)
        {
            int generation = ++boss.ActionGeneration;
            await SendMonsterAction(player, id, boss, generation, () =>
            {
                var here = boss.Walk.Position(AreaTime(player));
                boss.Walk.Order(target.X, target.Y, AreaTime(player));
                return InstanceProtocol.ActionStarted(id, here.X, here.Y, target.X, target.Y, 0x2909, player.NextActionSequence(), 0x0408);
            }, "[Brine King] move");
        }

        async Task Basic(Player player, string action, double now, Player? viewer = null)
        {
            if (Game.SkillsFor(player).ActionSkillId(action) is not { } skill) return;
            var at = boss.Walk.Position(now);
            var record = InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, skill, player.NextActionSequence(), 0x0800, id);
            if (viewer is null) await All(id, record, monster: true);
            else await SendWorldRecord(viewer, id, record, "[Brine King] restore defensive stance");
        }

        async Task State(Player player, string name, int value)
        {
            if (boss.States is not { } states) return;
            boss.States = states.Set(name, value);
            if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = boss.States } };
            int component = boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (component >= 0) await All(id, InstanceEntities.StateMachineUpdate(id, (byte)component, boss.States), monster: true);
        }

        void RegisterWaveObject(Player player)
        {
            if (QuestObjectTypes.Find(Wave) is { } info)
                lock (player.QuestObjects)
                    player.QuestObjects[waveId] = (new Quests.ObjectRule(player.Layout.Id, Wave, null), new(Wave, "", center.X, center.Y, 0, null, 0), info);
        }

        async Task WaveState(Player runner, byte state)
        {
            waveState = state;
            if (waveId == 0)
            {
                waveId = runner.TakeObjectId();
                // SendWorldCreate broadcasts once to every client currently in the instance.
                await SendWorldCreate(runner, waveId, () => InstanceEntities.CreateObject(waveId, InstanceEntities.ObjectTypeHash(Wave),
                    new QuestObject(center.X, center.Y, ["Positioned", "BaseEvents", "Animated", "Transitionable"], TransitionState: waveState).Blob()),
                    $"[Brine King] tidal wall {waveState} at {center}", wake: true, awaitBirth: false);
                foreach (var peer in players)
                {
                    RegisterWaveObject(peer);
                    waveViews.Add(peer);
                }
            }
            else await All(waveId, InstanceEntities.TransitionableUpdate(waveId, 3, state));
        }

        async Task Hazards(Player player, double now)
        {
            int stage = Phases.Completed + (Phases.Active && now >= floodAt ? 1 : 0);
            if (stage == 0) return;
            // The first storm includes water bolts; the second flood has neither; the last keeps lightning.
            if ((stage == 3 || stage == 1 && Phases.Active) && now >= nextStorm)
            {
                nextStorm = now + 3;
                for (int i = 0; i < 5; i++)
                {
                    double angle = AreaRng(player).NextDouble() * Math.Tau;
                    double reach = radius * 0.45 * Math.Sqrt(AreaRng(player).NextDouble());
                    var at = i == 0 ? player.Walk.Position(now)
                        : ((int)Math.Round(center.X + Math.Cos(angle) * reach), (int)Math.Round(center.Y + Math.Sin(angle) * reach));
                    await Effect(player, at, Art + "lightning_strike/buildup.ao", now + 0.9);
                    hits.Add((now + 0.9, at, 5, Attack(player, boss.Variety!, boss.Level, 6360), null));
                }
            }
            if (stage == 1 && now >= nextOrb)
            {
                nextOrb = now + 2.4;
                var target = player.Walk.Position(now);
                double angle = AreaRng(player).NextDouble() * Math.Tau;
                var from = ((int)Math.Round(center.X + Math.Cos(angle) * radius * 0.75), (int)Math.Round(center.Y + Math.Sin(angle) * radius * 0.75));
                double distance = Math.Max(1, Distance(from, target));
                var to = ((int)Math.Round(from.Item1 + (target.X - from.Item1) / distance * radius * 1.5),
                    (int)Math.Round(from.Item2 + (target.Y - from.Item2) / distance * radius * 1.5));
                double seconds = Distance(from, to) / 24;
                uint orb = await Effect(player, from, Art + "surf_cascade/surf_cascade.ao", now + seconds);
                await All(orb, InstanceProtocol.MoveTo(orb, to.Item1, to.Item2, (float)seconds, InstanceProtocol.FollowTerrain));
                surfs.Add(new(orb, from, to, now, now + seconds));
            }
            foreach (var surf in surfs.ToArray())
            {
                if (now >= surf.Ends) { surfs.Remove(surf); continue; }
                var p = surf.Position(now);
                var before = surf.Position(now - 0.5);
                foreach (var participant in players.Where(pl => !pl.Closed && !pl.LeavingArea && pl.PlayerLife > 0 && !surf.Hit.Contains(pl)))
                {
                    var at = participant.Walk.Position(now);
                    double dx = p.X - before.X, dy = p.Y - before.Y;
                    double t = Math.Clamp(((at.X - before.X) * dx + (at.Y - before.Y) * dy) / Math.Max(1e-9, dx * dx + dy * dy), 0, 1);
                    if (Math.Pow(at.X - before.X - t * dx, 2) + Math.Pow(at.Y - before.Y - t * dy, 2) > 16) continue;
                    surf.Hit.Add(participant);
                    var damage = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (50, 70);
                    var surfAtk = new MonsterAttack(Skill(6359), 1, 4,
                        DamagePacket.Of(DamageType.Cold, damage.Item1, damage.Item2)) { Spell = true, Scripted = true };
                    await HitArea(participant, ((int)p.X, (int)p.Y), 4, surfAtk);
                }
            }
            double open = Phases.Active ? 0.25 : stage == 1 ? 0.75 : stage == 2 ? 0.50 : 0.25;
            if (Phases.Active && now < floodAt + 3)
            {
                double previous = Phases.Completed == 0 ? 1 : Phases.Completed == 1 ? 0.75 : 0.50;
                open = previous + (0.25 - previous) * Math.Clamp((now - floodAt) / 3, 0, 1);
            }
            else if (expandUntil > now) open = 0.25 + (open - 0.25) * (1 - (expandUntil - now) / 3);
            if (now >= nextWaveHit)
            {
                nextWaveHit = now + 0.5;
                foreach (var peer in players.Where(p => !p.Closed && !p.LeavingArea && p.PlayerLife > 0))
                {
                    var at = peer.Walk.Position(now);
                    var damage = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (100, 150);
                    await TideAt(peer, Distance(at, center) > radius * open, (damage.Item1 + damage.Item2) * 0.75);
                }
            }
        }

        async Task LandHits(Player player, double now)
        {
            foreach (var hit in hits.Where(h => h.At <= now).ToArray())
            {
                hits.Remove(hit);
                if (hit.Generation is int generation && (generation != boss.ActionGeneration || MonsterActionFactor(boss, now) == 0)) continue;
                string art = hit.Attack.Damage[DamageType.Lightning].Max > 0 ? "lightning_strike/lightning_strike.ao" : "surf_cascade/surf_cascade.ao";
                if (hit.Attack.Spell) await Effect(player, hit.Position, Art + art, now + 1.5);
                foreach (var peer in players.Where(p => !p.Closed && !p.LeavingArea).ToArray())
                    await HitArea(peer, hit.Position, hit.Radius, hit.Attack);
            }
        }

        async Task HitArea(Player player, (int X, int Y) centerPos, double hitRadius, MonsterAttack attack)
        {
            Game.MonsterHitOutcome? outcome = null;
            await SendWorldRecord(player, id, [], null, atGate: () =>
            {
                if (!CurrentGameWork || stopped || player.Closed || player.LeavingArea || player.Paused || InGracePeriod(player) || boss.Dead) return null;
                double now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
                bool Inside((int X, int Y) at) =>
                    Distance(at, centerPos) <= hitRadius
                    && (attack.SkillId != Skill(MeleeEffect) || Distance(at, boss.Walk.Position(now)) <= attack.Range + 2);
                var minionAttack = attack with { Damage = attack.Damage.Scale(0.6) };
                foreach (var g in player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values).ToArray())
                    if (!g.Monster.Dead && Inside(g.Monster.Walk.Position(now)))
                        GolemTakeHitLocked(player, g, id, boss, minionAttack, now, area: true);
                if (player.PlayerLife == 0 || !Inside(player.Walk.Position(now))) return null;
                outcome = CommitMonsterHitLocked(player, id, boss, attack, now, player.Walk.Position(now), centerPos, false, player.NextActionSequence());
                return outcome?.HitPacket;
            });
            if (outcome is not null)
            {
                await PresentMonsterHit(player, id, outcome);
                if (outcome.Damage > 0 && attack.SkillId is var skill && (skill == Skill(6759) || skill == Skill(6761))) await Maim(player);
            }
        }

        async Task Minion(Player player)
        {
            var variety = Monsters.Find("Metadata/Monsters/BrineKing/BrineKingMinion_");
            if (variety is not { Supported: true }) return;
            if (player.Area.Monsters.Values.Count(m => m.SummonerId == id && !m.Dead && m.Life > 0) >= 20) return;
            var source = spawners.Count == 0 ? null : spawners[AreaRng(player).Next(spawners.Count)];
            var at = player.Layout.Near(source is null ? center : (source.X, source.Y));
            await SpawnMonster(player, variety, boss.Level, MonsterRarity.Normal, at.X, at.Y,
                instanceOwned: true,
                owned: (_, minion) => { minion.SummonerId = id; minion.NoRewards = true; minion.Aggro = true; },
                ownedCurrent: () => !stopped && !boss.Dead);
        }

        async Task<uint> Effect(Player player, (int X, int Y) at, string ao, double expires)
        {
            uint effect = player.TakeObjectId();
            var obj = new EffectObject(at.X, at.Y, ao);
            await All(effect, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()), create: true);
            await All(effect, InstanceProtocol.EnterWakeZone(effect));
            effects.Add((effect, expires));
            return effect;
        }
        async Task Expire(double now)
        {
            foreach (var effect in effects.Where(e => e.Ends <= now).ToArray())
            {
                effects.Remove(effect);
                foreach (var player in players) lock (player.QuestObjects) player.QuestObjects.Remove(effect.Id);
                await All(effect.Id, [], remove: true);
            }
        }
        bool Awake(Player player)
        {
            return MonsterShown(player, id) && !MonsterAsleep(player, id);
        }
        async Task All(uint target, byte[] bytes, bool monster = false, bool create = false, bool remove = false)
        {
            if (stopped) return;
            if (players.FirstOrDefault(p => !p.Closed && !p.LeavingArea && (!monster || Awake(p))) is { } player)
            {
                // Send encrypts in place: every recipient needs its own plaintext, including wave/effect broadcasts.
                bool Current() => !stopped && !player.Closed && !player.LeavingArea && (!monster || Awake(player));
                if (create) await SendWorldCreate(player, target, [.. bytes], still: Current);
                else if (remove) await SendWorldRemoval(player, target, still: Current);
                else await SendWorldRecord(player, target, [.. bytes], null, still: Current);
            }
        }
        public void Stop()
        {
            stopped = true;
            Phases.DamageTakenPercent = 100;
            CampaignBossHelpers.ClearInvulnerableOnUnload(area, id, boss, 350);
            if (players.FirstOrDefault() is { } player)
                boss.Walk.SetSpeed(speed, AreaTime(player));
        }
        static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }
}
