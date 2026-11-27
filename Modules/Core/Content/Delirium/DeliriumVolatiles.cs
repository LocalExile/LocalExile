using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Delirium;

/// <summary>
/// Delirium Mirror ground volatile pods (<c>DoodadDaemonBloodBagVolatile1..3</c>), pack spawner pods
/// (<c>DoodadDaemonEggFodderSpawner1..2</c>, <c>DoodadDaemonGlobSpawnPack</c>), and chasing
/// <c>AfflictionVolatile</c> cores (<c>AfflictionVolatileSuicideExplosion</c>).
/// </summary>
public static class DeliriumVolatiles
{
    public const string VolatilePath = "Metadata/Monsters/LeagueAffliction/Volatile/AfflictionVolatile";
    public static readonly string[] BloodBagPodPaths =
    [
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonBloodBagVolatile1",
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonBloodBagVolatile2",
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonBloodBagVolatile3",
    ];
    public static readonly string[] PackPodPaths =
    [
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonEggFodderSpawner1",
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonEggFodderSpawner2",
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonGlobSpawnPack",
    ];
    public const string SpawnNovaEffectId = "AfflictionMinionSpawnNova";
    public const string ExplosionEffectId = "AfflictionVolatileSuicideExplosion";

    public const double PodTriggerRadius = 14.0;
    public const double VolatileContactRadius = 7.0;
    public const double VolatileFuseSeconds = 6.0;
    public const double VolatileArmSeconds = 0.25;
    public const double PodSpawnIntervalSeconds = 3.5;
    public const double PodRemoveDelaySeconds = 0.9;
    public const int MaxActivePods = 12;
    public const int MaxTotalPods = 48;
    public const int MaxActiveVolatiles = 10;

    static readonly Dictionary<uint, int> InvulnerableStats = new() { [Stat.BaseCannotBeDamaged] = 1 };

    public sealed class PodEntry(uint id, int x, int y, string path, int coreCount, bool isPack, ushort? novaSkillId)
    {
        public uint Id { get; } = id;
        public int X { get; } = x;
        public int Y { get; } = y;
        public string Path { get; } = path;
        public int CoreCount { get; } = coreCount;
        public bool IsPack { get; } = isPack;
        public ushort? NovaSkillId { get; } = novaSkillId;
        public bool Popped { get; set; }
        public double RemoveAt { get; set; }
    }

    public sealed class CoreEntry(uint id, double speed, double armAt, double fuseAt, MonsterAttack attack, double radius)
    {
        public uint Id { get; } = id;
        public double Speed { get; } = speed;
        public double ArmAt { get; set; } = armAt;
        public double FuseAt { get; set; } = fuseAt;
        public MonsterAttack Attack { get; } = attack;
        public double Radius { get; } = radius;
        public double NextMoveAt { get; set; }
    }

