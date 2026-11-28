using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Mercenary;

public sealed partial class MercenaryModule
{
    /// <summary>Live runtime state for a player's active allied mercenary follower in a combat area.</summary>
    sealed class CompanionRuntime
    {
        public required uint EntityId { get; init; }
        public required SpawnedMonster Monster { get; init; }
        public required HiredMercenary Mercenary { get; init; }
        public required MercenaryTables.Class Kind { get; init; }
        public required MercenaryTables.Build Build { get; init; }
        public required MercenaryEquipment.ComputedStats Computed { get; set; }
        public readonly EnergyShield ShieldPool = new();
        public readonly RecoupRecovery Recoup = new();
        public List<MonsterKitSkill> BaseKit { get; set; } = [];
        public List<MonsterKitSkill> Kit { get; set; } = [];
        public readonly Dictionary<int, int> EffectToSkillRow = [];
        public readonly Dictionary<int, Dictionary<uint, int>> SupportStatsByEffect = [];
        public ushort[] LastSentSkillIds { get; set; } = [];
        public readonly Dictionary<int, double> Cooldowns = [];
        public readonly Dictionary<int, double> LastUsedAt = [];
        public PlayerMinions.Aura[] SharedAuras { get; set; } = [];
        public List<BlasphemyAura> BlasphemyAuras { get; set; } = [];
        public readonly List<ActiveSelfBuff> ActiveSelfBuffs = [];
        public readonly List<CompanionDelayedStrike> ActiveStrikes = [];
        public readonly List<CompanionTrap> ActiveTraps = [];
        public readonly List<CompanionGround> ActiveGrounds = [];
        public readonly List<CompanionSummon> ActiveSummons = [];
        public int GuardPoolRemaining;
        public int GuardAbsorbPct;
        public double GuardUntil = double.NegativeInfinity;
        public double LastTick;
        public double LastDamagedAt = double.NegativeInfinity;
        public double LastBlasphemySync = double.NegativeInfinity;
        public double RegenCarry;
        public bool NextOffHand;
        public bool LastActionWasSpecial;
        public PendingCompanionAttack? PendingAttack;
        public uint LastSentEs;
        public bool Closed;
    }

    sealed record BlasphemyAura(string CanonicalName, Dictionary<uint, int> Stats, double Radius);
    sealed record ActiveSelfBuff(int Effect, ushort BuffRow, ushort Tag, IReadOnlyDictionary<uint, int> Stats, double Until);
    sealed record CompanionDelayedStrike(uint MarkerId, bool HasVisual, MonsterAttack Attack, int Effect,
        (int X, int Y) At, double Radius, double StrikeAt, string? ImpactArt, double ImpactSeconds);
    sealed class CompanionTrap(uint id, MonsterAttack payload, int effect, (int X, int Y) at,
        double armedAt, double expiresAt, double triggerRadius, bool hasVisual)
    {
        public readonly uint Id = id;
        public readonly MonsterAttack Payload = payload;
        public readonly int Effect = effect;
        public readonly (int X, int Y) At = at;
        public readonly double ArmedAt = armedAt;
        public readonly double ExpiresAt = expiresAt;
        public readonly double TriggerRadius = triggerRadius;
        public readonly bool HasVisual = hasVisual;
    }
    sealed class CompanionGround(MonsterAttack attack, int effect, (int X, int Y) at, double radius,
        double expiresAt, double nextTickAt)
    {
        public readonly MonsterAttack Attack = attack;
        public readonly int Effect = effect;
        public readonly (int X, int Y) At = at;
        public readonly double Radius = radius;
        public readonly double ExpiresAt = expiresAt;
        public double NextTickAt = nextTickAt;
    }
    sealed class CompanionSummon(int sourceEffect, MonsterAttack bodyAttack, (int X, int Y) at,
        double expiresAt, double nextActAt)
    {
        public readonly int SourceEffect = sourceEffect;
        public readonly MonsterAttack BodyAttack = bodyAttack;
        public readonly (int X, int Y) At = at;
        public readonly double ExpiresAt = expiresAt;
        public double NextActAt = nextActAt;
    }
    sealed record PendingCompanionAttack(MonsterKitSkill Skill, MonsterAttack ScaledAttack, uint TargetId,
        SpawnedMonster Target, (int X, int Y) From, (int X, int Y) Aim, double ContactAt, int Generation,
        bool UseOffHand, (int X, int Y)? TravelLanding = null);

    sealed record BuiltCompanionKit(
        List<MonsterKitSkill> BaseKit,
        List<MonsterKitSkill> ScaledKit,
        Dictionary<int, int> EffectToSkillRow,
        Dictionary<int, Dictionary<uint, int>> SupportStatsByEffect,
        PlayerMinions.Aura[] SharedAuras,
        List<BlasphemyAura> BlasphemyAuras);

    readonly record struct CompanionOffence(
        MonsterAttack Attack,
        int BaseCritPermyriad,
        Penetration.PerType Penetration,
        bool CannotCrit,
        bool IgnoreEvasion,
        bool IgnorePhysicalReduction,
        bool CannotInflictAilments,
        int WitherChancePct,
        int CoverInAshChancePct,
        int CrushChancePct,
        int AdditionalChains,
        int PierceCount,
        bool AlwaysPierce,
        bool ProjectilesReturn,
        bool ProjectilesNova,
        int ProjectileSpeedPct,
        double AreaScaleFactor,
        int ExtraTrapsToThrow,
        int ExtraTrapsAllowed,
        int ExtraTotemsAllowed);

    const double CompanionTeleportDistance = 120.0;
    const double CompanionFollowDistance = 14.0;
    const double CompanionAggroRange = 75.0;
    const double CompanionOwnerMaxRange = 95.0;
    const double OutOfCombatDelaySeconds = 4.0;
    const double OutOfCombatRegenPerSecondFraction = 0.08; // 8%/s out-of-combat regen

    // ---- Spawning & Despawning ------------------------------------------------------------------------------------------------

