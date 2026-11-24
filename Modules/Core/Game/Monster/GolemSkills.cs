using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const uint FreshMeatDurationPercent = Stat.SupportRecentMinionsEffectDurationIsPctSummonDuration;
    const uint WakenedFuryChance = Stat.SupportRecentMinionsAdditionalCriticalStrikeChanceFromWakenedFury;
    const uint WakenedFuryMultiplier = Stat.SupportRecentMinionsAdditionalCriticalStrikeMultiplierFromWakenedFury;

    static double FreshMeatSeconds(IReadOnlyDictionary<uint, int> own, double summonSeconds)
    {
        int percent = GolemStat(own, FreshMeatDurationPercent);
        if (percent <= 0) return 0;
        double maximum = GolemStat(own, Stat.SupportRecentMinionsMaxEffectDurationMs) / 1000.0;
        return Math.Max(0, Math.Min(maximum, summonSeconds * (percent / 100.0)));
    }

    static bool FreshMeatActive(GolemState.Golem minion, double now) =>
        minion.FreshMeatUntil > 0 && minion.FreshMeatUntil > now && GolemStat(minion.Own, FreshMeatDurationPercent) > 0;

    static double MindlessAggressionDamage(IReadOnlyDictionary<uint, int> stats) =>
        Math.Max(0, 1 + GolemStat(stats, Stat.NecromancerMinionDamagePlusPctFinal) / 100.0);

    public static bool GolemSummonedInPastEightSeconds(GolemState.Golem golem, double at) =>
        golem.Definition.IsGolem && at >= golem.SummonedAt && at - golem.SummonedAt < 8;

    public static int GolemRecentDamageIncrease(GolemState.Golem golem, double at) =>
        GolemSummonedInPastEightSeconds(golem, at)
            ? GolemStat(golem.Player, Stat.GolemDamagePlusPctIfSummonedInPast8Seconds) : 0;

    public readonly record struct GolemOwnerResistances(int Fire, int Cold, int Lightning, int Chaos);

    static GolemOwnerResistances GolemOwnerResistancesFor(Player player)
    {
        int Capped(DamageType type) => DamageModel.Capped(Resistance(player, type), MaxResistance(player, type));
        return new(Capped(DamageType.Fire), Capped(DamageType.Cold), Capped(DamageType.Lightning), Capped(DamageType.Chaos));
    }

    public static MonsterModSet GolemModifiers(Player player, GolemState.Golem golem,
        IReadOnlyDictionary<uint, int> playerStats, double at = double.NegativeInfinity,
        GolemOwnerResistances? ownerResistances = null)
    {
        var values = new Dictionary<uint, int>(golem.InheritedMods.Effects.Stats);
        foreach (var (key, value) in playerStats)
        {
            string name = Stats.Name(key);
            uint target;
            if (name.StartsWith("minion_", StringComparison.Ordinal))
                target = name[7..] switch
                {
                    "elemental_resistance_%" => Stat.BaseResistAllElementsPct,
                    "fire_damage_resistance_%" => Stat.BaseFireDamageResistancePct,
                    "cold_damage_resistance_%" => Stat.BaseColdDamageResistancePct,
                    "lightning_damage_resistance_%" => Stat.BaseLightningDamageResistancePct,
                    "chaos_resistance_%" => Stat.BaseChaosDamageResistancePct,
                    "maximum_all_elemental_resistances_%" => Stat.AdditionalMaximumAllElementalResistancesPct,
                    "additional_physical_damage_reduction_%" => Stat.BaseAdditionalPhysicalDamageReductionPct,
                    "skill_area_of_effect_+%" or "area_of_effect_+%" => Stat.BaseSkillAreaOfEffectPlusPct,
                    var plain => Stat.Of(plain),
                };
            else if (key is Stat.EnergyShieldPlusPctFinalForMinion or Stat.MinionsHaveNoArmourOrEnergyShield)
                target = key;
            else continue;
            values[target] = ActorScale.Saturate((long)values.GetValueOrDefault(target) + value);
        }
        foreach (var (key, value) in golem.Own)
        {
            if (key is WakenedFuryChance or WakenedFuryMultiplier) continue;
            string name = Stats.Name(key) ?? "";
            // Spell Totem changes the summoning skill, not the actor that skill summons.
            if (name is "is_totem" or "base_skill_is_totemified" or "skill_is_totemified" or "is_ranged_attack_totem") continue;
            uint id = key;
            if (name.StartsWith("minion_", StringComparison.Ordinal)) id = Stat.Of(name = name[7..]);
            if (key == Stat.MinionAreaOfEffectPlusPct) id = Stat.BaseSkillAreaOfEffectPlusPct;
            // SupportedSkill already projects its global added damage into the attack and spell forms.
            if (name.StartsWith("global_", StringComparison.Ordinal) && name.Contains("_added_", StringComparison.Ordinal)
                && name.EndsWith("_damage", StringComparison.Ordinal)) continue;
            values[id] = values.GetValueOrDefault(id) + value;
        }
        double now = double.IsNegativeInfinity(at) ? AreaTime(player) : at;
        var link = MinionLinkOffenceAt(player, golem, playerStats, now);
        void AddLinkStat(uint stat, int value)
        {
            if (value != 0) values[stat] = ActorScale.Saturate((long)values.GetValueOrDefault(stat) + value);
        }
        AddLinkStat(Stat.GlobalMinimumAddedFireDamage, link.FireMin);
        AddLinkStat(Stat.GlobalMaximumAddedFireDamage, link.FireMax);
        AddLinkStat(Stat.BaseChanceToIgnitePct, link.Ignite);
        AddLinkStat(Stat.BaseCriticalStrikeMultiplierPlus, link.CritMultiplier);
        AddLinkStat(Stat.AccuracyRatingPlusPct, link.Accuracy);
        if (link.MainHandCrit is int linkedCrit)
        {
            values[Stat.DisplayCriticalLinkOverridesMainHandCriticalStrikeChance] = 1;
            values[Stat.OverrideMainHandCriticalStrikeChancePctFromCriticalLink] = linkedCrit;
        }
        if (FreshMeatActive(golem, now))
        {
            foreach (var (key, value) in AntiquarianEffects.AdrenalineStats)
                values[key] = ActorScale.Saturate((long)values.GetValueOrDefault(key) + value);
            values[WakenedFuryChance] = GolemStat(golem.Own, WakenedFuryChance);
            values[Stat.BaseCriticalStrikeMultiplierPlus] = ActorScale.Saturate(
                (long)values.GetValueOrDefault(Stat.BaseCriticalStrikeMultiplierPlus) + GolemStat(golem.Own, WakenedFuryMultiplier));
        }
        foreach (var (name, value) in golem.Auras.Concat(golem.Utility)) values[name] = values.GetValueOrDefault(name) + value;
        foreach (var (name, value) in golem.Gear) values[name] = values.GetValueOrDefault(name) + value;
        if (now < golem.OnslaughtUntil && ItemsFor(player).Onslaught is { } onslaught)
        {
            double effect = Math.Max(0, 1 + values.GetValueOrDefault(Stat.OnslaughtEffectPlusPct) / 100d);
            foreach (var source in UtilityFlasks.OnslaughtSources(onslaught, effect))
                values[source.Stat] = ActorScale.Saturate((long)values.GetValueOrDefault(source.Stat) + source.Value);
            values[Stat.CurrentlyHasOnslaught] = 1;
        }
        if (golem.CombatCharges is { } charges)
        {
            int frenzy = charges.Held(ChargeType.Frenzy);
            values[Stat.AttackSpeedPlusPct] = values.GetValueOrDefault(Stat.AttackSpeedPlusPct) + 4 * frenzy;
            values[Stat.CastSpeedPlusPct] = values.GetValueOrDefault(Stat.CastSpeedPlusPct) + 4 * frenzy;
            values[Stat.CriticalStrikeChancePlusPct] = values.GetValueOrDefault(Stat.CriticalStrikeChancePlusPct)
                + 50 * charges.Held(ChargeType.Power);
            int endurance = charges.Held(ChargeType.Endurance);
            values[Stat.BaseAdditionalPhysicalDamageReductionPct] = values.GetValueOrDefault(Stat.BaseAdditionalPhysicalDamageReductionPct)
                + 4 * endurance;
            values[Stat.AdditionalElementalDamageReductionPct] = values.GetValueOrDefault(Stat.AdditionalElementalDamageReductionPct)
                + 4 * endurance;
        }
        int Get(uint n) => GolemStat(playerStats, n);
        if (Get(Stat.NecromancerMinionDamagePlusPctFinal) is int damageMore and not 0)
            values[Stat.NecromancerMinionDamagePlusPctFinal] = damageMore;
        // Ancient Skull's real MinionCriticalStrikeChanceMaximumPowerChargeUnique__1 row uses the owner's maximum Power
        // Charges, not the charges held and not a minion's ability scheduling count. Charges.LimitsOf is the same
        // maximum kernel as the owner's charge session; its maximum already includes Character.ot's base three.
        int maximumPowerChargeCrit = ActorScale.Saturate((long)Get(Stat.MinionCriticalStrikeChancePlusPctPerMaximumPowerCharge)
            * OwnerMaximumPowerCharges(playerStats));
        if (maximumPowerChargeCrit != 0)
            values[Stat.CriticalStrikeChancePlusPct] = ActorScale.Saturate((long)values.GetValueOrDefault(Stat.CriticalStrikeChancePlusPct)
                + maximumPowerChargeCrit);
        // Speaker's Wreath's real rows are per complete 50 of the owner's final Dexterity. This is the same
        // authoritative attribute snapshot the character pools use (class, equipment, flat and percentage sources),
        // never the minion's Dexterity. The attack row intentionally does not enter cast speed.
        int dexGroups = Math.Max(0, Get(Stat.Dexterity)) / 50;
        int dexAttackSpeed = ActorScale.Saturate((long)dexGroups * Get(Stat.MinionAttackSpeedPlusPctPer50Dex));
        int dexMovementSpeed = ActorScale.Saturate((long)dexGroups * Get(Stat.MinionMovementSpeedPlusPctPer50Dex));
        values[Stat.AttackSpeedPlusPct] = ActorScale.Saturate((long)values.GetValueOrDefault(Stat.AttackSpeedPlusPct) + dexAttackSpeed);
        values[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate((long)values.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct)
            + dexMovementSpeed);
        int inheritedCoefficient = Get(Stat.MinionsGainXPercentOfYourResistances);
        if (inheritedCoefficient != 0)
        {
            var owner = ownerResistances ?? GolemOwnerResistancesFor(player);
            void Inherit(string type, int resistance)
            {
                uint key = Stat.Of($"base_{type}_damage_resistance_%");
                long inherited = (long)Math.Floor((double)resistance * inheritedCoefficient / 100d);
                values[key] = ActorScale.Saturate((long)values.GetValueOrDefault(key) + inherited);
            }
            Inherit("fire", owner.Fire); Inherit("cold", owner.Cold);
            Inherit("lightning", owner.Lightning); Inherit("chaos", owner.Chaos);
        }
        if (values.GetValueOrDefault(Stat.MinionsHaveNoArmourOrEnergyShield) != 0)
            values.Remove(Stat.BasePhysicalDamageReductionRating);
        if (!golem.Definition.IsGolem)
        {
            values[Stat.GlobalMinimumAddedPhysicalDamage] = values.GetValueOrDefault(Stat.GlobalMinimumAddedPhysicalDamage)
                + Get(Stat.NonGolemMinionGlobalMinimumAddedPhysicalDamage);
            values[Stat.GlobalMaximumAddedPhysicalDamage] = values.GetValueOrDefault(Stat.GlobalMaximumAddedPhysicalDamage)
                + Get(Stat.NonGolemMinionGlobalMaximumAddedPhysicalDamage);
            values[Stat.AttackSpeedPlusPct] = values.GetValueOrDefault(Stat.AttackSpeedPlusPct) + values.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPct);
            values[Stat.BaseMovementVelocityPlusPct] = values.GetValueOrDefault(Stat.Of("movement_speed_+%")) + values.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct);
            values.Remove(Stat.LifeRegenerationRatePerMinutePct);
            return golem.InheritedMods with { Effects = new(values) };
        }
        string family = golem.Definition.Kind == "Carrion" ? "bone" : golem.Definition.Kind.ToLowerInvariant();
        int recent = GolemSummonedInPastEightSeconds(golem, at)
            ? Get(Stat.GolemDamagePlusPctIfSummonedInPast8Seconds) : 0;
        values[Stat.DamagePlusPct] = values.GetValueOrDefault(Stat.DamagePlusPct) + Get(Stat.Of("golem_damage_+%")) + Get(Stat.Of(family + "_golem_damage_+%"))
            + Get(Stat.GolemDamagePlusPctPerActiveGolemType) * Math.Max(1, Get(Stat.VirtualCurrentNumberOfGolemTypes))
            + Get(Stat.GolemDamagePlusPctPerActiveGolem) * Get(Stat.VirtualTotalNumberOfActiveGolems) + recent;
        values[Stat.AttackAndCastSpeedPlusPct] = values.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPct) + Get(Stat.GolemAttackAndCastSpeedPlusPct);
        values[Stat.AttackSpeedPlusPct] = values.GetValueOrDefault(Stat.AttackSpeedPlusPct) + values.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPct);
        values[Stat.BaseMovementVelocityPlusPct] = values.GetValueOrDefault(Stat.Of("movement_speed_+%")) + values.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct) + Get(Stat.GolemMovementSpeedPlusPct);
        values[Stat.BaseResistAllElementsPct] = values.GetValueOrDefault(Stat.BaseResistAllElementsPct) + Get(Stat.Of(family + "_golem_elemental_resistances_%"));
        // Recovery is stepped once by the owner controller, not again by RegenerateMonstersLocked.
        values.Remove(Stat.LifeRegenerationRatePerMinutePct);
        return golem.InheritedMods with { Effects = new(values) };
    }

    static int OwnerMaximumPowerCharges(IReadOnlyDictionary<uint, int> player) =>
        Charges.LimitsOf(ChargeType.Power, name => GolemStat(player, name)).Maximum;

    public static uint NativeEnergyShield(double value) => value <= 0 ? 0
        : value >= int.MaxValue ? int.MaxValue : (uint)Math.Floor(value);

    static uint SnapshotMaximumEnergyShield(MonsterObject monster) =>
        Stat.MaximumEnergyShield is var key
            ? (uint)Math.Max(0, monster.Stats?.FirstOrDefault(stat => stat.Key == key).Value ?? 0) : 0;

    internal static void PublishEnergyShieldLocked(Player player, GolemState.Golem golem)
    {
        if (!player.MinionOrigins.TryGetValue(golem.Id, out var origin)) return;
        var current = golem.Shield;
        var maximum = golem.MaximumShield;
        var previousCurrent = origin.Object.EnergyShield;
        var previousMaximum = SnapshotMaximumEnergyShield(origin.Object);
        if (current == previousCurrent && maximum == previousMaximum) return;
        var maximumKey = Stat.MaximumEnergyShield;
        IReadOnlyList<(uint Key, int Value)>? stats = origin.Object.Stats;
        if (maximum != previousMaximum)
        {
            var rebuilt = stats?.Where(stat => stat.Key != maximumKey).ToList() ?? [];
            if (maximumKey is uint key) rebuilt.Add((key, (int)maximum));
            stats = rebuilt.Count == 0 ? null : rebuilt;
        }
        var synced = origin.Object with { EnergyShield = current, Stats = stats };
        player.MinionOrigins[golem.Id] = origin with { Object = synced };
        if (!player.Minions.OutputQueue.Any(output => output.Kind == PlayerMinions.OutputKind.EnergyShield
                && output.TargetId == golem.Id))
            player.Minions.OutputQueue.Add(new([], "golem energy shield", golem.Id, golem.Monster, PlayerMinions.OutputKind.EnergyShield));
    }

    public static byte[]? GolemEnergyShieldPresentation(Player player, PlayerMinions.Output output) =>
        GolemEnergyShieldRecords(player, output) is { } records ? [.. records.SelectMany(p => p)] : null;

    static byte[][]? GolemEnergyShieldRecords(Player player, PlayerMinions.Output output)
    {
        if (output.TargetId is not uint id || output.Target is null) return null;
        GolemState.Golem? golem = player.Golems.All.GetValueOrDefault(id)
            ?? player.Reservations.Fighters.GetValueOrDefault(id)
            ?? player.Summons.Fighters.GetValueOrDefault(id);
        if (golem is null || !ReferenceEquals(golem.Monster, output.Target) || !GolemCurrent(player, golem)) return null;
        var maximum = golem.MaximumShield;
        var current = Math.Min(maximum, golem.Shield);
        player.Area.Life.Set(id, AreaLife.EnergyShieldPool, current);
        SetStats(player, id, [(Stat.MaximumEnergyShield, (int)maximum)]);
        return [];
    }

    static int SkeletonShieldChaos(Player player, GolemState.Golem fighter, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (fighter.Definition.Kind != "Skeleton") return 0;
        int percent = GolemStat(playerStats, Stat.YourSkeletonsGainAddedChaosDamageEqualToPctOfEnergyShieldOnShield);
        if (percent <= 0 || ActiveOffHand(player) is not { Type: { Class: "Shield" } shield } hand) return 0;
        var items = ItemsFor(player);
        if (items.Defences(shield) is not { } defences) return 0;
        int localShield = ItemDefences.Calculate(hand.Item, defences, items.Mods).EnergyShield;
        return (int)Math.Min(int.MaxValue, (long)localShield * percent / 100);
    }

    public static IReadOnlyList<GolemState.Ability> GolemAbilities(Player player, MonsterCatalog.Variety variety, byte level,
        GolemState.Golem golem, IReadOnlyDictionary<uint, int> playerStats, DamageType element) =>
        GolemAbilities(player, variety, level, golem, playerStats, at: null, prismatic: element);

    public static IReadOnlyList<GolemState.Ability> GolemAbilities(Player player, MonsterCatalog.Variety variety, byte level,
        GolemState.Golem golem, IReadOnlyDictionary<uint, int> playerStats, double? at = null,
        GolemOwnerResistances? ownerResistances = null, DamageType prismatic = DamageType.Fire)
    {
        var catalog = SkillsFor(player); var result = new List<GolemState.Ability>();
        double now = at ?? AreaTime(player);
        var mods = GolemModifiers(player, golem, playerStats, now, ownerResistances).Effects;
        int shieldChaos = SkeletonShieldChaos(player, golem, playerStats);
        int Own(uint name) => GolemStat(golem.Own, name);
        foreach (var (effect, index) in (variety.GrantedEffects ?? []).Select((e, i) => (e, i)))
        {
            var (skillLevel, raw, multiplier, crit) = GolemData.Skill(catalog, effect, level);
            if (effect == 306)
            {
                foreach (var element in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
                    if (Stats.Key($"elemental_hit_cannot_roll_{element.ToString().ToLowerInvariant()}_damage") is uint key)
                        raw[key] = element == prismatic ? 0 : 1;
                foreach (var type in DamageTypes.ConversionOrder)
                    if (type != prismatic && Stats.Key($"base_deal_no_{type.ToString().ToLowerInvariant()}_damage") is uint prohibited)
                        raw[prohibited] = 1;
            }
            int Raw(uint name) => GolemStat(raw, name);
            bool spell = catalog.IsSpell(effect) && !catalog.IsAttack(effect);
            double seconds = (spell || Raw(Stat.CastTimeOverridesAttackDuration) != 0) && catalog.CastTimeMs(effect) > 0
                ? catalog.CastTimeMs(effect) / 1000.0 : (golem.Weapon?.AttackMs ?? variety.AttackMs) / 1000.0;
            double speed = (1 + mods[Stat.MonsterRarityAttackCastSpeedPlusPctAndDamageMinusPctFinal] / 100d) * (1 + (spell ? mods[Stat.CastSpeedPlusPct] + mods[Stat.BaseCastSpeedPlusPct] + mods[Stat.AttackAndCastSpeedPlusPct] : mods[Stat.AttackSpeedPlusPct] + Raw(Stat.AttackSpeedPlusPct)) / 100.0);
            speed *= Math.Max(.01, 1 + Raw(spell ? Stat.ActiveSkillCastSpeedPlusPctFinal : Stat.ActiveSkillAttackSpeedPlusPctFinal) / 100.0);
            speed *= Math.Max(.01, 1 + Own(spell ? Stat.Of("active_skill_minion_cast_speed_+%_final") : Stat.ActiveSkillMinionAttackSpeedPlusPctFinal) / 100d);
            bool multistrike = MinionSupportMatches(effect, "SupportMultistrike");
            if (!spell && catalog.IsMelee(effect))
                speed *= (multistrike ? Math.Max(.01, 1 + Own(Stat.SupportMultipleAttacksMeleeAttackSpeedPlusPctFinal) / 100d) : 1)
                    * Math.Max(.01, 1 + Own(Stat.Of("support_melee_physical_attack_speed_+%_final")) / 100d);
            if (!spell) speed *= Math.Max(.01, 1 + catalog.AttackSpeedMultiplier(effect, skillLevel) / 100.0);
            seconds = Math.Max(.1, seconds / Math.Max(.1, speed));
            var damage = DamagePacket.Empty;
            if (spell)
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    string slug = type.ToString().ToLowerInvariant();
                    int min = Raw(Stat.Of($"spell_minimum_base_{slug}_damage")), max = Raw(Stat.Of($"spell_maximum_base_{slug}_damage"));
                    if (max > 0) damage = damage.Add(type, new(min, max));
                }
            else if (catalog.IsAttack(effect) && Raw(Stat.BaseDealNoDamage) == 0)
            {
                if (crit == 0) crit = MinionWeaponCritical(player, golem);
                if (mods[Stat.DisplayCriticalLinkOverridesMainHandCriticalStrikeChance] != 0)
                    crit = mods[Stat.OverrideMainHandCriticalStrikeChancePctFromCriticalLink];
                var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
                damage = (golem.Weapon is { } equipped ? equipped.LocalDamage ?? DamagePacket.Of(DamageType.Physical, equipped.MinDamage, equipped.MaxDamage)
                    : DamagePacket.Of(DamageType.Physical, min, max)).Scale(multiplier);
            }
            if (effect == 5268 && damage.MaxTotal <= 0)
            {
                var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
                damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(multiplier);
            }
            if (shieldChaos > 0 && damage.MaxTotal > 0 && Raw(Stat.BaseDealNoDamage) == 0)
            {
                double added = shieldChaos * AddedEffectiveness(player, effect, skillLevel);
                damage = damage.Add(DamageType.Chaos, new(added, added));
            }
            // Flat damage augments an existing hit; it must not turn movement, auras or DoTs into attacks.
            if (damage.MaxTotal > 0 && Raw(Stat.BaseDealNoDamage) == 0)
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    string slug = type.ToString().ToLowerInvariant();
                    int min = mods[Stat.Of($"global_minimum_added_{slug}_damage")] + mods[Stat.Of($"{(spell ? "spell" : "attack")}_minimum_added_{slug}_damage")];
                    int max = mods[Stat.Of($"global_maximum_added_{slug}_damage")] + mods[Stat.Of($"{(spell ? "spell" : "attack")}_maximum_added_{slug}_damage")];
                    if (max != 0) damage = damage.Add(type, new(min, max));
                }
            var conversion = new DamageConversion();
            foreach (var type in DamageTypes.ConversionOrder.Where(t => t != DamageType.Physical))
            {
                string slug = type.ToString().ToLowerInvariant();
                conversion.AddSkill(DamageType.Physical, type, Raw(Stat.Of($"base_physical_damage_%_to_convert_to_{slug}")) + Raw(Stat.Of($"skill_physical_damage_%_to_convert_to_{slug}")));
                conversion.AddGlobal(DamageType.Physical, type, mods[Stat.Of($"physical_damage_%_to_convert_to_{slug}")]
                    + mods[Stat.Of($"base_physical_damage_%_to_convert_to_{slug}")]);
                conversion.AddGain(DamageType.Physical, type, mods[Stat.Of($"physical_damage_%_to_add_as_{slug}")]);
            }
            if (effect == 306) conversion.AddSkill(DamageType.Physical, prismatic, 100);
            if (golem.Definition.Kind == "HolyArmament")
                conversion.AddSkill(DamageType.Physical, DamageType.Lightning, Own(Stat.SkillPhysicalDamagePctToConvertToLightning));
            var rawDamage = damage;
            double more = Math.Max(0, 1 + mods[Stat.MonsterRarityDamagePlusPctFinal] / 100d)
                * Math.Max(0, 1 - mods[Stat.MonsterRarityAttackCastSpeedPlusPctAndDamageMinusPctFinal] / 100d)
                * Math.Max(0, 1 + Own(Stat.VaalUpgradeMinionDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Own(Stat.ActiveSkillMinionDamagePlusPctFinal) / 100.0)
                * Math.Max(0, 1 + Own(Stat.SupportMinionDamagePlusPctFinal) / 100.0)
                * Math.Max(0, 1 + Own(Stat.Of("minion_damage_+%_final")) / 100.0)
                * MindlessAggressionDamage(playerStats)
                * Math.Max(0, 1 + Raw(Stat.ActiveSkillDamagePlusPctFinal) / 100.0)
                * (golem.Definition.IsGolem ? Math.Max(0, 1 + GolemTether(playerStats, "damage") / 100.0) : 1);
            more *= 1 + .04 * (golem.CombatCharges?.Held(ChargeType.Frenzy) ?? 0);
            if (golem.Definition.Kind != "Reaper") more *= Math.Max(0, 1 + GolemStat(playerStats, Stat.ActiveSkillNonReaperMinionDamagePlusPctFinal) / 100d);
            var types = new HashSet<int>();
            if (spell) types.Add(1);
            else { types.Add(0); if (catalog.IsMelee(effect) && golem.Weapon is not { Melee: false }) types.Add(19); }
            if (Raw(Stat.IsAreaDamage) != 0) types.Add(7);
            if (Raw(Stat.BaseIsProjectile) != 0) types.Add(2);
            var supportStats = golem.Own.Select(p => (Id: p.Key, p.Value))
                .Where(p => multistrike || p.Id != Stat.SupportMultipleAttackDamagePlusPctFinal).ToArray();
            var modified = DamagePacket.Empty;
            foreach (var path in conversion.Trace(damage))
            {
                if (effect == 306 && path.Type != prismatic) continue;
                if (GemSupportDamage.Prohibited(path.Type, supportStats)) continue;
                double increase = mods[Stat.DamagePlusPct] + (spell ? mods[Stat.SpellDamagePlusPct] : mods[Stat.AttackDamagePlusPct])
                    + (Raw(Stat.IsAreaDamage) != 0 ? mods[Stat.AreaDamagePlusPct] : 0);
                foreach (var type in DamageTypes.ConversionOrder)
                    if ((path.History & DamageTypes.HistoryOf(type)) != 0) increase += mods[Stat.Of(type.ToString().ToLowerInvariant() + "_damage_+%")];
                if ((path.History & DamageTypeHistory.Elemental) != 0) increase += mods[Stat.ElementalDamagePlusPct];
                var (general, typed) = GemSupportDamage.Multipliers(path.History, supportStats, types);
                double physicalMore = (path.History & DamageTypeHistory.Physical) != 0
                    ? Math.Max(0, 1 + Own(Stat.ActiveSkillMinionPhysicalDamagePlusPctFinal) / 100.0) : 1;
                double auraMore = spell ? Math.Max(0, 1 + mods[Stat.SpellDamageAuraSpellDamagePlusPctFinal] / 100.0) : 1;
                if (spell && (path.History & DamageTypeHistory.Lightning) != 0)
                    auraMore *= Math.Max(0, 1 + mods[Stat.WrathAuraSpellLightningDamagePlusPctFinal] / 100.0);
                // Elemental Army: "Minions deal 10% more Elemental Damage" (the support's other half is the exposure).
                double elementalMore = (path.History & DamageTypeHistory.Elemental) != 0
                    ? Math.Max(0, 1 + Own(Stat.SupportMinionTotemResistanceElementalDamagePlusPctFinal) / 100.0) : 1;
                modified = modified.Add(path.Type, path.Range.Scale(Math.Max(0, 1 + increase / 100) * more * physicalMore * auraMore * general * typed * elementalMore));
            }
            damage = modified;
            string id = catalog.EffectId(effect) ?? "";
            double range = spell ? Math.Max(20, 70 * (1 + Raw(Stat.SpellMaximumActionDistancePlusPct) / 100.0)) : variety.AttackRange;
            if (!spell && golem.Weapon is { } weapon) range = weapon.Melee ? weapon.Range : 70;
            if (golem.Definition.Kind == "HolyArmament") range += Own(Stat.MinionMeleeRangePlus);
            if (effect == 9638) range = Math.Max(range, Raw(Stat.SmiteLightningTargetRange));
            if (id.Contains("Whirling", StringComparison.Ordinal) || id.Contains("Cyclone", StringComparison.Ordinal) || id.Contains("Leap", StringComparison.Ordinal)) range = 45;
            double radius = Raw(Stat.ActiveSkillBaseAreaOfEffectRadius);
            if (radius <= 0) radius = SpectreChargeRoar(golem, effect) is not null ? 60
                : id.Contains("Slam", StringComparison.Ordinal) ? 15 : 10;
            radius *= (1 + Raw(Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal) / 100.0)
                * Math.Sqrt(Math.Max(0, 1 + (mods[Stat.BaseSkillAreaOfEffectPlusPct] + Raw(Stat.BaseSkillAreaOfEffectPlusPct)) / 100.0));
            ushort skill = (ushort)(0x4000 + index);
            var attack = new MonsterAttack(skill, seconds, range, damage) { Spell = spell, Ranged = spell || variety.RangedWeapon || golem.Weapon is { Melee: false }, Scripted = true };
            var projectile = catalog.MonsterProjectile(effect, includeWeaponAttacks: true);
            // Ice spear / Flame spray are named active skills rather than monster_projectile_spell rows.
            projectile ??= GolemProjectile(effect, catalog);
            if (effect != 9511 && !spell && (variety.RangedWeapon || golem.Weapon is { Melee: false }))
                projectile ??= new(golem.Weapon is { WieldingWand: true } ? OrdinaryRangedAttack.Wand : OrdinaryRangedAttack.Arrow, 750, false, 2);
            var contact = projectile is null ? null : MonsterReleaseFor(player, variety, effect, level, projectile);
            double cd = catalog.CooldownMs(effect, skillLevel) / 1000.0 / Math.Max(.01, 1 +
                (GolemStat(playerStats, Stat.MinionCooldownRecoveryPlusPct) + (golem.Definition.IsGolem ? GolemStat(playerStats, Stat.GolemSkillCooldownRecoveryPlusPct) : 0) + Own(Stat.MinionCooldownRecoveryPlusPct)) / 100.0);
            var ability = new GolemState.Ability(effect, skill, id, attack, raw, radius, cd, Math.Max(1, catalog.CooldownUses(effect, skillLevel)),
                Math.Clamp(contact?.Fraction ?? .5, .05, 1), projectile, crit);
            result.Add(ability);
            int AilmentStat(uint name) => mods[name] + Raw(name) + (name == Stat.CriticalAilmentDotMultiplierPlus ? 50 : 0);
            string hand = spell ? "spell" : "main_hand";
            var rows = AilmentPanelStats.Compute(new(hand, spell, !spell && projectile is null), rawDamage.Scale(more), conversion, AilmentStat);
            var ignite = WithIgniteSpread(MonsterIgnites.Settings.From(rows, hand, AilmentStat),
                mods.Stats, raw, spell, hand, includeActorBaseArea: true);
            var bleed = spell ? MonsterIgnites.Settings.None : BleedSettings(rows, hand, AilmentStat, golem.Weapon?.LocalSources, projectile is null, projectile is not null);
            double bleedMore = Math.Max(0, 1 + Own(Stat.ActiveSkillMinionBleedingDamagePlusPctFinal) / 100d);
            bleed = bleed with { Min = bleed.Min * bleedMore, Max = bleed.Max * bleedMore, CriticalMin = bleed.CriticalMin * bleedMore, CriticalMax = bleed.CriticalMax * bleedMore };
            var poison = MonsterPoisons.Settings.From(rows, hand, AilmentStat, golem.Weapon?.LocalSources);
            int chance = AilmentStat(Stat.BaseChanceToPoisonOnHitPct) + AilmentStat(spell ? Stat.SpellsChanceToPoisonOnHitPct : Stat.ChanceToPoisonOnHitWithAttacksPct) + poison.WeaponChance;
            var weaponLocal = spell ? null : golem.Weapon?.LocalSources;
            var penetration = damage.MaxTotal > 0 ? Penetration.Of(mods.Stats, Stats, !spell, raw, weaponLocal) : default;
            int LeechStat(uint name) => mods[name] + Raw(name);
            var lifeLeech = damage.MaxTotal > 0 && mods[Stat.CannotLeech] == 0 && mods[Stat.BaseCannotLeech] == 0
                && mods[Stat.CannotLeechLife] == 0 && mods[Stat.BaseCannotLeechLife] == 0
                ? LeechSources.Coefficients("life", LeechStat, !spell, weaponLocal is null ? null : weaponLocal.GetValueOrDefault)
                : DamagePacket.Empty;
            MinionHitProfiles.Add(ability, new(ignite, bleed, poison, chance, penetration, lifeLeech));
        }
        return result;
    }

    static int MinionWeaponCritical(Player player, GolemState.Golem golem)
    {
        // Natural minion weapons have 5% base crit. Animated weapons use the equipped item's local chance.
        if (golem.Weapon is not { } weapon || golem.Equipment.GetValueOrDefault((byte)2) is not { } item
            || ItemsFor(player).ByHash(item.TypeHash) is not { } type) return 500;
        return AddedBaseCriticalStrike.LocalWeaponChance(ItemsFor(player).WeaponCritical(type),
            weapon.LocalSources?.GetValueOrDefault(Stat.LocalCriticalStrikeChance) ?? 0,
            weapon.LocalSources?.GetValueOrDefault(Stat.LocalCriticalStrikeChancePlusPct) ?? 0);
    }

    static int GolemTether(IReadOnlyDictionary<uint, int> player, string kind)
    {
        uint? derived = Stats.Key($"golem_{kind}_+%_final_from_unique_primordial_tether");
        return derived is uint key && player.ContainsKey(key) ? player[key]
            : GolemStat(player, Stat.Of($"unique_primordial_tether_golem_{kind}_+%_final"));
    }

    static SkillCatalog.MonsterProjectileInfo? GolemProjectile(int effect, SkillCatalog skills) => effect switch
    {
        2378 => new("Metadata/Projectiles/FireElementalSpray", 750, false, 2),
        5025 or 5026 => new("Metadata/Projectiles/IceElementalProjectile", 750, false, 2),
        9427 or 9428 when skills.Projectile("Metadata/Projectiles/SummonPhantasmFadingProjectile") is { } p
            => new(p.Id, p.Speed, false, skills.ProjectileContactCells(p.Id) ?? 2),
        _ => null,
    };

    public static IReadOnlyList<GrantedSkill> GolemNativeSkills(Player player, byte level, IReadOnlyList<GolemState.Ability> abilities)
    {
        var result = new List<GrantedSkill>();
        foreach (var ability in abilities)
        {
            var row = GolemData.Skill(SkillsFor(player), ability.Effect, level);
            var stats = new Dictionary<uint, int>(ability.Stats);
            foreach (var (key, value) in ability.Attack.InPlaceSkillStats()) stats[key] = value;
            foreach (var (type, range) in ability.Attack.Damage.Components)
            {
                string kind = ability.Attack.Spell ? "spell" : "attack", slug = type.ToString().ToLowerInvariant();
                if (Stats.Key($"{kind}_minimum_base_{slug}_damage") is uint min) stats[min] = (int)range.Min;
                if (Stats.Key($"{kind}_maximum_base_{slug}_damage") is uint max) stats[max] = (int)range.Max;
            }
            if (SkillsFor(player).FindByRow(ability.Skill, ability.Effect, row.Level, [.. stats.Select(p => (p.Key, p.Value))]) is { } skill)
                result.Add(skill);
        }
        return result;
    }

    static void RefreshGolemLocked(Player player, GolemState.Golem golem, IReadOnlyDictionary<uint, int> playerStats, double? at = null)
    {
        double now = at ?? AreaTime(player);
        golem.Auras = MinionAuraStats(player, golem);
        var hash = new HashCode();
        hash.Add(CombatLifetime);
        hash.Add(GolemSummonedInPastEightSeconds(golem, now));
        hash.Add(FreshMeatActive(golem, now));
        hash.Add(now < golem.OnslaughtUntil);
        hash.Add(MinionLinkOffenceAt(player, golem, playerStats, now));
        hash.Add(SkeletonShieldChaos(player, golem, playerStats));
        foreach (var p in playerStats.Where(p => (Stats.Name(p.Key) ?? "").Contains("minion", StringComparison.Ordinal)
            || (Stats.Name(p.Key) ?? "").Contains("golem", StringComparison.Ordinal)).OrderBy(p => p.Key)) hash.Add(p);
        if (Stat.Dexterity is var dexKey) hash.Add((dexKey, playerStats.GetValueOrDefault(dexKey)));
        hash.Add(OwnerMaximumPowerCharges(playerStats));
        if (golem.CombatCharges is { } charges)
        {
            hash.Add(charges.Held(ChargeType.Endurance));
            hash.Add(charges.Held(ChargeType.Frenzy));
            hash.Add(charges.Held(ChargeType.Power));
        }
        int inheritedCoefficient = GolemStat(playerStats, Stat.MinionsGainXPercentOfYourResistances);
        GolemOwnerResistances? ownerResistances = inheritedCoefficient == 0 ? null : GolemOwnerResistancesFor(player);
        if (ownerResistances is { } resistanceHash) hash.Add(resistanceHash);
        foreach (var p in golem.Own.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Auras.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Utility.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Gear.OrderBy(p => p.Key)) hash.Add(p);
        int input = hash.ToHashCode();
        if (input == golem.InputHash) return;
        golem.InputHash = input;
        golem.Player = new(playerStats);
        var m = golem.Monster;
        var mods = GolemModifiers(player, golem, playerStats, now, ownerResistances);
        m.Mods = mods;
        var pools = GolemPools.Calculate(Monsters, m.Variety!, m.Level, golem, playerStats, mods.Effects);
        if (pools.Fatal)
        {
            m.MaxLife = 1; m.Life = 1;
            golem.ShieldPool.SetMaximum(0);
            golem.ShieldPool.Empty();
            golem.Abilities = []; golem.Skills = [];
            golem.OwnerBuff.Clear();
            foreach (var hazard in golem.Hazards)
                if (hazard.Object is uint art) player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(art), null, WorldId: art, RemovesObject: true));
            golem.Hazards.Clear(); golem.Pulses.Clear();
            PublishEnergyShieldLocked(player, golem);
            SetStats(player, golem.Id, [(Stat.MaximumLife, 1)]);
            GolemDieLocked(player, golem, now, canReform: false);
            if (player.MinionOrigins.TryGetValue(golem.Id, out var fatalOrigin))
                player.MinionOrigins[golem.Id] = fatalOrigin with
                {
                    Object = SyncLifeMaximum(fatalOrigin.Object, m, 1) with { Skills = [] },
                };
            return;
        }

        uint maximum = pools.MaximumLife;
        uint nextLife = (uint)Math.Clamp(m.Life, 1, maximum);
        bool maximumChanged = maximum != m.MaxLife;
        bool lifeChanged = nextLife != m.Life;
        m.MaxLife = maximum; m.Life = nextLife;
        if (maximumChanged)
            SetStats(player, golem.Id, [(Stat.MaximumLife, (int)maximum)]);
        if (maximumChanged || lifeChanged)
            player.Minions.OutputQueue.Add(new([], "golem life", golem.Id, m, PlayerMinions.OutputKind.Life));
        if (player.MinionOrigins.TryGetValue(golem.Id, out var origin))
            player.MinionOrigins[golem.Id] = origin with { Object = SyncLifeMaximum(origin.Object, m, maximum) };
        golem.ShieldPool.SetMaximum(NativeEnergyShield(pools.MaximumEnergyShield));
        PublishEnergyShieldLocked(player, golem);
        if (!golem.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal))
        {
            golem.Abilities = GolemAbilities(player, m.Variety!, m.Level, golem, playerStats, now, ownerResistances);
            golem.Skills = GolemNativeSkills(player, m.Level, golem.Abilities);
            player.Minions.OutputQueue.Add(new(InstanceEntities.PlayerSkillsChange(golem.Id, golem.Skills, []), "golem abilities updated", golem.Id, m, WorldId: golem.Id));
        }
        SetStats(player, golem.Id, [(Stat.MovementVelocityPlusPct, m.Mods.Effects.MovementPct)]);
        if (player.MinionOrigins.TryGetValue(golem.Id, out origin))
            player.MinionOrigins[golem.Id] = origin with { Object = origin.Object with { Skills = golem.Skills } };
    }

    static MonsterObject SyncLifeMaximum(MonsterObject origin, SpawnedMonster monster, uint maximum)
    {
        IReadOnlyList<(uint Key, int Value)>? stats = origin.Stats;
        int baseLife = monster.Variety is { } variety ? Monsters.BaseLife(variety, monster.Level) ?? 100 : 100;
        if (maximum != baseLife)
            stats = [.. (stats ?? []).Where(stat => stat.Key != Stat.MaximumLife), (Stat.MaximumLife, (int)maximum)];
        else if (stats is not null)
        {
            var rebuilt = stats.Where(stat => stat.Key != Stat.MaximumLife).ToList();
            stats = rebuilt.Count == 0 ? null : rebuilt;
        }
        return origin with { Life = maximum, Stats = stats };
    }
}
