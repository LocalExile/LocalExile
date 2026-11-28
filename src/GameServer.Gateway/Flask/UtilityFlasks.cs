using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Utility flasks (Agents/docs/protocol/13-items.md §8a): Quicksilver, Granite, Jade, Ruby, Sapphire, Topaz, Amethyst,
/// Bismuth, Basalt, Stibnite, Diamond, Quartz, Sulphur and Silver. Everything comes from the tables: Flasks.dat names the
/// flask's BuffDefinitions row, its BuffStatValues (one per StatsKeys entry) and its duration (RecoveryTime, tenths of a
/// second); ComponentCharges its charges. A use puts the buff on the player for the duration and its stats count on the
/// character until it ends (<see cref="Apply"/>, <see cref="RawStats"/>), as do the flask's "during effect" mods
/// (<see cref="DuringEffect"/>). How each stat counts is <see cref="SupportOf"/>: modelled, shown on the character panel
/// only (unsupported ailment sources and curses), or consumed by the authoritative Ward pool.
/// </summary>
public static class UtilityFlasks
{
    /// <summary>One base type's use: its buff (BuffDefinitions row and Id), the buff's stats with their values, how long
    /// it lasts, and its charges. <paramref name="Extra"/>: a second buff the flask grants with its own stats (the Silver
    /// Flask's Onslaught).</summary>
    public sealed record Utility(int BaseRow, ushort BuffRow, string BuffId, IReadOnlyList<(uint Stat, int Value)> Stats,
        double Seconds, int ChargesPerUse, int MaxCharges, ExtraBuff? Extra = null)
    {
        /// <summary>The charges as <see cref="Flasks"/> spends and refills them.</summary>
        public Flasks.Flask Charges => new(0, 0, Seconds, ChargesPerUse, MaxCharges);

        /// <summary>The stats this flask's buffs grant (its own and the extra buff's) that a use carries: all but the
        /// unsupported ones (<see cref="SupportOf"/>).</summary>
        public IEnumerable<(uint Stat, int Value)> Applied =>
            Stats.Concat(Extra?.Stats ?? []).Where(s => SupportOf(s.Stat) != Support.Unsupported);
    }

    /// <summary>How the server takes a stat a flask's effect grants.</summary>
    public enum Support
    {
        /// <summary>It counts: the pools, the walk speed, the damage code or another mechanic use it.</summary>
        Modelled,
        /// <summary>It goes to the client for the character panel only; its gameplay source/consumer is unsupported.</summary>
        PanelOnly,
        /// <summary>Ward bonuses consumed by the authoritative pool through <see cref="Apply"/>.</summary>
        Ward,
        /// <summary>Not applied.</summary>
        Unsupported,
    }

    /// <summary>The stats only the character panel reads (<see cref="Support.PanelOnly"/>).</summary>
    static readonly IReadOnlySet<uint> PanelOnly = new HashSet<uint>()
    {
        Stat.StunThresholdPlusPct,
        Stat.BaseAvoidStunPct,
        Stat.BaseStunRecoveryPlusPct,
        Stat.CurseEffectOnSelfPlusPct, Stat.ChanceToFreezeShockIgnitePct,
        Stat.ImmuneToPoison, Stat.ImmuneToBleeding,
        Stat.LightRadiusPlusPct,   // the client draws the light radius from the stat it gets
    };

    static readonly IReadOnlySet<uint> ElementalAilmentInputs = new HashSet<uint>()
    {
        Stat.BaseFrozenEffectOnSelfPlusPct, Stat.BaseAvoidChillPct, Stat.BaseAvoidFreezePct, Stat.BaseAvoidIgnitePct,
        Stat.BaseAvoidShockPct, Stat.ChillEffectivenessOnSelfPlusPct, Stat.BaseSelfFreezeDurationMinusPct, Stat.ShockedEffectOnSelfPlusPct,
        Stat.BaseImmuneToFreeze, Stat.BaseImmuneToChill, Stat.BaseImmuneToShock, Stat.BaseImmuneToIgnite,
    };

    /// <summary>How a flask effect's stat counts here.</summary>
    public static Support SupportOf(uint stat) =>
        ActorScale.IsInput(stat) || Modelled.Contains(stat) || ElementalAilmentInputs.Contains(stat) || HandedFlag(stat)
            ? Support.Modelled
        : PanelOnly.Contains(stat) ? Support.PanelOnly
        : stat is Stat.BaseWard or Stat.WardPlusPct ? Support.Ward
        : Support.Unsupported;

