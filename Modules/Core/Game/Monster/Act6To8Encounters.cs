using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Facade wiring all Act 6–8 dedicated boss encounter controllers and side-boss polish modules, plus shared
/// zero-allocation driver helpers (boss life bar, native skill binding, walk/chase, melee, telegraphed casts,
/// and line/circle hits) so every controller in <see cref="IsBossControllerVariety"/> has complete retail AI.
/// </summary>
public static class Act6To8Encounters
{
    const string DefaultOwner = "Game";

    static readonly HashSet<string> ControlledVarieties = new(StringComparer.Ordinal)
    {
        // Act 6 — Karui Fortress (Tukohama)
        TukohamaEncounter.Boss,
        TukohamaEncounter.RageTotem,
        TukohamaEncounter.BeamTotem,
        TukohamaEncounter.ShieldTotem,
        TukohamaEncounter.SunderTotem,
        TukohamaEncounter.SlamTotem,

        // Act 6 — Shavronne's Tower (Shavronne & Brutus)
        ShavronneBrutusEncounter.Shavronne,
        ShavronneBrutusEncounter.Brutus,
        ShavronneBrutusEncounter.LightningBook,

        // Act 6 — Prisoner's Gate (Abberath)
        AbberathEncounter.Phase1,
        AbberathEncounter.Phase2,

        // Act 6 — Wetlands (Ryslatha)
        RyslathaEncounter.Boss,
        RyslathaEncounter.Egg,

        // Act 7 — Maligaro's Sanctum (Maligaro, Black Death, Fidelitas)
        MaligaroSanctumEncounter.Maligaro,
        MaligaroSanctumEncounter.BlackDeath,
        MaligaroSanctumEncounter.Fidelitas,

        // Act 7 — Ashen Fields (Ralakesh avatars)
        RalakeshEncounter.Greust,
        RalakeshEncounter.Oak,
        RalakeshEncounter.Alira,
        RalakeshEncounter.Kraityn,

        // Act 7 — Dread Thicket (Gruthkul)
        GruthkulEncounter.Boss,

        // Act 8 — The Quay (Tolman)
        TolmanEncounter.Boss,

        // Act 8 — Grain Gate (Gemling Legion)
        GemlingLegionEncounter.Fireball,
        GemlingLegionEncounter.Earthquake,
        GemlingLegionEncounter.GlacialCascade,
        GemlingLegionEncounter.Captain,

        // Act 8 — Solaris L2 (Dawn) & Lunaris L2 (Dusk)
        DawnDuskEncounters.Dawn,
        DawnDuskEncounters.Dusk,
    };

    /// <summary>Returns true for every MonsterVarieties.Id driven by the Act 6–8 dedicated boss controllers so
    /// <c>Monsters.cs</c> skips generic <c>BossKits</c> AI on them.</summary>
    public static bool IsBossControllerVariety(string? id) =>
        !string.IsNullOrEmpty(id) && ControlledVarieties.Contains(id);

    /// <summary>Wires all Act 6–8 boss encounter subscriptions using <see cref="GameEvents.InArea"/> O(1) guards
    /// and registers unload cleanups against <see cref="Game.CombatLifetime"/>.</summary>
    public static void Register() => Register(DefaultOwner, CombatLifetime);

    /// <summary>Overload accepting <see cref="IModuleContext"/> explicitly for <c>GameModule.Load</c>.</summary>
    public static void Register(IModuleContext module) => Register(module.Manifest.Id, module.Lifetime);