    async Task SpawnActiveCompanionIfEligible(Player player, RosterState roster, (int X, int Y)? spawnAt = null)
    {
        if (IsTownOrHideout(player)) return;
        if (EffectiveInvitedKey(player, roster) is not byte key) return;
        if (!roster.Slots.TryGetValue(key, out var merc) || merc.Dead) return;
        if (MercenaryTables.ClassOf(merc.ClassRow) is not { } kind || MercenaryTables.BuildOf(merc.BuildRow) is not { } build) return;
        if (Monsters.Find(kind.AlliedPath) is not { Supported: true } allied) return;
        if (States(allied, MercenaryWire.HiredStates) is not { } hired) return;

        await DespawnCompanion(player);

        var invRow = PlayerInventories.MercenaryRowForKey(key);
        var items = invRow is byte r ? ItemsOf(player).Items(r) : [];
        var looks = MercenaryEquipment.VisualsFromItems(kind, build, items);
        var computed = MercenaryEquipment.ComputeStats(player, kind, build, merc.Level, AreaLevelOf(player), items, merc.IsPermanent, merc.Skills);
        var builtKit = BuildCompanionKit(player, allied, merc, computed);
        var actorSkills = BuildCompanionActorSkills(player, allied, merc.Level, builtKit.ScaledKit);
        var at = spawnAt ?? NextToPlayer(player, Math.Clamp(allied.ObjectSize - 1, 0, 127));

        string displayName = MercenaryTables.FormatName(merc.FirstNameHash32, merc.SecondNameHash32) ?? build.Name;
        uint id = await SpawnMonster(player, allied, merc.Level, MonsterRarity.Unique, at.X, at.Y,
            friendly: true, instanceOwned: false, scripted: true, ownedLooks: looks,
            mods: computed.Mods, extraStats: computed.ClientStats,
            owned: (_, _) => { },
            ownedSkills: actorSkills,
            ownedBaseData: self => [(MonsterObject.OwnerKey, 0, player.Id), (MonsterObject.SelfKey, 1, self)],
            ownedMaximumLife: computed.MaxLife, ownedMaximumEnergyShield: computed.MaxEnergyShield,
            ownedEnergyShield: computed.MaxEnergyShield,
            shape: m => m with
            {
                Name = displayName,
                Team = MercenaryWire.AlliedTeam,
                StateMachine = hired,
                MercenaryBuild = build.Hash16,
                HasDialogue = true,
                NpcFlags = 0,
                InteractionFlags = 1,
            });
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var monster)) return;

        monster.States = hired;
        monster.Mods = computed.Mods;
        monster.IsAlliedCompanion = true;
        monster.IsPermanentCompanion = merc.IsPermanent;
        monster.Aggro = true;
        RegisterLoadoutNpc(player, id, key, kind, at);
        RecordEntitySync(player, id, looks, computed.ClientStats, monster.Life);
        RecordPlayerSnapshot(player);

        var runtime = new CompanionRuntime
        {
            EntityId = id,
            Monster = monster,
            Mercenary = merc,
            Kind = kind,
            Build = build,
            Computed = computed,
            BaseKit = builtKit.BaseKit,
            Kit = builtKit.ScaledKit,
            SharedAuras = builtKit.SharedAuras,
            BlasphemyAuras = builtKit.BlasphemyAuras,
            LastSentSkillIds = [.. actorSkills.Select(s => s.Id)],
            LastTick = AreaTime(player),
            LastSentEs = computed.MaxEnergyShield,
        };
        foreach (var (eff, row) in builtKit.EffectToSkillRow) runtime.EffectToSkillRow[eff] = row;
        foreach (var (eff, supp) in builtKit.SupportStatsByEffect) runtime.SupportStatsByEffect[eff] = supp;
        runtime.ShieldPool.SetMaximum(computed.MaxEnergyShield, refill: true);

        var activeHandle = new ActiveCompanion(id, monster, merc.IsPermanent)
        {
            EquippedUniqueCount = computed.EquippedUniqueCount,
            ApplyDamageLocked = (p, amount, now) => ApplyCompanionDamageLocked(p, runtime, amount, now),
            ApplyDamageAsync = (p, amount, now) => ApplyCompanionDamageAsync(p, runtime, amount, now),
            OnHitEnemy = (p, target) => OnCompanionHitEnemyLocked(p, runtime, target, AreaTime(p)),
        };

        companions.AddOrUpdate(player, runtime);
        player.Companion = activeHandle;
        if (runtime.SharedAuras.Length > 0)
            SyncCompanionSharedAuras(player, activeHandle, runtime.SharedAuras, AreaTime(player), scaleByLuminary: true);

        // Ensure client gets initial Defence & Luminary stats on entity
        SetStats(player, id, computed.ClientStats);
        context.Log($"{player.Layout.Id}: spawned {(merc.IsPermanent ? "permanent" : "temporary")} mercenary {build.Name} (key {key}, lvl {merc.Level}, {actorSkills.Count} skills) as 0x{id:x}");
    }

    async Task DespawnCompanion(Player player)
    {
        uint entityId;
        List<uint> cleanupIds;
        using (ProjectileMutation(player))
        {
            if (!companions.TryGetValue(player, out var comp)) return;
            companions.Remove(player);
            comp.Closed = true;
            entityId = comp.EntityId;
            cleanupIds = CollectAndClearCompanionObjects(comp);
            ClearCompanionSharedAuras(player, entityId);
            ClearCompanionTauntsLocked(player, entityId);
            ClearCompanionSelfBuffs(player, comp);
            if (player.Companion?.EntityId == entityId)
                player.Companion = null;
            if (loadoutNpcs.TryGetValue(player, out var map))
                lock (map) map.Remove(entityId);
            RemoveEntitySync(player, entityId);
            lock (player.Npcs) player.Npcs.Remove(entityId);
            player.Area.Monsters.Remove(entityId);
            player.Instance?.World.Area.Monsters.Remove(entityId);
            player.MinionOrigins.TryRemove(entityId, out _);
        }
        if (!player.Closed && !player.LeavingArea)
        {
            foreach (uint objId in cleanupIds)
                await SendWorldRemoval(player, objId, $"0x02bd despawn mercenary object 0x{objId:x}");
            await SendWorldRemoval(player, entityId, $"0x02bd despawn mercenary companion 0x{entityId:x}");
        }
    }

    Task OnPlayerLeaving(Player player, string why)
    {
        CloseCompanionTransient(player);
        return Task.CompletedTask;
    }

    void CloseCompanionTransient(Player player)
    {
        using (ProjectileMutation(player))
        {
            if (companions.TryGetValue(player, out var comp))
            {
                comp.Closed = true;
                CollectAndClearCompanionObjects(comp);
                ClearCompanionSharedAuras(player, comp.EntityId);
                ClearCompanionTauntsLocked(player, comp.EntityId);
                ClearCompanionSelfBuffs(player, comp);
                companions.Remove(player);
                if (player.Companion?.EntityId == comp.EntityId)
                    player.Companion = null;
                player.Area.Monsters.Remove(comp.EntityId);
                player.Instance?.World.Area.Monsters.Remove(comp.EntityId);
                player.MinionOrigins.TryRemove(comp.EntityId, out _);
                lock (player.Npcs) player.Npcs.Remove(comp.EntityId);
            }
            if (loadoutNpcs.TryGetValue(player, out var map))
            {
                uint[] npcIds;
                lock (map) { npcIds = [.. map.Keys]; map.Clear(); }
                foreach (uint entityId in npcIds)
                {
                    player.Area.Monsters.Remove(entityId);
                    player.Instance?.World.Area.Monsters.Remove(entityId);
                    player.MinionOrigins.TryRemove(entityId, out _);
                    lock (player.Npcs) player.Npcs.Remove(entityId);
                }
                loadoutNpcs.Remove(player);
            }
            lastEntitySync.Remove(player);
            lastPlayerSnapshot.Remove(player);
        }
    }

    static List<uint> CollectAndClearCompanionObjects(CompanionRuntime comp)
    {
        List<uint> ids = [];
        foreach (var s in comp.ActiveStrikes)
            if (s.HasVisual && s.MarkerId != 0) ids.Add(s.MarkerId);
        foreach (var t in comp.ActiveTraps)
            if (t.HasVisual && t.Id != 0) ids.Add(t.Id);
        comp.ActiveStrikes.Clear();
        comp.ActiveTraps.Clear();
        comp.ActiveGrounds.Clear();
        comp.ActiveSummons.Clear();
        comp.PendingAttack = null;
        return ids;
    }

    static void ClearCompanionSelfBuffs(Player player, CompanionRuntime comp)
    {
        foreach (var b in comp.ActiveSelfBuffs)
            if (b.BuffRow > 0)
                player.Area.Buffs.Clear(comp.EntityId, b.BuffRow, comp.EntityId, tag: b.Tag);
        comp.ActiveSelfBuffs.Clear();
        comp.Monster.BuffStats = null;
        comp.GuardPoolRemaining = 0;
        comp.GuardUntil = double.NegativeInfinity;
    }

    // ---- Kit, Actor Skills & Shared Aura Construction -------------------------------------------------------------------------

    static BuiltCompanionKit BuildCompanionKit(Player player, MonsterCatalog.Variety variety, HiredMercenary merc,
        MercenaryEquipment.ComputedStats computed)
    {
        var skills = SkillsFor(player);
        var sharedAuras = new List<PlayerMinions.Aura>();
        var blasphemyAuras = new List<BlasphemyAura>();
        var combatEffects = new List<int>();
        var effectToSkillRow = new Dictionary<int, int>();
        var supportStatsByEffect = new Dictionary<int, Dictionary<uint, int>>();
        int gearAuraPct = computed.Mods.Effects[Stat.AuraEffectPlusPct]
            + computed.Mods.Effects[Stat.NonCurseAuraEffectPlusPct];

        foreach (var rolled in merc.Skills)
        {
            if (MercenaryTables.SkillOf(rolled.SkillRow) is not { } skillRow) continue;
            int effect = skillRow.GrantedEffect;
            if (effect < 0) continue;

            var suppStats = MercenaryTables.SupportStatsOf([rolled]);

            if (MercenaryTables.FriendlyAuraFamily(skills, effect) is { } family)
            {
                ushort buffRow = AuraCatalog.BuffRow(family);
                var baseStats = PlayerMinions.AuraStatsAt(skills, Stats, effect, family, merc.Level);
                int generosityBonus = suppStats.GetValueOrDefault(Stat.NonCurseAuraEffectPlusPct)
                    + suppStats.GetValueOrDefault(Stat.AuraEffectPlusPct);
                bool noSelf = suppStats.GetValueOrDefault(Stat.AuraCannotAffectSelf) > 0;
                int effectLevel = skills.MaxLevelAt(effect, merc.Level);
                var own = skills.EffectStats(effect, effectLevel).ToDictionary(s => s.Item1, s => s.Item2);
                int radius = SkillArea.Radius(AuraCatalog.BaseRadius, k => own.GetValueOrDefault(k), spell: false);

                // Keep base stats unscaled and carry ownAuraBonus so SyncCompanionSharedAuras adds Bestowed Knighthood linearly.
                int ownAuraBonus = gearAuraPct + generosityBonus;
                var aura = new PlayerMinions.Aura(family, buffRow, Math.Max(40, radius), baseStats)
                {
                    SharesWithAllies = true,
                    AffectsSource = !noSelf,
                    OwnAuraEffectPct = ownAuraBonus,
                };
                sharedAuras.Add(aura);
            }
            else if (MercenaryTables.IsBlasphemyCurse(skills, effect)
                && MercenaryTables.CanonicalCurseName(skills.EffectId(effect)) is { } blasphemyName)
            {
                var own = GolemData.Skill(skills, effect, merc.Level).Stats;
                int radius = SkillArea.Radius(22, k => own.GetValueOrDefault(k) + suppStats.GetValueOrDefault(k), spell: true);
                blasphemyAuras.Add(new BlasphemyAura(blasphemyName, own, Math.Max(22, radius)));
            }
            else
            {
                if (!combatEffects.Contains(effect))
                    combatEffects.Add(effect);
                effectToSkillRow[effect] = rolled.SkillRow;
                supportStatsByEffect[effect] = suppStats;
            }
        }

        // Build kit with keepUnshaped=true so mercenary skills without a BossKits.Shaped form (slams, marks, rains,
        // totems, weapon skills) are kept; never insert MeleeFallbackEffect at index 0 ahead of build skills.
        var baseKit = combatEffects.Count > 0
            ? BossKits.KitFromEffects(player, variety, merc.Level, combatEffects, modifiers: null, keepUnshaped: true).ToList()
            : [];

        if (!baseKit.Any(k => k.Attack.Use is null && k.TriggerThreshold == 0) && MercenaryTables.MeleeFallbackEffect >= 0)
        {
            var fallback = BossKits.KitFromEffects(player, variety, merc.Level,
                [.. combatEffects, MercenaryTables.MeleeFallbackEffect], modifiers: null, keepUnshaped: true);
            if (fallback.Count > 0) baseKit = [.. fallback];
        }

        // Scale each kit skill's Attack once using equipped weapon, build ExtraStats, gear, supports, and Luminary bonuses
        // so Actor.Skills wire records carry accurate attack/cast times, projectile counts, and damage.
        var scaledKit = new List<MonsterKitSkill>(baseKit.Count);
        for (int i = 0; i < baseKit.Count; i++)
        {
            var entry = baseKit[i];
            supportStatsByEffect.TryGetValue(entry.Effect, out var supp);
            var offence = EvaluateCompanionOffence(player, variety, merc, computed, entry, supp,
                selfBuffStats: null, linkOff: default, target: null, now: 0, useOffHand: false);
            double cd = ScaleCooldown(entry.CooldownSeconds, computed.StatValues, supp);
            scaledKit.Add(entry with { Attack = offence.Attack, CooldownSeconds = cd });
        }

        return new BuiltCompanionKit(baseKit, scaledKit, effectToSkillRow, supportStatsByEffect,
            [.. sharedAuras], blasphemyAuras);
    }

    static IReadOnlyList<GrantedSkill> BuildCompanionActorSkills(Player player, MonsterCatalog.Variety variety, byte level,
        IReadOnlyList<MonsterKitSkill> kit)
    {
        if (kit.Count == 0) return [];
        var actorSkills = BossKits.SkillsOf(player, variety, level, kit);
        return MonsterCombos.WithChildSkills(player, variety, level, kit, actorSkills);
    }

    (IReadOnlyList<GrantedSkill> Skills, ushort[] RemovedIds) RebuildCompanionKitAndAuras(Player player, CompanionRuntime comp,
        MercenaryTables.Class kind, MercenaryTables.Build build)
    {
        if (comp.Monster.Variety is not { } variety)
            return ([], []);

        var built = BuildCompanionKit(player, variety, comp.Mercenary, comp.Computed);
        comp.BaseKit = built.BaseKit;
        comp.Kit = built.ScaledKit;
        comp.EffectToSkillRow.Clear();
        foreach (var (eff, row) in built.EffectToSkillRow) comp.EffectToSkillRow[eff] = row;
        comp.SupportStatsByEffect.Clear();
        foreach (var (eff, supp) in built.SupportStatsByEffect) comp.SupportStatsByEffect[eff] = supp;
        comp.SharedAuras = built.SharedAuras;
        comp.BlasphemyAuras = built.BlasphemyAuras;

        if (player.Companion is { } ac && ac.EntityId == comp.EntityId)
            SyncCompanionSharedAuras(player, ac, comp.SharedAuras, AreaTime(player), scaleByLuminary: true);

        var actorSkills = BuildCompanionActorSkills(player, variety, comp.Mercenary.Level, built.ScaledKit);
        var nextIds = actorSkills.Select(s => s.Id).ToHashSet();
        var removed = comp.LastSentSkillIds.Where(id => !nextIds.Contains(id)).ToArray();
        comp.LastSentSkillIds = [.. actorSkills.Select(s => s.Id)];
        return (actorSkills, removed);
    }

    static double ScaleCooldown(double baseSeconds, IReadOnlyDictionary<uint, int> stats, IReadOnlyDictionary<uint, int>? supp)
    {
        if (baseSeconds <= 0) return 0;
        int cdr = stats.GetValueOrDefault(Stat.BaseCooldownSpeedPlusPct)
            + (supp?.GetValueOrDefault(Stat.BaseCooldownSpeedPlusPct) ?? 0);
        return Math.Max(0.2, baseSeconds / Math.Max(0.2, 1.0 + cdr / 100.0));
    }

    // ---- Full PoE 3.29 Weapon, Gear, ExtraStats, Support & Luminary Offence Calculation ---------------------------------------

    static CompanionOffence EvaluateCompanionOffence(
        Player player, MonsterCatalog.Variety variety, HiredMercenary merc,
        MercenaryEquipment.ComputedStats computed, MonsterKitSkill entry,
        IReadOnlyDictionary<uint, int>? supportStats,
        IReadOnlyDictionary<uint, int>? selfBuffStats,
        CompanionLinkOffence linkOff, SpawnedMonster? target, double now, bool useOffHand)
    {
        var catalog = SkillsFor(player);
        var baseAttack = entry.Attack;
        int effect = entry.Effect;
        var native = GolemData.TrySkill(catalog, effect, merc.Level, out var ns) ? ns : (1, new Dictionary<uint, int>(), 1.0, 0);
        var skillStats = native.Item2;
        double baseMult = Math.Max(0.1, native.Item3);
        double dmgEff = MercenaryTables.DamageEffectiveness(effect, merc.Level);

        int Get(uint stat) =>
            computed.StatValues.GetValueOrDefault(stat)
            + (supportStats?.GetValueOrDefault(stat) ?? 0)
            + (selfBuffStats?.GetValueOrDefault(stat) ?? 0)
            + skillStats.GetValueOrDefault(stat);

        // Read skill/support/buff-only stats for skill-local stats that shouldn't double-count
        int SkillOrSupp(uint stat) =>
            (supportStats?.GetValueOrDefault(stat) ?? 0) + skillStats.GetValueOrDefault(stat);

        bool isSpell = baseAttack.Spell;
        bool isAttack = !isSpell;
        var weapons = computed.Weapons;
        var hand = useOffHand && weapons.OffHand is not null ? weapons.OffHand : weapons.MainHand;
        bool isBow = isAttack && (hand?.IsBow == true || catalog.HasType(effect, "Bow"));
        bool isWand = isAttack && (hand?.IsWand == true || catalog.HasType(effect, "WandAttack"));
        bool isMelee = isAttack && !isBow && !isWand && (catalog.IsMelee(effect) || hand?.IsMelee == true || !baseAttack.Ranged);
        bool isProjectile = baseAttack.Projectile is not null || catalog.IsProjectile(effect) || isBow || isWand;
        bool isArea = baseAttack.AreaRadius > 0 || catalog.HasType(effect, "Area") || catalog.HasType(effect, "AreaSpell")
            || catalog.HasType(effect, "Rain") || catalog.HasType(effect, "Slam");
        bool isTrapOrMine = baseAttack.Trap is not null || catalog.HasType(effect, "Trapped") || catalog.HasType(effect, "RemoteMined");
        bool isSlam = catalog.HasType(effect, "Slam");

        // 1. Base Damage (weapon local damage * skill BaseMultiplier for attacks; native spell damage for spells)
        DamagePacket damage;
        if (isAttack && baseAttack.Use is null && !baseAttack.Damage.IsZero)
        {
            if (hand is not null && !hand.LocalDamage.IsZero)
            {
                damage = hand.LocalDamage.Scale(baseMult);
            }
            else if (Monsters.BaseDamage(variety, merc.Level) is var (bMin, bMax))
            {
                damage = DamagePacket.Of(DamageType.Physical, bMin, bMax).Scale(baseMult);
            }
            else
            {
                damage = baseAttack.Damage;
            }
        }
        else if (isAttack && baseAttack.Use is { Shape: MonsterSkillShape.Travel } && !baseAttack.Damage.IsZero)
        {
            damage = hand is not null && !hand.LocalDamage.IsZero
                ? hand.LocalDamage.Scale(baseMult)
                : baseAttack.Damage;
        }
        else
        {
            damage = baseAttack.Damage;
        }

        // 2. Flat Added Damage from gear, quivers, rings, amulets, gloves, supports, skill stats, Battlemage, and Flame Link
        if (!damage.IsZero)
        {
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                long minAdd = Get(Stat.Of($"global_minimum_added_{slug}_damage"));
                long maxAdd = Get(Stat.Of($"global_maximum_added_{slug}_damage"));
                if (isAttack)
                {
                    minAdd += Get(Stat.Of($"attack_minimum_added_{slug}_damage"));
                    maxAdd += Get(Stat.Of($"attack_maximum_added_{slug}_damage"));
                    if (hand is not null)
                    {
                        minAdd += Get(Stat.Of($"attack_minimum_added_{slug}_damage_with_weapons"));
                        maxAdd += Get(Stat.Of($"attack_maximum_added_{slug}_damage_with_weapons"));
                    }
                    if (isBow)
                    {
                        minAdd += Get(Stat.Of($"attack_minimum_added_{slug}_damage_with_bow"));
                        maxAdd += Get(Stat.Of($"attack_maximum_added_{slug}_damage_with_bow"));
                    }
                    if (isWand)
                    {
                        minAdd += Get(Stat.Of($"attack_minimum_added_{slug}_damage_with_wands"));
                        maxAdd += Get(Stat.Of($"attack_maximum_added_{slug}_damage_with_wands"));
                    }
                }
                else
                {
                    minAdd += Get(Stat.Of($"spell_minimum_added_{slug}_damage"));
                    maxAdd += Get(Stat.Of($"spell_maximum_added_{slug}_damage"));
                }
                if (type == DamageType.Fire && linkOff.FireMax > 0)
                {
                    minAdd += linkOff.FireMin;
                    maxAdd += linkOff.FireMax;
                }
                if (maxAdd > 0)
                {
                    double effMin = Math.Max(0, minAdd * dmgEff);
                    double effMax = Math.Max(effMin, maxAdd * dmgEff);
                    damage = damage.Add(type, new DamageRange(effMin, effMax));
                }
            }

            // Battlemage: spells gain % of main-hand weapon damage as added spell damage
            if (isSpell && weapons.MainHand is { } bmWeapon)
            {
                int bmPct = Get(Stat.SpellsYouCastGainPctOfBaseMainHandWeaponDamageAsAddedSpellDamage);
                if (bmPct > 0)
                    damage = damage.Add(bmWeapon.LocalDamage.Scale(bmPct / 100.0 * dmgEff));
            }

            // 3. Damage Conversion & Gain-as-Extra (PoE conversion order: Physical -> Lightning -> Cold -> Fire -> Chaos)
            int randEleGain = Get(Stat.PhysicalDamagePctToAddAsRandomElement);
            int randPerEle = randEleGain > 0 ? (int)Math.Round(randEleGain / 3.0) : 0;
            var conversion = new DamageConversion()
                .Add(DamageType.Physical, DamageType.Fire,
                    Get(Stat.SkillPhysicalDamagePctToConvertToFire) + Get(Stat.BasePhysicalDamagePctToConvertToFire))
                .Add(DamageType.Physical, DamageType.Cold,
                    Get(Stat.SkillPhysicalDamagePctToConvertToCold) + Get(Stat.BasePhysicalDamagePctToConvertToCold))
                .Add(DamageType.Physical, DamageType.Lightning,
                    Get(Stat.SkillPhysicalDamagePctToConvertToLightning) + Get(Stat.BasePhysicalDamagePctToConvertToLightning))
                .Add(DamageType.Physical, DamageType.Chaos,
                    Get(Stat.SkillPhysicalDamagePctToConvertToChaos) + Get(Stat.BasePhysicalDamagePctToConvertToChaos))
                .Add(DamageType.Cold, DamageType.Fire, Get(Stat.BaseColdDamagePctToConvertToFire))
                .Add(DamageType.Lightning, DamageType.Chaos, Get(Stat.SkillLightningDamagePctToConvertToChaos))
                .AddGain(DamageType.Physical, DamageType.Fire,
                    Get(Stat.PhysicalDamagePctToAddAsFire) + randPerEle + (isWand ? Get(Stat.WandPhysicalDamagePctToAddAsFire) : 0))
                .AddGain(DamageType.Physical, DamageType.Cold,
                    Get(Stat.PhysicalDamagePctToAddAsCold) + randPerEle + (isWand ? Get(Stat.WandPhysicalDamagePctToAddAsCold) : 0))
                .AddGain(DamageType.Physical, DamageType.Lightning,
                    Get(Stat.PhysicalDamagePctToAddAsLightning) + randPerEle + (isWand ? Get(Stat.WandPhysicalDamagePctToAddAsLightning) : 0))
                .AddGain(DamageType.Physical, DamageType.Chaos, Get(Stat.PhysicalDamagePctToAddAsChaos))
                .AddGain(DamageType.Cold, DamageType.Fire, Get(Stat.ColdDamagePctToAddAsFire));

            int eleAsChaos = Get(Stat.ElementalDamagePctToAddAsChaos);
            if (eleAsChaos > 0)
            {
                conversion = conversion
                    .AddGain(DamageType.Fire, DamageType.Chaos, eleAsChaos)
                    .AddGain(DamageType.Cold, DamageType.Chaos, eleAsChaos)
                    .AddGain(DamageType.Lightning, DamageType.Chaos, eleAsChaos);
            }

            damage = damage.Convert(conversion);

            int nonChaosAsChaos = Get(Stat.NonChaosDamageToAddAsChaosDamagePct);
            if (nonChaosAsChaos > 0)
            {
                double cMin = 0, cMax = 0;
                foreach (var (t, r) in damage.Components)
                    if (t != DamageType.Chaos) { cMin += r.Min * nonChaosAsChaos / 100.0; cMax += r.Max * nonChaosAsChaos / 100.0; }
                if (cMax > 0) damage = damage.Add(DamageType.Chaos, new DamageRange(cMin, cMax));
            }

            // 4. Additive Increased Damage bucket + Multiplicative More Multipliers per damage type
            bool spiritualAid = Get(Stat.MinionDamageIncreasesAndReductionsAlsoAffectsYou) != 0;
            int commonInc = Get(Stat.DamagePlusPct) + (spiritualAid ? Get(Stat.MinionDamagePlusPct) : 0);
            int spellInc = Get(Stat.SpellDamagePlusPct);
            if (isSpell)
            {
                commonInc += spellInc;
            }
            else
            {
                commonInc += Get(Stat.AttackDamagePlusPct);
                int spellToAttackPct = Get(Stat.ActiveSkillAdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue);
                if (spellToAttackPct > 0 && spellInc != 0)
                    commonInc += (int)Math.Round(spellInc * spellToAttackPct / 100.0);
                if (isMelee) commonInc += Get(Stat.MeleeDamagePlusPct);
                if (isBow) commonInc += Get(Stat.BowDamagePlusPct);
                if (isWand) commonInc += Get(Stat.WandDamagePlusPct);
            }
            if (isProjectile) commonInc += Get(Stat.ProjectileDamagePlusPct);
            if (isArea) commonInc += Get(Stat.AreaDamagePlusPct);
            if (isTrapOrMine) commonInc += Get(Stat.TrapDamagePlusPct) + Get(Stat.MineDamagePlusPct);

            // Common More multipliers (Luminary + skill/support *_final)
            double commonMore = 1.0;
            if (merc.IsPermanent)
                commonMore *= LuminaryBonusesOf(player).MercenaryDamageMoreFactor(computed.EquippedUniqueCount);

            static double MulMore(double acc, int pct) => pct != 0 ? acc * Math.Max(0.0, 1.0 + pct / 100.0) : acc;

            // Avoid double-applying ActiveSkillDamagePlusPctFinal if BossKits.AttackFor already baked it into a fallback hit
            bool rebuiltFromWeapon = isAttack && hand is not null && !hand.LocalDamage.IsZero;
            if (rebuiltFromWeapon || isSpell)
                commonMore = MulMore(commonMore, SkillOrSupp(Stat.ActiveSkillDamagePlusPctFinal));
            if (isAttack)
                commonMore = MulMore(commonMore, Get(Stat.ActiveSkillAttackDamagePlusPctFinal));
            else
                commonMore = MulMore(commonMore, Get(Stat.ActiveSkillSpellDamagePlusPctFinal));
            if (isProjectile)
            {
                commonMore = MulMore(commonMore, Get(Stat.ActiveSkillProjectileDamagePlusPctFinal));
                commonMore = MulMore(commonMore, Get(Stat.SupportMultipleProjectileDamagePlusPctFinal));
            }
            if (isArea)
            {
                commonMore = MulMore(commonMore, Get(Stat.SupportAreaConcentrateAreaDamagePlusPctFinal));
                if (isMelee) commonMore = MulMore(commonMore, Get(Stat.SupportPulveriseMeleeAreaDamagePlusPctFinal));
            }
            if (isMelee) commonMore = MulMore(commonMore, Get(Stat.SupportMultipleAttackDamagePlusPctFinal));
            if (isTrapOrMine) commonMore = MulMore(commonMore, Get(Stat.SupportTrapAndMineDamagePlusPctFinal));
            if (isSlam) commonMore = MulMore(commonMore, Get(Stat.SupportAncestralSlamBigHitDamageWithHitsAndAilmentsPlusPctFinal));
            commonMore = MulMore(commonMore, Get(Stat.SupportBetterAilmentsHitDamagePlusPctFinal));
            if (target is not null && target.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0)
                commonMore = MulMore(commonMore, Get(Stat.SupportHypothermiaDamagePlusPctVsChilledEnemiesFinal));

            bool noEle = Get(Stat.DealNoElementalDamage) != 0;
            bool noChaos = Get(Stat.BaseDealNoChaosDamage) != 0;

            var finalDamage = DamagePacket.Empty;
            foreach (var (type, range) in damage.Components)
            {
                if (range.Max <= 0) continue;
                if (noEle && DamageTypes.IsElemental(type)) continue;
                if (noChaos && type == DamageType.Chaos) continue;

                int typeInc = commonInc;
                double typeMore = commonMore;
                switch (type)
                {
                    case DamageType.Physical:
                        typeInc += Get(Stat.PhysicalDamagePlusPct);
                        if (isMelee)
                        {
                            typeInc += Get(Stat.MeleePhysicalDamagePlusPct);
                            if (hand is { IsTwoHand: true }) typeInc += Get(Stat.TwoHandedMeleePhysicalDamagePlusPct);
                            else if (hand is not null) typeInc += Get(Stat.OneHandedMeleePhysicalDamagePlusPct);
                            if (weapons.IsDualWielding) typeInc += Get(Stat.PhysicalDamageWhileDualWieldingPlusPct);
                            typeMore = MulMore(typeMore, Get(Stat.SupportMeleePhysicalDamagePlusPctFinal));
                        }
                        if (isBow) typeInc += Get(Stat.PhysicalBowDamagePlusPct);
                        if (isWand) typeInc += Get(Stat.PhysicalWandDamagePlusPct);
                        typeMore = MulMore(typeMore, Get(Stat.SupportBrutalityPhysicalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillPhysicalDamagePlusPctFinal));
                        break;
                    case DamageType.Fire:
                        typeInc += Get(Stat.FireDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
                            + (isAttack ? Get(Stat.WeaponElementalDamagePlusPct) : 0);
                        typeMore = MulMore(typeMore, Get(Stat.SupportGemElementalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillElementalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillFireDamagePlusPctFinal));
                        if (isAttack)
                        {
                            typeMore = MulMore(typeMore, Get(Stat.SupportAttackSkillsElementalDamagePlusPctFinal));
                            typeMore = MulMore(typeMore, Get(Stat.SupportWeaponElementalDamagePlusPctFinal));
                        }
                        break;
                    case DamageType.Cold:
                        typeInc += Get(Stat.ColdDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
                            + (isAttack ? Get(Stat.WeaponElementalDamagePlusPct) : 0);
                        typeMore = MulMore(typeMore, Get(Stat.SupportGemElementalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillElementalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillColdDamagePlusPctFinal));
                        if (isAttack)
                        {
                            typeMore = MulMore(typeMore, Get(Stat.SupportAttackSkillsElementalDamagePlusPctFinal));
                            typeMore = MulMore(typeMore, Get(Stat.SupportWeaponElementalDamagePlusPctFinal));
                        }
                        break;
                    case DamageType.Lightning:
                        typeInc += Get(Stat.LightningDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
                            + (isAttack ? Get(Stat.WeaponElementalDamagePlusPct) : 0);
                        typeMore = MulMore(typeMore, Get(Stat.SupportGemElementalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillElementalDamagePlusPctFinal));
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillLightningDamagePlusPctFinal));
                        if (isAttack)
                        {
                            typeMore = MulMore(typeMore, Get(Stat.SupportAttackSkillsElementalDamagePlusPctFinal));
                            typeMore = MulMore(typeMore, Get(Stat.SupportWeaponElementalDamagePlusPctFinal));
                        }
                        break;
                    case DamageType.Chaos:
                        typeInc += Get(Stat.ChaosDamagePlusPct);
                        typeMore = MulMore(typeMore, Get(Stat.ActiveSkillChaosDamagePlusPctFinal));
                        break;
                }

                double factor = Math.Max(0.0, 1.0 + typeInc / 100.0) * typeMore;
                double lo = Math.Max(1.0, Math.Round(range.Min * factor));
                double hi = Math.Max(lo, Math.Round(range.Max * factor));
                finalDamage = finalDamage.Add(type, new DamageRange(lo, hi));
            }
            damage = finalDamage;
        }

        // 5. Attack / Cast / Trap / Warcry Speed Scaling
        double baseSec;
        double speedInc;
        double speedMore = 1.0;
        if (isTrapOrMine)
        {
            baseSec = baseAttack.Seconds;
            speedInc = Get(Stat.TrapThrowingSpeedPlusPct) + Get(Stat.MineLayingSpeedPlusPct);
            speedMore = Math.Max(0.2, 1.0 + Get(Stat.SupportTrapAndMineDamageTrapThrowingSpeedPlusPctFinal) / 100.0);
        }
        else if (catalog.IsWarcry(effect))
        {
            baseSec = catalog.CastTimeMs(effect) is > 0 and var wcMs ? wcMs / 1000.0 : baseAttack.Seconds;
            speedInc = Get(Stat.WarcrySpeedPlusPct);
        }
        else if (isSpell)
        {
            baseSec = catalog.CastTimeMs(effect) is > 0 and var ctMs ? ctMs / 1000.0 : variety.AttackMs / 1000.0;
            speedInc = Get(Stat.BaseCastSpeedPlusPct) + Get(Stat.CastSpeedPlusPct) + Get(Stat.AttackAndCastSpeedPlusPct);
            speedMore = Math.Max(0.2, 1.0 + Get(Stat.ActiveSkillCastSpeedPlusPctFinal) / 100.0);
        }
        else
        {
            // Weapon base attack time (+ Leap Slam additional_weapon_base_attack_time_ms)
            int weaponMs = hand?.AttackMs ?? variety.AttackMs;
            int extraMs = Get(Stat.AdditionalWeaponBaseAttackTimeMs);
            baseSec = Math.Max(0.15, (weaponMs + extraMs) / 1000.0);
            speedInc = Get(Stat.AttackSpeedPlusPct) + Get(Stat.AttackAndCastSpeedPlusPct)
                + (isMelee ? Get(Stat.MeleeAttackSpeedPlusPct) : 0)
                + (isBow ? Get(Stat.BowAttackSpeedPlusPct) : 0)
                + (isWand ? Get(Stat.WandAttackSpeedPlusPct) : 0)
                + (weapons.IsDualWielding ? Get(Stat.AttackSpeedWhileDualWieldingPlusPct) : 0);
            if (weapons.IsDualWielding) speedMore *= 1.10; // PoE inherent 10% more dual-wield attack speed
            int geplSpeedMult = MercenaryTables.AttackSpeedMultiplierPct(effect, merc.Level);
            if (geplSpeedMult != 0) speedMore *= Math.Max(0.2, 1.0 + geplSpeedMult / 100.0);
            speedMore *= Math.Max(0.2, 1.0 + Get(Stat.ActiveSkillAttackSpeedPlusPctFinal) / 100.0);
            if (isMelee)
            {
                speedMore *= Math.Max(0.2, 1.0 + Get(Stat.SupportMultipleAttacksMeleeAttackSpeedPlusPctFinal) / 100.0);
                speedMore *= Math.Max(0.2, 1.0 + Get(Stat.SupportPulveriseAttackSpeedPlusPctFinal) / 100.0);
            }
        }
        double scaledSeconds = Math.Clamp(baseSec / Math.Max(0.2, 1.0 + speedInc / 100.0) / Math.Max(0.2, speedMore), 0.15, 4.0);

        // 6. Area, Projectiles, Penetration, Crit, & Special Support Modifiers
        double aoeInc = 1.0 + Get(Stat.BaseSkillAreaOfEffectPlusPct) / 100.0;
        double aoeMore = 1.0 + Get(Stat.SupportConcentratedEffectSkillAreaOfEffectPlusPctFinal) / 100.0;
        double areaScale = Math.Sqrt(Math.Clamp(aoeInc * aoeMore, 0.2, 6.0));
        double scaledArea = baseAttack.AreaRadius > 0 ? baseAttack.AreaRadius * areaScale : 0;

        int extraProj = Math.Max(0, computed.StatValues.GetValueOrDefault(Stat.NumberOfAdditionalProjectiles)
            + (supportStats?.GetValueOrDefault(Stat.NumberOfAdditionalProjectiles) ?? 0)
            + (isBow ? Get(Stat.NumberOfAdditionalArrows) : 0));
        bool projNova = Get(Stat.ProjectilesNova) != 0;
        var scaledProj = baseAttack.Projectile;
        if (scaledProj is not null && (extraProj > 0 || projNova || areaScale != 1.0))
        {
            int nextCount = Math.Clamp(Math.Max(1, scaledProj.Count) + extraProj, 1, 24);
            double nextSpread = projNova ? Math.Tau
                : nextCount > 1 && scaledProj.SpreadRadians == 0 ? 0.45 : scaledProj.SpreadRadians;
            scaledProj = scaledProj with
            {
                Count = nextCount,
                SpreadRadians = nextSpread,
                AreaRadius = scaledProj.AreaRadius > 0 ? scaledProj.AreaRadius * areaScale : scaledProj.AreaRadius,
            };
        }

        double scaledRange = baseAttack.Range;
        if (isMelee && hand is not null && baseAttack.Use is null && !baseAttack.Ranged)
            scaledRange = Math.Max(scaledRange, hand.Range);
        if (baseAttack.AreaAtSelf && scaledArea > 0)
            scaledRange = Math.Max(scaledRange, scaledArea);
        if ((isBow || isWand || scaledProj is not null) && scaledRange < 45)
            scaledRange = 45;

        int hits = Math.Max(1, baseAttack.Hits + (isMelee ? Math.Max(0, Get(Stat.BaseMeleeAttackRepeatCount)) : 0));

        // Base Critical Strike Chance (permyriad)
        int baseCrit;
        if (isAttack && linkOff.MainHandCrit is int linkedCrit)
            baseCrit = linkedCrit;
        else if (isSpell)
            baseCrit = Math.Max(0, (catalog.SpellCritChance(effect, merc.Level) ?? MonsterModEffects.DefaultBaseCriticalChance)
                + Get(Stat.BaseSpellCriticalStrikeChance));
        else
            baseCrit = Math.Max(0, (hand?.BaseCritPermyriad ?? MonsterModEffects.DefaultBaseCriticalChance)
                + (catalog.AttackCritChance(effect, merc.Level) ?? 0));

        bool rt = Get(Stat.ResoluteTechnique) != 0;
        int elePen = Get(Stat.ReduceEnemyElementalResistancePct);
        var pen = new Penetration.PerType(
            Fire: Get(Stat.BaseReduceEnemyFireResistancePct) + Get(Stat.ReduceEnemyFireResistancePct) + elePen,
            Cold: Get(Stat.BaseReduceEnemyColdResistancePct) + Get(Stat.ReduceEnemyColdResistancePct) + elePen,
            Lightning: Get(Stat.BaseReduceEnemyLightningResistancePct) + Get(Stat.ReduceEnemyLightningResistancePct) + elePen,
            Chaos: Get(Stat.ReduceEnemyChaosResistancePct));

        bool ignorePhys = Get(Stat.HitsIgnoreEnemyMonsterPhysicalDamageReductionPctChance) is int ignPct and > 0
            && (target is null || AreaRng(player).Next(100) < ignPct);

        var scaledTrap = baseAttack.Trap is { } trap
            ? trap with
            {
                Damage = damage,
                AreaRadius = trap.AreaRadius > 0 ? trap.AreaRadius * areaScale : 0,
                Projectile = trap.Projectile is { } tp
                    ? tp with
                    {
                        Count = Math.Clamp(Math.Max(1, tp.Count) + extraProj, 1, 24),
                        SpreadRadians = projNova ? Math.Tau : tp.SpreadRadians,
                    } : null,
            }
            : null;

        var scaledAttack = baseAttack with
        {
            Damage = damage,
            Seconds = scaledSeconds,
            Range = scaledRange,
            AreaRadius = scaledArea,
            Projectile = scaledProj,
            Trap = scaledTrap,
            Hits = hits,
            DualWielding = weapons.IsDualWielding,
        };

        return new CompanionOffence(
            Attack: scaledAttack,
            BaseCritPermyriad: baseCrit,
            Penetration: pen,
            CannotCrit: rt,
            IgnoreEvasion: rt,
            IgnorePhysicalReduction: ignorePhys,
            CannotInflictAilments: Get(Stat.CannotInflictStatusAilments) != 0,
            WitherChancePct: Get(Stat.WitheredOnHitChancePct),
            CoverInAshChancePct: Get(Stat.CoverInAshOnHitPct),
            CrushChancePct: Get(Stat.ChanceToCrushOnHitPct),
            AdditionalChains: Math.Max(0, Get(Stat.NumberOfChains)),
            PierceCount: Math.Max(0, Get(Stat.ProjectileBaseNumberOfTargetsToPierce)),
            AlwaysPierce: Get(Stat.AlwaysPierce) != 0,
            ProjectilesReturn: Get(Stat.ProjectilesReturn) != 0,
            ProjectilesNova: projNova,
            ProjectileSpeedPct: Get(Stat.BaseProjectileSpeedPlusPct) + Get(Stat.ProjectileSpeedPlusPct),
            AreaScaleFactor: areaScale,
            ExtraTrapsToThrow: Math.Max(0, Get(Stat.NumberOfAdditionalTrapsToThrow)),
            ExtraTrapsAllowed: Math.Max(0, Get(Stat.NumberOfAdditionalTrapsAllowed)),
            ExtraTotemsAllowed: Math.Max(0, Get(Stat.BaseNumberOfTotemsAllowed)));
    }

    // ---- Companion Tick (AI, Follow/Teleport, Regen/Recoup/ES, Auras, Skills, Strikes, Traps, Grounds, Summons) ---------------

    async Task TickCompanion(Player player, double now)
    {
        if (player.Closed || player.LeavingArea || player.Paused) return;

        List<uint>? removals = null;
        List<(uint Id, byte[] Create, byte[] Wake)>? spawns = null;
        List<(uint Id, byte[] Packet)>? trapDetonations = null;
        bool launchedProjectile = false;

        using (ProjectileMutation(player))
        {
            if (!companions.TryGetValue(player, out var comp) || comp.Closed) return;
            var m = comp.Monster;
            if (player.Closed || player.LeavingArea) return;
            if (m.Dead || m.Life == 0 || !player.Area.Monsters.TryGetValue(comp.EntityId, out var held) || !ReferenceEquals(held, m))
            {
                ClearCompanionSharedAuras(player, comp.EntityId);
                ClearCompanionTauntsLocked(player, comp.EntityId);
                ClearCompanionSelfBuffs(player, comp);
                return;
            }

            double elapsed = Math.Max(0, now - comp.LastTick);
            comp.LastTick = now;

            // 1. Expire self-buffs & guard shield
            ExpireCompanionSelfBuffsLocked(player, comp, now);

            // 2. Life regeneration, Loyal Bodyguard Recoup, Out-of-Combat Regen, Energy Shield recharge
            bool lifeChanged = StepCompanionResourcesLocked(comp, now, elapsed);
            if (lifeChanged)
                player.Minions.OutputQueue.Add(new([], null, comp.EntityId, m, PlayerMinions.OutputKind.Life));
            if (comp.ShieldPool.Current != comp.LastSentEs)
            {
                comp.LastSentEs = comp.ShieldPool.Current;
                player.Area.Life.Set(comp.EntityId, AreaLife.EnergyShieldPool, comp.ShieldPool.Current);
            }

            // 3. Non-curse aura sharing (scaled by Bestowed Knighthood) + Blasphemy curse auras
            if (comp.SharedAuras.Length > 0 && player.Companion is { } ac && ac.EntityId == comp.EntityId)
                SyncCompanionSharedAuras(player, ac, comp.SharedAuras, now, scaleByLuminary: true);
            if (comp.BlasphemyAuras.Count > 0 && now - comp.LastBlasphemySync >= 0.4)
            {
                comp.LastBlasphemySync = now;
                SyncCompanionBlasphemyAurasLocked(player, comp, now);
            }

            // 4. Tick companion-owned delayed strikes, traps, grounds, and totems/summons
            launchedProjectile |= StepCompanionHazardsLocked(player, comp, now, ref trapDetonations, ref removals);

            // Keep NPC click coordinates synchronized with current walk position
            var here = m.Walk.Position(now);
            lock (player.Npcs)
                if (player.Npcs.TryGetValue(comp.EntityId, out var npc) && (npc.X != here.X || npc.Y != here.Y))
                    player.Npcs[comp.EntityId] = npc with { X = here.X, Y = here.Y };

            if (player.PlayerLife == 0) goto FlushAsync;

            // 5. Resolve pending skill contact
            if (comp.PendingAttack is { } pending)
            {
                if (pending.Generation != m.ActionGeneration)
                {
                    comp.PendingAttack = null;
                }
                else if (now >= pending.ContactAt)
                {
                    comp.PendingAttack = null;
                    launchedProjectile |= CommitCompanionSkillLocked(player, comp, pending, now, ref spawns, ref removals);
                }
                else goto FlushAsync;
            }

            if (now < m.BusyUntil || now < m.FlinchUntil) goto FlushAsync;
            double actionRate = MonsterActionFactor(m, now);
            if (actionRate <= 0) goto FlushAsync;

            var ownerAt = player.Walk.Position(now);

            // 6. Teleport to player if too far away
            if (Dist(here, ownerAt) > CompanionTeleportDistance)
            {
                var dest = NextToPlayer(player, LivingCollision.Size(m), comp.EntityId);
                m.Walk = player.NewMonsterWalk(comp.EntityId, dest.X, dest.Y, m.Walk.Speed, LivingCollision.Size(m), false);
                m.ActionGeneration++;
                m.LastTarget = null;
                player.Minions.OutputQueue.Add(new(
                    InstanceProtocol.Teleport(comp.EntityId, dest.X, dest.Y),
                    "mercenary teleports to player", comp.EntityId, m, WorldId: comp.EntityId));
                goto FlushAsync;
            }

            if (IsTownOrHideout(player))
            {
                FollowOwnerLocked(player, comp, here, ownerAt, now);
                goto FlushAsync;
            }

            // 7. Find nearest hostile target within aggro/owner leash range
            var target = FindCompanionTargetLocked(player, here, ownerAt, now);
            if (target is not { } tgt)
            {
                FollowOwnerLocked(player, comp, here, ownerAt, now);
                goto FlushAsync;
            }

            // 8. Pick a ready skill from the mercenary's kit (buffs, warcries, curses/marks, totems, travel, specials, LRU 0-cd)
            double dist = Dist(here, tgt.At);
            var (chosen, travelLanding) = ChooseCompanionSkillLocked(player, comp, here, dist, tgt.Monster, tgt.At, now);
            if (chosen is null)
            {
                double desiredRange = DesiredCompanionRange(comp);
                double factor = dist > 0 ? Math.Max(0, desiredRange - 2) / dist : 0;
                var approach = ((int)Math.Round(tgt.At.X + (here.X - tgt.At.X) * factor),
                    (int)Math.Round(tgt.At.Y + (here.Y - tgt.At.Y) * factor));
                MoveCompanionLocked(player, comp, here, approach, now);
                goto FlushAsync;
            }

            // 9. Start the skill action
            StartCompanionSkillLocked(player, comp, chosen, here, tgt.Id, tgt.Monster, tgt.At, travelLanding, now, actionRate);
        }

    FlushAsync:
        if (spawns is { Count: > 0 } && !player.Closed && !player.LeavingArea)
        {
            foreach (var (objId, create, wake) in spawns)
            {
                await SendWorldCreate(player, objId, () => create, null, wake: false, awaitBirth: false);
                await SendWorldRecord(player, objId, wake, null);
            }
        }
        if (trapDetonations is { Count: > 0 } && !player.Closed && !player.LeavingArea)
        {
            foreach (var (trapId, packet) in trapDetonations)
                await SendWorldRecord(player, trapId, packet, null);
        }
        if (removals is { Count: > 0 } && !player.Closed && !player.LeavingArea)
        {
            foreach (uint objId in removals)
                await SendWorldRemoval(player, objId, $"0x02bd remove mercenary object 0x{objId:x}");
        }
        if (launchedProjectile)
            ProjectilesFor(player).Advance();
    }

    static void ExpireCompanionSelfBuffsLocked(Player player, CompanionRuntime comp, double now)
    {
        if (comp.GuardPoolRemaining > 0 && now >= comp.GuardUntil)
        {
            comp.GuardPoolRemaining = 0;
            comp.GuardAbsorbPct = 0;
        }
        if (comp.ActiveSelfBuffs.Count == 0) return;
        bool changed = false;
        for (int i = comp.ActiveSelfBuffs.Count - 1; i >= 0; i--)
        {
            var b = comp.ActiveSelfBuffs[i];
            if (now < b.Until) continue;
            comp.ActiveSelfBuffs.RemoveAt(i);
            if (b.BuffRow > 0)
                player.Area.Buffs.Clear(comp.EntityId, b.BuffRow, comp.EntityId, tag: b.Tag);
            changed = true;
        }
        if (changed) RebuildCompanionBuffStatsLocked(comp);
    }

    static void RebuildCompanionBuffStatsLocked(CompanionRuntime comp)
    {
        if (comp.ActiveSelfBuffs.Count == 0)
        {
            comp.Monster.BuffStats = null;
            return;
        }
        var sum = new Dictionary<uint, int>();
        foreach (var b in comp.ActiveSelfBuffs)
            foreach (var (k, v) in b.Stats)
                sum[k] = ActorScale.Saturate((long)sum.GetValueOrDefault(k) + v);
        comp.Monster.BuffStats = sum;
    }

    static void SyncCompanionBlasphemyAurasLocked(Player player, CompanionRuntime comp, double now)
    {
        var here = comp.Monster.Walk.Position(now);
        foreach (var aura in comp.BlasphemyAuras)
        {
            foreach (var (_, enemy) in player.Area.MonstersNear(here, aura.Radius + 4))
            {
                if (!MinionMayTarget(enemy)) continue;
                if (Dist(here, enemy.Walk.Position(now)) > aura.Radius) continue;
                ScriptedAllyApplyCurseLocked(player, comp.EntityId, enemy, 0, aura.CanonicalName,
                    aura.Stats, now, comp.Computed.StatValues, aura: true);
            }
        }
    }

    bool StepCompanionHazardsLocked(Player player, CompanionRuntime comp, double now,
        ref List<(uint Id, byte[] Packet)>? trapDetonations, ref List<uint>? removals)
    {
        bool launchedProjectile = false;

        // 1. Delayed Strikes (e.g. Storm Call / Frost Bomb)
        for (int i = comp.ActiveStrikes.Count - 1; i >= 0; i--)
        {
            var strike = comp.ActiveStrikes[i];
            if (now < strike.StrikeAt) continue;
            comp.ActiveStrikes.RemoveAt(i);
            if (strike.HasVisual && strike.MarkerId != 0)
                (removals ??= []).Add(strike.MarkerId);
            ApplyCompanionAreaDamageLocked(player, comp, strike.Effect, strike.Attack, strike.At, strike.Radius, now,
                useOffHand: false, useOverrideBase: true);
        }

        // 2. Traps (e.g. Lightning Trap, Ice Trap, Bear Trap, Blade Trap)
        for (int i = comp.ActiveTraps.Count - 1; i >= 0; i--)
        {
            var trap = comp.ActiveTraps[i];
            if (now >= trap.ExpiresAt)
            {
                comp.ActiveTraps.RemoveAt(i);
                if (trap.HasVisual) (removals ??= []).Add(trap.Id);
                continue;
            }
            if (now < trap.ArmedAt) continue;

            SpawnedMonster? trigger = null;
            uint triggerId = 0;
            (int X, int Y) triggerAt = trap.At;
            foreach (var (eid, enemy) in player.Area.MonstersNear(trap.At, trap.TriggerRadius + 4))
            {
                if (!MinionMayTarget(enemy)) continue;
                var eat = enemy.Walk.Position(now);
                if (Dist(trap.At, eat) <= trap.TriggerRadius + Math.Max(0, LivingCollision.Size(enemy)))
                {
                    trigger = enemy;
                    triggerId = eid;
                    triggerAt = eat;
                    break;
                }
            }
            if (trigger is null) continue;

            comp.ActiveTraps.RemoveAt(i);
            if (trap.HasVisual)
            {
                (trapDetonations ??= []).Add((trap.Id, InstanceProtocol.ActionStarted(
                    trap.Id, trap.At.X, trap.At.Y, triggerAt.X, triggerAt.Y,
                    0x4000, 0xffff, player.MonsterOrderFlags)));
                (removals ??= []).Add(trap.Id);
            }

            var payload = trap.Payload;
            if (payload.Projectile is { } trapProj)
            {
                launchedProjectile |= LaunchCompanionProjectileLocked(player, comp, trap.Effect, payload, trapProj,
                    trap.At, triggerAt, now, useOffHand: false, useOverrideBase: true);
            }
            else if (payload.AreaRadius > 0)
            {
                double radius = EvaluateForEffectLocked(player, comp, trap.Effect, payload, null, now,
                    useOffHand: false, useOverrideBase: true).Attack.AreaRadius;
                ApplyCompanionAreaDamageLocked(player, comp, trap.Effect, payload, trap.At, Math.Max(payload.AreaRadius, radius), now,
                    useOffHand: false, useOverrideBase: true);
            }
            else
            {
                var off = EvaluateForEffectLocked(player, comp, trap.Effect, payload, trigger, now, useOffHand: false, useOverrideBase: true);
                CommitCompanionSingleHitLocked(player, comp, triggerId, trigger, off, now, isProjectile: false);
            }
        }

        // 3. Ground Effects (chilled ground, burning/caustic ground, Flame Wall)
        for (int i = comp.ActiveGrounds.Count - 1; i >= 0; i--)
        {
            var g = comp.ActiveGrounds[i];
            if (now >= g.ExpiresAt)
            {
                comp.ActiveGrounds.RemoveAt(i);
                continue;
            }
            if (now < g.NextTickAt) continue;
            g.NextTickAt = now + 0.5;
            if (!g.Attack.Damage.IsZero)
            {
                ApplyCompanionAreaDamageLocked(player, comp, g.Effect, g.Attack, g.At, g.Radius, now,
                    useOffHand: false, useOverrideBase: true, damageScale: 0.35);
            }
        }

        // 4. Totems & Summons (periodically attack nearby hostiles)
        for (int i = comp.ActiveSummons.Count - 1; i >= 0; i--)
        {
            var s = comp.ActiveSummons[i];
            if (now >= s.ExpiresAt)
            {
                comp.ActiveSummons.RemoveAt(i);
                continue;
            }
            if (now < s.NextActAt) continue;
            s.NextActAt = now + Math.Max(0.4, s.BodyAttack.Seconds);

            var tgt = FindCompanionTargetLocked(player, s.At, player.Walk.Position(now), now);
            if (tgt is not { } enemy) continue;
            if (Dist(s.At, enemy.At) > Math.Max(45, s.BodyAttack.Range) + LivingCollision.Size(enemy.Monster)) continue;

            if (s.BodyAttack.Projectile is { } proj)
                launchedProjectile |= LaunchCompanionProjectileLocked(player, comp, s.SourceEffect, s.BodyAttack, proj,
                    s.At, enemy.At, now, useOffHand: false, useOverrideBase: true);
            else if (s.BodyAttack.AreaRadius > 0)
            {
                double radius = EvaluateForEffectLocked(player, comp, s.SourceEffect, s.BodyAttack, null, now,
                    useOffHand: false, useOverrideBase: true).Attack.AreaRadius;
                ApplyCompanionAreaDamageLocked(player, comp, s.SourceEffect, s.BodyAttack,
                    s.BodyAttack.AreaAtSelf ? s.At : enemy.At, Math.Max(s.BodyAttack.AreaRadius, radius), now,
                    useOffHand: false, useOverrideBase: true);
            }
            else
            {
                var off = EvaluateForEffectLocked(player, comp, s.SourceEffect, s.BodyAttack, enemy.Monster, now, useOffHand: false, useOverrideBase: true);
                CommitCompanionSingleHitLocked(player, comp, enemy.Id, enemy.Monster, off, now, isProjectile: s.BodyAttack.Ranged);
            }
        }

        return launchedProjectile;
    }

    static bool StepCompanionResourcesLocked(CompanionRuntime comp, double now, double elapsed)
    {
        if (elapsed <= 0) return false;
        var m = comp.Monster;
        double healing = 0;

        // Base + gear + extra-stats + active self-buff (e.g. Enduring Cry) life regeneration
        double regenPctPerMin = m.Effects[Stat.LifeRegenerationRatePerMinutePct];
        double regenFlatPerMin = m.Effects[Stat.BaseLifeRegenerationRatePerMinute];
        healing += elapsed * (m.MaxLife * regenPctPerMin / 6000.0 + regenFlatPerMin / 60.0);

        // Out-of-combat mercenary regeneration (4s without taking damage)
        if (m.Life < m.MaxLife && now - comp.LastDamagedAt >= OutOfCombatDelaySeconds)
            healing += elapsed * (m.MaxLife * OutOfCombatRegenPerSecondFraction);

        // Loyal Bodyguard 4-second recoup ticks
        healing += comp.Recoup.Take(now);

        // Energy Shield recharge
        comp.ShieldPool.Advance(now - elapsed, now, stat => m.Effects[stat], alive: true);

        if (healing <= 0 || m.Life >= m.MaxLife || m.Effects[Stat.CannotRecoverLife] != 0) return false;
        comp.RegenCarry += healing;
        int whole = (int)Math.Floor(comp.RegenCarry);
        if (whole <= 0) return false;
        comp.RegenCarry -= whole;
        uint before = m.Life;
        m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + whole);
        return m.Life != before;
    }

    // ---- Movement, Targeting, Skill Selection & Rotation ----------------------------------------------------------------------

    static void FollowOwnerLocked(Player player, CompanionRuntime comp, (int X, int Y) here, (int X, int Y) ownerAt, double now)
    {
        if (Dist(here, ownerAt) <= CompanionFollowDistance) return;
        var dest = NextToPlayer(player, LivingCollision.Size(comp.Monster), comp.EntityId);
        MoveCompanionLocked(player, comp, here, dest, now);
    }

    static void MoveCompanionLocked(Player player, CompanionRuntime comp, (int X, int Y) here, (int X, int Y) to, double now)
    {
        var m = comp.Monster;
        if (m.LastTarget is { } last && Math.Abs(last.X - to.X) + Math.Abs(last.Y - to.Y) < 3 && now < m.Walk.ArrivesAt)
            return;
        m.LastTarget = to;
        m.Walk.Order(to.X, to.Y, now);
        player.Minions.OutputQueue.Add(new(
            InstanceProtocol.ActionStarted(comp.EntityId, here.X, here.Y, to.X, to.Y, 0x2909,
                0xffff, player.MonsterOrderFlags),
            null, comp.EntityId, m, WorldId: comp.EntityId));
    }

    static (uint Id, SpawnedMonster Monster, (int X, int Y) At)? FindCompanionTargetLocked(
        Player player, (int X, int Y) here, (int X, int Y) ownerAt, double now)
    {
        (uint Id, SpawnedMonster Monster, (int X, int Y) At)? best = null;
        double bestDist = double.MaxValue;
        foreach (var (id, enemy) in player.Area.Monsters)
        {
            if (!MinionMayTarget(enemy)) continue;
            var at = enemy.Walk.Position(now);
            double d = Dist(here, at);
            if (d >= bestDist || d > CompanionAggroRange || Dist(ownerAt, at) > CompanionOwnerMaxRange) continue;
            bestDist = d;
            best = (id, enemy, at);
        }
        return best;
    }

    /// <summary>Preferred engagement distance from primary build skills: ranged/caster mercs stay at range rather than
    /// collapsing into melee because of a fallback swing.</summary>
    static double DesiredCompanionRange(CompanionRuntime comp)
    {
        double bestNonFallback = 0;
        double anyFallback = 0;
        foreach (var k in comp.Kit)
        {
            if (k.TriggerThreshold > 0 || k.Attack.Use is not null) continue;
            if (k.Effect == MercenaryTables.MeleeFallbackEffect)
            {
                anyFallback = Math.Max(anyFallback, k.Attack.Range);
                continue;
            }
            if (k.Attack.Range > bestNonFallback) bestNonFallback = k.Attack.Range;
        }
        double chosen = bestNonFallback > 0 ? bestNonFallback : (anyFallback > 0 ? anyFallback : 10);
        return Math.Min(chosen, CompanionAggroRange - 5);
    }

    static bool HasActiveSelfBuff(CompanionRuntime comp, int effect, double now)
    {
        for (int i = 0; i < comp.ActiveSelfBuffs.Count; i++)
            if (comp.ActiveSelfBuffs[i].Effect == effect && now < comp.ActiveSelfBuffs[i].Until - 0.5)
                return true;
        return false;
    }

    static bool TargetAlreadyCursedBy(CompanionRuntime comp, SpawnedMonster target, string canonicalName, double now)
    {
        if (!target.HasCurses) return false;
        return target.Curses.All.TryGetValue((comp.EntityId, canonicalName), out var c) && now < c.Until - 0.5;
    }

    static (int X, int Y)? CompanionTravelLanding(Player player, SpawnedMonster m, (int X, int Y) from, (int X, int Y) targetAt)
    {
        int size = Math.Max(0, LivingCollision.Size(m));
        double dx = from.X - targetAt.X, dy = from.Y - targetAt.Y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1) return null;
        for (double back = 4; back >= 1; back--)
        {
            int x = targetAt.X + (int)Math.Round(dx / d * back);
            int y = targetAt.Y + (int)Math.Round(dy / d * back);
            if (!player.Layout.WalkableFor(x, y, size) || player.Barrier.Contains((x, y))) continue;
            if (player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(from, (x, y), sight, 1)) continue;
            bool blocked = false;
            foreach (var cell in ClientPathfinder.Line(from, (x, y)))
                if (player.Barrier.Contains(cell)) { blocked = true; break; }
            if (!blocked) return (x, y);
        }
        return null;
    }

    static (MonsterKitSkill? Skill, (int X, int Y)? TravelLanding) ChooseCompanionSkillLocked(
        Player player, CompanionRuntime comp, (int X, int Y) here, double distance,
        SpawnedMonster target, (int X, int Y) targetAt, double now)
    {
        var catalog = SkillsFor(player);
        int targetSize = Math.Max(0, LivingCollision.Size(target));
        bool IsReady(MonsterKitSkill s) => !comp.Cooldowns.TryGetValue(s.Effect, out double readyAt) || now >= readyAt;

        // 1. Self-buffs / Warcries / Guards when in combat and not currently active
        foreach (var skill in comp.Kit)
        {
            if (skill.TriggerThreshold > 0 || skill.Attack.Use is not { Shape: MonsterSkillShape.Self }) continue;
            if (!IsReady(skill) || HasActiveSelfBuff(comp, skill.Effect, now)) continue;
            if (distance > Math.Max(45, skill.Attack.Range)) continue;
            return (skill, null);
        }

        // 2. Hexes & Marks when off cooldown and target doesn't already have this curse from this companion
        foreach (var skill in comp.Kit)
        {
            if (skill.TriggerThreshold > 0 || skill.Attack.Use is not { Shape: MonsterSkillShape.Curse }) continue;
            if (!IsReady(skill) || distance > skill.Attack.Range + targetSize) continue;
            string? canonical = MercenaryTables.CanonicalCurseName(catalog.EffectId(skill.Effect));
            if (canonical is not null && TargetAlreadyCursedBy(comp, target, canonical, now)) continue;
            return (skill, null);
        }

        // 3. Totems / Summons when below active limit
        foreach (var skill in comp.Kit)
        {
            if (skill.TriggerThreshold > 0 || skill.Attack.Summon is not { } sum) continue;
            if (!IsReady(skill) || distance > Math.Max(45, skill.Attack.Range) + targetSize) continue;
            comp.SupportStatsByEffect.TryGetValue(skill.Effect, out var supp);
            int limit = Math.Max(1, sum.Limit) + Math.Max(0,
                comp.Computed.StatValues.GetValueOrDefault(Stat.BaseNumberOfTotemsAllowed)
                + (supp?.GetValueOrDefault(Stat.BaseNumberOfTotemsAllowed) ?? 0));
            int activeCount = comp.ActiveSummons.Count(a => a.SourceEffect == skill.Effect);
            if (activeCount < limit)
                return (skill, null);
        }

        // 4. Offensive skills in range: separate cooldown specials vs 0-cd skills (rotate 0-cd by LRU; never let melee fallback beat build skills)
        MonsterKitSkill? bestSpecial = null;
        double bestSpecialUsed = double.MaxValue;
        MonsterKitSkill? bestPlain = null;
        double bestPlainUsed = double.MaxValue;
        MonsterKitSkill? fallbackMelee = null;
        bool hasRangedOffensiveInReach = false;

        foreach (var skill in comp.Kit)
        {
            if (skill.TriggerThreshold > 0 || skill.Attack.Use is not null || skill.Attack.Summon is not null) continue;
            if (!IsReady(skill)) continue;
            if (distance > skill.Attack.Range + targetSize) continue;
            if (skill.MinRange > 0 && distance < skill.MinRange) continue;

            if (skill.Effect == MercenaryTables.MeleeFallbackEffect)
            {
                fallbackMelee ??= skill;
                continue;
            }
            if (skill.Attack.Ranged || skill.Attack.Projectile is not null)
                hasRangedOffensiveInReach = true;

            double usedAt = comp.LastUsedAt.GetValueOrDefault(skill.Effect, double.NegativeInfinity);
            if (skill.CooldownSeconds > 0)
            {
                if (bestSpecial is null || usedAt < bestSpecialUsed)
                {
                    bestSpecial = skill;
                    bestSpecialUsed = usedAt;
                }
            }
            else
            {
                if (bestPlain is null || usedAt < bestPlainUsed)
                {
                    bestPlain = skill;
                    bestPlainUsed = usedAt;
                }
            }
        }

        // 5. Travel / Gap-closer (Leap Slam, Shield Charge, Flicker Strike, Flame Dash, Frostblink) when target is at mid/far range
        if (!hasRangedOffensiveInReach || bestPlain is null)
        {
            foreach (var skill in comp.Kit)
            {
                if (skill.TriggerThreshold > 0 || skill.Attack.Use is not { Shape: MonsterSkillShape.Travel } use) continue;
                if (!IsReady(skill)) continue;
                double minR = Math.Max(skill.MinRange, Math.Max(use.MinRange, 10));
                if (distance < minR || distance > skill.Attack.Range + targetSize) continue;
                if (CompanionTravelLanding(player, comp.Monster, here, targetAt) is { } landing)
                    return (skill, landing);
            }
        }

        // Alternate specials and plain skills so cooldown specials fire promptly without starving 0-cd rotation
        if (bestSpecial is not null && (bestPlain is null || !comp.LastActionWasSpecial))
            return (bestSpecial, null);
        if (bestPlain is not null)
            return (bestPlain, null);
        if (bestSpecial is not null)
            return (bestSpecial, null);
        if (fallbackMelee is not null)
            return (fallbackMelee, null);

        return (null, null);
    }

    // ---- Skill Execution: Start & Contact -------------------------------------------------------------------------------------

    static void StartCompanionSkillLocked(Player player, CompanionRuntime comp, MonsterKitSkill skill,
        (int X, int Y) here, uint targetId, SpawnedMonster target, (int X, int Y) targetAt,
        (int X, int Y)? travelLanding, double now, double actionRate)
    {
        var m = comp.Monster;
        bool useOffHand = comp.Computed.Weapons.IsDualWielding && !skill.Attack.Spell && comp.NextOffHand;
        if (comp.Computed.Weapons.IsDualWielding && !skill.Attack.Spell)
            comp.NextOffHand = !comp.NextOffHand;

        // Evaluate live scaled attack timing (including self-buffs and supports)
        var baseEntry = comp.BaseKit.FirstOrDefault(k => k.Effect == skill.Effect) ?? skill;
        comp.SupportStatsByEffect.TryGetValue(skill.Effect, out var supp);
        var offence = EvaluateCompanionOffence(player, m.Variety!, comp.Mercenary, comp.Computed, baseEntry, supp,
            m.BuffStats, default, target, now, useOffHand);
        var attack = offence.Attack with { SkillId = skill.Attack.SkillId };

        double duration = Math.Max(0.15, attack.Seconds / Math.Max(0.2, actionRate));
        int gen = ++m.ActionGeneration;
        m.BusyUntil = now + duration;
        m.LastTarget = null;
        comp.LastUsedAt[skill.Effect] = now;
        comp.LastActionWasSpecial = skill.CooldownSeconds > 0 && skill.Attack.Use is null;

        if (skill.CooldownSeconds > 0)
            comp.Cooldowns[skill.Effect] = now + skill.CooldownSeconds;

        var aim = targetAt;
        uint? actionTargetId = targetId;
        if (attack.Use is { Shape: MonsterSkillShape.Self })
        {
            aim = here;
            actionTargetId = SkillCatalog.ActionTypeId(skill.Effect) == "FireShield" ? comp.EntityId : null;
            m.Walk.Order(here.X, here.Y, now);
        }
        else if (attack.Use is { Shape: MonsterSkillShape.Travel } && travelLanding is { } land)
        {
            aim = land;
            actionTargetId = null;
            m.Walk.Teleport(land.X, land.Y, now);
        }
        else
        {
            m.Walk.Order(here.X, here.Y, now);
            if (attack.AreaAtSelf) { aim = here; actionTargetId = null; }
        }

        double contactAt = now + duration * (attack.Release?.Fraction ?? 0.5);
        comp.PendingAttack = new PendingCompanionAttack(skill, attack, targetId, target, here, aim, contactAt, gen, useOffHand, travelLanding);

        player.Minions.OutputQueue.Add(new(
            InstanceProtocol.ActionStarted(comp.EntityId, here.X, here.Y, aim.X, aim.Y,
                attack.SkillId, 0xffff, player.MonsterOrderFlags, targetObjectId: actionTargetId),
            null, comp.EntityId, m, WorldId: comp.EntityId));
    }

    bool CommitCompanionSkillLocked(Player player, CompanionRuntime comp, PendingCompanionAttack pending, double now,
        ref List<(uint Id, byte[] Create, byte[] Wake)>? spawns, ref List<uint>? removals)
    {
        var m = comp.Monster;
        if (m.Dead || m.Life == 0) return false;
        var attack = pending.ScaledAttack;
        int effect = pending.Skill.Effect;

        // 1. Self-Buffs, Warcries & Guards
        if (attack.Use is { Shape: MonsterSkillShape.Self } selfUse)
        {
            ExecuteCompanionSelfSkillLocked(player, comp, effect, selfUse, now);
            return false;
        }

        // 2. Curses (Hexes) & Marks
        if (attack.Use is { Shape: MonsterSkillShape.Curse } curseUse)
        {
            ExecuteCompanionCurseLocked(player, comp, pending, curseUse, now);
            return false;
        }

        // 3. Travel skill landing impact
        if (attack.Use is { Shape: MonsterSkillShape.Travel })
        {
            if (!attack.Damage.IsZero)
            {
                var landAt = pending.TravelLanding ?? m.Walk.Position(now);
                double radius = attack.AreaRadius > 0 ? attack.AreaRadius : 14.0;
                ApplyCompanionAreaDamageLocked(player, comp, effect, attack with { Use = null }, landAt, radius, now, pending.UseOffHand);
            }
            return false;
        }

        // 4. Totems & Summons
        if (attack.Summon is { } summon)
        {
            ExecuteCompanionSummonLocked(player, comp, pending, summon, now);
            return false;
        }

        // 5. Traps & Mines (store unscaled base trap payload so detonation evaluates scaling once)
        if (attack.Trap is { } trapPayload)
        {
            var baseTrapPayload = comp.BaseKit.FirstOrDefault(k => k.Effect == effect)?.Attack.Trap ?? trapPayload;
            ExecuteCompanionTrapThrowLocked(player, comp, pending, baseTrapPayload, now, ref spawns, ref removals);
            return false;
        }

        // 6. Delayed Strikes (Storm Call, Frost Bomb)
        if (attack.Strike is { } strike)
        {
            ExecuteCompanionDelayedStrikeLocked(player, comp, pending, strike, now, ref spawns);
            return false;
        }

        // 7. Combo Parents (S3 multi-step skills)
        if (attack.ComboSteps is { Count: > 0 } steps)
        {
            bool launched = false;
            var at = m.Walk.Position(now);
            foreach (var step in steps)
            {
                var child = step.ChildAttack;
                if (child.Projectile is { } cp)
                    launched |= LaunchCompanionProjectileLocked(player, comp, step.ChildEffect, child, cp, at, pending.Aim, now,
                        pending.UseOffHand, useOverrideBase: true);
                else if (child.AreaRadius > 0)
                    ApplyCompanionAreaDamageLocked(player, comp, step.ChildEffect, child, child.AreaAtSelf ? at : pending.Aim,
                        child.AreaRadius, now, pending.UseOffHand, useOverrideBase: true);
                else if (!pending.Target.Dead && MinionMayTarget(pending.Target))
                {
                    var off = EvaluateForEffectLocked(player, comp, step.ChildEffect, child, pending.Target, now,
                        pending.UseOffHand, useOverrideBase: true);
                    CommitCompanionSingleHitLocked(player, comp, pending.TargetId, pending.Target, off, now, isProjectile: child.Ranged);
                }
            }
            return launched;
        }

        // 8. Projectiles (fire real server projectiles on ProjectileSession against hostiles!)
        if (attack.Projectile is { } projectile)
        {
            var from = m.Walk.Position(now);
            var aim = !pending.Target.Dead && MinionMayTarget(pending.Target)
                ? pending.Target.Walk.Position(now) : pending.Aim;
            return LaunchCompanionProjectileLocked(player, comp, effect, attack, projectile, from, aim, now, pending.UseOffHand);
        }

        // 9. Area / Nova / Slam / Rain hits (+ Ground effect)
        if (attack.AreaRadius > 0)
        {
            var center = attack.AreaAtSelf
                ? m.Walk.Position(now)
                : (!pending.Target.Dead && MinionMayTarget(pending.Target) ? pending.Target.Walk.Position(now) : pending.Aim);
            ApplyCompanionAreaDamageLocked(player, comp, effect, attack, center, attack.AreaRadius, now, pending.UseOffHand);
            if (attack.Ground is { } areaGround)
                AddCompanionGroundLocked(comp, effect, UnscaledBaseAttack(comp, effect, pending.Skill.Attack), center, Math.Max(attack.AreaRadius, areaGround.Radius), areaGround.Seconds, now);
            return false;
        }

        // 10. Direct Single-Target / Chaining / Multi-hit attack or spell
        var target = pending.Target;
        if (target.Dead || target.Life == 0 || !MinionMayTarget(target)
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(pending.TargetId), target))
            return false;

        var offence = EvaluateForEffectLocked(player, comp, effect, attack, target, now, pending.UseOffHand);
        int hits = Math.Clamp(offence.Attack.Hits, 1, 4);
        bool anyHit = false;
        for (int h = 0; h < hits && !target.Dead && target.Life > 0; h++)
            anyHit |= CommitCompanionSingleHitLocked(player, comp, pending.TargetId, target, offence, now, isProjectile: attack.Ranged);

        // Chaining skills without projectile (e.g., Arc): chain to nearby distinct hostiles
        var catalog = SkillsFor(player);
        if (anyHit && (catalog.HasType(effect, "Chains") || offence.AdditionalChains > 0))
        {
            var nativeStats = GolemData.TrySkill(catalog, effect, comp.Mercenary.Level, out var ns) ? ns.Item2 : null;
            int chains = Math.Clamp((nativeStats?.GetValueOrDefault(Stat.NumberOfChains) ?? 0)
                + offence.AdditionalChains + (catalog.HasType(effect, "Chains") ? 3 : 0), 0, 12);
            if (chains > 0)
            {
                var hitSet = new HashSet<uint> { pending.TargetId };
                var chainFrom = target.Walk.Position(now);
                for (int c = 0; c < chains; c++)
                {
                    (uint Id, SpawnedMonster Monster, (int X, int Y) At)? next = null;
                    double bestD = double.MaxValue;
                    foreach (var (eid, enemy) in player.Area.MonstersNear(chainFrom, 38))
                    {
                        if (hitSet.Contains(eid) || !MinionMayTarget(enemy)) continue;
                        var eat = enemy.Walk.Position(now);
                        double d = Dist(chainFrom, eat);
                        if (d <= 35 && d < bestD) { bestD = d; next = (eid, enemy, eat); }
                    }
                    if (next is not { } chained) break;
                    hitSet.Add(chained.Id);
                    var chainOff = EvaluateForEffectLocked(player, comp, effect, attack, chained.Monster, now, pending.UseOffHand);
                    CommitCompanionSingleHitLocked(player, comp, chained.Id, chained.Monster, chainOff, now, isProjectile: false);
                    chainFrom = chained.At;
                }
            }
        }

        if (attack.Ground is { } ground)
            AddCompanionGroundLocked(comp, effect, UnscaledBaseAttack(comp, effect, pending.Skill.Attack), target.Walk.Position(now), ground.Radius, ground.Seconds, now);

        return false;
    }

    // ---- Self-Buffs, Warcries, Guards, Curses, Traps, Strikes, Summons, Projectiles, AoE --------------------------------------

    static void ExecuteCompanionSelfSkillLocked(Player player, CompanionRuntime comp, int effect, MonsterSkillUse use, double now)
    {
        var m = comp.Monster;
        var catalog = SkillsFor(player);
        double duration = Math.Max(2.0, use.Seconds);
        ushort tag = player.NextBuff++;

        if (use.Stats.Count > 0 || use.BuffRow > 0)
        {
            for (int i = comp.ActiveSelfBuffs.Count - 1; i >= 0; i--)
            {
                var old = comp.ActiveSelfBuffs[i];
                if (old.Effect != effect) continue;
                comp.ActiveSelfBuffs.RemoveAt(i);
                if (old.BuffRow > 0)
                    player.Area.Buffs.Clear(comp.EntityId, old.BuffRow, comp.EntityId, tag: old.Tag);
            }
            comp.ActiveSelfBuffs.Add(new ActiveSelfBuff(effect, use.BuffRow, tag, use.Stats, now + duration));
            RebuildCompanionBuffStatsLocked(comp);
            if (use.BuffRow > 0)
                player.Area.Buffs.Set(comp.EntityId, global::BuffRows.Spec(use.BuffRow, comp.EntityId, use.Stats, duration, tag: tag));
        }

        // Guard skill (Molten Shell / Steelskin): absorb incoming hit damage into temporary guard pool
        if (catalog.HasType(effect, "Guard") || use.Stats.ContainsKey(Stat.MoltenShellDamageAbsorbedPct))
        {
            int absorbPct = Math.Clamp(use.Stats.GetValueOrDefault(Stat.MoltenShellDamageAbsorbedPct), 50, 80);
            int armourLimitPct = use.Stats.GetValueOrDefault(Stat.MoltenShellDamageAbsorbLimitPctOfArmour);
            int maxAbsorb = use.Stats.GetValueOrDefault(Stat.MoltenShellMaxDamageAbsorbed);
            int armour = m.Effects.Armour(0);
            int pool = armourLimitPct > 0
                ? Math.Min(maxAbsorb > 0 ? maxAbsorb : 5000, Math.Max(200, armour * armourLimitPct / 100))
                : Math.Max(300, (int)(m.MaxLife * 0.25));
            comp.GuardPoolRemaining = pool;
            comp.GuardAbsorbPct = absorbPct;
            comp.GuardUntil = now + duration;
        }

        // Warcry: taunt nearby enemies, and grant Endurance charges for Enduring Cry
        if (catalog.IsWarcry(effect))
        {
            var here = m.Walk.Position(now);
            int enemyCount = 0;
            if (player.Companion is { } ac && ac.EntityId == comp.EntityId)
            {
                foreach (var (_, enemy) in player.Area.MonstersNear(here, 45))
                {
                    if (!MinionMayTarget(enemy)) continue;
                    enemyCount++;
                    TryApplyCompanionTauntLocked(player, ac, enemy, now, extraChancePct: 100);
                }
            }
            if (enemyCount > 0 && catalog.EffectId(effect)?.Contains("EnduringCry", StringComparison.OrdinalIgnoreCase) == true)
                GrantMonsterChargeLocked(player.Area, comp.EntityId, m, ChargeType.Endurance, now, Math.Min(3, enemyCount));
        }

        // Rally charge skills (MassFrenzy / MassPower)
        if (use.RallyCharge is ChargeType rally)
            GrantMonsterChargeLocked(player.Area, comp.EntityId, m, rally, now, Math.Max(1, use.RallyCount));
    }

    void ExecuteCompanionCurseLocked(Player player, CompanionRuntime comp, PendingCompanionAttack pending,
        MonsterSkillUse use, double now)
    {
        var catalog = SkillsFor(player);
        int effect = pending.Skill.Effect;
        string? canonical = MercenaryTables.CanonicalCurseName(catalog.EffectId(effect));
        var center = !pending.Target.Dead && MinionMayTarget(pending.Target)
            ? pending.Target.Walk.Position(now) : pending.Aim;

        // Bane: deals area damage in addition to hexing
        if (!pending.ScaledAttack.Damage.IsZero)
        {
            double dmgRadius = pending.ScaledAttack.AreaRadius > 0 ? pending.ScaledAttack.AreaRadius : 22;
            ApplyCompanionAreaDamageLocked(player, comp, effect, pending.ScaledAttack with { Use = null }, center, dmgRadius, now, false);
        }

        if (canonical is null) return;
        bool isMark = CurseCatalog.All.TryGetValue(canonical, out var def) && def.Mark;
        var rawStats = GolemData.TrySkill(catalog, effect, comp.Mercenary.Level, out var ns) ? ns.Item2 : new Dictionary<uint, int>(use.Stats);

        if (isMark)
        {
            if (!pending.Target.Dead && MinionMayTarget(pending.Target))
                ScriptedAllyApplyCurseLocked(player, comp.EntityId, pending.Target, pending.ScaledAttack.SkillId,
                    canonical, rawStats, now, comp.Computed.StatValues);
        }
        else
        {
            double radius = SkillArea.Radius(22, k => rawStats.GetValueOrDefault(k)
                + comp.Computed.StatValues.GetValueOrDefault(k), spell: true);
            foreach (var (_, enemy) in player.Area.MonstersNear(center, radius + 4))
            {
                if (!MinionMayTarget(enemy)) continue;
                if (Dist(center, enemy.Walk.Position(now)) <= radius + Math.Max(0, LivingCollision.Size(enemy)))
                    ScriptedAllyApplyCurseLocked(player, comp.EntityId, enemy, pending.ScaledAttack.SkillId,
                        canonical, rawStats, now, comp.Computed.StatValues);
            }
        }
    }

    static void ExecuteCompanionSummonLocked(Player player, CompanionRuntime comp, PendingCompanionAttack pending,
        SkillCatalog.MonsterSummonInfo summon, double now)
    {
        int effect = pending.Skill.Effect;
        var at = !pending.Target.Dead && MinionMayTarget(pending.Target)
            ? pending.Target.Walk.Position(now) : pending.Aim;
        // Place totem/summon slightly beside target/companion
        var here = comp.Monster.Walk.Position(now);
        var placeAt = ((here.X + at.X) / 2, (here.Y + at.Y) / 2);

        MonsterAttack bodyAttack;
        if (summon.Effect >= 0 && Monsters.Find(summon.Variety.ToString()) is { } bodyVar
            && BossKits.BodyAttack(player, bodyVar, comp.Mercenary.Level, summon.Effect, 0x4000) is { } resolved)
        {
            bodyAttack = resolved;
        }
        else
        {
            bodyAttack = UnscaledBaseAttack(comp, effect, pending.Skill.Attack) with
            {
                Summon = null,
                Range = Math.Max(45, pending.ScaledAttack.Range),
            };
        }

        // If the skill itself deals immediate hit damage (e.g. Absolution / Holy Strike), apply that hit too!
        if (!pending.ScaledAttack.Damage.IsZero && !pending.Target.Dead && MinionMayTarget(pending.Target))
        {
            var off = EvaluateForEffectLocked(player, comp, effect, pending.ScaledAttack, pending.Target, now, pending.UseOffHand);
            CommitCompanionSingleHitLocked(player, comp, pending.TargetId, pending.Target, off, now, isProjectile: false);
        }

        comp.SupportStatsByEffect.TryGetValue(effect, out var supp);
        int limit = Math.Max(1, summon.Limit) + Math.Max(0,
            comp.Computed.StatValues.GetValueOrDefault(Stat.BaseNumberOfTotemsAllowed)
            + (supp?.GetValueOrDefault(Stat.BaseNumberOfTotemsAllowed) ?? 0));
        while (comp.ActiveSummons.Count(s => s.SourceEffect == effect) >= limit)
        {
            int idx = comp.ActiveSummons.FindIndex(s => s.SourceEffect == effect);
            if (idx >= 0) comp.ActiveSummons.RemoveAt(idx);
            else break;
        }

        double lifetime = 8.0;
        var native = GolemData.TrySkill(SkillsFor(player), effect, comp.Mercenary.Level, out var ns) ? ns.Item2 : null;
        if (native is not null)
        {
            int durMs = Math.Max(native.GetValueOrDefault(Stat.BaseTotemDuration), native.GetValueOrDefault(Stat.BaseSkillEffectDuration));
            if (durMs > 0) lifetime = Math.Clamp(durMs / 1000.0, 3.0, 20.0);
        }
        comp.ActiveSummons.Add(new CompanionSummon(effect, bodyAttack, placeAt, now + lifetime, now + 0.3));
    }

    static void ExecuteCompanionTrapThrowLocked(Player player, CompanionRuntime comp, PendingCompanionAttack pending,
        MonsterAttack payload, double now, ref List<(uint Id, byte[] Create, byte[] Wake)>? spawns, ref List<uint>? removals)
    {
        int effect = pending.Skill.Effect;
        var off = EvaluateForEffectLocked(player, comp, effect, pending.ScaledAttack, null, now, false);
        int count = Math.Clamp(1 + off.ExtraTrapsToThrow, 1, 6);
        int limit = Math.Max(3, 3 + off.ExtraTrapsAllowed);
        var aim = !pending.Target.Dead && MinionMayTarget(pending.Target) ? pending.Target.Walk.Position(now) : pending.Aim;
        var catalog = SkillsFor(player);
        var native = GolemData.TrySkill(catalog, effect, comp.Mercenary.Level, out var ns) ? ns.Item2 : new Dictionary<uint, int>();
        double lifetime = native.GetValueOrDefault(Stat.BaseTrapDuration) is > 0 and var ms ? ms / 1000.0 : 4.0;
        double triggerRadius = Math.Max(10, 12 * off.AreaScaleFactor);

        while (comp.ActiveTraps.Count + count > limit && comp.ActiveTraps.Count > 0)
        {
            var old = comp.ActiveTraps[0];
            comp.ActiveTraps.RemoveAt(0);
            if (old.HasVisual && old.Id != 0) (removals ??= []).Add(old.Id);
        }

        // Clear trap/mine flags on the proxy skill so client detonation plays the payload rather than another throw.
        var proxyStats = new Dictionary<uint, int>(native);
        proxyStats[Stat.IsTrap] = 0;
        proxyStats[Stat.IsRemoteMine] = 0;
        proxyStats[Stat.BaseSkillIsTrapped] = 0;
        proxyStats[Stat.BaseSkillIsMined] = 0;
        var proxyStatPairs = proxyStats.Select(p => (p.Key, p.Value)).ToArray();

        var rng = AreaRng(player);
        for (int i = 0; i < count; i++)
        {
            int ox = count > 1 ? rng.Next(-4, 5) : 0;
            int oy = count > 1 ? rng.Next(-4, 5) : 0;
            var land = (X: aim.X + ox, Y: aim.Y + oy);
            uint trapId = player.TakeObjectId();
            bool hasVisual = false;
            int effLvl = catalog.MaxLevelAt(effect, comp.Mercenary.Level);
            if (catalog.FindByRow(0x4000, effect, effLvl, proxyStatPairs) is { } rawTrapSkill)
            {
                var trapSkill = rawTrapSkill with { Parent = (comp.EntityId, pending.ScaledAttack.SkillId) };
                const string trapPath = "Metadata/MiscellaneousObjects/Trap";
                byte[] create = InstanceEntities.CreateObject(trapId, InstanceEntities.ObjectTypeHash(trapPath),
                    new TrapMineObject(land.X, land.Y, false, [trapSkill], [], Team: 0).Blob());
                byte[] wake = InstanceProtocol.EnterWakeZone(trapId);
                (spawns ??= []).Add((trapId, create, wake));
                hasVisual = true;
            }
            comp.ActiveTraps.Add(new CompanionTrap(trapId, payload, effect, land, now + 0.35, now + lifetime, triggerRadius, hasVisual));
        }
    }

    static void ExecuteCompanionDelayedStrikeLocked(Player player, CompanionRuntime comp, PendingCompanionAttack pending,
        MonsterDelayedStrike strike, double now, ref List<(uint Id, byte[] Create, byte[] Wake)>? spawns)
    {
        var aim = !pending.Target.Dead && MinionMayTarget(pending.Target) ? pending.Target.Walk.Position(now) : pending.Aim;
        uint markerId = 0;
        bool hasVisual = false;
        if (strike.MarkerArt is { } art)
        {
            markerId = player.TakeObjectId();
            byte[] create = InstanceEntities.CreateObject(markerId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(aim.X, aim.Y, art) { Flags0 = EffectObject.FixedRotation }.Blob());
            byte[] wake = InstanceProtocol.EnterWakeZone(markerId);
            (spawns ??= []).Add((markerId, create, wake));
            hasVisual = true;
        }
        double radius = pending.ScaledAttack.AreaRadius > 0 ? pending.ScaledAttack.AreaRadius : 16;
        var baseAttack = UnscaledBaseAttack(comp, pending.Skill.Effect, pending.Skill.Attack) with { Strike = null };
        comp.ActiveStrikes.Add(new CompanionDelayedStrike(markerId, hasVisual, baseAttack,
            pending.Skill.Effect, aim, radius, now + strike.Seconds, strike.ImpactArt, strike.ImpactSeconds));
    }

    static MonsterAttack UnscaledBaseAttack(CompanionRuntime comp, int effect, MonsterAttack fallback)
        => comp.BaseKit.FirstOrDefault(k => k.Effect == effect)?.Attack ?? fallback;

    static void AddCompanionGroundLocked(CompanionRuntime comp, int effect, MonsterAttack unscaledAttack,
        (int X, int Y) center, double radius, double seconds, double now)
    {
        if (comp.ActiveGrounds.Count >= 6) comp.ActiveGrounds.RemoveAt(0);
        comp.ActiveGrounds.Add(new CompanionGround(unscaledAttack with { Ground = null }, effect, center,
            Math.Max(8, radius), now + Math.Clamp(seconds, 1.0, 12.0), now + 0.5));
    }

    bool LaunchCompanionProjectileLocked(Player player, CompanionRuntime comp, int effect, MonsterAttack attack,
        SkillCatalog.MonsterProjectileInfo proj, (int X, int Y) from, (int X, int Y) aim, double now, bool useOffHand,
        bool useOverrideBase = false)
    {
        var session = ProjectilesFor(player);
        if (!session.Active) return false;

        var m = comp.Monster;
        var off = EvaluateForEffectLocked(player, comp, effect, attack, null, now, useOffHand, useOverrideBase);
        var scaledProj = off.Attack.Projectile ?? off.Attack.Trap?.Projectile ?? proj;

        var fromVec = ProjectileSession.Vector(from);
        var toVec = off.ProjectilesNova ? fromVec + new ProjectileVector(1, 0) : ProjectileSession.Vector(aim);
        if ((toVec - fromVec).Length < 0.5) toVec = fromVec + new ProjectileVector(1, 0);
        double distance = Math.Max(1, (toVec - fromVec).Length);

        double speed = Math.Max(15.0, scaledProj.Speed * Math.Max(0.25, 1.0 + off.ProjectileSpeedPct / 100.0));
        double maxRange = scaledProj.Lobbed ? distance : Math.Max(SystemSettings.Default.ProjectileMaximumRangeCells, distance + 15);
        int count = Math.Clamp(Math.Max(1, scaledProj.Count), 1, 24);
        double spread = off.ProjectilesNova ? Math.Tau
            : count > 1 && scaledProj.SpreadRadians <= 0 ? 0.45 : scaledProj.SpreadRadians;
        double splashRadius = Math.Max(scaledProj.AreaRadius, off.Attack.AreaRadius);

        var profile = new ProjectileProfile(scaledProj.Metadata)
        {
            Count = count,
            Speed = speed,
            Radius = Math.Max(1, scaledProj.CollisionRadius),
            SpreadRadians = spread,
            Range = maxRange,
            Lobbed = scaledProj.Lobbed,
            DirectDamage = !scaledProj.Lobbed,
            Pierce = off.AlwaysPierce ? -1 : off.PierceCount,
            Chain = off.AdditionalChains,
            Returns = off.ProjectilesReturn,
            ContactArea = !scaledProj.Lobbed && splashRadius > 0 ? new("mercenary splash", splashRadius) : null,
            EndArea = scaledProj.Lobbed ? new("mercenary mortar", Math.Max(10, splashRadius)) : null,
            EndAreaReasons = scaledProj.Lobbed
                ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        };

        var owner = session.ActorKey(comp.EntityId, m);
        double flightCells = scaledProj.Lobbed ? distance : maxRange;
        var presentation = MonsterProjectilePresentation(SkillsFor(player).Clips(scaledProj.Metadata), flightCells / profile.Speed);
        if (attack.Release?.Point is { } launch)
            presentation = (presentation ?? new ProjectilePresentation()) with { LaunchHeight = launch.HeightSteps };

        var offense = new ObjectProjectileOffense($"mercenary 0x{comp.EntityId:x} {scaledProj.Metadata}", (s, contact) =>
        {
            if (comp.Closed || !CurrentGameWork || m.Dead || player.Closed || player.LeavingArea)
                return ProjectileHitResult.Miss;
            if (player.Area.Monsters.GetValueOrDefault(contact.Target.Id) is not { } victim || !MinionMayTarget(victim))
                return ProjectileHitResult.Miss;
            double hitAt = Math.Max(AreaTime(player), contact.At / (double)ProjectileRuntime.QuantaPerSecond);
            var hitOff = EvaluateForEffectLocked(player, comp, effect, attack, victim, hitAt, useOffHand, useOverrideBase);
            if (Math.Abs(contact.Multiplier - 1.0) > 1e-6)
                hitOff = hitOff with { Attack = hitOff.Attack with { Damage = hitOff.Attack.Damage.Scale(contact.Multiplier) } };
            bool hit = CommitCompanionSingleHitLocked(player, comp, contact.Target.Id, victim, hitOff, hitAt, isProjectile: !contact.IsArea);
            if (hit)
            {
                if (attack.Ground is { } ground)
                {
                    var unscaledGround = useOverrideBase ? attack : UnscaledBaseAttack(comp, effect, attack);
                    AddCompanionGroundLocked(comp, effect, unscaledGround, victim.Walk.Position(hitAt), ground.Radius, ground.Seconds, hitAt);
                }
                s.Enqueue(() => PublishMinionOutcomes(player));
            }
            return hit ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        }) { Quiet = true, Presentation = presentation };

        session.Schedule(ProjectileRuntime.Time(now),
            () => !comp.Closed && CurrentGameWork && !player.Closed && !player.LeavingArea && !m.Dead,
            () => new(profile, owner, owner, 1, fromVec, toVec)
            {
                Payload = offense,
                ReturnAnchor = off.ProjectilesReturn ? owner : null,
            });
        return true;
    }

    void ApplyCompanionAreaDamageLocked(Player player, CompanionRuntime comp, int effect, MonsterAttack attack,
        (int X, int Y) center, double radius, double now, bool useOffHand, bool useOverrideBase = false, double damageScale = 1.0)
    {
        if (attack.Damage.IsZero || radius <= 0) return;
        foreach (var (enemyId, enemy) in player.Area.MonstersNear(center, radius + 8).ToArray())
        {
            if (!MinionMayTarget(enemy)) continue;
            var at = enemy.Walk.Position(now);
            if (Dist(center, at) > radius + Math.Max(0, LivingCollision.Size(enemy))) continue;
            var off = EvaluateForEffectLocked(player, comp, effect, attack, enemy, now, useOffHand, useOverrideBase);
            if (Math.Abs(damageScale - 1.0) > 1e-6)
                off = off with { Attack = off.Attack with { Damage = off.Attack.Damage.Scale(damageScale) } };
            CommitCompanionSingleHitLocked(player, comp, enemyId, enemy, off, now, isProjectile: false);
        }
    }

    static CompanionOffence EvaluateForEffectLocked(Player player, CompanionRuntime comp, int effect,
        MonsterAttack fallbackAttack, SpawnedMonster? target, double now, bool useOffHand, bool useOverrideBase = false)
    {
        var baseEntry = useOverrideBase
            ? new MonsterKitSkill(effect, fallbackAttack, 0)
            : (comp.BaseKit.FirstOrDefault(k => k.Effect == effect) ?? new MonsterKitSkill(effect, fallbackAttack, 0));
        comp.SupportStatsByEffect.TryGetValue(effect, out var supp);
        CompanionLinkOffence linkOff = player.Companion is { } ac && ac.EntityId == comp.EntityId
            ? CompanionLinkOffenceAt(player, ac, PlayerStatValues(player), now)
            : default;
        return EvaluateCompanionOffence(player, comp.Monster.Variety!, comp.Mercenary, comp.Computed,
            baseEntry, supp, comp.Monster.BuffStats, linkOff, target, now, useOffHand);
    }

    static bool CommitCompanionSingleHitLocked(Player player, CompanionRuntime comp, uint targetId, SpawnedMonster target,
        CompanionOffence offence, double now, bool isProjectile)
    {
        var m = comp.Monster;
        if (m.Dead || target.Dead || target.Life == 0 || !MinionMayTarget(target)
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(targetId), target))
            return false;

        var rng = AreaRng(player);
        comp.SupportStatsByEffect.TryGetValue(offence.Attack.Effect ?? -1, out var supportStats);
        CompanionLinkOffence linkOff = player.Companion is { } ac && ac.EntityId == comp.EntityId
            ? CompanionLinkOffenceAt(player, ac, PlayerStatValues(player), now)
            : default;

        // Overlay per-skill support stats & active Link crit/accuracy/ignite stats onto m.Mods for ScriptedAllyHitLocked
        var savedMods = m.Mods;
        bool hasOverlay = (supportStats is { Count: > 0 })
            || linkOff.Ignite != 0 || linkOff.CritMultiplier != 0 || linkOff.Accuracy != 0;
        if (hasOverlay)
        {
            var merged = new Dictionary<uint, int>(comp.Computed.StatValues);
            void AddStat(uint key, int delta)
            {
                if (delta != 0) merged[key] = ActorScale.Saturate((long)merged.GetValueOrDefault(key) + delta);
            }
            if (supportStats is not null)
                foreach (var (k, v) in supportStats)
                    AddStat(k, v);
            AddStat(Stat.BaseChanceToIgnitePct, linkOff.Ignite);
            AddStat(Stat.BaseCriticalStrikeMultiplierPlus, linkOff.CritMultiplier);
            AddStat(Stat.AccuracyRatingPlusPct, linkOff.Accuracy);
            m.Mods = savedMods with { Effects = new MonsterModEffects(merged) };
        }

        bool hit;
        try
        {
            hit = ScriptedAllyHitLocked(player, comp.EntityId, m, targetId, target, offence.Attack, now, rng,
                projectile: isProjectile,
                baseCritPermyriad: offence.BaseCritPermyriad,
                penetration: offence.Penetration,
                cannotCrit: offence.CannotCrit,
                ignoreEvasion: offence.IgnoreEvasion,
                ignorePhysicalReduction: offence.IgnorePhysicalReduction,
                cannotInflictAilments: offence.CannotInflictAilments);
        }
        finally
        {
            if (hasOverlay) m.Mods = savedMods;
        }

        if (hit)
        {
            var at = m.Walk.Position(now);
            var to = target.Walk.Position(now);
            player.Minions.OutputQueue.Add(new(
                InstanceProtocol.ActionHit(comp.EntityId, 0xffff, targetId,
                    flags: 0, kind: 0x03, code: 0, angle: HitAngle(to.X - at.X, to.Y - at.Y)),
                null, targetId, target, WorldId: comp.EntityId));

            // Apply support/skill on-hit debuffs (Withered, Covered in Ash, Crushed, skill Debuffs)
            if (!target.Dead)
            {
                if (offence.WitherChancePct > 0 && rng.Next(100) < offence.WitherChancePct)
                    ApplyWitherStacksLocked(target, 1, 2.0, k => m.Effects[k], now, comp.EntityId);
                if (offence.CoverInAshChancePct > 0 && rng.Next(100) < offence.CoverInAshChancePct)
                    target.CoveredInAshUntil = Math.Max(target.CoveredInAshUntil, now + 4.0 / target.RecipientDebuffRate);
                if (offence.CrushChancePct > 0 && rng.Next(100) < offence.CrushChancePct)
                    target.CrushedUntil = Math.Max(target.CrushedUntil, now + 4.0 / target.RecipientDebuffRate);
                foreach (var debuff in offence.Attack.Debuffs)
                    if (debuff.Row > 0 && debuff.Seconds > 0)
                        player.Area.Buffs.Set(targetId, global::BuffRows.Spec(debuff.Row, comp.EntityId, debuff.Stats, debuff.Seconds));
            }

            OnCompanionHitEnemyLocked(player, comp, target, now);
            if (player.Companion is { } activeComp && activeComp.EntityId == comp.EntityId)
                ReleaseCompanionIntuitiveLinkLocked(player, activeComp, to, now);
        }
        return hit;
    }

    static void OnCompanionHitEnemyLocked(Player player, CompanionRuntime comp, SpawnedMonster target, double now)
    {
        if (player.Companion is { } ac && ac.EntityId == comp.EntityId)
            TryApplyCompanionTauntLocked(player, ac, target, now);
    }

    // ---- Taking Damage (Guard / Post-Mitigation / Loyal Bodyguard Diversion), Recoup & Death ----------------------------------

    int ApplyCompanionDamageLocked(Player player, CompanionRuntime comp, int damage, double now, bool enqueueDeathPackets = true)
        => ApplyCompanionDamageLocked(player, comp, damage, now, enqueueDeathPackets, out _);

    int ApplyCompanionDamageLocked(Player player, CompanionRuntime comp, int damage, double now,
        bool enqueueDeathPackets, out List<uint>? cleanupIds)
    {
        cleanupIds = null;
        var m = comp.Monster;
        if (comp.Closed || m.Dead || m.Life == 0 || damage <= 0) return 0;
        if (m.Mods.Effects[Stat.BaseCannotBeDamaged] != 0) return 0;

        comp.LastDamagedAt = now;
        comp.ShieldPool.Interrupt(now);

        // Guard skill absorption (Molten Shell / Steelskin) before ES & Life
        int remaining = damage;
        if (comp.GuardPoolRemaining > 0 && now < comp.GuardUntil && comp.GuardAbsorbPct > 0)
        {
            int absorb = Math.Min(comp.GuardPoolRemaining, remaining * comp.GuardAbsorbPct / 100);
            comp.GuardPoolRemaining -= absorb;
            remaining -= absorb;
            if (remaining <= 0) return 0;
        }

        // Loyal Bodyguard recoup: if permanent mercenary's current Life < player's current Life, recoup 40% (HM 30%) over 4s
        CompanionRecoupHitLocked(player, m, comp.Mercenary.IsPermanent, remaining, comp.Recoup, now);

        if (comp.ShieldPool.Current > 0)
        {
            remaining = comp.ShieldPool.Damage(remaining, bypass: false, now);
            comp.LastSentEs = comp.ShieldPool.Current;
            player.Area.Life.Set(comp.EntityId, AreaLife.EnergyShieldPool, comp.ShieldPool.Current);
        }

        int lifeLost = (int)Math.Min(remaining, (long)m.Life);
        if (lifeLost > 0)
        {
            m.Life -= (uint)lifeLost;
            player.Area.Life.Set(comp.EntityId, AreaLife.LifePool, m.Life);
            if (enqueueDeathPackets)
                player.Minions.OutputQueue.Add(new([], null, comp.EntityId, m, PlayerMinions.OutputKind.Life));
        }

        if (m.Life == 0)
            cleanupIds = HandleCompanionDeathLocked(player, comp, enqueueDeathPackets);

        return lifeLost;
    }

    async Task<int> ApplyCompanionDamageAsync(Player player, CompanionRuntime comp, int amount, double now)
    {
        int taken;
        bool diedHere;
        uint lifeAfter;
        List<uint>? cleanupIds;
        using (ProjectileMutation(player))
        {
            bool wasDead = comp.Monster.Dead;
            taken = ApplyCompanionDamageLocked(player, comp, amount, now, enqueueDeathPackets: false, out cleanupIds);
            diedHere = !wasDead && comp.Monster.Dead;
            lifeAfter = comp.Monster.Life;
        }
        if (taken > 0 && !diedHere && !comp.Monster.Dead)
        {
            await SendWorldRecord(player, comp.EntityId, InstanceProtocol.LifeUpdate(comp.EntityId, lifeAfter),
                $"0x01b1 mercenary 0x{comp.EntityId:x} life {lifeAfter}");
        }
        if (diedHere)
        {
            if (cleanupIds is { Count: > 0 } && !player.Closed && !player.LeavingArea)
            {
                foreach (uint objId in cleanupIds)
                    await SendWorldRemoval(player, objId, $"0x02bd remove dead mercenary object 0x{objId:x}");
            }
            await Send(player, InstanceMercenaries.AreaSetDead(comp.Mercenary.Key),
                $"0x02c9 ruleset 0xa2 sub 5 mercenary key {comp.Mercenary.Key} died");
            await SendWorldRemoval(player, comp.EntityId, $"0x02bd remove dead mercenary 0x{comp.EntityId:x}");
        }
        return taken;
    }

    List<uint> HandleCompanionDeathLocked(Player player, CompanionRuntime comp, bool enqueuePackets)
    {
        var m = comp.Monster;
        m.Dead = true;
        m.Aggro = false;
        comp.Closed = true;
        var cleanupIds = CollectAndClearCompanionObjects(comp);
        ClearCompanionSharedAuras(player, comp.EntityId);
        ClearCompanionTauntsLocked(player, comp.EntityId);
        ClearCompanionSelfBuffs(player, comp);

        if (player.Companion?.EntityId == comp.EntityId)
            player.Companion = null;
        if (loadoutNpcs.TryGetValue(player, out var map))
            lock (map) map.Remove(comp.EntityId);
        lock (player.Npcs) player.Npcs.Remove(comp.EntityId);

        byte key = comp.Mercenary.Key;
        MutateRoster(player, r =>
        {
            if (r.Slots.TryGetValue(key, out var rec))
                rec.Dead = true;
        });

        SetStats(player, comp.EntityId, [(Stat.HideMiniLifeBar, 1)]);
        if (enqueuePackets)
        {
            foreach (uint objId in cleanupIds)
                player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(objId),
                    $"0x02bd remove dead mercenary object 0x{objId:x}", objId, m, RemovesObject: true));
            player.Minions.OutputQueue.Add(new(InstanceMercenaries.AreaSetDead(key),
                $"0x02c9 ruleset 0xa2 sub 5 mercenary key {key} died"));
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(comp.EntityId),
                $"0x02bd remove dead mercenary 0x{comp.EntityId:x}", comp.EntityId, m, RemovesObject: true));
        }
        player.Area.Monsters.Remove(comp.EntityId);
        player.Instance?.World.Area.Monsters.Remove(comp.EntityId);
        player.MinionOrigins.TryRemove(comp.EntityId, out _);
        RemoveEntitySync(player, comp.EntityId);
        companions.Remove(player);
        return cleanupIds;
    }
}
