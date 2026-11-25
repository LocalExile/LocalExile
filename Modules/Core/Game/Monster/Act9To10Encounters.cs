using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Facade and shared combat helpers for the Act 9–10 retail 3.29 boss encounter controllers:
/// <list type="bullet">
/// <item><see cref="ShakariEncounter"/> (<c>2_9_4</c> The Oasis — Shakari P1/P2, desert chase &amp; Sand Pit)</item>
/// <item><see cref="GarukhanKiraEncounter"/> (<c>2_9_8</c> The Quarry — Kira &amp; Garukhan)</item>
/// <item><see cref="GeneralAdusBasiliskEncounters"/> (<c>2_9_9</c> Refinery General Adus, <c>2_9_6</c> Boiling Lake
/// The Basilisk, <c>2_9_3</c> Vastiri Desert Storm Blade Mummy Event)</item>
/// <item><see cref="RottingCoreSoulsEncounter"/> (<c>2_9_10_2</c> The Rotting Core — Doedre, Maligaro &amp; Shavronne)</item>
/// <item><see cref="PlaguewingBannonEncounter"/> (<c>2_10_1</c> Cathedral Rooftop — Bannon defense &amp; Plaguewing)</item>
/// <item><see cref="AvariusReassembledEncounter"/> (<c>2_10_4</c> Desecrated Chambers — Avarius, Reassembled)</item>
/// <item><see cref="VilentaEncounter"/> (<c>2_10_7</c> Control Blocks — Vilenta)</item>
/// </list>
/// </summary>
public static class Act9To10Encounters
{
    const string DefaultOwnerId = "Core.Game";

    static readonly HashSet<string> ControlledBossVarieties = new(StringComparer.Ordinal)
    {
        ShakariEncounter.ShakariP1,
        ShakariEncounter.ShakariP2,
        GarukhanKiraEncounter.Garukhan,
        GeneralAdusBasiliskEncounters.GeneralAdus,
        GeneralAdusBasiliskEncounters.Basilisk,
        RottingCoreSoulsEncounter.DoedreSoul,
        RottingCoreSoulsEncounter.MaligaroSoul,
        RottingCoreSoulsEncounter.ShavronneSoul,
        PlaguewingBannonEncounter.Plaguewing,
        AvariusReassembledEncounter.AvariusWickerman,
        VilentaEncounter.Vilenta,
    };

    /// <summary>Returns true for every boss variety whose combat AI is owned by an Act 9–10 encounter controller,
    /// telling <c>Monsters.cs</c> to skip generic <c>BossKits</c> AI for it.</summary>
    public static bool IsBossControllerVariety(string? id) =>
        id is not null && ControlledBossVarieties.Contains(id);

    /// <summary>Registers all Act 9–10 boss encounter hooks using the module's manifest ID and hot-reload lifetime.</summary>
    public static void Register(IModuleContext module) =>
        Register(module.Manifest.Id, module.Lifetime);

    /// <summary>Registers all Act 9–10 boss encounter hooks using the active Game module lifetime.</summary>
    public static void Register() =>
        Register(DefaultOwnerId, CombatLifetime);