    public sealed class VolatileState
    {
        public List<PodEntry> Pods { get; } = [];
        public List<CoreEntry> Cores { get; } = [];
        public double NextPodSpawnAt { get; set; }
        public int TotalPodsSpawned { get; set; }
        public bool Spawning { get; set; }
    }

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> AliveClients(Player player) =>
        InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance)
            && Current(c) && c.PlayerLife > 0 && !InGracePeriod(c));

    static StateMachineData? PodStates(StateMachineData? states)
    {
        if (states is null) return null;
        var next = states;
        if (next.IndexOf("visible") >= 0) next = next.Set("visible", 1);
        if (next.IndexOf("blocking") >= 0) next = next.Set("blocking", 0);
        return next;
    }

    static (uint Key, int Value)[] InvulnerableClientStats()
    {
        var list = new List<(uint, int)>(2) { (Stat.HideMiniLifeBar, 1) };
        if (StatCatalog.IsClientId(Stat.BaseCannotBeDamaged)) list.Add((Stat.BaseCannotBeDamaged, 1));
        return [.. list];
    }

    static int CoreCountForPod(string path) => path switch
    {
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonBloodBagVolatile1" => 1,
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonBloodBagVolatile2" => 2,
        "Metadata/Monsters/LeagueAffliction/DoodadDaemons/DoodadDaemonBloodBagVolatile3" => 2,
        _ => 0,
    };

    static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    static (int X, int Y) Toward(AreaLayout layout, (int X, int Y) from, (int X, int Y) to, double stepDist)
    {
        double d = Distance(from, to);
        var raw = d <= stepDist
            ? to
            : ((int)Math.Round(from.X + (to.X - from.X) * stepDist / d),
               (int)Math.Round(from.Y + (to.Y - from.Y) * stepDist / d));
        return layout.WalkableFor(raw.Item1, raw.Item2, 0) ? raw : layout.Near(raw);
    }

    /// <summary>Builds the level-scaled <c>AfflictionVolatileSuicideExplosion</c> attack (50% physical converted to fire).</summary>
    public static (MonsterAttack Attack, double Radius) BuildVolatileAttack(Player player, MonsterCatalog.Variety variety,
        byte level, MonsterModEffects? modEffects = null)
    {
        var catalog = SkillsFor(player);
        int effect = catalog.EffectRow(ExplosionEffectId);
        var effects = modEffects ?? MonsterModEffects.None;
        if (effect >= 0)
        {
            var baseAttack = BossKits.AttackFor(player, variety, level, effect, SkillCatalog.FirstSkillId) with
            {
                Effect = effect,
                Spell = true,
                Secondary = true,
                AreaAtSelf = true,
            };
            var stats = GolemData.Skill(catalog, effect, level).Stats;
            double fireShare = Math.Clamp(stats.GetValueOrDefault(Stat.SkillPhysicalDamagePctToConvertToFire), 0, 100) / 100.0;
            var dmg = baseAttack.Damage;
            if (fireShare > 0 && !dmg[DamageType.Physical].IsZero)
            {
                var phys = dmg[DamageType.Physical];
                var converted = DamagePacket.Empty;
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    if (type == DamageType.Physical)
                        converted = converted.Add(DamageType.Physical, new(phys.Min * (1 - fireShare), phys.Max * (1 - fireShare)));
                    else if (type == DamageType.Fire)
                        converted = converted.Add(DamageType.Fire, dmg[DamageType.Fire].Add(phys.Scale(fireShare)));
                    else if (!dmg[type].IsZero)
                        converted = converted.Add(type, dmg[type]);
                }
                dmg = converted;
            }
            dmg = effects.Hit(dmg, level);
            double radius = BossKits.AreaRadius(catalog, effect, level) is > 0 and var r ? r : 10.0;
            return (baseAttack with { Damage = dmg, AreaRadius = radius }, radius);
        }

        var (min, max) = Monsters.BaseDamage(variety, level) ?? (20, 30);
        var fallback = effects.Hit(DamagePacket.Of(DamageType.Physical, min * 0.5, max * 0.5)
            .Add(DamageType.Fire, new(min * 0.5, max * 0.5)), level);
        return (new MonsterAttack(SkillCatalog.FirstSkillId, 1.0, 10.0, fallback)
        {
            Spell = true,
            Secondary = true,
            AreaAtSelf = true,
            AreaRadius = 10.0,
        }, 10.0);
    }

    /// <summary>Spawns a Delirium volatile or pack pod at <paramref name="x"/>, <paramref name="y"/> with <c>visible = 1</c>.</summary>
    public static async Task<uint> SpawnPodAt(Player player, DeliriumMirrorEncounter.MirrorState state, string podPath,
        int x, int y, byte level, double now)
    {
        if (!Current(player) || Monsters.Find(podPath) is not { Supported: true } rawVariety) return 0;
        var variety = rawVariety with { StateMachine = PodStates(rawVariety.StateMachine) };
        var catalog = SkillsFor(player);
        int novaEffect = catalog.EffectRow(SpawnNovaEffectId);
        const ushort novaSkillId = 0x4000;
        GrantedSkill[]? ownedSkills = null;
        ushort? novaId = null;
        if (novaEffect >= 0 && catalog.FindByRow(novaSkillId, novaEffect, level,
            [(Stat.SkillBehaviourVariation, catalog.BehaviourVariation(novaEffect))]) is { } novaSkill)
        {
            ownedSkills = [novaSkill];
            novaId = novaSkillId;
        }
        int coreCount = CoreCountForPod(podPath);
        bool isPack = coreCount == 0;
        uint spawnedId = await SpawnMonster(player, variety, level, MonsterRarity.Normal, x, y,
            unrisen: false, scripted: true, nonBlocking: true, log: false,
            extraStats: InvulnerableClientStats(), ownedSkills: ownedSkills,
            placement: MonsterPlacement.Established,
            shape: o => o with { Blocking = false },
            ownedCurrent: () => Current(player) && state.Stage == DeliriumMirrorEncounter.MirrorStage.Active,
            owned: (id, m) =>
            {
                m.NoRewards = true;
                m.Attack = null;
                m.Aggro = false;
                m.EntranceGrace = true;
                m.EncounterStats = InvulnerableStats;
                lock (state)
                {
                    if (state.Stage != DeliriumMirrorEncounter.MirrorStage.Active) return;
                    state.Volatiles.Pods.Add(new(id, x, y, podPath, coreCount, isPack, novaId));
                    state.Volatiles.TotalPodsSpawned++;
                }
            });
        return spawnedId;
    }

    /// <summary>Spawns a chasing <c>AfflictionVolatile</c> core at <paramref name="x"/>, <paramref name="y"/>.</summary>
    public static async Task<uint> SpawnVolatileCoreAt(Player player, DeliriumMirrorEncounter.MirrorState state,
        int x, int y, byte level, double now)
    {
        if (!Current(player) || Monsters.Find(VolatilePath) is not { Supported: true } rawVariety) return 0;
        var states = rawVariety.StateMachine;
        if (states is not null && states.IndexOf("invisible") >= 0) states = states.Set("invisible", 0);
        var variety = rawVariety with { StateMachine = states, AggroRange = Math.Max(135, rawVariety.AggroRange) };
        double speed = variety.MovementSpeed > 0 ? variety.MovementSpeed : 24.0;
        var (attack, blastRadius) = BuildVolatileAttack(player, variety, level);
        uint spawnedId = await SpawnMonster(player, variety, level, MonsterRarity.Normal, x, y,
            unrisen: false, scripted: true, nonBlocking: true, log: false,
            extraStats: InvulnerableClientStats(),
            placement: MonsterPlacement.Established,
            shape: o => o with { Blocking = false },
            ownedCurrent: () => Current(player) && state.Stage == DeliriumMirrorEncounter.MirrorStage.Active,
            owned: (id, m) =>
            {
                m.NoRewards = true;
                m.Attack = null;
                m.Aggro = true;
                m.EntranceGrace = true;
                m.EncounterStats = InvulnerableStats;
                m.Walk.SetSpeed(speed, now);
                lock (state)
                {
                    if (state.Stage != DeliriumMirrorEncounter.MirrorStage.Active) return;
                    state.Volatiles.Cores.Add(new(id, speed, now + VolatileArmSeconds, now + VolatileFuseSeconds, attack, blastRadius));
                }
            });
        return spawnedId;
    }

    /// <summary>Advances pod spawning, proximity pod triggers, volatile core chasing, and detonations.</summary>
    public static async Task Tick(Player player, DeliriumMirrorEncounter.MirrorState state, double now, float step,
        bool activeLifetime, Func<int, int, Task>? spawnPackAt = null)
    {
        if (!activeLifetime || !Current(player)) return;
        var alive = AliveClients(player).Select(p => (Player: p, Pos: p.Walk.Position(now))).ToArray();
        if (alive.Length == 0 && state.Volatiles.Cores.Count == 0 && state.Volatiles.Pods.Count == 0) return;

        bool fogPaused;
        byte level;
        bool shouldSpawnPod = false;
        PodEntry[] podsSnapshot;
        CoreEntry[] coresSnapshot;
        lock (state)
        {
            if (state.Stage != DeliriumMirrorEncounter.MirrorStage.Active) return;
            fogPaused = state.FogPaused;
            level = state.Level > 0 ? state.Level : (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 68, 84);
            var vs = state.Volatiles;
            int activePods = vs.Pods.Count(p => !p.Popped);
            if (!fogPaused && !vs.Spawning && now >= vs.NextPodSpawnAt && activePods < MaxActivePods && vs.TotalPodsSpawned < MaxTotalPods)
            {
                bool anyInFog = alive.Any(a => state.IsCellInFog(a.Pos.X, a.Pos.Y));
                if (anyInFog)
                {
                    vs.NextPodSpawnAt = now + PodSpawnIntervalSeconds;
                    vs.Spawning = true;
                    shouldSpawnPod = true;
                }
            }
            podsSnapshot = [.. vs.Pods];
            coresSnapshot = [.. vs.Cores];
        }

        if (shouldSpawnPod)
        {
            try
            {
                var rng = AreaRng(player);
                var inFogPlayers = alive.Where(a => state.IsCellInFog(a.Pos.X, a.Pos.Y)).ToArray();
                if (inFogPlayers.Length > 0)
                {
                    var anchor = inFogPlayers[rng.Next(inFogPlayers.Length)].Pos;
                    double angle = rng.NextDouble() * Math.Tau;
                    int radius = 22 + rng.Next(20);
                    var at = player.Layout.Near((anchor.X + (int)Math.Round(Math.Cos(angle) * radius),
                        anchor.Y + (int)Math.Round(Math.Sin(angle) * radius)));
                    if (state.IsCellInFog(at.X, at.Y))
                    {
                        string podPath = rng.Next(100) < 70
                            ? BloodBagPodPaths[rng.Next(BloodBagPodPaths.Length)]
                            : PackPodPaths[rng.Next(PackPodPaths.Length)];
                        await SpawnPodAt(player, state, podPath, at.X, at.Y, level, now);
                    }
                }
            }
            finally
            {
                lock (state) state.Volatiles.Spawning = false;
            }
        }

        // 1. Check pod proximity triggers & popped pod removals.
        foreach (var pod in podsSnapshot)
        {
            if (!pod.Popped)
            {
                if (!player.Area.Monsters.TryGetValue(pod.Id, out var pm) || pm.Dead || pm.Life == 0)
                {
                    lock (state) state.Volatiles.Pods.Remove(pod);
                    continue;
                }
                if (fogPaused) continue;
                bool triggered = alive.Any(a => Distance(a.Pos, (pod.X, pod.Y)) <= PodTriggerRadius);
                if (triggered)
                    await TriggerPod(player, state, pod, pm, level, now, spawnPackAt);
            }
            else if (now >= pod.RemoveAt)
            {
                RemoveEntity(player, pod.Id);
                await SendWorldRemoval(player, pod.Id, $"0x02bd Delirium pod 0x{pod.Id:x} popped");
                lock (state) state.Volatiles.Pods.Remove(pod);
            }
        }

        // 2. Drive chasing AfflictionVolatile cores & detonate on contact or fuse timeout.
        foreach (var core in coresSnapshot)
        {
            if (!player.Area.Monsters.TryGetValue(core.Id, out var cm) || cm.Dead || cm.Life == 0)
            {
                lock (state) state.Volatiles.Cores.Remove(core);
                continue;
            }
            if (fogPaused)
            {
                core.ArmAt += step;
                core.FuseAt += step;
                continue;
            }
            var from = cm.Walk.Position(now);
            Player? target = null;
            (int X, int Y) targetPos = default;
            double bestDist = double.MaxValue;
            double maxRange = Math.Max(135, cm.AggroRange);
            foreach (var (p, pos) in alive)
            {
                double d = Distance(from, pos);
                if (d <= maxRange && d < bestDist)
                {
                    bestDist = d;
                    target = p;
                    targetPos = pos;
                }
            }

            if ((now >= core.ArmAt && target is not null && bestDist <= VolatileContactRadius) || now >= core.FuseAt)
            {
                await DetonateCore(player, state, core, cm, from, now);
                continue;
            }

            if (target is not null && now >= core.NextMoveAt)
            {
                core.NextMoveAt = now + 0.2;
                var to = Toward(player.Layout, from, targetPos, core.Speed * 0.5);
                cm.Walk.SetSpeed(core.Speed, now);
                cm.Walk.Order(to.X, to.Y, now);
                await SendWorldRecord(player, core.Id,
                    InstanceProtocol.MoveTo(core.Id, to.X, to.Y, (float)core.Speed, InstanceProtocol.FollowTerrain),
                    null);
            }
        }
    }

    static async Task TriggerPod(Player player, DeliriumMirrorEncounter.MirrorState state, PodEntry pod,
        SpawnedMonster pm, byte level, double now, Func<int, int, Task>? spawnPackAt)
    {
        pod.Popped = true;
        pod.RemoveAt = now + PodRemoveDelaySeconds;
        pm.Dead = true;
        pm.Life = 0;
        pm.NoRewards = true;
        pm.ActionGeneration++;

        if (pod.NovaSkillId is ushort novaSkill)
            await SendWorldRecord(player, pod.Id,
                InstanceProtocol.ActionStarted(pod.Id, pod.X, pod.Y, pod.X, pod.Y, novaSkill, pm.NextSequence(), player.MonsterOrderFlags),
                $"0x01ab Delirium pod 0x{pod.Id:x} AfflictionMinionSpawnNova");

        if (pm.States is { } states && states.IndexOf("visible") >= 0
            && pm.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") is int smIdx and >= 0)
        {
            var hidden = states.Set("visible", 0);
            pm.States = hidden;
            await SendWorldRecord(player, pod.Id, InstanceEntities.StateMachineUpdate(pod.Id, (byte)smIdx, hidden), null);
        }

        if (pod.IsPack)
        {
            if (spawnPackAt is not null) await spawnPackAt(pod.X, pod.Y);
            return;
        }

        int spawnCount;
        lock (state)
            spawnCount = Math.Min(pod.CoreCount, Math.Max(0, MaxActiveVolatiles - state.Volatiles.Cores.Count));
        for (int i = 0; i < spawnCount; i++)
        {
            int ox = spawnCount == 1 ? 0 : (i == 0 ? -2 : 2);
            (int X, int Y) raw = (pod.X + ox, pod.Y);
            var spot = player.Layout.WalkableFor(raw.X, raw.Y, 0) ? raw : player.Layout.Near(raw);
            await SpawnVolatileCoreAt(player, state, spot.X, spot.Y, level, now);
        }
    }

    static async Task DetonateCore(Player player, DeliriumMirrorEncounter.MirrorState state, CoreEntry core,
        SpawnedMonster cm, (int X, int Y) at, double now)
    {
        cm.Life = 0;
        cm.Dead = true;
        cm.NoRewards = true;
        cm.Aggro = false;
        cm.ActionGeneration++;
        cm.Walk.Teleport(at.X, at.Y, now);

        await SendWorldRecord(player, core.Id,
            [.. InstanceProtocol.ActionEnded(core.Id),
             .. InstanceEntities.StatsUpdate(core.Id, [(Stat.HideMiniLifeBar, 1)]),
             .. InstanceProtocol.LifeUpdate(core.Id, 0)],
            $"0x01b1 AfflictionVolatile 0x{core.Id:x} detonates");

        var instance = player.Instance;
        foreach (var member in AliveClients(player).ToArray())
        {
            await MonsterDetachedAreaHit(member, core.Id, cm, core.Attack, at, core.Radius, cm.NextSequence(),
                () => CurrentGameWork && ReferenceEquals(member.Instance, instance) && !member.Closed && !member.LeavingArea);
        }

        RemoveEntity(player, core.Id);
        await SendWorldRemoval(player, core.Id, $"0x02bd AfflictionVolatile 0x{core.Id:x} removed");
        lock (state) state.Volatiles.Cores.Remove(core);
    }

    /// <summary>Removes all remaining pods and volatile cores when the Delirium Mirror encounter ends.</summary>
    public static async Task Clear(Player player, DeliriumMirrorEncounter.MirrorState state)
    {
        uint[] ids;
        lock (state)
        {
            ids = [.. state.Volatiles.Pods.Select(p => p.Id), .. state.Volatiles.Cores.Select(c => c.Id)];
            state.Volatiles.Pods.Clear();
            state.Volatiles.Cores.Clear();
        }
        foreach (uint id in ids)
        {
            RemoveEntity(player, id);
            await SendWorldRemoval(player, id, $"0x02bd Delirium volatile/pod 0x{id:x} cleared");
        }
    }

    static void RemoveEntity(Player player, uint id)
    {
        if (player.Area.Monsters.Remove(id, out var monster))
        {
            monster.Dead = true;
            monster.Life = 0;
            monster.NoRewards = true;
            monster.ActionGeneration++;
            player.Area.MonstersVersion++;
        }
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
    }
}