    /// <summary>A flask's "during effect" mods (suffixes, utility flasks only in 3.29): the flask's local stat, the
    /// character stat it grants while the effect runs, its sign, and whether it is a flag (not scaled by increased effect).
    /// Each is its own effect (poewiki: "different suffixes … stack"; two flasks with the same suffix: the strongest).</summary>
    public static readonly IReadOnlyList<(uint Local, uint Stat, int Sign, bool Flag)> DuringEffect =
    [
        (Stat.LocalFlaskArmourPlusPctWhileHealing, Stat.PhysicalDamageReductionRatingPlusPct, 1, false),
        (Stat.LocalFlaskEvasionPlusPctWhileHealing, Stat.EvasionRatingPlusPct, 1, false),
        (Stat.LocalFlaskMovementSpeedPlusPctWhileHealing, Stat.BaseMovementVelocityPlusPct, 1, false),
        (Stat.LocalFlaskResistancesPlusPctWhileHealing, Stat.BaseResistAllElementsPct, 1, false),
        (Stat.LocalFlaskCriticalStrikeChancePlusPctDuringFlaskEffect, Stat.CriticalStrikeChancePlusPct, 1, false),
        // "during effect" cast and attack speed suffixes: the same family as the movement-speed row above
        // (BuffDefinitions flask_bonus_cast_speed 1386 and flask_bonus_attack_speed 1385 grant
        // base_cast_speed_+% and attack_speed_+%).
        (Stat.LocalFlaskCastSpeedPlusPctWhileHealing, Stat.BaseCastSpeedPlusPct, 1, false),
        (Stat.LocalFlaskAttackSpeedPlusPctWhileHealing, Stat.AttackSpeedPlusPct, 1, false),
        (Stat.LocalFlaskWardPlusPctDuringEffect, Stat.WardPlusPct, 1, false),
        (Stat.LocalFlaskStunRecoveryPlusPctWhileHealing, Stat.BaseStunRecoveryPlusPct, 1, false),
        (Stat.LocalFlaskAvoidStunChancePctDuringFlaskEffect, Stat.BaseAvoidStunPct, 1, false),
        (Stat.LocalAvoidChillPctDuringFlaskEffect, Stat.BaseAvoidChillPct, 1, false),
        (Stat.LocalAvoidFreezePctDuringFlaskEffect, Stat.BaseAvoidFreezePct, 1, false),
        (Stat.LocalAvoidIgnitePctDuringFlaskEffect, Stat.BaseAvoidIgnitePct, 1, false),
        (Stat.LocalAvoidShockPctDuringFlaskEffect, Stat.BaseAvoidShockPct, 1, false),
        (Stat.LocalSelfChillEffectPlusPctDuringFlaskEffect, Stat.ChillEffectivenessOnSelfPlusPct, 1, false),
        (Stat.LocalSelfFreezeDurationPlusPctDuringFlaskEffect, Stat.BaseSelfFreezeDurationMinusPct, -1, false),
        (Stat.LocalSelfShockEffectPlusPctDuringFlaskEffect, Stat.ShockedEffectOnSelfPlusPct, 1, false),
        (Stat.LocalSelfCurseEffectPlusPctDuringFlaskEffect, Stat.CurseEffectOnSelfPlusPct, 1, false),
        (Stat.LocalFlaskChanceToFreezeShockIgnitePctWhileHealing, Stat.ChanceToFreezeShockIgnitePct, 1, false),
        (Stat.LocalFlaskImmuneToFreezeAndChillDuringFlaskEffect, Stat.BaseImmuneToFreeze, 1, true),
        (Stat.LocalFlaskImmuneToFreezeAndChillDuringFlaskEffect, Stat.BaseImmuneToChill, 1, true),
        (Stat.LocalFlaskImmuneToShockDuringFlaskEffect, Stat.BaseImmuneToShock, 1, true),
        (Stat.LocalFlaskDispelsBurningAndIgniteImmunityDuringEffect, Stat.BaseImmuneToIgnite, 1, true),
        (Stat.LocalFlaskImmuneToPoisonDuringFlaskEffect, Stat.ImmuneToPoison, 1, true),
        (Stat.LocalFlaskImmuneToBleedingAndCorruptedBloodDuringFlaskEffect, Stat.ImmuneToBleeding, 1, true),
        (Stat.LocalFlaskImmuneToBleedingAndCorruptedBloodDuringFlaskEffect, Stat.ImmuneToCorruptedBlood, 1, true),
        // Dying Sun: "(10-20)% increased Area of Effect during Effect", "Skills fire 2 additional Projectiles during
        // Effect". Both scale with increased flask effect, rounded down (2 x 1.5 = 3 projectiles).
        (Stat.LocalFlaskAreaOfEffectPlusPctDuringFlaskEffect, Stat.BaseSkillAreaOfEffectPlusPct, 1, false),
        (Stat.LocalFlaskNumberOfAdditionalProjectilesDuringFlaskEffect, Stat.NumberOfAdditionalProjectiles, 1, false),
        // Oriath's End: the kill explosion's chance (KillExplosions), scaled by flask effect.
        (Stat.LocalUniqueFlaskExplodeEnemiesFor10PctLifeAsRandomElementOnKillChancePctDuringFlaskEffect,
            Stat.ExplodeEnemiesFor10PctLifeAsRandomElementOnKillChancePct, 1, false),
        // Divination Distillate (a hybrid flask: RecoveryDuringEffects keeps these while its recovery runs).
        (Stat.LocalFlaskEnergyShieldRechargeNotDelayedByDamageDuringEffect, Stat.EnergyShieldRechargeNotDelayedByDamage, 1, true),
        (Stat.LocalUniqueFlaskItemRarityPlusPctWhileHealing, Stat.BaseItemFoundRarityPlusPct, 1, false),
        (Stat.LocalUniqueFlaskItemQuantityPlusPctWhileHealing, Stat.BaseItemFoundQuantityPlusPct, 1, false),
        (Stat.LocalUniqueFlaskLightRadiusPlusPctWhileHealing, Stat.LightRadiusPlusPct, 1, false),
        (Stat.LocalUniqueFlaskResistAllElementsPctDuringFlaskEffect, Stat.BaseResistAllElementsPct, 1, false),
        // Bottled Faith: increased critical strike chance against enemies on consecrated ground (Game.CriticalChanceOn),
        // and the increased damage taken that consecrated ground created during the effect applies (FlaskGrounds).
        (Stat.LocalUniqueFlaskCriticalStrikeChancePlusPctVsEnemiesOnConsecratedGroundDuringFlaskEffect,
            FlaskGrounds.VsConsecratedCriticalStat, 1, false),
        (Stat.LocalFlaskDuringEffectEnemyDamageTakenPlusPctOnConsecratedGround, FlaskGrounds.EnemyDamageTakenStat, 1, false),
        // Cinderswallow Urn: recovery on kill (KillRecovery.From reads the merged player stats), and the increased damage
        // taken retained by each ignite applied during the effect (MonsterIgnites).
        (Stat.LocalFlaskRecoverPctMaximumLifeOnKillDuringFlaskEffect, Stat.RecoverPctMaximumLifeOnKill, 1, false),
        (Stat.LocalFlaskRecoverPctMaximumManaOnKillDuringFlaskEffect, Stat.RecoverPctMaximumManaOnKill, 1, false),
        (Stat.LocalFlaskRecoverPctMaximumEnergyShieldOnKillDuringFlaskEffect, Stat.RecoverEnergyShieldPctOnKill, 1, false),
        (Stat.LocalFlaskEnemiesIgnitedDuringFlaskEffectDamageTakenPlusPct, IgnitedDuringEffectTakenStat, 1, false),
        // Rumi's Concoction and its Replica: additional chance to block attack and spell damage (the buff's own stats,
        // BuffDefinitions unique_flask_rumis_concoction), added to the pools' block before its increases (Apply).
        (Stat.LocalUniqueFlaskBlockPctWhileHealing, Stat.MonsterBaseBlockPct, 1, false),
        (Stat.LocalUniqueFlaskSpellBlockPctWhileHealing, Stat.BaseSpellBlockPct, 1, false),
        // Stormblood (and its cold sibling): all damage from critical strikes can shock (chill, freeze) during the effect
        // (MonsterElementalAilments.Inflicting). Sap and Brittle are not modelled.
        (Stat.LocalUniqueFlaskAllDamageFromCriticalStrikesCanShockDuringEffect, Stat.AllDamageFromCriticalStrikesCanShock, 1, true),
        (Stat.LocalUniqueFlaskAllDamageFromCriticalStrikesCanChillDuringEffect, Stat.AllDamageFromCriticalStrikesCanChill, 1, true),
        (Stat.LocalUniqueFlaskAllDamageFromCriticalStrikesCanFreezeDuringEffect, Stat.AllDamageFromCriticalStrikesCanFreeze, 1, true),
        // "25% reduced Mana Cost of Skills during Effect" (the official Topaz Flask,
        // JunMaster2LocalFlaskSkillManaCostDuringFlaskEffect1): the local stat is
        // negative, the character stat is the reduction, so the sign flips.
        // The finished skill mana cost reads base_mana_cost_-% as its first percentage.
        (Stat.LocalFlaskSkillManaCostPlusPctDuringFlaskEffect, Stat.BaseManaCostMinusPct, -1, false),
    ];