    static void Register(string id, IModuleLifetime? lifetime)
    {
        // Act 9 — Shakari (2_9_4 The Oasis)
        GameEvents.AreaTick.Add(id, GameEvents.InArea(ShakariEncounter.Area, ShakariEncounter.Tick));
        GameEvents.PlayerLeaving.Add(id, (player, _) => ShakariEncounter.Leaving(player));
        lifetime?.Own(ShakariEncounter.CleanupOnUnload());

        // Act 9 — Kira & Garukhan (2_9_8 The Quarry)
        GameEvents.AreaTick.Add(id, GameEvents.InArea(GarukhanKiraEncounter.Area, GarukhanKiraEncounter.Tick));
        GameEvents.MonsterKilled.Add(id, GarukhanKiraEncounter.OnMonsterKilled);
        GameEvents.PlayerLeaving.Add(id, (player, _) => GarukhanKiraEncounter.Leaving(player));
        lifetime?.Own(GarukhanKiraEncounter.CleanupOnUnload());

        // Act 9 — General Adus (2_9_9), The Basilisk (2_9_6), Storm Blade Mummy Event (2_9_3)
        GameEvents.PlayerEntered.Add(id, GeneralAdusBasiliskEncounters.OnPlayerEntered);
        GameEvents.ObjectUse.Add(id, GeneralAdusBasiliskEncounters.Use);
        foreach (string area in GeneralAdusBasiliskEncounters.Areas)
            GameEvents.AreaTick.Add(id, GameEvents.InArea(area, GeneralAdusBasiliskEncounters.Tick));
        GameEvents.PlayerLeaving.Add(id, (player, _) => GeneralAdusBasiliskEncounters.Leaving(player));
        lifetime?.Own(GeneralAdusBasiliskEncounters.CleanupOnUnload());

        // Act 9 — Rotting Core Organ Souls (2_9_10_2)
        GameEvents.AreaTick.Add(id, GameEvents.InArea(RottingCoreSoulsEncounter.Area, RottingCoreSoulsEncounter.Tick));
        GameEvents.PlayerLeaving.Add(id, (player, _) => RottingCoreSoulsEncounter.Leaving(player));
        lifetime?.Own(RottingCoreSoulsEncounter.CleanupOnUnload());

        // Act 10 — Bannon Defense & Plaguewing (2_10_1 Cathedral Rooftop)
        GameEvents.AreaTick.Add(id, GameEvents.InArea(PlaguewingBannonEncounter.Area, PlaguewingBannonEncounter.Tick));
        GameEvents.MonsterKilled.Add(id, PlaguewingBannonEncounter.OnMonsterKilled);
        GameEvents.PlayerLeaving.Add(id, (player, _) => PlaguewingBannonEncounter.Leaving(player));
        lifetime?.Own(PlaguewingBannonEncounter.CleanupOnUnload());

        // Act 10 — Avarius, Reassembled (2_10_4 Desecrated Chambers)
        GameEvents.AreaTick.Add(id, GameEvents.InArea(AvariusReassembledEncounter.Area, AvariusReassembledEncounter.Tick));
        GameEvents.PlayerLeaving.Add(id, (player, _) => AvariusReassembledEncounter.Leaving(player));
        lifetime?.Own(AvariusReassembledEncounter.CleanupOnUnload());

        // Act 10 — Vilenta (2_10_7 Control Blocks)
        GameEvents.AreaTick.Add(id, GameEvents.InArea(VilentaEncounter.Area, VilentaEncounter.Tick));
        GameEvents.PlayerLeaving.Add(id, (player, _) => VilentaEncounter.Leaving(player));
        lifetime?.Own(VilentaEncounter.CleanupOnUnload());
    }

    // ---- Shared internal combat primitives for owned Act 9–10 boss controllers ----

    internal static readonly IReadOnlyDictionary<uint, int> EmptyStats = new Dictionary<uint, int>();

    internal static ushort SkillSlot(int[] effects, int effect)
    {
        int idx = Array.IndexOf(effects, effect);
        return (ushort)(0x4000 + Math.Max(0, idx));
    }

    internal static async Task EnsureNativeSkills(Player runner, uint bossId, SpawnedMonster boss, int[] effects, HashSet<uint> boundBosses)
    {
        if (boss.Variety is null || !boundBosses.Add(bossId)) return;
        var catalog = SkillsFor(runner);
        List<GrantedSkill> skills = [];
        for (int i = 0; i < effects.Length; i++)
        {
            int effect = effects[i];
            ushort slot = (ushort)(0x4000 + i);
            int rank = catalog.MaxLevelAt(effect, boss.Level);
            var atk = BossKits.AttackFor(runner, boss.Variety, boss.Level, effect, slot);
            if (catalog.FindByRow(slot, effect, rank, MonsterActionSkillStats(runner, effect, rank, atk)) is { } granted)
                skills.Add(granted);
        }
        if (skills.Count > 0)
        {
            if (runner.Instance.World.MonsterOrigins.TryGetValue(bossId, out var origin))
                runner.Instance.World.MonsterOrigins[bossId] = origin with { Object = origin.Object with { Skills = skills } };
            await SendWorldRecord(runner, bossId, InstanceEntities.PlayerSkillsChange(bossId, skills, []),
                $"[Act9-10] bound {skills.Count} native skills to {boss.Variety.Id} 0x{bossId:x}");
        }
    }

    internal static async Task EnsureBossBar(Player runner, uint bossId, SpawnedMonster boss)
    {
        if (boss.Variety is not { BossHealthBar: true }) return;
        boss.BossBarShown = true;
        foreach (var p in ActivePlayers(runner))
            if (MonsterShown(p, bossId) && !MonsterAsleep(p, bossId) && BossBars(p).Add(bossId))
                await ShowBossLifeBar(bossId, boss, p);
    }