    static void Register(string owner, IModuleLifetime? lifetime)
    {
        // Act 6
        GameEvents.AreaTick.Add(owner, GameEvents.InArea(TukohamaEncounter.Area, TukohamaEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => TukohamaEncounter.Leaving(p));
        lifetime?.Own(TukohamaEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(ShavronneBrutusEncounter.Area, ShavronneBrutusEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => ShavronneBrutusEncounter.Leaving(p));
        lifetime?.Own(ShavronneBrutusEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(AbberathEncounter.Area, AbberathEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => AbberathEncounter.Leaving(p));
        lifetime?.Own(AbberathEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(RyslathaEncounter.Area, RyslathaEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => RyslathaEncounter.Leaving(p));
        lifetime?.Own(RyslathaEncounter.CleanupOnUnload());

        // Act 7
        GameEvents.AreaTick.Add(owner, GameEvents.InArea(MaligaroSanctumEncounter.Area, MaligaroSanctumEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => MaligaroSanctumEncounter.Leaving(p));
        lifetime?.Own(MaligaroSanctumEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(RalakeshEncounter.Area, RalakeshEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => RalakeshEncounter.Leaving(p));
        lifetime?.Own(RalakeshEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(GruthkulEncounter.Area, GruthkulEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => GruthkulEncounter.Leaving(p));
        lifetime?.Own(GruthkulEncounter.CleanupOnUnload());

        // Act 8
        GameEvents.AreaTick.Add(owner, GameEvents.InArea(TolmanEncounter.Area, TolmanEncounter.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => TolmanEncounter.Leaving(p));
        lifetime?.Own(TolmanEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(GemlingLegionEncounter.Area, GemlingLegionEncounter.Tick));
        GameEvents.MonsterKilled.Add(owner, GemlingLegionEncounter.OnMonsterKilled);
        GameEvents.Kill.Add(owner, kill =>
        {
            if (kill.PlayerKill || kill.Player.Layout.Id != GemlingLegionEncounter.Area) return;
            var payload = new MonsterKill(kill.Player, global::Game.ModMonster(kill.Monster, kill.At));
            kill.Later(() => GemlingLegionEncounter.OnMonsterKilled(payload));
        });
        GameEvents.PlayerLeaving.Add(owner, (p, _) => GemlingLegionEncounter.Leaving(p));
        lifetime?.Own(GemlingLegionEncounter.CleanupOnUnload());

        GameEvents.AreaTick.Add(owner, GameEvents.InArea(DawnDuskEncounters.SolarisArea, DawnDuskEncounters.Tick));
        GameEvents.AreaTick.Add(owner, GameEvents.InArea(DawnDuskEncounters.LunarisArea, DawnDuskEncounters.Tick));
        GameEvents.PlayerLeaving.Add(owner, (p, _) => DawnDuskEncounters.Leaving(p));
        lifetime?.Own(DawnDuskEncounters.CleanupOnUnload());

        // Act 6 & 8 side boss polish
        foreach (string sideArea in Act6To8SideBossPolish.Areas)
            GameEvents.AreaTick.Add(owner, GameEvents.InArea(sideArea, Act6To8SideBossPolish.Tick));
        GameEvents.MonsterKilled.Add(owner, Act6To8SideBossPolish.OnMonsterKilled);
        GameEvents.Kill.Add(owner, kill =>
        {
            if (kill.PlayerKill || kill.Player.Layout.Id != Act6To8SideBossPolish.ToxicConduits
                || kill.VarietyId != Act6To8SideBossPolish.LingeringAberration) return;
            var payload = new MonsterKill(kill.Player, global::Game.ModMonster(kill.Monster, kill.At));
            kill.Later(() => Act6To8SideBossPolish.OnMonsterKilled(payload));
        });
        GameEvents.PlayerLeaving.Add(owner, (p, _) => Act6To8SideBossPolish.Leaving(p));
        lifetime?.Own(Act6To8SideBossPolish.CleanupOnUnload());
    }
}

/// <summary>Shared low-allocation combat driver helpers for Act 6–8 full-controlled boss encounters.</summary>
internal static class Act6To8Driver
{
    public const ushort MoveSkillId = 0x2909;
    public const ushort DefaultAttackSkillId = 0x4000;
    public const ushort CannotBeDamagedBuff = 350;

    static readonly RuleTable<SpawnedMonster, Dictionary<int, ushort>> BoundSkillMaps = new();

    /// <summary>Shows the boss health bar for every active player who has not received it yet (including late joiners
    /// and portal re-entry clients).</summary>
    public static async Task EnsureBossBar(Player runner, uint id, SpawnedMonster boss)
    {
        if (boss.Variety is not { BossHealthBar: true }) return;
        if (boss.States?.IndexOf("boss_life_bar") is not >= 0) return;
        foreach (var p in ActivePlayers(runner))
        {
            if (MonsterShown(p, id) && !MonsterAsleep(p, id) && BossBars(p).Add(id))
            {
                boss.BossBarShown = true;
                await ShowBossLifeBar(id, boss, p);
            }
        }
    }

    /// <summary>Appends any missing native <c>GrantedEffects</c> rows to the monster's Actor skill list once so
    /// <see cref="CastSkill"/> plays native animations without client skill-lookup errors.</summary>
    public static async Task BindEffects(Player runner, uint id, SpawnedMonster boss, int[] effectRows)
    {
        if (boss.Variety is null || effectRows.Length == 0) return;
        if (BoundSkillMaps.TryGetValue(boss, out _)) return;
        var map = new Dictionary<int, ushort>();
        var catalog = SkillsFor(runner);
        List<GrantedSkill> skills = [];

        if (runner.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) && origin.Object.Skills is { Count: > 0 } existing)
            skills.AddRange(existing);
        else if (boss.Kit.Count > 0)
            skills.AddRange(BossKits.SkillsOf(runner, boss.Variety, boss.Level, boss.Kit));
        else if (boss.Attack is { } atk && catalog.FindByRow(atk.SkillId, 406, boss.Level) is { } melee)
            skills.Add(melee);

        foreach (var entry in boss.Kit)
            map.TryAdd(entry.Effect, entry.Attack.SkillId);

        ushort nextId = skills.Count == 0 ? DefaultAttackSkillId : (ushort)(skills.Max(s => s.Id) + 1);
        if (nextId < 0x4000) nextId = 0x4000;
        bool added = false;

        foreach (int effect in effectRows)
        {
            if (map.ContainsKey(effect)) continue;
            int rank = catalog.MaxLevelAt(effect, boss.Level);
            var atk = BossKits.AttackFor(runner, boss.Variety, boss.Level, effect, nextId);
            var stats = MonsterActionSkillStats(runner, effect, rank, atk);
            if (catalog.FindByRow(nextId, effect, rank, stats) is { } granted)
            {
                skills.Add(granted);
                map[effect] = nextId;
                nextId++;
                added = true;
            }
        }

        BoundSkillMaps.AddOrUpdate(boss, map);
        if (!added || skills.Count == 0) return;
        if (runner.Instance.World.MonsterOrigins.TryGetValue(id, out var curOrigin))
            runner.Instance.World.MonsterOrigins[id] = curOrigin with { Object = curOrigin.Object with { Skills = skills } };
        if (ActivePlayers(runner).Any(p => MonsterShown(p, id) && !MonsterAsleep(p, id)))
            await SendWorldRecord(runner, id, InstanceEntities.PlayerSkillsChange(id, skills, []), null);
    }

    public static ushort SkillIdFor(SpawnedMonster boss, int effect)
    {
        if (BoundSkillMaps.TryGetValue(boss, out var map) && map.TryGetValue(effect, out ushort id))
            return id;
        for (int i = 0; i < boss.Kit.Count; i++)
            if (boss.Kit[i].Effect == effect)
                return boss.Kit[i].Attack.SkillId;
        return boss.Attack?.SkillId ?? DefaultAttackSkillId;
    }

    public static bool CanAct(SpawnedMonster boss, double now) =>
        Alive(boss) && !boss.WorldHidden && now >= boss.BusyUntil && now >= boss.FlinchUntil && MonsterActionFactor(boss, now) > 0;

    /// <summary>Sends a native walk action toward <paramref name="goal"/>, stopping at <paramref name="stopRange"/>.</summary>
    public static async Task MoveToward(Player runner, uint id, SpawnedMonster boss, (int X, int Y) goal, double now, double stopRange = 10)
    {
        if (boss.Variety is { MovementSpeed: 0 } or { Immobile: true } || boss.Walk.MovementBlocked(now)) return;
        var pos = boss.Walk.Position(now);
        double dist = Distance(pos, goal);
        if (dist <= stopRange) return;
        double frac = Math.Clamp((dist - Math.Max(2, stopRange - 1)) / dist, 0, 1);
        var dest = WalkableNear(runner, (
            (int)Math.Round(pos.X + (goal.X - pos.X) * frac),
            (int)Math.Round(pos.Y + (goal.Y - pos.Y) * frac)));
        int gen = ++boss.ActionGeneration;
        await SendMonsterAction(runner, id, boss, gen, () =>
        {
            var here = boss.Walk.Position(AreaTime(runner));
            boss.Walk.Order(dest.X, dest.Y, AreaTime(runner));
            return InstanceProtocol.ActionStarted(id, here.X, here.Y, dest.X, dest.Y, MoveSkillId, runner.NextActionSequence(), 0x0408);
        }, null);
    }

    /// <summary>Starts a boss skill action, freezes walk in place, bumps <c>ActionGeneration</c> and <c>BusyUntil</c>.</summary>
    public static async Task<int?> CastAction(Player runner, uint id, SpawnedMonster boss, int effect,
        (int X, int Y) target, double duration, double now, string? log = null, uint? targetObjectId = null)
    {
        var pos = boss.Walk.Position(now);
        boss.Walk.Order(pos.X, pos.Y, now);
        int gen = ++boss.ActionGeneration;
        boss.SwingGeneration = null;
        boss.BusyUntil = now + Math.Max(0.35, duration);
        ushort skillId = SkillIdFor(boss, effect);
        ushort seq = 0;
        bool sent = await SendMonsterAction(runner, id, boss, gen, () =>
        {
            var here = boss.Walk.Position(AreaTime(runner));
            seq = runner.NextActionSequence();
            return InstanceProtocol.ActionStarted(id, here.X, here.Y, target.X, target.Y, skillId, seq, 0x0408,
                targetObjectId: targetObjectId);
        }, log);
        if (sent && SkillsFor(runner).IsChannelled(effect))
            SendAfter(runner, Math.Max(0.35, duration), () =>
                !boss.Dead && boss.ActionGeneration == gen
                    ? SendWorldRecord(runner, id, InstanceProtocol.ChannelFinished(id, seq), $"0x01ae boss 0x{id:x} channel 0x{seq:x4} ended")
                    : Task.CompletedTask);
        return sent ? gen : null;
    }

    /// <summary>Runs a standard chase + melee swing loop for a controlled boss when no special skill is due,
    /// returning the updated <c>nextChaseAt</c> timestamp.</summary>
    public static async Task<double> StepChaseOrMelee(Player runner, uint id, SpawnedMonster boss, Player target,
        double now, double nextChaseAt, List<ScheduledHit> hits, double meleeScale = 1.0, int meleeEffect = 406)
    {
        var pos = boss.Walk.Position(now);
        var aim = target.Walk.Position(now);
        double dist = Distance(pos, aim);
        double reach = Math.Max(10, boss.Variety?.AttackRange ?? 12);
        if (dist <= reach + 2)
        {
            double swingSec = Math.Clamp((boss.Variety?.AttackMs ?? 1100) / 1000.0, 0.55, 1.8);
            if (await CastAction(runner, id, boss, meleeEffect, aim, swingSec, now) is { } gen)
                hits.Add(new(now + swingSec * 0.5, id, boss, aim, reach + 3, PhysicalAttack(boss, meleeScale, reach + 3), gen));
            return nextChaseAt;
        }
        if (now >= nextChaseAt)
        {
            await MoveToward(runner, id, boss, aim, now, Math.Max(4, reach - 2));
            return now + 0.7;
        }
        return nextChaseAt;
    }

    public sealed record ScheduledHit(double At, uint SourceId, SpawnedMonster Source, (int X, int Y) Spot,
        double Radius, MonsterAttack Attack, int? Generation, string? ImpactAo = null, float ImpactScale = 1.1f);

    /// <summary>Resolves all due <see cref="ScheduledHit"/> entries through <see cref="ArenaHazards.Hit"/> (covering
    /// players + Golems + Reservations + Summons.Fighters).</summary>
    public static async Task ResolveHits(Player runner, List<ScheduledHit> hits, List<TimedEffect> effects,
        double now, Func<bool> active)
    {
        if (hits.Count == 0) return;
        var due = hits.Where(hit => hit.At <= now).OrderBy(hit => hit.At).ToArray();
        if (due.Length == 0) return;
        hits.RemoveAll(hit => hit.At <= now);
        var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
        foreach (var h in due)
        {
            if (!Alive(h.Source) || h.Source.WorldHidden) continue;
            if (h.Generation is int gen && (h.Source.ActionGeneration != gen || MonsterActionFactor(h.Source, now) == 0))
                continue;
            if (!string.IsNullOrEmpty(h.ImpactAo))
                await SpawnTimedEffect(runner, effects, h.Spot, h.ImpactAo, now + 1.1, scale: h.ImpactScale);
            foreach (var p in players)
                await ArenaHazards.Hit(p, h.SourceId, h.Source, h.Attack,
                    at => Distance(at, h.Spot) <= h.Radius,
                    () => active() && Alive(h.Source));
        }
    }

    /// <summary>Evaluates a line/beam hit from <paramref name="origin"/> along <paramref name="angle"/>.</summary>
    public static async Task HitBeamLine(Player runner, uint sourceId, SpawnedMonster source,
        (int X, int Y) origin, double angle, double length, double halfWidth, MonsterAttack attack, Func<bool> active)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        bool Inside((int X, int Y) at)
        {
            double dx = at.X - origin.X, dy = at.Y - origin.Y;
            double along = dx * cos + dy * sin;
            if (along < 2 || along > length) return false;
            return Math.Abs(-dx * sin + dy * cos) <= halfWidth;
        }
        var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
        foreach (var p in players)
            await ArenaHazards.Hit(p, sourceId, source, attack, Inside, () => active() && Alive(source));
    }

    /// <summary>Broadcasts a boss life heal safely via <see cref="SendWorldRecord"/>.</summary>
    public static async Task HealBoss(Player runner, uint id, SpawnedMonster boss, uint newLife, string? log = null)
    {
        uint clamped = Math.Min(boss.MaxLife, Math.Max(boss.Life, newLife));
        if (clamped <= boss.Life) return;
        boss.Life = clamped;
        await SendWorldRecord(runner, id, InstanceProtocol.LifeUpdate(id, clamped), log);
    }

    /// <summary>Applies mixed Physical + Elemental damage attack packet scaled from monster base damage.</summary>
    public static MonsterAttack HybridAttack(SpawnedMonster source, DamageType element, double physScale, double elemScale,
        double radius, bool spell = false)
    {
        var (min, max) = Monsters.BaseDamage(source.Variety!, source.Level) ?? (10, 15);
        var pkt = DamagePacket.Empty;
        if (physScale > 0) pkt = pkt.Add(DamageType.Physical, new(min * physScale, max * physScale));
        if (elemScale > 0) pkt = pkt.Add(element, new(min * elemScale, max * elemScale));
        return new(DefaultAttackSkillId, 1.0, radius, pkt) { Spell = spell, Scripted = true };
    }
}
