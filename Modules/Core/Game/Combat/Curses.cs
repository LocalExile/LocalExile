using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static readonly HashSet<uint> CurseRecipientStats = [
        Stat.BaseResistAllElementsPct, Stat.BaseFireDamageResistancePct, Stat.BaseColdDamageResistancePct,
        Stat.BaseLightningDamageResistancePct, Stat.BaseChaosDamageResistancePct, Stat.AccuracyRatingPlusPct,
        Stat.EnfeebleDamagePlusPctFinal, Stat.EnfeebleDamagePlusPctVsRareOrUniqueFinal, Stat.PhysicalDamageTakenPlusPct,
        Stat.DamageTakenPlusPctOnLowLife, Stat.DebilitateSelfForXMillisecondsOnHit,
        Stat.TemporalChainsActionSpeedPlusPctFinal, Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal,
        Stat.BuffTimePassedPlusPctOtherThanTemporalChains, Stat.ChanceToBeIgnitedPct, Stat.ChanceToBeFrozenPct, Stat.ChanceToBeShockedPct,
        Stat.ReceiveBleedingChancePctWhenHitByAttack, Stat.EnemyAggravateBleedingOnAttackHitChancePctAgainstSelf,
        Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf, Stat.EnemyAdditionalCriticalStrikeMultiplierAgainstSelf,
        Stat.LifeGrantedWhenKilled, Stat.ManaGrantedWhenKilled, Stat.ChanceToGrantPowerChargeOnDeathPct,
        Stat.ChanceToGrantFrenzyChargeOnDeathPct, Stat.ChanceToGrantEnduranceChargeOnDeathPct,
        Stat.GrantAttackerPowerChargeWhenHitPctChance, Stat.GrantAttackerFrenzyChargeWhenHitPctChance, Stat.GrantAttackerEnduranceChargeWhenHitPctChance,
        Stat.ProjectilesHittingSelfSplitIntoX, Stat.ProjectileDamageTakenPlusPct, Stat.BaseAdditionalPhysicalDamageReductionPct,
        Stat.MinimumAddedPhysicalDamageTaken, Stat.MaximumAddedPhysicalDamageTaken, Stat.LifeGrantedWhenHitByAttacks, Stat.ManaGrantedWhenHitByAttacks,
        Stat.EnemyGrantXRageOnStun, Stat.EnemyChanceToDoubleStunDurationPctVsSelf,
        Stat.LifeLeechOnAnyDamageWhenHitByAttackPermyriad, Stat.ManaLeechOnAnyDamageWhenHitByAttackPermyriad,
        Stat.AlchemistsMarkGrantAttackerXFlaskChargesWhenHitOncePer3s,
        Stat.AlchemistsMarkIgniterCreatesBurningGroundPctIgniteDamage, Stat.AlchemistsMarkPoisonerCreatesCausticGroundPctPoisonDamage
    ];

    static readonly RuleTable<SkillCatalog, IReadOnlyDictionary<uint, int>> TemporalChainsOnHitStats = new();

    static bool ExternalTemporalChains(Player player, string effect) => effect == "TemporalChains"
        && GameEvents.GrantedSkillExecutable.For(player).Any(accept => accept(player, effect));

    static void CurseCharges(Player player, SpawnedMonster target, bool death)
    {
        foreach (var (kind, name) in new[] { (ChargeType.Power, "power"), (ChargeType.Frenzy, "frenzy"), (ChargeType.Endurance, "endurance") })
            if (RandomRolls.Chance(target.Curses[Stat.Of(death ? $"chance_to_grant_{name}_charge_on_death_%" : $"grant_attacker_{name}_charge_when_hit_%_chance")], 100, AreaRng(player)))
                ChargesOf(player).Enqueue(new ChargeEvent.Grant(kind, 1, "curse"));
    }

    static int CurseLimit(Player player, IReadOnlyDictionary<uint, int> playerStats)
    {
        int Get(uint n) => GolemStat(playerStats, n);
        // Anathema: the maximum Power Charges (Character.ot's 3 with the mods), not the raw +N stat.
        return Get(Stat.HasCurseLimitEqualToMaximumPowerCharges) != 0 ? ChargeMaxima.Of(ChargeType.Power, Get)
            : Math.Max(0, 1 + Get(Stat.NumberOfAdditionalCursesAllowed));
    }

    public static int CurseRadius(Player player, ushort skill, bool aura, SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var gem = captured ?? SupportedSkill(player, skill);
        var area = SkillAreaStats(player, gem.Stats, gem.Effect, sources ?? CharacterPlayerStats(player));
        int Get(uint n) => GolemStat(area, n);
        // Hexes retain their own base radius when Blasphemy converts them to auras.
        return SkillArea.Radius(22,
            n => Get(n) + (n == SkillArea.BaseIncreased ? Get(Stat.CurseAreaOfEffectPlusPct) : 0), spell: true);
    }

    public static bool ApplyCurseLocked(Player player, SpawnedMonster monster, ushort skill, double now, bool aura,
        SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
        => ApplyCurseLocked(player, monster, skill, now, aura, captured, sources, 0, 1);

    static bool ApplyCurseLocked(Player player, SpawnedMonster monster, ushort skill, double now, bool aura,
        SupportedGem? captured, IReadOnlyDictionary<uint, int>? sources, int addedEffect, double effectMultiplier)
    {
        var gem = captured ?? SupportedSkill(player, skill);
        return gem.RequirementsMet && ApplyCurseDataLocked(player, monster, skill, SkillsFor(player).EffectId(gem.Effect) ?? "",
            gem.Stats, now, aura, sources, addedEffect, effectMultiplier);
    }

    static bool ApplyCurseDataLocked(Player player, SpawnedMonster monster, ushort skill, string effect,
        IReadOnlyDictionary<uint, int> curseStats, double now, bool aura, IReadOnlyDictionary<uint, int>? sources,
        int addedEffect = 0, double effectMultiplier = 1)
    {
        if (!CurseCatalog.All.TryGetValue(effect, out var definition) || ExternalTemporalChains(player, effect)
            || monster.Dead || monster.Life == 0 || !monster.Hostile) return false;
        var playerStats = sources ?? CharacterPlayerStats(player);
        int Own(uint n) => GolemStat(curseStats, n);
        int Get(uint n) => GolemStat(playerStats, n) + Own(n);
        bool hexMaster = Get(Stat.KeystoneHexMaster) != 0;
        if (monster.Mods.Effects[Stat.ImmuneToCurses] != 0 || !definition.Mark && monster.Mods.Effects[Stat.Hexproof] != 0
            && Get(Stat.IgnoreHexproof) == 0 && Get(Stat.Of("hexes_ignore_hexproof")) == 0
            && !RandomRolls.Chance(Get(Stat.ChanceToIgnoreHexproofPct), 100, AreaRng(player))) return false;
        int increased = Get(Stat.CurseEffectPlusPct) + Get(Stat.Of(definition.Mark ? "mark_effect_+%" : "hex_effect_+%"))
            + (definition.Mark ? Get(Stat.MarkSkillsCurseEffectPlusPct) : 0)
            + Get(Stat.Of(ReservationNames.GetValueOrDefault(effect, effect.ToLowerInvariant()) + "_curse_effect_+%"))
            + monster.Mods.Effects[Stat.CurseEffectOnSelfPlusPct] + addedEffect;
        double more = Math.Max(0, 1 + Own(Stat.SupportBlasphemyCurseEffectPlusPctFinal) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportHextouchCurseEffectPlusPctFinal) / 100.0)
            * Math.Max(0, 1 + monster.Mods.Effects[Stat.CurseEffectOnSelfPlusPctFinal] / 100.0)
            * Math.Max(0, 1 + monster.Mods.Effects[Stat.MapMonstersCurseEffectOnSelfPlusPctFinal] / 100.0)
            * effectMultiplier * (hexMaster ? .8 : 1);
        Dictionary<uint, int> RecipientStats(int extra) => curseStats.Where(p => CurseRecipientStats.Contains(p.Key))
            .ToDictionary(p => p.Key, p => (int)Math.Truncate(p.Value * Math.Max(0, 1 + ((long)increased + extra) / 100d) * more));
        var stats = RecipientStats(0);
        double seconds = definition.Mark ? double.PositiveInfinity : SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
            n => GolemStat(playerStats, n) + (n == SkillDurationModifiers.IncreasedDuration ? Get(Stat.CurseDurationPlusPct) + Get(Stat.CurseSkillEffectDurationPlusPct) : 0),
            n => Own(n) + (n == SkillDurationModifiers.IncreasedDuration ? Own(Stat.BaseCurseDurationPlusPct) : 0));
        var recipientEffects = monster.Effects;
        bool temporalChains = effect == "TemporalChains";
        double rate = RecipientEffectTime.Rate(5, n => recipientEffects[n], temporalChains);
        seconds *= Math.Max(0, 1 + Own(Stat.HextouchSupportCurseDurationPlusPctFinal) / 100d);
        int damageTaken = GolemStat(playerStats, Stat.EnemiesDamageTakenPlusPctWhileCursed);
        bool preventsCritical = Get(definition.Mark ? Stat.MarkedEnemiesCannotDealCriticalStrikes : Stat.HexedEnemiesCannotDealCriticalStrikes) != 0;
        if (aura && monster.Curses.All.GetValueOrDefault((player.Id, effect)) is { Aura: true } existing
            && existing.Skill == skill && existing.CursedDamageTaken == damageTaken
            && existing.PreventsCriticalStrikes == preventsCritical
            && existing.Stats.Count == stats.Count && existing.Stats.All(p => stats.GetValueOrDefault(p.Key) == p.Value)) return true;
        bool newlyCursed = !monster.HasCurses || !monster.Curses.HasActive(now);
        if (definition.Mark)
            foreach (var m in player.Area.Monsters.Values.Where(m => !ReferenceEquals(m, monster) && m.HasCurses))
                foreach (var mark in m.Curses.All.Values.Where(c => c.Caster == player.Id && c.Mark).ToArray())
                { m.Curses.Remove(mark.Caster, mark.Effect); m.PresentationChanged?.Invoke(); }
        double until = aura || hexMaster && !definition.Mark ? double.PositiveInfinity : now + seconds / rate;
        int amplify = double.IsFinite(until) ? Get(Stat.WhileCurseIs50PctExpiredCurseEffectPlusPct) : 0;
        var curse = new MonsterCurses.Curse(player.Id, effect, skill, definition.Buff, player.NextBuff++, now, until, aura, definition.Mark, stats)
        {
            RemainsOnDeath = !aura && Get(Stat.CursesYouInflictRemainAfterDeath) != 0,
            CursedDamageTaken = damageTaken,
            PreventsCriticalStrikes = preventsCritical,
            TransferChance = definition.Mark ? Get(Stat.YourMarksTransferToNearbyEnemiesOnDeathPctChance) : 0,
            Rate = rate,
            UsesBaseExpiryRate = temporalChains,
            AmplifiesAt = amplify == 0 ? double.PositiveInfinity : now + (until - now) / 2,
            AmplifiedStats = amplify == 0 ? null : RecipientStats(amplify),
        };
        monster.Curses.Put(curse, CurseLimit(player, playerStats));
        if (!ReferenceEquals(monster.Curses.All.GetValueOrDefault((curse.Caster, curse.Effect)), curse)) return false;
        if (!aura && Get(Stat.RefreshDurationOfShockChillIgniteOnEnemyWhenCursingEnemy) != 0)
        {
            monster.Ailments.RefreshChillAndShock(now);
            monster.Ignites.Refresh(now);
        }
        if (newlyCursed)
        {
            if (!aura && Get(Stat.NonCursedEnemiesYouCurseAreBlindedFor4Seconds) != 0)
                InflictMonsterBlind(player, monster.ObjectId, monster, now, 100, Get(Stat.BlindEffectPlusPct), BlindSeconds(Get), null);
            if (!aura) RecoverOnFreshCurseLocked(player, Get);
        }
        if (!aura) monster.PresentationChanged?.Invoke();
        return true;
    }

    static void RecoverOnFreshCurseLocked(Player player, Func<uint, int> get)
    {
        int lifePct = LifeRecovery.NonLeechBlocked(get) ? 0 : get(Stat.RecoverPctMaximumLifeWhenCursingNonCursedEnemy);
        int manaPct = get(Stat.RecoverPctMaximumManaWhenCursingNonCursedEnemy);
        if (player.PlayerLife == 0 || lifePct <= 0 && manaPct <= 0) return;
        var session = LeechOf(player);
        uint life = player.PlayerLife, mana = player.PlayerMana;
        if (lifePct > 0)
            player.PlayerLife = PetrifiedBlood.RecoverLife(player, life,
                (long)player.PlayerMaxLife * lifePct / 100, ref session.LifeInstantCarry, minimum: 1);
        if (manaPct > 0)
            player.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(player),
                (long)player.Pools.Mana * manaPct / 100, ref session.ManaInstantCarry);
        session.InstantLifeChanged |= life != player.PlayerLife;
        session.InstantManaChanged |= mana != player.PlayerMana;
    }

    static void TransferMarksOnDeathLocked(Player player, SpawnedMonster dead, double now)
    {
        if (!dead.Hostile || !dead.HasCurses || dead.Life != 0) return;
        const int radius = 30;
        var at = dead.Walk.Position(now);
        foreach (var mark in dead.Curses.All.Values.Where(c => c.Mark && c.TransferChance > 0 && c.Until > now).ToArray())
        {
            var owner = player.Id == mark.Caster ? player
                : player.Instance.PlayersExcept(player.Id).OfType<Player>().FirstOrDefault(p => p.Id == mark.Caster);
            if (owner is not { Closed: false, LeavingArea: false, PlayerLife: > 0 }
                || !ReferenceEquals(player.Area, owner.Area)
                || !RandomRolls.Chance(mark.TransferChance, 100, AreaRng(owner))) continue;
            int limit = CurseLimit(owner, CharacterPlayerStats(owner));
            if (limit < 1) continue;
            foreach (var target in owner.Area.MonstersNear(at, radius).Select(entry => entry.Value)
                .Where(m => m is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false }
                    && m.Mods.Effects[Stat.ImmuneToCurses] == 0)
                .Select(m => (Monster: m, Distance: Dist(at, m.Walk.Position(now))))
                .Where(candidate => candidate.Distance <= radius).OrderBy(candidate => candidate.Distance))
            {
                var transferred = mark with { Applied = now, Buff = owner.NextBuff++ };
                target.Monster.Curses.Put(transferred, limit);
                if (!ReferenceEquals(target.Monster.Curses.All.GetValueOrDefault((mark.Caster, mark.Effect)), transferred)) continue;
                dead.Curses.Remove(mark.Caster, mark.Effect);
                dead.PresentationChanged?.Invoke();
                target.Monster.PresentationChanged?.Invoke();
                break;
            }
        }
    }

    static void CursesOnHitLocked(Player player, SpawnedMonster target, ushort sourceSkill, bool spell, double now,
        IReadOnlyDictionary<uint, int>? sourceStats, IReadOnlyDictionary<uint, int> hitStats, bool playerHit, Random random)
    {
        if (target.Dead || target.Life == 0 || player.Reservations.Secondary) return;
        if (playerHit && RandomRolls.Chance(hitStats.GetValueOrDefault(Stat.CurseOnHitPctTemporalChains), 100, random))
        {
            const string effect = "TemporalChains";
            var curse = TemporalChainsOnHitStats.GetValue(SkillsFor(player), static catalog =>
                catalog.EffectStats(catalog.EffectRow(effect), 1).ToDictionary(p => p.Item1, p => p.Item2));
            if (curse.Count != 0) ApplyCurseDataLocked(player, target, 0, effect, curse, now, false, hitStats);
        }
        if (playerHit) ItemCursesOnHitLocked(player, target, hitStats, now);
        bool mayHextouch = sourceSkill != 0
            && (sourceStats is null || GolemStat(sourceStats, Stat.ApplyLinkedCursesOnHitPct) > 0);
        if (!mayHextouch && (spell || target.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))) return;
        var source = mayHextouch ? SupportedSkill(player, sourceSkill) : null;
        bool hextouch = source is not null && GolemStat(source.Stats, Stat.ApplyLinkedCursesOnHitPct) is var chance
            && RandomRolls.Chance(chance, 100, AreaRng(player));
        if (!hextouch && (spell || target.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))) return;
        var playerStats = CharacterPlayerStats(player);
        foreach (var skill in GemSkillEffects(player))
        {
            string effect = SkillsFor(player).EffectId(skill.Effect) ?? "";
            if (!CurseCatalog.All.TryGetValue(effect, out var curse)) continue;
            var gem = SupportedSkill(player, skill.Id);
            if (!gem.RequirementsMet) continue;
            if (!curse.Mark && hextouch && source!.Host != 0 && gem.Host == source.Host && gem.Group == source.Group
                && GolemStat(gem.Stats, Stat.CurseTriggeredByHextouch) != 0)
                ApplyCurseLocked(player, target, skill.Id, now, false, gem, playerStats);
            if (curse.Mark && !spell && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                && GolemStat(gem.Stats, Stat.TriggerOnAttackHitAgainstRareOrUnique) != 0
                && now >= player.SkillReadyAt.GetValueOrDefault(gem.Effect))
            {
                if (!PayMana(player, skill.Id, out uint manaCost, out uint lifeCost)) continue;
                if (lifeCost > 0 || manaCost > 0)
                {
                    player.Frames.Area?.Life.Resend(player);
                    if (player.ProjectileRuntime is { Active: true } session)
                        session.Enqueue(() => SendSkillCost(player, lifeCost, "Mark on Hit cost"));
                }
                player.SkillReadyAt[gem.Effect] = now + 4;
                ApplyCurseLocked(player, target, skill.Id, now, false, gem, playerStats);
            }
        }
    }

    static async Task<bool> CastCurse(Player player,
        ushort skill, (int X, int Y) aim, uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(player, skill); string effect = SkillsFor(player).EffectId(gem.Effect)!;
        PlayerSkillSource? source;
        source = CurrentPlayerSkillSourceLocked(player, skill);
        if (source is not { Active: true }) return false;
        double release = AreaTime(player) + (CastMs(player, gem.Effect, skill) ?? 1000) / 1000d;
        var lifetime = CombatLifetime;
        while (true)
        {
            if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || player.Closed || player.LeavingArea
                || player.PlayerLife == 0 || recovery != player.ActionInterruptionGeneration) return false;
            if (!player.Paused && AreaTime(player) >= release)
            {
                double now = AreaTime(player); var playerStats = CharacterPlayerStats(player);
                var currentSource = CurrentPlayerSkillSourceLocked(player, skill);
                var currentGem = SupportedSkill(player, skill);
                if (currentSource is not { Active: true } || currentSource.Identity != source.Identity
                    || currentGem.Effect != gem.Effect || !currentGem.RequirementsMet
                    || GemSkillInstanceDisabled(player, currentGem.Effect, skill) is not null) return false;
                RecordCompletedCurseCast(player, effect, AreaTime(player));
                ApplyCurseTargetsLocked(player, skill, currentGem, CurseCatalog.All[effect], aim, target, now, playerStats);
                break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        await EnemyDebuffsTick(player, AreaTime(player));
        return true;
    }

    static void ApplyCurseTargetsLocked(Player player, ushort skill, SupportedGem gem, CurseCatalog.Definition curse,
        (int X, int Y) aim, uint target, double now, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (curse.Mark)
        {
            if (player.Area.Monsters.GetValueOrDefault(target) is { } marked)
                ApplyCurseLocked(player, marked, skill, now, false, gem, playerStats);
            return;
        }
        int radius = CurseRadius(player, skill, false, gem, playerStats);
        foreach (var monster in player.Area.Monsters.Values)
            if (Dist(monster.Walk.Position(now), aim) <= radius)
                ApplyCurseLocked(player, monster, skill, now, false, gem, playerStats);
    }

    static bool WitchbaneImmunityActive(Player player, double now) =>
        player.PlayerLife > 0 && player.WitchbaneImmunityUntil > now;

    static void WitchbaneOnKill(Player player, SpawnedMonster target, double now)
    {
        double until = target.NonAuraHexUntilAtDeath;
        if (until <= Math.Max(now, player.WitchbaneImmunityUntil) || player.PlayerLife == 0
            || StatSourceValues(player).GetValueOrDefault(Stat.ImmuneToCursesOnKillingCursedEnemyForRemainingDurationOfCurse) == 0)
            return;
        player.WitchbaneImmunityUntil = until;
        if (HasMapHexes(MapModsOf(player))) RecomputePoolsAndSpeed(player);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        MarkPlayerStats(player, PlayerStatStep.SelfVulnerability);
        player.AuraStatsDirty = true;
    }

    // A curse aura of the player (Blasphemy): its gem and radius, the zone's (EnemyDebuffs.Rules' CurseZone).
    record struct CurseAura(AuraCatalog.Aura Aura, SupportedGem Gem, int Radius);

    // The player's curse auras now, within the curse limit, and the stats their curses read.
    static CurseAura[] CurseAuras(Player player, out IReadOnlyDictionary<uint, int>? playerStats)
    {
        var active = player.Auras.Values.Where(a => CurseCatalog.Hex(a.EffectId) && !ExternalTemporalChains(player, a.EffectId)).ToArray();
        var stats = active.Length > 0 ? CharacterPlayerStats(player) : null;
        playerStats = stats;
        return [.. active.TakeLast(stats is null ? 0 : CurseLimit(player, stats))
            .Select(a => (Aura: a, Gem: SupportedSkill(player, a.SkillId)))
            .Select(a => new CurseAura(a.Aura, a.Gem, CurseRadius(player, a.Aura.SkillId, true, a.Gem, stats)))];
    }

    // One monster's curses: a mark ends with its monster or caster, a curse aura's with its zone (inAura), and what
    // changed is shown.
    static void StepCursesLocked(Player player, CurseAura[] auras, IReadOnlyDictionary<uint, int>? playerStats, uint id, SpawnedMonster m,
        double now, Func<ushort, bool> inAura)
    {
        bool hasCurses = m.HasCurses;
        if (hasCurses || auras.Length > 0)
        {
            bool dead = m.Dead || m.Life == 0;
            if (hasCurses && m.Curses.All.Count > 0)
            {
                foreach (var curse in m.Curses.All.Values.Where(c => c.Mark
                    && (dead && !c.RemainsOnDeath || c.Caster == player.Id && player.PlayerLife == 0)).ToArray())
                    m.Curses.Remove(curse.Caster, curse.Effect);
                foreach (var curse in m.Curses.All.Values.Where(c => c.Caster == player.Id && c.Aura).ToArray())
                    if (player.PlayerLife == 0 || dead || !auras.Any(a => a.Aura.SkillId == curse.Skill && inAura(a.Aura.SkillId)))
                        m.Curses.Remove(curse.Caster, curse.Effect);
            }
            if (auras.Length > 0 && player.PlayerLife > 0 && !InTown(player))
                foreach (var aura in auras)
                    if (inAura(aura.Aura.SkillId)) ApplyCurseLocked(player, m, aura.Aura.SkillId, now, true, aura.Gem, playerStats);
            if (m.HasCurses)
            {
                if (m.Curses.All.Count > 0)
                {
                    m.Curses.Expire(now);
                    var effects = m.Effects;
                    int Get(uint key) => effects[key];
                    m.Curses.Retime(now, RecipientEffectTime.Rate(5, Get), RecipientEffectTime.Rate(5, Get, temporalChains: true));
                }
                bool Visible(MonsterCurses.Curse curse) => !dead || !m.CorpseConsumed && curse.RemainsOnDeath;
                if (m.Curses.Presented.Count > 0)
                {
                    var changed = m.Curses.Presented.Values.Where(c => !m.Curses.All.TryGetValue((c.Caster, c.Effect), out var next)
                        || next.Buff != c.Buff || next.Until != c.Until || !Visible(next)).ToArray();
                    foreach (var old in changed)
                    { player.Area.Buffs.Clear(id, old.Row, old.Caster, tag: old.Buff); m.Curses.Presented.Remove((old.Caster, old.Effect)); }
                }
                // A curse is its caster's; each application its own buff (its tag), so a renewed curse restarts its countdown.
                if (m.Curses.All.Count > 0)
                    foreach (var curse in m.Curses.All.Values)
                    {
                        if (!Visible(curse) || m.Curses.Presented.ContainsKey((curse.Caster, curse.Effect))) continue;
                        player.Area.Buffs.Set(id, new(curse.Row, curse.Caster, curse.Skill, Tag: curse.Buff,
                            U68: double.IsFinite(curse.Until) ? Buff.Seconds(curse.Until - now) : null));
                        m.Curses.Presented[(curse.Caster, curse.Effect)] = curse;
                    }
            }
        }
        bool anyActiveCurse = m.HasCurses && m.Curses.All.Count > 0;
        double slow = !anyActiveCurse ? 0
            : m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                ? m.Curses[Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal] : m.Curses[Stat.TemporalChainsActionSpeedPlusPctFinal];
        if (!ExternalTemporalChains(player, "TemporalChains"))
        {
            double oldRate = m.RecipientDebuffRate;
            double rate;
            if (!anyActiveCurse && m.EncounterStats is null && m.BuffStats is null && m.TemporaryStats.Count == 0 && ReferenceEquals(m.Mods, MonsterModSet.None))
                rate = 1;
            else
            {
                var effects = m.Effects;
                rate = RecipientEffectTime.Rate(2, n => effects[n]);
            }
            if (rate != oldRate)
            {
                SettleBleeding(m, now);
                m.Ignites.SetExpiryRate(now, rate); m.Poisons.SetExpiryRate(now, rate); m.Ailments.SetExpiryRate(now, rate);
                m.RuleAilments.SetExpiryRate(now, rate);
                m.SpiderWebs.SetExpiryRate(now, rate);
                m.TauntedUntil = RecipientEffectTime.RetimedDeadline(m.TauntedUntil, now, oldRate, rate); m.RecipientDebuffRate = rate;
                m.Ruptures.SetExpiryRate(now, rate);
                m.Bleeds.SetExpiryRate(now, BleedingExpiryRate(m, now));
            }
            // SlowestActionFactor is <= 1, so when slow == 0 (no Temporal Chains), Max(floor, 1) is always 1.
            m.ReceivedActionFactor = slow == 0 ? 1 : Math.Max(BossMods.SlowestActionFactor(m.Variety), 1 + slow / 100);
            m.ReceivedActionUntil = slow != 0 ? double.PositiveInfinity : now;
        }
        if (m.HasCurses && m.Curses.PresentedVersion != m.Curses.Version)
        {
            var keys = m.Curses.PresentedStats.Concat(m.Curses.Stats.Keys).Distinct().ToArray();
            var effects = m.Effects;
            SetStats(player, id, keys.Where(StatCatalog.IsClientId)
                .Select(n => (n, effects[n] + SpiderRecipientStat(m, n, now))));
            m.Curses.PresentedStats.Clear(); m.Curses.PresentedStats.UnionWith(m.Curses.Stats.Keys);
            m.Curses.PresentedVersion = m.Curses.Version;
        }
    }
}
