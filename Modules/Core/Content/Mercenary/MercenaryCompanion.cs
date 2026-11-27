using GameServer.Core;
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
        public List<MonsterKitSkill> Kit { get; set; } = [];
        public readonly Dictionary<int, int> EffectToSkillRow = [];
        public readonly Dictionary<int, double> Cooldowns = [];
        public PlayerMinions.Aura[] SharedAuras { get; set; } = [];
        public double LastTick;
        public double LastDamagedAt = double.NegativeInfinity;
        public double RegenCarry;
        public PendingCompanionAttack? PendingAttack;
        public uint LastSentEs;
        public bool Closed;
    }

    sealed record PendingCompanionAttack(MonsterKitSkill Skill, uint TargetId, SpawnedMonster Target, double ContactAt, int Generation);

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
        var at = spawnAt ?? NextToPlayer(player, Math.Clamp(allied.ObjectSize - 1, 0, 127));

        string displayName = MercenaryTables.FormatName(merc.FirstNameHash32, merc.SecondNameHash32) ?? build.Name;
        uint id = await SpawnMonster(player, allied, merc.Level, MonsterRarity.Unique, at.X, at.Y,
            friendly: true, instanceOwned: false, scripted: true, ownedLooks: looks,
            mods: computed.Mods, extraStats: computed.ClientStats,
            owned: (_, _) => { },
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
            LastTick = AreaTime(player),
            LastSentEs = computed.MaxEnergyShield,
        };
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
        RebuildCompanionKitAndAuras(player, runtime, kind, build);
        if (runtime.SharedAuras.Length > 0)
            SyncCompanionSharedAuras(player, activeHandle, runtime.SharedAuras, AreaTime(player), scaleByLuminary: true);

        // Ensure client gets initial Defence & Luminary stats on entity
        SetStats(player, id, computed.ClientStats);
        context.Log($"{player.Layout.Id}: spawned {(merc.IsPermanent ? "permanent" : "temporary")} mercenary {build.Name} (key {key}, lvl {merc.Level}) as 0x{id:x}");
    }

    async Task DespawnCompanion(Player player)
    {
        if (!companions.TryGetValue(player, out var comp)) return;
        companions.Remove(player);
        comp.Closed = true;
        ClearCompanionSharedAuras(player, comp.EntityId);
        ClearCompanionTauntsLocked(player, comp.EntityId);
        if (player.Companion?.EntityId == comp.EntityId)
            player.Companion = null;
        if (loadoutNpcs.TryGetValue(player, out var map))
            lock (map) map.Remove(comp.EntityId);
        RemoveEntitySync(player, comp.EntityId);
        lock (player.Npcs) player.Npcs.Remove(comp.EntityId);
        player.Area.Monsters.Remove(comp.EntityId);
        player.Instance?.World.Area.Monsters.Remove(comp.EntityId);
        player.MinionOrigins.TryRemove(comp.EntityId, out _);
        if (!player.Closed && !player.LeavingArea)
            await SendWorldRemoval(player, comp.EntityId, $"0x02bd despawn mercenary companion 0x{comp.EntityId:x}");
    }

    Task OnPlayerLeaving(Player player, string why)
    {
        CloseCompanionTransient(player);
        return Task.CompletedTask;
    }

    void CloseCompanionTransient(Player player)
    {
        if (companions.TryGetValue(player, out var comp))
        {
            comp.Closed = true;
            ClearCompanionSharedAuras(player, comp.EntityId);
            ClearCompanionTauntsLocked(player, comp.EntityId);
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

    // ---- Kit & Shared Non-Curse Aura Construction -----------------------------------------------------------------------------

    void RebuildCompanionKitAndAuras(Player player, CompanionRuntime comp, MercenaryTables.Class kind, MercenaryTables.Build build)
    {
        var skills = SkillsFor(player);
        var sharedAuras = new List<PlayerMinions.Aura>();
        var combatEffects = new List<int>();
        comp.EffectToSkillRow.Clear();
        int gearAuraPct = comp.Monster.Mods.Effects[Stat.AuraEffectPlusPct];

        foreach (var rolled in comp.Mercenary.Skills)
        {
            if (MercenaryTables.SkillOf(rolled.SkillRow) is not { } skillRow) continue;
            int effect = skillRow.GrantedEffect;
            if (effect < 0) continue;

            if (MercenaryTables.FriendlyAuraFamily(skills, effect) is { } family)
            {
                ushort buffRow = AuraCatalog.BuffRow(family);
                var baseStats = PlayerMinions.AuraStatsAt(skills, Stats, effect, family, comp.Mercenary.Level);
                var suppStats = MercenaryTables.SupportStatsOf([rolled]);
                // Generosity's MercenarySupports stats are non_curse_aura_effect_+% and aura_cannot_affect_self.
                int generosityBonus = suppStats.GetValueOrDefault(Stat.NonCurseAuraEffectPlusPct)
                    + suppStats.GetValueOrDefault(Stat.AuraEffectPlusPct);
                bool noSelf = suppStats.GetValueOrDefault(Stat.AuraCannotAffectSelf) > 0;
                int effectLevel = skills.MaxLevelAt(effect, comp.Mercenary.Level);
                var own = skills.EffectStats(effect, effectLevel).ToDictionary(s => s.Item1, s => s.Item2);
                int radius = SkillArea.Radius(AuraCatalog.BaseRadius, k => own.GetValueOrDefault(k), spell: false);

                var aura = new PlayerMinions.Aura(family, buffRow, Math.Max(40, radius), baseStats)
                {
                    SharesWithAllies = true,
                    AffectsSource = !noSelf,
                };
                int ownAuraBonus = gearAuraPct + generosityBonus;
                if (ownAuraBonus != 0)
                    aura = ScaleCompanionNonCurseAura(aura, ownAuraBonus);
                sharedAuras.Add(aura);
            }
            else
            {
                combatEffects.Add(effect);
                comp.EffectToSkillRow[effect] = rolled.SkillRow;
            }
        }

        comp.SharedAuras = [.. sharedAuras];
        if (player.Companion is { } ac && ac.EntityId == comp.EntityId)
            SyncCompanionSharedAuras(player, ac, comp.SharedAuras, AreaTime(player), scaleByLuminary: true);

        if (comp.Monster.Variety is { } variety)
        {
            var kit = combatEffects.Count > 0
                ? BossKits.KitFromEffects(player, variety, comp.Mercenary.Level, combatEffects)
                : [];
            if (!kit.Any(k => k.Attack.Use is null && k.TriggerThreshold == 0) && MercenaryTables.MeleeFallbackEffect >= 0)
            {
                var withFallback = BossKits.KitFromEffects(player, variety, comp.Mercenary.Level,
                    [MercenaryTables.MeleeFallbackEffect, .. combatEffects]);
                if (withFallback.Count > 0) kit = withFallback;
            }
            comp.Kit = [.. kit];
        }
    }

    // ---- Companion Tick (AI, Follow/Teleport, Regen/Recoup/ES, Auras, Skills) -------------------------------------------------

    Task TickCompanion(Player player, double now)
    {
        if (!companions.TryGetValue(player, out var comp) || comp.Closed) return Task.CompletedTask;
        var m = comp.Monster;
        if (player.Closed || player.LeavingArea || player.Paused) return Task.CompletedTask;
        if (m.Dead || m.Life == 0 || !player.Area.Monsters.TryGetValue(comp.EntityId, out var held) || !ReferenceEquals(held, m))
        {
            ClearCompanionSharedAuras(player, comp.EntityId);
            ClearCompanionTauntsLocked(player, comp.EntityId);
            return Task.CompletedTask;
        }

        double elapsed = Math.Max(0, now - comp.LastTick);
        comp.LastTick = now;

        // 1. Life regeneration, Loyal Bodyguard Recoup, Out-of-Combat Regen, Energy Shield recharge
        bool lifeChanged = StepCompanionResourcesLocked(comp, now, elapsed);
        if (lifeChanged)
            player.Minions.OutputQueue.Add(new([], null, comp.EntityId, m, PlayerMinions.OutputKind.Life));
        if (comp.ShieldPool.Current != comp.LastSentEs)
        {
            comp.LastSentEs = comp.ShieldPool.Current;
            player.Area.Life.Set(comp.EntityId, AreaLife.EnergyShieldPool, comp.ShieldPool.Current);
        }

        // 2. Non-curse aura sharing (scaled by Bestowed Knighthood for permanent mercenaries via SharedMinionAuraEmitters)
        if (comp.SharedAuras.Length > 0 && player.Companion is { } ac && ac.EntityId == comp.EntityId)
            SyncCompanionSharedAuras(player, ac, comp.SharedAuras, now, scaleByLuminary: true);

        // Keep NPC click coordinates synchronized with current walk position (only replace record when moved so Npcs.TalkToNpc reference lookup stays stable)
        var here = m.Walk.Position(now);
        lock (player.Npcs)
            if (player.Npcs.TryGetValue(comp.EntityId, out var npc) && (npc.X != here.X || npc.Y != here.Y))
                player.Npcs[comp.EntityId] = npc with { X = here.X, Y = here.Y };

        if (player.PlayerLife == 0) return Task.CompletedTask;

        // 3. Resolve pending attack contact
        if (comp.PendingAttack is { } pending)
        {
            if (pending.Generation != m.ActionGeneration)
            {
                comp.PendingAttack = null;
            }
            else if (now >= pending.ContactAt)
            {
                comp.PendingAttack = null;
                CommitCompanionHitLocked(player, comp, pending, now);
            }
            else return Task.CompletedTask;
        }

        if (now < m.BusyUntil || now < m.FlinchUntil) return Task.CompletedTask;
        double actionRate = MonsterActionFactor(m, now);
        if (actionRate <= 0) return Task.CompletedTask;

        var ownerAt = player.Walk.Position(now);

        // 4. Teleport to player if too far away
        if (Dist(here, ownerAt) > CompanionTeleportDistance)
        {
            var dest = NextToPlayer(player, LivingCollision.Size(m), comp.EntityId);
            m.Walk = player.NewMonsterWalk(comp.EntityId, dest.X, dest.Y, m.Walk.Speed, LivingCollision.Size(m), false);
            m.ActionGeneration++;
            m.LastTarget = null;
            player.Minions.OutputQueue.Add(new(
                InstanceProtocol.Teleport(comp.EntityId, dest.X, dest.Y),
                "mercenary teleports to player", comp.EntityId, m, WorldId: comp.EntityId));
            return Task.CompletedTask;
        }

        if (IsTownOrHideout(player))
        {
            FollowOwnerLocked(player, comp, here, ownerAt, now);
            return Task.CompletedTask;
        }

        // 5. Find nearest hostile target within aggro/owner leash range
        var target = FindCompanionTargetLocked(player, here, ownerAt, now);
        if (target is not { } tgt)
        {
            FollowOwnerLocked(player, comp, here, ownerAt, now);
            return Task.CompletedTask;
        }

        // 6. Pick a ready skill from the mercenary's kit
        double dist = Dist(here, tgt.At);
        var chosen = ChooseCompanionSkill(comp, dist, tgt.Monster, now);
        if (chosen is null)
        {
            double desiredRange = comp.Kit.Where(k => k.Attack.Use is null)
                .Select(k => k.Attack.Range).DefaultIfEmpty(8).Min();
            double factor = dist > 0 ? Math.Max(0, desiredRange - 2) / dist : 0;
            var approach = ((int)Math.Round(tgt.At.X + (here.X - tgt.At.X) * factor),
                (int)Math.Round(tgt.At.Y + (here.Y - tgt.At.Y) * factor));
            MoveCompanionLocked(player, comp, here, approach, now);
            return Task.CompletedTask;
        }

        // 7. Start the skill action
        StartCompanionSkillLocked(player, comp, chosen, here, tgt.Id, tgt.Monster, tgt.At, now, actionRate);
        return Task.CompletedTask;
    }

    static bool StepCompanionResourcesLocked(CompanionRuntime comp, double now, double elapsed)
    {
        if (elapsed <= 0) return false;
        var m = comp.Monster;
        double healing = 0;

        // Base + gear + extra-stats life regeneration
        double regenPctPerMin = m.Mods.Effects[Stat.LifeRegenerationRatePerMinutePct];
        double regenFlatPerMin = m.Mods.Effects[Stat.BaseLifeRegenerationRatePerMinute];
        healing += elapsed * (m.MaxLife * regenPctPerMin / 6000.0 + regenFlatPerMin / 60.0);

        // Out-of-combat mercenary regeneration (4s without taking damage)
        if (m.Life < m.MaxLife && now - comp.LastDamagedAt >= OutOfCombatDelaySeconds)
            healing += elapsed * (m.MaxLife * OutOfCombatRegenPerSecondFraction);

        // Loyal Bodyguard 4-second recoup ticks
        healing += comp.Recoup.Take(now);

        // Energy Shield recharge
        comp.ShieldPool.Advance(now - elapsed, now, stat => m.Mods.Effects[stat], alive: true);

        if (healing <= 0 || m.Life >= m.MaxLife || m.Effects[Stat.CannotRecoverLife] != 0) return false;
        comp.RegenCarry += healing;
        int whole = (int)Math.Floor(comp.RegenCarry);
        if (whole <= 0) return false;
        comp.RegenCarry -= whole;
        uint before = m.Life;
        m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + whole);
        return m.Life != before;
    }

    // ---- Movement, Targeting, Skill Casting & Combat Hits ---------------------------------------------------------------------

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

    static MonsterKitSkill? ChooseCompanionSkill(CompanionRuntime comp, double distance, SpawnedMonster target, double now)
    {
        int targetSize = Math.Max(0, LivingCollision.Size(target));
        MonsterKitSkill? best = null;
        foreach (var skill in comp.Kit)
        {
            if (skill.TriggerThreshold > 0 || skill.Attack.Use is not null) continue;
            if (comp.Cooldowns.TryGetValue(skill.Effect, out double readyAt) && now < readyAt) continue;
            if (distance > skill.Attack.Range + targetSize) continue;
            if (best is null || (skill.CooldownSeconds > 0 && best.CooldownSeconds <= 0))
                best = skill;
        }
        return best;
    }

    static void StartCompanionSkillLocked(Player player, CompanionRuntime comp, MonsterKitSkill skill,
        (int X, int Y) here, uint targetId, SpawnedMonster target, (int X, int Y) targetAt, double now, double actionRate)
    {
        var m = comp.Monster;
        var attack = skill.Attack;
        // Include per-skill MercenarySupports attack/cast speed modifiers
        double supportSpeed = 1.0;
        if (comp.EffectToSkillRow.TryGetValue(skill.Effect, out int skillRow))
        {
            var supp = MercenaryTables.SupportStatsOf(comp.Mercenary.Skills, forSkillRow: skillRow);
            int speedPct = (attack.Spell ? supp.GetValueOrDefault(Stat.BaseCastSpeedPlusPct) : supp.GetValueOrDefault(Stat.AttackSpeedPlusPct))
                + supp.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPct);
            if (speedPct != 0) supportSpeed = Math.Max(0.2, 1.0 + speedPct / 100.0);
        }
        double duration = Math.Max(0.15, attack.Seconds / Math.Max(0.2, actionRate * supportSpeed));
        int gen = ++m.ActionGeneration;
        m.BusyUntil = now + duration;
        m.LastTarget = null;
        m.Walk.Order(here.X, here.Y, now);
        if (skill.CooldownSeconds > 0)
            comp.Cooldowns[skill.Effect] = now + skill.CooldownSeconds;

        double contactAt = now + duration * (attack.Release?.Fraction ?? 0.5);
        comp.PendingAttack = new PendingCompanionAttack(skill, targetId, target, contactAt, gen);

        player.Minions.OutputQueue.Add(new(
            InstanceProtocol.ActionStarted(comp.EntityId, here.X, here.Y, targetAt.X, targetAt.Y,
                attack.SkillId, 0xffff, player.MonsterOrderFlags, targetObjectId: targetId),
            null, comp.EntityId, m, WorldId: comp.EntityId));
    }

    void CommitCompanionHitLocked(Player player, CompanionRuntime comp, PendingCompanionAttack pending, double now)
    {
        var m = comp.Monster;
        var target = pending.Target;
        if (m.Dead || target.Dead || target.Life == 0 || !MinionMayTarget(target)
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(pending.TargetId), target))
            return;

        var attack = pending.Skill.Attack;
        var rng = AreaRng(player);

        // Collect per-skill MercenarySupports stats and active Flame Link / Destructive Link offence bonuses
        Dictionary<uint, int>? supportStats = comp.EffectToSkillRow.TryGetValue(pending.Skill.Effect, out int skillRow)
            ? MercenaryTables.SupportStatsOf(comp.Mercenary.Skills, forSkillRow: skillRow)
            : null;
        CompanionLinkOffence linkOff = player.Companion is { } ac && ac.EntityId == comp.EntityId
            ? CompanionLinkOffenceAt(player, ac, PlayerStatValues(player), now)
            : default;

        var damage = attack.Damage;
        if (linkOff.FireMax > 0)
            damage = damage.Add(DamageType.Fire, new DamageRange(linkOff.FireMin, linkOff.FireMax));

        // Apply per-skill MercenarySupports damage scaling & Luminary Noble Blood / Legendary Arms multipliers
        double mult = 1.0;
        if (supportStats is not null && supportStats.GetValueOrDefault(Stat.DamagePlusPct) is int suppInc and not 0)
            mult *= Math.Max(0.1, 1.0 + suppInc / 100.0);
        if (comp.Mercenary.IsPermanent)
            mult *= LuminaryBonusesOf(player).MercenaryDamageMoreFactor(comp.Computed.EquippedUniqueCount);
        if (Math.Abs(mult - 1.0) > 1e-6)
            damage = damage.Scale(mult);

        attack = attack with { Damage = damage };

        // Temporarily overlay per-skill support stats & Destructive/Flame Link stats onto m.Mods for ScriptedAllyHitLocked
        var savedMods = m.Mods;
        bool hasOverlay = (supportStats is { Count: > 0 })
            || linkOff.Ignite != 0 || linkOff.CritMultiplier != 0 || linkOff.Accuracy != 0 || linkOff.MainHandCrit is not null;
        if (hasOverlay)
        {
            var merged = new Dictionary<uint, int>(comp.Computed.StatValues);
            void AddStat(uint key, int delta)
            {
                if (delta != 0) merged[key] = ActorScale.Saturate((long)merged.GetValueOrDefault(key) + delta);
            }
            if (supportStats is not null)
                foreach (var (k, v) in supportStats)
                    if (k != Stat.DamagePlusPct) AddStat(k, v);
            AddStat(Stat.BaseChanceToIgnitePct, linkOff.Ignite);
            AddStat(Stat.BaseCriticalStrikeMultiplierPlus, linkOff.CritMultiplier);
            AddStat(Stat.AccuracyRatingPlusPct, linkOff.Accuracy);
            if (!attack.Spell && linkOff.MainHandCrit is int linkedCrit)
            {
                // MonsterModEffects computes crit as base(500) * (100 + CriticalStrikeChancePlusPct) / 100; override to player's MainHandCrit permyriad
                const int monsterBaseCritPermyriad = 500;
                int targetInc = (int)Math.Round(linkedCrit * 100.0 / monsterBaseCritPermyriad) - 100;
                merged[Stat.CriticalStrikeChancePlusPct] = targetInc;
            }
            m.Mods = savedMods with { Effects = new MonsterModEffects(merged) };
        }

        bool hit;
        try
        {
            hit = ScriptedAllyHitLocked(player, comp.EntityId, m, pending.TargetId, target, attack, now, rng, attack.Ranged);
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
                InstanceProtocol.ActionHit(comp.EntityId, 0xffff, pending.TargetId,
                    flags: 0, kind: 0x03, code: 0, angle: HitAngle(to.X - at.X, to.Y - at.Y)),
                null, pending.TargetId, target, WorldId: comp.EntityId));
            OnCompanionHitEnemyLocked(player, comp, target, now);
            if (player.Companion is { } activeComp && activeComp.EntityId == comp.EntityId)
                ReleaseCompanionIntuitiveLinkLocked(player, activeComp, to, now);
        }
    }

    static void OnCompanionHitEnemyLocked(Player player, CompanionRuntime comp, SpawnedMonster target, double now)
    {
        if (player.Companion is { } ac && ac.EntityId == comp.EntityId)
            TryApplyCompanionTauntLocked(player, ac, target, now);
    }

    // ---- Taking Damage (Post-Mitigation / Loyal Bodyguard Diversion), Recoup & Death ------------------------------------------

    int ApplyCompanionDamageLocked(Player player, CompanionRuntime comp, int damage, double now, bool enqueueDeathPackets = true)
    {
        var m = comp.Monster;
        if (comp.Closed || m.Dead || m.Life == 0 || damage <= 0) return 0;
        if (m.Mods.Effects[Stat.BaseCannotBeDamaged] != 0) return 0;

        comp.LastDamagedAt = now;
        comp.ShieldPool.Interrupt(now);

        // Loyal Bodyguard recoup: if permanent mercenary's current Life < player's current Life, recoup 40% (HM 30%) over 4s
        CompanionRecoupHitLocked(player, m, comp.Mercenary.IsPermanent, damage, comp.Recoup, now);

        int remaining = damage;
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
            HandleCompanionDeathLocked(player, comp, enqueueDeathPackets);

        return lifeLost;
    }

    async Task<int> ApplyCompanionDamageAsync(Player player, CompanionRuntime comp, int amount, double now)
    {
        int taken = ApplyCompanionDamageLocked(player, comp, amount, now, enqueueDeathPackets: false);
        if (taken > 0 && !comp.Monster.Dead)
        {
            await SendWorldRecord(player, comp.EntityId, InstanceProtocol.LifeUpdate(comp.EntityId, comp.Monster.Life),
                $"0x01b1 mercenary 0x{comp.EntityId:x} life {comp.Monster.Life}");
        }
        if (comp.Monster.Dead)
        {
            await Send(player, InstanceMercenaries.AreaSetDead(comp.Mercenary.Key),
                $"0x02c9 ruleset 0xa2 sub 5 mercenary key {comp.Mercenary.Key} died");
            await SendWorldRemoval(player, comp.EntityId, $"0x02bd remove dead mercenary 0x{comp.EntityId:x}");
        }
        return taken;
    }

    void HandleCompanionDeathLocked(Player player, CompanionRuntime comp, bool enqueuePackets)
    {
        var m = comp.Monster;
        m.Dead = true;
        m.Aggro = false;
        comp.Closed = true;
        comp.PendingAttack = null;
        ClearCompanionSharedAuras(player, comp.EntityId);
        ClearCompanionTauntsLocked(player, comp.EntityId);

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
    }
}
