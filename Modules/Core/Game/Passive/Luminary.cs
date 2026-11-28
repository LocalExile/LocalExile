using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>
    /// Resolved Luminary Ascendancy bonuses for a player (from allocated passives, <see cref="GameEvents.PlayerStatSources"/>,
    /// or <see cref="GameEvents.Stats"/>). Per retail 3.29.3c/d (<c>ReminderTextPermanentMercenary</c>), all Luminary
    /// passives apply only to permanent mercenaries (<c>is_permanent_mercenary == 1</c>), never temporary mercenaries.
    /// </summary>
    public readonly record struct LuminaryBonuses(
        bool CanHavePermanentFollower,
        int DamageMoreFromLuminaryPct,
        int DamageMorePerUniquePct,
        int DamageIncreasedPct,
        int MaxLifeIncreasedPct,
        int NonCurseAuraEffectPct,
        int TauntOnHitPct,
        int RecoupIfLifeLowerPct,
        int DivertIfLifeHigherPct,
        bool NoDeathOnLinkedMercenaryDeath,
        bool InfiniteLinkDuration,
        int LinkCostMorePct,
        bool GoldenGlory,
        bool CanUseUniqueRing,
        bool CanUseUniqueAmulet,
        bool CanUseUniqueBelt,
        bool CanUseUniqueHelmet,
        bool CanUseUniqueBoots,
        bool CanUseUniqueGloves,
        bool CanUseUniqueWeaponsShieldsAndQuivers)
    {
        /// <summary>Combined final damage multiplier for a permanent mercenary (and its minions):
        /// Noble Blood (-30% less) multiplied by Legendary Arms (+8%/+4% more per equipped unique).</summary>
        public double MercenaryDamageMoreFactor(int equippedUniqueCount)
        {
            double luminary = Math.Max(0, 1 + DamageMoreFromLuminaryPct / 100.0);
            long perUnique = (long)DamageMorePerUniquePct * Math.Max(0, equippedUniqueCount);
            double uniques = Math.Max(0, 1 + perUnique / 100.0);
            return luminary * uniques;
        }

        /// <summary>Non-curse aura effect multiplier for a permanent mercenary (Bestowed Knighthood: +50% / HM +30%).</summary>
        public double NonCurseAuraEffectFactor => Math.Max(0, 1 + NonCurseAuraEffectPct / 100.0);

        /// <summary>Whether the player's Luminary stats permit a permanent mercenary to equip a Unique in the given
        /// equipment row (1=BodyArmour [always false in 3.29], 2/3=Weapon/Offhand, 4=Helm, 5=Amulet, 6/7/102=Ring,
        /// 8=Gloves, 9=Boots, 10=Belt).</summary>
        public bool CanUseUniqueInSlot(byte equipRow) => equipRow switch
        {
            PlayerInventories.Ring1 or PlayerInventories.Ring2 or PlayerInventories.Ring3 => CanUseUniqueRing,
            PlayerInventories.Amulet => CanUseUniqueAmulet,
            PlayerInventories.Belt => CanUseUniqueBelt,
            PlayerInventories.Helmet => CanUseUniqueHelmet,
            PlayerInventories.Boots => CanUseUniqueBoots,
            PlayerInventories.Gloves => CanUseUniqueGloves,
            PlayerInventories.Weapon1 or PlayerInventories.Offhand1 => CanUseUniqueWeaponsShieldsAndQuivers,
            PlayerInventories.BodyArmour => false, // hardcoded block at 0x142481017 in 3.29.3c/d
            _ => false,
        };
    }

    /// <summary>Reads the player's current Luminary bonuses from the full stat snapshot (identical whether stats come from
    /// allocated Luminary passives, <see cref="GameEvents.PlayerStatSources"/>, or <see cref="GameEvents.Stats"/>).</summary>
    public static LuminaryBonuses LuminaryBonusesOf(Player player) =>
        LuminaryBonusesFrom(PlayerStatValues(player));

    /// <summary>Pure projection of Luminary bonuses from any stat dictionary.</summary>
    public static LuminaryBonuses LuminaryBonusesFrom(IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        return new(
            CanHavePermanentFollower: Get(Stat.CanHaveActiveMercenaryFollower) > 0,
            DamageMoreFromLuminaryPct: Get(Stat.PermanenetMercenaryDamagePlusPctFinalAndMinionDamagePlusPctFinalFromLuminary),
            DamageMorePerUniquePct: Get(Stat.PermanenetMercenaryDamagePlusPctFinalAndMinionDamagePlusPctFinalPerEquippedUnique),
            DamageIncreasedPct: Get(Stat.PermanenetMercenaryDamagePlusPctAndMinionDamagePlusPct),
            MaxLifeIncreasedPct: Get(Stat.PermanenetMercenaryMaximumLifePlusPctAndMinionMaximumLifePlusPct),
            NonCurseAuraEffectPct: Get(Stat.PermanenetMercenaryNonCurseAuraEffectPlusPct),
            TauntOnHitPct: Get(Stat.PermanenetMercenaryChanceToTauntOnHitPct),
            RecoupIfLifeLowerPct: Get(Stat.PermanenetMercenaryDamageTakenRecoupedAsLifePctIfLifeLowerThanParentsLife),
            DivertIfLifeHigherPct: Get(Stat.DamageRemovedFromYourNearestPermanentMercenaryBeforeLifeOrEsPctIfTheirCurrentLifeIsHigherThanYours),
            NoDeathOnLinkedMercenaryDeath: Get(Stat.ObjectsLinkedToYourPermanenetMercenariesDoNotDieWhenTheyDie) != 0,
            InfiniteLinkDuration: Get(Stat.LinkSkillHaveInfiniteAttachmentDuration) != 0,
            LinkCostMorePct: Get(Stat.LinkSkillCostPlusPctFinal),
            GoldenGlory: Get(Stat.LightRadiusAdditiveModifiersApplyToBuffEffectOfLinkSkillsIfLinkTargetIsYourPermanentMercenary) != 0,
            CanUseUniqueRing: Get(Stat.MercenaryCanUseUniqueRing) != 0,
            CanUseUniqueAmulet: Get(Stat.MercenaryCanUseUniqueAmulet) != 0,
            CanUseUniqueBelt: Get(Stat.MercenaryCanUseUniqueBelt) != 0,
            CanUseUniqueHelmet: Get(Stat.MercenaryCanUseUniqueHelmet) != 0,
            CanUseUniqueBoots: Get(Stat.MercenaryCanUseUniqueBoots) != 0,
            CanUseUniqueGloves: Get(Stat.MercenaryCanUseUniqueGloves) != 0,
            CanUseUniqueWeaponsShieldsAndQuivers: Get(Stat.MercenaryCanUseUniqueWeaponsShieldsAndQuivers) != 0);
    }

    /// <summary>Builds the mercenary-entity mirror stats to publish on a spawned mercenary's <c>Stats</c> component
    /// (both for client <c>CanMercenaryEquipItem</c> checks outside town/hideout and for live entity stat inspection).
    /// Returns an empty list for temporary mercenaries.</summary>
    public static List<(uint Key, int Value)> LuminaryCompanionStats(Player player, int equippedUniqueCount, bool isPermanent)
    {
        if (!isPermanent) return [];
        var b = LuminaryBonusesOf(player);
        var list = new List<(uint Key, int Value)>
        {
            (Stat.IsPermanentMercenary, 1),
        };
        void AddIf(uint key, int value) { if (value != 0) list.Add((key, value)); }
        AddIf(Stat.DamagePlusPctFinalFromLuminary, b.DamageMoreFromLuminaryPct);
        AddIf(Stat.MinionDamagePlusPctFinalFromLuminary, b.DamageMoreFromLuminaryPct);
        AddIf(Stat.DamagePlusPctAndMinionDamagePlusPct, b.DamageIncreasedPct);
        AddIf(Stat.MaximumLifePlusPctAndMinionMaximumLifePlusPct, b.MaxLifeIncreasedPct);
        int perUniqueTotal = ActorScale.Saturate((long)b.DamageMorePerUniquePct * Math.Max(0, equippedUniqueCount));
        AddIf(Stat.DamagePlusPctFinalPerEquippedUnique, perUniqueTotal);
        AddIf(Stat.MinionDamagePlusPctFinalPerEquippedUnique, perUniqueTotal);
        AddIf(Stat.DamageTakenRecoupedAsLifePctIfLifeLowerThanParentsLife, b.RecoupIfLifeLowerPct);
        AddIf(Stat.ObjectsLinkedToYouDoNotDieWhenYouDie, b.NoDeathOnLinkedMercenaryDeath ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueRing, b.CanUseUniqueRing ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueAmulet, b.CanUseUniqueAmulet ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueBelt, b.CanUseUniqueBelt ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueHelmet, b.CanUseUniqueHelmet ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueBoots, b.CanUseUniqueBoots ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueGloves, b.CanUseUniqueGloves ? 1 : 0);
        AddIf(Stat.MercenaryCanUseUniqueWeaponsShieldsAndQuivers, b.CanUseUniqueWeaponsShieldsAndQuivers ? 1 : 0);
        return list;
    }

    /// <summary>Scales a mercenary's non-curse aura stat values by <paramref name="nonCurseAuraEffectPct"/> (e.g. +50%
    /// from Bestowed Knighthood).</summary>
    public static PlayerMinions.Aura ScaleCompanionNonCurseAura(PlayerMinions.Aura aura, int nonCurseAuraEffectPct)
    {
        if (nonCurseAuraEffectPct == 0) return aura;
        double factor = Math.Max(0, 1 + nonCurseAuraEffectPct / 100.0);
        var scaled = aura.Stats
            .Select(s => (s.Key, (int)Math.Round(s.Value * factor, MidpointRounding.AwayFromZero)))
            .ToArray();
        return aura with
        {
            Stats = scaled,
            BuffValues = aura.BuffValues is null ? null : [.. scaled.Select(s => unchecked((uint)s.Item2))],
        };
    }

    // ---- Active companion helpers ----

    internal static bool CompanionAliveInArea(Player player, ActiveCompanion? companion) =>
        companion is not null
        && player.Area.Monsters.TryGetValue(companion.EntityId, out var held)
        && ReferenceEquals(held, companion.Monster)
        && !held.Dead && held.Life > 0;

    internal static bool IsActivePermanentCompanion(Player player, uint entityId) =>
        player.Companion is { IsPermanent: true } comp
        && comp.EntityId == entityId
        && CompanionAliveInArea(player, comp);

    /// <summary>Applies Loyal Bodyguard damage diversion (before Ward, ES, Mana and Life) from the player to their
    /// nearest living permanent mercenary when the mercenary's current Life is strictly higher than the player's current Life.</summary>
    internal static int CompanionTakeOwnerHitLocked(Player player, int damage, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (damage <= 0 || player.Companion is not { IsPermanent: true } comp || !CompanionAliveInArea(player, comp))
            return 0;
        if (comp.Monster.Invulnerable || comp.Monster.Effects[Stat.BaseCannotBeDamaged] != 0)
            return 0;
        int pct = Math.Clamp(stats.GetValueOrDefault(
            Stat.DamageRemovedFromYourNearestPermanentMercenaryBeforeLifeOrEsPctIfTheirCurrentLifeIsHigherThanYours), 0, 100);
        if (pct <= 0) return 0;
        if (comp.Monster.Life <= player.PlayerLife) return 0;
        int share = (int)((long)damage * pct / 100);
        if (share <= 0) return 0;
        if (comp.ApplyDamageLocked is { } apply)
            return Math.Clamp(apply(player, share, now), 0, share);
        // Fallback when no module damage handler is attached (e.g. unit tests): directly subtract from companion Life.
        int loss = (int)Math.Min(share, (long)comp.Monster.Life);
        if (loss <= 0) return 0;
        comp.Monster.Life -= (uint)loss;
        if (comp.Monster.Life == 0) comp.Monster.Dead = true;
        player.Area.Life.Set(comp.EntityId, AreaLife.LifePool, comp.Monster.Life);
        return loss;
    }

    /// <summary>Loyal Bodyguard recoup half: if a permanent mercenary's current Life is strictly lower than the player's
    /// current Life when it takes hit damage, adds 40% (HM 30%) of the damage taken to its <paramref name="recoup"/>
    /// recovery over 4 seconds (scaled by the mercenary's life recovery rate).</summary>
    public static double CompanionRecoupHitLocked(Player player, SpawnedMonster companion, bool isPermanent,
        int damageTaken, RecoupRecovery recoup, double now)
    {
        if (!isPermanent || damageTaken <= 0 || companion.Dead || companion.Life == 0
            || companion.Effects[Stat.CannotRecoverLife] != 0)
            return 0;
        int pct = PlayerStatValues(player).GetValueOrDefault(
            Stat.PermanenetMercenaryDamageTakenRecoupedAsLifePctIfLifeLowerThanParentsLife);
        if (pct <= 0 || companion.Life >= player.PlayerLife) return 0;
        double rate = Math.Max(0, 1 + companion.Effects[Stat.LifeRecoveryRatePlusPct] / 100.0);
        double amount = damageTaken * (pct / 100.0) * rate;
        if (amount <= 0) return 0;
        recoup.Add(now, amount, 4.0);
        return amount;
    }

    // ---- Companion Taunt on Hit (Bestowed Knighthood) ----

    sealed record CompanionTauntEntry(Player Owner, ActiveCompanion Companion, double Until);
    static readonly RuleTable<SpawnedMonster, CompanionTauntEntry> CompanionTauntSources = new();

    /// <summary>Rolls and applies Taunt on hit from an active companion onto a hostile monster (including Bestowed
    /// Knighthood's 100% chance on permanent mercenaries).</summary>
    public static bool TryApplyCompanionTauntLocked(Player player, ActiveCompanion companion, SpawnedMonster target,
        double now, int extraChancePct = 0)
    {
        if (!CompanionAliveInArea(player, companion) || target.Dead || target.Life == 0 || !target.Hostile
            || target.Effects[Stat.CannotBeTaunted] != 0)
            return false;
        long chance = (companion.IsPermanent
                ? PlayerStatValues(player).GetValueOrDefault(Stat.PermanenetMercenaryChanceToTauntOnHitPct) : 0)
            + companion.Monster.Effects[Stat.ChanceToTauntOnHitPct]
            + extraChancePct;
        if (chance <= 0 || !RandomRolls.Chance((int)Math.Min(100, chance), 100, AreaRng(player)))
            return false;
        double seconds = FlaskGrounds.TauntSeconds / target.RecipientDebuffRate;
        double until = now + seconds;
        CompanionTauntSources.AddOrUpdate(target, new(player, companion, until));
        player.Area.Buffs.Set(target.ObjectId, new(FlaskGrounds.TauntedBuffRow, companion.EntityId,
            Values: TauntedBuffValues(companion.EntityId), U68: Buff.Seconds(seconds * target.RecipientDebuffRate)));
        target.PresentationChanged?.Invoke();
        return true;
    }

    internal static ActiveCompanion? ActiveCompanionTaunter(Player player, SpawnedMonster enemy, double now)
    {
        if (!CompanionTauntSources.TryGetValue(enemy, out var entry)) return null;
        if (entry.Until <= now || !CompanionAliveInArea(entry.Owner, entry.Companion)
            || !ReferenceEquals(entry.Owner.Area, player.Area))
        {
            if (entry.Until <= now || entry.Companion.Monster.Dead)
                player.Area.Buffs.Clear(enemy.ObjectId, FlaskGrounds.TauntedBuffRow, entry.Companion.EntityId);
            CompanionTauntSources.Remove(enemy);
            return null;
        }
        return entry.Companion;
    }

    internal static bool IsTauntedByPlayerCompanion(Player player, SpawnedMonster enemy, double now) =>
        player.Companion is { } comp && ReferenceEquals(ActiveCompanionTaunter(player, enemy, now), comp);

    /// <summary>Clears any active taunt buffs owned by a despawned or killed companion.</summary>
    public static void ClearCompanionTauntsLocked(Player player, uint companionId)
    {
        foreach (var (id, monster) in player.Area.Monsters)
        {
            if (!CompanionTauntSources.TryGetValue(monster, out var entry) || entry.Companion.EntityId != companionId)
                continue;
            CompanionTauntSources.Remove(monster);
            player.Area.Buffs.Clear(id, FlaskGrounds.TauntedBuffRow, companionId);
            monster.PresentationChanged?.Invoke();
        }
    }

    /// <summary>PoE Taunt penalty: a hostile monster taunted by an active companion deals 10% less damage to targets
    /// other than that companion (the player and minions).</summary>
    internal static double CompanionTauntedHitAgainstOtherFactor(Player player, SpawnedMonster attacker, double now) =>
        ActiveCompanionTaunter(player, attacker, now) is not null ? 0.9 : 1.0;

    // ---- Hostile Enemy Targeting & Hits on Active Companion ----

    sealed class CompanionIncomingHit(uint enemy, SpawnedMonster monster, ActiveCompanion target, MonsterAttack attack, int generation)
    {
        public readonly uint Enemy = enemy;
        public readonly SpawnedMonster Monster = monster;
        public readonly ActiveCompanion Target = target;
        public readonly MonsterAttack Attack = attack;
        public readonly int Generation = generation;
        public bool Started;
        public double ContactAt;
    }

    static readonly RuleTable<Player, List<CompanionIncomingHit>> CompanionIncomingHits = new();

    static List<CompanionIncomingHit> CompanionIncomingList(Player player) =>
        CompanionIncomingHits.GetOrCreateValue(player);

    internal static bool HasCompanionIncomingHit(Player player, uint enemyId, int generation) =>
        CompanionIncomingHits.TryGetValue(player, out var list)
        && list.Any(h => h.Enemy == enemyId && h.Generation == generation);

    internal static bool CompanionEnemyOrderLocked(Player player, uint id, SpawnedMonster enemy, (int X, int Y) here,
        double now, double bestOtherDistance, bool otherTaunted)
    {
        if (player.Companion is not { } comp || !CompanionAliveInArea(player, comp)
            || comp.Monster.Invulnerable || comp.Monster.Effects[Stat.BaseCannotBeDamaged] != 0
            || enemy.Attack is not { } attack)
            return false;
        var to = comp.Monster.Walk.Position(now);
        double distance = Dist(here, to);
        bool taunted = IsTauntedByPlayerCompanion(player, enemy, now);
        if (distance > enemy.AggroRange) return false;
        if (!taunted && (otherTaunted || distance >= bestOtherDistance)) return false;

        if (distance > attack.Range + LivingCollision.Size(comp.Monster))
        {
            double factor = distance > 0 ? Math.Max(1, attack.Range - 1) / distance : 0;
            var aim = ((int)Math.Round(to.X + (here.X - to.X) * factor), (int)Math.Round(to.Y + (here.Y - to.Y) * factor));
            if (enemy.LastTarget is { } old && (Dist(old, to) < 5 || Dist(old, aim) < 5) && now < enemy.Walk.ArrivesAt) return true;
            if (player.Connection.LockstepClient)
            {
                if (!LockstepTakesMove(here, aim)) aim = to;
                if (!LockstepTakesMove(here, aim)) return true;
                if (InAreaFrame(player))
                {
                    int gen = ++enemy.ActionGeneration;
                    player.Minions.OutputQueue.Add(new([], null, id, enemy, Action: gen, At: aim, WorldId: id));
                    return true;
                }
            }
            int generation = ++enemy.ActionGeneration;
            enemy.LastTarget = to;
            enemy.Walk.Order(aim.Item1, aim.Item2, now);
            player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionStarted(id, here.X, here.Y, aim.Item1, aim.Item2,
                0x2909, NextMonsterMoveSequence(player, enemy), player.MonsterOrderFlags), null, id, enemy, Action: generation, WorldId: id));
            return true;
        }
        int action = ++enemy.ActionGeneration;
        enemy.BusyUntil = double.PositiveInfinity;
        enemy.LastTarget = null;
        CompanionIncomingList(player).Add(new(id, enemy, comp, attack, action));
        player.Minions.OutputQueue.Add(new([], null, id, enemy, PlayerMinions.OutputKind.GolemIncoming, Action: action));
        return true;
    }

    internal static byte[]? CompanionIncomingPresentation(Player player, PlayerMinions.Output output)
    {
        if (!CompanionIncomingHits.TryGetValue(player, out var list)) return null;
        var hit = list.FirstOrDefault(h => h.Enemy == output.TargetId && h.Generation == output.Action);
        if (hit is null || hit.Started || hit.Monster.Dead || hit.Monster.ActionGeneration != hit.Generation
            || !CompanionAliveInArea(player, hit.Target))
            return null;
        double now = LockstepOrderStart(player, AreaTime(player));
        var from = hit.Monster.Walk.Position(now);
        var to = hit.Target.Monster.Walk.Position(now);
        hit.Started = true;
        hit.ContactAt = now + hit.Attack.Seconds * (hit.Attack.Release?.Fraction ?? 0.5);
        hit.Monster.BusyUntil = now + hit.Attack.Seconds;
        hit.Monster.Walk.Order(from.X, from.Y, now);
        return InstanceProtocol.ActionStarted(hit.Enemy, from.X, from.Y, to.X, to.Y, hit.Attack.SkillId,
            0xffff, player.MonsterOrderFlags, targetObjectId: hit.Target.EntityId);
    }

    internal static void CompanionIncomingContactsLocked(Player player, double now)
    {
        if (!CompanionIncomingHits.TryGetValue(player, out var list) || list.Count == 0) return;
        foreach (var hit in list.ToArray())
        {
            if (player.Area.Monsters.GetValueOrDefault(hit.Enemy) != hit.Monster || hit.Monster.Dead
                || hit.Monster.ActionGeneration != hit.Generation || !CompanionAliveInArea(player, hit.Target))
            {
                list.Remove(hit);
                if (hit.Monster.ActionGeneration == hit.Generation && double.IsPositiveInfinity(hit.Monster.BusyUntil))
                    hit.Monster.BusyUntil = now;
                continue;
            }
            if (!hit.Started || now < hit.ContactAt) continue;
            list.Remove(hit);
            if (MonsterActionFactor(hit.Monster, now) == 0) continue;
            if (hit.Attack.Projectile is { } projectile)
            {
                var session = ProjectilesFor(player);
                session.Schedule(session.Runtime.Now, () => CurrentGameWork && !player.Closed && !player.LeavingArea,
                    () => MonsterProjectileUse(session, hit.Enemy, hit.Monster, hit.Attack, projectile, hit.Generation, hit.Target.EntityId));
                continue;
            }
            if (Dist(hit.Monster.Walk.Position(now), hit.Target.Monster.Walk.Position(now))
                > hit.Attack.Range + LivingCollision.Size(hit.Target.Monster) + 2)
                continue;
            CompanionTakeEnemyHitLocked(player, hit.Target, hit.Enemy, hit.Monster, hit.Attack, now);
        }
    }

    /// <summary>Applies an enemy melee, area, or projectile hit to the player's active companion, delegating final
    /// mitigation/recoup/death to <see cref="ActiveCompanion.ApplyDamageLocked"/> when provided.</summary>
    internal static bool CompanionTakeEnemyHitLocked(Player player, ActiveCompanion comp, uint attacker,
        SpawnedMonster source, MonsterAttack attack, double now, bool area = false)
    {
        if (!CompanionAliveInArea(player, comp) || comp.Monster.Invulnerable
            || comp.Monster.Effects[Stat.BaseCannotBeDamaged] != 0)
            return false;
        var m = comp.Monster;
        var random = player.IncomingHitRandom;
        if (!attack.Spell && !attack.Secondary)
        {
            int evasion = (int)Math.Max(0, m.Effects.Evasion((Monsters.BaseEvasion(m.Level) ?? 0) + m.Effects[Stat.BaseEvasionRating])
                * (1 + m.Effects[Stat.GraceAuraEvasionRatingPlusPctFinal] / 100.0));
            int accuracy = source.Effects.Accuracy(Monsters.BaseAccuracy(source.Level) ?? 0);
            if (random.Next(100) >= DamageModel.AccuracyHitChance(evasion, accuracy)) return false;
        }
        var currentPlayer = PlayerStatValues(player);
        var linkDef = CompanionLinkDefenceAt(player, comp, currentPlayer, now);
        int block = attack.Spell ? m.Effects[Stat.BaseSpellBlockPct] : m.Effects[Stat.MonsterBaseBlockPct];
        int maxBlock = Math.Clamp(DamageModel.DefaultMaxBlockChance
            + m.Effects[attack.Spell ? Stat.BaseMaximumSpellBlockPct : Stat.MaximumBlockPct], 0, DamageModel.BlockChanceCap);
        if (!attack.Spell && linkDef.ProtectiveBlockChance is int protBlock && linkDef.ProtectiveMaxBlockChance is int protMax)
        {
            block = protBlock;
            maxBlock = protMax;
        }
        if (!attack.Spell && m.Effects[Stat.CannotBlockAttacks] != 0) block = 0;
        if (!attack.Secondary && random.Next(100) < Math.Clamp(block, 0, maxBlock))
        {
            if (linkDef.ProtectiveRecoverLifeOnBlock > 0 && m.Life < m.MaxLife && m.Effects[Stat.CannotRecoverLife] == 0)
            {
                m.Life = (uint)Math.Min(m.MaxLife, m.Life + (long)linkDef.ProtectiveRecoverLifeOnBlock);
                player.Area.Life.Set(comp.EntityId, AreaLife.LifePool, m.Life);
            }
            return false;
        }
        var rolled = MonsterModHit(source, MonsterTemporaryHit(source, attack.Damage, random).Roll(random),
            m.Life, m.MaxLife, random, out bool critical, now: now,
            cannotCrit: VaalOversoul.CannotCrit(source, attack), additionalCritical: BrittleCriticalChance(m, now))
            .Scale(SappedDamageFactor(source, now) * SigilEnemyDamageFactor(player, source, now));
        double total = 0;
        var mitigated = DamagePacket.Empty;
        foreach (var (type, range) in rolled.Components)
        {
            var resistance = MonsterHitResistance(m, type, default, now);
            resistance = resistance with { Penetration = Math.Max(0, MonsterPenetration(source.Effects, type)) };
            if (type != DamageType.Physical)
                resistance = resistance with
                {
                    Maximum = Math.Clamp(resistance.Maximum
                        + m.Effects[Stat.Of($"base_maximum_{type.ToString().ToLowerInvariant()}_damage_resistance_%")], 0, 90),
                };
            int value = (int)Math.Round(DamageModel.Mitigate(DamagePacket.Of(type, range.Min, range.Min), _ => resistance)[type].Min);
            if (type == DamageType.Physical)
            {
                double armour = Math.Max(0L, (long)m.Effects[Stat.BasePhysicalDamageReductionRating])
                    * Math.Max(0, 1 + m.Effects[Stat.PhysicalDamageReductionRatingPlusPct] / 100.0)
                    * Math.Max(0, 1 + m.Effects[Stat.DeterminationAuraArmourPlusPctFinal] / 100.0);
                double reduction = Math.Min(0.9, armour / Math.Max(1, armour + 5 * value)
                    + (m.Effects.PhysicalReduction - CrushReduction(m, now)) / 100.0);
                value = (int)Math.Round(value * (1 - reduction));
            }
            value = (int)Math.Round(value * linkDef.DamageTakenFactor
                * (area ? Math.Max(0, 1 + m.Effects[Stat.AreaDamageTakenFromHitsPlusPct] / 100.0) : 1));
            if (value > 0)
            {
                mitigated = mitigated.Add(type, new(value, value));
                total += value;
            }
        }
        int finalDamage = (int)Math.Clamp(Math.Round(total), 0, int.MaxValue);
        if (finalDamage > 0 && linkDef.SoulLinkEsInterceptPct > 0 && player.EnergyShield.Current > 0)
        {
            int intercept = (int)((long)finalDamage * linkDef.SoulLinkEsInterceptPct / 100);
            int remaining = player.EnergyShield.Damage(intercept, bypass: false, now);
            finalDamage = Math.Max(0, finalDamage - (intercept - remaining));
        }
        if (finalDamage > 0)
        {
            if (comp.ApplyDamageLocked is { } apply)
                apply(player, finalDamage, now);
            else
            {
                int loss = (int)Math.Min(finalDamage, (long)m.Life);
                m.Life -= (uint)loss;
                if (m.Life == 0) m.Dead = true;
                player.Area.Life.Set(comp.EntityId, AreaLife.LifePool, m.Life);
            }
        }
        var from = source.Walk.Position(now);
        var to = m.Walk.Position(now);
        player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(attacker, 0xffff, comp.EntityId,
            flags: MonsterHitFlags(blocked: false, critical), kind: 0x03, code: HitCode(mitigated),
            angle: HitAngle(to.X - from.X, to.Y - from.Y)), null, comp.EntityId, m, WorldId: attacker));
        return true;
    }

    // ---- Link Skills on Permanent Mercenary (Golden Glory + Full Link Buff Effects) ----

    /// <summary>Additive Light Radius modifiers applied by Golden Glory ("Increases and Reductions to Light Radius also
    /// apply to Effect of your Link Skill Buffs on your Mercenary"). Can be negative when light radius is reduced.</summary>
    public static int AdditiveLightRadiusPct(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        bool phased = player.IsPhasing || stats.GetValueOrDefault(Stat.PhaseThroughObjects) != 0;
        int mapLight = MapModsOf(player)?.Get(Stat.MapPlayerLightRadiusPlusPct) ?? 0;
        return ActorScale.Saturate((long)stats.GetValueOrDefault(Stat.LightRadiusPlusPct)
            + (phased ? stats.GetValueOrDefault(Stat.LightRadiusPlusPctWhilePhased) : 0)
            + mapLight);
    }

    /// <summary>Active link currently bound to the player's companion entity, or null.</summary>
    public static LinkSkills.Link? ActiveCompanionLink(Player player, uint companionId, double now) =>
        player.PlayerLife > 0 && player.Links.On(companionId) is { } link && now < link.Until
        && (link.AwaySince is not double away || now - away < link.BreakGraceSeconds) ? link : null;

    /// <summary>Total Link Skill Buff Effect % for a companion target, including Golden Glory's additive Light Radius
    /// modifiers when targeting the player's permanent mercenary.</summary>
    public static long LinkBuffEffectPercentForCompanion(Player player, ActiveCompanion companion,
        IReadOnlyDictionary<uint, int> casterStats)
    {
        long effect = 100L
            + casterStats.GetValueOrDefault(Stat.LinkSkillBuffEffectPlusPct)
            + companion.Monster.Effects[Stat.LinkBuffEffectOnSelfPlusPct];
        if (companion.IsPermanent
            && casterStats.GetValueOrDefault(Stat.LightRadiusAdditiveModifiersApplyToBuffEffectOfLinkSkillsIfLinkTargetIsYourPermanentMercenary) != 0)
            effect += AdditiveLightRadiusPct(player, casterStats);
        return Math.Max(0, effect);
    }

    internal static long LinkBuffEffectPercentForLink(Player player, LinkSkills.Link link, Func<uint, int> casterStat)
    {
        long effect = 100L + casterStat(Stat.LinkSkillBuffEffectPlusPct);
        if (player.Companion is { IsPermanent: true } comp && comp.EntityId == link.Target
            && casterStat(Stat.LightRadiusAdditiveModifiersApplyToBuffEffectOfLinkSkillsIfLinkTargetIsYourPermanentMercenary) != 0)
        {
            bool phased = player.IsPhasing || casterStat(Stat.PhaseThroughObjects) != 0;
            int mapLight = MapModsOf(player)?.Get(Stat.MapPlayerLightRadiusPlusPct) ?? 0;
            effect += (long)casterStat(Stat.LightRadiusPlusPct)
                + (phased ? casterStat(Stat.LightRadiusPlusPctWhilePhased) : 0)
                + mapLight;
        }
        return Math.Max(0, effect);
    }

    /// <summary>Offence bonuses granted to a linked companion by Destructive Link or Flame Link (scaled by Golden Glory).</summary>
    public readonly record struct CompanionLinkOffence(int FireMin = 0, int FireMax = 0, int Ignite = 0,
        int? MainHandCrit = null, int CritMultiplier = 0, int Accuracy = 0);

    public static CompanionLinkOffence CompanionLinkOffenceAt(Player player, ActiveCompanion companion,
        IReadOnlyDictionary<uint, int> casterStats, double now)
    {
        if (ActiveCompanionLink(player, companion.EntityId, now) is not { } link) return default;
        long effect = LinkBuffEffectPercentForCompanion(player, companion, casterStats);
        if (link.EffectId == "DestructiveLink")
            return new(MainHandCrit: player.Pools.AttackCriticalStrikeChance,
                CritMultiplier: ScaledLinkStat(link, Stat.CriticalLinkGrantsBaseCriticalStrikeMultiplierPlus, effect),
                Accuracy: ScaledLinkStat(link, Stat.CriticalLinkGrantsAccuracyRatingPlusPct, effect));
        if (link.EffectId != "FlameLink") return default;
        long fromLife = (long)player.PlayerMaxLife * link.SkillStats.GetValueOrDefault(Stat.FlameLinkAddedFireDamageFromLifePct) / 100;
        int Fire(uint stat) => ActorScale.Saturate((fromLife + link.SkillStats.GetValueOrDefault(stat)) * effect / 100);
        return new(Fire(Stat.FlameLinkMinimumFireDamage), Fire(Stat.FlameLinkMaximumFireDamage),
            ScaledLinkStat(link, Stat.FlameLinkGrantsChanceToIgnitePct, effect));
    }

    /// <summary>Defence bonuses granted to a linked companion by Soul Link or Protective Link (scaled by Golden Glory).</summary>
    public readonly record struct CompanionLinkDefence(double DamageTakenFactor = 1, int SoulLinkEsInterceptPct = 0,
        int? ProtectiveBlockChance = null, int? ProtectiveMaxBlockChance = null, int ProtectiveRecoverLifeOnBlock = 0);

    public static CompanionLinkDefence CompanionLinkDefenceAt(Player player, ActiveCompanion companion,
        IReadOnlyDictionary<uint, int> casterStats, double now)
    {
        if (ActiveCompanionLink(player, companion.EntityId, now) is not { } link) return new();
        long effect = LinkBuffEffectPercentForCompanion(player, companion, casterStats);
        if (link.EffectId == "SoulLink")
            return new(
                DamageTakenFactor: SoulLinkDamageTaken(link, effect),
                SoulLinkEsInterceptPct: Math.Clamp(ScaledLinkStat(link,
                    Stat.SoulLinkGrantsTakePctOfHitDamageFromSoulLinkSourceEnergyShieldBeforeYou, effect), 0, 100));
        if (link.EffectId == "ProtectiveLink")
        {
            int maxBlock = Math.Clamp(player.Pools.BlockChanceMax, 0, DamageModel.BlockChanceCap);
            int block = player.Pools.AttackBlockAtMaximum ? maxBlock : player.Pools.BlockChance;
            int recover = ScaledLinkStat(link, Stat.BulwarkLinkGrantsRecoverXLifeOnBlock, effect);
            return new(ProtectiveBlockChance: block, ProtectiveMaxBlockChance: maxBlock, ProtectiveRecoverLifeOnBlock: recover);
        }
        return new();
    }
}
