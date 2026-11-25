using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static bool GolemEnemyOrderLocked(Player player, uint id, SpawnedMonster enemy, double now, double ownerDistance)
    {
        if (!enemy.Hostile || enemy.Attack is not { } attack || InTown(player) || InGracePeriod(player)) return false;
        if (player.Golems.IncomingHits.Any(h => h.Enemy == id && h.Generation == enemy.ActionGeneration)) return true;
        if (enemy.BusyUntil > now || MonsterActionFactor(enemy, now) == 0) return false;
        var here = enemy.Walk.Position(now);
        var golem = GolemTargetLocked(player, id, enemy, here, now, ownerDistance);
        if (golem.G is null) return false;
        var to = golem.G.Monster.Walk.Position(now);
        if (golem.Distance > attack.Range + LivingCollision.Size(golem.G.Monster))
        {
            double factor = golem.Distance > 0 ? Math.Max(1, attack.Range - 1) / golem.Distance : 0;
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
            int generation = ++enemy.ActionGeneration; enemy.LastTarget = to;
            enemy.Walk.Order(aim.Item1, aim.Item2, now);
            player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionStarted(id, here.X, here.Y, aim.Item1, aim.Item2,
                0x2909, NextMonsterMoveSequence(player, enemy), player.MonsterOrderFlags), null, id, enemy, Action: generation, WorldId: id));
            return true;
        }
        int action = ++enemy.ActionGeneration;
        enemy.BusyUntil = double.PositiveInfinity; enemy.LastTarget = null;
        player.Golems.IncomingHits.Add(new(id, enemy, golem.G, attack, action));
        player.Minions.OutputQueue.Add(new([], null, id, enemy, PlayerMinions.OutputKind.GolemIncoming, Action: action));
        return true;
    }

    static (GolemState.Golem? G, double Distance) GolemTargetLocked(Player player, uint id, SpawnedMonster enemy,
        (int X, int Y) here, double now, double ownerDistance)
    {
        if (player.Golems.All.Count + player.Reservations.Fighters.Count + player.Summons.Fighters.Count == 0) return default;
        return player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values).Where(g => !g.Monster.Dead && g.Monster.Life > 0
                && (g.Definition.IsGolem || g.Definition.Kind == "Purity" || player.Summons.Definitions.GetValueOrDefault(g.Id)?.Targetable == true))
            .Select(g => (G: g, Distance: Dist(here, g.Monster.Walk.Position(now))))
            .Where(t => t.Distance <= enemy.AggroRange && (t.Distance < ownerDistance || t.G.Taunts.GetValueOrDefault(id) > now))
            .OrderByDescending(t => t.G.Taunts.GetValueOrDefault(id) > now).ThenBy(t => t.Distance).FirstOrDefault();
    }

    static byte[]? GolemIncomingPresentation(Player player, PlayerMinions.Output output)
    {
        var hit = player.Golems.IncomingHits.FirstOrDefault(h => h.Enemy == output.TargetId && h.Generation == output.Action);
        if (hit is null || hit.Started || hit.Monster.Dead || hit.Monster.ActionGeneration != hit.Generation || hit.Target.Monster.Dead) return null;
        double now = LockstepOrderStart(player, AreaTime(player));
        var from = hit.Monster.Walk.Position(now); var to = hit.Target.Monster.Walk.Position(now);
        hit.Started = true;
        hit.ContactAt = now + hit.Attack.Seconds * (IsEssenceExplosiveInsect(hit.Monster.Variety?.Id) ? 1 : hit.Attack.Release?.Fraction ?? .5);
        hit.Monster.BusyUntil = now + hit.Attack.Seconds;
        hit.Monster.Walk.Order(from.X, from.Y, now);
        return InstanceProtocol.ActionStarted(hit.Enemy, from.X, from.Y, to.X, to.Y, hit.Attack.SkillId,
            0xffff, player.MonsterOrderFlags, targetObjectId: hit.Target.Id);
    }

    static void GolemIncomingContactsLocked(Player player, double now)
    {
        foreach (var hit in player.Golems.IncomingHits.ToArray())
        {
            if (player.Area.Monsters.GetValueOrDefault(hit.Enemy) != hit.Monster || hit.Monster.Dead || hit.Monster.ActionGeneration != hit.Generation
                || !GolemCurrent(player, hit.Target) || hit.Target.Monster.Dead)
            {
                player.Golems.IncomingHits.Remove(hit);
                if (hit.Monster.ActionGeneration == hit.Generation && double.IsPositiveInfinity(hit.Monster.BusyUntil))
                    hit.Monster.BusyUntil = now;
                continue;
            }
            if (!hit.Started || now < hit.ContactAt) continue;
            player.Golems.IncomingHits.Remove(hit);
            if (MonsterActionFactor(hit.Monster, now) == 0) continue;
            if (IsEssenceExplosiveInsect(hit.Monster.Variety?.Id))
            {
                QueueEssenceInsectContactLocked(player, hit.Enemy, hit.Monster, hit.Attack, hit.Generation);
                continue;
            }
            if (hit.Attack.Projectile is { } projectile)
            {
                var session = ProjectilesFor(player);
                session.Schedule(session.Runtime.Now, () => CurrentGameWork && !player.Closed && !player.LeavingArea,
                    () => MonsterProjectileUse(session, hit.Enemy, hit.Monster, hit.Attack, projectile, hit.Generation, hit.Target.Id));
                continue;
            }
            if (Dist(hit.Monster.Walk.Position(now), hit.Target.Monster.Walk.Position(now))
                > hit.Attack.Range + LivingCollision.Size(hit.Target.Monster) + 2) continue;
            GolemTakeHitLocked(player, hit.Target, hit.Enemy, hit.Monster, hit.Attack, now);
        }
    }

    public static bool GolemTakeHitLocked(Player player, GolemState.Golem g, uint attacker, SpawnedMonster source, MonsterAttack attack, double now, bool area = false) =>
        GolemTakeHitLocked(player, g, attacker, source, attack, now, area, reflectedPhysical: false);

    static bool GolemTakeHitLocked(Player player, GolemState.Golem g, uint attacker, SpawnedMonster source,
        MonsterAttack attack, double now, bool area, bool reflectedPhysical)
    {
        if (!GolemCurrent(player, g) || g.Monster.Dead || g.Monster.Life == 0) return false;
        if (GolemIgnoresDots(g)) return false;
        using var scope = new PlayerStatScope(player, fresh: true);
        var currentPlayer = CharacterPlayerStats(player);
        ExpireMinionChargesLocked(player, g, now);
        RefreshGolemLocked(player, g, currentPlayer, now);
        if (g.Monster.Dead || g.Monster.Life == 0 || g.Monster.Effects[Stat.BaseCannotBeDamaged] != 0) return false;
        var m = g.Monster; var effects = m.Effects;
        var random = player.IncomingHitRandom;
        int Player(uint name) => GolemStat(g.Player, name);
        if (!attack.Spell && !attack.Secondary)
        {
            int evasion = (int)Math.Max(0, effects.Evasion((Monsters.BaseEvasion(m.Level) ?? 0) + effects[Stat.BaseEvasionRating])
                * (1 + effects[Stat.GraceAuraEvasionRatingPlusPctFinal] / 100.0));
            int accuracy = source.Effects.Accuracy(Monsters.BaseAccuracy(source.Level) ?? 0);
            if (random.Next(100) >= DamageModel.AccuracyHitChance(evasion, accuracy)) return false;
        }
        int block = attack.Spell ? Player(Stat.MinionAdditionalSpellBlockPct) + effects[Stat.BaseSpellBlockPct]
            : Player(Stat.MinionBlockPct) + effects[Stat.MonsterBaseBlockPct];
        var link = ActiveMinionLink(player, g, now);
        bool protective = link?.EffectId == "ProtectiveLink";
        int maximumBlock = Math.Clamp(DamageModel.DefaultMaxBlockChance
            + effects[attack.Spell ? Stat.BaseMaximumSpellBlockPct : Stat.MaximumBlockPct], 0, DamageModel.BlockChanceCap);
        if (protective && !attack.Spell)
        {
            maximumBlock = Math.Clamp(player.Pools.BlockChanceMax, 0, DamageModel.BlockChanceCap);
            block = player.Pools.AttackBlockAtMaximum ? maximumBlock : player.Pools.BlockChance;
        }
        if (!attack.Spell && effects[Stat.CannotBlockAttacks] != 0) block = 0;
        if (!attack.Secondary && random.Next(100) < Math.Clamp(block, 0, maximumBlock))
        {
            g.BlockedAt = now;
            int linkedRecovery = protective
                ? ScaledLinkStat(link!, Stat.BulwarkLinkGrantsRecoverXLifeOnBlock, LinkBuffPercent(g, currentPlayer)) : 0;
            if (m.Life < m.MaxLife && !LifeRecovery.NonLeechBlocked(name => effects[name]))
            {
                long recovery = m.MaxLife * (long)effects[Stat.RecoverPctOfMaximumLifeOnBlock] / 100
                    + effects[Stat.RecoverXLifeOnBlock] + effects[Stat.LifeGainedOnBlock] + linkedRecovery;
                m.Life = (uint)Math.Min(m.MaxLife, m.Life + Math.Max(0, recovery));
            }
            if (g.Shield < g.MaximumShield && effects[Stat.CannotRecoverEnergyShield] == 0 && effects[Stat.NoEnergyShieldRecovery] == 0)
            {
                int fromArmour = effects[Stat.EnergyShieldPctOfArmourRatingGainedOnBlock];
                double recovery = effects[Stat.EnergyShieldGainedOnBlock]
                    + (fromArmour > 0 ? GolemArmour(g) * fromArmour / 100 : 0);
                if (recovery >= 1)
                {
                    g.ShieldPool.Recover((uint)Math.Min(recovery, uint.MaxValue));
                    if (g.ShieldPool.Full) MinionShieldLeeches.Remove(g);
                    PublishEnergyShieldLocked(player, g);
                }
            }
            player.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Life));
            return false;
        }
        bool critical = false;
        var rolled = reflectedPhysical ? attack.Damage
            : MonsterModHit(source, MonsterTemporaryHit(source, attack.Damage, random).Roll(random), m.Life, m.MaxLife, random, out critical,
            now: now, cannotCrit: VaalOversoul.CannotCrit(source, attack), additionalCritical: BrittleCriticalChance(m, now))
            .Scale(SappedDamageFactor(source, now) * TauntedHitAgainstMinion(player, attacker, source, g, now)
                * SigilEnemyDamageFactor(player, source, now));
        bool elementalImmune = GolemElementalImmune(g);
        bool chaosNoBypass = effects[Stat.ChaosDamageDoesNotBypassEnergyShield] != 0;
        bool spellHit = attack.Spell && !attack.Secondary;
        if (reflectedPhysical && (effects[Stat.CannotTakeReflectedPhysicalDamage] != 0
            || effects[Stat.VirtualCannotTakeReflectedPhysicalDamage] != 0))
            return false;
        int reflectedTaken = reflectedPhysical ? ActorScale.Saturate((long)effects[Stat.PhysicalReflectDamageTakenPlusPct]
            + effects[Stat.ReflectDamageTakenPlusPct] + Player(Stat.MinionsReflectedDamageTakenPlusPct)
            + Player(Stat.ReflectDamageTakenAndMinionReflectDamageTakenPlusPct)
            + Player(Stat.PhysicalReflectDamageTakenAndMinionPhysicalReflectDamageTakenPlusPct)) : 0;
        double taken = effects.DamageTaken(spellHit, m.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, now) + reflectedTaken
            + m.Ailments.ChillDamageTakenPct(now) + HitDebuffTaken(m, spellHit, now)) * MinionFortificationHitFactor(player, g, now)
            * CompanionshipDamageTaken(player, g, now) * HallowedMonarchDamageTaken(link, currentPlayer);
        int prevented = 0;
        if (spellHit)
        {
            long chance = effects[Stat.BaseSpellSuppressionChancePct];
            if (Player(Stat.MinionsGainYourSpellSuppressionChance) != 0)
                chance += SpellDefences(player, currentPlayer, now).Suppression;
            if (chance > 0 && RandomRolls.Chance((int)Math.Min(chance, 100), 100, random))
                prevented = Math.Clamp(DamageModel.SpellSuppressionEffect + effects[Stat.BaseSpellDamagePctSuppressed], 0, 100);
        }
        var rejuvenation = RejuvenationTotemAt(player, m.Walk.Position(now));
        long damage = 0, nonChaos = 0;
        int chaos = 0;
        var mitigated = DamagePacket.Empty;
        int interceptedPct = 0;
        double soulDamageTaken = 1;
        long interceptedCarry = 0;
        if (link is { EffectId: "SoulLink" })
        {
            long effect = LinkBuffPercent(g, currentPlayer);
            soulDamageTaken = SoulLinkDamageTaken(link, effect);
            interceptedPct = Math.Clamp(ScaledLinkStat(link, Stat.SoulLinkGrantsTakePctOfHitDamageFromSoulLinkSourceEnergyShieldBeforeYou,
                effect), 0, 100);
        }
        foreach (var (type, range) in rolled.Components)
        {
            if (elementalImmune && type is DamageType.Fire or DamageType.Cold or DamageType.Lightning) continue;
            var resistance = MonsterHitResistance(m, type, default, now);
            if (!reflectedPhysical) resistance = resistance with { Penetration = Math.Max(0, MonsterPenetration(source.Effects, type)) };
            if (type != DamageType.Physical)
                resistance = resistance with { Maximum = Math.Clamp(resistance.Maximum
                    + effects[Stat.Of($"base_maximum_{type.ToString().ToLowerInvariant()}_damage_resistance_%")], 0, 90) };
            int value = (int)Math.Round(DamageModel.Mitigate(DamagePacket.Of(type, range.Min, range.Min), _ => resistance)[type].Min);
            if (type == DamageType.Physical)
            {
                double armour = GolemArmour(g);
                double reduction = Math.Min(.9, armour / Math.Max(1, armour + 5 * value)
                    + (effects.PhysicalReduction - CrushReduction(m, now)) / 100.0);
                value = (int)Math.Round(value * (1 - reduction));
            }
            else if (DamageTypes.IsElemental(type))
                value = (int)Math.Round(value * GolemElementalReductionFactor(effects));
            value = (int)Math.Round(value * taken
                * GolemMeatShieldDamageFactor(g)
                * soulDamageTaken
                * Math.Max(0, 1 + GolemStat(g.Own, Stat.VaalUpgradeMinionDamageTakenPlusPctFinal) / 100d)
                * (area ? Math.Max(0, 1 + effects[Stat.AreaDamageTakenFromHitsPlusPct] / 100.0) : 1));
            if (prevented != 0) value = DamageModel.Suppress(value, prevented);
            if (value > 0)
            {
                mitigated = mitigated.Add(type, new(value, value));
                if (rejuvenation is null) TakePoolDamage(value, type == DamageType.Chaos);
                else if (type == DamageType.Chaos) chaos = value;
                else nonChaos += value;
            }
        }
        if (rejuvenation is { } protection && nonChaos + chaos > 0)
        {
            int redirectable = (int)Math.Min(int.MaxValue, nonChaos);
            var kept = RedirectTotemHitLocked(protection.Owner, protection.Totem, redirectable, chaos,
                RejuvenationRedirectPct(protection.Owner, protection.Totem.Aura!), AreaTime(protection.Owner),
                protection.Owner == player ? currentPlayer : PlayerStatValues(protection.Owner));
            // Totem redirection reduces the mitigated hit before Soul Link and the minion's Energy Shield use it.
            for (long overflow = nonChaos - redirectable; overflow > 0;)
            {
                int chunk = (int)Math.Min(int.MaxValue, overflow);
                TakePoolDamage(chunk, false);
                overflow -= chunk;
            }
            TakePoolDamage(kept.NonChaos, false);
            TakePoolDamage(kept.Chaos, true);
        }

        void TakePoolDamage(int value, bool isChaos)
        {
            if (value <= 0) return;
            if (interceptedPct > 0 && player.EnergyShield.Current > 0)
            {
                long share = interceptedCarry + (long)value * interceptedPct;
                int intercept = (int)(share / 100);
                interceptedCarry = share % 100;
                value -= intercept - player.EnergyShield.Damage(intercept, bypass: false, now);
            }
            if (source.Hostile && value > 0 && HasFrostShieldZones(player))
                value = AbsorbFrostShield(player, m.Walk.Position(now), source.Walk.Position(now), value, now);
            damage += g.ShieldPool.Damage(value, isChaos && !chaosNoBypass, now);
        }

        PublishEnergyShieldLocked(player, g);
        m.Life = (uint)Math.Max(0, (long)m.Life - damage);
        if (reflectedPhysical)
        {
            player.Minions.OutputQueue.Add(new([], "minion takes reflected physical damage", g.Id, m, PlayerMinions.OutputKind.Life));
            if (m.Life == 0) GolemDieLocked(player, g, now);
            else MinionInstabilityLocked(player, g, now);
            return true;
        }
        StealMapChargesLocked(player, attacker, source, g, now, random);
        GainMonsterHitChargesLocked(player.Area, attacker, source, now, random);
        if (!elementalImmune)
            m.Ailments.Inflict(rolled, critical, m.MaxLife, now, n => n == Stat.NeverFreeze && attack.Ailments.NeverFreeze ? 1 : source.Effects[n], n => effects[n], random);
        if (EqualisingPercent(player, source) is > 0 and var equalising)
        {
            m.Life -= HitResourceLoss(m.Life, m.MaxLife, equalising);
            uint removedShield = HitResourceLoss(g.Shield, g.MaximumShield, equalising);
            if (removedShield > 0 && g.ShieldPool.Spend(removedShield)) PublishEnergyShieldLocked(player, g);
        }
        if (m.Life > 0)
        {
            if (mitigated.MinTotal > 0) SpectreDamagedLocked(player, g, now, random);
            GolemHostileAilmentsLocked(player, g, attacker, source, rolled, critical, !attack.Spell && !attack.Secondary, now,
                (100 - prevented) / 100d);
            if (!attack.Secondary && mitigated.MinTotal > 0)
            {
                int Source(uint name) => source.Effects[name];
                ApplyContactDebuffsLocked(m, Source, spellHit, critical, now, random);
                AlternateAilmentsOnHitLocked(m, mitigated, critical, now, Source, random);
            }
        }
        var from = source.Walk.Position(now);
        var to = m.Walk.Position(now);
        // Immediate kind 3 reaches the attacker's native result consumer; 0xffff never supplies a consuming action.
        player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(attacker, 0xffff, g.Id,
            flags: MonsterHitFlags(blocked: false, critical), kind: 0x03, code: HitCode(mitigated),
            angle: HitAngle(to.X - from.X, to.Y - from.Y)), null, g.Id, m, WorldId: attacker));
        player.Minions.OutputQueue.Add(new([], "golem takes damage", g.Id, m, PlayerMinions.OutputKind.Life));
        if (m.Life == 0) GolemDieLocked(player, g, now);
        else if (MinionInstabilityLocked(player, g, now)) return true;
        else if (g.Definition.Kind == "Ice" && g.Definition.Special && !g.DeathBurstDone && m.Life * 2L <= m.MaxLife)
        {
            g.DeathBurstDone = true;
            if (g.Abilities.FirstOrDefault(a => a.Effect == 4839) is { } death)
                GolemReserveLocked(player, g, death, 0, null, m.Walk.Position(now), now);
        }
        else if (!attack.Secondary && source.Effects[Stat.CannotStun] == 0
            && !(critical && mitigated[DamageType.Cold].Min > 0)
            && HitReaction.TryFlinch(m, MinionIncomingStun(source, attack, damage, mitigated), now, random))
        {
            g.Pending = null; g.Pulses.RemoveAll(p => p.Follows);
            player.Minions.OutputQueue.Add(new([], null, g.Id, m, PlayerMinions.OutputKind.Flinch, Until: m.FlinchUntil, Action: m.ActionGeneration));
        }
        return true;
    }

    static StunHit MinionIncomingStun(SpawnedMonster source, MonsterAttack attack, long damage, DamagePacket taken)
    {
        bool melee = !attack.Spell && !attack.Ranged;
        return new(HitReaction.StunningDamage(damage, taken[DamageType.Physical].Min / Math.Max(1, taken.MinTotal), melee),
            source.Effects[Stat.BaseStunThresholdReductionPlusPct], source.Effects[Stat.BaseStunDurationPlusPct], Melee: melee);
    }

    static double GolemArmour(GolemState.Golem minion)
    {
        var effects = minion.Monster.Effects;
        if (effects[Stat.MinionsHaveNoArmourOrEnergyShield] != 0) return 0;
        return Math.Max(0L, (long)GolemStat(minion.Player, Stat.MinionPhysicalDamageReductionRating)
            + (minion.Definition.IsGolem ? GolemStat(minion.Player, Stat.GolemPhysicalDamageReductionRating) : 0)
            + effects[Stat.BasePhysicalDamageReductionRating])
            * Math.Max(0, 1 + effects[Stat.PhysicalDamageReductionRatingPlusPct] / 100.0)
            * Math.Max(0, 1 + effects[Stat.BasaltFlaskArmourPlusPctFinal] / 100.0)
            * Math.Max(0, 1 + effects[Stat.DeterminationAuraArmourPlusPctFinal] / 100.0)
            * Math.Max(0, 1 + effects[Stat.ArmourPlusPctFinalFromSeismicCryBuff] / 100.0);
    }

    internal static void GolemDieLocked(Player player, GolemState.Golem g, double now, bool emitLife = true, bool canReform = true)
    {
        if (g.Monster.Dead && double.IsFinite(g.ReviveAt)) return;
        g.Monster.Dead = true; g.Monster.Life = 0; g.Monster.ActionGeneration++; g.Pending = null;
        MarkPlayerStats(player, PlayerStatStep.Golems);   // the active-minion counts
        OwnedMinionDeathEffectsLocked(player, g, now);
        ClearSharedMinionAuras(player, g.Id);
        ClearMinionFlasksLocked(player, g, now);
        ReconcileLinkedMinionModifiersLocked(player, g, now, dead: true);
        MinionLifeLeeches.Remove(g);
        MinionShieldLeeches.Remove(g);
        ClearSpectreArcaneSurgeLocked(player, g);
        if (g.DevouredSouls is not null)
        {
            g.DevouredSouls = null;
            player.Area.Buffs.Clear(g.Id, SoulEaterBuffRow, g.Id);
        }
        MinionFortification.Remove(g);
        player.Area.Buffs.Clear(g.Id, FortificationBuffRow, g.Id);
        if (g.Definition.Kind == "Phantasm")
        {
            player.Area.Buffs.Clear(player.Id, PhantasmalMightBuff, g.Id);
            player.Minions.StatRevision++;
        }
        ReconcileGuardianBlessingLocked(player);
        if (g.Definition.Kind == "AnimatedGuardian") RecordAnimatedGuardianDeath(player);
        g.ShieldPool.Empty();
        ExpireMinionChargesLocked(player, g, double.PositiveInfinity);
        PublishEnergyShieldLocked(player, g);
        foreach (var hazard in g.Hazards) if (hazard.Object is uint obj) player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(obj), null, WorldId: obj, RemovesObject: true));
        EndDot(player, g.Id, ref g.Monster.Ignites.Buff, g.Monster.Ignites.BuffSource);
        EndDot(player, g.Id, ref g.Monster.Bleeds.Buff, g.Monster.Bleeds.BuffSource);
        EndDot(player, g.Id, ref g.Monster.Poisons.Buff, g.Monster.Poisons.BuffSource);
        if (g.SpectreBuff != 0)
        {
            player.Area.Buffs.Clear(g.Id, SpectreBuffRowValue(), g.Id);
            g.SpectreBuff = 0;
        }
        g.Monster.Ignites.Clear();
        g.Monster.Bleeds.Clear();
        g.Monster.Poisons.Clear();
        g.Hazards.Clear(); g.Pulses.Clear();
        // Local gameplay rule: a successfully activated golem returns after two game seconds while its source remains valid.
        g.ReviveAt = g.Definition.IsGolem ? now + 2 : double.PositiveInfinity;
        // Marionette reformation also follows a lethal Infernal Legion tick or a Minion Instability explosion.
        if (canReform && g.Definition.Kind == "Spectre"
            && g.Monster.Mods.Effects[Stat.ReviveSpectreIfKilledByEnemiesMs] is int delay and > 0)
        {
            g.ReviveAt = now + delay / 1000d;
            player.Summons.RefreshAt = 0;
            player.Minions.StatRevision++;
        }
        player.Golems.RefreshAt = 0;
        if (emitLife)
            player.Minions.OutputQueue.Add(new([], "golem died", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
    }

    static int GolemSafeguardLocked(Player player, int damage, double now)
    {
        if (damage <= 0) return damage;
        var candidates = player.Golems.All.Values.Where(g => !g.Monster.Dead && g.Definition.Kind == "Stone" && g.Definition.Special)
            .Select(g => (G: g, Pct: GolemStat(g.OwnerBuff, Stat.MeleeDamageRemovedFromStoneGolemBeforeLifeOrEsPct))).ToArray();
        if (candidates.Length == 0) return damage;
        int total = Math.Min(damage, (int)((long)damage * Math.Clamp(candidates.Max(g => g.Pct), 0, 100) / 100));
        int left = total;
        foreach (var (g, _) in candidates)
        {
            int share = Math.Min(left, (int)g.Monster.Life); g.Monster.Life -= (uint)share; left -= share;
            player.Minions.OutputQueue.Add(new([], "stone golem safeguards owner", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
            if (g.Monster.Life == 0) GolemDieLocked(player, g, now);
            if (left == 0) break;
        }
        return damage - (total - left);
    }
}