    // The "during effect" flags modules handed over (HandOverDuringEffectFlags), in the order they were handed over.
    static volatile (uint Local, uint Stat)[] _handedFlags = [];
    static readonly object HandedFlagsLock = new();

    static bool HandedFlag(uint stat)
    {
        foreach (var flag in _handedFlags) if (flag.Stat == stat) return true;
        return false;
    }

    /// <summary>A hand-over for modules: each pair is a flask's local "during effect" flag and the character stat it
    /// sets to 1 while the effect runs, as a <see cref="DuringEffect"/> flag row, put in the raw stats the rules read.
    /// The framework names none; disposing the result (the module's lifetime) takes back the pairs it added.</summary>
    public static IDisposable HandOverDuringEffectFlags(IReadOnlyCollection<(uint Local, uint Stat)> flags)
    {
        (uint, uint)[] added;
        lock (HandedFlagsLock)
        {
            added = [.. flags.Distinct().Except(_handedFlags)];
            _handedFlags = [.. _handedFlags, .. added];
        }
        return new FlagsHandOver(added);
    }

    sealed class FlagsHandOver((uint, uint)[] added) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (HandedFlagsLock) _handedFlags = [.. _handedFlags.Except(added)];
        }
    }

    /// <summary>A buff a flask grants besides its own (BuffDefinitions row, with its stats).</summary>
    public sealed record ExtraBuff(ushort BuffRow, string BuffId, IReadOnlyList<(uint Stat, int Value)> Stats);

    /// <summary>The buff stats the server applies to the character, and where: the pools (<see cref="Apply"/>) or the raw
    /// stats (<see cref="RawStats"/>), or both.</summary>
    public static readonly IReadOnlySet<uint> Modelled = new HashSet<uint>()
    {
        Stat.NoManaCost, Stat.EnergyShieldRechargeNotDelayedByDamage,
        Stat.BaseFireDamageResistancePct, Stat.BaseColdDamageResistancePct, Stat.BaseLightningDamageResistancePct,
        Stat.BaseChaosDamageResistancePct, Stat.BaseResistAllElementsPct,
        Stat.BaseMaximumFireDamageResistancePct, Stat.BaseMaximumColdDamageResistancePct,
        Stat.BaseMaximumLightningDamageResistancePct, Stat.BaseMaximumChaosDamageResistancePct,
        Stat.BasePhysicalDamageReductionRating, Stat.BaseEvasionRating,
        Stat.BasaltFlaskArmourPlusPctFinal, Stat.StibniteFlaskEvasionRatingPlusPctFinal,
        Stat.BaseMovementVelocityPlusPct, Stat.AttackSpeedPlusPct, Stat.BaseCastSpeedPlusPct,
        Stat.CriticalStrikeChancePlusPct, Stat.BaseSpellSuppressionChancePct, Stat.DamagePlusPct,
        Stat.PhysicalDamageReductionRatingPlusPct, Stat.EvasionRatingPlusPct, Stat.BaseItemFoundRarityPlusPct, Stat.PhaseThroughObjects,
        Stat.BaseSkillAreaOfEffectPlusPct, Stat.NumberOfAdditionalProjectiles,
        Stat.ExplodeEnemiesFor10PctLifeAsRandomElementOnKillChancePct,
        Stat.ImmuneToCorruptedBlood,
        Stat.BaseItemFoundQuantityPlusPct,   // ItemQuantityFactor
        FlaskGrounds.VsConsecratedCriticalStat, FlaskGrounds.EnemyDamageTakenStat,   // Bottled Faith
        Stat.RecoverPctMaximumLifeOnKill, Stat.RecoverPctMaximumManaOnKill, Stat.RecoverEnergyShieldPctOnKill,
        IgnitedDuringEffectTakenStat,   // Cinderswallow Urn
        Stat.MonsterBaseBlockPct, Stat.BaseSpellBlockPct,   // Rumi's Concoction (Apply)
        Stat.AllDamageFromCriticalStrikesCanShock, Stat.AllDamageFromCriticalStrikesCanChill,
        Stat.AllDamageFromCriticalStrikesCanFreeze,   // Stormblood
        Stat.BaseManaCostMinusPct,   // "reduced Mana Cost of Skills during Effect"
    };

    /// <summary>The flask buff flag that stands for Onslaught (the Silver Flask's flask_utility_haste has no stats of its
    /// own), and the BuffTemplates row with the player's Onslaught values (20% attack, cast and movement speed).</summary>
    const uint OnslaughtFlag = Stat.SilverFlaskDisplayOnslaught;
    const string OnslaughtBuff = "onslaught", OnslaughtTemplate = "MapModPlayerOnslaught";

    /// <summary>The utility flasks by BaseItemTypes row, from the client tables Flasks, BuffDefinitions, BuffTemplates,
    /// ComponentCharges and Stats: every one, whatever its stats do here (<see cref="SupportOf"/>).</summary>
    public static Dictionary<int, Utility> Load(string folder, IReadOnlyList<ItemCatalog.BaseType> rows, bool hardMode = false)
    {
        var result = new Dictionary<int, Utility>();
        if (new[] { "Flasks", "BuffDefinitions", "BuffTemplates", "ComponentCharges", "Stats" }.Any(t => !ClientTables.Has(t))) return result;
        var stats = new StatCatalog();
        static uint StatId(int key) => (uint)key + 1;

        var charges = new Dictionary<string, (int Max, int PerUse)>();
        foreach (var r in ClientTables.Table("ComponentCharges"))
            charges.TryAdd(r.Text("BaseItemTypesKey") ?? "", (r.Int("MaxCharges"), r.Int("PerCharge")));
        var buffRows = ClientTables.Table("BuffDefinitions").ToList();
        int onslaught = buffRows.FindIndex(b => b.Text("Id") == OnslaughtBuff);
        ExtraBuff? onslaughtBuff = null;
        if (onslaught >= 0)
        {
            foreach (var t in ClientTables.Table("BuffTemplates"))
                if (t.Text("Id") == OnslaughtTemplate + (hardMode ? "HardMode" : "")
                    && t.IntOrNull("BuffDefinitionsKey") is int k && k == onslaught)
                    onslaughtBuff = new((ushort)onslaught, OnslaughtBuff,
                        [.. buffRows[onslaught].Ints("StatsKeys").Select(StatId)
                            .Zip(t.Ints("Buff_StatValues"))]);
        }

        var flasks = ClientTables.Table("Flasks");
        foreach (var f in flasks)
        {
            if (f.IntOrNull("BaseItemTypesKey") is not int b || b >= rows.Count
                || f.IntOrNull("BuffDefinitionsKey") is not int bk || bk >= buffRows.Count
                || f.Int("LifePerUse") + f.Int("ManaPerUse") > 0
                || !charges.TryGetValue(rows[b].Id, out var use)) continue;
            var buff = buffRows[bk];
            // BuffStatValues has one value per StatsKeys entry (Ruby Flask: base_fire_damage_resistance_% 40,
            // base_maximum_fire_damage_resistance_% 5).
            List<(uint, int)> values = [.. buff.Ints("StatsKeys").Select(StatId)
                .Zip(f.Ints(hardMode && !f.IsNull("BuffStatValues2") ? "BuffStatValues2" : "BuffStatValues"))];
            bool grantsOnslaught = buff.Ints("GrantedFlags").Any(s => StatId(s) == OnslaughtFlag);
            // RecoveryTime is in tenths of a second, as for life flasks (Quicksilver Flask: 60, 6 s; Path of Building and
            // RePoE 3.29 agree). RecoveryTime2 is the HardMode duration (Quicksilver 20, 2 s).
            result.TryAdd(b, new Utility(b, (ushort)bk, buff.Text("Id") ?? "", values,
                f.Int(hardMode && !f.IsNull("RecoveryTime2") ? "RecoveryTime2" : "RecoveryTime") / 10.0,
                use.PerUse, use.Max, grantsOnslaught ? onslaughtBuff : null));
        }
        return result;
    }

    /// <summary>One use of a flask: its stats scaled by increased effect, and its duration with increased duration and
    /// quality. <paramref name="local"/>: the flask's own mods (ItemModStats.Local, by Stats.dat Id);
    /// <paramref name="character"/>: the character's stats (passives and gear: flask_duration_+%, flask_effect_+%,
    /// magic_utility_flask_effect_+% for a magic flask). As Path of Building's Item.lua and CalcPerform.lua: duration =
    /// base × (1 + local increased) × (1 + global increased) × (1 + quality) × (1 + local more), quality multiplying the other increases; effect
    /// = 1 + the local and the character's increased effect.</summary>
    public static (IReadOnlyList<Source> Stats, double Seconds) For(Utility u, Item flask,
        IReadOnlyDictionary<uint, int> local, Func<uint, int> character, int playerLevel = 1, double finalMultiplier = 1)
    {
        int Get(uint name) => local.GetValueOrDefault(name);
        double durationInc = PanelStats.FlaskDurationIncrease(character, playerLevel);
        // PoB Item.lua2421-2447 applies local duration/quality first; global FlaskDuration multiplies that result.
        double seconds = u.Seconds * (100.0 + Get(Stat.LocalFlaskDurationPlusPct)) / 100.0
            * (100.0 + durationInc) / 100.0 * (100.0 + flask.Quality) / 100.0
            * (100.0 + Get(Stat.LocalFlaskDurationPlusPctFinal)) / 100.0;
        double effect = FlaskEffectScaling.Multiplier(character, playerLevel, Get(Stat.LocalFlaskEffectPlusPct),
            flask.Rarity == ItemRarity.Magic ? character(Stat.MagicUtilityFlaskEffectPlusPct) : 0, finalMultiplier);
        bool Applies(uint stat) => SupportOf(stat) != Support.Unsupported;
        List<Source> stats = [.. u.Stats.Where(s => Applies(s.Stat))
            .Select(s => new Source($"buff {u.BuffRow}", s.Stat, s.Stat == Stat.PhaseThroughObjects ? s.Value
                : ActorScale.IsInput(s.Stat)
                    ? (int)Math.Clamp(Math.Truncate(Math.Round(s.Value * effect, 2)), int.MinValue, int.MaxValue)
                    : Scale(s.Value, effect)))];
        // A flask's Onslaught scales with the flask's effect modifiers (wiki Silver Flask, Onslaught: since 3.20 Onslaught is
        // a buff and flask effect applies when a flask grants it), added to the character's increased Onslaught effect.
        double onslaughtEffect = effect + character(Stat.OnslaughtEffectPlusPct) / 100.0;
        if (u.Extra is { } x) stats.AddRange(OnslaughtSources(x, onslaughtEffect));
        // The flask's "during effect" mods, scaled by increased effect too (poewiki, Quality: increased flask effect
        // improves "any of the mods that the flask has rolled"); flags stay as they are.
        stats.AddRange(DuringEffectSources(local, effect));
        return (stats, Math.Max(0.1, seconds));
    }

    /// <summary>Cinderswallow Urn's "Enemies Ignited by you during Effect take X% increased Damage", as a running
    /// effect's stat (no client stat carries it; the server reads it from the running flasks).</summary>
    public const uint IgnitedDuringEffectTakenStat = Stat.LocalFlaskEnemiesIgnitedDuringFlaskEffectDamageTakenPlusPct;

    /// <summary>A flask's "during effect" mods at <paramref name="effect"/> (its increased effect): utility flasks through
    /// <see cref="For"/>, life, mana and hybrid flasks through <see cref="RecoveryDuringEffects"/>.</summary>
    public static List<Source> DuringEffectSources(IReadOnlyDictionary<uint, int> local, double effect)
    {
        List<Source> stats = [];
        foreach (var (localStat, stat, sign, flag) in DuringEffect.Concat(_handedFlags.Select(f => (f.Local, f.Stat, 1, true))))
            if (local.GetValueOrDefault(localStat) is int value and not 0 && SupportOf(stat) != Support.Unsupported)
                stats.Add(new Source($"mod {stat}", stat, sign * (flag ? 1 : Scale(value, effect))));
        return stats;
    }

    /// <summary>The BuffDefinitions row of each "during effect" suffix buff, by the character stat it grants:
    /// every BuffDefinitions row whose Id starts with <c>flask_bonus_</c>, keyed by its StatsKeys or GrantedFlags
    /// entry (the granted stat's id, as <see cref="Load"/> reads it: Stats row + 1). Read from the tables, never
    /// hardcoded: flask_bonus_cast_speed 1386 (base_cast_speed_+%), flask_bonus_reduced_self_curse_effect 1928
    /// (curse_effect_on_self_+%), flask_bonus_evasion 43 (evasion_rating_+%), flask_bonus_armour 42
    /// (physical_damage_reduction_rating_+%), flask_bonus_mana_cost 1241 (base_mana_cost_-%), and the rest of
    /// the family the same way. Empty when the tables are missing, so a use then sends no suffix buff, as before.</summary>
    public static Dictionary<uint, ushort> SuffixBuffRows()
    {
        var map = new Dictionary<uint, ushort>();
        if (!ClientTables.Has("BuffDefinitions")) return map;
        foreach (var (i, b) in ClientTables.Table("BuffDefinitions").Select((b, i) => (i, b)))
        {
            if (b.Text("Id") is not { } id || !id.StartsWith("flask_bonus_", StringComparison.Ordinal)) continue;
            foreach (int key in b.Ints("StatsKeys").Concat(b.Ints("GrantedFlags")))
                map.TryAdd((uint)key + 1, (ushort)i);
        }
        return map;
    }

    /// <summary>The suffix buffs a use sends: for each "during effect" mod on the flask
    /// (<see cref="DuringEffect"/>) that names a flask_bonus_* row in <paramref name="rows"/>, that row — in
    /// <see cref="DuringEffect"/> order, the official order in the captures' sample rows (one suffix per flask
    /// there, so the order across suffixes on one flask is unmeasured). The immunity flags are in the map too,
    /// through their GrantedFlags (flask_bonus_poison_immunity 1960 grants immune_to_poison); a mod with no
    /// flask_bonus_* row at all sends nothing. This does not filter by <see cref="SupportOf"/>: the buff is the
    /// client's icon for the mod, whether or not the server models its stat (base_mana_cost_-% is unmodelled here).</summary>
    public static IReadOnlyList<ushort> SuffixBuffRowsFor(IReadOnlyDictionary<uint, int> local,
        IReadOnlyDictionary<uint, ushort> rows) =>
        [.. DuringEffect.Where(e => local.GetValueOrDefault(e.Local) != 0 && rows.ContainsKey(e.Stat))
            .Select(e => rows[e.Stat]).Distinct()];

    /// <summary>The "during effect" mods of life, mana and hybrid flasks (Divination Distillate, the suffixes): each use's
    /// stats count while any of that use's own recovery effects still runs (Flasks.Effects), so they also end when the
    /// pools fill and the recovery stops early, at death and when the recovery is discarded. Copies of the same mod do not
    /// stack: the strongest-per-(effect, stat) pass (CombineUtilitySources) counts one.</summary>
    public sealed class RecoveryDuringEffects
    {
        // Changes are rare. Readers can share a published list without nesting this lock with the recovery queue's.
        volatile (IReadOnlyList<Flasks.Effect> Effects, IReadOnlyList<Source> Stats)[] _uses = [];
        readonly Lock _lock = new();

        public void Start(IReadOnlyList<Flasks.Effect> effects, IReadOnlyList<Source> stats)
        {
            if (effects.Count == 0 || stats.Count == 0) return;
            lock (_lock) _uses = [.. _uses, (effects, stats)];
        }

        static bool Running(IReadOnlyList<Flasks.Effect> effects, Flasks.Effects running)
        {
            for (int i = 0; i < effects.Count; i++)
                if (running.Remaining(effects[i]) > 0) return true;
            return false;
        }

        public IEnumerable<Source> Sources(Flasks.Effects running) =>
            [.. _uses.Where(u => Running(u.Effects, running)).SelectMany(u => u.Stats)];

        /// <summary>Whether an effective recovery use currently supplies the requested flag.</summary>
        public bool HasStat(Flasks.Effects running, uint stat, double at)
        {
            foreach (var use in _uses)
                for (int i = 0; i < use.Stats.Count; i++)
                {
                    var source = use.Stats[i];
                    if (source.Stat != stat || source.Value == 0) continue;
                    for (int j = 0; j < use.Effects.Count; j++)
                        if (running.ExpectedEnd(use.Effects[j], at) is double end && at < end) return true;
                    break;
                }
            return false;
        }

        /// <summary>The recovery carriers of a flag, until the caller prunes completed uses.</summary>
        public IEnumerable<Flasks.Effect> EffectsWithStat(uint stat)
        {
            foreach (var use in _uses)
                if (use.Stats.Any(source => source.Stat == stat && source.Value != 0))
                    foreach (var effect in use.Effects) yield return effect;
        }

        /// <summary>Drops the uses whose recovery has ended: whether any were dropped (the stats changed).</summary>
        public bool Prune(Flasks.Effects running)
        {
            var ended = _uses.Where(u => !Running(u.Effects, running)).ToList();
            if (ended.Count == 0) return false;
            lock (_lock) _uses = [.. _uses.Where(u => !ended.Any(e => ReferenceEquals(e.Effects, u.Effects)))];
            return true;
        }
    }

    /// <summary>A value scaled by increased effect as the game rounds it (poewiki: "always rounded down"; Path of Building
    /// ModStore.ScaleAddMod: rounded to two decimals, then the fraction dropped).</summary>
    /// <summary>The Onslaught buff's stats at <paramref name="effect"/>, as sources of that buff: a flask's and a kill's
    /// Onslaught are one effect, so the strongest-per-(effect, stat) pass counts it once.</summary>
    public static IEnumerable<Source> OnslaughtSources(ExtraBuff onslaught, double effect) =>
        onslaught.Stats.Where(s => SupportOf(s.Stat) != Support.Unsupported)
            .Select(s => new Source($"buff {onslaught.BuffRow}", s.Stat, Scale(s.Value, effect)));

    public static int Scale(int value, double effect) =>
        (int)Math.Clamp(Math.Truncate(Math.Round(value * effect, 2)), int.MinValue, int.MaxValue);

    /// <summary>One stat an effect applies: the effect it belongs to (its buff, "buff 121", or a flask mod's stat), the
    /// stat and its value.</summary>
    public readonly record struct Source(string Effect, uint Stat, int Value);

    /// <summary>A utility flask's effect under way: the flask (item id and flask-bar cell), its buffs on the player, what it
    /// applies, and when it started and ends (connection clock seconds, which stop while the game is paused).</summary>
    public sealed record Active(uint ItemId, byte Slot, string Name, Utility Flask, ushort Buff, ushort? ExtraBuff,
        IReadOnlyList<Source> Stats, double Started, double Ends)
    {
        public long Activation { get; set; }
        public int SelfChaosDamageLifePctPerMinute { get; set; }
        public int SelfChaosDamageFlatPerMinute { get; set; }
        public IReadOnlyDictionary<uint, int>? LocalStats { get; init; }
        public double PeriodicThrough { get; set; } = Started;
        public double Ends { get; set; } = Ends;
        public double ExpiryRate { get; set; } = 1;

        /// <summary>The stats this effect applies now. They change only through <see cref="Effects.Retune"/>.</summary>
        public IReadOnlyList<Source> Stats { get; internal set; } = Stats;

        /// <summary>The "during effect" suffix buffs this use sends: (buff instance, BuffDefinitions
        /// row), in send order. Allocated with the effect in UseUtilityFlaskCore, so an effect always carries the
        /// buffs it sent and <see cref="EndBuffs"/> removes exactly those.</summary>
        public IReadOnlyList<(ushort Buff, ushort Row)> SuffixBuffs { get; init; } = [];
    }

    /// <summary>Whether a flask's effect ends when the flask leaves its cell of the flask bar (picked up, swapped, moved).
    /// Not confirmed by any source (poewiki, the Fandom mirror, Path of Building and the forum don't say); a flask works
    /// only from the bar, so here it ends. Flip it if the real game keeps the effect running.</summary>
    public const bool EndsWhenFlaskLeavesItsCell = true;

    /// <summary>Whether the effect's flask is no longer in its cell of the flask bar (<paramref name="bar"/>), so the effect
    /// ends (<see cref="EndsWhenFlaskLeavesItsCell"/>).</summary>
    public static bool LeftItsCell(Active e, IEnumerable<InventoryItem> bar, bool ends = EndsWhenFlaskLeavesItsCell) =>
        ends && !bar.Any(i => i.Id == e.ItemId && i.X == e.Slot);

    /// <summary>The utility flasks running on one connection, at most one per flask. The current area recovery writer
    /// ends them. Legacy ticker tickets remain available to callers of Start/End; activation identities distinguish
    /// a pending End edge from any subsequent accepted use of the same item.</summary>
    public sealed class Effects
    {
        public sealed class DamageProgress
        {
            public double Through = double.NaN;
            public double Pending, TakenCarry, BypassCarry;
        }

        readonly List<Active> _active = [];
        readonly Lock _lock = new();
        int _ticker, _lastTicket;
        long _activation;
        readonly Dictionary<uint, long> _latest = [];
        volatile bool hasActiveEffects;
        double expiryRate = 1;
        public DamageProgress SelfDamage { get; } = new();

        public bool HasSelfDamage
        {
            get { lock (_lock) return _active.Any(a => a.SelfChaosDamageLifePctPerMinute > 0 || a.SelfChaosDamageFlatPerMinute > 0); }
        }

        public IReadOnlyList<Active> SelfDamageEffects()
        {
            lock (_lock) return [.. _active.Where(a => a.SelfChaosDamageLifePctPerMinute > 0 || a.SelfChaosDamageFlatPerMinute > 0)];
        }

        public void SetExpiryRate(double now, double rate)
        {
            lock (_lock)
            {
                rate = Math.Max(.25, rate);
                foreach (var effect in _active)
                {
                    effect.Ends = RecipientEffectTime.RetimedDeadline(effect.Ends, now, expiryRate, rate);
                    effect.ExpiryRate = rate;
                }
                expiryRate = rate;
            }
        }

        public bool IsLatest(uint item, long activation)
        {
            lock (_lock) return _latest.GetValueOrDefault(item) == activation;
        }

        /// <summary>The effects running now, a copy.</summary>
        public IReadOnlyList<Active> All()
        {
            lock (_lock) return [.. _active];
        }

        /// <summary>Gives a running effect new stats and self damage rate (its buff, cell and deadline stay).</summary>
        public void Retune(Active effect, IReadOnlyList<Source> stats, int selfChaosDamageLifePctPerMinute) =>
            Retune(effect, stats, selfChaosDamageLifePctPerMinute, effect.SelfChaosDamageFlatPerMinute);

        public void Retune(Active effect, IReadOnlyList<Source> stats, int selfChaosDamageLifePctPerMinute,
            int selfChaosDamageFlatPerMinute)
        {
            lock (_lock)
            {
                effect.Stats = stats;
                effect.SelfChaosDamageLifePctPerMinute = selfChaosDamageLifePctPerMinute;
                effect.SelfChaosDamageFlatPerMinute = selfChaosDamageFlatPerMinute;
            }
        }

        /// <summary>The effect of this flask (item id) still running, or null.</summary>
        public Active? Of(uint itemId)
        {
            lock (_lock) return _active.FirstOrDefault(a => a.ItemId == itemId);
        }

        public Active? FirstWithLocalStat(uint stat, Func<Active, bool> eligible)
        {
            lock (_lock)
                return _active.FirstOrDefault(effect => effect.LocalStats?.GetValueOrDefault(stat) > 0 && eligible(effect));
        }

        /// <summary>Whether a paid utility effect with this local flag is active at the action's time.</summary>
        public bool HasActiveLocalStat(uint stat, double now)
        {
            if (!hasActiveEffects) return false;
            lock (_lock)
                foreach (var effect in _active)
                    if (effect.Started <= now && now < effect.Ends && effect.LocalStats?.GetValueOrDefault(stat) > 0)
                        return true;
            return false;
        }

        /// <summary>Starts an effect, next to any other, of the same kind or not: each keeps its own duration
        /// (<see cref="Stats"/> applies only the strongest of one kind). Returns the ticket of a ticker to start now, or
        /// null when one runs.</summary>
        public int? Start(Active effect)
        {
            lock (_lock)
            {
                effect.Activation = ++_activation;
                effect.Ends = effect.Started + (effect.Ends - effect.Started) / expiryRate;
                effect.ExpiryRate = expiryRate;
                _latest[effect.ItemId] = effect.Activation;
                _active.RemoveAll(a => a.ItemId == effect.ItemId);
                _active.Add(effect);
                hasActiveEffects = true;
                if (_ticker != 0) return null;
                _ticker = ++_lastTicket;
                return _ticker;
            }
        }

        /// <summary>The effects that ended by <paramref name="now"/>, or whose flask <paramref name="gone"/> says left its
        /// cell (removed), or all of them with <paramref name="all"/> (death). <paramref name="stop"/> tells the ticker to
        /// stop: nothing runs any more.</summary>
        public List<Active> End(double now, bool all, out bool stop, Func<Active, bool>? gone = null)
        {
            lock (_lock)
            {
                List<Active> ended = [.. _active.Where(a => all || a.Ends <= now || gone?.Invoke(a) == true)];
                foreach (var a in ended) _active.Remove(a);
                stop = _active.Count == 0;
                hasActiveEffects = !stop;
                if (stop) _ticker = 0;
                return ended;
            }
        }

        /// <summary>The ticker with this ticket exited (normally or not): unless a newer one runs, the next start starts
        /// one again, so no effect is left running without a ticker to end it.</summary>
        public void TickerExited(int ticket)
        {
            lock (_lock) if (_ticker == ticket) _ticker = 0;
        }

        /// <summary>Whether a ticker is meant to run.</summary>
        public bool Ticking
        {
            get { lock (_lock) return _ticker != 0; }
        }

        /// <summary>What the running effects apply: of the same effect, only the strongest (<see cref="Strongest"/>).</summary>
        public IReadOnlyList<(uint Stat, int Value)> Stats()
        {
            lock (_lock) return Strongest(_active.SelectMany(a => a.Stats));
        }

        /// <summary>The running effects' sources, unreduced: callers that also have other sources of the same effects
        /// (Mageblood's permanent ones) combine them before one strongest-per-(effect, stat) pass.</summary>
        public IReadOnlyList<Source> Sources() => Sources(null);

        public IReadOnlyList<Source> Sources(double? at) => Sources(at, null);

        /// <summary>The sources from each eligible effect, optionally filtered with the paid effect's identity.</summary>
        public IReadOnlyList<Source> Sources(double? at, Func<Active, Source, bool>? include)
        {
            lock (_lock)
            {
                if (_active.Count == 0) return [];
                List<Source> result = [];
                foreach (var effect in _active)
                {
                    if (at is double now && (effect.Started > now || now >= effect.Ends)) continue;
                    foreach (var source in effect.Stats)
                        if (include is null || include(effect, source)) result.Add(source);
                }
                return result;
            }
        }

        /// <summary>The strongest positive value of one stat without allocating a source snapshot.</summary>
        public int MaximumStat(uint stat)
        {
            lock (_lock)
            {
                int value = 0;
                foreach (var effect in _active)
                    foreach (var source in effect.Stats)
                        if (source.Stat == stat) value = Math.Max(value, source.Value);
                return value;
            }
        }

        /// <summary>Whether a running effect shows its own Onslaught buff (a Silver Flask's).</summary>
        public bool ShowsOnslaught
        {
            get { lock (_lock) return _active.Any(a => a.ExtraBuff is not null); }
        }

        /// <summary>Whether any effect runs.</summary>
        public bool Any
        {
            get { lock (_lock) return _active.Count > 0; }
        }

        public bool ActiveAt(double now)
        {
            lock (_lock) return _active.Any(a => a.Started <= now && now < a.Ends);
        }

        public double? NextExpiry(double now)
        {
            lock (_lock)
            {
                double next = double.PositiveInfinity;
                foreach (var a in _active)
                    if (a.Ends > now) next = Math.Min(next, a.Ends);
                return double.IsFinite(next) ? next : null;
            }
        }
    }

    /// <summary>poewiki, Flask, "Effects of flasks": "When creating multiple of the same (non-recovery) flask effect, each
    /// effect has an independent duration, and only the strongest applies"; different effects stack. Path of Building
    /// merges flask buffs the same way (CalcPerform mergeBuff: per effect, the highest value of each stat). So per
    /// (effect, stat) the value with the largest size wins, a bonus over an equal penalty.</summary>
    public static IReadOnlyList<(uint Stat, int Value)> Strongest(IEnumerable<Source> sources) =>
        [.. sources.GroupBy(s => (s.Effect, s.Stat))
            .Select(g => (g.Key.Stat, g.OrderByDescending(s => Math.Abs((long)s.Value)).ThenByDescending(s => s.Value).First().Value))];

    /// <summary>The character's pool bonuses with the running flasks' stats: resistances and their maxima, armour and
    /// evasion (flat, and the Basalt / Stibnite more multipliers on top of any others), movement and attack speed,
    /// critical strike chance and spell suppression.</summary>
    public static CharacterStats.Bonus Apply(CharacterStats.Bonus b, IEnumerable<(uint Stat, int Value)> stats)
    {
        var s = new Dictionary<uint, int>();
        foreach (var (stat, value) in stats) s[stat] = s.GetValueOrDefault(stat) + value;
        if (s.Count == 0) return b;
        int Get(uint name) => s.GetValueOrDefault(name);
        int all = Get(Stat.BaseResistAllElementsPct);
        // Preserve the complete product until the final defence rating is rounded.
        static decimal More(decimal a, int c) => (100m + a) * (100m + c) / 100m - 100m;
        return b with
        {
            FireResistance = b.FireResistance + all + Get(Stat.BaseFireDamageResistancePct),
            ColdResistance = b.ColdResistance + all + Get(Stat.BaseColdDamageResistancePct),
            LightningResistance = b.LightningResistance + all + Get(Stat.BaseLightningDamageResistancePct),
            ChaosResistance = b.ChaosResistance + Get(Stat.BaseChaosDamageResistancePct),
            FireMaxResistance = b.FireMaxResistance + Get(Stat.BaseMaximumFireDamageResistancePct),
            ColdMaxResistance = b.ColdMaxResistance + Get(Stat.BaseMaximumColdDamageResistancePct),
            LightningMaxResistance = b.LightningMaxResistance + Get(Stat.BaseMaximumLightningDamageResistancePct),
            ChaosMaxResistance = b.ChaosMaxResistance + Get(Stat.BaseMaximumChaosDamageResistancePct),
            Armour = b.Armour + Get(Stat.BasePhysicalDamageReductionRating),
            Ward = b.Ward + Get(Stat.BaseWard), WardPct = b.WardPct + Get(Stat.WardPlusPct),
            Evasion = b.Evasion + Get(Stat.BaseEvasionRating),
            ArmourPct = b.ArmourPct + Get(Stat.PhysicalDamageReductionRatingPlusPct),
            EvasionPct = b.EvasionPct + Get(Stat.EvasionRatingPlusPct),
            ArmourMorePct = More(b.ArmourMorePct, Get(Stat.BasaltFlaskArmourPlusPctFinal)),
            EvasionMorePct = More(b.EvasionMorePct, Get(Stat.StibniteFlaskEvasionRatingPlusPctFinal)),
            MovementVelocityPct = b.MovementVelocityPct + Get(Stat.BaseMovementVelocityPlusPct),
            AttackSpeedPct = b.AttackSpeedPct + Get(Stat.AttackSpeedPlusPct),
            CriticalStrikeChancePct = b.CriticalStrikeChancePct + Get(Stat.CriticalStrikeChancePlusPct),
            SpellCriticalStrikeChancePct = b.SpellCriticalStrikeChancePct + Get(Stat.CriticalStrikeChancePlusPct),
            SpellSuppressionChance = b.SpellSuppressionChance + Get(Stat.BaseSpellSuppressionChancePct),
            BlockChance = b.BlockChance + Get(Stat.MonsterBaseBlockPct),
            SpellBlockChance = b.SpellBlockChance + Get(Stat.BaseSpellBlockPct),
        };
    }

    /// <summary>The running flasks' stats that the client and the damage code read as plain stats rather than from the
    /// pools: the maximum resistances (the client adds them to its own 75), increased critical strike chance (gem
    /// tooltips, spell crits), increased damage (Sulphur), cast speed (Onslaught), increased item rarity (Gold: the drops
    /// and the panel read it) and the panel-only stats
    /// (<see cref="Support.PanelOnly"/>), by client stat key.</summary>
    public static IEnumerable<(uint Key, int Value)> RawStats(IEnumerable<(uint Stat, int Value)> stats, StatCatalog catalog)
    {
        var result = new Dictionary<uint, int>();
        foreach (var (stat, value) in stats)
        {
            uint? name = stat switch
            {
                Stat.BaseMaximumFireDamageResistancePct or Stat.BaseMaximumColdDamageResistancePct
                    or Stat.BaseMaximumLightningDamageResistancePct or Stat.BaseMaximumChaosDamageResistancePct
                    or Stat.CriticalStrikeChancePlusPct or Stat.DamagePlusPct or Stat.BaseItemFoundRarityPlusPct => stat,
                // Dying Sun: the skill area and projectile count read these from the merged player stats
                // (SkillArea.Increased, GatewayProjectileModifiers.Resolve); the client predicts its spell
                // projectiles from the same stat.
                Stat.BaseSkillAreaOfEffectPlusPct or Stat.NumberOfAdditionalProjectiles => stat,
                // Oriath's End: KillExplosions.ExplodeOnKill reads it from the merged player stats.
                Stat.ExplodeEnemiesFor10PctLifeAsRandomElementOnKillChancePct => stat,
                Stat.BaseItemFoundQuantityPlusPct => stat,   // ItemQuantityFactor reads the merged player stats
                // Cinderswallow Urn: KillRecovery.From reads these from the merged player stats.
                Stat.RecoverPctMaximumLifeOnKill or Stat.RecoverPctMaximumManaOnKill or Stat.RecoverEnergyShieldPctOnKill => stat,
                // Stormblood: the hit's ailments read these from the merged player stats.
                Stat.AllDamageFromCriticalStrikesCanShock or Stat.AllDamageFromCriticalStrikesCanChill
                    or Stat.AllDamageFromCriticalStrikesCanFreeze => stat,
                Stat.BaseCastSpeedPlusPct => Stat.CastSpeedPlusPct,
                // Onslaught's attack speed is PoB's Speed INC (Attack): the attack-speed stat itself, not only the
                // pools' timing bucket. The pools already add it through Apply, and PassiveBonus reads the passives'
                // own sources, so the stat does not count it twice.
                Stat.AttackSpeedPlusPct => Stat.AttackSpeedPlusPct,
                Stat.LifeRegenerationRatePerMinutePct => stat,   // consecrated ground (FlaskGrounds)
                // Quartz: the client's Pathfinding reads virtual_phase_through_objects (0x141ea3618: +0x52e "Phase Through
                // Objects", which picks its blocker filter, 0x141e81e6c).
                Stat.PhaseThroughObjects => Stat.VirtualPhaseThroughObjects,
                // The flask's reduced skill mana cost: the finished cost reads it
                // off the character's stats, and the running flasks reach those only here.
                Stat.BaseManaCostMinusPct => stat,
                // Recovery flasks supply this flag only while their paid effect is active.
                Stat.EnergyShieldRechargeNotDelayedByDamage => stat,
                _ when ActorScale.IsInput(stat) || PanelOnly.Contains(stat) || ElementalAilmentInputs.Contains(stat)
                    || HandedFlag(stat) => stat,
                _ => null,
            };
            if (name is uint key)
                result[key] = ActorScale.Add(key, result.GetValueOrDefault(key), value);
        }
        return result.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>Whether the running flasks make the player phase (Quartz Flask: phase_through_objects): monsters do not
    /// block the walk (Player.Blockers), as the client's pathfinder with its phasing flag. NPCs and stashes still do
    /// (which objects the client's phasing filter lets through is not traced).</summary>
    public static bool Phasing(IEnumerable<(uint Stat, int Value)> stats) => stats.Any(s => s.Stat == Stat.PhaseThroughObjects && s.Value > 0);

    /// <summary>Ward bonuses use the authoritative Ward pool (base_ward and ward_+%).</summary>
    public static (int Flat, int Increased) Ward(IEnumerable<(uint Stat, int Value)> stats) =>
        (stats.Where(s => s.Stat == Stat.BaseWard).Sum(s => s.Value), stats.Where(s => s.Stat == Stat.WardPlusPct).Sum(s => s.Value));

    // Call only after the normal flask-use admission/payment and effect start, never from a stat projection.
    public static bool RestoreWardAfterPaidUse(WardState ward, Func<uint, int> local, double now)
    {
        if (local(Stat.LocalFlaskRestoreWard) <= 0) return false;
        ward.Advance(now);
        bool wasBroken = ward.Broken;
        ward.Restore();
        return wasBroken;
    }

    /// <summary>Adds the running flasks' <see cref="RawStats"/> to the character's stats (Program.CharacterPlayerStats).</summary>
    public static void AddRawStats(StatMap stats, IEnumerable<(uint Stat, int Value)> running, StatCatalog catalog)
    {
        foreach (var (k, v) in RawStats(running, catalog)) stats[k] = ActorScale.Add(k, stats.GetValueOrDefault(k), v);
    }

    /// <summary>Why a use is refused, or null: dead,
    /// or fewer charges than a use needs.</summary>
    public static string? Refusal(bool dead, Active? running, Item flask, Flasks.Flask charges, double now) =>
        dead ? "dead"
        : Flasks.Spend(flask, charges) is null ? $"{flask.Charges}/{charges.MaxCharges} charges, a use needs {charges.ChargesPerUse}"
        : null;

    /// <summary>Starts an effect and recomputes the character (<paramref name="recompute"/>: the pools and the walk
    /// speed) in the area's frame, which every pools writer takes, so no writer keeps a stale total.</summary>
    public static int? Start(Player player, Active effect, Action recompute)
    {
        var started = player.UtilityFlaskEffects.Start(effect);
        recompute();
        return started;
    }

    /// <summary>Ends the effects that are due (<see cref="Effects.End"/>) and recomputes the character in the same locked
    /// block, when any ended.</summary>
    public static List<Active> EndDue(Player player, double now, bool dead, Func<Active, bool>? gone, Action recompute, out bool stop)
    {
        var ended = player.UtilityFlaskEffects.End(now, dead, out stop, gone);
        if (ended.Count > 0) recompute();
        return ended;
    }

    /// <summary>S→C 0x1c1 for an effect: the flask's buff (<see cref="Flasks.Buff"/>: duration and flask-bar cell),
    /// the extra buff (Onslaught: same duration, no cell, so it shows in the buff row with its countdown), and the
    /// "during effect" suffix buffs: one 0x1c1 per suffix mod, on the same flask-bar cell with the same duration as
    /// the flask's own buff — the official sends one per suffix on every use with a flask buff, so the client draws
    /// one icon per suffix instead of one less.</summary>
    public static void ShowBuffs(Active e, Player player) => ShowBuffs(e, player, null);

    public static void ShowBuffs(Active e, Player player, ushort? visual)
    {
        double seconds = (e.Ends - e.Started) * e.ExpiryRate;
        player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, e.Buff, e.Flask.BuffRow, e.Slot, seconds, values: null, visual: visual));
        if (e.Flask.Extra is { } x && e.ExtraBuff is ushort extra)
            player.Area.Buffs.Set(player.Id, new(x.BuffRow, player.Id, U68: global::Buff.Seconds(seconds), Tag: extra));
        foreach (var (buff, row) in e.SuffixBuffs)
            player.Area.Buffs.Set(player.Id, Flasks.Buff(player.Id, buff, row, e.Slot, seconds));
    }

    /// <summary>An effect that ends: its buff, the extra buff, and the suffix buffs.</summary>
    public static void EndBuffs(Active e, Player player)
    {
        player.Area.Buffs.Clear(player.Id, e.Flask.BuffRow, player.Id, tag: e.Buff);
        if (e.Flask.Extra is { } x && e.ExtraBuff is ushort extra) player.Area.Buffs.Clear(player.Id, x.BuffRow, player.Id, tag: extra);
        foreach (var (buff, row) in e.SuffixBuffs) player.Area.Buffs.Clear(player.Id, row, player.Id, tag: buff);
    }
}
