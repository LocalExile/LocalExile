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
    public sealed record Utility(int BaseRow, ushort BuffRow, string BuffId, IReadOnlyList<(string Stat, int Value)> Stats,
        double Seconds, int ChargesPerUse, int MaxCharges, ExtraBuff? Extra = null)
    {
        /// <summary>The charges as <see cref="Flasks"/> spends and refills them.</summary>
        public Flasks.Flask Charges => new(0, 0, Seconds, ChargesPerUse, MaxCharges);

        /// <summary>The stats this flask's buffs grant (its own and the extra buff's) that a use carries: all but the
        /// unsupported ones (<see cref="SupportOf"/>).</summary>
        public IEnumerable<(string Stat, int Value)> Applied =>
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
    static readonly IReadOnlySet<string> PanelOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "stun_threshold_+%",
        "base_avoid_stun_%",
        "base_stun_recovery_+%",
        "curse_effect_on_self_+%", "chance_to_freeze_shock_ignite_%",
        "immune_to_poison", "immune_to_bleeding",
        "light_radius_+%",   // the client draws the light radius from the stat it gets
    };

    static readonly IReadOnlySet<string> ElementalAilmentInputs = new HashSet<string>(StringComparer.Ordinal)
    {
        "base_frozen_effect_on_self_+%", "base_avoid_chill_%", "base_avoid_freeze_%", "base_avoid_ignite_%",
        "base_avoid_shock_%", "chill_effectiveness_on_self_+%", "base_self_freeze_duration_-%", "shocked_effect_on_self_+%",
        "base_immune_to_freeze", "base_immune_to_chill", "base_immune_to_shock", "base_immune_to_ignite",
    };

    /// <summary>How a flask effect's stat counts here.</summary>
    public static Support SupportOf(string stat) =>
        ActorScale.IsInput(stat) || Modelled.Contains(stat) || ElementalAilmentInputs.Contains(stat) ? Support.Modelled
        : PanelOnly.Contains(stat) ? Support.PanelOnly
        : stat is "base_ward" or "ward_+%" ? Support.Ward
        : Support.Unsupported;

    /// <summary>A flask's "during effect" mods (suffixes, utility flasks only in 3.29): the flask's local stat, the
    /// character stat it grants while the effect runs, its sign, and whether it is a flag (not scaled by increased effect).
    /// Each is its own effect (poewiki: "different suffixes … stack"; two flasks with the same suffix: the strongest).</summary>
    public static readonly IReadOnlyList<(string Local, string Stat, int Sign, bool Flag)> DuringEffect =
    [
        ("local_flask_armour_+%_while_healing", "physical_damage_reduction_rating_+%", 1, false),
        ("local_flask_evasion_+%_while_healing", "evasion_rating_+%", 1, false),
        ("local_flask_movement_speed_+%_while_healing", "base_movement_velocity_+%", 1, false),
        ("local_flask_resistances_+%_while_healing", "base_resist_all_elements_%", 1, false),
        ("local_flask_critical_strike_chance_+%_during_flask_effect", "critical_strike_chance_+%", 1, false),
        ("local_flask_ward_+%_during_effect", "ward_+%", 1, false),
        ("local_flask_stun_recovery_+%_while_healing", "base_stun_recovery_+%", 1, false),
        ("local_flask_avoid_stun_chance_%_during_flask_effect", "base_avoid_stun_%", 1, false),
        ("local_avoid_chill_%_during_flask_effect", "base_avoid_chill_%", 1, false),
        ("local_avoid_freeze_%_during_flask_effect", "base_avoid_freeze_%", 1, false),
        ("local_avoid_ignite_%_during_flask_effect", "base_avoid_ignite_%", 1, false),
        ("local_avoid_shock_%_during_flask_effect", "base_avoid_shock_%", 1, false),
        ("local_self_chill_effect_+%_during_flask_effect", "chill_effectiveness_on_self_+%", 1, false),
        ("local_self_freeze_duration_+%_during_flask_effect", "base_self_freeze_duration_-%", -1, false),
        ("local_self_shock_effect_+%_during_flask_effect", "shocked_effect_on_self_+%", 1, false),
        ("local_self_curse_effect_+%_during_flask_effect", "curse_effect_on_self_+%", 1, false),
        ("local_flask_chance_to_freeze_shock_ignite_%_while_healing", "chance_to_freeze_shock_ignite_%", 1, false),
        ("local_flask_immune_to_freeze_and_chill_during_flask_effect", "base_immune_to_freeze", 1, true),
        ("local_flask_immune_to_freeze_and_chill_during_flask_effect", "base_immune_to_chill", 1, true),
        ("local_flask_immune_to_shock_during_flask_effect", "base_immune_to_shock", 1, true),
        ("local_flask_dispels_burning_and_ignite_immunity_during_effect", "base_immune_to_ignite", 1, true),
        ("local_flask_immune_to_poison_during_flask_effect", "immune_to_poison", 1, true),
        ("local_flask_immune_to_bleeding_and_corrupted_blood_during_flask_effect", "immune_to_bleeding", 1, true),
        // Dying Sun: "(10-20)% increased Area of Effect during Effect", "Skills fire 2 additional Projectiles during
        // Effect". Both scale with increased flask effect, rounded down (2 x 1.5 = 3 projectiles).
        ("local_flask_area_of_effect_+%_during_flask_effect", "base_skill_area_of_effect_+%", 1, false),
        ("local_flask_number_of_additional_projectiles_during_flask_effect", "number_of_additional_projectiles", 1, false),
        // Oriath's End: the kill explosion's chance (KillExplosions), scaled by flask effect.
        ("local_unique_flask_explode_enemies_for_10%_life_as_random_element_on_kill_chance_%_during_flask_effect",
            "explode_enemies_for_10%_life_as_random_element_on_kill_chance_%", 1, false),
        // Divination Distillate (a hybrid flask: RecoveryDuringEffects keeps these while its recovery runs).
        ("local_unique_flask_item_rarity_+%_while_healing", "base_item_found_rarity_+%", 1, false),
        ("local_unique_flask_item_quantity_+%_while_healing", "base_item_found_quantity_+%", 1, false),
        ("local_unique_flask_light_radius_+%_while_healing", "light_radius_+%", 1, false),
        ("local_unique_flask_resist_all_elements_%_during_flask_effect", "base_resist_all_elements_%", 1, false),
        // Bottled Faith: increased critical strike chance against enemies on consecrated ground (Game.CriticalChanceOn),
        // and the increased damage taken that consecrated ground created during the effect applies (FlaskGrounds).
        ("local_unique_flask_critical_strike_chance_+%_vs_enemies_on_consecrated_ground_during_flask_effect",
            FlaskGrounds.VsConsecratedCriticalStat, 1, false),
        ("local_flask_during_effect_enemy_damage_taken_+%_on_consecrated_ground", FlaskGrounds.EnemyDamageTakenStat, 1, false),
        // Cinderswallow Urn: recovery on kill (KillRecovery.From reads the merged player stats), and the increased damage
        // taken retained by each ignite applied during the effect (MonsterIgnites).
        ("local_flask_recover_%_maximum_life_on_kill_during_flask_effect", "recover_%_maximum_life_on_kill", 1, false),
        ("local_flask_recover_%_maximum_mana_on_kill_during_flask_effect", "recover_%_maximum_mana_on_kill", 1, false),
        ("local_flask_recover_%_maximum_energy_shield_on_kill_during_flask_effect", "recover_energy_shield_%_on_kill", 1, false),
        ("local_flask_enemies_ignited_during_flask_effect_damage_taken_+%", IgnitedDuringEffectTakenStat, 1, false),
        // Rumi's Concoction and its Replica: additional chance to block attack and spell damage (the buff's own stats,
        // BuffDefinitions unique_flask_rumis_concoction), added to the pools' block before its increases (Apply).
        ("local_unique_flask_block_%_while_healing", "monster_base_block_%", 1, false),
        ("local_unique_flask_spell_block_%_while_healing", "base_spell_block_%", 1, false),
        // Stormblood (and its cold sibling): all damage from critical strikes can shock (chill, freeze) during the effect
        // (MonsterElementalAilments.Inflicting). Sap and Brittle are not modelled.
        ("local_unique_flask_all_damage_from_critical_strikes_can_shock_during_effect", "all_damage_from_critical_strikes_can_shock", 1, true),
        ("local_unique_flask_all_damage_from_critical_strikes_can_chill_during_effect", "all_damage_from_critical_strikes_can_chill", 1, true),
        ("local_unique_flask_all_damage_from_critical_strikes_can_freeze_during_effect", "all_damage_from_critical_strikes_can_freeze", 1, true),
    ];

    /// <summary>A buff a flask grants besides its own (BuffDefinitions row, with its stats).</summary>
    public sealed record ExtraBuff(ushort BuffRow, string BuffId, IReadOnlyList<(string Stat, int Value)> Stats);

    /// <summary>The buff stats the server applies to the character, and where: the pools (<see cref="Apply"/>) or the raw
    /// stats (<see cref="RawStats"/>), or both.</summary>
    public static readonly IReadOnlySet<string> Modelled = new HashSet<string>(StringComparer.Ordinal)
    {
        "base_fire_damage_resistance_%", "base_cold_damage_resistance_%", "base_lightning_damage_resistance_%",
        "base_chaos_damage_resistance_%", "base_resist_all_elements_%",
        "base_maximum_fire_damage_resistance_%", "base_maximum_cold_damage_resistance_%",
        "base_maximum_lightning_damage_resistance_%", "base_maximum_chaos_damage_resistance_%",
        "base_physical_damage_reduction_rating", "base_evasion_rating",
        "basalt_flask_armour_+%_final", "stibnite_flask_evasion_rating_+%_final",
        "base_movement_velocity_+%", "attack_speed_+%", "base_cast_speed_+%",
        "critical_strike_chance_+%", "base_spell_suppression_chance_%", "damage_+%",
        "physical_damage_reduction_rating_+%", "evasion_rating_+%", "base_item_found_rarity_+%", "phase_through_objects",
        "base_skill_area_of_effect_+%", "number_of_additional_projectiles",
        "explode_enemies_for_10%_life_as_random_element_on_kill_chance_%",
        "base_item_found_quantity_+%",   // ItemQuantityFactor
        FlaskGrounds.VsConsecratedCriticalStat, FlaskGrounds.EnemyDamageTakenStat,   // Bottled Faith
        "recover_%_maximum_life_on_kill", "recover_%_maximum_mana_on_kill", "recover_energy_shield_%_on_kill",
        IgnitedDuringEffectTakenStat,   // Cinderswallow Urn
        "monster_base_block_%", "base_spell_block_%",   // Rumi's Concoction (Apply)
        "all_damage_from_critical_strikes_can_shock", "all_damage_from_critical_strikes_can_chill",
        "all_damage_from_critical_strikes_can_freeze",   // Stormblood
    };

    /// <summary>The flask buff flag that stands for Onslaught (the Silver Flask's flask_utility_haste has no stats of its
    /// own), and the BuffTemplates row with the player's Onslaught values (20% attack, cast and movement speed).</summary>
    const string OnslaughtFlag = "silver_flask_display_onslaught", OnslaughtBuff = "onslaught", OnslaughtTemplate = "MapModPlayerOnslaught";

    /// <summary>The utility flasks by BaseItemTypes row, from the client tables Flasks, BuffDefinitions, BuffTemplates,
    /// ComponentCharges and Stats: every one, whatever its stats do here (<see cref="SupportOf"/>).</summary>
    public static Dictionary<int, Utility> Load(string folder, IReadOnlyList<ItemCatalog.BaseType> rows, bool hardMode = false)
    {
        var result = new Dictionary<int, Utility>();
        if (new[] { "Flasks", "BuffDefinitions", "BuffTemplates", "ComponentCharges", "Stats" }.Any(t => !ClientTables.Has(t))) return result;
        var stats = new StatCatalog();
        string StatName(int key) => stats.Name((uint)key + 1);

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
                        [.. buffRows[onslaught].Ints("StatsKeys").Select(StatName)
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
            List<(string, int)> values = [.. buff.Ints("StatsKeys").Select(StatName)
                .Zip(f.Ints(hardMode && !f.IsNull("BuffStatValues2") ? "BuffStatValues2" : "BuffStatValues"))];
            bool grantsOnslaught = buff.Ints("GrantedFlags").Any(s => StatName(s) == OnslaughtFlag);
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
        IReadOnlyDictionary<string, int> local, Func<string, int> character, int playerLevel = 1)
    {
        int Get(string name) => local.GetValueOrDefault(name);
        double durationInc = PanelStats.FlaskDurationIncrease(character, playerLevel);
        // PoB Item.lua2421-2447 applies local duration/quality first; global FlaskDuration multiplies that result.
        double seconds = u.Seconds * (100.0 + Get("local_flask_duration_+%")) / 100.0
            * (100.0 + durationInc) / 100.0 * (100.0 + flask.Quality) / 100.0
            * (100.0 + Get("local_flask_duration_+%_final")) / 100.0;
        double effect = FlaskEffectScaling.Multiplier(character, playerLevel, Get("local_flask_effect_+%"),
            flask.Rarity == ItemRarity.Magic ? character("magic_utility_flask_effect_+%") : 0);
        bool Applies(string stat) => SupportOf(stat) != Support.Unsupported;
        List<Source> stats = [.. u.Stats.Where(s => Applies(s.Stat))
            .Select(s => new Source($"buff {u.BuffRow}", s.Stat, s.Stat == "phase_through_objects" ? s.Value
                : ActorScale.IsInput(s.Stat)
                    ? (int)Math.Clamp(Math.Truncate(Math.Round(s.Value * effect, 2)), int.MinValue, int.MaxValue)
                    : Scale(s.Value, effect)))];
        // A flask's Onslaught scales with the flask's effect modifiers (wiki Silver Flask, Onslaught: since 3.20 Onslaught is
        // a buff and flask effect applies when a flask grants it), added to the character's increased Onslaught effect.
        double onslaughtEffect = effect + character("onslaught_effect_+%") / 100.0;
        if (u.Extra is { } x) stats.AddRange(OnslaughtSources(x, onslaughtEffect));
        // The flask's "during effect" mods, scaled by increased effect too (poewiki, Quality: increased flask effect
        // improves "any of the mods that the flask has rolled"); flags stay as they are.
        stats.AddRange(DuringEffectSources(local, effect));
        return (stats, Math.Max(0.1, seconds));
    }

    /// <summary>Cinderswallow Urn's "Enemies Ignited by you during Effect take X% increased Damage", as a running
    /// effect's stat (no client stat carries it; the server reads it from the running flasks).</summary>
    public const string IgnitedDuringEffectTakenStat = "local_flask_enemies_ignited_during_flask_effect_damage_taken_+%";

    /// <summary>A flask's "during effect" mods at <paramref name="effect"/> (its increased effect): utility flasks through
    /// <see cref="For"/>, life, mana and hybrid flasks through <see cref="RecoveryDuringEffects"/>.</summary>
    public static List<Source> DuringEffectSources(IReadOnlyDictionary<string, int> local, double effect)
    {
        List<Source> stats = [];
        foreach (var (localStat, stat, sign, flag) in DuringEffect)
            if (local.GetValueOrDefault(localStat) is int value and not 0 && SupportOf(stat) != Support.Unsupported)
                stats.Add(new Source($"mod {stat}", stat, sign * (flag ? 1 : Scale(value, effect))));
        return stats;
    }

    /// <summary>The "during effect" mods of life, mana and hybrid flasks (Divination Distillate, the suffixes): each use's
    /// stats count while any of that use's own recovery effects still runs (Flasks.Effects), so they also end when the
    /// pools fill and the recovery stops early, at death and when the recovery is discarded. Copies of the same mod do not
    /// stack: the strongest-per-(effect, stat) pass (CombineUtilitySources) counts one.</summary>
    public sealed class RecoveryDuringEffects
    {
        readonly List<(IReadOnlyList<Flasks.Effect> Effects, IReadOnlyList<Source> Stats)> _uses = [];
        readonly Lock _lock = new();

        public void Start(IReadOnlyList<Flasks.Effect> effects, IReadOnlyList<Source> stats)
        {
            if (effects.Count == 0 || stats.Count == 0) return;
            lock (_lock) _uses.Add((effects, stats));
        }

        (IReadOnlyList<Flasks.Effect> Effects, IReadOnlyList<Source> Stats)[] Snapshot()
        {
            lock (_lock) return [.. _uses];
        }

        static bool Running(IReadOnlyList<Flasks.Effect> effects, Flasks.Effects running) =>
            effects.Any(e => running.Remaining(e) > 0);

        public IEnumerable<Source> Sources(Flasks.Effects running) =>
            [.. Snapshot().Where(u => Running(u.Effects, running)).SelectMany(u => u.Stats)];

        /// <summary>Drops the uses whose recovery has ended: whether any were dropped (the stats changed).</summary>
        public bool Prune(Flasks.Effects running)
        {
            var ended = Snapshot().Where(u => !Running(u.Effects, running)).ToList();
            if (ended.Count == 0) return false;
            lock (_lock) _uses.RemoveAll(u => ended.Any(e => ReferenceEquals(e.Effects, u.Effects)));
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
    public readonly record struct Source(string Effect, string Stat, int Value);

    /// <summary>A utility flask's effect under way: the flask (item id and flask-bar cell), its buffs on the player, what it
    /// applies, and when it started and ends (connection clock seconds, which stop while the game is paused).</summary>
    public sealed record Active(uint ItemId, byte Slot, string Name, Utility Flask, ushort Buff, ushort? ExtraBuff,
        IReadOnlyList<Source> Stats, double Started, double Ends)
    {
        public long Activation { get; set; }
        public double Ends { get; set; } = Ends;
        public double ExpiryRate { get; set; } = 1;
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
        readonly List<Active> _active = [];
        readonly Lock _lock = new();
        int _ticker, _lastTicket;
        long _activation;
        readonly Dictionary<uint, long> _latest = [];
        double expiryRate = 1;
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

        /// <summary>The effect of this flask (item id) still running, or null.</summary>
        public Active? Of(uint itemId)
        {
            lock (_lock) return _active.FirstOrDefault(a => a.ItemId == itemId);
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
        public IReadOnlyList<(string Stat, int Value)> Stats()
        {
            lock (_lock) return Strongest(_active.SelectMany(a => a.Stats));
        }

        /// <summary>The running effects' sources, unreduced: callers that also have other sources of the same effects
        /// (Mageblood's permanent ones) combine them before one strongest-per-(effect, stat) pass.</summary>
        public IReadOnlyList<Source> Sources()
        {
            lock (_lock) return [.. _active.SelectMany(a => a.Stats)];
        }

        /// <summary>The strongest positive value of one stat without allocating a source snapshot.</summary>
        public int MaximumStat(string stat)
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
    }

    /// <summary>poewiki, Flask, "Effects of flasks": "When creating multiple of the same (non-recovery) flask effect, each
    /// effect has an independent duration, and only the strongest applies"; different effects stack. Path of Building
    /// merges flask buffs the same way (CalcPerform mergeBuff: per effect, the highest value of each stat). So per
    /// (effect, stat) the value with the largest size wins, a bonus over an equal penalty.</summary>
    public static IReadOnlyList<(string Stat, int Value)> Strongest(IEnumerable<Source> sources) =>
        [.. sources.GroupBy(s => (s.Effect, s.Stat))
            .Select(g => (g.Key.Stat, g.OrderByDescending(s => Math.Abs((long)s.Value)).ThenByDescending(s => s.Value).First().Value))];

    /// <summary>The character's pool bonuses with the running flasks' stats: resistances and their maxima, armour and
    /// evasion (flat, and the Basalt / Stibnite more multipliers on top of any others), movement and attack speed,
    /// critical strike chance and spell suppression.</summary>
    public static CharacterStats.Bonus Apply(CharacterStats.Bonus b, IEnumerable<(string Stat, int Value)> stats)
    {
        var s = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (stat, value) in stats) s[stat] = s.GetValueOrDefault(stat) + value;
        if (s.Count == 0) return b;
        int Get(string name) => s.GetValueOrDefault(name);
        int all = Get("base_resist_all_elements_%");
        // Preserve the complete product until the final defence rating is rounded.
        static decimal More(decimal a, int c) => (100m + a) * (100m + c) / 100m - 100m;
        return b with
        {
            FireResistance = b.FireResistance + all + Get("base_fire_damage_resistance_%"),
            ColdResistance = b.ColdResistance + all + Get("base_cold_damage_resistance_%"),
            LightningResistance = b.LightningResistance + all + Get("base_lightning_damage_resistance_%"),
            ChaosResistance = b.ChaosResistance + Get("base_chaos_damage_resistance_%"),
            FireMaxResistance = b.FireMaxResistance + Get("base_maximum_fire_damage_resistance_%"),
            ColdMaxResistance = b.ColdMaxResistance + Get("base_maximum_cold_damage_resistance_%"),
            LightningMaxResistance = b.LightningMaxResistance + Get("base_maximum_lightning_damage_resistance_%"),
            ChaosMaxResistance = b.ChaosMaxResistance + Get("base_maximum_chaos_damage_resistance_%"),
            Armour = b.Armour + Get("base_physical_damage_reduction_rating"),
            Ward = b.Ward + Get("base_ward"), WardPct = b.WardPct + Get("ward_+%"),
            Evasion = b.Evasion + Get("base_evasion_rating"),
            ArmourPct = b.ArmourPct + Get("physical_damage_reduction_rating_+%"),
            EvasionPct = b.EvasionPct + Get("evasion_rating_+%"),
            ArmourMorePct = More(b.ArmourMorePct, Get("basalt_flask_armour_+%_final")),
            EvasionMorePct = More(b.EvasionMorePct, Get("stibnite_flask_evasion_rating_+%_final")),
            MovementVelocityPct = b.MovementVelocityPct + Get("base_movement_velocity_+%"),
            AttackSpeedPct = b.AttackSpeedPct + Get("attack_speed_+%"),
            CriticalStrikeChancePct = b.CriticalStrikeChancePct + Get("critical_strike_chance_+%"),
            SpellCriticalStrikeChancePct = b.SpellCriticalStrikeChancePct + Get("critical_strike_chance_+%"),
            SpellSuppressionChance = b.SpellSuppressionChance + Get("base_spell_suppression_chance_%"),
            BlockChance = b.BlockChance + Get("monster_base_block_%"),
            SpellBlockChance = b.SpellBlockChance + Get("base_spell_block_%"),
        };
    }

    /// <summary>The running flasks' stats that the client and the damage code read as plain stats rather than from the
    /// pools: the maximum resistances (the client adds them to its own 75), increased critical strike chance (gem
    /// tooltips, spell crits), increased damage (Sulphur), cast speed (Onslaught), increased item rarity (Gold: the drops
    /// and the panel read it) and the panel-only stats
    /// (<see cref="Support.PanelOnly"/>), by client stat key.</summary>
    public static IEnumerable<(uint Key, int Value)> RawStats(IEnumerable<(string Stat, int Value)> stats, StatCatalog catalog)
    {
        var result = new Dictionary<uint, int>();
        foreach (var (stat, value) in stats)
        {
            string? name = stat switch
            {
                "base_maximum_fire_damage_resistance_%" or "base_maximum_cold_damage_resistance_%"
                    or "base_maximum_lightning_damage_resistance_%" or "base_maximum_chaos_damage_resistance_%"
                    or "critical_strike_chance_+%" or "damage_+%" or "base_item_found_rarity_+%" => stat,
                // Dying Sun: the skill area and projectile count read these from the merged player stats
                // (SkillArea.Increased, GatewayProjectileModifiers.Resolve); the client predicts its spell
                // projectiles from the same stat.
                "base_skill_area_of_effect_+%" or "number_of_additional_projectiles" => stat,
                // Oriath's End: KillExplosions.ExplodeOnKill reads it from the merged player stats.
                "explode_enemies_for_10%_life_as_random_element_on_kill_chance_%" => stat,
                "base_item_found_quantity_+%" => stat,   // ItemQuantityFactor reads the merged player stats
                // Cinderswallow Urn: KillRecovery.From reads these from the merged player stats.
                "recover_%_maximum_life_on_kill" or "recover_%_maximum_mana_on_kill" or "recover_energy_shield_%_on_kill" => stat,
                // Stormblood: the hit's ailments read these from the merged player stats.
                "all_damage_from_critical_strikes_can_shock" or "all_damage_from_critical_strikes_can_chill"
                    or "all_damage_from_critical_strikes_can_freeze" => stat,
                "base_cast_speed_+%" => "cast_speed_+%",
                // Onslaught's attack speed is PoB's Speed INC (Attack): the attack-speed stat itself, not only the
                // pools' timing bucket. The pools already add it through Apply, and PassiveBonus reads the passives'
                // own sources, so the stat does not count it twice.
                "attack_speed_+%" => "attack_speed_+%",
                "life_regeneration_rate_per_minute_%" => stat,   // consecrated ground (FlaskGrounds)
                // Quartz: the client's Pathfinding reads virtual_phase_through_objects (0x141ea3618: +0x52e "Phase Through
                // Objects", which picks its blocker filter, 0x141e81e6c).
                "phase_through_objects" => "virtual_phase_through_objects",
                _ when ActorScale.IsInput(stat) || PanelOnly.Contains(stat) || ElementalAilmentInputs.Contains(stat) => stat,
                _ => null,
            };
            if (name is not null && catalog.Key(name) is uint key)
                result[key] = ActorScale.Add(key, result.GetValueOrDefault(key), value);
        }
        return result.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>Whether the running flasks make the player phase (Quartz Flask: phase_through_objects): monsters do not
    /// block the walk (ClientState.Blockers), as the client's pathfinder with its phasing flag. NPCs and stashes still do
    /// (which objects the client's phasing filter lets through is not traced).</summary>
    public static bool Phasing(IEnumerable<(string Stat, int Value)> stats) => stats.Any(s => s.Stat == "phase_through_objects" && s.Value > 0);

    /// <summary>Ward bonuses use the authoritative Ward pool (base_ward and ward_+%).</summary>
    public static (int Flat, int Increased) Ward(IEnumerable<(string Stat, int Value)> stats) =>
        (stats.Where(s => s.Stat == "base_ward").Sum(s => s.Value), stats.Where(s => s.Stat == "ward_+%").Sum(s => s.Value));

    // Call only after the normal flask-use admission/payment and effect start, never from a stat projection.
    public static bool RestoreWardAfterPaidUse(WardState ward, Func<string, int> local, double now)
    {
        if (local("local_flask_restore_ward") <= 0) return false;
        ward.Advance(now);
        bool wasBroken = ward.Broken;
        ward.Restore();
        return wasBroken;
    }

    /// <summary>Adds the running flasks' <see cref="RawStats"/> to the character's stats (Program.CharacterPlayerStats).</summary>
    public static void AddRawStats(Dictionary<uint, int> stats, IEnumerable<(string Stat, int Value)> running, StatCatalog catalog)
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
    /// speed) under the connection's Monsters lock, which every pools writer takes, so no writer keeps a stale total.</summary>
    public static int? Start(ClientState conn, Active effect, Action recompute)
    {
        lock (conn.Monsters)
        {
            var started = conn.UtilityFlaskEffects.Start(effect);
            recompute();
            return started;
        }
    }

    /// <summary>Ends the effects that are due (<see cref="Effects.End"/>) and recomputes the character in the same locked
    /// block, when any ended.</summary>
    public static List<Active> EndDue(ClientState conn, double now, bool dead, Func<Active, bool>? gone, Action recompute, out bool stop)
    {
        lock (conn.Monsters)
        {
            var ended = conn.UtilityFlaskEffects.End(now, dead, out stop, gone);
            if (ended.Count > 0) recompute();
            return ended;
        }
    }

    /// <summary>S→C 0x1c1 for an effect: the flask's buff (<see cref="Flasks.Buff"/>: duration and flask-bar cell) and the
    /// extra buff (Onslaught: same duration, no cell, so it shows in the buff row with its countdown).</summary>
    public static IEnumerable<byte[]> StartPackets(Active e, uint player)
    {
        yield return Flasks.Buff(player, e.Buff, e.Flask.BuffRow, e.Slot, (e.Ends - e.Started) * e.ExpiryRate);
        if (e.Flask.Extra is { } x && e.ExtraBuff is ushort extra)
            yield return InstanceEntities.AddBuff(player, extra, x.BuffRow, player, u68: BitConverter.SingleToUInt32Bits((float)((e.Ends - e.Started) * e.ExpiryRate)));
    }

    /// <summary>S→C 0x1c2 for an effect that ends: its buff and the extra buff.</summary>
    public static IEnumerable<byte[]> EndPackets(Active e, uint player)
    {
        yield return InstanceEntities.RemoveBuff(player, e.Buff);
        if (e.ExtraBuff is ushort extra) yield return InstanceEntities.RemoveBuff(player, extra);
    }
}