    internal static async Task Chase(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) goal, double now, string tag)
    {
        int generation = ++boss.ActionGeneration;
        boss.SwingGeneration = null;
        await SendMonsterAction(runner, bossId, boss, generation, () =>
        {
            double at = AreaTime(runner);
            var here = boss.Walk.Position(at);
            boss.Walk.Order(goal.X, goal.Y, at);
            return InstanceProtocol.ActionStarted(bossId, here.X, here.Y, goal.X, goal.Y, 0x2909, runner.NextActionSequence(), runner.MonsterOrderFlags);
        }, $"[{tag}] chase");
        boss.BusyUntil = Math.Max(boss.BusyUntil, now + 0.55);
    }

    internal static MonsterAttack BuildAttack(Player runner, SpawnedMonster boss, int[] effects, int effect,
        DamageType fallbackElement, double fallbackScale, double radius, bool? forceSpell = null)
    {
        ushort slot = SkillSlot(effects, effect);
        var atk = boss.Variety is not null
            ? BossKits.AttackFor(runner, boss.Variety, boss.Level, effect, slot)
            : PhysicalAttack(boss, fallbackScale, radius);
        if (atk.Damage.IsZero)
        {
            var fallback = fallbackElement == DamageType.Physical
                ? PhysicalAttack(boss, fallbackScale, radius)
                : ElementalSpellAttack(boss, fallbackElement, fallbackScale, radius);
            atk = fallback with
            {
                SkillId = slot,
                Seconds = Math.Max(0.65, atk.Seconds),
                Spell = forceSpell ?? fallback.Spell,
                Scripted = true,
            };
        }
        else
        {
            atk = atk with
            {
                SkillId = slot,
                Range = Math.Max(atk.Range, radius),
                Spell = forceSpell ?? atk.Spell,
                Scripted = true,
            };
        }
        return atk;
    }

    internal static async Task<(int Generation, MonsterAttack Attack)?> CastAction(Player runner, uint bossId, SpawnedMonster boss,
        int[] effects, int effect, (int X, int Y) aim, double now, DamageType fallbackElement, double fallbackScale, double radius,
        string tag, bool? forceSpell = null, double extraRecovery = 0.25, uint? targetObjectId = null)
    {
        var atk = BuildAttack(runner, boss, effects, effect, fallbackElement, fallbackScale, radius, forceSpell);
        var pos = boss.Walk.Position(now);
        boss.Walk.Order(pos.X, pos.Y, now);
        int generation = ++boss.ActionGeneration;
        boss.SwingGeneration = null;
        boss.BusyUntil = now + Math.Max(0.6, atk.Seconds) + extraRecovery;
        ushort slot = SkillSlot(effects, effect);
        bool sent = await SendMonsterAction(runner, bossId, boss, generation,
            () => [.. InstanceProtocol.ActionStarted(bossId, pos.X, pos.Y, aim.X, aim.Y, slot, runner.NextActionSequence(), 0x0408,
                       targetObjectId: targetObjectId),
                   .. InstanceProtocol.ActionEnded(bossId)],
            $"[{tag}] {SkillsFor(runner).EffectId(effect)}");
        return sent ? (generation, atk) : null;
    }

    internal static async Task HitCircle(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) center, double radius, Func<bool> current)
    {
        foreach (var p in ActivePlayers(runner))
            await ArenaHazards.Hit(p, sourceId, source, attack, at => Distance(at, center) <= radius, current);
    }

    internal static async Task HitCone(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) origin, double facingAngle, double halfAngleRad, double maxRange, Func<bool> current)
    {
        foreach (var p in ActivePlayers(runner))
            await ArenaHazards.Hit(p, sourceId, source, attack, at =>
            {
                double d = Distance(at, origin);
                if (d < 1.0 || d > maxRange) return d <= 4.0;
                double angle = Math.Atan2(at.Y - origin.Y, at.X - origin.X);
                return Math.Abs(Math.IEEERemainder(angle - facingAngle, Math.Tau)) <= halfAngleRad;
            }, current);
    }

    internal static async Task HitSegment(Player runner, uint sourceId, SpawnedMonster source, MonsterAttack attack,
        (int X, int Y) from, (int X, int Y) to, double halfWidth, Func<bool> current)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lenSq = dx * dx + dy * dy;
        foreach (var p in ActivePlayers(runner))
            await ArenaHazards.Hit(p, sourceId, source, attack, at =>
            {
                if (lenSq < 1.0) return Distance(at, from) <= halfWidth;
                double t = Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / lenSq, 0.0, 1.0);
                double px = from.X + dx * t, py = from.Y + dy * t;
                double ex = at.X - px, ey = at.Y - py;
                return Math.Sqrt(ex * ex + ey * ey) <= halfWidth;
            }, current);
    }

    internal static async Task TeleportBoss(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) dest, double now, string? log = null)
    {
        var safe = WalkableNear(runner, dest);
        boss.Walk.Teleport(safe.X, safe.Y, now);
        await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, safe.X, safe.Y), log);
    }
}
