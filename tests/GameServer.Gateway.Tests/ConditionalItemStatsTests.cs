using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GameServer.Gateway.Tests;


/// <summary>
/// Conditional item mods, issue #504, against the real 3.29.3b tables. <see cref="StatConditions"/> decides whether a
/// stat id's condition holds; a condition the server cannot answer stays unmodelled and keeps its previous behaviour.
/// A condition that does hold moves the value onto the stat's plain name, which is what the pools and panels already
/// read — without that the condition would be decided correctly and the mod would still do nothing.
/// </summary>
public partial class ConditionalItemStatsTests
{
    [Fact]
    public void Conditional_operators_resolve_real_item_mods_and_recent_event_lifecycles()
    {
        Incoming_damage_controls_run_through_the_shared_production_owner();
        Conditional_flask_uses_remove_and_protect_from_real_rows();
        Recent_events_expire_at_four_seconds_and_cache_inputs_track_condition_changes();
        A_stats_condition_is_decided_from_the_state_the_server_keeps();
        The_shield_condition_is_answered_in_every_spelling_the_tables_use();
        A_shield_condition_that_holds_counts_on_its_plain_stat_and_not_without_a_shield();
        A_held_condition_counts_on_its_plain_stat_and_nothing_changes_without_a_subject();
        Real_aura_and_numeric_threshold_rolls_reach_existing_consumers();
        Recent_amounts_count_actual_occurrences_and_expire_individually();
        Equipment_counts_and_composed_conditions_use_the_same_subject();
        Per_charge_leaves_reach_existing_final_consumers();
        Per_rage_item_coefficients_reach_existing_final_consumers();
        Ordinary_slot_conditions_reach_existing_consumers();
        Canonical_condition_aliases_reach_defence_recovery_and_spell_consumers();
        Worn_item_grants_run_real_auras_with_host_identity_and_source_lifecycle();
        Archery_equipment_hardening_owns_dash_travel_and_conditional_consumers();
        Existing_owner_predicates_wire_exact_events_and_final_consumers();
        Physical_aegis_uses_its_real_grant_for_absorption_recharge_and_conditions();
        Doppelganger_guise_owns_its_toggle_and_final_consumers();
        Scorpion_call_uses_the_real_shared_stance_owner();
        Yriel_beast_source_owns_real_ursa_condition_and_physical_consumer();
        Bonemeld_counts_live_non_vaal_owner_minions_in_final_pools();
    }

    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    void Canonical_condition_aliases_reach_defence_recovery_and_spell_consumers()
    {
        string[] flags = ["_while_stationary", "_while_not_moving", "_while_frozen", "_while_shocked",
            "_when_on_full_life", "_when_on_low_life", "_while_chilled", "_while_ignited", "_while_phasing"];
        var cases = new (string Source, string Target)[]
        {
            ("X_armour_if_you_have_blocked_recently", "base_physical_damage_reduction_rating"),
            ("armour_+_while_affected_by_determination", "base_physical_damage_reduction_rating"),
            ("armour_while_stationary", "base_physical_damage_reduction_rating"),
            ("physical_damage_reduction_rating_%_while_not_moving", "physical_damage_reduction_rating_+%"),
            ("evasion_rating_+_when_on_full_life", "base_evasion_rating"),
            ("evasion_rating_+_when_on_low_life", "base_evasion_rating"),
            ("life_regeneration_per_minute_%_while_frozen", "life_regeneration_rate_per_minute_%"),
            ("energy_shield_regeneration_%_per_minute_while_shocked", "base_energy_shield_regeneration_rate_per_minute_%"),
            ("energy_shield_regeneration_rate_per_minute_%_while_affected_by_discipline", "base_energy_shield_regeneration_rate_per_minute_%"),
            ("additional_critical_strike_chance_permyriad_while_affected_by_hatred", "additional_base_critical_strike_chance"),
            ("critical_strike_chance_+%_for_spells_if_you_have_killed_recently", "spell_critical_strike_chance_+%"),
            ("critical_strike_multiplier_+_for_spells_if_you_havent_killed_recently", "spell_critical_strike_multiplier_+"),
            ("physical_damage_%_to_add_as_fire_damage_while_affected_by_anger", "physical_damage_%_to_add_as_fire"),
            ("physical_damage_%_to_add_as_lightning_damage_while_affected_by_wrath", "physical_damage_%_to_add_as_lightning"),
            ("fire_penetration_%_if_you_have_blocked_recently", "base_reduce_enemy_fire_resistance_%"),
            ("elemental_penetration_%_while_chilled", "base_penetrate_elemental_resistances_%"),
            ("block_%_while_affected_by_determination", "additional_block_%"),
            ("energy_shield_recovery_rate_while_affected_by_discipline_+%", "energy_shield_recovery_rate_+%"),
            ("life_leech_from_fire_damage_while_ignited_permyriad", "base_life_leech_from_fire_damage_permyriad"),
            ("burning_damage_+%_if_ignited_an_enemy_recently", "burn_damage_+%"),
            ("chance_to_deal_triple_damage_%_while_at_least_400_strength", "base_chance_to_deal_triple_damage_%"),
            ("energy_shield_leech_from_lightning_damage_permyriad_while_affected_by_wrath", "base_energy_shield_leech_from_lightning_damage_permyriad"),
            ("unaffected_by_bleeding_while_affected_by_malevolence", "base_unaffected_by_bleeding"),
            ("unaffected_by_poison_while_affected_by_malevolence", "base_unaffected_by_poison"),
            ("damage_removed_from_mana_before_life_%_while_affected_by_clarity", "base_damage_removed_from_mana_before_life_%"),
            ("physical_damage_reduction_%_while_affected_by_herald_of_purity", "base_additional_physical_damage_reduction_%"),
            ("minimum_added_cold_damage_while_affected_by_hatred", "global_minimum_added_cold_damage"),
            ("maximum_added_cold_damage_while_affected_by_hatred", "global_maximum_added_cold_damage"),
            ("minimum_added_fire_damage_if_blocked_recently", "global_minimum_added_fire_damage"),
            ("maximum_added_fire_damage_if_blocked_recently", "global_maximum_added_fire_damage"),
            ("number_of_additional_chains_for_projectiles_while_phasing", "number_of_chains"),
            ("gain_iron_reflexes_while_stationary", "keystone_iron_reflexes"),
            ("you_have_zealots_oath_if_you_havent_been_hit_recently", "keystone_zealots_oath"),
        };
        var output = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (source, target) in cases)
        {
            var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == source && !s.Local));
            var item = WithMod(row.Id, 11);
            bool notHit = source.Contains("havent_been_hit", StringComparison.Ordinal);
            bool negative = notHit || source.Contains("havent_killed", StringComparison.Ordinal);
            var on = new StatConditions.Subject(
                Recent: negative ? new HashSet<string>() : new HashSet<string> { StatConditions.Blocked, StatConditions.KilledEnemy, StatConditions.IgnitedEnemy },
                Auras: new HashSet<string> { "determination", "discipline", "hatred", "anger", "wrath", "malevolence", "clarity", "herald_of_purity" },
                Flags: flags.ToDictionary(s => s, _ => true), Value: _ => 10000);
            var off = new StatConditions.Subject(
                Recent: negative ? new HashSet<string> { notHit ? StatConditions.BeenHit : StatConditions.KilledEnemy } : new HashSet<string>(),
                Auras: new HashSet<string>(), Flags: flags.ToDictionary(s => s, _ => false), Value: _ => 0);
            Assert.Equal(StatConditions.State.Holds, StatConditions.Of(source, on));
            Assert.Equal(StatConditions.State.Fails, StatConditions.Of(source, off));
            var held = ItemModStats.Sum([item], Program.Items.Mods, subject: on, statExists: Program.Stats.Has);
            Assert.Equal(11, held.GetValueOrDefault(target));
            Assert.False(held.ContainsKey(source));
            Assert.Equal(0, ItemModStats.Sum([item], Program.Items.Mods, subject: off, statExists: Program.Stats.Has).GetValueOrDefault(target));
            // Unknown state and a catalogue without the destination must retain the original row.
            Assert.Equal(11, ItemModStats.Sum([item], Program.Items.Mods).GetValueOrDefault(source));
            Assert.Equal(11, ItemModStats.Sum([item], Program.Items.Mods, subject: on, statExists: _ => false).GetValueOrDefault(source));
            output[source] = held;
        }
        int Read(string source, string name) => output[source].GetValueOrDefault(name);
        var iron = output["gain_iron_reflexes_while_stationary"].Where(p => Program.Stats.Key(p.Key) is not null)
            .ToDictionary(p => Program.Stats.Key(p.Key)!.Value, p => p.Value);
        Assert.True(Game.PassiveBonus(iron, default, default).IronReflexes);
        Assert.False(Game.PassiveBonus([], default, default).IronReflexes);
        Assert.True(PanelStats.ZealotsOath(name => Read("you_have_zealots_oath_if_you_havent_been_hit_recently", name)));
        Assert.False(PanelStats.ZealotsOath(_ => 0));
        const string martyr = "immune_to_freeze_and_chill_while_ignited";
        var martyrMod = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == martyr));
        var martyrItem = WithMod(martyrMod.Id, 1);
        foreach (bool ignited in new[] { false, true })
        {
            var raw = ItemModStats.Sum([martyrItem], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_ignited"] = ignited }), statExists: Program.Stats.Has);
            var panel = PanelStats.Compute(raw.GetValueOrDefault).ToDictionary(p => p.Stat, p => p.Value);
            var ailments = new PlayerElementalAilments();
            foreach (var kind in new[] { PlayerElementalAilments.Kind.Freeze, PlayerElementalAilments.Kind.Chill })
                ailments.Inflict(kind, 10, 2, 100, panel.GetValueOrDefault, new Random(504));
            Assert.Equal(!ignited, ailments.Snapshot(100).Freeze);
            Assert.Equal(ignited ? 0 : 10, ailments.Snapshot(100).Chill);
        }
        Assert.Equal(555, SpellCriticalStrike.Chance(500,
            name => Read("critical_strike_chance_+%_for_spells_if_you_have_killed_recently", name)));
        Assert.Equal(511, SpellCriticalStrike.Chance(500,
            name => Read("additional_critical_strike_chance_permyriad_while_affected_by_hatred", name)));
        Assert.Equal(110, EnergyShield.RegenPerMinute(1000,
            name => Read("energy_shield_regeneration_%_per_minute_while_shocked", name)));
        var penetration = output["fire_penetration_%_if_you_have_blocked_recently"]
            .Where(p => Program.Stats.Key(p.Key) is not null).ToDictionary(p => Program.Stats.Key(p.Key)!.Value, p => p.Value);
        Assert.Equal(11, Penetration.Of(penetration, Program.Stats, attack: false).Fire);
        Assert.Equal(0, Penetration.Of(penetration, Program.Stats, attack: false).Cold);
        Assert.Equal(11, GamblerModifiers.From(
            name => Read("chance_to_deal_triple_damage_%_while_at_least_400_strength", name)).TripleDamageChance);
        var fireLeech = LeechSources.From(name => Read("life_leech_from_fire_damage_while_ignited_permyriad", name), attack: false);
        Assert.Equal(11, fireLeech.Life[GameServer.Core.World.DamageType.Fire].Min);
        Assert.Equal(0, fireLeech.Life[GameServer.Core.World.DamageType.Physical].Min);
        Assert.Equal(111, EnergyShield.RegenPerMinute(1000, name => name == "base_energy_shield_regeneration_rate_per_minute_%"
            ? 10 : Read("energy_shield_recovery_rate_while_affected_by_discipline_+%", name)), precision: 8);
        Assert.True(StatConditions.TryAuraName("energy_shield_recovery_rate_while_affected_by_discipline_+%", out string family));
        Assert.Equal("discipline", family);
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of("damage_while_affected_by_unknown_shrine_+%",
            new StatConditions.Subject(Auras: new HashSet<string>())));
        foreach (bool spell in new[] { false, true })
        {
            var added = DamageStats.RawAdded(GameServer.Core.World.DamagePacket.Empty,
                name => Read("minimum_added_cold_damage_while_affected_by_hatred", name), spell);
            Assert.Equal(11, added[GameServer.Core.World.DamageType.Cold].Min);
            Assert.Equal(11, added[GameServer.Core.World.DamageType.Cold].Max);
        }
        const string bothRecovery = "life_and_energy_shield_recovery_rate_+%_while_affected_by_malevolence";
        var recoveryMod = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == bothRecovery));
        var recovery = ItemModStats.Sum([WithMod(recoveryMod.Id, 11)], Program.Items.Mods,
            subject: new(Auras: new HashSet<string> { "malevolence" }), statExists: Program.Stats.Has);
        Assert.Equal(11, recovery.GetValueOrDefault("life_recovery_rate_+%"));
        Assert.Equal(11, recovery.GetValueOrDefault("energy_shield_recovery_rate_+%"));
        Assert.False(recovery.ContainsKey(bothRecovery));
        const string kintsugi = "damage_taken_+%_if_not_hit_recently_final";
        var kintsugiMod = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == kintsugi));
        var kintsugiItem = WithMod(kintsugiMod.Id, -35);
        var history = new RecentEvents();
        foreach (var (at, expected) in new[] { (10d, 65), (10.1, 100), (14.1, 65) })
        {
            if (at == 10.1) history.Record(StatConditions.BeenHit, at);
            var values = ItemModStats.Sum([kintsugiItem], Program.Items.Mods,
                subject: new(Recent: history.Snapshot(at)), statExists: Program.Stats.Has);
            Assert.Equal(expected, IncomingResourceDefences.Taken(100, values.GetValueOrDefault));
            double carry = 0;
            Assert.Equal(expected, IncomingResourceDefences.TakenOverTime(100, values.GetValueOrDefault, ref carry));
        }

        foreach (var (source, target) in new[]
                 {
                     ("maximum_es_+%_per_equipped_corrupted_item", "maximum_energy_shield_+%"),
                     ("all_resistances_%_per_equipped_corrupted_item", "base_resist_all_elements_%"),
                 })
        {
            var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == source && !s.Local));
            var item = WithMod(row.Id, 11);
            foreach (int count in new[] { 0, 1, 3, 0 })
            {
                var values = ItemModStats.Sum([item], Program.Items.Mods,
                    subject: new(Value: name => name == "equipped_corrupted" ? count : null), statExists: Program.Stats.Has);
                Assert.Equal(11 * count, values.GetValueOrDefault(target));
                Assert.False(values.ContainsKey(source));
                if (source.StartsWith("all_resistances", StringComparison.Ordinal))
                    Assert.Equal(11 * count, values.GetValueOrDefault("base_chaos_damage_resistance_%"));
            }
        }
    }

    void Yriel_beast_source_owns_real_ursa_condition_and_physical_consumer()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-yriel-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        var lifetimeLogic = new GameServer.Modules.Game.GameLogic(lifetime);
        Program.SetGameLogic(lifetimeLogic);
        using var wire = new Wire();
        using var lifecycleWire = new Wire();
        var characters = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            ClientState NewConnection(string label)
            {
                characters.Add(label);
                var connection = new ClientState(new(1, label, 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = label,
                    PlayerLife = 1000,
                    PlayerMana = 1000,
                    PlayerSent = true,
                    Instance = new(),
                    Experience = CharacterStats.ExperienceFor(100),
                    Pools = new(100, 1000, 1000, int.MaxValue, 0,
                        Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                        new HashSet<string>(["Game"])),
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    connection.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                return connection;
            }

            Item YrielWithBeastGrant(Item item, bool rhoa, bool snake = false, bool projectileAilments = false)
            {
                var speed = Mod("AttackAndMovementSpeedBeastialMinionUnique__1");
                var chaos = Mod("AddedChaosDamageToAttacksBeastialMinionUnique__1");
                var physical = Mod("AddedPhysicalDamageToAttacksBeastialMinionUnique__1");
                var bleed = Mod("ProjectileAttacksChanceToBleedBeastialMinionUnique__1_");
                var poison = Mod("ProjectileAttacksChanceToPoisonBeastialMinionUnique__1");
                var maim = Mod("ProjectileAttacksChanceToMaimBeastialMinionUnique__1");
                string[] profileIds =
                [
                    "GrantsSummonBeastUrsaUnique__1",
                    "GrantsSummonBeastRhoaUnique__1",
                    "GrantsSummonBeastSnakeUnique__1",
                    "AddedPhysicalDamageToAttacksBeastialMinionUnique__1",
                    "AttackAndMovementSpeedBeastialMinionUnique__1",
                    "AddedChaosDamageToAttacksBeastialMinionUnique__1",
                    "ProjectileAttacksChanceToMaimBeastialMinionUnique__1",
                    "ProjectileAttacksChanceToBleedBeastialMinionUnique__1_",
                    "ProjectileAttacksChanceToPoisonBeastialMinionUnique__1",
                ];
                bool IsProfileMod(ItemMod mod) =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id is { } id && profileIds.Contains(id);
                string grantId = snake ? "GrantsSummonBeastSnakeUnique__1"
                    : rhoa ? "GrantsSummonBeastRhoaUnique__1" : "GrantsSummonBeastUrsaUnique__1";
                var profileMods = new List<ItemMod>
                {
                    Mod(grantId).Roll(ModSection.Explicit, new Random(1601)),
                };
                if (rhoa)
                {
                    profileMods.Add(speed.Roll(ModSection.Explicit, new Random(1801)) with { Values = [20] });
                    profileMods.Add(bleed.Roll(ModSection.Explicit, new Random(1803)));
                }
                else if (snake)
                {
                    profileMods.Add(chaos.Roll(ModSection.Explicit, new Random(1802)) with { Values = [27, 42] });
                    profileMods.Add(poison.Roll(ModSection.Explicit, new Random(1804)));
                }
                else
                {
                    profileMods.Add(physical.Roll(ModSection.Explicit, new Random(1802)));
                    profileMods.Add(maim.Roll(ModSection.Explicit, new Random(1805)));
                }
                item = item with
                {
                    Mods = [
                        .. item.Mods.Where(mod => !IsProfileMod(mod)),
                        .. profileMods,
                    ]
                };
                // Shared consumer fixture: diagnostic hybrid, not ordinary-catalogue proof.
                item = item with
                {
                    Mods = [
                        .. item.Mods.Where(mod => mod.Hash != speed.Hash && mod.Hash != chaos.Hash),
                        speed.Roll(ModSection.Explicit, new Random(1801)) with { Values = [20] },
                        chaos.Roll(ModSection.Explicit, new Random(1802)) with { Values = [27, 42] },
                    ]
                };
                if (projectileAilments)
                {
                    string[] chances =
                    [
                        "ProjectileAttacksChanceToBleedBeastialMinionUnique__1_",
                        "ProjectileAttacksChanceToPoisonBeastialMinionUnique__1",
                        "ProjectileAttacksChanceToMaimBeastialMinionUnique__1",
                    ];
                    bool IsChanceMod(ItemMod mod) =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id is { } id && chances.Contains(id);
                    item = item with
                    {
                        Mods = [
                            .. item.Mods.Where(mod => !IsChanceMod(mod)),
                            bleed.Roll(ModSection.Explicit, new Random(1803)) with { Values = [100] },
                            poison.Roll(ModSection.Explicit, new Random(1804)) with { Values = [100] },
                            maim.Roll(ModSection.Explicit, new Random(1805)) with { Values = [100] },
                        ]
                    };
                }
                return item;
            }

            InventoryItem EquipYriel(ClientState target, bool rhoa = false, bool snake = false,
                bool projectileAilments = false)
            {
                var items = (ItemCatalog)global::Game.ItemsFor(target)!;
                var inventory = (PlayerInventories)global::Game.ItemsOf(target)!;
                string variant = snake ? "Snake" : rhoa ? "Rhoa" : "Ursa";
                var yriel = YrielWithBeastGrant(
                    new UniqueCatalog(items).Create($"Yriel's Fostering ({variant})", new Random(504))!, rhoa, snake,
                    projectileAilments);
                foreach (var old in inventory.Items(PlayerInventories.BodyArmour))
                    inventory.Remove(PlayerInventories.BodyArmour, old.Id);
                var worn = inventory.Add(PlayerInventories.BodyArmour, 0, 0, yriel);
                global::Game.EquipmentStatSources(target, inventory.EquipmentSnapshot());
                target.Pools = global::Game.PoolsFor(target, 100);
                target.PlayerLife = target.Pools.Life;
                target.PlayerMana = target.Pools.Mana;
                return worn;
            }

            bool Begin(ClientState target, ushort skillId, out global::Game.SupportedGem supported,
                out CooldownUseTicket? ticket, Action? sideEffects = null)
            {
                supported = global::Game.SupportedSkill(target, skillId);
                var cooldown = global::Game.CooldownFor(target, supported.Effect, supported.Level, supported.Quality);
                Assert.NotNull(cooldown);
                return global::Game.TryBeginSkillCooldownUse(target, skillId, supported.Effect, cooldown,
                    instant: false, sideEffects ?? (() => { }), out ticket);
            }

            bool Cast(ClientState target, ushort skillId)
            {
                if (!Begin(target, skillId, out _, out var ticket)) return false;
                return global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, target, skillId,
                    (0, 0), (0, 0), cooldownUse: ticket).GetAwaiter().GetResult();
            }

            bool CastWithAcceptedAction(ClientState target, ushort skillId)
            {
                Assert.True(Begin(target, skillId, out _, out var ticket,
                    () => { lock (target.Monsters) target.ActionCount++; }));
                Assert.NotNull(ticket);
                int? actionToken = ticket!.ActionToken;
                bool accepted = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, target, skillId,
                    (0, 0), (0, 0), actionToken: actionToken, cooldownUse: ticket).GetAwaiter().GetResult();
                return accepted;
            }

            void Ready(ClientState target)
            {
                lock (target.Monsters)
                {
                    target.SingleUseCooldowns.Clear();
                    target.SkillReadyAt.Clear();
                }
            }

            DamagePacket Attack(ClientState target)
            {
                var stats = global::Game.FinalPlayerStats(target).ToDictionary(s => s.Item1, s => s.Item2);
                return global::Game.AttackHitConverted(global::Game.WeaponFor(target), stats, melee: true, null);
            }
            DamageRange Physical(ClientState target) => Attack(target)[GameServer.Core.World.DamageType.Physical];

            void Yriel_catalogue_factory_creates_complete_pinned_and_ordinary_profiles()
            {
                var uniques = new UniqueCatalog(Program.Items);
                string[] common =
                [
                    "IncreasedAccuracyUnique__1",
                    "LocalIncreasedEvasionRatingPercentUnique__9",
                    "IncreasedLifeUniqueBodyStrDexInt1",
                    "ProjectileAttackCriticalStrikeChanceUnique__1",
                ];
                var profiles = new (string Name, string[] Mods)[]
                {
                    ("Ursa",
                    [
                        "GrantsSummonBeastUrsaUnique__1",
                        "AddedPhysicalDamageToAttacksBeastialMinionUnique__1",
                        "ProjectileAttacksChanceToMaimBeastialMinionUnique__1",
                    ]),
                    ("Rhoa",
                    [
                        "GrantsSummonBeastRhoaUnique__1",
                        "AttackAndMovementSpeedBeastialMinionUnique__1",
                        "ProjectileAttacksChanceToBleedBeastialMinionUnique__1_",
                    ]),
                    ("Snake",
                    [
                        "GrantsSummonBeastSnakeUnique__1",
                        "AddedChaosDamageToAttacksBeastialMinionUnique__1",
                        "ProjectileAttacksChanceToPoisonBeastialMinionUnique__1",
                    ]),
                };
                var profileOnly = profiles.SelectMany(profile => profile.Mods).ToHashSet(StringComparer.Ordinal);
                string ModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)!.Id;
                ItemMod ModOn(Item item, string id) => Assert.Single(item.Mods, mod => ModId(mod) == id);

                var canonicalDefinition = uniques.Find("Yriel's Fostering");
                Assert.NotNull(canonicalDefinition);
                var canonical = uniques.Create(canonicalDefinition!, new FixedRandom(0))!;
                Assert.Equal("Rhoa", uniques.Find("yriel\u2019s fostering (rhoa)")?.CreationVariant);
                Assert.Null(uniques.Find("Yriel's Fostering (unknown)"));

                foreach (var profile in profiles)
                {
                    var definition = uniques.Find($"Yriel's Fostering ({profile.Name})");
                    Assert.NotNull(definition);
                    Assert.Equal(profile.Name, definition!.CreationVariant);
                    var pinned = uniques.Create(definition, new Random(504))!;
                    Assert.Equal(canonical.NameWords, pinned.NameWords);
                    Assert.Equal(canonical.VisualIdentity, pinned.VisualIdentity);

                    var actualProfile = pinned.Mods.Where(mod => profileOnly.Contains(ModId(mod)))
                        .Select(ModId).ToArray();
                    Assert.Equal(profile.Mods.Length, actualProfile.Length);
                    Assert.All(profile.Mods, id => Assert.Contains(id, actualProfile));
                    Assert.All(profile.Mods, id => Assert.Single(pinned.Mods, mod => ModId(mod) == id));
                    Assert.All(common, id => Assert.Single(pinned.Mods, mod => ModId(mod) == id));
                    foreach (string id in profile.Mods.Concat(common))
                    {
                        var itemMod = ModOn(pinned, id);
                        var catalogueMod = Program.Items.Mods.ByHash(itemMod.Hash)!;
                        for (int stat = 0; stat < itemMod.Values.Count; stat++)
                        {
                            var (min, max) = catalogueMod.RangeAt(stat);
                            Assert.InRange(itemMod.Values[stat], min, max);
                        }
                    }
                }

                for (int pick = 0; pick < profiles.Length; pick++)
                {
                    var ordinary = uniques.Create(canonicalDefinition!, new FixedRandom(pick))!;
                    var actualProfile = ordinary.Mods.Where(mod => profileOnly.Contains(ModId(mod)))
                        .Select(ModId).ToArray();
                    Assert.Equal(profiles[pick].Mods.Length, actualProfile.Length);
                    Assert.All(profiles[pick].Mods, id => Assert.Contains(id, actualProfile));
                }

                var snake = uniques.Create(uniques.Find("Yriel's Fostering (Snake)")!, new Random(504))!;
                string savedCharacter = "YrielCatalogue" + Guid.NewGuid().ToString("N");
                CharacterSaves.Write(savedCharacter, new CharacterSaves.Save(new()
                {
                    [PlayerInventories.BodyArmour] =
                    [
                        new(1, 0, 0, CharacterSaves.ToSaved(snake, Program.Items.Mods)),
                    ],
                }));
                var saved = Assert.Single(CharacterSaves.Load(savedCharacter)!.Inventories[PlayerInventories.BodyArmour]);
                var restored = CharacterSaves.FromSaved(saved.Item, Program.Items);
                Assert.Equal(snake.NameWords, restored.NameWords);
                Assert.Equal(snake.VisualIdentity, restored.VisualIdentity);
                Assert.Equal(string.Join("|", snake.Mods.Select(mod =>
                        $"{ModId(mod)}:{mod.Section}:{string.Join(',', mod.Values)}")),
                    string.Join("|", restored.Mods.Select(mod =>
                        $"{ModId(mod)}:{mod.Section}:{string.Join(',', mod.Values)}")));
            }
            Yriel_catalogue_factory_creates_complete_pinned_and_ordinary_profiles();

            Projectile_attack_ailments_use_real_captured_offense_and_frozen_source_conditions();

            void Projectile_attack_ailments_use_real_captured_offense_and_frozen_source_conditions()
            {
                var projectileConn = NewConnection("YrielProjectile" + Guid.NewGuid().ToString("N"));
                var projectileWorn = EquipYriel(projectileConn, projectileAilments: true);
                var inventory = (PlayerInventories)global::Game.ItemsOf(projectileConn)!;
                var items = (ItemCatalog)global::Game.ItemsFor(projectileConn)!;
                var burningArrow = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemBurningArrow")!) with
                {
                    Quality = 20,
                };
                var fireball = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFireball")!) with
                {
                    Quality = 20,
                };
                var bow = new UniqueCatalog(items).Create("Arborix", new Random(504))! with
                {
                    Sockets = [
                        new ItemSocket(SocketColour.White, burningArrow),
                        new ItemSocket(SocketColour.White, fireball),
                    ],
                    Links = [1, 1],
                };
                foreach (var old in inventory.Items(PlayerInventories.Weapon1))
                    inventory.Remove(PlayerInventories.Weapon1, old.Id);
                foreach (var old in inventory.Items(PlayerInventories.Offhand1))
                    inventory.Remove(PlayerInventories.Offhand1, old.Id);
                inventory.Add(PlayerInventories.Weapon1, 0, 0, bow);
                global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, projectileConn, inventory)
                    .GetAwaiter().GetResult();
                projectileConn.Pools = global::Game.PoolsFor(projectileConn, 100);
                projectileConn.PlayerLife = projectileConn.Pools.Life;
                projectileConn.PlayerMana = projectileConn.Pools.Mana;

                var projectileActor = Assert.Single(global::Game.GemSkillEffects(projectileConn),
                    g => global::Game.SkillsFor(projectileConn).EffectId(g.Effect) == "SummonBeastialUrsa");
                string[] handAliases =
                [
                    "virtual_total_main_hand_chance_to_bleed_on_hit_%",
                    "virtual_total_main_hand_chance_to_poison_on_hit_%",
                    "main_hand_maim_on_hit_%",
                ];
                var beforeBeastStats = global::Game.FinalPlayerStats(projectileConn)
                    .ToDictionary(s => s.Item1, s => s.Item2);
                int PanelValue(IReadOnlyDictionary<uint, int> values, string stat) =>
                    values.GetValueOrDefault(Program.Stats.Key(stat)!.Value);
                foreach (var alias in handAliases) Assert.Equal(0, PanelValue(beforeBeastStats, alias));
                Ready(projectileConn);
                Assert.True(Cast(projectileConn, projectileActor.Id));
                global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, projectileConn,
                    projectileConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
                var beast = Assert.IsType<BeastCompanions.ActiveBeast>(projectileConn.Beasts.Active);
                Assert.True(projectileConn.Minions.Owns(beast.Id));
                Assert.True(global::Game.ItemConditionFlags(projectileConn,
                    projectileConn.Clock.Elapsed.TotalSeconds)["_if_you_have_beast_minion"]);
                var projectileStats = global::Game.FinalPlayerStats(projectileConn)
                    .ToDictionary(s => s.Item1, s => s.Item2);
                Dictionary<uint, int> PublishedRows(ClientState conn, uint skillId) =>
                    Assert.Single(global::Game.PlayerSkills(conn), skill => skill.Id == skillId).Stats!
                    .ToDictionary(stat => stat.Key, stat => stat.Value);
                uint PublishedSkill(string effect) => Assert.Single(global::Game.GemSkillEffects(projectileConn),
                    grant => global::Game.SkillsFor(projectileConn).EffectId(grant.Effect) == effect).Id;
                Assert.Equal(100, PanelValue(projectileStats, "virtual_total_main_hand_chance_to_bleed_on_hit_%"));
                Assert.Equal(100, PanelValue(projectileStats, "virtual_total_main_hand_chance_to_poison_on_hit_%"));
                Assert.Equal(100, PanelValue(projectileStats, "main_hand_maim_on_hit_%"));
                Assert.Equal(0, PanelValue(projectileStats, "virtual_total_off_hand_chance_to_bleed_on_hit_%"));
                Assert.Equal(0, PanelValue(projectileStats, "virtual_total_off_hand_chance_to_poison_on_hit_%"));
                Assert.Equal(0, PanelValue(projectileStats, "off_hand_maim_on_hit_%"));
                var repeatedProjectileStats = global::Game.FinalPlayerStats(projectileConn)
                    .ToDictionary(s => s.Item1, s => s.Item2);
                Assert.True(projectileStats.OrderBy(s => s.Key).SequenceEqual(repeatedProjectileStats.OrderBy(s => s.Key)));

                var defaultAttackRows = PublishedRows(projectileConn, SkillCatalog.FirstSkillId);
                Assert.Equal(100, defaultAttackRows.GetValueOrDefault(
                    Program.Stats.Key("virtual_total_main_hand_chance_to_bleed_on_hit_%")!.Value));
                Assert.Equal(100, defaultAttackRows.GetValueOrDefault(
                    Program.Stats.Key("virtual_total_main_hand_chance_to_poison_on_hit_%")!.Value));
                Assert.Equal(100, defaultAttackRows.GetValueOrDefault(
                    Program.Stats.Key("main_hand_maim_on_hit_%")!.Value));
                Assert.Equal(defaultAttackRows.OrderBy(stat => stat.Key),
                    PublishedRows(projectileConn, SkillCatalog.FirstSkillId).OrderBy(stat => stat.Key));
                var burningArrowRows = PublishedRows(projectileConn, PublishedSkill("BurningArrow"));
                Assert.Equal(100, burningArrowRows.GetValueOrDefault(
                    Program.Stats.Key("virtual_total_main_hand_chance_to_bleed_on_hit_%")!.Value));
                Assert.Equal(100, burningArrowRows.GetValueOrDefault(
                    Program.Stats.Key("virtual_total_main_hand_chance_to_poison_on_hit_%")!.Value));
                Assert.Equal(100, burningArrowRows.GetValueOrDefault(
                    Program.Stats.Key("main_hand_maim_on_hit_%")!.Value));
                Assert.Equal(burningArrowRows.OrderBy(stat => stat.Key),
                    PublishedRows(projectileConn, PublishedSkill("BurningArrow")).OrderBy(stat => stat.Key));
                var fireballRows = PublishedRows(projectileConn, PublishedSkill("Fireball"));
                foreach (var alias in handAliases) Assert.Equal(0, fireballRows.GetValueOrDefault(Program.Stats.Key(alias)!.Value));

                var chanceSources = new Dictionary<string, int>
                {
                    ["bleed_on_hit_with_attacks_%"] = 10,
                    ["local_chance_to_bleed_on_hit_%"] = 20,
                    ["projectile_attacks_chance_to_bleed_on_hit_%_if_you_have_beast_minion"] = 30,
                    ["projectile_attacks_chance_to_poison_on_hit_%_if_you_have_beast_minion"] = 50,
                };
                static int Source(Dictionary<string, int> stats, string name) => stats.GetValueOrDefault(name);
                var chanceRows = PanelStats.Compute(name => Source(chanceSources, name),
                    name => name == "local_chance_to_bleed_on_hit_%" ? Source(chanceSources, name) : 0,
                    mainProjectileAttack: true, offProjectileAttack: true).ToDictionary(r => r.Stat, r => r.Value);
                Assert.Equal(60, chanceRows.GetValueOrDefault("virtual_total_main_hand_chance_to_bleed_on_hit_%"));
                Assert.Equal(50, chanceRows.GetValueOrDefault("virtual_total_off_hand_chance_to_poison_on_hit_%"));
                chanceSources["bleed_on_hit_with_attacks_%"] = 70;
                chanceSources["projectile_attacks_chance_to_bleed_on_hit_%_if_you_have_beast_minion"] = 70;
                Assert.Equal(100, PanelStats.Compute(name => Source(chanceSources, name),
                    name => name == "local_chance_to_bleed_on_hit_%" ? Source(chanceSources, name) : 0,
                    mainProjectileAttack: true).Single(r => r.Stat == "virtual_total_main_hand_chance_to_bleed_on_hit_%").Value);
                Assert.Equal(100, PanelStats.Compute(name => Source(chanceSources, name),
                    name => name == "local_chance_to_bleed_on_hit_%" ? Source(chanceSources, name) : 0,
                    mainProjectileAttack: true).Single(r => r.Stat == "virtual_total_main_hand_chance_to_bleed_on_hit_%").Value);
                Assert.Equal(100, projectileStats.GetValueOrDefault(Program.Stats.Key(
                    "projectile_attacks_chance_to_maim_on_hit_%_if_you_have_beast_minion")!.Value));
                Assert.Equal(0, projectileStats.GetValueOrDefault(
                    Program.Stats.Key("maim_on_hit_%") ?? 0));

                // Keep one production session and do not start its update pump.
                var projectileSession = new global::Game.ProjectileSession(
                    "test", wire.Stream, wire.Ciphers, projectileConn);
                projectileConn.ProjectileRuntime = projectileSession;

                SpawnedMonster Target(uint id, int dx, bool hostile = true)
                {
                    var from = projectileConn.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var monster = new SpawnedMonster($"Yriel projectile target 0x{id:x}",
                        new PlayerWalk(from.X + dx, from.Y, 0), 0, 0, 100_000)
                    {
                        Hostile = hostile,
                        Life = 100_000,
                        NoRewards = true,
                    };
                    lock (projectileConn.Monsters) projectileConn.Monsters[id] = monster;
                    return monster;
                }
                void Release(ProjectileUse use, global::Game.ProjectileSession session)
                {
                    session.Runtime.Release(use);
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(.5));
                }
                void AssertPayload(ProjectileUse use, int bleedChance, int poisonChance, int maimChance)
                {
                    var offense = Assert.IsType<ProjectileOffense>(use.Payload);
                    Assert.Equal(bleedChance, offense.Bleed.Chance);
                    Assert.Equal(poisonChance, offense.Poison.ProjectileChance);
                    Assert.Equal(maimChance, offense.MaimChance);
                }
                void AssertHitEffects(SpawnedMonster monster, bool expectedBleed, bool expectedPoison, bool expectedMaim)
                {
                    double now = projectileSession.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                    if (expectedBleed)
                    {
                        Assert.True(monster.Bleeds.Burning);
                        Assert.True(monster.Bleeds.PerSecond(now) > 0);
                    }
                    else
                    {
                        Assert.False(monster.Bleeds.Burning);
                        Assert.Equal(0, monster.Bleeds.PerSecond(now));
                    }
                    if (expectedPoison)
                    {
                        Assert.True(monster.Poisons.Stacks(now) > 0);
                        Assert.True(monster.Poisons.TotalDps(now) > 0);
                    }
                    else
                    {
                        Assert.Equal(0, monster.Poisons.Stacks(now));
                        Assert.Equal(0, monster.Poisons.TotalDps(now));
                    }
                    if (expectedMaim)
                    {
                        Assert.True(monster.MaimedUntil > now);
                        Assert.Equal(0, global::Game.MaimedPhysicalTaken(monster, now));
                    }
                    else
                    {
                        Assert.False(monster.MaimedUntil > now);
                    }
                }
                void RemoveTarget(uint id)
                {
                    lock (projectileConn.Monsters) projectileConn.Monsters.Remove(id);
                }
                void AssertNoMaimContact(uint id, Action<SpawnedMonster>? configure = null, bool hostile = true)
                {
                    var target = Target(id, 12, hostile);
                    configure?.Invoke(target);
                    double maimedBefore = target.MaimedUntil;
                    var aim = target.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var use = global::Game.CaptureOrdinaryRangedProjectile(projectileConn, projectileSession,
                        ((int)aim.X, (int)aim.Y), global::OrdinaryRangedAttack.Arrow, (ushort)id, new SkillCriticalRoll(0), null);
                    AssertPayload(use, bleedChance: 100, poisonChance: 100, maimChance: 100);
                    Release(use, projectileSession);
                    Assert.Equal(maimedBefore, target.MaimedUntil);
                    RemoveTarget(id);
                }

                try
                {
                    const uint ordinaryBeforeRetirement = 0x18b2;
                    var ordinaryTarget = Target(ordinaryBeforeRetirement, 12);
                    var ordinaryAim = ordinaryTarget.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var ordinary = global::Game.CaptureOrdinaryRangedProjectile(projectileConn, projectileSession,
                        ((int)ordinaryAim.X, (int)ordinaryAim.Y), global::OrdinaryRangedAttack.Arrow, 0x18b2,
                        new SkillCriticalRoll(0), null);
                    Release(ordinary, projectileSession);
                    AssertPayload(ordinary, bleedChance: 100, poisonChance: 100, maimChance: 100);
                    Assert.True(ordinaryTarget.Life < 100_000);
                    Assert.True(ordinaryTarget.Life > 0);
                    AssertHitEffects(ordinaryTarget, expectedBleed: true, expectedPoison: true, expectedMaim: true);
                    RemoveTarget(ordinaryBeforeRetirement);

                    const uint attackTargetId = 0x18b3;
                    var attackTarget = Target(attackTargetId, 12);
                    var arrow = Assert.Single(global::Game.GemSkillEffects(projectileConn),
                        g => global::Game.SkillsFor(projectileConn).EffectId(g.Effect) == "BurningArrow");
                    var attackAim = attackTarget.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var attack = global::Game.CaptureGemProjectile(projectileConn, projectileSession, arrow.Id, arrow,
                        ((int)attackAim.X, (int)attackAim.Y), new SkillCriticalRoll(0));
                    Release(attack, projectileSession);
                    AssertPayload(attack, bleedChance: 100, poisonChance: 100, maimChance: 100);
                    var attackOffense = Assert.IsType<ProjectileOffense>(attack.Payload);
                    Assert.Equal(0, attackOffense.Hit[GameServer.Core.World.DamageType.Physical].Min);
                    Assert.Equal(0, attackOffense.Hit[GameServer.Core.World.DamageType.Physical].Max);
                    Assert.Equal(0d, attackOffense.Bleed.Min);
                    Assert.Equal(0d, attackOffense.Bleed.Max);
                    var actualDefaultAttackBleed = global::Game.DefaultAttackBleed(projectileConn, projectileStats);
                    var actualDefaultAttackPoison = global::Game.DefaultAttackPoison(projectileConn, projectileStats);
                    Assert.Equal(0, actualDefaultAttackBleed.Chance);
                    Assert.Equal(0, actualDefaultAttackPoison.Chance);
                    Assert.Equal(0, actualDefaultAttackPoison.ProjectileChance);
                    Assert.True(attackTarget.Life < 100_000);
                    Assert.True(attackTarget.Life > 0);
                    AssertHitEffects(attackTarget, expectedBleed: false, expectedPoison: true, expectedMaim: true);
                    RemoveTarget(attackTargetId);

                    const uint spellTargetId = 0x18b4;
                    var spellTarget = Target(spellTargetId, 12);
                    var spell = Assert.Single(global::Game.GemSkillEffects(projectileConn),
                        g => global::Game.SkillsFor(projectileConn).EffectId(g.Effect) == "Fireball");
                    var spellAim = spellTarget.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var spellUse = global::Game.CaptureGemProjectile(projectileConn, projectileSession, spell.Id, spell,
                        ((int)spellAim.X, (int)spellAim.Y), new SkillCriticalRoll(0));
                    Release(spellUse, projectileSession);
                    AssertPayload(spellUse, bleedChance: 0, poisonChance: 0, maimChance: 0);
                    Assert.True(spellTarget.Life < 100_000);
                    Assert.True(spellTarget.Life > 0);
                    AssertHitEffects(spellTarget, expectedBleed: false, expectedPoison: false, expectedMaim: false);
                    RemoveTarget(spellTargetId);

                    const uint immuneTargetId = 0x18b7;
                    AssertNoMaimContact(immuneTargetId, target =>
                        target.Mods = new MonsterModSet([], [],
                            new MonsterModEffects(new Dictionary<string, int> { { "immune_to_maim", 1 } })));

                    const uint avoidTargetId = 0x18b8;
                    AssertNoMaimContact(avoidTargetId, target =>
                        target.Mods = new MonsterModSet([], [],
                            new MonsterModEffects(new Dictionary<string, int> { { "avoid_maim_%_chance", 100 } })));

                    const uint friendlyTargetId = 0x18b9;
                    AssertNoMaimContact(friendlyTargetId, hostile: false);

                    const uint invulnerableTargetId = 0x18ba;
                    AssertNoMaimContact(invulnerableTargetId, target => target.KingGrace = true);

                    const uint deadTargetId = 0x18bb;
                    AssertNoMaimContact(deadTargetId, target =>
                    {
                        target.Dead = true;
                        target.Life = 0;
                    });

                    const uint staleTargetId = 0x18bc;
                    var stale = Target(staleTargetId, 12);
                    var staleAim = stale.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var staleUse = global::Game.CaptureOrdinaryRangedProjectile(projectileConn, projectileSession,
                        ((int)staleAim.X, (int)staleAim.Y), global::OrdinaryRangedAttack.Arrow, 0x18bc,
                        new SkillCriticalRoll(0), null);
                    AssertPayload(staleUse, bleedChance: 100, poisonChance: 100, maimChance: 100);
                    var staleRuntime = projectileSession.Runtime;
                    long staleUseId = staleRuntime.Release(staleUse);
                    var actualProjectile = staleRuntime.Projectiles.First(projectile =>
                        projectile.UseId == staleUseId);
                    Assert.Equal(100, Assert.IsType<ProjectileOffense>(actualProjectile.Use.Payload).MaimChance);
                    var oldActorKey = projectileSession.ActorKey(staleTargetId, stale);
                    RemoveTarget(staleTargetId);
                    var replacement = Target(staleTargetId, 12);
                    double replacementMaimed = replacement.MaimedUntil;
                    uint replacementLife = replacement.Life;
                    Assert.NotEqual(oldActorKey, projectileSession.ActorKey(staleTargetId, replacement));
                    Assert.Equal(ProjectileHitResult.Miss, projectileSession.Hit(new ProjectileDamage(
                        actualProjectile, oldActorKey, staleRuntime.Now, "recycled", default)));
                    Assert.Equal(replacementMaimed, replacement.MaimedUntil);
                    Assert.Equal(replacementLife, replacement.Life);
                    RemoveTarget(staleTargetId);

                    const uint deadlineTargetId = 0x18bd;
                    var deadlineTarget = Target(deadlineTargetId, 12);
                    double controlledNow = projectileSession.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                    Assert.True(global::Game.TryMaimOnHitLocked(
                        deadlineTarget, 100, 0, controlledNow, new Random(1806)));
                    Assert.Equal(controlledNow + global::Game.MaimSeconds, deadlineTarget.MaimedUntil);
                    Assert.Equal(0, global::Game.MaimedPhysicalTaken(deadlineTarget, controlledNow));
                    RemoveTarget(deadlineTargetId);

                    // Capture and start the ordinary bow flight while both the real beast and its item source exist.
                    const uint frozenTargetId = 0x18b5;
                    var frozenTarget = Target(frozenTargetId, 12);
                    var frozenAim = frozenTarget.Walk.Position(global::Game.ProjectileWorldTime(projectileConn));
                    var frozen = global::Game.CaptureOrdinaryRangedProjectile(projectileConn, projectileSession,
                        ((int)frozenAim.X, (int)frozenAim.Y), global::OrdinaryRangedAttack.Arrow, 0x18b5,
                        new SkillCriticalRoll(0), null);
                    projectileSession.Runtime.Release(frozen);
                    AssertPayload(frozen, bleedChance: 100, poisonChance: 100, maimChance: 100);
                    Assert.Equal(100_000u, frozenTarget.Life);

                    Assert.NotNull(inventory.Remove(PlayerInventories.BodyArmour, projectileWorn.Id));
                    global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, projectileConn,
                        inventory).GetAwaiter().GetResult();
                    Assert.Null(projectileConn.Beasts.Active);
                    Assert.False(global::Game.ItemConditionFlags(projectileConn,
                        projectileConn.Clock.Elapsed.TotalSeconds)["_if_you_have_beast_minion"]);
                    var clearedStats = global::Game.FinalPlayerStats(projectileConn).ToDictionary(s => s.Item1, s => s.Item2);
                    foreach (var alias in handAliases) Assert.Equal(0, PanelValue(clearedStats, alias));
                    var clearedDefaultRows = PublishedRows(projectileConn, SkillCatalog.FirstSkillId);
                    var clearedBurningArrowRows = PublishedRows(projectileConn, PublishedSkill("BurningArrow"));
                    var clearedFireballRows = PublishedRows(projectileConn, PublishedSkill("Fireball"));
                    foreach (var alias in handAliases)
                    {
                        var key = Program.Stats.Key(alias)!.Value;
                        Assert.Equal(0, clearedDefaultRows.GetValueOrDefault(key));
                        Assert.Equal(0, clearedBurningArrowRows.GetValueOrDefault(key));
                        Assert.Equal(0, clearedFireballRows.GetValueOrDefault(key));
                    }
                    Assert.Equal(clearedDefaultRows.OrderBy(stat => stat.Key),
                        PublishedRows(projectileConn, SkillCatalog.FirstSkillId).OrderBy(stat => stat.Key));
                    Assert.Equal(clearedBurningArrowRows.OrderBy(stat => stat.Key),
                        PublishedRows(projectileConn, PublishedSkill("BurningArrow")).OrderBy(stat => stat.Key));
                    Assert.Equal(clearedFireballRows.OrderBy(stat => stat.Key),
                        PublishedRows(projectileConn, PublishedSkill("Fireball")).OrderBy(stat => stat.Key));
                    Assert.Equal(100_000u, frozenTarget.Life);

                    projectileSession.AdvanceTo(projectileSession.Runtime.Now + ProjectileRuntime.Time(.5));
                    Assert.True(frozenTarget.Life < 100_000);
                    Assert.True(frozenTarget.Life > 0);
                    AssertHitEffects(frozenTarget, expectedBleed: true, expectedPoison: true, expectedMaim: true);
                    RemoveTarget(frozenTargetId);

                    const uint afterRetirementTargetId = 0x18b6;
                    var afterRetirementTarget = Target(afterRetirementTargetId, 12);
                    var afterRetirementAim = afterRetirementTarget.Walk.Position(
                        global::Game.ProjectileWorldTime(projectileConn));
                    var afterRetirement = global::Game.CaptureOrdinaryRangedProjectile(projectileConn,
                        projectileSession, ((int)afterRetirementAim.X, (int)afterRetirementAim.Y),
                        global::OrdinaryRangedAttack.Arrow, 0x18b6, new SkillCriticalRoll(0), null);
                    Release(afterRetirement, projectileSession);
                    AssertPayload(afterRetirement, bleedChance: 0, poisonChance: 0, maimChance: 0);
                    Assert.True(afterRetirementTarget.Life < 100_000);
                    Assert.True(afterRetirementTarget.Life > 0);
                    AssertHitEffects(afterRetirementTarget, expectedBleed: false, expectedPoison: false, expectedMaim: false);
                    RemoveTarget(afterRetirementTargetId);
                }
                finally
                {
                    projectileSession.Invalidate();
                    projectileSession.OutputCompleted.GetAwaiter().GetResult();
                }
            }

            string character = "Yriel" + Guid.NewGuid().ToString("N");
            var conn = NewConnection(character);
            var worn = EquipYriel(conn);
            var withoutBeastAttack = Attack(conn);
            var withoutBeastStats = global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            var withoutBeastSources = global::Game.CharacterStatSources(conn).Stats;
            var withoutBeastPools = conn.Pools;
            var grant = Assert.Single(global::Game.AllWornGrantedSkillsOf(conn),
                g => g.EffectId == "SummonBeastialUrsa");
            Assert.Equal((50728, 8453, 20, true, false),
                (grant.EffectsPerLevelRow, grant.Effect, grant.Level, grant.ManuallyCast, grant.Support));
            Assert.Contains(global::Game.WornGrantedSkillsOf(conn), g => g.Identity == grant.Identity);
            var actor = Assert.Single(global::Game.GemSkillEffects(conn),
                g => global::Game.SkillsFor(conn).EffectId(g.Effect) == "SummonBeastialUrsa");
            var supported = global::Game.SupportedSkill(conn, actor.Id);
            Assert.Equal((8453, 20), (supported.Effect, supported.Level));
            Assert.Equal(30, supported.Stats[Program.Stats.Key("mana_cost")!.Value]);
            Assert.Equal(70, supported.Stats[Program.Stats.Key("base_display_minion_actor_level")!.Value]);
            Assert.Equal(1, supported.Stats[Program.Stats.Key("base_number_of_beast_minions_allowed")!.Value]);
            Assert.Equal(30, supported.Stats[Program.Stats.Key("minion_maximum_life_+%")!.Value]);
            Assert.Equal(5000, global::Game.CooldownFor(conn, supported.Effect, supported.Level, supported.Quality)!
                .Value.BaseMilliseconds);

            var withoutBeast = Physical(conn);
            Assert.False(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            uint manaBefore = conn.PlayerMana;
            Assert.True(Cast(conn, actor.Id));
            Assert.Equal(manaBefore - 30, conn.PlayerMana);
            Assert.NotNull(conn.Beasts.Pending);
            Assert.False(Begin(conn, actor.Id, out _, out _)); // The shared cooldown is already spent.

            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, conn,
                conn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var active = Assert.IsType<BeastCompanions.ActiveBeast>(conn.Beasts.Active);
            Assert.True(conn.Minions.Owns(active.Id));
            Assert.True(conn.Minions.Fights(active.Id));
            Assert.Equal((byte)70, active.Monster.Level);
            Assert.False(active.Monster.Hostile);
            Assert.NotNull(active.Monster.Attack);
            Assert.Equal(30, active.Monster.Mods.Effects["maximum_life_+%"]);

            // The server's friendly follow for the real summoned Ursa reaches the native actor as a Move. The observer
            // sees the packet before encryption, so it must copy the id while the original bytes are still plaintext.
            var previousOutgoing = Program.Outgoing;
            var nativeMoves = new List<uint>();
            Program.Outgoing = (ciphers, bytes) =>
            {
                previousOutgoing?.Invoke(ciphers, bytes);
                if (ReferenceEquals(ciphers, wire.Ciphers) && bytes.Length >= 6
                    && (bytes[0] << 8 | bytes[1]) == InstanceProtocol.ActionStartedId)
                    nativeMoves.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2, 4)));
            };
            try
            {
                global::Game.StepMonsterAi("test", wire.Stream, wire.Ciphers, conn, new MonsterAiCadence())
                    .GetAwaiter().GetResult();
            }
            finally
            {
                Program.Outgoing = previousOutgoing;
            }
            Assert.Contains(active.Id, nativeMoves);
            Assert.True(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster, active.Monster.ActionGeneration));
            Assert.False(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster,
                active.Monster.ActionGeneration - 1));

            // Roster membership is not the gate: both the id and its exact spawned object, registry identity, current
            // generation and connection lifetime must still match.
            var ownedUrsa = Assert.Single(conn.Minions.All, m => m.Id == active.Id);
            conn.Minions.Remove(active.Id);
            Assert.False(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster, active.Monster.ActionGeneration));
            conn.Minions.Add(ownedUrsa);
            Assert.True(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster, active.Monster.ActionGeneration));
            var unrelated = global::Game.Monsters.Find("Metadata/Monsters/AnimatedItem/ElementalLivingRelicFire")!;
            uint unrelatedId = global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, conn, unrelated, 70,
                MonsterRarity.Normal, conn.Layout.SpawnX + 5, conn.Layout.SpawnY, friendly: true)
                .GetAwaiter().GetResult();
            SpawnedMonster unrelatedMonster;
            lock (conn.Monsters) unrelatedMonster = conn.Monsters[unrelatedId];
            Assert.False(global::Game.MonsterActionCurrent(conn, unrelatedId, unrelatedMonster,
                unrelatedMonster.ActionGeneration));
            lock (conn.Monsters) conn.Monsters[active.Id] = unrelatedMonster;
            Assert.False(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster, active.Monster.ActionGeneration));
            lock (conn.Monsters)
            {
                conn.Monsters[active.Id] = active.Monster;
                conn.Monsters.Remove(unrelatedId);
            }
            conn.RageRetired = true;
            Assert.False(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster, active.Monster.ActionGeneration));
            conn.RageRetired = false;
            conn.LeavingArea = true;
            Assert.False(global::Game.MonsterActionCurrent(conn, active.Id, active.Monster, active.Monster.ActionGeneration));
            conn.LeavingArea = false;

            Assert.True(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            var stableSourceOwner = active.Source.Owner;
            global::Game.SendSkills("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();
            Assert.Same(stableSourceOwner, Assert.IsType<BeastCompanions.ActiveBeast>(conn.Beasts.Active).Source.Owner);
            var withBeastStats = global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            var withBeastSources = global::Game.CharacterStatSources(conn).Stats;
            var withBeastAttack = Attack(conn);
            _ = global::Game.EnsureConditionalItemPools(conn);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            Assert.Equal(10, withoutBeastPools.AttackSpeedPct);
            Assert.Equal(32, conn.Pools.AttackSpeedPct);
            Assert.Equal(withoutBeastPools.MovementVelocityPct + 20, conn.Pools.MovementVelocityPct);
            Assert.Equal(27, (int)Math.Floor(withBeastAttack[DamageType.Chaos].Min
                - withoutBeastAttack[DamageType.Chaos].Min));
            Assert.Equal(42, (int)Math.Floor(withBeastAttack[DamageType.Chaos].Max
                - withoutBeastAttack[DamageType.Chaos].Max));
            int Stat(Dictionary<uint, int> values, string name) =>
                values.GetValueOrDefault(Program.Stats.Key(name) ?? throw new InvalidOperationException(name));
            Assert.Equal(20, Stat(withBeastSources, "attack_speed_+%") - Stat(withoutBeastSources, "attack_speed_+%"));
            Assert.Equal(Stat(withoutBeastStats, "cast_speed_+%"), Stat(withBeastStats, "cast_speed_+%"));
            Assert.Equal(Stat(withoutBeastStats, "spell_minimum_added_chaos_damage"),
                Stat(withBeastStats, "spell_minimum_added_chaos_damage"));
            Assert.Equal(Stat(withoutBeastStats, "spell_maximum_added_chaos_damage"),
                Stat(withBeastStats, "spell_maximum_added_chaos_damage"));
            var refreshedPools = conn.Pools;
            _ = global::Game.EnsureConditionalItemPools(conn);
            Assert.Equal(refreshedPools.AttackSpeedPct, global::Game.PoolsFor(conn, 100).AttackSpeedPct);
            Assert.Equal(refreshedPools.MovementVelocityPct, global::Game.PoolsFor(conn, 100).MovementVelocityPct);
            var withBeast = Physical(conn);
            Assert.InRange((int)Math.Floor(withBeast.Min - withoutBeast.Min), 18, 24);
            Assert.InRange((int)Math.Floor(withBeast.Max - withoutBeast.Max), 30, 36);

            // A different fighting player minion owns no beast predicate and contributes no conditional physical damage.
            var other = NewConnection(character + "Other");
            EquipYriel(other);
            var otherBefore = Physical(other);
            var relic = global::Game.Monsters.Find("Metadata/Monsters/AnimatedItem/ElementalLivingRelicFire")!;
            uint relicId = global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, other, relic, 70,
                MonsterRarity.Normal, other.Layout.SpawnX + 3, other.Layout.SpawnY, friendly: true,
                owned: (id, m) => other.Minions.Add(new(id, m, "ElementalRelic", "Fire") { Fights = true }))
                .GetAwaiter().GetResult();
            Assert.NotEqual(0u, relicId);
            Assert.False(global::Game.ItemConditionFlags(other, other.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            Assert.Equal(otherBefore, Physical(other));

            // The chieftain owner's previous broad Fights cleanup no longer retires the Ursa.
            global::Game.SpiritChieftainMinion("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();
            Assert.True(conn.Minions.Owns(active.Id));

            // A recast replaces the real capped entity rather than accumulating another beast.
            Ready(conn);
            conn.PlayerMana = conn.Pools.Mana;
            Assert.True(Cast(conn, actor.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, conn,
                conn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var replacement = Assert.IsType<BeastCompanions.ActiveBeast>(conn.Beasts.Active);
            Assert.NotEqual(active.Id, replacement.Id);
            Assert.Single(conn.Minions.All, m => m.Kind == "Summoned Ursa");
            Assert.False(conn.Minions.Owns(active.Id));

            // Removing and publishing the real item retires the paid active source. Restoring the same item host without a
            // minion tick starts a new source generation but cannot revive the old entity; fresh paid use can.
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            var removed = inventory.Remove(PlayerInventories.BodyArmour, worn.Id);
            Assert.NotNull(removed);
            Assert.Equal(worn.Id, removed!.Id);
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, conn, inventory)
                .GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, conn).GetAwaiter().GetResult();
            Assert.False(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            Assert.Null(conn.Beasts.Active);
            _ = global::Game.EnsureConditionalItemPools(conn);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            Assert.Equal(withoutBeastPools.AttackSpeedPct, conn.Pools.AttackSpeedPct);
            Assert.Equal(withoutBeastPools.MovementVelocityPct, conn.Pools.MovementVelocityPct);
            Assert.Equal(withoutBeastAttack[DamageType.Chaos], Attack(conn)[DamageType.Chaos]);
            Assert.False(conn.Minions.Owns(replacement.Id));
            Assert.True(replacement.Source.Owner.Retired);
            Assert.False(global::Game.TryBeginSkillCooldownUse(conn, actor.Id, actor.Effect,
                global::Game.CooldownFor(conn, supported.Effect, supported.Level, supported.Quality),
                instant: false, () => { }, out _));
            inventory.Put(PlayerInventories.BodyArmour, removed!);
            Assert.Equal(worn.Id, Assert.Single(inventory.Items(PlayerInventories.BodyArmour)).Id);
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, conn, inventory)
                .GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, conn).GetAwaiter().GetResult();
            Assert.False(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            Assert.Null(conn.Beasts.Active);
            _ = global::Game.EnsureConditionalItemPools(conn);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            Assert.Equal(withoutBeastPools.AttackSpeedPct, conn.Pools.AttackSpeedPct);
            Assert.Equal(withoutBeastPools.MovementVelocityPct, conn.Pools.MovementVelocityPct);
            Assert.Equal(withoutBeastAttack[DamageType.Chaos], Attack(conn)[DamageType.Chaos]);
            Assert.DoesNotContain(conn.Minions.All, m => m.Kind == "Summoned Ursa");
            var reequippedActor = Assert.Single(global::Game.GemSkillEffects(conn),
                g => global::Game.SkillsFor(conn).EffectId(g.Effect) == "SummonBeastialUrsa");
            Ready(conn);
            conn.PlayerMana = conn.Pools.Mana;
            uint reequippedMana = conn.PlayerMana;
            Assert.True(Cast(conn, reequippedActor.Id));
            Assert.Equal(reequippedMana - 30, conn.PlayerMana);
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, conn,
                conn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var reequippedActive = Assert.IsType<BeastCompanions.ActiveBeast>(conn.Beasts.Active);
            Assert.NotEqual(replacement.Id, reequippedActive.Id);
            Assert.NotSame(replacement.Source.Owner, reequippedActive.Source.Owner);
            Assert.True(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            lock (conn.Monsters) conn.Minions.Remove(reequippedActive.Id);
            Assert.False(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            Assert.Null(conn.Beasts.Active);
            Assert.DoesNotContain(conn.Minions.All, m => m.Kind == "Summoned Ursa");

            // Pending work from the real paid use cannot survive source removal/re-equip before the next minion step.
            var pending = NewConnection(character + "Pending");
            var pendingWorn = EquipYriel(pending);
            var pendingActor = Assert.Single(global::Game.GemSkillEffects(pending),
                g => global::Game.SkillsFor(pending).EffectId(g.Effect) == "SummonBeastialUrsa");
            var pendingInventory = (PlayerInventories)global::Game.ItemsOf(pending)!;
            uint pendingMana = pending.PlayerMana;
            Assert.True(Cast(pending, pendingActor.Id));
            Assert.Equal(pendingMana - 30, pending.PlayerMana);
            var pendingSourceOwner = Assert.IsType<BeastCompanions.PendingSummon>(pending.Beasts.Pending).Source.Owner;
            var pendingRemoved = pendingInventory.Remove(PlayerInventories.BodyArmour, pendingWorn.Id);
            Assert.NotNull(pendingRemoved);
            Assert.Equal(pendingWorn.Id, pendingRemoved!.Id);
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, pending, pendingInventory)
                .GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, pending).GetAwaiter().GetResult();
            Assert.True(pendingSourceOwner.Retired);
            pendingInventory.Put(PlayerInventories.BodyArmour, pendingRemoved!);
            Assert.Equal(pendingWorn.Id, Assert.Single(pendingInventory.Items(PlayerInventories.BodyArmour)).Id);
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, pending, pendingInventory)
                .GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, pending).GetAwaiter().GetResult();
            Assert.Null(pending.Beasts.Pending);
            Assert.Null(pending.Beasts.Active);
            Assert.DoesNotContain(pending.Minions.All, m => m.Kind == "Summoned Ursa");
            Assert.False(global::Game.ItemConditionFlags(pending, pending.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            var pendingReequippedActor = Assert.Single(global::Game.GemSkillEffects(pending),
                g => global::Game.SkillsFor(pending).EffectId(g.Effect) == "SummonBeastialUrsa");
            Ready(pending);
            pending.PlayerMana = pending.Pools.Mana;
            uint pendingFreshMana = pending.PlayerMana;
            Assert.True(Cast(pending, pendingReequippedActor.Id));
            Assert.Equal(pendingFreshMana - 30, pending.PlayerMana);
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, pending,
                pending.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var pendingActive = Assert.IsType<BeastCompanions.ActiveBeast>(pending.Beasts.Active);
            Assert.NotSame(pendingSourceOwner, pendingActive.Source.Owner);
            pending.PlayerLife = 0;
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, pending,
                pending.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            Assert.Null(pending.Beasts.Active);
            Assert.False(pending.Minions.Owns(pendingActive.Id));
            Assert.True(pendingActive.Monster.Dead);
            Assert.Equal(0u, pendingActive.Monster.Life);

            // Roster ownership alone is not enough: the registry must hold that exact active monster.
            var registryGap = NewConnection(character + "RegistryGap");
            EquipYriel(registryGap);
            var registryActor = Assert.Single(global::Game.GemSkillEffects(registryGap),
                g => global::Game.SkillsFor(registryGap).EffectId(g.Effect) == "SummonBeastialUrsa");
            Assert.True(Cast(registryGap, registryActor.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, registryGap,
                registryGap.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var orphan = Assert.IsType<BeastCompanions.ActiveBeast>(registryGap.Beasts.Active);
            lock (registryGap.Monsters) Assert.True(registryGap.Monsters.Remove(orphan.Id));
            Assert.True(registryGap.Minions.Owns(orphan.Id));
            global::Game.PlayerMinionsTick("test", lifecycleWire.Stream, lifecycleWire.Ciphers, registryGap,
                registryGap.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            Assert.Null(registryGap.Beasts.Active);
            Assert.False(registryGap.Minions.Owns(orphan.Id));
            Assert.True(orphan.Monster.Dead);
            Assert.Equal(0u, orphan.Monster.Life);
            Assert.Contains(registryGap.Minions.Bodies,
                body => body.Id == orphan.Id && ReferenceEquals(body.Monster, orphan.Monster));

            // The creation await rechecks the accepted generation and all ownership/lifetime predicates. Releasing an
            // old create after a lifecycle change must not attach an owned Ursa.
            ushort? blockedSourceActor = null;
            BeastCompanions.SourceGeneration? blockedPendingOwner = null;
            void BlockedPending(string name, Action<ClientState> invalidate, Action<ClientState>? restore = null)
            {
                var blocked = NewConnection(character + name.Replace(" ", ""));
                EquipYriel(blocked);
                var blockedActor = Assert.Single(global::Game.GemSkillEffects(blocked),
                    g => global::Game.SkillsFor(blocked).EffectId(g.Effect) == "SummonBeastialUrsa");
                Assert.True(CastWithAcceptedAction(blocked, blockedActor.Id));
                Assert.NotNull(blocked.Beasts.Pending);
                var outputGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(outputGate.Wait(0));
                var blockedSpawn = global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, blocked,
                    blocked.Clock.Elapsed.TotalSeconds);
                Assert.False(blockedSpawn.IsCompleted, $"{name}: the paid spawn did not reach its create output");
                invalidate(blocked);
                outputGate.Release();
                blockedSpawn.GetAwaiter().GetResult();
                Assert.Null(blocked.Beasts.Pending);
                Assert.Null(blocked.Beasts.Active);
                Assert.DoesNotContain(blocked.Minions.All, m => m.Kind == "Summoned Ursa");
                Assert.False(global::Game.ItemConditionFlags(blocked, blocked.Clock.Elapsed.TotalSeconds)
                    ["_if_you_have_beast_minion"]);
                restore?.Invoke(blocked);
            }

            BlockedPending("source removal", blocked =>
            {
                var inventory = (PlayerInventories)global::Game.ItemsOf(blocked)!;
                var held = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
                blockedPendingOwner = Assert.IsType<BeastCompanions.PendingSummon>(blocked.Beasts.Pending).Source.Owner;
                var removed = inventory.Remove(PlayerInventories.BodyArmour, held.Id);
                Assert.NotNull(removed);
                Assert.Equal(held.Id, removed!.Id);
                global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, blocked, inventory)
                    .GetAwaiter().GetResult();
                global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, blocked)
                    .GetAwaiter().GetResult();
                Assert.True(blockedPendingOwner.Retired);
                inventory.Put(PlayerInventories.BodyArmour, removed!);
                Assert.Equal(held.Id, Assert.Single(inventory.Items(PlayerInventories.BodyArmour)).Id);
                global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, blocked, inventory)
                    .GetAwaiter().GetResult();
                global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, blocked)
                    .GetAwaiter().GetResult();
                Assert.Null(blocked.Beasts.Pending);
                Assert.Null(blocked.Beasts.Active);
                Assert.DoesNotContain(blocked.Minions.All, m => m.Kind == "Summoned Ursa");
                Assert.DoesNotContain(blocked.Beasts.SourceGenerations.Values,
                    owner => ReferenceEquals(owner, blockedPendingOwner));
                blockedSourceActor = Assert.Single(global::Game.GemSkillEffects(blocked),
                    g => global::Game.SkillsFor(blocked).EffectId(g.Effect) == "SummonBeastialUrsa").Id;
            }, blocked =>
            {
                Ready(blocked);
                blocked.PlayerMana = blocked.Pools.Mana;
                Assert.True(Cast(blocked, blockedSourceActor!.Value));
                global::Game.PlayerMinionsTick("test", lifecycleWire.Stream, lifecycleWire.Ciphers, blocked,
                    blocked.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
                var fresh = Assert.IsType<BeastCompanions.ActiveBeast>(blocked.Beasts.Active);
                Assert.NotSame(blockedPendingOwner, fresh.Source.Owner);
            });
            BlockedPending("later action", blocked => { lock (blocked.Monsters) blocked.ActionCount++; });
            BlockedPending("retired", blocked => blocked.RageRetired = true, blocked => blocked.RageRetired = false);
            {
                var melee = NewConnection(character + "FreshMelee");
                EquipYriel(melee, projectileAilments: true);
                var meleeActor = Assert.Single(global::Game.GemSkillEffects(melee),
                    g => global::Game.SkillsFor(melee).EffectId(g.Effect) == "SummonBeastialUrsa");
                Ready(melee);
                Assert.True(Cast(melee, meleeActor.Id));
                global::Game.PlayerMinionsTick("test", lifecycleWire.Stream, lifecycleWire.Ciphers, melee,
                    melee.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
                Assert.NotNull(melee.Beasts.Active);
                var meleeStats = global::Game.FinalPlayerStats(melee).ToDictionary(s => s.Item1, s => s.Item2);
                foreach (var qualified in new[]
                         {
                             "projectile_attacks_chance_to_bleed_on_hit_%_if_you_have_beast_minion",
                             "projectile_attacks_chance_to_poison_on_hit_%_if_you_have_beast_minion",
                             "projectile_attacks_chance_to_maim_on_hit_%_if_you_have_beast_minion",
                         })
                    Assert.Equal(100, meleeStats.GetValueOrDefault(Program.Stats.Key(qualified)!.Value));
                foreach (var alias in new[]
                         {
                             "virtual_total_main_hand_chance_to_bleed_on_hit_%",
                             "virtual_total_main_hand_chance_to_poison_on_hit_%",
                             "main_hand_maim_on_hit_%",
                         })
                    Assert.Equal(0, meleeStats.GetValueOrDefault(Program.Stats.Key(alias)!.Value));
                var meleeDefaultRows = Assert.Single(global::Game.PlayerSkills(melee),
                    skill => skill.Id == SkillCatalog.FirstSkillId).Stats!
                    .ToDictionary(stat => stat.Key, stat => stat.Value);
                foreach (var alias in new[]
                         {
                             "virtual_total_main_hand_chance_to_bleed_on_hit_%",
                             "virtual_total_main_hand_chance_to_poison_on_hit_%",
                             "main_hand_maim_on_hit_%",
                         })
                    Assert.Equal(0, meleeDefaultRows.GetValueOrDefault(Program.Stats.Key(alias)!.Value));
            }
            var oldLifetime = global::Game.CombatLifetime;
            var blockedReplacementLifetime = new ModuleLifetime("replacement-beast-pending");
            try
            {
                BlockedPending("module", blocked =>
                {
                    global::Game.CombatLifetime = blockedReplacementLifetime;
                    Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(blockedReplacementLifetime));
                    global::Game.PlayerMinionsTick("test", lifecycleWire.Stream, lifecycleWire.Ciphers, blocked,
                        blocked.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
                    Assert.Null(blocked.Beasts.Pending);
                }, blocked =>
                {
                    global::Game.CombatLifetime = oldLifetime;
                    Program.SetGameLogic(lifetimeLogic);
                });
            }
            finally
            {
                global::Game.CombatLifetime = oldLifetime;
                Program.SetGameLogic(lifetimeLogic);
                blockedReplacementLifetime.Stop(ex => throw ex);
            }
            BlockedPending("closed", blocked => blocked.Closed = true);
            BlockedPending("leaving", blocked => blocked.LeavingArea = true);

            // The real public pickup path removes the worn source and then waits behind the same output gate as an old
            // paid spawn. Retiring the source must happen before that wait, and the released spawn must not attach an Ursa.
            var pickupBoundary = NewConnection(character + "PickupBoundary");
            var pickupWorn = EquipYriel(pickupBoundary);
            var pickupActor = Assert.Single(global::Game.GemSkillEffects(pickupBoundary),
                g => global::Game.SkillsFor(pickupBoundary).EffectId(g.Effect) == "SummonBeastialUrsa");
            Assert.True(CastWithAcceptedAction(pickupBoundary, pickupActor.Id));
            var pickupOwner = Assert.IsType<BeastCompanions.PendingSummon>(pickupBoundary.Beasts.Pending).Source.Owner;
            var pickupInventory = (PlayerInventories)global::Game.ItemsOf(pickupBoundary)!;
            uint bodyInventory = PlayerInventories.InventoryId(PlayerInventories.BodyArmour);
            Assert.Equal(2u, bodyInventory);
            var pickupGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            Assert.True(pickupGate.Wait(0));
            var blockedPickupSpawn = global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, pickupBoundary,
                pickupBoundary.Clock.Elapsed.TotalSeconds);
            Assert.False(blockedPickupSpawn.IsCompleted, "the paid spawn did not reach the shared output gate");
            var pickup = global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, pickupBoundary,
                new InstanceItems.PickUpItem(bodyInventory, pickupWorn.Id, 3, 0, 0));
            Assert.False(pickup.IsCompleted, "public pickup did not suspend at its first output");
            Assert.Empty(pickupInventory.Items(PlayerInventories.BodyArmour));
            Assert.Equal(pickupWorn.Id, Assert.Single(pickupInventory.Items(PlayerInventories.Cursor)).Id);
            Assert.Null(pickupBoundary.Beasts.Pending);
            Assert.True(pickupOwner.Retired);
            pickupGate.Release();
            blockedPickupSpawn.GetAwaiter().GetResult();
            pickup.GetAwaiter().GetResult();
            Assert.Null(pickupBoundary.Beasts.Active);
            Assert.DoesNotContain(pickupBoundary.Minions.All, m => m.Kind == "Summoned Ursa");

            // Restore the exact same InventoryItem through the public placement path. It becomes a fresh source generation,
            // and a newly paid use must create the only Ursa.
            global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, pickupBoundary,
                new InstanceItems.PlaceItem(bodyInventory, 0, 0, 0, 3, 0, 0)).GetAwaiter().GetResult();
            Assert.Equal(pickupWorn.Id, Assert.Single(pickupInventory.Items(PlayerInventories.BodyArmour)).Id);
            var restoredActor = Assert.Single(global::Game.GemSkillEffects(pickupBoundary),
                g => global::Game.SkillsFor(pickupBoundary).EffectId(g.Effect) == "SummonBeastialUrsa");
            Ready(pickupBoundary);
            pickupBoundary.PlayerMana = pickupBoundary.Pools.Mana;
            uint restoredMana = pickupBoundary.PlayerMana;
            Assert.True(Cast(pickupBoundary, restoredActor.Id));
            Assert.Equal(restoredMana - 30, pickupBoundary.PlayerMana);
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, pickupBoundary,
                pickupBoundary.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var restoredUrsa = Assert.IsType<BeastCompanions.ActiveBeast>(pickupBoundary.Beasts.Active);
            Assert.NotSame(pickupOwner, restoredUrsa.Source.Owner);
            Assert.True(pickupBoundary.Minions.Owns(restoredUrsa.Id));

            // The same worn-source owner creates the actual Rhoa variety. It is a real friendly, fighting, indefinitely
            // owned entity with the grant's level/life stats, not a dummy boolean or an Ursa substitution.
            var rhoaConn = NewConnection(character + "Rhoa");
            EquipYriel(rhoaConn, rhoa: true);
            var rhoaGrant = Assert.Single(global::Game.AllWornGrantedSkillsOf(rhoaConn),
                g => g.EffectId == "SummonBeastialRhoa");
            Assert.Equal((50727, 8452, 20, true, false),
                (rhoaGrant.EffectsPerLevelRow, rhoaGrant.Effect, rhoaGrant.Level,
                    rhoaGrant.ManuallyCast, rhoaGrant.Support));
            Assert.Contains(global::Game.WornGrantedSkillsOf(rhoaConn), g => g.Identity == rhoaGrant.Identity);
            var rhoaActor = Assert.Single(global::Game.GemSkillEffects(rhoaConn),
                g => global::Game.SkillsFor(rhoaConn).EffectId(g.Effect) == "SummonBeastialRhoa");
            var rhoaSupported = global::Game.SupportedSkill(rhoaConn, rhoaActor.Id);
            Assert.Equal((8452, 20), (rhoaSupported.Effect, rhoaSupported.Level));
            Assert.Equal(30, rhoaSupported.Stats[Program.Stats.Key("mana_cost")!.Value]);
            Assert.Equal(70, rhoaSupported.Stats[Program.Stats.Key("base_display_minion_actor_level")!.Value]);
            Assert.Equal(1, rhoaSupported.Stats[Program.Stats.Key("base_number_of_beast_minions_allowed")!.Value]);
            Assert.Equal(30, rhoaSupported.Stats[Program.Stats.Key("minion_maximum_life_+%")!.Value]);
            var rhoaCooldown = global::Game.CooldownFor(rhoaConn, rhoaSupported.Effect,
                rhoaSupported.Level, rhoaSupported.Quality);
            Assert.NotNull(rhoaCooldown);
            Assert.Equal(5000, rhoaCooldown!.Value.BaseMilliseconds);
            Assert.Equal(36, global::Game.SkillsFor(rhoaConn).CooldownGroup(rhoaSupported.Effect,
                rhoaSupported.Level));
            Assert.False(global::Game.ItemConditionFlags(rhoaConn, rhoaConn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            uint rhoaMana = rhoaConn.PlayerMana;
            Assert.True(Cast(rhoaConn, rhoaActor.Id));
            Assert.Equal(rhoaMana - 30, rhoaConn.PlayerMana);
            Assert.NotNull(rhoaConn.Beasts.Pending);
            Assert.False(Begin(rhoaConn, rhoaActor.Id, out _, out _));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, rhoaConn,
                rhoaConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var rhoaActive = Assert.IsType<BeastCompanions.ActiveBeast>(rhoaConn.Beasts.Active);
            Assert.Equal(("Metadata/Monsters/Rhoas/RhoaUniqueSummoned", "Summoned Rhoa"),
                (rhoaActive.Variety, rhoaActive.MinionKind));
            Assert.Equal(465, global::Game.Monsters.Find(rhoaActive.Variety)!.Row);
            Assert.Equal((byte)70, rhoaActive.Monster.Level);
            Assert.Equal(30, rhoaActive.Monster.Mods.Effects["maximum_life_+%"]);
            Assert.True(rhoaConn.Minions.Owns(rhoaActive.Id));
            Assert.True(rhoaConn.Minions.Fights(rhoaActive.Id));
            var ownedRhoa = Assert.Single(rhoaConn.Minions.All, m => m.Id == rhoaActive.Id);
            Assert.Equal(double.PositiveInfinity, ownedRhoa.Until);
            Assert.Equal("Summoned Rhoa", ownedRhoa.Kind);
            Assert.True(global::Game.MonsterActionCurrent(rhoaConn, rhoaActive.Id,
                rhoaActive.Monster, rhoaActive.Monster.ActionGeneration));
            Assert.True(global::Game.ItemConditionFlags(rhoaConn, rhoaConn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);

            var rhoaMoves = new List<uint>();
            var previousRhoaOutgoing = Program.Outgoing;
            Program.Outgoing = (ciphers, bytes) =>
            {
                previousRhoaOutgoing?.Invoke(ciphers, bytes);
                if (ReferenceEquals(ciphers, wire.Ciphers) && bytes.Length >= 6
                    && (bytes[0] << 8 | bytes[1]) == InstanceProtocol.ActionStartedId)
                    rhoaMoves.Add(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2, 4)));
            };
            try
            {
                global::Game.StepMonsterAi("test", wire.Stream, wire.Ciphers, rhoaConn, new MonsterAiCadence())
                    .GetAwaiter().GetResult();
            }
            finally
            {
                Program.Outgoing = previousRhoaOutgoing;
            }
            Assert.Contains(rhoaActive.Id, rhoaMoves);

            lock (rhoaConn.Monsters)
            {
                const uint targetId = 0x16a;
                var at = rhoaActive.Monster.Walk.Position(rhoaConn.Clock.Elapsed.TotalSeconds);
                var target = new SpawnedMonster("Rhoa hit target", new(at.X, at.Y, 0), 0, 0, 100_000)
                {
                    Hostile = true, Life = 100_000, NoRewards = true
                };
                rhoaConn.Monsters[targetId] = target;
                uint lifeBefore = target.Life;
                var landed = global::Game.MinionHitLocked(rhoaConn, rhoaActive.Id, rhoaActive.Monster,
                    new MinionTarget(targetId, target, at, true, at),
                    rhoaConn.Clock.Elapsed.TotalSeconds, new Random(1601));
                Assert.NotNull(landed);
                Assert.True(target.Life < lifeBefore);
                rhoaConn.Monsters.Remove(targetId);
            }

            // A same-id roster entry does not stand in for the sourced Rhoa when its object is not the registered one.
            // The exact-id-and-object gate retires the real active, while cleanup leaves the impostor untouched.
            var rosterImpostor = new SpawnedMonster("Rhoa roster impostor",
                new(rhoaActive.Monster.Walk.Position(rhoaConn.Clock.Elapsed.TotalSeconds).X,
                    rhoaActive.Monster.Walk.Position(rhoaConn.Clock.Elapsed.TotalSeconds).Y, 0), 0, 0, 1_000)
            {
                Hostile = true, Life = 1_000, NoRewards = true
            };
            lock (rhoaConn.Monsters)
                rhoaConn.Minions.Add(new(rhoaActive.Id, rosterImpostor, rhoaActive.Skill, rhoaActive.MinionKind)
                    { Fights = true });
            Assert.False(global::Game.ItemConditionFlags(rhoaConn, rhoaConn.Clock.Elapsed.TotalSeconds)
                ["_if_you_have_beast_minion"]);
            Assert.Null(rhoaConn.Beasts.Active);
            Assert.True(rhoaActive.Monster.Dead);
            Assert.Equal(0u, rhoaActive.Monster.Life);
            Assert.True(rhoaConn.Minions.Owns(rhoaActive.Id, rosterImpostor));
            Assert.Same(rhoaActive.Monster, rhoaConn.Monsters[rhoaActive.Id]);
            lock (rhoaConn.Monsters)
            {
                rhoaConn.Minions.Remove(rhoaActive.Id);
                rhoaConn.Monsters.Remove(rhoaActive.Id);
            }

            // The third real Beast grant owns the catalogue's summoned Cobra. Its actual
            // monster_projectile_variation 1 -> MonsterProjectileAttack 1 -> Projectile 31 path must survive into the
            // spawned attack, with the variety's real 30% physical-to-chaos special mod conserved.
            var snakeConn = NewConnection(character + "Snake");
            var snakeWorn = EquipYriel(snakeConn, snake: true);
            var snakeGrant = Assert.Single(global::Game.AllWornGrantedSkillsOf(snakeConn),
                g => g.EffectId == "SummonBeastialSnake");
            Assert.Equal((50729, 8454, 20, true, false),
                (snakeGrant.EffectsPerLevelRow, snakeGrant.Effect, snakeGrant.Level,
                    snakeGrant.ManuallyCast, snakeGrant.Support));
            var snakeActor = Assert.Single(global::Game.GemSkillEffects(snakeConn),
                g => global::Game.SkillsFor(snakeConn).EffectId(g.Effect) == "SummonBeastialSnake");
            var snakeSupported = global::Game.SupportedSkill(snakeConn, snakeActor.Id);
            Assert.Equal((8454, 20), (snakeSupported.Effect, snakeSupported.Level));
            Assert.Equal(30, snakeSupported.Stats[Program.Stats.Key("mana_cost")!.Value]);
            Assert.Equal(70, snakeSupported.Stats[Program.Stats.Key("base_display_minion_actor_level")!.Value]);
            Assert.Equal(1, snakeSupported.Stats[Program.Stats.Key("base_number_of_beast_minions_allowed")!.Value]);
            Assert.Equal(30, snakeSupported.Stats[Program.Stats.Key("minion_maximum_life_+%")!.Value]);
            Assert.Equal(36, global::Game.SkillsFor(snakeConn).CooldownGroup(
                snakeSupported.Effect, snakeSupported.Level));
            uint snakeMana = snakeConn.PlayerMana;
            Assert.True(Cast(snakeConn, snakeActor.Id));
            Assert.Equal(snakeMana - 30, snakeConn.PlayerMana);
            Assert.False(Begin(snakeConn, snakeActor.Id, out _, out _));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, snakeConn,
                snakeConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var snakeActive = Assert.IsType<BeastCompanions.ActiveBeast>(snakeConn.Beasts.Active);
            Assert.Equal(("Metadata/Monsters/Snake/SnakeSpitUniqueSummoned", "Summoned Cobra"),
                (snakeActive.Variety, snakeActive.MinionKind));
            Assert.Equal(944, global::Game.Monsters.Find(snakeActive.Variety)!.Row);
            Assert.Equal((byte)70, snakeActive.Monster.Level);
            Assert.False(snakeActive.Monster.Hostile);
            Assert.True(snakeConn.Minions.Owns(snakeActive.Id));
            Assert.True(snakeConn.Minions.Fights(snakeActive.Id));
            Assert.Equal(double.PositiveInfinity,
                Assert.Single(snakeConn.Minions.All, m => m.Id == snakeActive.Id).Until);
            var snakeAttack = Assert.IsType<MonsterAttack>(snakeActive.Monster.Attack);
            Assert.True(snakeAttack.Ranged);
            var snakeProjectile = Assert.IsType<SkillCatalog.MonsterProjectileInfo>(snakeAttack.Projectile);
            Assert.Equal(("Metadata/Projectiles/Snake", 1800, false, 2d),
                (snakeProjectile.Metadata, snakeProjectile.Speed, snakeProjectile.Lobbed,
                    snakeProjectile.CollisionRadius));
            Assert.Equal("SpellProjectile", snakeProjectile.AnimationKey);
            Assert.Equal(0.566, snakeAttack.Release!.Fraction, 3);
            var snakeDamage = snakeAttack.Damage;
            Assert.True(snakeDamage[DamageType.Physical].Min > 0);
            Assert.True(snakeDamage[DamageType.Chaos].Min > 0);
            double snakeChaosShare = snakeDamage[DamageType.Chaos].Min
                / snakeDamage.MinTotal;
            Assert.InRange(snakeChaosShare, 0.28, 0.32);
            var snakeStats = snakeAttack.Stats().ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal((int)Math.Floor(snakeDamage[DamageType.Physical].Min), snakeStats[StatKeys.MainHandMinPhysical]);
            Assert.Equal((int)Math.Floor(snakeDamage[DamageType.Physical].Max), snakeStats[StatKeys.MainHandMaxPhysical]);
            Assert.Equal((int)Math.Floor(snakeDamage[DamageType.Chaos].Min), snakeStats[StatKeys.MainHandMinChaos]);
            Assert.Equal((int)Math.Floor(snakeDamage[DamageType.Chaos].Max), snakeStats[StatKeys.MainHandMaxChaos]);
            Assert.Equal((int)Math.Floor(snakeDamage.MinTotal), snakeStats[StatKeys.MainHandMinTotal]);
            Assert.Equal((int)Math.Floor(snakeDamage.MaxTotal), snakeStats[StatKeys.MainHandMaxTotal]);

            async Task SwingOn(ClientState targetConn, PlayerMinions.Attack pending)
            {
                var start = global::Game.ProjectileWorldTime(targetConn);
                var schedule = new AdvancingOneStepSchedule(start, () => { });
                var updates = new PlayerUpdatesState(lifetime, schedule);
                global::Game.AreaUpdateContext.Value = updates;
                try
                {
                    await global::Game.MinionSwing("test", wire.Stream, wire.Ciphers, targetConn, pending);
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = null;
                }
            }
            void Swing(PlayerMinions.Attack pending) => SwingOn(snakeConn, pending).GetAwaiter().GetResult();

            PlayerMinions.Attack AcceptedAttack(uint targetId, SpawnedMonster target)
            {
                double now = global::Game.ProjectileWorldTime(snakeConn);
                var from = snakeActive.Monster.Walk.Position(now);
                var attack = new PlayerMinions.Attack(snakeActive.Id, snakeActive.Monster, from, targetId, target,
                    target.Walk.Position(now), now + .01);
                lock (snakeConn.Monsters) snakeConn.Minions.Attacks[snakeActive.Id] = attack;
                return attack;
            }

            SpawnedMonster Target(uint id, int dx, bool hostile = true)
            {
                var at = snakeActive.Monster.Walk.Position(global::Game.ProjectileWorldTime(snakeConn));
                var target = new SpawnedMonster($"Snake target 0x{id:x}", new(at.X + dx, at.Y, 0), 0, 0, 50_000)
                {
                    Hostile = hostile, Life = 50_000, NoRewards = true
                };
                lock (snakeConn.Monsters) snakeConn.Monsters[id] = target;
                return target;
            }

            const uint snakeTargetId = 0x17a1;
            var snakeTarget = Target(snakeTargetId, 12);
            var friendlyTargetId = 0x17a2u;
            var snakeFriendly = Target(friendlyTargetId, 6, hostile: false);
            uint playerLife = snakeConn.PlayerLife;
            int minionOutputs = snakeConn.Minions.OutputQueue.Count;
            var firstSnakeAttack = AcceptedAttack(snakeTargetId, snakeTarget);
            Swing(firstSnakeAttack);
            var snakeSession = global::Game.ProjectilesFor("test", wire.Stream, wire.Ciphers, snakeConn);
            Assert.Single(snakeSession.Runtime.Projectiles);
            Assert.False(snakeConn.Minions.Attacks.ContainsKey(snakeActive.Id));
            Assert.Equal(50_000u, snakeTarget.Life);
            Assert.Equal(50_000u, snakeFriendly.Life);
            Assert.Equal(playerLife, snakeConn.PlayerLife);

            // A later swing advances the actor action generation. The released shot keeps only its source/object identity.
            lock (snakeConn.Monsters) snakeActive.Monster.ActionGeneration++;
            var snakeGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            snakeGate.WaitAsync().GetAwaiter().GetResult();
            try
            {
                Assert.Equal(0, snakeGate.CurrentCount);
                snakeSession.AdvanceTo(snakeSession.Runtime.Now + ProjectileRuntime.Time(.25));
                Assert.True(snakeTarget.Life < 50_000);
                Assert.Equal(50_000u, snakeFriendly.Life);
                Assert.Equal(playerLife, snakeConn.PlayerLife);
                Assert.True(snakeConn.Minions.OutputQueue.Count > minionOutputs);
                Assert.Contains(snakeConn.Minions.OutputQueue,
                    output => output.Log?.Contains("projectile hit on monster", StringComparison.Ordinal) == true);
            }
            finally
            {
                snakeGate.Release();
            }
            Assert.Empty(snakeSession.Runtime.Projectiles);
            lock (snakeConn.Monsters)
            {
                snakeConn.Monsters.Remove(snakeTargetId);
                snakeConn.Monsters.Remove(friendlyTargetId);
            }

            // The helper's own accepted-token path releases once, then consumes the token without relying on MinionSwing.
            const uint directTargetId = 0x17a7;
            var directTarget = Target(directTargetId, 12);
            var frozenAttack = snakeAttack with
            {
                Damage = DamagePacket.Of(DamageType.Physical, 1_000, 1_000)
            };
            snakeActive.Monster.Attack = frozenAttack;
            var directAttack = AcceptedAttack(directTargetId, directTarget);
            Assert.True(global::Game.ReleaseMinionProjectile("test", wire.Stream, wire.Ciphers, snakeConn,
                directAttack, snakeProjectile, global::Game.ProjectileWorldTime(snakeConn)));
            Assert.False(snakeConn.Minions.Attacks.ContainsKey(snakeActive.Id));
            snakeActive.Monster.Attack = frozenAttack with
            {
                Damage = DamagePacket.Of(DamageType.Physical, 1, 1)
            };
            snakeSession.AdvanceTo(snakeSession.Runtime.Now + ProjectileRuntime.Time(.25));
            Assert.True(50_000u - directTarget.Life >= 500u);
            snakeActive.Monster.Attack = snakeAttack;
            lock (snakeConn.Monsters) snakeConn.Monsters.Remove(directTargetId);

            // A recycled object under the same id is a new actor generation and cannot inherit the old flight's contact.
            const uint recycledTargetId = 0x17a8;
            var recycledOriginal = Target(recycledTargetId, 12);
            var recycledAttack = AcceptedAttack(recycledTargetId, recycledOriginal);
            Swing(recycledAttack);
            var recycledProjectile = Assert.Single(snakeSession.Runtime.Projectiles);
            var oldRecycledActorKey = snakeSession.ActorKey(recycledTargetId, recycledOriginal);
            var recycledAt = recycledOriginal.Walk.Position(global::Game.ProjectileWorldTime(snakeConn));
            var recycledReplacement = new SpawnedMonster("Snake recycled target",
                new PlayerWalk(recycledAt.X, recycledAt.Y + 20, 0), 0, 0, 50_000)
                { Hostile = true, Life = 50_000, NoRewards = true };
            lock (snakeConn.Monsters) snakeConn.Monsters[recycledTargetId] = recycledReplacement;
            var staleContact = new ProjectileDamage(recycledProjectile, oldRecycledActorKey, snakeSession.Runtime.Now,
                "recycled", default);
            var staleShot = Assert.IsType<MinionProjectileOffense>(recycledProjectile.Use.Payload);
            Assert.Equal(snakeActive.Id, staleShot.MinionId);
            Assert.Same(snakeActive.Monster, staleShot.Monster);
            Assert.True(staleShot.Accuracy > 0);
            Assert.Equal(ProjectileHitResult.Miss,
                Game.MinionProjectileHit(snakeSession, staleContact, staleShot));
            snakeSession.AdvanceTo(snakeSession.Runtime.Now + ProjectileRuntime.Time(.25));
            Assert.Equal(50_000u, recycledReplacement.Life);
            lock (snakeConn.Monsters) snakeConn.Monsters.Remove(recycledTargetId);

            // The shared terrain trace, not a second projectile path, stops a minion shot at a wall.
            const uint wallTargetId = 0x17a3;
            var wallTarget = Target(wallTargetId, 12);
            var wallAt = snakeActive.Monster.Walk.Position(global::Game.ProjectileWorldTime(snakeConn));
            snakeConn.ProjectileWalls.Add((wallAt.X + 6, wallAt.Y));
            var wallAttack = AcceptedAttack(wallTargetId, wallTarget);
            Swing(wallAttack);
            snakeSession.AdvanceTo(snakeSession.Runtime.Now + ProjectileRuntime.Time(.25));
            Assert.Equal(50_000u, wallTarget.Life);
            snakeConn.ProjectileWalls.Remove((wallAt.X + 6, wallAt.Y));
            lock (snakeConn.Monsters) snakeConn.Monsters.Remove(wallTargetId);

            // Flight contacts use the actor's current position: stepping aside before the contact evades the straight shot.
            const uint movingTargetId = 0x17a4;
            var movingTarget = Target(movingTargetId, 12);
            var movingAttack = AcceptedAttack(movingTargetId, movingTarget);
            Swing(movingAttack);
            var movingAt = movingTarget.Walk.Position(global::Game.ProjectileWorldTime(snakeConn));
            movingTarget.Walk = new PlayerWalk(movingAt.X, movingAt.Y + 20, 0);
            snakeSession.AdvanceTo(snakeSession.Runtime.Now + ProjectileRuntime.Time(.25));
            Assert.Equal(50_000u, movingTarget.Life);
            lock (snakeConn.Monsters) snakeConn.Monsters.Remove(movingTargetId);

            // A released shot is not cancelled by a later action token, but source retirement invalidates its contact.
            const uint retiredFlightTargetId = 0x17a5;
            var retiredFlightTarget = Target(retiredFlightTargetId, 12);
            var retiredFlightAttack = AcceptedAttack(retiredFlightTargetId, retiredFlightTarget);
            Swing(retiredFlightAttack);
            var snakeInventory = (PlayerInventories)global::Game.ItemsOf(snakeConn)!;
            Assert.NotNull(snakeInventory.Remove(PlayerInventories.BodyArmour, snakeWorn.Id));
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, snakeConn, snakeInventory)
                .GetAwaiter().GetResult();
            Assert.Null(snakeConn.Beasts.Active);
            snakeSession.AdvanceTo(snakeSession.Runtime.Now + ProjectileRuntime.Time(.25));
            Assert.Equal(50_000u, retiredFlightTarget.Life);
            lock (snakeConn.Monsters) snakeConn.Monsters.Remove(retiredFlightTargetId);

            // Before release, the same source/roster/attack gates refuse stale work and create no projectile.
            var staleSnake = NewConnection(character + "SnakeStale");
            var staleWorn = EquipYriel(staleSnake, snake: true);
            var staleActor = Assert.Single(global::Game.GemSkillEffects(staleSnake),
                g => global::Game.SkillsFor(staleSnake).EffectId(g.Effect) == "SummonBeastialSnake");
            Assert.True(Cast(staleSnake, staleActor.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, staleSnake,
                staleSnake.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var staleActive = Assert.IsType<BeastCompanions.ActiveBeast>(staleSnake.Beasts.Active);
            const uint staleTargetId = 0x17a6;
            var staleAt = staleActive.Monster.Walk.Position(global::Game.ProjectileWorldTime(staleSnake));
            var staleTarget = new SpawnedMonster("Snake stale target", new(staleAt.X + 12, staleAt.Y, 0),
                0, 0, 50_000) { Hostile = true, Life = 50_000, NoRewards = true };
            lock (staleSnake.Monsters) staleSnake.Monsters[staleTargetId] = staleTarget;
            var staleAttack = new PlayerMinions.Attack(staleActive.Id, staleActive.Monster, staleAt, staleTargetId,
                staleTarget, staleTarget.Walk.Position(global::Game.ProjectileWorldTime(staleSnake)),
                global::Game.ProjectileWorldTime(staleSnake) + .01);
            lock (staleSnake.Monsters) staleSnake.Minions.Attacks[staleActive.Id] = staleAttack;
            var staleInventory = (PlayerInventories)global::Game.ItemsOf(staleSnake)!;
            Assert.NotNull(staleInventory.Remove(PlayerInventories.BodyArmour, staleWorn.Id));
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, staleSnake, staleInventory)
                .GetAwaiter().GetResult();
            SwingOn(staleSnake, staleAttack).GetAwaiter().GetResult();
            Assert.False(staleSnake.Minions.Attacks.ContainsKey(staleActive.Id));
            Assert.Equal(50_000u, staleTarget.Life);

            // Replacing the same worn host's exact grant retires the Ursa generation. The stale Ursa actor is refused
            // before payment, while Rhoa remains behind the shared group-36 cooldown already spent by the Ursa.
            var switchConn = NewConnection(character + "GrantSwitch");
            var switchWorn = EquipYriel(switchConn);
            var switchUrsa = Assert.Single(global::Game.GemSkillEffects(switchConn),
                g => global::Game.SkillsFor(switchConn).EffectId(g.Effect) == "SummonBeastialUrsa");
            Assert.True(Cast(switchConn, switchUrsa.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, switchConn,
                switchConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var switchUrsaActive = Assert.IsType<BeastCompanions.ActiveBeast>(switchConn.Beasts.Active);
            var switchUrsaOwner = switchUrsaActive.Source.Owner;
            var switchInventory = (PlayerInventories)global::Game.ItemsOf(switchConn)!;
            var switchRemoved = switchInventory.Remove(PlayerInventories.BodyArmour, switchWorn.Id);
            Assert.NotNull(switchRemoved);
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, switchConn,
                switchInventory).GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, switchConn)
                .GetAwaiter().GetResult();
            Assert.True(switchUrsaOwner.Retired);
            Assert.False(switchConn.Minions.Owns(switchUrsaActive.Id));
            uint removedMana = switchConn.PlayerMana;
            int removedActions = switchConn.ActionCount;
            Assert.False(global::Game.TryBeginSkillCooldownUse(switchConn, switchUrsa.Id, switchUrsa.Effect,
                global::Game.CooldownFor(switchConn, switchUrsa.Effect, switchUrsa.Level, switchUrsa.Quality),
                instant: false, () => { }, out _));
            Assert.Equal((removedMana, removedActions), (switchConn.PlayerMana, switchConn.ActionCount));
            var rhoaSwitchWorn = switchRemoved! with
            {
                Item = YrielWithBeastGrant(switchRemoved.Item, rhoa: true)
            };
            switchInventory.Put(PlayerInventories.BodyArmour, rhoaSwitchWorn);
            global::Game.SendEquipment("test", lifecycleWire.Stream, lifecycleWire.Ciphers, switchConn,
                switchInventory).GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, switchConn)
                .GetAwaiter().GetResult();
            var switchedRhoa = Assert.Single(global::Game.GemSkillEffects(switchConn),
                g => global::Game.SkillsFor(switchConn).EffectId(g.Effect) == "SummonBeastialRhoa");
            Assert.Equal(global::Game.CooldownKey(switchConn, switchUrsa.Effect, switchUrsa.Level),
                global::Game.CooldownKey(switchConn, switchedRhoa.Effect, switchedRhoa.Level));
            uint switchMana = switchConn.PlayerMana;
            Assert.False(Begin(switchConn, switchedRhoa.Id, out _, out _));
            Assert.Equal(switchMana, switchConn.PlayerMana);
            Assert.Null(switchConn.Beasts.Pending);
            Assert.DoesNotContain(switchConn.Minions.All, m =>
                m.Kind is "Summoned Ursa" or "Summoned Rhoa");
            Ready(switchConn);
            switchConn.PlayerMana = switchConn.Pools.Mana;
            Assert.True(Cast(switchConn, switchedRhoa.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, switchConn,
                switchConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var switchedRhoaActive = Assert.IsType<BeastCompanions.ActiveBeast>(switchConn.Beasts.Active);
            Assert.Equal("Summoned Rhoa", switchedRhoaActive.MinionKind);
            Assert.NotSame(switchUrsaOwner, switchedRhoaActive.Source.Owner);
            var switchedOwner = switchedRhoaActive.Source.Owner;
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, switchConn)
                .GetAwaiter().GetResult();
            global::Game.SendSkills("test", lifecycleWire.Stream, lifecycleWire.Ciphers, switchConn)
                .GetAwaiter().GetResult();
            Assert.Same(switchedOwner,
                Assert.IsType<BeastCompanions.ActiveBeast>(switchConn.Beasts.Active).Source.Owner);
            Assert.True(switchUrsaOwner.Retired);

            // Cooldown and insufficient payment leave the current Rhoa untouched. A later accepted recast replaces it
            // at the shared beast cap, without creating a second kind.
            var currentBeforeRefusals = Assert.IsType<BeastCompanions.ActiveBeast>(switchConn.Beasts.Active);
            Assert.False(global::Game.TryBeginSkillCooldownUse(switchConn, switchedRhoa.Id, switchedRhoa.Effect,
                global::Game.CooldownFor(switchConn, switchedRhoa.Effect, switchedRhoa.Level, switchedRhoa.Quality),
                instant: false, () => { }, out _));
            Assert.Same(currentBeforeRefusals, switchConn.Beasts.Active);
            Ready(switchConn);
            switchConn.PlayerMana = 0;
            Assert.False(Cast(switchConn, switchedRhoa.Id));
            Assert.Same(currentBeforeRefusals, switchConn.Beasts.Active);
            Assert.Null(switchConn.Beasts.Pending);
            Assert.True(switchConn.Minions.Owns(currentBeforeRefusals.Id));
            Ready(switchConn);
            switchConn.PlayerMana = switchConn.Pools.Mana;
            Assert.True(Cast(switchConn, switchedRhoa.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, switchConn,
                switchConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var capReplacement = Assert.IsType<BeastCompanions.ActiveBeast>(switchConn.Beasts.Active);
            Assert.NotEqual(currentBeforeRefusals.Id, capReplacement.Id);
            Assert.Single(switchConn.Minions.All, m => m.Kind == "Summoned Rhoa");
            Assert.DoesNotContain(switchConn.Minions.All, m => m.Kind == "Summoned Ursa");
            Assert.False(switchConn.Minions.Owns(currentBeforeRefusals.Id));
            switchConn.PlayerLife = 0;
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, switchConn,
                switchConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            Assert.Null(switchConn.Beasts.Active);
            Assert.False(switchConn.Minions.Owns(capReplacement.Id));
            Assert.True(capReplacement.Source.Owner.Retired == false);

            // A real Rhoa create suspended at its output boundary is rejected by the public pickup before the first
            // await completes: no deferred Rhoa may attach after its worn source is already gone.
            var rhoaPickup = NewConnection(character + "RhoaPickupBoundary");
            var rhoaPickupWorn = EquipYriel(rhoaPickup, rhoa: true);
            var rhoaPickupActor = Assert.Single(global::Game.GemSkillEffects(rhoaPickup),
                g => global::Game.SkillsFor(rhoaPickup).EffectId(g.Effect) == "SummonBeastialRhoa");
            Assert.True(CastWithAcceptedAction(rhoaPickup, rhoaPickupActor.Id));
            var rhoaPickupOwner = Assert.IsType<BeastCompanions.PendingSummon>(rhoaPickup.Beasts.Pending)
                .Source.Owner;
            var rhoaPickupInventory = (PlayerInventories)global::Game.ItemsOf(rhoaPickup)!;
            var rhoaPickupGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            Assert.True(rhoaPickupGate.Wait(0));
            var blockedRhoaSpawn = global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers,
                rhoaPickup, rhoaPickup.Clock.Elapsed.TotalSeconds);
            Assert.False(blockedRhoaSpawn.IsCompleted, "the paid Rhoa spawn did not reach its create output");
            var rhoaPickupTask = global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, rhoaPickup,
                new InstanceItems.PickUpItem(bodyInventory, rhoaPickupWorn.Id, 3, 0, 0));
            Assert.False(rhoaPickupTask.IsCompleted, "public pickup did not suspend at its first output");
            Assert.Empty(rhoaPickupInventory.Items(PlayerInventories.BodyArmour));
            Assert.Equal(rhoaPickupWorn.Id,
                Assert.Single(rhoaPickupInventory.Items(PlayerInventories.Cursor)).Id);
            Assert.Null(rhoaPickup.Beasts.Pending);
            Assert.True(rhoaPickupOwner.Retired);
            rhoaPickupGate.Release();
            blockedRhoaSpawn.GetAwaiter().GetResult();
            rhoaPickupTask.GetAwaiter().GetResult();
            Assert.Null(rhoaPickup.Beasts.Active);
            Assert.DoesNotContain(rhoaPickup.Minions.All, m => m.Kind == "Summoned Rhoa");

            // Replacing the live module generation retires an already-owned active Ursa, not merely a blocked spawn.
            var moduleConn = NewConnection(character + "ModuleActive");
            EquipYriel(moduleConn);
            var moduleActor = Assert.Single(global::Game.GemSkillEffects(moduleConn),
                g => global::Game.SkillsFor(moduleConn).EffectId(g.Effect) == "SummonBeastialUrsa");
            Assert.True(Cast(moduleConn, moduleActor.Id));
            global::Game.PlayerMinionsTick("test", wire.Stream, wire.Ciphers, moduleConn,
                moduleConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
            var moduleActive = Assert.IsType<BeastCompanions.ActiveBeast>(moduleConn.Beasts.Active);
            var activeReplacementLifetime = new ModuleLifetime("replacement-beast-active");
            try
            {
                global::Game.CombatLifetime = activeReplacementLifetime;
                Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(activeReplacementLifetime));
                global::Game.PlayerMinionsTick("test", lifecycleWire.Stream, lifecycleWire.Ciphers, moduleConn,
                    moduleConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
                Assert.Null(moduleConn.Beasts.Active);
                Assert.False(moduleConn.Minions.Owns(moduleActive.Id));
                Assert.True(moduleActive.Monster.Dead);
                Assert.Equal(0u, moduleActive.Monster.Life);

                lock (moduleConn.Monsters)
                {
                    int body = moduleConn.Minions.Bodies.FindIndex(b =>
                        b.Id == moduleActive.Id && ReferenceEquals(b.Monster, moduleActive.Monster));
                    Assert.True(body >= 0);
                    moduleConn.Minions.Bodies[body] =
                        (moduleActive.Id, moduleActive.Monster, moduleConn.Clock.Elapsed.TotalSeconds - 1);
                }
                global::Game.PlayerMinionsTick("test", lifecycleWire.Stream, lifecycleWire.Ciphers, moduleConn,
                    moduleConn.Clock.Elapsed.TotalSeconds).GetAwaiter().GetResult();
                Assert.False(moduleConn.Monsters.ContainsKey(moduleActive.Id));
                Assert.DoesNotContain(moduleConn.Minions.Bodies, body => ReferenceEquals(body.Monster, moduleActive.Monster));
            }
            finally
            {
                global::Game.CombatLifetime = lifetime;
                Program.SetGameLogic(lifetimeLogic);
                activeReplacementLifetime.Stop(ex => throw ex);
            }

            // Resource, dead and paused failures do not create pending work; the item requirement gates native reuse.
            var refused = NewConnection(character + "Refused");
            var refusedWorn = EquipYriel(refused);
            var refusedActor = Assert.Single(global::Game.GemSkillEffects(refused),
                g => global::Game.SkillsFor(refused).EffectId(g.Effect) == "SummonBeastialUrsa");
            refused.PlayerMana = 0;
            Assert.False(Cast(refused, refusedActor.Id));
            Assert.Null(refused.Beasts.Pending);
            Ready(refused);
            refused.PlayerLife = 0;
            Assert.False(Begin(refused, refusedActor.Id, out _, out _));
            refused.PlayerLife = refused.Pools.Life;
            refused.Instance.World.Clock.Stop();
            Assert.False(Begin(refused, refusedActor.Id, out _, out _));
            refused.Instance.World.Clock.Start();
            refused.Pools = refused.Pools with { Level = 1 };
            global::Game.EquipmentStatSources(refused,
                ((PlayerInventories)global::Game.ItemsOf(refused)!).EquipmentSnapshot());
            global::Game.InactiveItems[refused.Character!] = [refusedWorn.Id];
            Assert.False(global::Game.TryBeginSkillCooldownUse(refused, refusedActor.Id, refusedActor.Effect,
                global::Game.CooldownFor(refused, refusedActor.Effect, refusedActor.Level, refusedActor.Quality),
                instant: false, () => { }, out _));
            Assert.Null(refused.Beasts.Pending);
        }
        finally
        {
            foreach (string character in characters)
            {
                PlayerInventories.Forget(character);
                global::Game.InactiveItems.TryRemove(character, out _);
            }
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    void Bonemeld_counts_live_non_vaal_owner_minions_in_final_pools()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-bonemeld-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var wire = new Wire();
        var conn = new ClientState(new(1, "bonemeld-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "BonemeldRuntime",
            Instance = new(),
            PlayerLife = 1000,
            PlayerMana = 1000,
            PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new GameServer.Core.Accounts.League("bonemeld-test", "Standard", false, false),
                new HashSet<string>(["Game"])),
        };
        try
        {
            int ownerFireSource = 85, ownerColdSource = 44, ownerLightningSource = 3, ownerChaosSource = -57;
            int minionFireResistance = 0;
            // Raw flat sources make every final defence rating observable, not just the increased percentage buckets.
            using var flatDefenceSources = GameEvents.PlayerStatSources.Add("Game", (sourceConn, sources) =>
            {
                if (!ReferenceEquals(sourceConn, conn)) return sources;
                foreach (string stat in new[] { "base_physical_damage_reduction_rating", "base_evasion_rating",
                             "base_maximum_energy_shield", "base_ward" })
                    sources[Program.Stats.Key(stat)!.Value] = 1000;
                uint ResistanceKey(string type) => Program.Stats.Key($"base_{type}_damage_resistance_%")!.Value;
                sources[ResistanceKey("fire")] = sources.GetValueOrDefault(ResistanceKey("fire")) + ownerFireSource;
                sources[ResistanceKey("cold")] = sources.GetValueOrDefault(ResistanceKey("cold")) + ownerColdSource;
                sources[ResistanceKey("lightning")] = sources.GetValueOrDefault(ResistanceKey("lightning")) + ownerLightningSource;
                sources[ResistanceKey("chaos")] = sources.GetValueOrDefault(ResistanceKey("chaos")) + ownerChaosSource;
                uint minionFire = Program.Stats.Key("minion_fire_damage_resistance_%")!.Value;
                sources[minionFire] = sources.GetValueOrDefault(minionFire) + minionFireResistance;
                return sources;
            });
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            conn.Clock.Stop();
            conn.Golems.Restored = true;
            conn.Summons.Restored = true;

            var itemCatalog = (ItemCatalog)global::Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            Item RingWithMod(string modId, ModSection section, int value, int randomSeed, bool synthesised = false)
            {
                var row = itemCatalog.Mods.ById(modId)!;
                return ItemCatalog.Create(itemCatalog.Find("Metadata/Items/Rings/Ring1")!, random: new Random(randomSeed)) with
                {
                    ItemLevel = 100, RequiredLevel = 1, Identified = true, Synthesised = synthesised,
                    Mods = [new ItemMod(section, row.Hash, [value])],
                };
            }
            var bonemeld = new UniqueCatalog(itemCatalog).Create("Bonemeld", new Random(504));
            Assert.NotNull(bonemeld);
            string ModId(ItemMod mod) => itemCatalog.Mods.ByHash(mod.Hash)?.Id ?? "";
            var resistanceMod = Assert.Single(bonemeld!.Mods,
                mod => ModId(mod) == "AllResistancesReducedPerActiveNonVaalSkillMinionUnique_1");
            var defenceMod = Assert.Single(bonemeld.Mods,
                mod => ModId(mod) == "GlobalDefensesIncreasedPerActiveNonVaalSkillMinionUnique_1");
            var inheritedResistanceMod = Assert.Single(bonemeld.Mods,
                mod => ModId(mod) == "MinionsGainPercentOfYourResistancesUnique_1");
            Assert.Equal(-2, resistanceMod.Values.Single());
            Assert.InRange(defenceMod.Values.Single(), 3, 4);
            Assert.Equal(50, inheritedResistanceMod.Values.Single());
            foreach (var old in inventory.Items(5)) inventory.Remove(5, old.Id);
            var wornBonemeld = inventory.Add(5, 0, 0, bonemeld);

            var golemGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
            var zombieGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemRaiseZombie")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            {
                Sockets = [new(SocketColour.White, golemGem), new(SocketColour.White, zombieGem)],
            };
            foreach (var old in inventory.Items(PlayerInventories.Weapon1))
                inventory.Remove(PlayerInventories.Weapon1, old.Id);
            foreach (var old in inventory.Items(PlayerInventories.Offhand1))
                inventory.Remove(PlayerInventories.Offhand1, old.Id);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.SendSkills("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();
            conn.Pools = global::Game.PoolsFor(conn, conn.Pools.Level);
            conn.PlayerLife = conn.Pools.Life;
            Assert.DoesNotContain(wornBonemeld.Id, conn.StatSourcesCache!.Inactive);

            (Dictionary<uint, int> Stats, CharacterStats.Pools Pools, long Count) Snapshot()
            {
                var stats = global::Game.FinalPlayerStats(conn).ToDictionary(p => p.Item1, p => p.Item2);
                var snapshot = conn.StatSourcesCache!;
                Assert.True(snapshot.Key.Amounts!.TryGetValue(StatConditions.ActiveNonVaalSkillMinionCount, out long count));
                return (stats, conn.Pools, count);
            }

            void TickSummonsAt(double now)
            {
                Assert.False(conn.Clock.IsRunning);
                conn.Clock.Start();
                try
                {
                    global::Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, now).GetAwaiter().GetResult();
                }
                finally
                {
                    conn.Clock.Stop();
                }
                Assert.False(conn.Clock.IsRunning);
                Assert.Equal(now + .5, conn.Summons.RefreshAt);
                Assert.All(conn.Summons.Fighters.Values, fighter =>
                {
                    Assert.Same(fighter.Monster, conn.Monsters.GetValueOrDefault(fighter.Id));
                    Assert.False(fighter.Monster.Dead);
                    Assert.True(fighter.Monster.Life > 0);
                });
            }

            var withZero = Snapshot();
            Assert.Equal(0, withZero.Count);
            Assert.True(withZero.Pools.Armour > 0);
            Assert.True(withZero.Pools.Evasion > 0);
            Assert.True(withZero.Pools.EnergyShield > 0);
            Assert.True(withZero.Pools.Ward > 0);
            var removedZero = inventory.Remove(5, wornBonemeld.Id);
            Assert.NotNull(removedZero);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            var withoutZero = Snapshot();
            Assert.Equal(0, withoutZero.Count);
            Assert.Equal(withoutZero.Stats[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value],
                withZero.Stats[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value]);
            uint defence = Program.Stats.Key("global_defences_+%")!.Value;
            Assert.Equal(withoutZero.Stats.GetValueOrDefault(defence), withZero.Stats.GetValueOrDefault(defence));
            Assert.Equal(withoutZero.Pools.ArmourPct, withZero.Pools.ArmourPct);
            inventory.Put(5, removedZero!);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.SendSkills("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();

            var skills = global::Game.SkillsFor(conn);
            var golemSource = Assert.Single(global::Game.GemSkillEffects(conn),
                g => skills.EffectId(g.Effect) == "SummonFlameGolem");
            var golemDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;
            uint golemId = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, golemSource.Id,
                golemDefinition, (100, 100)).GetAwaiter().GetResult();
            Assert.NotEqual(0u, golemId);
            var withOne = Snapshot();
            Assert.Equal(1, withOne.Count);

            var zombieSource = Assert.Single(global::Game.GemSkillEffects(conn),
                g => skills.EffectId(g.Effect) == "RaiseZombie");
            var corpseVariety = global::Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            const uint corpseId = 0x1000b22;
            var corpse = new SpawnedMonster("bonemeld zombie corpse", new(100, 100, 0), 70, 8, 0)
            {
                Hostile = true, Dead = true, Life = 0, Variety = corpseVariety, Level = 70,
            };
            conn.Monsters[corpseId] = corpse;
            uint zombieId = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource.Id,
                global::Game.SummonData.Find("RaiseZombie")!, (100, 100), corpseId).GetAwaiter().GetResult();
            Assert.NotEqual(0u, zombieId);
            var withTwo = Snapshot();
            Assert.Equal(2, withTwo.Count);
            Assert.Equal(75, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Fire),
                global::Game.MaxResistance(conn, DamageType.Fire)));
            Assert.Equal(40, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Cold),
                global::Game.MaxResistance(conn, DamageType.Cold)));
            Assert.Equal(-1, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Lightning),
                global::Game.MaxResistance(conn, DamageType.Lightning)));
            Assert.Equal(-61, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Chaos),
                global::Game.MaxResistance(conn, DamageType.Chaos)));

            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 1).GetAwaiter().GetResult();
            var inheritedGolem = conn.Golems.All[golemId];
            Assert.Equal(37, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));
            Assert.Equal(20, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(-1, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Lightning));
            Assert.Equal(-31, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos));
            TickSummonsAt(1.5);
            var inheritedFighter = conn.Summons.Fighters[zombieId];
            Assert.Equal(37, inheritedFighter.Monster.Mods.Effects.Resistance(DamageType.Fire));
            Assert.Equal(20, inheritedFighter.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(-1, inheritedFighter.Monster.Mods.Effects.Resistance(DamageType.Lightning));
            Assert.Equal(-31, inheritedFighter.Monster.Mods.Effects.Resistance(DamageType.Chaos));
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 1.5).GetAwaiter().GetResult();
            Assert.Equal(37, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));

            Assert.Empty(inventory.Items(6));
            var maximumFireRow = Mod("SynthesisImplicitMaxFireResist2");
            Assert.Equal((ushort)11402, maximumFireRow.Hash);
            Assert.Equal("base_maximum_fire_damage_resistance_%", Assert.Single(maximumFireRow.Stats).Id);
            var maximumFireRing = RingWithMod(maximumFireRow.Id, ModSection.Implicit, 3, 504, synthesised: true);
            var maximumFirePlacement = inventory.Add(6, 0, 0, maximumFireRing);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 2).GetAwaiter().GetResult();
            Assert.Same(inheritedGolem, conn.Golems.All[golemId]);
            Assert.Equal(78, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Fire),
                global::Game.MaxResistance(conn, DamageType.Fire)));
            Assert.Equal(39, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));
            Assert.NotNull(inventory.Remove(6, maximumFirePlacement.Id));
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 2.5).GetAwaiter().GetResult();
            Assert.Same(inheritedGolem, conn.Golems.All[golemId]);
            Assert.Equal(75, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Fire),
                global::Game.MaxResistance(conn, DamageType.Fire)));
            Assert.Equal(37, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));

            var coldRow = Mod("ColdResist8");
            Assert.Equal((ushort)3521, coldRow.Hash);
            Assert.Equal("base_cold_damage_resistance_%", Assert.Single(coldRow.Stats).Id);
            var coldRing = RingWithMod(coldRow.Id, ModSection.Explicit, 48, 505);
            var coldPlacement = inventory.Add(6, 0, 0, coldRing);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 3).GetAwaiter().GetResult();
            Assert.Equal(75, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Cold),
                global::Game.MaxResistance(conn, DamageType.Cold)));
            Assert.Equal(37, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(50, inheritedGolem.Player.GetValueOrDefault(
                Program.Stats.Key("minions_gain_x_percent_of_your_resistances")!.Value));
            Assert.NotNull(inventory.Remove(6, coldPlacement.Id));
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 3.5).GetAwaiter().GetResult();
            Assert.Same(inheritedGolem, conn.Golems.All[golemId]);
            Assert.Equal(40, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Cold),
                global::Game.MaxResistance(conn, DamageType.Cold)));
            Assert.Equal(20, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));

            bool priorSin = conn.HasQuestFlag("SinOnKitavaDefeat");
            if (priorSin)
                global::Game.SetQuestFlags("test", wire.Stream, wire.Ciphers, conn, ["SinOnKitavaDefeat"], false).GetAwaiter().GetResult();
            global::Game.SetQuestFlags("test", wire.Stream, wire.Ciphers, conn, ["SinOnKitavaDefeat"]).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 4).GetAwaiter().GetResult();
            Assert.Same(inheritedGolem, conn.Golems.All[golemId]);
            Assert.Equal(new[] { 51, 10, -31, -91 }, new[]
            {
                DamageModel.Capped(global::Game.Resistance(conn, DamageType.Fire), global::Game.MaxResistance(conn, DamageType.Fire)),
                DamageModel.Capped(global::Game.Resistance(conn, DamageType.Cold), global::Game.MaxResistance(conn, DamageType.Cold)),
                DamageModel.Capped(global::Game.Resistance(conn, DamageType.Lightning), global::Game.MaxResistance(conn, DamageType.Lightning)),
                DamageModel.Capped(global::Game.Resistance(conn, DamageType.Chaos), global::Game.MaxResistance(conn, DamageType.Chaos)),
            });
            Assert.Equal(new[] { 25, 5, -16, -46 }, new[]
            {
                inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Fire),
                inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold),
                inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Lightning),
                inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos),
            });
            global::Game.SetQuestFlags("test", wire.Stream, wire.Ciphers, conn, ["SinOnKitavaDefeat"], priorSin).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 4.5).GetAwaiter().GetResult();
            Assert.Equal(37, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));
            Assert.Equal(20, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(-1, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Lightning));
            Assert.Equal(-31, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos));

            uint fireResistance = Program.Stats.Key("base_fire_damage_resistance_%")!.Value;
            uint chaosResistance = Program.Stats.Key("base_chaos_damage_resistance_%")!.Value;
            int resistanceCoefficient = resistanceMod.Values.Single();
            int defenceCoefficient = defenceMod.Values.Single();
            var oneMinionSubject = new StatConditions.Subject(Amounts: new Dictionary<string, long>
            {
                [StatConditions.ActiveNonVaalSkillMinionCount] = 1,
            });
            var directOne = ItemModStats.Sum([bonemeld], itemCatalog.Mods,
                subject: oneMinionSubject, statExists: Program.Stats.Has);
            Assert.Equal(resistanceCoefficient, directOne["base_resist_all_elements_%"]);
            Assert.Equal(resistanceCoefficient, directOne["base_chaos_damage_resistance_%"]);
            Assert.Equal(defenceCoefficient, directOne["global_defences_+%"]);
            Assert.Equal(withOne.Stats[fireResistance] + resistanceCoefficient, withTwo.Stats[fireResistance]);
            Assert.Equal(withOne.Stats[chaosResistance] + resistanceCoefficient, withTwo.Stats[chaosResistance]);
            Assert.Equal(withOne.Pools.FireResistance + resistanceCoefficient, withTwo.Pools.FireResistance);
            Assert.Equal(withOne.Pools.ColdResistance + resistanceCoefficient, withTwo.Pools.ColdResistance);
            Assert.Equal(withOne.Pools.LightningResistance + resistanceCoefficient, withTwo.Pools.LightningResistance);
            Assert.Equal(withOne.Pools.ChaosResistance + resistanceCoefficient, withTwo.Pools.ChaosResistance);
            Assert.Equal(withOne.Pools.ArmourPct + defenceCoefficient, withTwo.Pools.ArmourPct);
            Assert.Equal(withOne.Pools.EvasionPct + defenceCoefficient, withTwo.Pools.EvasionPct);
            Assert.Equal(withOne.Pools.EnergyShieldPct + defenceCoefficient, withTwo.Pools.EnergyShieldPct);
            Assert.Equal(1000 * (100 + withOne.Pools.ArmourPct) / 100, (int)withOne.Pools.Armour);
            Assert.Equal((int)Math.Round(1015m * (100 + withOne.Pools.EvasionPct) / 100,
                MidpointRounding.AwayFromZero), (int)withOne.Pools.Evasion);
            Assert.Equal(1000 * (100 + withOne.Pools.EnergyShieldPct) / 100, (int)withOne.Pools.EnergyShield);
            Assert.Equal((int)withZero.Pools.Armour + 10 * defenceCoefficient, (int)withOne.Pools.Armour);
            Assert.Equal((int)withZero.Pools.EnergyShield + 10 * defenceCoefficient, (int)withOne.Pools.EnergyShield);
            Assert.Equal((int)withZero.Pools.Ward + 10 * defenceCoefficient, (int)withOne.Pools.Ward);
            Assert.Equal((int)withOne.Pools.Armour + 10 * defenceCoefficient, (int)withTwo.Pools.Armour);
            Assert.Equal((int)Math.Round(1015m * (100 + withTwo.Pools.EvasionPct) / 100,
                MidpointRounding.AwayFromZero), (int)withTwo.Pools.Evasion);
            Assert.True(withTwo.Pools.Evasion > withOne.Pools.Evasion);
            Assert.Equal((int)withOne.Pools.EnergyShield + 10 * defenceCoefficient, (int)withTwo.Pools.EnergyShield);
            Assert.Equal((int)withOne.Pools.Ward + 10 * defenceCoefficient, (int)withTwo.Pools.Ward);

            var ownedGolem = Assert.Single(conn.Minions.All, minion => minion.Id == golemId);
            var ownedZombie = Assert.Single(conn.Minions.All, minion => minion.Id == zombieId);
            double golemUntil = ownedGolem.Until;
            double zombieUntil = ownedZombie.Until;
            ownedGolem.Until = global::Game.AreaTime(conn);
            ownedZombie.Until = global::Game.AreaTime(conn);
            var withZeroAgain = Snapshot();
            Assert.Equal(0, withZeroAgain.Count);
            Assert.Equal(withTwo.Stats[fireResistance] - 2 * resistanceCoefficient, withZeroAgain.Stats[fireResistance]);
            Assert.Equal(withTwo.Stats[chaosResistance] - 2 * resistanceCoefficient, withZeroAgain.Stats[chaosResistance]);
            Assert.Equal(withTwo.Pools.FireResistance - 2 * resistanceCoefficient, withZeroAgain.Pools.FireResistance);
            Assert.Equal(withTwo.Pools.ColdResistance - 2 * resistanceCoefficient, withZeroAgain.Pools.ColdResistance);
            Assert.Equal(withTwo.Pools.LightningResistance - 2 * resistanceCoefficient, withZeroAgain.Pools.LightningResistance);
            Assert.Equal(withTwo.Pools.ChaosResistance - 2 * resistanceCoefficient, withZeroAgain.Pools.ChaosResistance);
            Assert.Equal(withTwo.Pools.ArmourPct - 2 * defenceCoefficient, withZeroAgain.Pools.ArmourPct);
            Assert.Equal(withTwo.Pools.EvasionPct - 2 * defenceCoefficient, withZeroAgain.Pools.EvasionPct);
            Assert.Equal(withTwo.Pools.EnergyShieldPct - 2 * defenceCoefficient, withZeroAgain.Pools.EnergyShieldPct);
            Assert.Equal((int)withZero.Pools.Armour, (int)withZeroAgain.Pools.Armour);
            Assert.Equal((int)withZero.Pools.Evasion, (int)withZeroAgain.Pools.Evasion);
            Assert.Equal((int)withZero.Pools.EnergyShield, (int)withZeroAgain.Pools.EnergyShield);
            Assert.Equal((int)withZero.Pools.Ward, (int)withZeroAgain.Pools.Ward);
            ownedGolem.Until = golemUntil;
            Assert.Equal(1, Snapshot().Count);
            ownedZombie.Until = zombieUntil;
            Assert.Equal(2, Snapshot().Count);

            ownedZombie.Monster.Dead = true;
            ownedZombie.Monster.Life = 0;
            Assert.Equal(1, Snapshot().Count);
            ownedZombie.Monster.Dead = false;
            ownedZombie.Monster.Life = ownedZombie.Monster.MaxLife;
            Assert.Equal(2, Snapshot().Count);

            // A real fighter death removes its registry entry, then a real corpse summon restores count two. The same
            // living golem follows the owner's changed penalty at the ordinary cadence.
            var removedFighter = conn.Summons.Fighters[zombieId];
            removedFighter.Monster.Dead = true;
            removedFighter.Monster.Life = 0;
            TickSummonsAt(5);
            Assert.DoesNotContain(zombieId, conn.Summons.Fighters.Keys);
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 5.5).GetAwaiter().GetResult();
            Assert.Same(inheritedGolem, conn.Golems.All[golemId]);
            Assert.Equal(42, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Cold),
                global::Game.MaxResistance(conn, DamageType.Cold)));
            Assert.Equal(-59, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Chaos),
                global::Game.MaxResistance(conn, DamageType.Chaos)));
            Assert.Equal(21, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(-30, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos));

            const uint replacementCorpseId = 0x1000b23;
            var replacementCorpse = new SpawnedMonster("bonemeld replacement zombie corpse", new(100, 100, 0), 70, 8, 0)
            {
                Hostile = true, Dead = true, Life = 0, Variety = corpseVariety, Level = 70,
            };
            conn.Monsters[replacementCorpseId] = replacementCorpse;
            zombieId = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource.Id,
                global::Game.SummonData.Find("RaiseZombie")!, (100, 100), replacementCorpseId).GetAwaiter().GetResult();
            Assert.NotEqual(0u, zombieId);
            Assert.NotEqual(removedFighter.Id, zombieId);
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 6).GetAwaiter().GetResult();
            Assert.Same(inheritedGolem, conn.Golems.All[golemId]);
            Assert.Equal(40, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Cold),
                global::Game.MaxResistance(conn, DamageType.Cold)));
            Assert.Equal(-61, DamageModel.Capped(global::Game.Resistance(conn, DamageType.Chaos),
                global::Game.MaxResistance(conn, DamageType.Chaos)));
            Assert.Equal(20, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(-31, inheritedGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos));
            ownedZombie = Assert.Single(conn.Minions.All, minion => minion.Id == zombieId);

            var golemMonster = conn.Monsters[golemId];
            lock (conn.Monsters)
            {
                conn.Monsters[zombieId] = golemMonster;
                Assert.Equal(1, Snapshot().Count);
                conn.Monsters[zombieId] = ownedZombie.Monster;
            }

            var vaalRow = skills.EffectRow("VaalSummonSkeletons");
            Assert.True(vaalRow >= 0);
            Assert.True(skills.IsVaal(vaalRow));
            var skeletonVariety = global::Game.Monsters.Find(
                "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard")!;
            uint vaalId = global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, conn, skeletonVariety, 70,
                MonsterRarity.Normal, 103, 100, friendly: true).GetAwaiter().GetResult();
            Assert.NotEqual(0u, vaalId);
            var vaalMonster = conn.Monsters[vaalId];
            var vaalMinion = new PlayerMinions.Minion(vaalId, vaalMonster, "VaalSummonSkeletons", "Skeleton")
            {
                Until = double.PositiveInfinity,
            };
            Assert.False(vaalMonster.Dead);
            Assert.True(vaalMonster.Life > 0);
            Assert.Same(vaalMonster, conn.Monsters[vaalId]);
            Assert.Equal(double.PositiveInfinity, vaalMinion.Until);
            conn.Minions.Add(vaalMinion);
            Assert.Equal(2, Snapshot().Count);

            uint unknownId = global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, conn, skeletonVariety, 70,
                MonsterRarity.Normal, 106, 100, friendly: true).GetAwaiter().GetResult();
            Assert.NotEqual(0u, unknownId);
            var unknownMonster = conn.Monsters[unknownId];
            var unknownMinion = new PlayerMinions.Minion(unknownId, unknownMonster, "MissingNonVaalProvenance", "Skeleton")
            {
                Until = double.PositiveInfinity,
            };
            Assert.False(unknownMonster.Dead);
            Assert.True(unknownMonster.Life > 0);
            Assert.Same(unknownMonster, conn.Monsters[unknownId]);
            Assert.Equal(double.PositiveInfinity, unknownMinion.Until);
            conn.Minions.Add(unknownMinion);
            Assert.Equal(2, Snapshot().Count);

            var ordinaryRow = skills.EffectRow("SummonSkeletons");
            Assert.True(ordinaryRow >= 0);
            Assert.False(skills.IsVaal(ordinaryRow));
            uint ordinaryId = global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, conn, skeletonVariety, 70,
                MonsterRarity.Normal, 109, 100, friendly: true).GetAwaiter().GetResult();
            Assert.NotEqual(0u, ordinaryId);
            var ordinaryMonster = conn.Monsters[ordinaryId];
            var ordinaryMinion = new PlayerMinions.Minion(ordinaryId, ordinaryMonster, "SummonSkeletons", "Skeleton")
            {
                Until = double.PositiveInfinity,
            };
            Assert.False(ordinaryMonster.Dead);
            Assert.True(ordinaryMonster.Life > 0);
            Assert.Same(ordinaryMonster, conn.Monsters[ordinaryId]);
            Assert.Equal(double.PositiveInfinity, ordinaryMinion.Until);
            conn.Minions.Add(ordinaryMinion);
            Assert.Equal(3, Snapshot().Count);
            conn.Minions.Remove(ordinaryId);
            conn.Monsters.Remove(ordinaryId);
            Assert.Equal(2, Snapshot().Count);

            conn.PlayerLife = 0;
            Assert.Equal(0, Snapshot().Count);
            conn.PlayerLife = conn.Pools.Life;
            Assert.Equal(2, Snapshot().Count);

            // An ordinary minion-resistance source enters the same additive bucket once, and repeating the ordinary
            // refresh changes nothing. Restore the input before the older owner-only comparisons continue.
            int fireBeforeMinionSource = conn.Golems.All[golemId].Monster.Mods.Effects.Resistance(DamageType.Fire);
            Assert.Equal(37, fireBeforeMinionSource);
            minionFireResistance = 50;
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 7).GetAwaiter().GetResult();
            var minionSourceGolem = conn.Golems.All[golemId];
            Assert.Equal(87, minionSourceGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 7.5).GetAwaiter().GetResult();
            Assert.Same(minionSourceGolem, conn.Golems.All[golemId]);
            Assert.Equal(87, minionSourceGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));

            var removedTwo = inventory.Remove(5, wornBonemeld.Id);
            Assert.NotNull(removedTwo);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.SendSkills("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();
            var withoutTwo = Snapshot();
            Assert.Equal(2, withoutTwo.Count);
            Assert.Equal(withoutTwo.Stats[fireResistance] + 2 * resistanceCoefficient, withTwo.Stats[fireResistance]);
            Assert.Equal(withoutTwo.Stats[chaosResistance] + 2 * resistanceCoefficient, withTwo.Stats[chaosResistance]);
            Assert.Equal(withoutTwo.Pools.FireResistance + 2 * resistanceCoefficient, withTwo.Pools.FireResistance);
            Assert.Equal(withoutTwo.Pools.ColdResistance + 2 * resistanceCoefficient, withTwo.Pools.ColdResistance);
            Assert.Equal(withoutTwo.Pools.LightningResistance + 2 * resistanceCoefficient, withTwo.Pools.LightningResistance);
            Assert.Equal(withoutTwo.Pools.ChaosResistance + 2 * resistanceCoefficient, withTwo.Pools.ChaosResistance);
            Assert.Equal(withoutTwo.Pools.ArmourPct + 2 * defenceCoefficient, withTwo.Pools.ArmourPct);
            Assert.Equal(withoutTwo.Stats.GetValueOrDefault(defence) + 2 * defenceCoefficient,
                withTwo.Stats.GetValueOrDefault(defence));
            Assert.DoesNotContain(withTwo.Stats.Keys,
                key => key == Program.Stats.Key("all_resistances_%_per_active_minion_not_from_vaal_skills")!.Value);
            Assert.DoesNotContain(withTwo.Stats.Keys,
                key => key == Program.Stats.Key("global_defences_+%_per_active_minion_not_from_vaal_skills")!.Value);

            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 8).GetAwaiter().GetResult();
            var withoutCoefficientGolem = Assert.Single(conn.Golems.All.Values);
            Assert.Same(withoutCoefficientGolem.Monster, conn.Monsters[withoutCoefficientGolem.Id]);
            Assert.Equal(0, withoutCoefficientGolem.Player.GetValueOrDefault(
                Program.Stats.Key("minions_gain_x_percent_of_your_resistances")!.Value));

            int IncomingLifeLoss(GolemState.Golem golem, DamageType type, double at, int expectedLoss)
            {
                var monster = golem.Monster;
                Assert.Same(monster, conn.Monsters[golem.Id]);
                Assert.Equal(0, golem.Player.GetValueOrDefault(
                    Program.Stats.Key("minion_additional_spell_block_%")!.Value));
                golem.ShieldPool.SetMaximum(0);
                monster.Life = monster.MaxLife;
                const uint attackerId = 0x07100001;
                var attacker = new SpawnedMonster("plain bonemeld attacker", new(100, 100, 0), 100, 8, 1000);
                conn.Monsters[attackerId] = attacker;
                var attack = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(type, 400, 400)) { Spell = true };
                Assert.Equal(0, attacker.Effects.CriticalChance);
                Assert.Equal(1, monster.Effects.DamageTaken(true));
                uint before = monster.Life;
                try
                {
                    Assert.True(global::Game.GolemTakeHitLocked(conn, golem, attackerId, attacker, attack, at));
                }
                finally
                {
                    conn.Monsters.Remove(attackerId);
                }
                int loss = (int)(before - monster.Life);
                Assert.Equal(expectedLoss, loss);
                return loss;
            }

            var flameVariety = withoutCoefficientGolem.Monster.Variety!;
            Assert.Equal(70, global::Game.Monsters.BaseResistance(flameVariety, 20, DamageType.Fire));
            Assert.Equal(40, global::Game.Monsters.BaseResistance(flameVariety, 20, DamageType.Cold));
            Assert.Equal(20, global::Game.Monsters.BaseResistance(flameVariety, 20, DamageType.Chaos));
            Assert.Equal(0, withoutCoefficientGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(0, withoutCoefficientGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos));
            int SpellLifeLoss(int innateResistance, int inheritedResistance) =>
                (int)Math.Round(400 * (100 - Math.Clamp(innateResistance + inheritedResistance, -200, 75)) / 100d);
            Assert.Equal(240, SpellLifeLoss(40, 0));
            Assert.Equal(320, SpellLifeLoss(20, 0));
            Assert.Equal(160, SpellLifeLoss(40, 20));
            Assert.Equal(444, SpellLifeLoss(20, -31));
            IncomingLifeLoss(withoutCoefficientGolem, DamageType.Cold, 8.1, SpellLifeLoss(40, 0));
            IncomingLifeLoss(withoutCoefficientGolem, DamageType.Chaos, 8.2, SpellLifeLoss(20, 0));
            inventory.Put(5, removedTwo!);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            global::Game.SendSkills("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 8.5).GetAwaiter().GetResult();
            var reWornGolem = Assert.Single(conn.Golems.All.Values);
            Assert.Same(reWornGolem.Monster, conn.Monsters[reWornGolem.Id]);
            Assert.Equal(50, reWornGolem.Player.GetValueOrDefault(
                Program.Stats.Key("minions_gain_x_percent_of_your_resistances")!.Value));
            Assert.Equal(20, reWornGolem.Monster.Mods.Effects.Resistance(DamageType.Cold));
            Assert.Equal(-31, reWornGolem.Monster.Mods.Effects.Resistance(DamageType.Chaos));
            IncomingLifeLoss(reWornGolem, DamageType.Cold, 8.6, SpellLifeLoss(40, 20));
            IncomingLifeLoss(reWornGolem, DamageType.Chaos, 8.7, SpellLifeLoss(20, -31));

            minionFireResistance = 0;
            global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9).GetAwaiter().GetResult();
            reWornGolem = Assert.Single(conn.Golems.All.Values);
            Assert.Equal(37, reWornGolem.Monster.Mods.Effects.Resistance(DamageType.Fire));

            var repeatedA = Snapshot();
            var repeatedB = Snapshot();
            Assert.Equal(2, repeatedA.Count);
            Assert.Equal(repeatedA.Pools, repeatedB.Pools);
            Assert.Equal(repeatedA.Stats.Count, repeatedB.Stats.Count);
            Assert.All(repeatedA.Stats, stat => Assert.Equal(stat.Value, repeatedB.Stats[stat.Key]));

            // The same final source feeds the ordinary ward percentage branch.
            uint ward = Program.Stats.Key("base_ward")!.Value;
            var withoutWard = new Dictionary<uint, int>(withoutTwo.Stats) { [ward] = 100 };
            var wardWithSources = new Dictionary<uint, int>(repeatedA.Stats) { [ward] = 100 };
            var attributes = global::Game.CharacterStatSources(conn).Attributes;
            var weapons = global::Game.WeaponProfileFor(conn);
            var withoutBonus = global::Game.PassiveBonus(withoutWard, weapons, attributes);
            var withBonus = global::Game.PassiveBonus(wardWithSources, weapons, attributes);
            Assert.Equal(withoutBonus.WardPct + 2 * defenceCoefficient, withBonus.WardPct);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            global::Game.InactiveItems.TryRemove(conn.Character!, out _);
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    void Archery_equipment_hardening_owns_dash_travel_and_conditional_consumers()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-arborix-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string character = "Arborix" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), 1, 1)
            {
                Character = character,
                PlayerLife = 1000,
                PlayerMana = 1000,
                PlayerSent = true,
                Instance = new(),
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                    new HashSet<string>(["Game"])),
            };
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            var items = (ItemCatalog)global::Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            var uniques = new UniqueCatalog(items);
            var burningArrow = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemBurningArrow")!);
            var frostblink = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFrostblink")!);
            var socketedDash = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemDash")!);
            var arborix = uniques.Create("Arborix", new Random(504))! with
            {
                Sockets = [new ItemSocket(SocketColour.White, burningArrow)],
                Links = [1],
            };
            foreach (var old in inventory.Items(PlayerInventories.Weapon1))
                inventory.Remove(PlayerInventories.Weapon1, old.Id);
            foreach (var old in inventory.Items(PlayerInventories.Offhand1))
                inventory.Remove(PlayerInventories.Offhand1, old.Id);
            var bow = inventory.Add(PlayerInventories.Weapon1, 0, 0, arborix);
            global::Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = global::Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            using var wire = new Wire();

            var grants = global::Game.AllWornGrantedSkillsOf(conn);
            var grant = Assert.Single(grants, g => global::Game.IsDashEffect(g.EffectId));
            Assert.Equal((60999, 10623, 30, true, false),
                (grant.EffectsPerLevelRow, grant.Effect, grant.Level, grant.ManuallyCast, grant.Support));
            Assert.Contains(global::Game.WornGrantedSkillsOf(conn), g => g.Identity == grant.Identity);
            var actor = Assert.Single(global::Game.GemSkillEffects(conn), g => g.Effect == grant.Effect);
            var supported = global::Game.SupportedSkill(conn, actor.Id);
            Assert.Equal((grant.Effect, 30, 0), (supported.Effect, supported.Level, supported.Quality));
            Assert.Equal(20, supported.Stats[Program.Stats.Key("mana_cost")!.Value]);
            var cooldown = global::Game.CooldownFor(conn, actor.Effect, actor.Level, actor.Quality);
            Assert.NotNull(cooldown);
            Assert.Equal(2000, cooldown.Value.BaseMilliseconds);

            // Item eligibility belongs to the bow host, not to a socketed gem's requirement. Resolve current requirements
            // before the cached source list's Active filter, without refreshing CharacterPlayerStats between actions.
            ResetDashCooldown(actor.Effect, actor.Level);
            conn.Pools = conn.Pools with { Level = 1 };
            int requirementsActions = conn.ActionCount, requirementsVersions = CooldownVersion();
            uint requirementsMana = conn.PlayerMana;
            var requirementsFrom = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            conn.RecentEvents.Clear();
            Assert.False(global::Game.TryCommitDashUse(conn, actor.Id, actor.Effect, FindDashAim(conn), out _),
                "the first Dash at an unmet real host requirement was admitted");
            Assert.Equal(requirementsActions, conn.ActionCount);
            Assert.Equal(requirementsVersions, CooldownVersion());
            Assert.Equal(requirementsMana, conn.PlayerMana);
            Assert.Equal(requirementsFrom, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            Assert.Empty(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds));

            var restoredPools = global::Game.PoolsFor(conn, 100);
            global::Game.InactiveItems[character] = [bow.Id]; // the stale inactive cache that used to win admission
            conn.Pools = restoredPools;
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            Assert.True(global::Game.TryCommitDashUse(conn, actor.Id, actor.Effect, FindDashAim(conn), out var restoredUse),
                "the first restored-valid Dash was refused by a stale inactive cache");
            Assert.NotNull(restoredUse);
            Assert.NotEqual(requirementsFrom, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            Assert.Equal(conn.Pools.Mana - 20, conn.PlayerMana);
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedDash, conn.Clock.Elapsed.TotalSeconds));
            global::Game.InactiveItems[character] = [];
            actor = Assert.Single(global::Game.GemSkillEffects(conn), g => g.Effect == grant.Effect);
            ResetDashCooldown(actor.Effect, actor.Level);
            conn.RecentEvents.Clear();

            int Stat(string name) => Program.Stats.Key(name) is uint key
                ? global::Game.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
            Assert.Equal(2, Stat("number_of_additional_arrows"));
            int attackBefore = Stat("attack_speed_+%");
            Assert.InRange(attackBefore, 20, 30);
            Assert.Equal(attackBefore, conn.Pools.AttackSpeedPct);
            Assert.Equal(0, conn.Pools.MovementVelocityPct);
            Assert.Equal(0, conn.Pools.Evasion);
            int armourBefore = conn.Pools.Armour;

            // Confirm the real supported bow release, not only the folded dictionary.
            using (global::Game.ProjectileMutation(conn))
            {
                var arrow = global::Game.GemSkillEffects(conn).Single(g =>
                    global::Game.SkillsFor(conn).EffectId(g.Effect) == "BurningArrow");
                var shot = global::Game.CaptureGemProjectile(conn,
                    global::Game.ProjectilesFor("test", wire.Stream, wire.Ciphers, conn), arrow.Id, arrow,
                    (conn.Layout.SpawnX + 20, conn.Layout.SpawnY), new SkillCriticalRoll(0));
                Assert.Equal(3, shot.Profile.Count);
            }

            (int X, int Y) FindDashAim(ClientState target)
            {
                var from = target.Walk.Position(target.Clock.Elapsed.TotalSeconds);
                for (int distance = 1; distance <= 33; distance++)
                    foreach (var aim in new[]
                             {
                                 (from.X + distance, from.Y), (from.X - distance, from.Y),
                                 (from.X, from.Y + distance), (from.X, from.Y - distance),
                             })
                        if (target.Layout.Walkable is { } walkable && aim.Item1 >= 0 && aim.Item2 >= 0
                            && aim.Item1 < target.Layout.GridW && aim.Item2 < target.Layout.GridH
                            && walkable(aim.Item1, aim.Item2) && !target.Barrier.Contains(aim)
                            && global::Game.MovementLanding(target, actor.Effect, from, aim) != from)
                            return aim;
                throw new Xunit.Sdk.XunitException("the test layout has no Dash landing");
            }
            void Ready(int effect, int level)
            {
                lock (conn.Monsters)
                {
                    int key = global::Game.CooldownKey(conn, effect, level);
                    if (conn.SingleUseCooldowns.TryGetValue(key, out var state))
                        state.Refresh(conn.SkillReadyAt.GetValueOrDefault(key), state.Rate);
                }
            }
            void ResetDashCooldown(int effect, int level)
            {
                lock (conn.Monsters)
                {
                    int key = global::Game.CooldownKey(conn, effect, level);
                    conn.SingleUseCooldowns.Remove(key);
                    conn.SkillReadyAt.Remove(key);
                }
            }
            int CooldownVersion() => conn.SingleUseCooldowns.Values.Sum(s => s.UseVersion);
            Task GroundDash(ushort skill, (int X, int Y) aim)
            {
                var packet = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId).U32((uint)aim.X).U32((uint)aim.Y)
                    .U16(skill).U16(1).U16(0);
                return global::Game.OnClientMessages("test", wire.Stream, wire.Ciphers,
                    [(InstanceProtocol.UseSkillAtLocationId, packet.ToArray())], [], conn);
            }
            Task ObjectDash(ushort skill, (int X, int Y) aim)
            {
                const uint targetId = 0x504;
                var target = new SpawnedMonster("Dash target", new(aim.X, aim.Y, 0), 0, 0, 100_000);
                return global::Game.CastOn("test", wire.Stream, wire.Ciphers, conn,
                    new(targetId, skill, 1, 0), target, conn.Clock.Elapsed.TotalSeconds);
            }
            bool CastDash((int X, int Y) aim, out CooldownUseTicket? ticket)
            {
                var current = global::Game.GemSkillEffects(conn).Single(g => g.Effect == grant.Effect);
                Ready(current.Effect, current.Level);
                ticket = null;
                return global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, current.Id,
                    conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), aim).GetAwaiter().GetResult();
            }

            var from = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            var dashAim = FindDashAim(conn);
            conn.RecentEvents.Clear();
            uint manaBefore = conn.PlayerMana;
            ResetDashCooldown(actor.Effect, actor.Level);
            GroundDash(actor.Id, dashAim).GetAwaiter().GetResult();
            var landed = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            Assert.NotEqual(from, landed);
            Assert.InRange(global::Game.Dist(from, landed), 1, 33);
            Assert.Equal(manaBefore - 20, conn.PlayerMana);
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedDash, conn.Clock.Elapsed.TotalSeconds));
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedMovementSkill, conn.Clock.Elapsed.TotalSeconds));

            // The same accepted use through the object route also pays, moves and records at its admission boundary.
            ResetDashCooldown(actor.Effect, actor.Level);
            from = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            dashAim = FindDashAim(conn);
            manaBefore = conn.PlayerMana;
            conn.RecentEvents.Clear();
            ObjectDash(actor.Id, dashAim).GetAwaiter().GetResult();
            Assert.NotEqual(from, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            Assert.Equal(manaBefore - 20, conn.PlayerMana);
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedDash, conn.Clock.Elapsed.TotalSeconds));

            // The four rows now reach the real pools, projectile profile and IR conversion.
            global::Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            _ = global::Game.EnsureConditionalItemPools(conn);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            Assert.Equal(0, Stat("number_of_additional_arrows"));
            Assert.Equal(0, conn.Pools.AttackSpeedPct);
            Assert.InRange(conn.Pools.MovementVelocityPct, 20, 30);
            Assert.Equal(0, conn.Pools.Evasion);
            Assert.True(conn.Pools.Armour > armourBefore);
            using (global::Game.ProjectileMutation(conn))
            {
                var arrow = global::Game.GemSkillEffects(conn).Single(g =>
                    global::Game.SkillsFor(conn).EffectId(g.Effect) == "BurningArrow");
                var shot = global::Game.CaptureGemProjectile(conn,
                    global::Game.ProjectilesFor("test", wire.Stream, wire.Ciphers, conn), arrow.Id, arrow,
                    (conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds).X + 20, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds).Y),
                    new SkillCriticalRoll(0));
                Assert.Equal(1, shot.Profile.Count);
            }

            // Refusals neither displace nor pay. The real executor uses the shared effect cooldown, including when a
            // socketed Dash and Arborix's grant are present together.
            var held = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            conn.RecentEvents.Clear();
            ResetDashCooldown(actor.Effect, actor.Level);
            conn.PlayerMana = 0;
            int actionsBeforeRefusal = conn.ActionCount, versionsBeforeRefusal = CooldownVersion();
            var refusalGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            Assert.True(refusalGate.Wait(0));
            var groundRefusal = GroundDash(actor.Id, FindDashAim(conn));
            Assert.True(groundRefusal.IsCompleted, "insufficient Mana must not start a native echo");
            refusalGate.Release();
            Assert.Equal(actionsBeforeRefusal, conn.ActionCount);
            Assert.Equal(versionsBeforeRefusal, CooldownVersion());
            Assert.Equal(held, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            Assert.Empty(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds));
            Assert.True(refusalGate.Wait(0));
            var objectRefusal = ObjectDash(actor.Id, FindDashAim(conn));
            Assert.True(objectRefusal.IsCompleted, "insufficient Mana must not start an object-path native echo");
            refusalGate.Release();
            Assert.Equal(actionsBeforeRefusal, conn.ActionCount);
            Assert.Equal(versionsBeforeRefusal, CooldownVersion());
            Assert.Empty(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds));
            conn.PlayerMana = conn.Pools.Mana;
            foreach (var (name, apply, clear) in new (string, Action, Action)[]
                     {
                         ("dead", () => conn.PlayerLife = 0, () => conn.PlayerLife = conn.Pools.Life),
                         ("paused", () => conn.Clock.Stop(), () => conn.Clock.Start()),
                         ("leaving", () => conn.LeavingArea = true, () => conn.LeavingArea = false),
                         ("retired", () => conn.RageRetired = true, () => conn.RageRetired = false),
                         ("closed", () => conn.Closed = true, () => conn.Closed = false),
                     })
            {
                ResetDashCooldown(actor.Effect, actor.Level);
                conn.RecentEvents.Clear();
                held = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                apply();
                actionsBeforeRefusal = conn.ActionCount;
                versionsBeforeRefusal = CooldownVersion();
                Assert.True(refusalGate.Wait(0));
                var refused = GroundDash(actor.Id, FindDashAim(conn));
                Assert.True(refused.IsCompleted, $"{name} Dash must not acknowledge");
                refusalGate.Release();
                Assert.Equal(actionsBeforeRefusal, conn.ActionCount);
                Assert.Equal(versionsBeforeRefusal, CooldownVersion());
                Assert.Equal(held, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
                Assert.Empty(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds));
                clear();
            }
            var blocked = (conn.Layout.GridW + 10, conn.Layout.SpawnY);
            Assert.False(CastDash(blocked, out _));
            Assert.Equal(held, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));

            // Travel is disabled at shared admission for exact Travel rows, except Dash.
            var helmet = ItemCatalog.Create(items.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, frostblink)],
                Links = [1],
            };
            if (inventory.Items(PlayerInventories.Helmet).FirstOrDefault() is { } oldHelmet)
                inventory.Remove(PlayerInventories.Helmet, oldHelmet.Id);
            var helmetPlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, helmet);
            global::Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            var blink = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "Frostblink");
            Assert.NotNull(global::Game.GemSkillInstanceDisabled(conn, blink.Effect, blink.Id));
            Assert.False(global::Game.TryBeginSkillCooldownUse(conn, blink.Id, blink.Effect, null,
                instant: false, () => { }, out _));

            // A simultaneous socketed Dash remains a distinct host identity but shares the effect cooldown.
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, bow.Id, item => item with
            {
                Sockets = [.. item.Sockets, new ItemSocket(SocketColour.White, socketedDash)],
                Links = [1, 1],
            }));
            global::Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            var dashes = global::Game.GemSkillEffects(conn).Where(g =>
                global::Game.IsDashEffect(global::Game.SkillsFor(conn).EffectId(g.Effect))).ToArray();
            Assert.Equal(2, dashes.Length);
            Assert.Contains(dashes, d => d.Effect == grant.Effect);
            Assert.Contains(dashes, d => d.Effect != grant.Effect);
            Assert.Equal(2, dashes.Select(d => d.Id).Distinct().Count());
            var socketed = dashes.Single(d => d.Effect != grant.Effect);
            Assert.Equal(global::Game.CooldownKey(conn, grant.Effect, actor.Level),
                global::Game.CooldownKey(conn, socketed.Effect, socketed.Level));
            lock (conn.Monsters)
            {
                int key = global::Game.CooldownKey(conn, grant.Effect, actor.Level);
                conn.SingleUseCooldowns.Remove(key);
                conn.SkillReadyAt.Remove(key);
            }
            Assert.True(CastDash(FindDashAim(conn), out _));
            Assert.False(global::Game.TryBeginSkillCooldownUse(conn, socketed.Id, socketed.Effect,
                global::Game.CooldownFor(conn, socketed.Effect, socketed.Level, socketed.Quality),
                instant: false, () => { }, out _));

            // A held cost/trigger/callback publication and acknowledgement cannot erase the accepted use, move it into the
            // publication window, or revive it after its connection/lifetime/action/source has changed.
            int staleCallbacks = 0;
            using var staleCallback = GameEvents.SkillCastAt.Add("Game", (_, effectId, _, _, _) =>
            {
                if (global::Game.IsDashEffect(effectId)) staleCallbacks++;
                return null;
            });
            void AssertDeferredControl(string name, Action change, Action clear)
            {
                ResetDashCooldown(actor.Effect, actor.Level);
                conn.RecentEvents.Clear();
                var committedFrom = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                uint committedMana = conn.PlayerMana;
                int triggersBefore = conn.FlaskAutomation.Events.Count;
                var outputGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(outputGate.Wait(0));
                Assert.True(global::Game.TryCommitDashUse(conn, actor.Id, actor.Effect, FindDashAim(conn),
                    out var committedUse), $"{name}: the accepted-action fixture did not commit");
                Assert.NotNull(committedUse);
                var publish = global::Game.PublishDashUse("test", wire.Stream, wire.Ciphers, conn, committedUse);
                Assert.False(publish.IsCompleted, $"{name}: publication did not block");
                double committedExpiry = committedUse.AcceptedAt + RecentEvents.WindowSeconds;
                var committedLanding = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                Assert.NotEqual(committedFrom, committedLanding);
                Assert.Equal(committedMana - 20, conn.PlayerMana);
                Assert.Equal(committedExpiry, conn.RecentEvents.NextExpiry(committedUse.AcceptedAt));
                change();
                int invalidatedAction = conn.ActionCount;
                outputGate.Release();
                Assert.False(publish.GetAwaiter().GetResult(), $"{name}: stale Dash publication was accepted");
                Assert.Equal(committedLanding, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
                Assert.Equal(committedMana - 20, conn.PlayerMana);
                Assert.Equal(invalidatedAction, conn.ActionCount);
                Assert.Equal(committedExpiry, conn.RecentEvents.NextExpiry(committedUse.AcceptedAt));
                Assert.True(conn.RecentEvents.Happened(StatConditions.UsedDash, committedExpiry - .001));
                Assert.False(conn.RecentEvents.Happened(StatConditions.UsedDash, committedExpiry));
                Assert.Equal(triggersBefore, conn.FlaskAutomation.Events.Count);
                Assert.Equal(0, staleCallbacks);
                Assert.Equal(0, committedUse.Cooldown.NativeSkillId);
                Assert.False(committedUse.ConfirmNativeUse(conn));
                clear();
            }

            AssertDeferredControl("death", () => conn.PlayerLife = 0, () => conn.PlayerLife = conn.Pools.Life);
            AssertDeferredControl("retirement", () => conn.RageRetired = true, () => conn.RageRetired = false);
            AssertDeferredControl("epoch", () => conn.ProjectileEpoch++, () => conn.ProjectileEpoch--);
            AssertDeferredControl("module", () => global::Game.CombatLifetime = new ModuleLifetime("stale"),
                () => global::Game.CombatLifetime = lifetime);
            AssertDeferredControl("later action", () => { lock (conn.Monsters) conn.ActionCount++; }, () => { });

            ResetDashCooldown(actor.Effect, actor.Level);
            conn.RecentEvents.Clear();
            var otherConnection = new ClientState(conn.Layout, 2, 1);
            Assert.True(global::Game.TryCommitDashUse(conn, actor.Id, actor.Effect, FindDashAim(conn),
                out var ownerBoundUse));
            Assert.False(ownerBoundUse!.Current(otherConnection));
            Assert.False(ownerBoundUse.ConfirmNativeUse(otherConnection));
            Assert.Equal(0, ownerBoundUse.Cooldown.NativeSkillId);
            AssertDeferredControl("source removal", () => inventory.Remove(PlayerInventories.Weapon1, bow.Id), () => { });
            Assert.Empty(global::Game.WornGrantedSkillsOf(conn));
            Assert.Null(global::Game.GemSkillInstanceDisabled(conn, blink.Effect, blink.Id));

            // The raw dynamic id is still cast=true after its source leaves, but it must not reach generic admission. A held
            // send gate fails immediately here for either route if an acknowledgement is attempted.
            foreach (var objectRoute in new[] { false, true })
            {
                ResetDashCooldown(actor.Effect, actor.Level);
                conn.RecentEvents.Clear();
                int staleActions = conn.ActionCount, staleVersions = CooldownVersion(), staleTriggers = conn.FlaskAutomation.Events.Count;
                uint staleMana = conn.PlayerMana;
                var staleFrom = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                var staleGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(staleGate.Wait(0));
                var staleUse = objectRoute ? ObjectDash(actor.Id, FindDashAim(conn))
                    : GroundDash(actor.Id, FindDashAim(conn));
                Assert.True(staleUse.IsCompleted, $"stale dynamic id on {objectRoute} route waited for acknowledgement");
                staleGate.Release();
                Assert.Equal(staleActions, conn.ActionCount);
                Assert.Equal(staleVersions, CooldownVersion());
                Assert.Equal(staleMana, conn.PlayerMana);
                Assert.Equal(staleFrom, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
                Assert.Empty(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds));
                Assert.Equal(staleTriggers, conn.FlaskAutomation.Events.Count);
                Assert.Equal(0, staleCallbacks);
            }
            var reequipped = inventory.Add(PlayerInventories.Weapon1, 0, 0, arborix);
            Assert.NotEqual(bow.Id, reequipped.Id);
            var replacement = Assert.Single(global::Game.GemSkillEffects(conn), g => g.Effect == grant.Effect);
            Assert.NotEqual(actor.Id, replacement.Id);
            inventory.Remove(PlayerInventories.Helmet, helmetPlacement.Id);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    void Worn_item_grants_run_real_auras_with_host_identity_and_source_lifecycle()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-worn-grants-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string character = "WornGrants" + Guid.NewGuid().ToString("N");
        const byte amulet = 5; // Equipment rows 5-7 have no named PlayerInventories constants.
        ClientState NewConnection(string name, bool hardMode = false)
        {
            var connection = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = name,
                PlayerLife = 1000,
                PlayerMana = 1000,
                PlayerSent = true,
                Instance = new(),
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League(
                        hardMode ? "Ruthless" : "test", hardMode ? "Ruthless" : "Standard", false, false)
                    { DataSet = hardMode ? "HardMode" : null },
                    new HashSet<string>(["Game"])),
            };
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                connection.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            return connection;
        }

        try
        {
            var conn = NewConnection(character);
            var items = (ItemCatalog)global::Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            var uniques = new UniqueCatalog(items);
            var doryani = uniques.Create("Doryani's Delusion", new Random(1))!;
            var aul = uniques.Create("Aul's Uprising", new Random(2))!;
            var severed = uniques.Create("Severed in Sleep", new Random(3))!;
            var ylfeban = uniques.Create("Ylfeban's Trickery", new Random(4))!;
            var area = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SupportGemIncreasedAreaOfEffect")!)
                with { Quality = 20 };
            var melee = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SupportGemMeleePhysicalDamage")!);
            var empower = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SupportGemAdditionalLevel")!);
            var fireball = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var auraLevel = Mod("LocalIncreaseSocketedAuraGemLevelUnique___1")
                .Roll(ModSection.Explicit, new Random(4));
            var supportQuality = Mod("IncreaseSocketedSupportGemQualityUnique__2")
                .Roll(ModSection.Explicit, new Random(4));
            severed = severed with
            {
                Mods = [.. severed.Mods, auraLevel, supportQuality],
                // Deliberately separate link groups: supports on a granting host are not gem-link participants.
                Sockets = [
                    new ItemSocket(SocketColour.White, area),
                    new ItemSocket(SocketColour.White, melee),
                    new ItemSocket(SocketColour.White, empower),
                    new ItemSocket(SocketColour.White, fireball),
                ],
                Links = [1, 1, 1, 1],
            };
            foreach (var (row, item) in new[]
                     {
                         (PlayerInventories.Boots, doryani),
                         (amulet, aul),
                         (PlayerInventories.Weapon1, severed),
                         (PlayerInventories.Helmet, ylfeban),
                     })
            {
                if (inventory.Items(row).FirstOrDefault() is { } previous) inventory.Remove(row, previous.Id);
                inventory.Add(row, 0, 0, item);
            }
            conn.Pools = global::Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            Assert.Empty(global::Game.InactiveItems.GetValueOrDefault(character) ?? []);

            // The owner preserves the actual rows, not remembered item text or levels.
            var equipment = inventory.EquipmentSnapshot();
            var raw = global::Game.AllWornGrantedSkillsOf(conn, equipment);
            var puritySource = Assert.Single(raw, g =>
                g.EffectId == "PurityOfLightning" && g.ModId == "GrantsPurityOfLightningUnique__1");
            Assert.Equal((28267, 3020, 30), (puritySource.EffectsPerLevelRow, puritySource.Effect, puritySource.Level));
            Assert.True(puritySource.ManuallyCast);
            Assert.False(puritySource.Support);
            var envySources = raw.Where(g => g.EffectId == "Envy" && !g.Support).ToArray();
            Assert.Equal(2, envySources.Length);
            Assert.Contains(envySources, g => g.ModId == "GrantsEnvyUnique__1"
                && g.EffectsPerLevelRow == 47391 && g.Level == 25);
            Assert.Contains(envySources, g => g.ModId == "GrantsEnvyUnique__2"
                && g.EffectsPerLevelRow == 47390 && g.Level == 15);
            var noReservation = Assert.Single(Mod("EnvyNoReservationUnique__1").Stats);
            Assert.Equal("envy_reserves_no_mana", noReservation.Id);
            Assert.Equal(noReservation.Id, AuraCatalog.NoManaReservationStat("Envy"));
            Assert.True(global::Game.WornGrantedSkillOwner(conn).StillWorn(puritySource, equipment));
            var triggeredSource = Assert.Single(raw, g => g.EffectId == "TriggeredShockedGround");
            Assert.False(triggeredSource.ManuallyCast);

            var admitted = global::Game.WornGrantedSkillsOf(conn);
            Assert.Contains(admitted, g => g.EffectsPerLevelRow == 28267);
            Assert.Equal(2, admitted.Count(g => g.EffectId == "Envy"));
            Assert.DoesNotContain(admitted, g => g.EffectId == "TriggeredShockedGround");
            var visible = global::Game.GemSkillEffects(conn).ToArray();
            var purity = Assert.Single(visible, g => g.Effect == puritySource.Effect);
            var envies = visible.Where(g => g.Effect == envySources[0].Effect).ToArray();
            Assert.Equal(2, envies.Length);
            Assert.NotEqual(envies[0].Id, envies[1].Id);
            Assert.Equal(30, purity.Level);
            var aulSkill = envies.Select(e => global::Game.SupportedSkill(conn, e.Id)).Single(s => s.Level == 15);
            var severedSkill = envies.Select(e => global::Game.SupportedSkill(conn, e.Id)).Single(s => s.Level == 25);
            ushort aulId = envies.Single(e => e.Level == 15).Id;
            ushort severedId = envies.Single(e => e.Level == 25).Id;
            Assert.Equal((0, 0), global::Game.SupportedReservation(conn, aulId));
            Assert.Equal((0, 0), global::Game.SupportedReservation(conn, severedId));
            Assert.DoesNotContain(severedSkill.Supports, s => s.Effect.Id == "SupportMeleePhysicalDamage");
            Assert.DoesNotContain(severedSkill.Supports, s => s.Effect.Id == "SupportEmpower");
            var areaSupport = Assert.Single(severedSkill.Supports,
                s => s.Effect.Id == "SupportIncreasedAreaOfEffect");
            Assert.Equal(50, areaSupport.Gem.Quality);
            Assert.Equal(25, severedSkill.Stats[Program.Stats.Key("area_damage_+%")!.Value]);
            Assert.Empty(aulSkill.Supports);
            Assert.Equal(severedSkill.Level, global::Game.SupportedSkill(conn, envies.Single(e => e.Level == 25).Id).Level);

            var actor = global::Game.PlayerSkills(conn);
            var purityActor = actor.Single(s => s.EffectsPerLevelRow == 28267);
            var severedActor = actor.Single(s => s.Id == envies.Single(e => e.Level == 25).Id);
            Assert.Empty(purityActor.Supports);
            Assert.Single(severedActor.Supports);
            var resolvedSupport = global::Game.SupportedSkill(conn, severedActor.Id).Supports.Single();
            var expectedSupport = global::Game.SkillsFor(conn)
                .FindByRow(0, resolvedSupport.Effect.Row, resolvedSupport.Gem.Level)!;
            Assert.Equal(expectedSupport.EffectsPerLevelRow, severedActor.Supports.Single().EffectsPerLevelRow);

            // The same source stays stable while another host and its skill come/go.
            ushort stablePurity = purity.Id;
            inventory.Remove(amulet, inventory.Items(amulet).Single().Id);
            equipment = inventory.EquipmentSnapshot();
            var withoutAul = global::Game.GemSkillEffects(conn).ToArray();
            Assert.Equal(stablePurity, Assert.Single(withoutAul, g => g.Effect == puritySource.Effect).Id);
            Assert.DoesNotContain(withoutAul, g => g.Effect == envySources[0].Effect && g.Level == 15);
            inventory.Add(amulet, 0, 0, aul);
            envies = global::Game.GemSkillEffects(conn).Where(g => g.Effect == envySources[0].Effect).ToArray();
            Assert.Equal(2, envies.Length);
            aulId = envies.Single(e => e.Level == 15).Id;
            severedId = envies.Single(e => e.Level == 25).Id;
            Assert.Equal(stablePurity, Assert.Single(global::Game.GemSkillEffects(conn),
                g => g.Effect == puritySource.Effect).Id);

            // Cast through the existing actor admission and aura executor.
            using var wire = new Wire();
            bool CastAuraOn(ClientState target, ushort skillId)
            {
                var supported = global::Game.SupportedSkill(target, skillId);
                var cooldown = global::Game.CooldownFor(target, supported.Effect, supported.Level, supported.Quality);
                CooldownUseTicket? ticket = null;
                if (cooldown is not null)
                    Assert.True(global::Game.TryBeginSkillCooldownUse(target, skillId, supported.Effect, cooldown,
                        instant: true, () => { }, out ticket));
                return global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, target, skillId,
                    (0, 0), (0, 0), cooldownUse: ticket).GetAwaiter().GetResult();
            }
            bool CastAura(ushort skillId) => CastAuraOn(conn, skillId);
            int baseLightning = conn.Pools.LightningResistance;
            int baseLightningMax = conn.Pools.LightningMaxResistance;
            Assert.True(CastAura(purity.Id));
            int purityReserved = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values);
            Assert.Equal(purityReserved, global::Game.ReservedMana(conn));
            Assert.Equal(conn.Pools.Mana - purityReserved, conn.PlayerMana);
            uint manaAfterPurity = conn.PlayerMana;
            uint lifeAfterPurity = conn.PlayerLife;
            uint maximumLifeAfterPurity = conn.Pools.Life;
            Assert.Equal(baseLightning + 51, conn.Pools.LightningResistance);
            Assert.Equal(baseLightningMax + 5, conn.Pools.LightningMaxResistance);
            var runningPurity = Assert.Single(conn.Auras.Values, a => a.EffectId == "PurityOfLightning");
            Assert.Equal(30, runningPurity.Level);
            Assert.NotNull(runningPurity.SourceBuff);
            Assert.Equal(2, AuraCatalog.StartPackets(runningPurity, 1, null).Count());
            Assert.True(conn.PresentedAuras.ContainsKey(purity.Id));
            Assert.Contains("PurityOfLightning", CharacterSaves.Load(character)!.ActiveAuras!);

            var beforeEnvy = global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            var beforeHit = global::Game.AttackHitConverted(global::Game.WeaponFor(conn), beforeEnvy, melee: true, null)
                [GameServer.Core.World.DamageType.Chaos];
            Assert.True(CastAura(aulId));
            Assert.Contains(aulId, conn.Auras.Keys);
            Assert.Equal((0, 0), global::Game.SupportedReservation(conn, aulId));
            var afterEnvy = global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            var afterHit = global::Game.AttackHitConverted(global::Game.WeaponFor(conn), afterEnvy, melee: true, null)
                [GameServer.Core.World.DamageType.Chaos];
            Assert.Equal(101, (int)Math.Floor(afterHit.Min - beforeHit.Min));
            Assert.Equal(141, (int)Math.Floor(afterHit.Max - beforeHit.Max));
            Assert.Equal(15, conn.Auras[aulId].Level);
            Assert.Equal(AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values),
                global::Game.ReservedMana(conn));
            Assert.Equal(purityReserved, global::Game.ReservedMana(conn));
            Assert.Equal(manaAfterPurity, conn.PlayerMana);
            Assert.Equal(lifeAfterPurity, conn.PlayerLife);
            Assert.Equal(maximumLifeAfterPurity, conn.Pools.Life);

            // The same host/socket/effect keeps its bar id when its actual GEPL row changes, but the active aura is
            // rebuilt from Envy 15 to Envy 25: level and real added-chaos consumers update while Aul keeps it free.
            var aulBefore = inventory.Items(amulet).Single();
            Assert.True(inventory.Change(amulet, aulBefore.Id, item => item with
            {
                Mods = [.. item.Mods.Select(mod => mod.Hash == Mod("GrantsEnvyUnique__2").Hash
                    ? Mod("GrantsEnvyUnique__1").Roll(mod.Section, new Random(6), _ => (25, 25))
                    : mod)],
            }) is not null);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.Contains(aulId, global::Game.GemSkillEffects(conn).Select(g => g.Id));
            Assert.Equal(25, global::Game.SupportedSkill(conn, aulId).Level);
            Assert.Equal(25, conn.Auras[aulId].Level);
            Assert.Equal(0, conn.Auras[aulId].ManaPercent);
            Assert.Equal(purityReserved, AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values));
            Assert.Equal(manaAfterPurity, conn.PlayerMana);
            var level25Stats = global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            var level25Hit = global::Game.AttackHitConverted(global::Game.WeaponFor(conn), level25Stats, melee: true, null)
                [GameServer.Core.World.DamageType.Chaos];
            Assert.Equal(205, (int)Math.Floor(level25Hit.Min - beforeHit.Min));
            Assert.Equal(287, (int)Math.Floor(level25Hit.Max - beforeHit.Max));

            // Removing the Doryani source ends its aura and keeps the already paid mana gone. It never resurrects
            // from the old skill id after another host provides the same skill family.
            ushort doryaniBefore = purity.Id;
            uint keptMana = conn.PlayerMana;
            inventory.Remove(PlayerInventories.Boots, inventory.Items(PlayerInventories.Boots).Single().Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "PurityOfLightning");
            Assert.Equal(keptMana, conn.PlayerMana);
            Assert.Equal(baseLightning, conn.Pools.LightningResistance);
            Assert.NotNull(global::Game.GemSkillInstanceDisabled(conn, purity.Effect, doryaniBefore));
            var replacement = uniques.Create("Doryani's Delusion", new Random(5))!;
            inventory.Add(PlayerInventories.Boots, 0, 0, replacement);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            var replacementPurity = global::Game.GemSkillEffects(conn)
                .Single(g => global::Game.SkillsFor(conn).EffectId(g.Effect) == "PurityOfLightning");
            Assert.NotEqual(doryaniBefore, replacementPurity.Id);
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "PurityOfLightning");

            // Inactivity is keyed by the actual host item, not merely by the name of its effect.
            conn.Pools = conn.Pools with { Level = 1 };
            Assert.Equal("the granting item is inactive",
                global::Game.GemSkillInstanceDisabled(conn, replacementPurity.Effect, replacementPurity.Id));
            lock (conn.Monsters)
            {
                conn.SingleUseCooldowns.Clear();
                conn.SkillReadyAt.Clear();
            }
            Assert.False(CastAura(replacementPurity.Id));
            conn.Pools = global::Game.PoolsFor(conn, 100);
            Assert.Null(global::Game.GemSkillInstanceDisabled(conn, replacementPurity.Effect, replacementPurity.Id));

            // Aul also exempts the supported Severed owner. Toggle the Aul owner off and start Severed through real
            // admission; the ordinary supported cost would be 65%, but the equipped exemption makes this owner free.
            Assert.True(CastAura(aulId));
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "Envy");
            lock (conn.Monsters)
            {
                conn.SingleUseCooldowns.Clear();
                conn.SkillReadyAt.Clear();
            }
            uint manaBeforeFreeSevered = conn.PlayerMana;
            uint lifeBeforeFreeSevered = conn.PlayerLife;
            uint maximumLifeBeforeFreeSevered = conn.Pools.Life;
            var severedDisabled = global::Game.GemSkillInstanceDisabled(conn,
                global::Game.SupportedSkill(conn, severedId).Effect, severedId);
            Assert.Null(severedDisabled);
            Assert.True(CastAura(severedId));
            var freeSupported = conn.Auras[severedId];
            Assert.Equal((0, 0), global::Game.SupportedReservation(conn, severedId));
            Assert.Equal(25, freeSupported.Level);
            Assert.Equal(0, freeSupported.ManaPercent);
            var freeSupport = Assert.Single(global::Game.SupportedSkill(conn, severedId).Supports);
            var rawReservation = global::Game.SkillsFor(conn).Reservation(freeSupported.Effect, freeSupported.Level)!.Value;
            Assert.Equal(6500, (int)Math.Floor(rawReservation.Percent * freeSupport.CostMultiplier / 100.0));
            var freeSupportedStats = global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            var freeSupportedHit = global::Game.AttackHitConverted(global::Game.WeaponFor(conn), freeSupportedStats,
                melee: true, null)[GameServer.Core.World.DamageType.Chaos];
            Assert.Equal(205, (int)Math.Floor(freeSupportedHit.Min - beforeHit.Min));
            Assert.Equal(287, (int)Math.Floor(freeSupportedHit.Max - beforeHit.Max));
            Assert.Equal(0, global::Game.ReservedMana(conn));
            Assert.Equal(manaBeforeFreeSevered, conn.PlayerMana);
            Assert.Equal(lifeBeforeFreeSevered, conn.PlayerLife);
            Assert.Equal(maximumLifeBeforeFreeSevered, conn.Pools.Life);

            // Removing the exemption host must reprice the valid active Severed owner in place, never leave its stale
            // zero reservation, and never refill Mana when the new reservation appears.
            uint severedHost = inventory.Items(PlayerInventories.Weapon1).Single().Id;
            uint manaWithFreeEnvy = conn.PlayerMana;
            inventory.Remove(amulet, inventory.Items(amulet).Single().Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            var transferredEnvy = Assert.Single(conn.Auras.Values, a => a.EffectId == "Envy");
            Assert.Equal(severedId, transferredEnvy.SkillId);
            Assert.Equal(severedHost, transferredEnvy.WornGrant?.Host);
            Assert.Equal(25, transferredEnvy.Level);
            Assert.Equal(6500, transferredEnvy.ManaPercent);
            Assert.Equal((0, 6500), global::Game.SupportedReservation(conn, severedId));
            int repricedReservation = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values);
            Assert.Equal(repricedReservation, global::Game.ReservedMana(conn));
            Assert.Equal((uint)Math.Max(0, Math.Min(manaWithFreeEnvy,
                (uint)Math.Max(0, conn.Pools.Mana - repricedReservation))), conn.PlayerMana);
            Assert.True(conn.PlayerMana <= manaWithFreeEnvy);
            Assert.Contains("Envy", CharacterSaves.Load(character)!.ActiveAuras!);
            var supportedEnvy = conn.Auras[severedId];
            int radiusWithSupport = AuraRadius.Of(supportedEnvy, 0, global::Game.SkillsFor(conn), Program.Stats);
            int radiusWithoutSupport = AuraRadius.Of(supportedEnvy with
            {
                SkillStats = global::Game.SkillsFor(conn).EffectStats(supportedEnvy.Effect, supportedEnvy.Level),
            }, 0, global::Game.SkillsFor(conn), Program.Stats);
            Assert.True(radiusWithSupport > radiusWithoutSupport);
            global::Game.ItemsOf(conn).SaveUnsaved();
            var currentSaved = CharacterSaves.Load(character)?.ActiveAuras ?? [];
            var restored = NewConnection(character);
            restored.Pools = global::Game.PoolsFor(restored, 100);
            restored.PlayerMana = restored.Pools.Mana;
            var restoredAuras = global::Game.RestoreRememberedAuras(restored, currentSaved);
            Assert.Contains(restoredAuras, a => a.EffectId == "Envy" && a.Level == 25);

            // Re-equipping the exemption reprices the same valid Severed owner to zero without refilling the Mana that
            // the unpriced period removed. Removing the real exemption mod alone (no equipment publication) must update
            // the active owner's reservation on the next query from current equipment.
            uint manaWithReservedEnvy = conn.PlayerMana;
            inventory.Add(amulet, 0, 0, aul);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            aulId = global::Game.GemSkillEffects(conn)
                .Single(g => global::Game.SkillsFor(conn).EffectId(g.Effect) == "Envy" && g.Level == 15).Id;
            Assert.Equal(severedId, Assert.Single(conn.Auras.Values, a => a.EffectId == "Envy").SkillId);
            Assert.Equal(0, conn.Auras[severedId].ManaPercent);
            Assert.Equal(0, global::Game.ReservedMana(conn));
            Assert.Equal(manaWithReservedEnvy, conn.PlayerMana);

            var aulWithExemption = inventory.Items(amulet).Single();
            Assert.True(inventory.Change(amulet, aulWithExemption.Id, item => item with
            {
                Mods = [.. item.Mods.Where(mod => mod.Hash != Mod("EnvyNoReservationUnique__1").Hash)],
            }) is not null);
            Assert.Equal((0, 5000), global::Game.SupportedReservation(conn, aulId));
            Assert.Equal((0, 6500), global::Game.SupportedReservation(conn, severedId));
            int requalifiedReservation = AuraCatalog.RequiredMana(conn.Pools,
                [conn.Auras[severedId] with { ManaFlat = 0, ManaPercent = 6500 }]);
            Assert.Equal(requalifiedReservation, global::Game.ReservedMana(conn));
            Assert.Equal(6500, conn.Auras[severedId].ManaPercent);
            Assert.True(conn.PlayerMana <= manaWithReservedEnvy);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.Equal(6500, Assert.Single(conn.Auras.Values, a => a.EffectId == "Envy").ManaPercent);

            // If the lost exemption pushes the active reservation over the maximum, the repricing owner loses admission
            // and its saved intent. The already spent Mana stays spent, and an explicit cast is refused at that capacity.
            uint manaBeforeOverCapacity = conn.PlayerMana;
            conn.Pools = conn.Pools with { ManaReservationEfficiencyPct = -50 };
            Assert.Equal(0, global::Game.ReservedMana(conn));
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "Envy");
            Assert.Equal(manaBeforeOverCapacity, conn.PlayerMana);
            Assert.DoesNotContain("Envy", CharacterSaves.Load(character)?.ActiveAuras ?? []);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            conn.Pools = conn.Pools with { ManaReservationEfficiencyPct = -50 };
            Assert.False(CastAura(severedId));
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "Envy");
            conn.Pools = conn.Pools with { ManaReservationEfficiencyPct = 0 };

            var restoredExemption = Mod("EnvyNoReservationUnique__1").Roll(ModSection.Explicit, new Random(10));
            Assert.True(inventory.Change(amulet, aulWithExemption.Id, item => item with
            {
                Mods = [.. item.Mods, restoredExemption],
            }) is not null);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "Envy");
            lock (conn.Monsters)
            {
                conn.SingleUseCooldowns.Clear();
                conn.SkillReadyAt.Clear();
            }
            uint manaBeforeFreeRecast = conn.PlayerMana;
            Assert.True(CastAura(severedId));
            Assert.Equal(0, Assert.Single(conn.Auras.Values, a => a.EffectId == "Envy").ManaPercent);
            Assert.Equal(manaBeforeFreeRecast, conn.PlayerMana);
            uint manaBeforeLiveRemoval = conn.PlayerMana;
            inventory.Remove(amulet, inventory.Items(amulet).Single().Id);
            Assert.Equal((0, 6500), global::Game.SupportedReservation(conn, severedId));
            global::Game.ReservedMana(conn);
            var liveRemovalPools = global::Game.PoolsFor(conn, conn.Pools.Level);
            Assert.Equal(liveRemovalPools.Mana, conn.Pools.Mana);
            Assert.Equal(liveRemovalPools.ManaReservationEfficiencyPct, conn.Pools.ManaReservationEfficiencyPct);
            Assert.Equal(AuraCatalog.RequiredMana(liveRemovalPools, conn.Auras.Values), global::Game.ReservedMana(conn));
            Assert.Equal(6500, Assert.Single(conn.Auras.Values, a => a.EffectId == "Envy").ManaPercent);
            Assert.True(conn.PlayerMana <= manaBeforeLiveRemoval);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.Equal(6500, Assert.Single(conn.Auras.Values, a => a.EffectId == "Envy").ManaPercent);

            // Trimming an unaffordable pool-changing aura must rebuild the pool after removal. Discipline's real grant
            // adds flat Energy Shield; forcing it over capacity removes it and restores the pre-aura pool immediately.
            string poolAuraCharacter = "PoolAura" + Guid.NewGuid().ToString("N");
            var poolAura = NewConnection(poolAuraCharacter);
            var poolInventory = (PlayerInventories)global::Game.ItemsOf(poolAura)!;
            if (poolInventory.Items(6).FirstOrDefault() is { } oldRing) poolInventory.Remove(6, oldRing.Id);
            var disciplineRing = ItemCatalog.Create(items.Find("Metadata/Items/Rings/Ring1")!) with
            {
                Mods = [Mod("DisciplineSkillCorrupted").Roll(ModSection.Implicit, new Random(11))],
            };
            poolInventory.Add(6, 0, 0, disciplineRing);
            poolAura.Pools = global::Game.PoolsFor(poolAura, 100);
            poolAura.PlayerLife = poolAura.Pools.Life;
            poolAura.PlayerMana = poolAura.Pools.Mana;
            uint baseEnergyShield = poolAura.Pools.EnergyShield;
            var discipline = global::Game.GemSkillEffects(poolAura)
                .Single(g => global::Game.SkillsFor(poolAura).EffectId(g.Effect) == "Discipline");
            Assert.True(CastAuraOn(poolAura, discipline.Id));
            Assert.True(poolAura.Pools.EnergyShield > baseEnergyShield);
            poolAura.Pools = poolAura.Pools with { ManaReservationEfficiencyPct = -100 };
            Assert.Equal(0, global::Game.ReservedMana(poolAura));
            Assert.DoesNotContain(poolAura.Auras.Values, a => a.EffectId == "Discipline");
            Assert.Equal(baseEnergyShield, poolAura.Pools.EnergyShield);
            PlayerInventories.Forget(poolAuraCharacter);

            // A different character with saved intent but no granting item gets no runtime aura.
            string absentCharacter = "WornMissing" + Guid.NewGuid().ToString("N");
            CharacterSaves.SetAuraActive(absentCharacter, "Envy", true);
            var absent = NewConnection(absentCharacter);
            absent.Pools = global::Game.PoolsFor(absent, 100);
            Assert.Empty(global::Game.RestoreRememberedAuras(absent, CharacterSaves.Load(absentCharacter)!.ActiveAuras!));
            PlayerInventories.Forget(absentCharacter);

            // Swapping the granting weapon out ends Envy and keeps the paid mana gone. The source's saved intent is
            // cleared, so returning it later is an explicit activation rather than an automatic resurrection.
            uint manaWithEnvy = conn.PlayerMana;
            inventory.SwapWeaponSets();
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "Envy");
            Assert.Equal(manaWithEnvy, conn.PlayerMana);
            Assert.True(manaWithEnvy <= keptMana);
            Assert.DoesNotContain("Envy", CharacterSaves.Load(character)?.ActiveAuras ?? []);

            // Death clears the restored source aura as it does a gem aura.
            lock (restored.Monsters) restored.PlayerLife = 0;
            global::Game.EndAurasOnDeath("test", wire.Stream, wire.Ciphers, restored).GetAwaiter().GetResult();
            Assert.Empty(restored.Auras);
            Assert.Empty(CharacterSaves.Load(character)!.ActiveAuras ?? []);

            // The reverse ownership ordering: an ordinary gem owns the family while an unrelated Doryani host is
            // removed. The gem aura and saved intent stay untouched.
            string gemOwnerCharacter = "WornGemOwner" + Guid.NewGuid().ToString("N");
            var gemOwner = NewConnection(gemOwnerCharacter);
            var gemInventory = (PlayerInventories)global::Game.ItemsOf(gemOwner)!;
            var gemPurity = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemLightningResistAura")!);
            var helmet = ItemCatalog.Create(items.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, gemPurity)],
            };
            var gemDoryani = uniques.Create("Doryani's Delusion", new Random(7))!;
            gemInventory.Add(PlayerInventories.Helmet, 0, 0, helmet);
            var gemDoryaniPlaced = gemInventory.Add(PlayerInventories.Boots, 0, 0, gemDoryani);
            gemOwner.Pools = global::Game.PoolsFor(gemOwner, 100);
            gemOwner.PlayerMana = gemOwner.Pools.Mana;
            var purities = global::Game.GemSkillEffects(gemOwner)
                .Where(g => global::Game.SkillsFor(gemOwner).EffectId(g.Effect) == "PurityOfLightning").ToArray();
            Assert.Equal(2, purities.Length);
            var gemOwned = purities.Single(g => global::Game.SupportedSkill(gemOwner, g.Id).WornGrant is null);
            Assert.Single(purities, g => global::Game.SupportedSkill(gemOwner, g.Id).WornGrant is not null);
            Assert.True(global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, gemOwner, gemOwned.Id,
                (0, 0), (0, 0)).GetAwaiter().GetResult());
            Assert.Null(Assert.Single(gemOwner.Auras.Values,
                a => a.EffectId == "PurityOfLightning").WornGrant);
            uint gemOwnerMana = gemOwner.PlayerMana;
            gemInventory.Remove(PlayerInventories.Boots, gemDoryaniPlaced.Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, gemOwner, gemInventory)
                .GetAwaiter().GetResult();
            Assert.Equal(gemOwned.Id, Assert.Single(gemOwner.Auras.Values,
                a => a.EffectId == "PurityOfLightning").SkillId);
            Assert.Equal(gemOwnerMana, gemOwner.PlayerMana);
            Assert.Contains("PurityOfLightning", CharacterSaves.Load(gemOwnerCharacter)!.ActiveAuras!);
            PlayerInventories.Forget(gemOwnerCharacter);

            // The same owner keeps item and skill Life reservations apart from full maximum Life, publishes the
            // reserved pool, and turns the real no-life-reserved spell rows on and off.
            string lifeCharacter = "ReservedLife" + Guid.NewGuid().ToString("N");
            var lifeConn = NewConnection(lifeCharacter);
            var lifeInventory = (PlayerInventories)global::Game.ItemsOf(lifeConn)!;
            if (lifeInventory.Items(PlayerInventories.BodyArmour).FirstOrDefault() is { } oldBody)
                lifeInventory.Remove(PlayerInventories.BodyArmour, oldBody.Id);
            if (lifeInventory.Items(PlayerInventories.Helmet).FirstOrDefault() is { } oldHelmet)
                lifeInventory.Remove(PlayerInventories.Helmet, oldHelmet.Id);
            if (lifeInventory.Items(PlayerInventories.Weapon1).FirstOrDefault() is { } oldWeapon)
                lifeInventory.Remove(PlayerInventories.Weapon1, oldWeapon.Id);
            var malachai = uniques.Create("Malachai's Awakening", new Random(21))! with
            {
                Sockets = [new ItemSocket(SocketColour.White, fireball),
                    new ItemSocket(SocketColour.White,
                        ItemCatalog.Create(items.Find("Metadata/Items/Gems/SupportGemLifetap")!))],
                Links = [2],
            };
            var ivory = uniques.Create("The Ivory Tower", new Random(22))!;
            var midnight = uniques.Create("Midnight Bargain", new Random(23))!;
            lifeInventory.Add(PlayerInventories.Helmet, 0, 0, malachai);
            lifeInventory.Add(PlayerInventories.BodyArmour, 0, 0, ivory);
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            lifeConn.PlayerLife = lifeConn.PlayerMaxLife;
            lifeConn.PlayerMana = lifeConn.Pools.Mana;
            Assert.Equal(0, global::Game.LifeReservationOf(lifeConn).Reserved);
            var unconstrained = global::Game.CharacterPlayerStats(lifeConn);
            Assert.True(unconstrained.GetValueOrDefault(
                Program.Stats.Key("spell_minimum_added_fire_damage")!.Value) > 0);
            Assert.True(unconstrained.GetValueOrDefault(
                Program.Stats.Key("spell_maximum_added_lightning_damage")!.Value) > 0);
            int Stat(Dictionary<uint, int> stats, string name) =>
                stats.GetValueOrDefault(Program.Stats.Key(name) ?? 0);
            var spellUnreserved = SpellDamage.Hit(name => Stat(unconstrained, name));

            var midnightPlacement = lifeInventory.Add(PlayerInventories.Weapon1, 0, 0, midnight);
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            // This is the first shared-owner query after qualification changed. Both fields must come from the same
            // reconciled snapshot, not an old current Life followed by a newly lowered reservation.
            var firstLifeReservationUpdate = global::Game.PlayerLifeUpdate(lifeConn);
            var reserved = global::Game.LifeReservationOf(lifeConn);
            Assert.Equal((int)Math.Floor(lifeConn.PlayerMaxLife * .3), reserved.ItemReserved);
            Assert.Equal(reserved.Maximum - reserved.Unreserved, reserved.Reserved);
            Assert.Equal(lifeConn.PlayerMaxLife, lifeConn.Pools.Life);
            Assert.Equal((uint)reserved.Reserved, global::Game.PlayerForCreation(lifeConn).LifeReserved);
            Assert.Equal((uint)reserved.Reserved,
                BinaryPrimitives.ReadUInt32BigEndian(firstLifeReservationUpdate.AsSpan(16)));
            Assert.Equal((uint)reserved.Unreserved,
                BinaryPrimitives.ReadUInt32BigEndian(firstLifeReservationUpdate.AsSpan(12)));
            Assert.Equal(lifeConn.PlayerLife,
                BinaryPrimitives.ReadUInt32BigEndian(firstLifeReservationUpdate.AsSpan(12)));
            var itemOnlyFlags = global::Game.ItemConditionFlags(lifeConn, lifeConn.Clock.Elapsed.TotalSeconds);
            Assert.False(itemOnlyFlags["_while_on_full_life"]);
            Assert.False(itemOnlyFlags["_while_no_life_is_reserved"]);
            var noLife = global::Game.CharacterPlayerStats(lifeConn);
            Assert.Equal(0, noLife.GetValueOrDefault(
                Program.Stats.Key("spell_minimum_added_fire_damage")!.Value));
            Assert.Equal(0, noLife.GetValueOrDefault(
                Program.Stats.Key("spell_maximum_added_lightning_damage")!.Value));
            var spellReserved = SpellDamage.Hit(name => Stat(noLife, name));
            Assert.True(spellUnreserved.Min > spellReserved.Min);
            Assert.True(spellUnreserved.Max > spellReserved.Max);
            var paidFireball = global::Game.GemSkillEffects(lifeConn)
                .Single(g => global::Game.SkillsFor(lifeConn).EffectId(g.Effect) == "Fireball");
            uint lifeBeforePayment = lifeConn.PlayerLife;
            Assert.True(global::Game.PayMana(lifeConn, paidFireball.Id, out _, out uint lifeCost));
            Assert.True(lifeCost > 0);
            Assert.Equal(lifeBeforePayment - lifeCost, lifeConn.PlayerLife);
            Assert.True(lifeConn.PlayerLife <= global::Game.LifeCap(lifeConn));

            // Ivory Tower's real +30 ES per 100 reserved Life is in the actual maximum ES pool.
            var ivoryKey = Program.Stats.Key(LifeReservation.EnergyShieldPerReservedStat)!.Value;
            var withIvory = lifeConn.Pools.EnergyShield;
            lifeConn.PlayerStats[ivoryKey] = 0;
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            var withoutIvory = lifeConn.Pools.EnergyShield;
            lifeConn.PlayerStats.Remove(ivoryKey);
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            Assert.True(withIvory > withoutIvory);
            Assert.Equal(withIvory, lifeConn.Pools.EnergyShield);
            var esToLife = Program.Stats.Key(
                "gain_maximum_life_instead_of_maximum_es_from_armour")!.Value;
            lifeConn.PlayerStats[esToLife] = 1;
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            uint convertedLife = lifeConn.Pools.Life;
            lifeConn.PlayerStats[ivoryKey] = 0;
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            Assert.Equal(convertedLife, lifeConn.Pools.Life);
            lifeConn.PlayerStats.Remove(ivoryKey);
            lifeConn.PlayerStats.Remove(esToLife);
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            uint capped = global::Game.LifeCap(lifeConn);
            var cappedRecovery = FlaskRecovery.Instant(
                new(100, 0, 1, 100, 0, 0, 0, 0, false, false, 0),
                capped - 5, lifeConn.PlayerMana,
                lifeConn.PlayerMaxLife, capped, int.MaxValue)!.Value;
            Assert.Equal(capped, cappedRecovery.Life);

            // Removing the item releases capacity but never restores current Life.
            lifeConn.PlayerLife = global::Game.LifeCap(lifeConn);
            uint constrained = lifeConn.PlayerLife;
            lifeInventory.Remove(PlayerInventories.Weapon1, midnightPlacement.Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, lifeConn, lifeInventory).GetAwaiter().GetResult();
            Assert.Equal(0, global::Game.LifeReservationOf(lifeConn).Reserved);
            Assert.Equal(constrained, lifeConn.PlayerLife);
            Assert.True(lifeConn.PlayerLife < lifeConn.PlayerMaxLife);

            // The item is inert while its real level requirement is not met.
            midnightPlacement = lifeInventory.Add(PlayerInventories.Weapon1, 0, 0, midnight);
            lifeConn.Pools = lifeConn.Pools with { Level = 18 };
            var firstLowLevelQuery = global::Game.CharacterPlayerStats(lifeConn);
            Assert.Contains(midnightPlacement.Id,
                global::Game.InactiveItems.GetValueOrDefault(lifeCharacter) ?? []);
            Assert.Equal(0, global::Game.LifeReservationOf(lifeConn).Reserved);
            Assert.True(firstLowLevelQuery.GetValueOrDefault(
                Program.Stats.Key("spell_minimum_added_fire_damage")!.Value) > 0);
            lifeConn.Pools = lifeConn.Pools with { Level = 100 };
            var firstRestoredQuery = global::Game.CharacterPlayerStats(lifeConn);
            Assert.True(global::Game.LifeReservationOf(lifeConn).Reserved > 0);
            Assert.Equal(0, firstRestoredQuery.GetValueOrDefault(
                Program.Stats.Key("spell_minimum_added_fire_damage")!.Value));

            // A late qualification pass must reuse this call's coherent conditions. Advancing the Fortification owner after
            // capture used to make that pass read its old clock and fault the whole player-update group.
            lifeConn.StatSourcesCache = null;
            var conflictEquipment = lifeInventory.EquipmentSnapshot();
            var fortification = global::Game.FortificationOf(lifeConn);
            double conflictAt = lifeConn.Clock.Elapsed.TotalSeconds;
            lock (lifeConn.Monsters) fortification.Read(conflictAt);
            var coherent = global::Game.EquipmentStatSources(lifeConn, conflictEquipment, null, null, () =>
            {
                lock (lifeConn.Monsters)
                {
                    double grantedAt = lifeConn.Clock.Elapsed.TotalSeconds;
                    fortification.State.GrantExplicitStacks(grantedAt, 1);
                    fortification.Read(grantedAt + 7);
                }
            });
            Assert.True(coherent.Sources.GetValueOrDefault(
                Program.Stats.Key(LifeReservation.ItemPercentStat)!.Value) > 0);
            var capturedFlags = lifeConn.StatSourcesCache!.Key.Flags!;
            Assert.False(capturedFlags["_while_no_life_is_reserved"]);
            Assert.False(capturedFlags["_while_fortified"]);
            Assert.Equal(0, fortification.Value.Current);
            fortification.Clear();

            // The same connection's continuous recovery and monster DoT paths complete a production step after the race.
            void RunPlayerUpdate(Func<PlayerUpdatesState, Task> loop)
            {
                PlayerUpdatesState? updates = null;
                double stepAt = lifeConn.Clock.Elapsed.TotalSeconds;
                updates = new PlayerUpdatesState(lifetime, new AdvancingOneStepSchedule(stepAt, () => updates!.Stop.Cancel()));
                lifeConn.PlayerUpdatesState = updates;
                Assert.True(lifeConn.PlayerUpdatesWorker.TryStart(lifeConn, lifetime, null, updates, out long generation));
                updates.Generation = generation;
                try
                {
                    loop(updates).GetAwaiter().GetResult();
                    Assert.True(lifeConn.PlayerUpdatesWorker.Current(lifetime, null, updates, generation));
                }
                finally
                {
                    lifeConn.PlayerUpdatesWorker.Complete(lifetime, null, updates, generation);
                    lifeConn.RetirePlayerUpdates();
                }
            }
            RunPlayerUpdate(updates => global::Game.RunResourceRecovery(
                "test", wire.Stream, wire.Ciphers, lifeConn, updates));
            uint bleedingId = 1_000_001;
            var bleeding = new SpawnedMonster("bleeding target", new(0, 0, 0), 0, 0, 100_000)
            {
                Life = 100_000,
                Rarity = MonsterRarity.Normal,
            };
            lock (lifeConn.Monsters)
            {
                lifeConn.Monsters[bleedingId] = bleeding;
                double worldNow = global::Game.ProjectileWorldTime(lifeConn);
                Assert.NotNull(global::Game.BleedOnHitLocked(lifeConn, bleedingId, bleeding,
                    new MonsterIgnites.Settings(100, 100, 100, 100, 100, 5), critical: false, worldNow - .5));
            }
            RunPlayerUpdate(updates => global::Game.RunPlayerTick(
                "test", wire.Stream, wire.Ciphers, lifeConn, updates));
            Assert.InRange(bleeding.Life, 1u, 99_999u);

            // Clear Mind's real jewel row is independent of Life reservation and turns off only while Mana remains
            // reserved. The control uses the production aura toggle rather than a injected condition flag.
            const int jewelSlot = 0;
            var jewelNode = Program.Passives.JewelNodeForSlot(jewelSlot)!.Value;
            lock (lifeConn.Monsters)
                if (!lifeConn.Passives.Contains(jewelNode)) lifeConn.Passives.Add(jewelNode);
            var clearMind = uniques.Create("Clear Mind", new Random(24))!;
            lifeInventory.Add(PlayerInventories.PassiveJewels, jewelSlot, 0, clearMind);
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            var clearMindNoMana = global::Game.CharacterPlayerStats(lifeConn);
            Assert.True(Stat(clearMindNoMana, "spell_damage_+%") > 0);
            var clearMindFireball = global::Game.GemSkillEffects(lifeConn)
                .Single(g => global::Game.SkillsFor(lifeConn).EffectId(g.Effect) == "Fireball");
            var clearSpellNoMana = global::Game.SupportedHit(lifeConn, clearMindFireball.Id);

            if (lifeInventory.Items(6).FirstOrDefault() is { } clearMindOldRing)
                lifeInventory.Remove(6, clearMindOldRing.Id);
            var hasteRing = ItemCatalog.Create(items.Find("Metadata/Items/Rings/Ring1")!) with
            {
                Mods = [Mod("HasteSkillCorrupted").Roll(ModSection.Implicit, new Random(25))],
            };
            var hastePlacement = lifeInventory.Add(6, 0, 0, hasteRing);
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            var clearMindHaste = global::Game.GemSkillEffects(lifeConn)
                .Single(g => global::Game.SkillsFor(lifeConn).EffectId(g.Effect) == "Haste");
            Assert.True(CastAuraOn(lifeConn, clearMindHaste.Id));
            var clearMindReservedMana = global::Game.CharacterPlayerStats(lifeConn);
            int clearMindDelta = Stat(clearMindNoMana, "spell_damage_+%")
                - Stat(clearMindReservedMana, "spell_damage_+%");
            Assert.InRange(clearMindDelta, 40, 60);
            var clearSpellReservedMana = global::Game.SupportedHit(lifeConn, clearMindFireball.Id);
            Assert.True(clearSpellNoMana.MinTotal > clearSpellReservedMana.MinTotal);
            Assert.True(clearSpellNoMana.MaxTotal > clearSpellReservedMana.MaxTotal);
            lifeInventory.Remove(6, hastePlacement.Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, lifeConn, lifeInventory)
                .GetAwaiter().GetResult();
            Assert.DoesNotContain(clearMindHaste.Id, lifeConn.Auras.Keys);
            Assert.True(Stat(global::Game.CharacterPlayerStats(lifeConn), "spell_damage_+%") > 0);

            var (relogLife, _) = global::Game.InitialVitals(true, null,
                global::Game.LifeCap(lifeConn), (uint)global::Game.UnreservedMana(lifeConn));
            Assert.Equal(global::Game.LifeCap(lifeConn), relogLife);
            var (savedOverCap, _) = global::Game.InitialVitals(true,
                new CharacterSaves.Save([], Life: lifeConn.PlayerMaxLife), global::Game.LifeCap(lifeConn),
                (uint)global::Game.UnreservedMana(lifeConn));
            Assert.Equal(global::Game.LifeCap(lifeConn), savedOverCap);

            // Arrogance's real support stat moves only its linked aura's Mana cost to Life.
            string arroganceCharacter = "ArroganceLife" + Guid.NewGuid().ToString("N");
            var arrogance = NewConnection(arroganceCharacter);
            var arroganceInventory = (PlayerInventories)global::Game.ItemsOf(arrogance)!;
            if (arroganceInventory.Items(PlayerInventories.Helmet).FirstOrDefault() is { } oldMagicHelmet)
                arroganceInventory.Remove(PlayerInventories.Helmet, oldMagicHelmet.Id);
            var purityGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemLightningResistAura")!);
            var arroganceGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SupportGemBloodMagic")!);
            var vitalityType = items.Gem(items.Find("Metadata/Items/Gems/SkillGemVitality")!)!;
            var vitalityGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemVitality")!)
                with { GemExperience = vitalityType.ExperienceFor(20) };
            var hasteGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemHaste")!);
            arroganceInventory.Add(PlayerInventories.Helmet, 0, 0,
                ItemCatalog.Create(items.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
                {
                    Sockets = [new ItemSocket(SocketColour.White, purityGem),
                        new ItemSocket(SocketColour.White, arroganceGem),
                        new ItemSocket(SocketColour.White, vitalityGem),
                        new ItemSocket(SocketColour.White, arroganceGem),
                        new ItemSocket(SocketColour.White, hasteGem)],
                    Links = [2, 2, 1],
                });
            arrogance.Pools = global::Game.PoolsFor(arrogance, 100);
            arrogance.PlayerLife = global::Game.LifeCap(arrogance);
            arrogance.PlayerMana = arrogance.Pools.Mana;
            var arrogancePurity = global::Game.GemSkillEffects(arrogance)
                .Single(g => global::Game.SkillsFor(arrogance).EffectId(g.Effect) == "PurityOfLightning");
            var cost = global::Game.SupportedReservationCost(arrogance, arrogancePurity.Id)!.Value;
            Assert.Equal(0, cost.ManaPercent);
            Assert.True(cost.LifePercent > 0);
            arrogance.PlayerStats[Program.Stats.Key(
                AuraCatalog.NoManaReservationStat("PurityOfLightning"))!.Value] = 1;
            var exemptCost = global::Game.SupportedReservationCost(arrogance, arrogancePurity.Id)!.Value;
            Assert.Equal(0, exemptCost.ManaPercent);
            Assert.Equal(cost.LifePercent, exemptCost.LifePercent);
            var haste = global::Game.GemSkillEffects(arrogance)
                .Single(g => global::Game.SkillsFor(arrogance).EffectId(g.Effect) == "Haste");
            var hasteCost = global::Game.SupportedReservationCost(arrogance, haste.Id)!.Value;
            Assert.True(hasteCost.ManaPercent > 0);
            Assert.Equal(0, hasteCost.LifePercent);
            Assert.True(CastAuraOn(arrogance, arrogancePurity.Id));
            int arroganceReserved = global::Game.LifeReservationOf(arrogance).Reserved;
            Assert.True(arroganceReserved > 0);
            Assert.True(arrogance.PlayerLife < arrogance.PlayerMaxLife);
            var vitality = global::Game.GemSkillEffects(arrogance)
                .Single(g => global::Game.SkillsFor(arrogance).EffectId(g.Effect) == "Vitality");
            Assert.Equal(20, global::Game.SupportedSkill(arrogance, vitality.Id).Level);
            var vitalityCost = global::Game.SupportedReservationCost(arrogance, vitality.Id)!.Value;
            var proposed = arrogance.Auras.Values.Append(new AuraCatalog.Aura(vitality.Id, vitality.Effect,
                "Vitality", vitality.Name, 1, 1, vitalityCost.ManaFlat, vitalityCost.ManaPercent,
                vitalityCost.LifeFlat, vitalityCost.LifePercent));
            Assert.True(LifeReservation.RequiredSkillLife(arrogance.Pools, proposed) >= arrogance.Pools.Life,
                $"Purity {cost.LifePercent}, Vitality {vitalityCost.LifePercent}/{vitalityCost.LifeFlat}, " +
                $"required {LifeReservation.RequiredSkillLife(arrogance.Pools, proposed)}/{arrogance.Pools.Life}");
            uint lifeBeforeRefusal = arrogance.PlayerLife;
            int capBeforeRefusal = arrogance.PlayerLifeCap;
            var vitalitySupported = global::Game.SupportedSkill(arrogance, vitality.Id);
            Assert.False(global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, arrogance, vitality.Id,
                vitality.Effect, vitality.Name, vitalityCost, vitalitySupported.Level, vitalitySupported.Quality)
                .GetAwaiter().GetResult());
            Assert.Equal(lifeBeforeRefusal, arrogance.PlayerLife);
            Assert.Equal(capBeforeRefusal, arrogance.PlayerLifeCap);
            Assert.Contains(arrogance.Auras.Values, aura => aura.EffectId == "PurityOfLightning");
            Assert.Equal(arroganceReserved, global::Game.LifeReservationOf(arrogance).Reserved);
            Assert.False(CastAuraOn(arrogance, vitality.Id));
            Assert.DoesNotContain(vitality.Id, arrogance.Auras.Keys);
            Assert.DoesNotContain("Vitality", CharacterSaves.Load(arroganceCharacter)?.ActiveAuras ?? []);
            Assert.Contains("PurityOfLightning", CharacterSaves.Load(arroganceCharacter)?.ActiveAuras ?? []);

            // Saved intent is admitted as one set and trims an over-capacity source instead of restoring cap zero.
            arrogance.Auras.Clear();
            CharacterSaves.SetAuraActive(arroganceCharacter, "PurityOfLightning", true);
            CharacterSaves.SetAuraActive(arroganceCharacter, "Vitality", true);
            uint lifeBeforeRestore = arrogance.PlayerLife;
            var restoredLifeAuras = global::Game.RestoreRememberedAuras(arrogance,
                CharacterSaves.Load(arroganceCharacter)!.ActiveAuras!);
            Assert.Single(restoredLifeAuras);
            Assert.Contains(arrogance.Auras.Values, aura => aura.EffectId == "PurityOfLightning");
            Assert.DoesNotContain(arrogance.Auras.Values, aura => aura.EffectId == "Vitality");
            Assert.DoesNotContain("Vitality", CharacterSaves.Load(arroganceCharacter)!.ActiveAuras!);
            Assert.True(arrogance.PlayerLife >= 1 && arrogance.PlayerLife <= global::Game.LifeCap(arrogance));
            Assert.Equal(lifeBeforeRestore, arrogance.PlayerLife);

            // Positive efficiency reprices the live source; the crossing to over 100% retires it without healing.
            arrogance.Pools = arrogance.Pools with { LifeReservationEfficiencyPct = 100 };
            global::Game.EnsureOwnAuraPools(arrogance);
            Assert.Contains(arrogance.Auras.Values, aura => aura.EffectId == "PurityOfLightning");
            Assert.True(global::Game.LifeReservationOf(arrogance).Reserved < arroganceReserved);
            uint lifeBeforeTrim = arrogance.PlayerLife;
            arrogance.Pools = arrogance.Pools with { LifeReservationEfficiencyPct = -50 };
            global::Game.EnsureOwnAuraPools(arrogance);
            Assert.Empty(arrogance.Auras);
            Assert.Empty(CharacterSaves.Load(arroganceCharacter)?.ActiveAuras ?? []);
            Assert.Equal(lifeBeforeTrim, arrogance.PlayerLife);

            PlayerInventories.Forget(arroganceCharacter);
            PlayerInventories.Forget(lifeCharacter);

            // A real Mods row with a differing Ruthless GEPL reference selects the native HardMode effect in HardMode
            // and retains the normal row in Standard. This exercises the generic owner, not the unaffected Purity/Envy rows.
            ClientState DeterminationConnection(bool hardMode, string name)
            {
                var mode = NewConnection(name, hardMode);
                var modeItems = (ItemCatalog)global::Game.ItemsFor(mode)!;
                var modeInventory = (PlayerInventories)global::Game.ItemsOf(mode)!;
                var determination = modeItems.Mods.ById("DeterminationSkillCorrupted")!;
                modeInventory.Add(amulet, 0, 0, ItemCatalog.Create(
                    modeItems.Find("Metadata/Items/Amulets/Amulet1")!) with
                {
                    Mods = [determination.Roll(ModSection.Explicit, new Random(hardMode ? 8 : 9))],
                });
                mode.Pools = global::Game.PoolsFor(mode, 100);
                mode.PlayerMana = mode.Pools.Mana;
                return mode;
            }
            string normalDeterminationCharacter = "NormalDetermination" + Guid.NewGuid().ToString("N");
            string ruthlessDeterminationCharacter = "RuthlessDetermination" + Guid.NewGuid().ToString("N");
            var normalDetermination = DeterminationConnection(false, normalDeterminationCharacter);
            var ruthlessDetermination = DeterminationConnection(true, ruthlessDeterminationCharacter);
            var normalGrant = global::Game.GemSkillEffects(normalDetermination).Single(g =>
                global::Game.SkillsFor(normalDetermination).EffectId(g.Effect) == "Determination");
            var ruthlessGrant = global::Game.GemSkillEffects(ruthlessDetermination).Single(g =>
                global::Game.SkillsFor(ruthlessDetermination).EffectId(g.Effect) == "DeterminationHardMode");
            Assert.Equal((17758, 1998, 16, false),
                (global::Game.SupportedSkill(normalDetermination, normalGrant.Id).WornGrant!.Value.EffectsPerLevelRow,
                    normalGrant.Effect, normalGrant.Level,
                    global::Game.SupportedSkill(normalDetermination, normalGrant.Id).WornGrant!.Value.Ruthless));
            Assert.Equal((17798, 1999, 16, true),
                (global::Game.SupportedSkill(ruthlessDetermination, ruthlessGrant.Id).WornGrant!.Value.EffectsPerLevelRow,
                    ruthlessGrant.Effect, ruthlessGrant.Level,
                    global::Game.SupportedSkill(ruthlessDetermination, ruthlessGrant.Id).WornGrant!.Value.Ruthless));
            Assert.Equal((uint)17758, global::Game.PlayerSkills(normalDetermination)
                .Single(s => s.Id == normalGrant.Id).EffectsPerLevelRow);
            Assert.Equal((uint)17798, global::Game.PlayerSkills(ruthlessDetermination)
                .Single(s => s.Id == ruthlessGrant.Id).EffectsPerLevelRow);
            PlayerInventories.Forget(normalDeterminationCharacter);
            PlayerInventories.Forget(ruthlessDeterminationCharacter);

            // An id removed for a skill never becomes a different newly added worn skill in the same publication.
            var ids = new GemSkillIds();
            ushort? first = ids.Assign([new(10, -1, puritySource.Effect, GemSkillIds.SourceKind.WornGrant)],
                global::Game.GemSkillIdPool.Value).Single();
            ushort? next = ids.Assign([new(11, -1, envySources[0].Effect, GemSkillIds.SourceKind.WornGrant)],
                global::Game.GemSkillIdPool.Value).Single();
            Assert.NotNull(first);
            Assert.NotNull(next);
            Assert.NotEqual(first, next);
        }
        finally
        {
            PlayerInventories.Forget(character);
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    void Incoming_damage_controls_run_through_the_shared_production_owner()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-incoming-controls-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        try
        {
        ClientState Fresh()
        {
            var conn = new ClientState(new(1, "incoming-controls", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 0, 0),
                PlayerLife = 1000,
            };
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            conn.PlayerLife = conn.PlayerMaxLife;
            conn.EnergyShield.SetMaximum(0, refill: true);
            conn.Clock.Stop();
            return conn;
        }

        static void Advance(ClientState conn, double from, double until, Dictionary<uint, int>? stats = null)
        {
            lock (conn.Monsters)
                Game.AdvancePlayerAilmentsLocked(conn, from, until, stats ?? []);
        }

        // A stationary player takes the standing bleed rate plus poison. Movement is Walk.Moving, not the
        // absence of the unique that disables its extra damage.
        var stationary = Fresh();
        double now = stationary.Clock.Elapsed.TotalSeconds;
        Assert.True(stationary.IncomingAilments.ApplyBleed(now, 10, 5));
        Assert.True(stationary.IncomingAilments.ApplyPoison(now, 10, 5));
        uint before = stationary.PlayerLife;
        Advance(stationary, now, now + 1);
        Assert.Equal(before - 20, stationary.PlayerLife);

        var moving = Fresh();
        now = moving.Clock.Elapsed.TotalSeconds;
        Assert.True(moving.IncomingAilments.ApplyBleed(now, 10, 5));
        Assert.True(moving.IncomingAilments.ApplyPoison(now, 10, 5));
        var (x, y) = moving.Walk.Position(now);
        moving.Walk.Order(x + 100, y, now);
        Assert.True(moving.Walk.Moving(now));
        before = moving.PlayerLife;
        Advance(moving, now, now + 1);
        Assert.Equal(before - 40, moving.PlayerLife);

        var exempt = Fresh();
        now = exempt.Clock.Elapsed.TotalSeconds;
        Assert.True(exempt.IncomingAilments.ApplyBleed(now, 10, 5));
        Assert.True(exempt.IncomingAilments.ApplyPoison(now, 10, 5));
        exempt.Walk.Order(exempt.Walk.Position(now).X + 100, exempt.Walk.Position(now).Y, now);
        var exemptStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("no_extra_bleed_damage_while_moving")!.Value] = 1,
        };
        before = exempt.PlayerLife;
        Advance(exempt, now, now + 1, exemptStats);
        Assert.Equal(before - 20, exempt.PlayerLife);

        // A weaker accepted bleed survives the stronger one and becomes the active instance.
        var bleeds = Fresh();
        now = bleeds.Clock.Elapsed.TotalSeconds;
        Assert.True(bleeds.IncomingAilments.ApplyBleed(now, 10, 3));
        Assert.True(bleeds.IncomingAilments.ApplyBleed(now + 1, 5, 5));
        Assert.Equal(10, bleeds.IncomingAilments.BleedPerSecond(now + 2));
        before = bleeds.PlayerLife;
        Advance(bleeds, now, now + 3);
        Assert.Equal(before - 30, bleeds.PlayerLife);
        before = bleeds.PlayerLife;
        Advance(bleeds, now + 3, now + 6);
        Assert.Equal(before - 15, bleeds.PlayerLife);
        Assert.False(bleeds.IncomingAilments.Bleeding(now + 6));

        // An instance first applied inside the interval contributes from its own start.
        var inside = Fresh();
        now = inside.Clock.Elapsed.TotalSeconds;
        Assert.True(inside.IncomingAilments.ApplyPoison(now + 1, 10, 2));
        before = inside.PlayerLife;
        Advance(inside, now, now + 2);
        Assert.Equal(before - 10, inside.PlayerLife);

        // Splitting an interval must not change the result, and re-presenting it must not consume it twice.
        var whole = Fresh();
        var split = Fresh();
        now = whole.Clock.Elapsed.TotalSeconds;
        foreach (var conn in new[] { whole, split })
        {
            Assert.True(conn.IncomingAilments.ApplyBleed(now, 7.5, 3));
            Assert.True(conn.IncomingAilments.ApplyPoison(now + .75, 4.5, 1.25));
        }
        uint wholeBefore = whole.PlayerLife, splitBefore = split.PlayerLife;
        Advance(whole, now, now + 2);
        Advance(split, now, now + .8);
        Advance(split, now + .8, now + 2);
        Assert.Equal(wholeBefore - whole.PlayerLife, splitBefore - split.PlayerLife);
        uint after = split.PlayerLife;
        Advance(split, now, now + 2);
        Assert.Equal(after, split.PlayerLife);

        // Resistance and typed damage-taken fractions survive every update partition, including poison's ES route.
        var resistedWhole = Fresh();
        var resistedSplit = Fresh();
        foreach (var conn in new[] { resistedWhole, resistedSplit })
        {
            conn.Pools = conn.Pools with { ChaosResistance = 50 };
            Assert.True(conn.IncomingAilments.ApplyPoison(now, 1, 4));
        }
        before = resistedWhole.PlayerLife;
        Advance(resistedWhole, now, now + 4);
        uint resistedLife = resistedSplit.PlayerLife;
        for (int i = 0; i < 4; i++) Advance(resistedSplit, now + i, now + i + 1);
        Assert.Equal(2u, before - resistedWhole.PlayerLife);
        Assert.Equal(2u, resistedLife - resistedSplit.PlayerLife);

        var negativeWhole = Fresh();
        var negativeSplit = Fresh();
        foreach (var conn in new[] { negativeWhole, negativeSplit })
        {
            conn.Pools = conn.Pools with { ChaosResistance = -50 };
            Assert.True(conn.IncomingAilments.ApplyPoison(now, 1, 2));
        }
        before = negativeWhole.PlayerLife;
        Advance(negativeWhole, now, now + 2);
        uint negativeLife = negativeSplit.PlayerLife;
        for (int i = 0; i < 2; i++) Advance(negativeSplit, now + i, now + i + 1);
        Assert.Equal(3u, before - negativeWhole.PlayerLife);
        Assert.Equal(3u, negativeLife - negativeSplit.PlayerLife);

        var typedWhole = Fresh();
        var typedSplit = Fresh();
        var typedStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("chaos_damage_taken_+%")!.Value] = 50,
        };
        foreach (var conn in new[] { typedWhole, typedSplit })
        {
            conn.Pools = conn.Pools with { ChaosResistance = 50 };
            Assert.True(conn.IncomingAilments.ApplyPoison(now, 1, 2));
        }
        before = typedWhole.PlayerLife;
        Advance(typedWhole, now, now + 2, typedStats);
        uint typedLife = typedSplit.PlayerLife;
        Advance(typedSplit, now, now + 1, typedStats);
        Advance(typedSplit, now + 1, now + 2, typedStats);
        Assert.Equal(1u, before - typedWhole.PlayerLife);
        Assert.Equal(1u, typedLife - typedSplit.PlayerLife);

        var esWhole = Fresh();
        var esSplit = Fresh();
        var noBypassStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("chaos_damage_does_not_bypass_energy_shield")!.Value] = 1,
        };
        foreach (var conn in new[] { esWhole, esSplit })
        {
            conn.Pools = conn.Pools with { ChaosResistance = 50 };
            conn.EnergyShield.SetMaximum(100, refill: true);
            Assert.True(conn.IncomingAilments.ApplyPoison(now, 1, 4));
        }
        uint esWholeBefore = esWhole.EnergyShield.Current, esSplitBefore = esSplit.EnergyShield.Current;
        Advance(esWhole, now, now + 4, noBypassStats);
        for (int i = 0; i < 4; i++) Advance(esSplit, now + i, now + i + 1, noBypassStats);
        Assert.Equal(esWholeBefore - 2, esWhole.EnergyShield.Current);
        Assert.Equal(esSplitBefore - 2, esSplit.EnergyShield.Current);
        Assert.Equal(esWhole.PlayerLife, esSplit.PlayerLife);

        var transitioned = Fresh();
        transitioned.Pools = transitioned.Pools with { ChaosResistance = 50 };
        Assert.True(transitioned.IncomingAilments.ApplyPoison(now, 1, 4));
        Advance(transitioned, now, now + 1);
        before = transitioned.PlayerLife;
        var typedChaosImmunity = new Dictionary<uint, int>
        {
            [Program.Stats.Key("chaos_damage_immunity")!.Value] = 1,
        };
        Advance(transitioned, now + 1, now + 2, typedChaosImmunity);
        Assert.Equal(before, transitioned.PlayerLife);
        Assert.Equal(1, transitioned.IncomingAilments.PoisonStacks(now + 2));
        before = transitioned.PlayerLife;
        Advance(transitioned, now + 2, now + 4);
        Assert.Equal(before - 1, transitioned.PlayerLife);
        Assert.Equal(0, transitioned.IncomingAilments.PoisonStacks(now + 4));

        // A sub-point earned by an expired instance does not charge a later, unrelated application.
        var idleGap = Fresh();
        Assert.True(idleGap.IncomingAilments.ApplyPoison(now, .6, 1));
        before = idleGap.PlayerLife;
        Advance(idleGap, now, now + 1);
        Assert.Equal(before, idleGap.PlayerLife);
        Assert.True(idleGap.IncomingAilments.ApplyPoison(now + 2, .6, 1));
        before = idleGap.PlayerLife;
        Advance(idleGap, now + 2, now + 3);
        Assert.Equal(before, idleGap.PlayerLife);

        // Unaffected keeps the ailment but neither damages nor leaves stale fraction to charge later.
        var unaffected = Fresh();
        now = unaffected.Clock.Elapsed.TotalSeconds;
        Assert.True(unaffected.IncomingAilments.ApplyPoison(now, 1.5, 1));
        var unaffectedStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("virtual_unaffected_by_poison")!.Value] = 1,
        };
        before = unaffected.PlayerLife;
        Advance(unaffected, now, now + 1, unaffectedStats);
        Assert.Equal(before, unaffected.PlayerLife);
        Assert.True(unaffected.IncomingAilments.PoisonStacks(now + 1) == 0);
        Assert.True(unaffected.IncomingAilments.ApplyPoison(now + 1, 1.5, 1));
        Assert.Equal(1, unaffected.IncomingAilments.PoisonStacks(now + 1));
        Assert.Equal(1.5, unaffected.IncomingAilments.PoisonPerSecond(now + 1));
        lock (unaffected.Monsters)
            Game.AdvancePlayerAilmentsLocked(unaffected, now + 1, now + 2, new Dictionary<uint, int>());
        Assert.Equal(before - 1, unaffected.PlayerLife);

        // Poison is chaos damage: typed chaos immunity retains the stack but suppresses only its damage.
        var chaosImmune = Fresh();
        chaosImmune.EnergyShield.SetMaximum(100, refill: true);
        now = chaosImmune.Clock.Elapsed.TotalSeconds;
        Assert.True(chaosImmune.IncomingAilments.ApplyPoison(now, 10, 2));
        var immuneStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("chaos_damage_immunity")!.Value] = 1,
        };
        before = chaosImmune.PlayerLife;
        uint shield = chaosImmune.EnergyShield.Current;
        Advance(chaosImmune, now, now + 1, immuneStats);
        Assert.Equal(before, chaosImmune.PlayerLife);
        Assert.Equal(shield, chaosImmune.EnergyShield.Current);
        Assert.Equal(1, chaosImmune.IncomingAilments.PoisonStacks(now + 1));

        // Ordinary poison bypasses ES; Shavronne's chaos-does-not-bypass source lets ES absorb it.
        var ordinaryPoison = Fresh();
        ordinaryPoison.EnergyShield.SetMaximum(100, refill: true);
        now = ordinaryPoison.Clock.Elapsed.TotalSeconds;
        Assert.True(ordinaryPoison.IncomingAilments.ApplyPoison(now, 10, 2));
        before = ordinaryPoison.PlayerLife;
        shield = ordinaryPoison.EnergyShield.Current;
        Advance(ordinaryPoison, now, now + 1);
        Assert.Equal(before - 10, ordinaryPoison.PlayerLife);
        Assert.Equal(shield, ordinaryPoison.EnergyShield.Current);

        var shavronne = Fresh();
        shavronne.EnergyShield.SetMaximum(100, refill: true);
        now = shavronne.Clock.Elapsed.TotalSeconds;
        Assert.True(shavronne.IncomingAilments.ApplyPoison(now, 10, 2));
        var noBypass = new Dictionary<uint, int>
        {
            [Program.Stats.Key("chaos_damage_does_not_bypass_energy_shield")!.Value] = 1,
        };
        before = shavronne.PlayerLife;
        shield = shavronne.EnergyShield.Current;
        Advance(shavronne, now, now + 1, noBypass);
        Assert.Equal(before, shavronne.PlayerLife);
        Assert.Equal(shield - 10, shavronne.EnergyShield.Current);

        // Bleeding is physical, goes through ES, and Ward never absorbs damage over time.
        var bleeding = Fresh();
        bleeding.EnergyShield.SetMaximum(100, refill: true);
        bleeding.Ward.SetMaximum(100);
        now = bleeding.Clock.Elapsed.TotalSeconds;
        Assert.True(bleeding.IncomingAilments.ApplyBleed(now, 10, 2));
        before = bleeding.PlayerLife;
        shield = bleeding.EnergyShield.Current;
        int ward = (int)bleeding.Ward.Current;
        Advance(bleeding, now, now + 1);
        Assert.Equal(before, bleeding.PlayerLife);
        Assert.Equal(shield - 10, bleeding.EnergyShield.Current);
        Assert.Equal((uint)ward, bleeding.Ward.Current);
        Assert.False(bleeding.Ward.Broken);

        // Relevant typed damage taken and non-hit physical reduction reach the final DoT writer.
        var defended = Fresh();
        now = defended.Clock.Elapsed.TotalSeconds;
        Assert.True(defended.IncomingAilments.ApplyBleed(now, 10, 2));
        var defenceStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("physical_damage_taken_+%")!.Value] = 100,
            [Program.Stats.Key("base_additional_physical_damage_reduction_%")!.Value] = 50,
        };
        Assert.Equal(10, defended.IncomingAilments.BleedPerSecond(now));
        Assert.Equal(50, ChargeEffects.Reduction(name => defenceStats.GetValueOrDefault(Program.Stats.Key(name) ?? 0)).Physical);
        before = defended.PlayerLife;
        Advance(defended, now, now + 1, defenceStats);
        Assert.Equal(before - 10, defended.PlayerLife);

        // The extracted map and monster rows reach the same effects/admission path.
        Dictionary<string, int> Row(string id) => Mod(id).Stats.Where(s => s.Min != 0)
            .ToDictionary(s => s.Id, s => s.Min);
        var poisoning = Game.MapMonsterEffects(new MapMods(Row("MapPoisoning")));
        Assert.Equal(100, poisoning["base_chance_to_poison_on_hit_%"]);
        Assert.False(poisoning.ContainsKey("global_poison_on_hit"));
        var uberPoison = Game.MapMonsterEffects(new MapMods(Row("MapUberModPoison")));
        Assert.Equal(100, uberPoison["base_poison_duration_+%"]);
        Assert.Equal(1, uberPoison["all_damage_can_poison"]);

        MonsterModSet ContactMods(string id)
        {
            var rolled = Mod(id).Roll(ModSection.Explicit, new Random(1));
            return Game.MonsterModRolls.Value.Create([rolled], [], MonsterRarity.Normal, 1);
        }
        var bleedContact = ContactMods("MonsterBleedOnHitChance");
        Assert.Equal(25, bleedContact.Effects["bleed_on_hit_with_attacks_%"]);
        var poisonedContact = ContactMods("MonsterNemesisAttacksPoison");
        Assert.Equal(1, poisonedContact.Effects["base_attacks_poison"]);
        var sawbones = global::Game.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonMeleeSawbones")!;
        Assert.Contains(sawbones.SpeciesModsOrEmpty, row =>
            Program.Items.Mods.Row(row)?.Id == "MonsterBleedOnHitChance");
        var sawbonesMods = global::Game.MonsterModRolls.Value.Species(sawbones.SpeciesModsOrEmpty, new Random(1));
        var sawbonesSet = global::Game.MonsterModRolls.Value.Create(sawbonesMods, [], MonsterRarity.Normal, 1);
        Assert.Equal(25, sawbonesSet.Effects["bleed_on_hit_with_attacks_%"]);
        Assert.Equal(25, Game.MonsterModsFor(Fresh(), sawbones, 20, MonsterRarity.Normal, null, alone: true)
            .Effects["bleed_on_hit_with_attacks_%"]);

        static void Contact(ClientState conn, MonsterModEffects effects, DamagePacket hit, bool attack)
        {
            var monster = new SpawnedMonster("source", new PlayerWalk(10, 10, 0), 0, 0, 100)
            {
                Mods = new MonsterModSet([], [], effects),
            };
            lock (conn.Monsters)
                Game.ApplyMonsterDamagingAilmentsLocked(conn, 77, monster, hit, false,
                    conn.Clock.Elapsed.TotalSeconds, attack);
        }

        var bleedChance = Fresh();
        bleedChance.IncomingHitRandom = new Random(7);
        now = bleedChance.Clock.Elapsed.TotalSeconds;
        for (int i = 0; i < 20 && !bleedChance.IncomingAilments.Bleeding(now); i++)
            Contact(bleedChance, bleedContact.Effects,
                DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), attack: true);
        Assert.True(bleedChance.IncomingAilments.Bleeding(now));
        var noAttackBleed = Fresh();
        Contact(noAttackBleed, bleedContact.Effects,
            DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), attack: false);
        Assert.False(noAttackBleed.IncomingAilments.Bleeding(bleedChance.Clock.Elapsed.TotalSeconds));

        var attackPoison = Fresh();
        var attackAt = attackPoison.Clock.Elapsed.TotalSeconds;
        Contact(attackPoison, poisonedContact.Effects,
            DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), attack: true);
        Assert.Equal(1, attackPoison.IncomingAilments.PoisonStacks(attackAt));
        Assert.Equal(30, attackPoison.IncomingAilments.PoisonPerSecond(attackAt));
        var spellPoison = Fresh();
        Contact(spellPoison, poisonedContact.Effects,
            DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), attack: false);
        Assert.Equal(0, spellPoison.IncomingAilments.PoisonStacks(spellPoison.Clock.Elapsed.TotalSeconds));
        var immuneTarget = new SpawnedMonster("immune", new PlayerWalk(10, 10, 0), 0, 0, 100)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
            {
                ["cannot_be_poisoned"] = 1,
            })),
        };
        var playerChance = new Dictionary<uint, int>
        {
            [Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100,
        };
        Assert.False(Game.TryPoisonOnHitLocked(spellPoison, immuneTarget,
            DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 10, 10), false, false,
            playerChance, null, new MonsterPoisons.Settings(100, Min: 10, Max: 10, CriticalMin: 10,
                CriticalMax: 10, Seconds: 2), spellPoison.Clock.Elapsed.TotalSeconds, new Random(1),
            out _, out _));

        var regularMap = Fresh();
        var regularAt = regularMap.Clock.Elapsed.TotalSeconds;
        Contact(regularMap, new MonsterModEffects(poisoning),
            DamagePacket.Of(GameServer.Core.World.DamageType.Fire, 100, 100), attack: true);
        Assert.Equal(0, regularMap.IncomingAilments.PoisonStacks(regularAt));
        var regularPhysical = Fresh();
        var regularPhysicalAt = regularPhysical.Clock.Elapsed.TotalSeconds;
        Contact(regularPhysical, new MonsterModEffects(poisoning),
            DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), attack: true);
        Assert.Equal(1, regularPhysical.IncomingAilments.PoisonStacks(regularPhysicalAt));
        var uberMap = Fresh();
        var uberAt = uberMap.Clock.Elapsed.TotalSeconds;
        Contact(uberMap, new MonsterModEffects(uberPoison),
            DamagePacket.Of(GameServer.Core.World.DamageType.Fire, 100, 100), attack: true);
        Assert.Equal(1, uberMap.IncomingAilments.PoisonStacks(uberAt));
        Assert.Equal(Ailments.PoisonDurationSeconds * 2,
            uberMap.IncomingAilments.PoisonUntil(uberAt) - uberAt, 6);

        // Native presentation observes temporal identity, not only a boolean/count. A refreshed bleed or a
        // same-count poison replacement therefore refreshes its deadline.
        var temporalBefore = new IncomingDamagingAilments.State(true, now + 1, 1, now + 3, 4, 7);
        var temporalAfter = new IncomingDamagingAilments.State(true, now + 5, 1, now + 8, 5, 8);
        var presentation = Game.IncomingAilmentBuffs(temporalBefore, temporalAfter, now);
        Assert.Contains(presentation, p => p.Log.Contains("bleeding ends", StringComparison.Ordinal));
        Assert.Contains(presentation, p => p.Log.Contains("player bleeding", StringComparison.Ordinal));
        Assert.Contains(presentation, p => p.Log.Contains("poison ends", StringComparison.Ordinal));
        Assert.Contains(presentation, p => p.Log.Contains("player poisoned (1)", StringComparison.Ordinal));

        var lifecycle = Fresh();
        now = lifecycle.Clock.Elapsed.TotalSeconds;
        Assert.True(lifecycle.IncomingAilments.ApplyPoison(now, 1, 5));
        var oldPresentation = lifecycle.IncomingAilments.Snapshot(now);
        lifecycle.PresentedIncomingAilments = oldPresentation;
        lifecycle.InvalidateIncomingHits();
        Assert.Null(lifecycle.PresentedIncomingAilments);
        Assert.Equal(lifecycle.IncomingHitEpoch, lifecycle.PresentedIncomingAilmentEpoch);
        Assert.Equal(0, lifecycle.IncomingAilments.PoisonStacks(now));
        Assert.NotEqual(oldPresentation, lifecycle.IncomingAilments.Snapshot(now));

        // Losing the reflection source changes future admission only. Real immunity remains a removal state.
        var reflected = Fresh();
        now = reflected.Clock.Elapsed.TotalSeconds;
        Assert.True(reflected.IncomingAilments.ApplyPoison(now, 1, 5, reflected: true));
        Game.CharacterPlayerStats(reflected);
        Assert.Equal(1, reflected.IncomingAilments.PoisonStacks(now));
        Assert.True(reflected.IncomingAilments.ApplyPoison(now, 1, 5, reflected: true));
        Assert.Equal(2, reflected.IncomingAilments.PoisonStacks(now));
        var immuneReflection = new Dictionary<uint, int>
        {
            [Program.Stats.Key("immune_to_poison")!.Value] = 1,
        };
        int ImmuneStat(string name) => immuneReflection.GetValueOrDefault(Program.Stats.Key(name) ?? 0);
        lock (reflected.Monsters) Assert.True(reflected.IncomingAilments.Reconcile(ImmuneStat));
        Assert.Equal(0, reflected.IncomingAilments.PoisonStacks(now));

        var prevented = Fresh();
        now = prevented.Clock.Elapsed.TotalSeconds;
        Assert.True(prevented.IncomingAilments.ApplyBleed(now, 5, 5));
        var preventionStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("cannot_gain_bleeding")!.Value] = 1,
        };
        before = prevented.PlayerLife;
        Advance(prevented, now, now + 1, preventionStats);
        Assert.Equal(before, prevented.PlayerLife);
        Assert.False(prevented.IncomingAilments.Bleeding(now + 1));

        // Esh's Visage's extracted conditional non-bypass is decided from live low life and reaches the plain
        // source that poison consumes.
        var esh = Fresh();
        var itemCatalog = (ItemCatalog)Game.ItemsFor(esh)!;
        var eshItem = new UniqueCatalog(itemCatalog).Create("Esh's Visage", new Random(1))!;
        Assert.Contains(eshItem.Mods, m => itemCatalog.Mods.ByHash(m.Hash)?.Id ==
            "ChaosDamageDoesNotBypassESNotLowLifeOrManaUnique__1");
        Assert.Equal(1, ItemModStats.Sum([eshItem], itemCatalog.Mods, subject: new StatConditions.Subject(
            Flags: new Dictionary<string, bool> { ["_while_not_low_life"] = true }), statExists: Program.Stats.Has)
            .GetValueOrDefault("chaos_damage_does_not_bypass_energy_shield"));
        var eshPlacement = ((PlayerInventories)Game.ItemsOf(esh)!).Add(PlayerInventories.Offhand1, 0, 0, eshItem);
        esh.Pools = esh.Pools with { Level = 80 };
        esh.PlayerStats[Program.Stats.Key("additional_strength")!.Value] = 500;
        esh.PlayerStats[Program.Stats.Key("additional_intelligence")!.Value] = 500;
        Game.CharacterPlayerStats(esh);
        esh.PlayerLife = esh.PlayerMaxLife;
        var fullLife = Game.CharacterPlayerStats(esh);
        Assert.DoesNotContain(eshPlacement.Id, esh.StatSourcesCache!.Inactive);
        Assert.True(Game.ItemConditionFlags(esh, esh.Clock.Elapsed.TotalSeconds)["_while_not_low_life"]);
        uint noBypassKey = Program.Stats.Key("chaos_damage_does_not_bypass_energy_shield")!.Value;
        Assert.Equal(1, esh.StatSourcesCache!.Sources.GetValueOrDefault(noBypassKey));
        Assert.Equal(1, fullLife.GetValueOrDefault(noBypassKey));
        esh.PlayerLife = (uint)Math.Max(1, esh.PlayerMaxLife / 4);
        var lowLifeStats = Game.CharacterPlayerStats(esh);
        Assert.Equal(0, lowLifeStats.GetValueOrDefault(noBypassKey));

        var flask = Fresh();
        now = flask.Clock.Elapsed.TotalSeconds;
        Assert.True(flask.IncomingAilments.ApplyPoison(now, 1, 5));
        var amethyst = itemCatalog.Find("Amethyst Flask")!;
        var utility = itemCatalog.UtilityFlaskOf(amethyst)!;
        flask.UtilityFlaskEffects.Start(new UtilityFlasks.Active(1, 0, amethyst.Name, utility, 1, null,
            [new UtilityFlasks.Source("buff", "immune_to_poison", 1)], now, now + 10));
        Game.CharacterPlayerStats(flask);
        Assert.Equal(0, flask.IncomingAilments.PoisonStacks(now));
        flask.UtilityFlaskEffects.End(now, all: true, out _);
        Game.CharacterPlayerStats(flask);
        Assert.True(flask.IncomingAilments.ApplyPoison(now, 1, 5));
        Assert.Equal(1, flask.IncomingAilments.PoisonStacks(now));

        // The real Golden Rule coefficient and the two other self-poison coefficients reach the final pools.
        // Only setup calls a pool builder; every state transition below goes through admission and the ordinary
        // character-stat snapshot.
        var golden = Fresh();
        var inventory = (PlayerInventories)Game.ItemsOf(golden)!;
        var goldenItem = new UniqueCatalog(itemCatalog).Create("The Golden Rule", new Random(1))!;
        Assert.Contains(goldenItem.Mods, m => itemCatalog.Mods.ByHash(m.Hash)?.Id == "ChaosResistancePerPoisonOnSelfUnique__1");
        Assert.Equal(1, ItemModStats.Sum([goldenItem], itemCatalog.Mods)
            .GetValueOrDefault("chaos_damage_resistance_%_per_poison_stack"));
        var goldenPlacement = inventory.Add(6, 0, 0, goldenItem);
        var otherCoefficients = ItemCatalog.Create(itemCatalog.Find("Paua Ring")!, random: new Random(1)) with
        {
            Rarity = ItemRarity.Unique,
            Mods = [
                Mod("MovementSpeedPerPoisonOnSelfUnique__1_").Roll(ModSection.Explicit, new Random(1)),
                Mod("EnergyShieldRegenPerPoisonUnique__1").Roll(ModSection.Explicit, new Random(1)),
            ],
        };
        inventory.Add(7, 0, 0, otherCoefficients);
        golden.Pools = golden.Pools with { Level = 20 };
        var baselineStats = Game.CharacterPlayerStats(golden);
        Assert.Equal(1, baselineStats.GetValueOrDefault(
            Program.Stats.Key("chaos_damage_resistance_%_per_poison_stack")!.Value));
        int BaseChaosRaw() => baselineStats[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value];
        int BaseChaosPool = golden.Pools.ChaosResistance;
        int BaseMovement = golden.Pools.MovementVelocityPct;
        int BaseEsRegen = baselineStats.GetValueOrDefault(
            Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value);
        int ChaosCoefficient = (int)goldenItem.Mods
            .Where(m => itemCatalog.Mods.ByHash(m.Hash)?.Id == "ChaosResistancePerPoisonOnSelfUnique__1")
            .SelectMany(m => m.Values).Sum();
        int MoveCoefficient = otherCoefficients.Mods
            .Where(m => itemCatalog.Mods.ByHash(m.Hash)?.Id == "MovementSpeedPerPoisonOnSelfUnique__1_")
            .SelectMany(m => m.Values).Sum();
        int RegenCoefficient = otherCoefficients.Mods
            .Where(m => itemCatalog.Mods.ByHash(m.Hash)?.Id == "EnergyShieldRegenPerPoisonUnique__1")
            .SelectMany(m => m.Values).Sum();

        var poisonSource = new SpawnedMonster("golden-source", new PlayerWalk(10, 10, 0), 0, 0, 100)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
            {
                ["base_chance_to_poison_on_hit_%"] = 100,
            })),
        };
        now = golden.Clock.Elapsed.TotalSeconds;
        void AdmitPoison()
        {
            lock (golden.Monsters)
                Game.ApplyMonsterDamagingAilmentsLocked(golden, 88, poisonSource,
                    DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 1, 1), false, now);
        }
        AdmitPoison();
        var one = Game.CharacterPlayerStats(golden);
        Assert.Equal(BaseChaosRaw() + ChaosCoefficient, one[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value]);
        Assert.Equal(BaseChaosPool + ChaosCoefficient, golden.Pools.ChaosResistance);
        Assert.True(DamageModel.Mitigate(1000, golden.Pools.ChaosResistance, golden.Pools.ChaosMaxResistance)
            < DamageModel.Mitigate(1000, BaseChaosPool, golden.Pools.ChaosMaxResistance));
        Assert.Equal(BaseMovement + MoveCoefficient, golden.Pools.MovementVelocityPct);
        Assert.Equal(BaseEsRegen + RegenCoefficient, one.GetValueOrDefault(
            Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value));

        AdmitPoison();
        var two = Game.CharacterPlayerStats(golden);
        Assert.Equal(BaseChaosRaw() + 2 * ChaosCoefficient, two[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value]);
        Assert.Equal(BaseChaosPool + 2 * ChaosCoefficient, golden.Pools.ChaosResistance);
        Assert.Equal(BaseMovement + 2 * MoveCoefficient, golden.Pools.MovementVelocityPct);
        Assert.Equal(BaseEsRegen + 2 * RegenCoefficient, two.GetValueOrDefault(
            Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value));

        Advance(golden, now, now + 3);
        var expired = Game.CharacterPlayerStats(golden);
        Assert.Equal(BaseChaosRaw(), expired[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value]);
        Assert.Equal(BaseChaosPool, golden.Pools.ChaosResistance);
        Assert.Equal(BaseMovement, golden.Pools.MovementVelocityPct);
        Assert.Equal(BaseEsRegen, expired.GetValueOrDefault(
            Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value));

        // A real outgoing accepted hit reflects through the production consumer. An actual supported immunity suffix
        // then removes it, and this first ordinary snapshot must already return baseline pools and raw stats.
        var outgoingTarget = new SpawnedMonster("golden-outgoing", new PlayerWalk(10, 10, 0), 0, 0, 100);
        var goldenPlayer = Game.CharacterPlayerStats(golden);
        goldenPlayer[Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100;
        lock (golden.Monsters)
            Game.AilmentsOnHitLocked(golden, 0x504001u, outgoingTarget, DamagePacket.Of(DamageType.Physical, 10, 10),
                false, false, 100, goldenPlayer, null, now + 3, new Random(1),
                poison: new MonsterPoisons.Settings(100, 1, 1, 1, 1, 5));
        Assert.Equal(1, golden.IncomingAilments.PoisonStacks(now + 3));
        var realImmunityMod = Mod("FlaskPoisonImmunityDuringEffect").Roll(ModSection.Explicit, new Random(1));
        var immunityFlask = ItemCatalog.Create(amethyst) with
        {
            Rarity = ItemRarity.Magic,
            Mods = [realImmunityMod],
        };
        var immunityLocal = ItemModStats.Local(immunityFlask, itemCatalog.Mods);
        var immunitySources = UtilityFlasks.For(utility, immunityFlask, immunityLocal,
            name => baselineStats.GetValueOrDefault(Program.Stats.Key(name) ?? 0), golden.Pools.Level).Stats;
        Assert.Contains(immunitySources, s => s.Stat == "immune_to_poison" && s.Value != 0);
        golden.UtilityFlaskEffects.Start(new UtilityFlasks.Active(99, 0, amethyst.Name, utility, 1, null,
            immunitySources, now + 3, now + 8));
        var afterImmunity = Game.CharacterPlayerStats(golden);
        Assert.Equal(0, golden.IncomingAilments.PoisonStacks(now + 3));
        Assert.Equal(BaseChaosRaw(), afterImmunity[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value]);
        Assert.Equal(BaseChaosPool, golden.Pools.ChaosResistance);
        Assert.Equal(BaseMovement, golden.Pools.MovementVelocityPct);
        Assert.Equal(BaseEsRegen, afterImmunity.GetValueOrDefault(
            Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value));

        // Equipping Golden Rule is not ownership of the accepted DoT. Removing it stops future reflection, while
        // already admitted player bleeding and poison continue through their original deadlines.
        golden.UtilityFlaskEffects.End(now + 3, all: true, out _);
        Game.CharacterPlayerStats(golden);
        var reflectedAilments = golden;
        var reflectedTarget = new SpawnedMonster("golden-reflection", new PlayerWalk(10, 10, 0), 0, 0, 100);
        var reflectionStart = reflectedAilments.Clock.Elapsed.TotalSeconds;
        var reflectingPlayer = Game.CharacterPlayerStats(reflectedAilments);
        Assert.Equal(1, reflectingPlayer.GetValueOrDefault(Program.Stats.Key("poison_reflected_to_self")!.Value));
        reflectingPlayer[Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100;
        for (int i = 0; i < 99; i++)
            Assert.True(reflectedAilments.IncomingAilments.ApplyPoison(reflectionStart, 1, 5, reflected: true));
        lock (reflectedAilments.Monsters)
        {
            Game.AilmentsOnHitLocked(reflectedAilments, 0x504002u, reflectedTarget,
                DamagePacket.Of(DamageType.Physical, 10, 10), false, false, 100,
                reflectingPlayer, null, reflectionStart, new Random(1),
                poison: new MonsterPoisons.Settings(100, 1, 1, 1, 1, 5));
            Game.BleedOnHitLocked(reflectedAilments, 701, reflectedTarget,
                new MonsterIgnites.Settings(100, 1, 1, 1, 1, 5), false, reflectionStart,
                Game.BleedReflected(reflectingPlayer));
        }
        Assert.Equal(100, reflectedAilments.IncomingAilments.PoisonStacks(reflectionStart));
        Assert.True(reflectedAilments.IncomingAilments.Bleeding(reflectionStart));
        lock (reflectedAilments.Monsters)
            Game.AilmentsOnHitLocked(reflectedAilments, 0x504002u, reflectedTarget,
                DamagePacket.Of(DamageType.Physical, 10, 10), false, false, 100,
                reflectingPlayer, null, reflectionStart + .01, new Random(3),
                poison: new MonsterPoisons.Settings(100, 1, 1, 1, 1, 5));
        Assert.Equal(100, reflectedAilments.IncomingAilments.PoisonStacks(reflectionStart + .01));
        double reflectedPoisonUntil = reflectedAilments.IncomingAilments.PoisonUntil(reflectionStart);
        double reflectedBleedUntil = reflectedAilments.IncomingAilments.BleedingUntil(reflectionStart);

        inventory.Remove(6, goldenPlacement.Id);
        var noReflection = Game.CharacterPlayerStats(reflectedAilments);
        Assert.Equal(0, noReflection.GetValueOrDefault(
            Program.Stats.Key("poison_reflected_to_self")!.Value));
        Assert.Equal(0, noReflection.GetValueOrDefault(
            Program.Stats.Key("bleeding_reflected_to_self")!.Value));
        Assert.Equal(100, reflectedAilments.IncomingAilments.PoisonStacks(reflectionStart));
        Assert.True(reflectedAilments.IncomingAilments.Bleeding(reflectionStart));
        Assert.Equal(reflectedPoisonUntil, reflectedAilments.IncomingAilments.PoisonUntil(reflectionStart));
        Assert.Equal(reflectedBleedUntil, reflectedAilments.IncomingAilments.BleedingUntil(reflectionStart));

        var laterPlayer = noReflection;
        laterPlayer[Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100;
        lock (reflectedAilments.Monsters)
        {
            Game.AilmentsOnHitLocked(reflectedAilments, 0x504002u, reflectedTarget,
                DamagePacket.Of(DamageType.Physical, 10, 10), false, false, 100,
                laterPlayer, null, reflectionStart + .1, new Random(2),
                poison: new MonsterPoisons.Settings(100, 1, 1, 1, 1, 5));
            Game.BleedOnHitLocked(reflectedAilments, 702, reflectedTarget,
                new MonsterIgnites.Settings(100, 1, 1, 1, 1, 5), false, reflectionStart + .1,
                Game.BleedReflected(laterPlayer));
            Assert.Equal(100, reflectedAilments.IncomingAilments.PoisonStacks(reflectionStart + .1));
            Assert.Equal(reflectedPoisonUntil, reflectedAilments.IncomingAilments.PoisonUntil(reflectionStart + .1));
            Assert.Equal(reflectedBleedUntil, reflectedAilments.IncomingAilments.BleedingUntil(reflectionStart + .1));
        }
        Advance(reflectedAilments, reflectionStart, reflectedBleedUntil + .01);
        Assert.Equal(0, reflectedAilments.IncomingAilments.PoisonStacks(reflectedBleedUntil + .01));
        Assert.False(reflectedAilments.IncomingAilments.Bleeding(reflectedBleedUntil + .01));
        }
        finally
        {
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    void Conditional_flask_uses_remove_and_protect_from_real_rows()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-flask-protection-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), 1, 1)
            {
                Pools = new(100, 1000, 100, 0, 0),
                PlayerLife = 500,
                PlayerSent = true,
                Instance = new(),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                    new HashSet<string>(["Game"])),
            };
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            conn.PlayerLife = conn.Pools.Life / 2;
            conn.Clock.Stop();
            var catalog = (ItemCatalog)Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)Game.ItemsOf(conn)!;
            var life = catalog.All.First(t => t.Class == "LifeFlask" && t.Supported && catalog.FlaskOf(t) is not null);
            var poisonMod = Mod("FlaskPoisonImmunity1__");
            var bleedMod = Mod("FlaskBleedCorruptingBloodImmunity1");
            Assert.Contains(poisonMod.Stats, s => s.Id == "local_flask_poison_immunity_if_poisoned_s");
            Assert.Contains(bleedMod.Stats, s =>
                s.Id == "local_flask_bleeding_immunity_if_bleeding_and_remove_corrupted_blood_s");
            Assert.True(poisonMod.WeightFor(life.Tags) > 0);
            Assert.True(bleedMod.WeightFor(life.Tags) > 0);
            int poisonSeconds = poisonMod.Roll(ModSection.Explicit, new Random(1)).Values.Sum();
            int bleedSeconds = bleedMod.Roll(ModSection.Explicit, new Random(1)).Values.Sum();
            Assert.InRange(poisonSeconds, 6, 17);
            Assert.InRange(bleedSeconds, 6, 17);
            foreach (byte slot in new byte[] { 0, 1, 2, 3 })
                if (inventory.Items(PlayerInventories.Flask).FirstOrDefault(i => i.X == slot) is { } starter)
                    inventory.Remove(PlayerInventories.Flask, starter.Id);
            var poisonFlaskPlacement = inventory.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(life) with
            {
                Rarity = ItemRarity.Magic,
                Charges = 100,
                Mods = [poisonMod.Roll(ModSection.Explicit, new Random(1))],
            });
            var bleedFlaskPlacement = inventory.Add(PlayerInventories.Flask, 1, 0, ItemCatalog.Create(life) with
            {
                Rarity = ItemRarity.Magic,
                Charges = 100,
                Mods = [bleedMod.Roll(ModSection.Explicit, new Random(1))],
            });
            inventory.Add(PlayerInventories.Flask, 2, 0, ItemCatalog.Create(life) with
            {
                Rarity = ItemRarity.Magic,
                Charges = 0,
                Mods = [poisonMod.Roll(ModSection.Explicit, new Random(1))],
            });
            var plainFlask = inventory.Add(PlayerInventories.Flask, 3, 0, ItemCatalog.Create(life) with { Charges = 100 });
            Assert.Equal(poisonSeconds, ItemModStats.Local(poisonFlaskPlacement.Item, catalog.Mods)
                .GetValueOrDefault("local_flask_poison_immunity_if_poisoned_s"));
            Assert.Equal(bleedSeconds, ItemModStats.Local(bleedFlaskPlacement.Item, catalog.Mods)
                .GetValueOrDefault("local_flask_bleeding_immunity_if_bleeding_and_remove_corrupted_blood_s"));
            Assert.Equal(poisonSeconds, ItemModStats.Local(
                inventory.Items(PlayerInventories.Flask).Single(i => i.Id == poisonFlaskPlacement.Id).Item, catalog.Mods)
                .GetValueOrDefault("local_flask_poison_immunity_if_poisoned_s"));
            double now = conn.Clock.Elapsed.TotalSeconds;
            Assert.True(conn.IncomingAilments.ApplyPoison(now, 2, 5));
            using var wire = new Wire();

            // An empty or failed use never reaches the accepted-use callback, so it neither removes nor protects.
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 99).GetAwaiter().GetResult();
            Assert.Equal(1, conn.IncomingAilments.PoisonStacks(now));
            Assert.True(conn.IncomingAilments.ApplyPoison(now, 2, 5));
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 2).GetAwaiter().GetResult();
            Assert.Equal(2, conn.IncomingAilments.PoisonStacks(now));

            // Unaffected suppresses damage, not the poison state that the flask's real trigger reads.
            conn.PlayerStats[Program.Stats.Key("virtual_unaffected_by_poison")!.Value] = 1;
            var unaffected = new Dictionary<uint, int>
            {
                [Program.Stats.Key("virtual_unaffected_by_poison")!.Value] = 1,
            };
            uint lifeBefore = conn.PlayerLife;
            lock (conn.Monsters) Game.AdvancePlayerAilmentsLocked(conn, now, now + .5, unaffected);
            Assert.Equal(lifeBefore, conn.PlayerLife);
            Assert.Equal(2, conn.IncomingAilments.PoisonStacks(now + .5));
            conn.PlayerStats.Remove(Program.Stats.Key("virtual_unaffected_by_poison")!.Value);

            double poisonUseAt = conn.Clock.Elapsed.TotalSeconds;
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 0).GetAwaiter().GetResult();
            Assert.Equal(0, conn.IncomingAilments.PoisonStacks(now + .5));
            double poisonExpiry = poisonUseAt + poisonSeconds;
            Assert.False(conn.IncomingAilments.ApplyPoison(poisonExpiry - .001, 1, 5));
            Assert.True(conn.IncomingAilments.ApplyPoison(poisonExpiry, 1, 5));

            // Clear is the same lifecycle/disconnect teardown used by the incoming owner and must reset protection.
            conn.IncomingAilments.Clear();
            Assert.True(conn.IncomingAilments.ApplyPoison(poisonExpiry, 1, 5));
            conn.IncomingAilments.Clear();
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 0).GetAwaiter().GetResult();
            Assert.True(conn.IncomingAilments.ApplyPoison(poisonExpiry + .001, 1, 5));
            conn.IncomingAilments.Clear();

            double bleedUseAt = conn.Clock.Elapsed.TotalSeconds;
            Assert.True(conn.IncomingAilments.ApplyBleed(bleedUseAt, 2, 5));
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 1).GetAwaiter().GetResult();
            Assert.False(conn.IncomingAilments.Bleeding(bleedUseAt));
            double bleedExpiry = bleedUseAt + bleedSeconds;
            Assert.False(conn.IncomingAilments.ApplyBleed(bleedExpiry - .001, 1, 5));
            Assert.True(conn.IncomingAilments.ApplyBleed(bleedExpiry, 1, 5));

            // A triggered curative suffix is usable at full pools; a plain recovery flask keeps the ordinary refusal.
            conn.IncomingAilments.Clear();
            conn.PlayerLife = conn.PlayerMaxLife;
            double fullAt = conn.Clock.Elapsed.TotalSeconds;
            Assert.True(conn.IncomingAilments.ApplyPoison(fullAt, 1, 5));
            int plainCharges = inventory.Items(PlayerInventories.Flask).Single(i => i.Id == plainFlask.Id).Item.Charges;
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 3).GetAwaiter().GetResult();
            Assert.Equal(plainCharges,
                inventory.Items(PlayerInventories.Flask).Single(i => i.Id == plainFlask.Id).Item.Charges);
            Assert.Equal(1, conn.IncomingAilments.PoisonStacks(fullAt));
            inventory.Change(PlayerInventories.Flask, poisonFlaskPlacement.Id, item => item with { Charges = 100 });
            int poisonCharges = inventory.Items(PlayerInventories.Flask).Single(i => i.Id == poisonFlaskPlacement.Id).Item.Charges;
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 0).GetAwaiter().GetResult();
            Assert.True(inventory.Items(PlayerInventories.Flask).Single(i => i.Id == poisonFlaskPlacement.Id).Item.Charges
                < poisonCharges);
            Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife);
            Assert.Equal(0, conn.IncomingAilments.PoisonStacks(fullAt));

            // Each named utility base records its own recent only inside SpendFlask's accepted payment callback.
            conn.Clock.Start();
            bool UseNamedFlask(string baseId, string eventKey, byte slot)
            {
                if (inventory.Items(PlayerInventories.Flask).FirstOrDefault(i => i.X == slot) is { } previous)
                    inventory.Remove(PlayerInventories.Flask, previous.Id);
                var type = catalog.All.Single(t => t.Id == baseId);
                var placed = inventory.Add(PlayerInventories.Flask, slot, 0,
                    ItemCatalog.Create(type) with { Charges = 100 });
                int charges = inventory.Items(PlayerInventories.Flask).Single(i => i.Id == placed.Id).Item.Charges;
                conn.RecentEvents.Clear();
                Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, slot).GetAwaiter().GetResult();
                Assert.True(inventory.Items(PlayerInventories.Flask).Single(i => i.Id == placed.Id).Item.Charges < charges);
                Assert.True(conn.RecentEvents.Happened(eventKey, conn.Clock.Elapsed.TotalSeconds));
                return true;
            }
            UseNamedFlask("Metadata/Items/Flasks/FlaskUtility2", StatConditions.UsedRubyFlask, 0);
            UseNamedFlask("Metadata/Items/Flasks/FlaskUtility3", StatConditions.UsedSapphireFlask, 1);
            UseNamedFlask("Metadata/Items/Flasks/FlaskUtility4", StatConditions.UsedTopazFlask, 2);
            UseNamedFlask("Metadata/Items/Flasks/FlaskUtility7", StatConditions.UsedAmethystFlask, 3);

            // A charge-refused use never reaches the callback or activates the recency.
            if (inventory.Items(PlayerInventories.Flask).FirstOrDefault(i => i.X == 3) is { } amethyst)
                inventory.Change(PlayerInventories.Flask, amethyst.Id, item => item with { Charges = 0 });
            conn.RecentEvents.Clear();
            Game.UseFlask("test", wire.Stream, wire.Ciphers, conn, 3).GetAwaiter().GetResult();
            Assert.False(conn.RecentEvents.Happened(StatConditions.UsedAmethystFlask, conn.Clock.Elapsed.TotalSeconds));

            // Accepted Dash and Fireball casts use the production actor admission. Dash's ordinary executor resolves a
            // real movement landing; refused casts leave both recents clear.
            if (inventory.Items(PlayerInventories.Helmet).FirstOrDefault() is { } previousHelmet)
                inventory.Remove(PlayerInventories.Helmet, previousHelmet.Id);
            var dashGem = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemDash")!);
            var fireballGem = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var purityFireGem = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemFireResistAura")!);
            var frostboltGem = ItemCatalog.Create(catalog.FindGem("Frostbolt")!.Value.Type);
            inventory.Add(PlayerInventories.Helmet, 0, 0,
                ItemCatalog.Create(catalog.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
                {
                    Sockets = [new ItemSocket(SocketColour.White, dashGem),
                        new ItemSocket(SocketColour.White, fireballGem),
                        new ItemSocket(SocketColour.White, purityFireGem),
                        new ItemSocket(SocketColour.White, frostboltGem)],
                    Links = [1, 1, 1, 1],
                });
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            conn.PlayerMana = 1000;
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            var visible = Game.GemSkillEffects(conn).ToArray();
            var dash = visible.Single(g => Game.SkillsFor(conn).EffectId(g.Effect) is "Dash" or "DashHardMode");
            var fireball = visible.Single(g => Game.SkillsFor(conn).EffectId(g.Effect) is "Fireball" or "FireballHardMode");
            Assert.True(Game.LandsAtAim(conn, dash.Effect));
            var dashStart = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            var dashLandingAim = default((int X, int Y));
            for (int distance = 1; distance <= 33 && dashLandingAim == default; distance++)
                foreach (var candidate in new[]
                         {
                             (X: dashStart.X + distance, Y: dashStart.Y),
                             (X: dashStart.X - distance, Y: dashStart.Y),
                             (X: dashStart.X, Y: dashStart.Y + distance),
                             (X: dashStart.X, Y: dashStart.Y - distance),
                         })
                    if (candidate.X >= 0 && candidate.Y >= 0 && candidate.X < conn.Layout.GridW
                        && candidate.Y < conn.Layout.GridH && conn.Layout.Walkable!(candidate.X, candidate.Y)
                        && !conn.Barrier.Contains(candidate)
                        && Game.MovementLanding(conn, dash.Effect, dashStart, candidate) != dashStart)
                    {
                        dashLandingAim = candidate;
                        break;
                    }
            Assert.NotEqual(default, dashLandingAim);
            Assert.NotEqual(dashStart, Game.MovementLanding(conn, dash.Effect, dashStart, dashLandingAim));
            foreach (var invalidAim in new[]
                     {
                         (-1, dashStart.Y), (conn.Layout.GridW, dashStart.Y),
                         (dashStart.X, -1), (dashStart.X, conn.Layout.GridH),
                         (int.MinValue, dashStart.Y), (dashStart.X, int.MaxValue),
                     })
                lock (conn.Monsters)
                    Assert.False(Game.TryDashLanding(conn, dash.Effect, invalidAim, out _, out _),
                        $"raw aim {invalidAim} must be refused before Dash coordinate arithmetic");
            bool Cast(string effectName)
            {
                var current = Game.GemSkillEffects(conn).Single(g =>
                    Game.SkillsFor(conn).EffectId(g.Effect) is string id
                    && (id == effectName || id == effectName + "HardMode"));
                var from = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                var aim = (X: 20, Y: 0);
                if (effectName == "Dash")
                {
                    aim = default;
                    for (int distance = 1; distance <= 33 && aim == default; distance++)
                        foreach (var candidate in new[]
                                 {
                                     (X: from.X + distance, Y: from.Y), (X: from.X - distance, Y: from.Y),
                                     (X: from.X, Y: from.Y + distance), (X: from.X, Y: from.Y - distance),
                                 })
                            if (candidate.X >= 0 && candidate.Y >= 0 && candidate.X < conn.Layout.GridW
                                && candidate.Y < conn.Layout.GridH && conn.Layout.Walkable!(candidate.X, candidate.Y)
                                && !conn.Barrier.Contains(candidate)
                                && Game.MovementLanding(conn, current.Effect, from, candidate) != from)
                            {
                                aim = candidate;
                                break;
                            }
                    Assert.NotEqual(default, aim);
                }
                CooldownUseTicket? ticket = null;
                if (!global::Game.IsDashEffect(Game.SkillsFor(conn).EffectId(current.Effect)))
                {
                    var plan = Game.CooldownFor(conn, current.Effect, current.Level, current.Quality);
                    if (plan is not null && !Game.TryBeginSkillCooldownUse(conn, current.Id, current.Effect, plan,
                            instant: false, () => { }, out ticket)) return false;
                }
                return Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, current.Id,
                    from, aim, cooldownUse: ticket).GetAwaiter().GetResult();
            }
            conn.RecentEvents.Clear();
            bool movementRecentAtCast = false, dashRecentAtCast = false, fireRecentAtCast = false, coldRecentAtCast = false;
            double coldObservedAt = 0;
            string recentCapture = "handler did not run";
            using var captureRecent = GameEvents.SkillCastAt.Add("Game", (target, effectId, _, _, _) =>
            {
                recentCapture = $"{effectId}: {string.Join(",", target.RecentEvents.Snapshot(target.Clock.Elapsed.TotalSeconds))}";
                if (global::Game.IsDashEffect(effectId))
                {
                    movementRecentAtCast = target.RecentEvents.Happened(StatConditions.UsedMovementSkill,
                        target.Clock.Elapsed.TotalSeconds);
                    dashRecentAtCast = target.RecentEvents.Happened(StatConditions.UsedDash,
                        target.Clock.Elapsed.TotalSeconds);
                }
                if (effectId is "Fireball" or "FireballHardMode")
                    fireRecentAtCast = target.RecentEvents.Happened(StatConditions.UsedFireSkill,
                        target.Clock.Elapsed.TotalSeconds);
                if (effectId is "FrostBolt" or "FrostBoltHardMode")
                {
                    coldObservedAt = target.Clock.Elapsed.TotalSeconds;
                    coldRecentAtCast = target.RecentEvents.Happened(StatConditions.UsedColdSkill, coldObservedAt);
                }
                return null;
            });
            Assert.Single(GameEvents.SkillCastAt.For(conn));
            Assert.True(Cast("Dash"),
                $"life={conn.PlayerLife} town={Game.InTown(conn)} paused={conn.Paused} " +
                $"skills={string.Join(",", Game.GemSkillEffects(conn).Select(g => $"{g.Id}:{g.Name}"))}");
            Assert.True(movementRecentAtCast, recentCapture);
            Assert.True(dashRecentAtCast, recentCapture);
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedSkill, conn.Clock.Elapsed.TotalSeconds));
            Assert.True(Cast("Fireball"));
            Assert.True(fireRecentAtCast);
            conn.RecentEvents.Clear();
            Assert.True(Cast("FrostBolt"));
            Assert.True(coldRecentAtCast);
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedSkill, coldObservedAt));
            Assert.False(conn.RecentEvents.Happened(StatConditions.UsedFireSkill, coldObservedAt));
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedColdSkill, coldObservedAt + 3.9));
            Assert.False(conn.RecentEvents.Happened(StatConditions.UsedColdSkill, coldObservedAt + 4));
            const string coldFire = "fire_damage_+%_if_you_have_used_a_cold_skill_recently";
            var coldFireMod = catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == coldFire));
            var coldFireItem = WithMod(coldFireMod.Id, 17);
            foreach (var (at, expected) in new[] { (coldObservedAt, 17), (coldObservedAt + 4, 0) })
            {
                var folded = ItemModStats.Sum([coldFireItem], catalog.Mods,
                    subject: new(Recent: conn.RecentEvents.Snapshot(at), KnownRecent: Game.TrackedItemRecentEvents), statExists: Program.Stats.Has);
                Assert.Equal(expected, folded.GetValueOrDefault("fire_damage_+%"));
            }
            conn.RecentEvents.Clear();
            var purity = visible.Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "PurityOfFire");
            Assert.True(Cast("PurityOfFire"),
                $"mana={conn.PlayerMana}/{conn.Pools.Mana}, reserved={Game.ReservedMana(conn)}, life={conn.PlayerLife}/{conn.Pools.Life}, " +
                $"pause={conn.Paused}, closed={conn.Closed}, town={Game.InTown(conn)}, auras={conn.Auras.Count}, " +
                $"reservation={Game.SupportedReservationCost(conn, purity.Id)}, " +
                $"skills={string.Join(",", Game.GemSkillEffects(conn).Select(g => $"{g.Id}:{g.Name}"))}, " +
                $"disabled={Game.GemSkillInstanceDisabled(conn, purity.Effect, purity.Id)}");
            Assert.True(conn.RecentEvents.Happened(StatConditions.UsedFireSkill, conn.Clock.Elapsed.TotalSeconds));
            conn.RecentEvents.Clear();
            conn.PlayerMana = 0;
            fireRecentAtCast = false;
            Assert.False(Cast("Fireball"));
            Assert.False(fireRecentAtCast);
            Assert.False(conn.RecentEvents.Happened(StatConditions.UsedSkill, conn.Clock.Elapsed.TotalSeconds));
            Assert.False(Cast("FrostBolt"));
            Assert.False(conn.RecentEvents.Happened(StatConditions.UsedColdSkill, conn.Clock.Elapsed.TotalSeconds));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    void Equipment_counts_and_composed_conditions_use_the_same_subject()
    {
        var exists = (string name) => Program.Stats.Has(name);
        double now = 0;
        var conn = new ClientState(new(1, "conditional-state", 300, 300, 100, 100, null, []), 1, 1)
        {
            Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900, PlayerMana = 70,
            ChannelSkill = 7,
        };
        now = conn.Clock.Elapsed.TotalSeconds;
        Assert.True(conn.IncomingAilments.ApplyBleed(now, 10, 1));
        Assert.True(conn.IncomingAilments.ApplyPoison(now, 5, 2));
        conn.IncomingAilments.Clear();
        var bleedingMonster = new SpawnedMonster("bleeding contact", new(100, 100, 0), 0, 0, 100)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
            {
                ["global_bleed_on_hit"] = 1,
                ["global_poison_on_hit"] = 1,
            })),
        };
        lock (conn.Monsters)
            Game.ApplyMonsterDamagingAilmentsLocked(conn, 77, bleedingMonster,
                GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100), false, now);
        // Applying a real source reads the victim's duration sources and settles the connection's real pools.
        conn.PlayerLife = 900;
        now = conn.Clock.Elapsed.TotalSeconds;
        Assert.True(conn.IncomingAilments.Bleeding(now));
        Assert.Equal(1, conn.IncomingAilments.PoisonStacks(now));
        conn.FlaskGrounds.SetConsecrated(true, () => 1);
        var flags = Game.ItemConditionFlags(conn, now);
        Assert.True(flags["_while_channelling"]);
        Assert.False(flags["_while_not_channelling"]);
        Assert.True(flags["_while_bleeding"]);
        Assert.True(flags["_while_poisoned"]);
        Assert.True(flags["_while_on_consecrated_ground"]);
        Assert.True(flags["_while_not_on_low_mana"]);
        Assert.True(flags["_while_not_moving"]);
        var victim = new Dictionary<uint, int>();
        uint life = conn.PlayerLife;
        lock (conn.Monsters) Game.AdvancePlayerAilmentsLocked(conn, now, now + 1, victim);
        // A 100 physical contact grants 10/s bleed and 30/s poison. The dedicated control above proves that real Walk
        // movement alone changes the same one-second standing case from 40 to 60.
        Assert.Equal(life - 40, conn.PlayerLife);
        lock (conn.Monsters) Game.AdvancePlayerAilmentsLocked(conn, now + 1, now + 6, victim);
        Assert.False(conn.IncomingAilments.Bleeding(now + 6));
        Assert.Equal(0, conn.IncomingAilments.PoisonStacks(now + 6));
        Assert.False(Game.ItemConditionFlags(conn, now + 6)["_while_bleeding"]);
        Assert.False(Game.ItemConditionFlags(conn, now + 6)["_while_poisoned"]);

        lock (conn.Monsters)
        {
            Assert.True(conn.IncomingAilments.ApplyBleed(now + 6, 10, 5));
            Assert.True(conn.IncomingAilments.ApplyPoison(now + 6, 10, 5));
            Assert.True(conn.IncomingAilments.Bleeding(now + 6));
            var unaffected = new Dictionary<uint, int>
            {
                [Program.Stats.Key("virtual_unaffected_by_bleeding")!.Value] = 1,
                [Program.Stats.Key("virtual_unaffected_by_poison")!.Value] = 1,
            };
            uint before = conn.PlayerLife;
            Assert.True(before > 0);
            Game.AdvancePlayerAilmentsLocked(conn, now + 6, now + 7, unaffected);
            Assert.Equal(before, conn.PlayerLife);
            Assert.True(conn.IncomingAilments.Bleeding(now + 7));
            Assert.Equal(1, conn.IncomingAilments.PoisonStacks(now + 7));
        }
        conn.InvalidateIncomingHits();
        Assert.False(Game.ItemConditionFlags(conn, now + 7)["_while_bleeding"]);
        Assert.False(Game.ItemConditionFlags(conn, now + 7)["_while_poisoned"]);
        conn.FlaskGrounds.SetConsecrated(false, () => 2);
        Assert.False(Game.ItemConditionFlags(conn, now + 7)["_while_on_consecrated_ground"]);

        double live = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            conn.IncomingAilments.Clear();
            Assert.True(conn.IncomingAilments.ApplyPoison(live, 1, 5));
            Assert.True(conn.IncomingAilments.ApplyPoison(live, 1, 5));
        }
        var poisonSources = new Dictionary<uint, int>
        {
            [Program.Stats.Key("damage_+%_per_poison_up_to_75%")!.Value] = 40,
            [Program.Stats.Key("movement_speed_+%_per_poison_up_to_50%")!.Value] = 30,
            [Program.Stats.Key("chaos_damage_resistance_%_per_poison_stack")!.Value] = 2,
        };
        var poisonStats = Game.WithCharges(conn, poisonSources);
        Assert.Equal(75, poisonStats[Program.Stats.Key("damage_+%")!.Value]);
        Assert.Equal(50, poisonStats[Program.Stats.Key("base_movement_velocity_+%")!.Value]);
        Assert.Equal(4, poisonStats[Program.Stats.Key("base_chaos_damage_resistance_%")!.Value]);
        var reused = Game.WithCharges(conn, poisonSources);
        Assert.Equal(poisonStats[Program.Stats.Key("damage_+%")!.Value],
            reused[Program.Stats.Key("damage_+%")!.Value]);

        var perUnique = WithMod("IncreasedIntelligencePerUniqueUniqueRing14", 2);
        Assert.Equal(6, ItemModStats.RawAttributes([perUnique], Program.Items.Mods,
            new(Value: name => name == "equipped_unique" ? 3 : null), exists)["intelligence_+%"]);
        var perElder = WithMod("IncreasedLifePerElderItemUnique__1", 2);
        Assert.Equal(6, ItemModStats.Sum([perElder], Program.Items.Mods,
            subject: new(Value: name => name == "equipped_elder" ? 3 : null), statExists: exists)
            .GetValueOrDefault("maximum_life"));
        var perSword = WithMod("MaxRagePerEquippedSwordUnique__1____", 2);
        Assert.Equal(4, ItemModStats.Sum([perSword], Program.Items.Mods,
            subject: new(Value: name => name == "equipped_one_handed_sword" ? 2 : null), statExists: exists)
            .GetValueOrDefault("maximum_rage"));
        var relative = WithMod("PercentDexterityIfStrengthHigherThanIntelligenceUnique__1", 10);
        foreach (int strength in new[] { 99, 100, 101 })
        {
            var subject = new StatConditions.Subject(Value: name => name == "strength" ? strength : name == "intelligence" ? 100 : null);
            Assert.Equal(strength > 100 ? 10 : 0, ItemModStats.Sum([relative], Program.Items.Mods,
                subject: subject, statExists: exists).GetValueOrDefault("dexterity_+%"));
        }
        const string compound = "damage_+%_if_have_been_hit_recently_if_4_warlord_items";
        Assert.Equal("damage_+%", StatConditions.PlainOf(compound));
        foreach (bool hit in new[] { false, true })
            foreach (int equipment in new[] { 3, 4 })
            {
                var subject = new StatConditions.Subject(Recent: hit ? new HashSet<string> { StatConditions.BeenHit } : new HashSet<string>(),
                    Value: name => name == "equipped_warlord" ? equipment : null);
                Assert.Equal(hit && equipment >= 4 ? StatConditions.State.Holds : StatConditions.State.Fails,
                    StatConditions.Of(compound, subject));
            }
    }

    void Recent_amounts_count_actual_occurrences_and_expire_individually()
    {
        var events = new RecentEvents();
        events.RecordAmount(StatConditions.KilledEnemy, 10);
        events.Record(StatConditions.KilledEnemy, 10); // another observer, not another kill
        events.RecordAmount(StatConditions.KilledEnemy, 11, 4);
        Assert.Equal(5, events.Amounts(13.999)[StatConditions.KilledEnemy]);
        Assert.Equal(14, events.NextExpiry(13));
        Assert.Equal(4, events.Amounts(14)[StatConditions.KilledEnemy]);
        Assert.Equal(0, events.Amounts(15)[StatConditions.KilledEnemy]);
        events.RecordAmount(StatConditions.SpentMana, 20, 199);
        var mod = WithMod("SpellDamagePer200ManaSpentRecentlyUnique__1__", 50);
        var exists = (string name) => Program.Stats.Has(name);
        int Damage(double at) => ItemModStats.Sum([mod], Program.Items.Mods,
            subject: new(Amounts: events.Amounts(at)), statExists: exists).GetValueOrDefault("spell_damage_+%");
        Assert.Equal(0, Damage(20));
        events.RecordAmount(StatConditions.SpentMana, 21, 1);
        Assert.Equal(50, Damage(21));
        Assert.Equal(0, Damage(24)); // only the later 1 mana remains in the window
        events.RecordAmount(StatConditions.SpentMana, 24, long.MaxValue);
        Assert.Equal(int.MaxValue, Damage(24));
        events.Clear();
        Assert.Empty(events.Amounts(24));

        const double fractionalAt = .1;
        double fractionalExpiry = fractionalAt + RecentEvents.WindowSeconds;
        events.Record("four-second-boundary", fractionalAt);
        Assert.True(events.Happened("four-second-boundary", fractionalExpiry - .001));
        Assert.False(events.Happened("four-second-boundary", fractionalExpiry));
        Assert.Contains("four-second-boundary", events.Snapshot(fractionalExpiry - .001));
        Assert.DoesNotContain("four-second-boundary", events.Snapshot(fractionalExpiry));
        Assert.Equal(fractionalExpiry, events.NextExpiry(fractionalAt));
        Assert.Null(events.NextExpiry(fractionalExpiry));

        events.Clear();
        events.RecordAmount("counted-boundary", fractionalAt, 3);
        Assert.Equal(3, events.Amounts(fractionalExpiry - .001)["counted-boundary"]);
        Assert.Equal(fractionalExpiry, events.NextExpiry(fractionalAt));
        Assert.Equal(0, events.Amounts(fractionalExpiry)["counted-boundary"]);
        Assert.Null(events.NextExpiry(fractionalExpiry));
        events.Clear();

        var counted = WithMod("WeaponTreeAreaOfEffectIfKilledRecentlyReducedAreaOfEffect1", 25);
        counted = counted with { Mods = [counted.Mods.Single() with { Values = [25, -10] }] };
        var scaled = WithMod("DivergentAreaOfEffectPerEnemyKilledRecentlyCapped25Unique__1", 5);
        foreach (int kills in new[] { 0, 4, 5, 20 })
        {
            var subject = new StatConditions.Subject(Amounts: new Dictionary<string, long> { [StatConditions.KilledEnemy] = kills });
            var gated = ItemModStats.Sum([counted], Program.Items.Mods, subject: subject, statExists: exists);
            Assert.Equal(kills >= 5 ? 15 : -10, gated.GetValueOrDefault("base_skill_area_of_effect_+%"));
            Assert.Equal(Math.Min(kills * 5, 25), ItemModStats.Sum([scaled], Program.Items.Mods,
                subject: subject, statExists: exists).GetValueOrDefault("base_skill_area_of_effect_+%"));
        }
    }

    void Real_aura_and_numeric_threshold_rolls_reach_existing_consumers()
    {
        var exists = (string name) => Program.Stats.Has(name);
        var wrath = WithMod("WrathIncreasedLightningDamage", 40);
        Assert.Equal(40, ItemModStats.Sum([wrath], Program.Items.Mods,
            subject: new(Auras: new HashSet<string> { "wrath" }), statExists: exists)["lightning_damage_+%"]);
        Assert.Empty(ItemModStats.Sum([wrath], Program.Items.Mods,
            subject: new(Auras: new HashSet<string>()), statExists: exists));
        Assert.Contains("a_herald", StatConditions.AuraSet(["HeraldOfAsh"]));
        Assert.Equal("spell_damage_+%", StatConditions.PlainOf("spell_damage_+%_while_wielding_two_handed_weapon"));

        var threshold = WithMod("DoubleDamageWith200StrengthUnique__1", 10);
        foreach (int strength in new[] { 199, 200, 201 })
        {
            var sum = ItemModStats.Sum([threshold], Program.Items.Mods,
                subject: new(Value: name => name == "strength" ? strength : null), statExists: exists);
            Assert.Equal(strength >= 200 ? 10 : 0, sum.GetValueOrDefault("chance_to_deal_double_damage_%"));
        }
        var es = WithMod("LifeRegenerationWith500EnergyShieldUnique__1", 60);
        foreach (int shield in new[] { 499, 500 })
        {
            var sum = ItemModStats.Sum([es], Program.Items.Mods,
                subject: new(Value: name => name == "energy_shield" ? shield : null), statExists: exists);
            Assert.Equal(shield >= 500 ? 60 : 0, sum.GetValueOrDefault("base_life_regeneration_rate_per_minute"));
        }
        foreach (var (conditional, attribute, target) in new[]
        {
            ("area_of_effect_+%_if_below_100_intelligence", "intelligence", "base_skill_area_of_effect_+%"),
            ("movement_speed_+%_if_below_100_dexterity", "dexterity", "base_movement_velocity_+%"),
            ("double_damage_chance_%_if_below_100_strength", "strength", "chance_to_deal_double_damage_%"),
        })
        {
            var rows = Program.Items.Mods.All.Where(m => m.Stats.Count == 1 && m.Stats[0].Id == conditional).ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                int amount = row.Stats[0].Min;
                var item = WithMod(row.Id, amount);
                foreach (int current in new[] { 99, 100, 101 })
                {
                    var subject = new StatConditions.Subject(Value: n => n == attribute ? current : null);
                    var sum = ItemModStats.Sum([item], Program.Items.Mods, subject: subject, statExists: exists);
                    int expected = current < 100 ? amount : 0;
                    Assert.Equal(expected, sum.GetValueOrDefault(target));
                    Assert.False(sum.ContainsKey(conditional));
                    if (attribute == "intelligence")
                        Assert.Equal(1 + expected / 100d, SkillArea.Modifier(n => sum.GetValueOrDefault(n), spell: true));
                    if (attribute == "strength")
                    {
                        var rolls = GamblerModifiers.From(n => sum.GetValueOrDefault(n));
                        var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
                        Assert.Equal(current < 100 ? 200 : 100, rolls.RollOutgoingHit(hit, new ThresholdRoll(0)).MinTotal);
                        Assert.Equal(100, rolls.RollOutgoingHit(hit, new ThresholdRoll(amount)).MinTotal);
                    }
                }
                Assert.Equal(amount, ItemModStats.Sum([item], Program.Items.Mods,
                    subject: new(Value: _ => null), statExists: exists).GetValueOrDefault(conditional));
            }
        }
        const string composedBelow = "damage_+%_while_wielding_shield_if_below_100_strength";
        Assert.Equal("damage_+%", StatConditions.PlainOf(composedBelow));
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of(composedBelow, new(Shield: false, Value: _ => 99)));
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of(composedBelow, new(Shield: true, Value: _ => 100)));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of(composedBelow, new(Shield: true, Value: _ => 99)));
        Assert.Equal("base_movement_velocity_+%", StatConditions.Fold("movement_speed_+%_if_enemy_killed_recently", exists));
        Assert.False(StatConditions.TryRecentEvent("damage_+%_per_enemy_killed_recently", out _, out _));
        Assert.False(StatConditions.TryRecentEvent("damage_+%_if_killed_at_least_5_enemies_recently", out _, out _));
        Assert.False(StatConditions.TryRecentEvent("x_if_used_a_skill_recently_if_4_warlord_items", out _, out _));
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of("x_if_you_have_used_a_fire_skill_recently",
            new(Recent: new HashSet<string>(), KnownRecent: new HashSet<string> { StatConditions.BeenHit })));
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of("x_if_blocked_a_spell_recently",
            new(Recent: new HashSet<string> { StatConditions.BlockedAttack })));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("x_if_blocked_a_spell_recently",
            new(Recent: new HashSet<string> { StatConditions.BlockedSpell })));

        const string coefficient = "base_attack_speed_+%_per_frenzy_charge";
        var chargeMod = Program.Items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == coefficient);
        var raw = ItemModStats.Sum([WithMod(chargeMod.Id, 7)], Program.Items.Mods,
            subject: new(Recent: new HashSet<string>()), statExists: exists);
        Assert.Equal(7, raw[coefficient]);
        Assert.Equal(0, raw.GetValueOrDefault("attack_speed_+%"));
        var expanded = ChargeEffects.Expand(name => raw.GetValueOrDefault(name),
            type => type == ChargeType.Frenzy ? 3 : 0);
        Assert.Equal(21, expanded["attack_speed_+%"]);
        var phased = WithMod("MovementSpeedWhilePhasedUnique__1", 20);
        var lowLife = WithMod("SpellBlockPercentageOnLowLifeUniqueShieldStrDex1_", 25);
        foreach (bool active in new[] { false, true })
        {
            var subject = new StatConditions.Subject(Flags: new Dictionary<string, bool>
            {
                ["_while_phasing"] = active, ["_while_on_low_life"] = active,
                ["_while_at_maximum_power_charges"] = active,
            });
            Assert.Equal(active ? 20 : 0, ItemModStats.Sum([phased], Program.Items.Mods,
                subject: subject, statExists: exists).GetValueOrDefault("base_movement_velocity_+%"));
            Assert.Equal(active ? 25 : 0, ItemModStats.Sum([lowLife], Program.Items.Mods,
                subject: subject, statExists: exists).GetValueOrDefault("base_spell_block_%"));
            var maximum = WithMod("PhysAddedAsChaosWithMaxPowerChargesUnique__1", 15);
            Assert.Equal(active ? 15 : 0, ItemModStats.Sum([maximum], Program.Items.Mods,
                subject: subject, statExists: exists).GetValueOrDefault("physical_damage_%_to_add_as_chaos"));
            var flask = WithMod("ChaosResistanceWhileUsingFlaskUniqueBootsStrDex3", 35);
            Assert.Equal(active ? 35 : 0, ItemModStats.Sum([flask], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_using_flask"] = active }), statExists: exists)
                .GetValueOrDefault("base_chaos_damage_resistance_%"));
            var whileBleeding = WithMod("IncreasedArmourWhileBleedingUnique__1", 40);
            Assert.Equal(active ? 40 : 0, ItemModStats.Sum([whileBleeding], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_bleeding"] = active }), statExists: exists)
                .GetValueOrDefault("physical_damage_reduction_rating_+%"));
            var whilePoisoned = WithMod("MaximumResistancesWhilePoisonedUnique__1", 2);
            Assert.Equal(active ? 2 : 0, ItemModStats.Sum([whilePoisoned], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_poisoned"] = active }), statExists: exists)
                .GetValueOrDefault("additional_maximum_all_resistances_%"));
            var channelling = WithMod("ChanceToSuppressSpellsWhileChannellingUnique__1____", 12);
            var channellingStats = ItemModStats.Sum([channelling], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_channelling"] = active }), statExists: exists);
            Assert.Equal(active ? 12 : 0, channellingStats.GetValueOrDefault("base_spell_suppression_chance_%"));
            Assert.False(channellingStats.ContainsKey("spell_suppression_chance_%"));
            var consecrated = WithMod("BlockChanceOnConsecratedGroundUnique__1", 6);
            Assert.Equal(active ? 6 : 0, ItemModStats.Sum([consecrated], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_on_consecrated_ground"] = active }), statExists: exists)
                .GetValueOrDefault("additional_block_%"));
            var onslaught = WithMod("IncreasedEvasionWithOnslaughtUnique_1", 20);
            Assert.Equal(active ? 20 : 0, ItemModStats.Sum([onslaught], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_while_onslaught_is_active"] = active }), statExists: exists)
                .GetValueOrDefault("evasion_rating_+%"));
        }
    }

    void Per_charge_leaves_reach_existing_final_consumers()
    {
        var exists = (string name) => Program.Stats.Has(name);
        var cases = new (string Source, string Mod, string[] Targets, ChargeType Type)[]
        {
            ("additional_critical_strike_chance_per_power_charge_permyriad",
                "AdditionalCriticalStrikeChancePerPowerChargeUnique__1",
                ["additional_base_critical_strike_chance"], ChargeType.Power),
            ("base_max_fortification_per_endurance_charge",
                "MaximumFortificationPerEnduranceChargeUnique__1",
                ["base_max_fortification"], ChargeType.Endurance),
            ("bleeding_dot_multiplier_per_frenzy_charge_+",
                "BleedDotMultiplierPerFrenzyChargeUnique__1_",
                ["bleeding_dot_multiplier_+"], ChargeType.Frenzy),
            ("faster_bleed_per_frenzy_charge_%",
                "FasterBleedPerFrenzyChargeUnique__1",
                ["faster_bleed_%"], ChargeType.Frenzy),
            ("chance_to_poison_on_hit_%_per_power_charge",
                "PoisonChancePerPowerChargeUnique__1",
                ["base_chance_to_poison_on_hit_%"], ChargeType.Power),
            ("poison_damage_+%_per_frenzy_charge",
                "PoisonDamagePerFrenzyChargeUnique__1",
                ["base_poison_damage_+%"], ChargeType.Frenzy),
            ("poison_damage_+%_per_power_charge",
                "PoisonDamagePerPowerChargeUnique__1",
                ["base_poison_damage_+%"], ChargeType.Power),
            ("poison_dot_multiplier_+_per_frenzy_charge",
                "PoisonDotMultiplierPerFrenzyChargeUniqueGlovesDexInt5",
                ["poison_dot_multiplier_+"], ChargeType.Frenzy),
            ("poison_duration_+%_per_power_charge",
                "PoisonDurationPerPowerChargeUnique__1",
                ["poison_duration_+%"], ChargeType.Power),
            ("life_leech_from_attack_damage_permyriad_per_frenzy_charge",
                "AttackDamageLeechPerFrenzyChargeUnique__1",
                ["base_life_leech_from_attack_damage_permyriad"], ChargeType.Frenzy),
            ("mana_leech_from_attack_damage_permyriad_per_power_charge",
                "ManaLeechPermyriadPerPowerChargeUniqueBelt5_",
                ["base_mana_leech_from_attack_damage_permyriad"], ChargeType.Power),
            ("life_and_energy_shield_recovery_rate_+%_per_power_charge",
                "LifeAndEnergyShieldRecoveryRatePerPowerChargeUnique__1",
                ["life_recovery_rate_+%", "energy_shield_recovery_rate_+%"], ChargeType.Power),
            ("damage_taken_goes_to_mana_%_per_power_charge",
                "DamageTakeFromManaBeforeLifePerPowerChargeUnique__1",
                ["base_damage_removed_from_mana_before_life_%"], ChargeType.Power),
        };

        foreach (var entry in cases)
        {
            Assert.True(exists(entry.Source), entry.Source);
            Assert.All(entry.Targets, target => Assert.True(exists(target), target));
            Assert.Contains(Mod(entry.Mod).Stats, stat => stat.Id == entry.Source);
            var item = WithMod(entry.Mod, 7);
            var raw = ItemModStats.Sum([item], Program.Items.Mods,
                subject: new StatConditions.Subject(Recent: new HashSet<string>()), statExists: exists);
            Assert.Equal(7, raw[entry.Source]);
            Assert.All(entry.Targets, target => Assert.False(raw.ContainsKey(target), target));
            Assert.Null(StatConditions.Fold(entry.Source, exists));
            Assert.True(ChargeEffects.Expands(entry.Source));

            foreach (int charges in new[] { 0, 1, 3, 0 })
            {
                var expanded = ChargeEffects.Expand(raw.GetValueOrDefault, type => type == entry.Type ? charges : 0);
                foreach (string target in entry.Targets)
                    Assert.Equal(charges == 0 ? 0 : charges * 7, expanded.GetValueOrDefault(target));
            }
        }

        var uniques = new UniqueCatalog(Program.Items);
        var representatives = new (string Name, string Source)[]
        {
            ("Shimmeron", "additional_critical_strike_chance_per_power_charge_permyriad"),
            ("Usurper's Penance", "bleeding_dot_multiplier_per_frenzy_charge_+"),
            ("Usurper's Penance", "faster_bleed_per_frenzy_charge_%"),
            ("Fenumus' Toxins", "chance_to_poison_on_hit_%_per_power_charge"),
            ("Fenumus' Toxins", "poison_damage_+%_per_power_charge"),
            ("Maligaro's Cruelty", "poison_damage_+%_per_frenzy_charge"),
            ("Maligaro's Cruelty", "poison_duration_+%_per_power_charge"),
            ("Snakebite", "poison_dot_multiplier_+_per_frenzy_charge"),
            ("Hopeshredder", "life_leech_from_attack_damage_permyriad_per_frenzy_charge"),
            ("Auxium", "mana_leech_from_attack_damage_permyriad_per_power_charge"),
            ("Machina Mitts", "life_and_energy_shield_recovery_rate_+%_per_power_charge"),
            ("The Aylardex", "damage_taken_goes_to_mana_%_per_power_charge"),
            ("Replica Badge of the Brotherhood", "base_max_fortification_per_endurance_charge"),
        };
        foreach (var (name, source) in representatives)
        {
            var item = uniques.Create(name, new Random(1));
            Assert.NotNull(item);
            var raw = ItemModStats.Sum([item], Program.Items.Mods,
                subject: new StatConditions.Subject(Recent: new HashSet<string>()), statExists: exists);
            Assert.NotEqual(0, raw.GetValueOrDefault(source));
        }

        Dictionary<string, int> Expanded(Item item, int endurance, int frenzy, int power)
        {
            var raw = ItemModStats.Sum([item], Program.Items.Mods,
                subject: new StatConditions.Subject(Recent: new HashSet<string>()), statExists: exists);
            var result = ChargeEffects.Expand(raw.GetValueOrDefault, type => type switch
            {
                ChargeType.Endurance => endurance,
                ChargeType.Frenzy => frenzy,
                _ => power,
            });
            result["current_endurance_charges"] = endurance;
            result["current_frenzy_charges"] = frenzy;
            result["current_power_charges"] = power;
            result["object_inherent_damage_+%_final_per_frenzy_charge"] = 4;
            return result;
        }

        var shimmeron = uniques.Create("Shimmeron", new Random(1))!;
        var shimmeronZero = Expanded(shimmeron, 0, 0, 0);
        var shimmeronThree = Expanded(shimmeron, 0, 0, 3);
        int addedCrit = ItemModStats.Sum([shimmeron], Program.Items.Mods)
            .GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad") * 3;
        int Crit(Dictionary<string, int> stats, bool spell) =>
            AddedBaseCriticalStrike.Chance(500, 0, spell, stats.GetValueOrDefault);
        Assert.Equal(500, Crit(shimmeronZero, spell: false));
        Assert.Equal(500, Crit(shimmeronZero, spell: true));
        Assert.Equal(500 + addedCrit, Crit(shimmeronThree, spell: false));
        Assert.Equal(500 + addedCrit, Crit(shimmeronThree, spell: true));

        var bleedItem = uniques.Create("Usurper's Penance", new Random(1))!;
        var bleedZero = Expanded(bleedItem, 0, 0, 0);
        var bleedTwo = Expanded(bleedItem, 0, 2, 0);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var conversion = new DamageConversion();
        List<(string Stat, int Value)> BleedRows(Dictionary<string, int> stats) =>
            AilmentPanelStats.Compute(new("main_hand", Spell: false), physical, conversion, stats.GetValueOrDefault);
        var bleedZeroRows = BleedRows(bleedZero);
        var bleedRows = BleedRows(bleedTwo);
        const string bleedRow = "minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage";
        Assert.True(bleedRows.Single(r => r.Stat == bleedRow).Value
            > bleedZeroRows.Single(r => r.Stat == bleedRow).Value);
        var bleedZeroSettings = Game.BleedSettings(bleedZeroRows, "main_hand", bleedZero.GetValueOrDefault,
            new Dictionary<string, int>(), melee: false, bow: false);
        var bleedSettings = Game.BleedSettings(bleedRows, "main_hand", bleedTwo.GetValueOrDefault,
            new Dictionary<string, int>(), melee: false, bow: false);
        Assert.True(bleedSettings.Seconds < bleedZeroSettings.Seconds);
        Assert.True(bleedSettings.Max > bleedZeroSettings.Max);

        var fenumus = uniques.Create("Fenumus' Toxins", new Random(1))!;
        var maligaro = uniques.Create("Maligaro's Cruelty", new Random(1))!;
        var snakebite = uniques.Create("Snakebite", new Random(1))!;
        Dictionary<string, int> PoisonStats(int frenzy, int power)
        {
            var raw = ItemModStats.Sum([fenumus, maligaro, snakebite], Program.Items.Mods,
                subject: new StatConditions.Subject(Recent: new HashSet<string>()), statExists: exists);
            var result = ChargeEffects.Expand(raw.GetValueOrDefault, type => type switch
            {
                ChargeType.Frenzy => frenzy,
                ChargeType.Power => power,
                _ => 0,
            });
            result["current_frenzy_charges"] = frenzy;
            result["current_power_charges"] = power;
            result["object_inherent_damage_+%_final_per_frenzy_charge"] = 4;
            return result;
        }
        var poisonZero = PoisonStats(0, 0);
        var poisonThree = PoisonStats(3, 3);
        List<(string Stat, int Value)> PoisonRows(Dictionary<string, int> stats) =>
            AilmentPanelStats.Compute(new("spell", Spell: true), physical, conversion, stats.GetValueOrDefault);
        var poisonZeroRows = PoisonRows(poisonZero);
        var poisonRows = PoisonRows(poisonThree);
        const string poisonRow = "minimum_poison_damage_to_deal_per_minute_from_non_critical_spell_damage";
        Assert.True(poisonRows.Single(r => r.Stat == poisonRow).Value
            > poisonZeroRows.Single(r => r.Stat == poisonRow).Value);
        var poisonSettings = MonsterPoisons.Settings.From(poisonRows, "spell", poisonThree.GetValueOrDefault);
        Assert.Equal(Math.Clamp(poisonThree.GetValueOrDefault("base_chance_to_poison_on_hit_%"), 0, 100),
            poisonSettings.Chance);
        Assert.Equal(2 * (1 + poisonThree.GetValueOrDefault("poison_duration_+%") / 100.0),
            poisonSettings.Seconds, 10);

        var hopeshredder = uniques.Create("Hopeshredder", new Random(1))!;
        var hopeshredderStats = Expanded(hopeshredder, 0, 2, 0);
        var attackLeech = LeechSources.From(hopeshredderStats.GetValueOrDefault, attack: true);
        var spellLeech = LeechSources.From(hopeshredderStats.GetValueOrDefault, attack: false);
        Assert.True(attackLeech.Life[DamageType.Physical].Min > 0);
        Assert.Equal(0, spellLeech.Life.MinTotal);
        var auxium = uniques.Create("Auxium", new Random(1))!;
        var auxiumStats = Expanded(auxium, 0, 0, 2);
        var attackManaLeech = LeechSources.From(auxiumStats.GetValueOrDefault, attack: true);
        var spellManaLeech = LeechSources.From(auxiumStats.GetValueOrDefault, attack: false);
        Assert.True(attackManaLeech.Mana[DamageType.Physical].Min > 0);
        Assert.Equal(0, spellManaLeech.Mana.MinTotal);

        var replicaBadge = uniques.Create("Replica Badge of the Brotherhood", new Random(1))!;
        var fortificationStats = Expanded(replicaBadge, 3, 0, 0);
        var fortification = new FortificationSession().Observe(10, fortificationStats.GetValueOrDefault);
        Assert.Equal(20 + fortificationStats.GetValueOrDefault("base_max_fortification"), fortification.Maximum);

        var aylardex = uniques.Create("The Aylardex", new Random(1))!;
        var aylardexStats = Expanded(aylardex, 0, 0, 3);
        int manaShare = aylardexStats.GetValueOrDefault("base_damage_removed_from_mana_before_life_%");
        Assert.True(manaShare > 0);
        Assert.Equal(manaShare, PlayerDamageTaken.ManaBeforeLifePct(DamageType.Fire, aylardexStats.GetValueOrDefault));
        Assert.Equal(manaShare, PlayerDamageTaken.FromMana(100, 0,
            DamagePacket.Of(DamageType.Fire, 100, 100), 1000, aylardexStats.GetValueOrDefault));

        var machina = uniques.Create("Machina Mitts", new Random(1))!;
        var recoveryStats = Expanded(machina, 0, 0, 3);
        Assert.Equal(1 + recoveryStats.GetValueOrDefault("life_recovery_rate_+%") / 100.0,
            PanelStats.LifeRegenerationMultiplier(recoveryStats.GetValueOrDefault), 10);
        Assert.Equal(1000 * 20 * (1 + recoveryStats.GetValueOrDefault("energy_shield_recovery_rate_+%") / 100.0),
            EnergyShield.RechargePerMinute(1000, recoveryStats.GetValueOrDefault), 10);
        var machinaRaw = ItemModStats.Sum([machina], Program.Items.Mods);
        Assert.Equal(1, machinaRaw.GetValueOrDefault("lose_power_charge_each_second_if_not_detonated_mines_recently"));
        var machinaCharges = new ChargeSession();
        machinaCharges.Observe(machinaRaw.GetValueOrDefault);
        machinaCharges.Admit(0, [new ChargeEvent.Grant(ChargeType.Power, 3, "fixture")],
            machinaRaw.GetValueOrDefault, () => default, alive: true, new Random(1));
        void StepMachina(double at, bool detonated = false) => machinaCharges.Step(at, null, () => default,
            alive: true, moving: false, inTown: false, new Random(1), mineDetonatedRecently: detonated);
        StepMachina(0); StepMachina(1);
        Assert.Equal(2, machinaCharges.Effective(ChargeType.Power));
        StepMachina(2, true); StepMachina(3.9, true);
        Assert.Equal(2, machinaCharges.Effective(ChargeType.Power));
        StepMachina(4.1); Assert.Equal(2, machinaCharges.Effective(ChargeType.Power));
        StepMachina(5.1); Assert.Equal(1, machinaCharges.Effective(ChargeType.Power));
        machinaCharges.Observe(_ => 0); StepMachina(7.1);
        Assert.Equal(1, machinaCharges.Effective(ChargeType.Power));
    }

    void Per_rage_item_coefficients_reach_existing_final_consumers()
    {
        var exists = (string name) => Program.Stats.Has(name);
        var definitions = new[]
        {
            (Unique: "Kaom's Primacy", Mod: "PhysicalAddedAsFirePerRageUnique__1",
                Source: RageStats.PhysicalAddedAsFirePerRage, Target: "physical_damage_%_to_add_as_fire", Value: 1),
            (Unique: "Rigwald's Command", Mod: "BleedDotMultiplierPerRagePerEquippedAxeUnique__1",
                Source: RageStats.BleedDotMultiplierPerRagePerEquippedAxe, Target: "bleeding_dot_multiplier_+", Value: 2),
            (Unique: "The Grey Wind", Mod: "FireDoTMultiPerRageUnique_1",
                Source: RageStats.FireDotMultiplierPerRage, Target: "fire_dot_multiplier_+", Value: 2),
        };
        var uniques = new UniqueCatalog(Program.Items);
        Item? rigwald = null;
        foreach (var definition in definitions)
        {
            Assert.Contains(Mod(definition.Mod).Stats, stat => stat.Id == definition.Source);
            var item = uniques.Create(definition.Unique, new Random(1));
            Assert.NotNull(item);
            Assert.Equal(definition.Value, ItemModStats.Sum([item], Program.Items.Mods,
                statExists: exists).GetValueOrDefault(definition.Source));
            if (definition.Unique == "Rigwald's Command") rigwald = item;
        }
        var isolated = definitions.Select(definition => WithMod(definition.Mod, definition.Value)).ToArray();

        int Get(Dictionary<uint, int> stats, string name) =>
            Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Dictionary<uint, int> Expanded(int rage, bool axe = true)
        {
            var folded = ItemModStats.Sum(isolated, Program.Items.Mods, subject: new(Axe: axe), statExists: exists);
            var stats = folded
                .Where(entry => Program.Stats.Key(entry.Key) is not null)
                .ToDictionary(entry => Program.Stats.Key(entry.Key)!.Value, entry => entry.Value);
            RageStats.ExpandItems(stats, rage);
            return stats;
        }

        var zero = Expanded(0);
        var ten = Expanded(10);
        foreach (var definition in definitions)
        {
            Assert.Equal(definition.Value * 10, Get(ten, definition.Target));
            Assert.Equal(0, Get(zero, definition.Target));
            Assert.Equal(definition.Value * 5, Get(Expanded(5), definition.Target));
        }
        var flat = new Dictionary<uint, int>
        {
            [Program.Stats.Key(RageStats.PhysicalAddedAsFirePerRage)!.Value] = 1,
            [Program.Stats.Key("physical_damage_%_to_add_as_fire")!.Value] = 7,
        };
        RageStats.ExpandItems(flat, 10);
        Assert.Equal(17, Get(flat, "physical_damage_%_to_add_as_fire"));
        var saturated = new Dictionary<uint, int>
        {
            [Program.Stats.Key(RageStats.PhysicalAddedAsFirePerRage)!.Value] = int.MaxValue,
            [Program.Stats.Key("physical_damage_%_to_add_as_fire")!.Value] = int.MaxValue,
        };
        RageStats.ExpandItems(saturated, 2);
        Assert.Equal(int.MaxValue, Get(saturated, "physical_damage_%_to_add_as_fire"));

        var withoutAxe = ItemModStats.Sum([isolated[1]], Program.Items.Mods,
            subject: new(Axe: false), statExists: exists);
        Assert.DoesNotContain(RageStats.BleedDotMultiplierPerRagePerEquippedAxe, withoutAxe);
        Assert.False(withoutAxe.ContainsKey("bleeding_dot_multiplier_+"));

        var rage = new RageState();
        var scaledInputs = new RageState.Inputs(EffectPercent: 50, NoInherentLoss: true);
        Assert.True(rage.Gain(10, 0, scaledInputs));
        Assert.Equal(15, rage.Read(0, scaledInputs).Effective);
        rage.Clear();
        var decaying = new RageState.Inputs();
        Assert.True(rage.Gain(10, 0, decaying));
        Assert.Equal(10, rage.Read(2, decaying).Effective);
        Assert.Equal(0, rage.Read(3, decaying).Effective);

        // The three sources reach the real damage path: 10 Rage is 10% added fire, 20% bleed DoT and
        // 20% fire DoT. The ordinary Rage attack multiplier remains separate and never reaches either ailment.
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var conversion = DamageStats.Conversion(ten, Program.Stats, attack: true);
        var hit = PlayerHitDamage.Resolve(physical, name => Get(ten, name), conversion,
            spell: false, melee: true);
        Assert.Equal(new DamageRange(100, 100), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(10, 10), hit[DamageType.Fire]);
        var attackStats = new Dictionary<uint, int>(ten)
        {
            [Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value] = 10,
        };
        var ragedHit = RageStats.ApplyAttack(hit, name => Get(attackStats, name));
        Assert.Equal(new DamageRange(110, 110), ragedHit[DamageType.Physical]);
        Assert.Equal(new DamageRange(11, 11), ragedHit[DamageType.Fire]);

        var context = new AilmentPanelStats.Context("main_hand", Spell: false, Melee: true);
        var rows = AilmentPanelStats.Compute(context, physical, conversion, name => Get(ten, name));
        var ragedRows = AilmentPanelStats.Compute(context, physical, conversion, name => Get(attackStats, name));
        Assert.Equal(rows, ragedRows);
        var bleed = Game.BleedSettings(rows, "main_hand", name => Get(ten, name),
            new Dictionary<string, int>(), melee: true, bow: false);
        var ignite = MonsterIgnites.Settings.From(rows, "main_hand", name => Get(ten, name));
        Assert.Equal(84, bleed.Min, 6);
        Assert.Equal(84, bleed.Max, 6);
        Assert.Equal(10.8, ignite.Min, 6);
        Assert.Equal(10.8, ignite.Max, 6);

        // Active-set qualification is tested through the production stat projection. Rows 14/15, a
        // requirement-inactive axe and a removed source cannot leave the axe coefficient or its target behind.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-rage-items-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "rage-item", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "RageItems" + Guid.NewGuid().ToString("N"),
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                new HashSet<string>(["Game"])),
        };
        try
        {
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            conn.PlayerStats[Program.Stats.Key("no_inherent_rage_loss")!.Value] = 1;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            var held = inventory.Add(PlayerInventories.Weapon1, 0, 0, rigwald!);
            var axe = ItemCatalog.Create(Program.Items.All.First(type =>
                type.Supported && type.Class == "One Hand Axe" && type.DropLevel <= 100));
            var heldAxe = inventory.Add(PlayerInventories.Offhand1, 0, 0, axe);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            var rigwaldType = global::Game.ItemsFor(conn)!.ByHash(held.Item.TypeHash);
            Assert.True(!global::Game.IsInactive(conn, held.Id),
                $"Rigwald's Command is inactive: {rigwaldType?.Class}, level {conn.Pools.Level}");
            Assert.True(!global::Game.IsInactive(conn, heldAxe.Id), "The active axe is requirement-inactive");
            Assert.True(global::Game.WeaponProfileFor(conn).Axe, "The equipped off-hand axe is not active");
            Assert.True(conn.Rage.Gain(10, conn.Clock.Elapsed.TotalSeconds,
                RageStats.Inputs(name => Get(global::Game.CharacterPlayerStats(conn), name))));

            int Equipped(string name)
            {
                var stats = global::Game.CharacterPlayerStats(conn);
                return Get(stats, name);
            }
            Assert.Equal(20, Equipped("bleeding_dot_multiplier_+"));

            conn.PlayerStats[Program.Stats.Key("rage_effect_+%")!.Value] = 50;
            Assert.Equal(30, Equipped("bleeding_dot_multiplier_+"));
            conn.PlayerStats.Remove(Program.Stats.Key("rage_effect_+%")!.Value);
            Assert.Equal(20, Equipped("bleeding_dot_multiplier_+"));

            conn.PlayerStats[Program.Stats.Key("maximum_rage")!.Value] = -21;
            Assert.Equal(18, Equipped("bleeding_dot_multiplier_+"));
            conn.PlayerStats.Remove(Program.Stats.Key("maximum_rage")!.Value);
            Assert.True(conn.Rage.Gain(1, 0, RageStats.Inputs(name => Get(global::Game.CharacterPlayerStats(conn), name))));
            Assert.Equal(20, Equipped("bleeding_dot_multiplier_+"));

            conn.PlayerStats.Remove(Program.Stats.Key("no_inherent_rage_loss")!.Value);
            var liveDecay = RageStats.Inputs(name => Get(global::Game.CharacterPlayerStats(conn), name));
            Assert.Equal(10, conn.Rage.Read(2, liveDecay).Current);
            Assert.Equal(20, Equipped("bleeding_dot_multiplier_+"));
            Assert.Equal(0, conn.Rage.Read(4, liveDecay).Current);
            Assert.Equal(0, Equipped("bleeding_dot_multiplier_+"));
            Assert.True(conn.Rage.Gain(10, 4, liveDecay));
            Assert.Equal(20, Equipped("bleeding_dot_multiplier_+"));

            inventory.Remove(PlayerInventories.Offhand1, heldAxe.Id);
            heldAxe = inventory.Add(PlayerInventories.Offhand2, 0, 0, heldAxe.Item);
            Assert.Equal(0, Equipped("bleeding_dot_multiplier_+"));

            inventory.Remove(PlayerInventories.Offhand2, heldAxe.Id);
            heldAxe = inventory.Add(PlayerInventories.Offhand1, 0, 0, heldAxe.Item);
            conn.Pools = conn.Pools with { Level = 1 };
            Assert.Equal(0, Equipped("bleeding_dot_multiplier_+"));

            conn.Pools = conn.Pools with { Level = 100 };
            Assert.Equal(20, Equipped("bleeding_dot_multiplier_+"));
            inventory.Remove(PlayerInventories.Weapon1, held.Id);
            Assert.Equal(0, Equipped(RageStats.BleedDotMultiplierPerRagePerEquippedAxe));
            Assert.Equal(0, Equipped("bleeding_dot_multiplier_+"));
        }
        finally
        {
            conn.Closed = true;
            conn.RetireOwnedCombat();
            lifetime.Stop(ex => throw ex);
            PlayerInventories.Forget(conn.Character!);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    void Ordinary_slot_conditions_reach_existing_consumers()
    {
        Source_relative_ring_conditions_use_qualified_counterparts();
        var exists = (string name) => Program.Stats.Has(name);
        var cases = new (string Source, string Mod, string Target, int Value, StatConditions.Subject Holds, StatConditions.Subject Fails)[]
        {
            ("cold_damage_+%_while_off_hand_is_empty", "IncreasedColdDamageWhileOffhandIsEmpty_",
                "cold_damage_+%", 17, new StatConditions.Subject(OffHandEmpty: true),
                new StatConditions.Subject(OffHandEmpty: false)),
            ("spell_suppression_chance_%_while_off_hand_empty", "ChanceToDodgeWhileOffhandIsEmpty",
                "base_spell_suppression_chance_%", 23, new StatConditions.Subject(OffHandEmpty: true),
                new StatConditions.Subject(OffHandEmpty: false)),
            ("spell_suppression_chance_%_while_off_hand_empty", "VillageSuppressChanceEmptyOffhand",
                "base_spell_suppression_chance_%", 29, new StatConditions.Subject(OffHandEmpty: true),
                new StatConditions.Subject(OffHandEmpty: false)),
            ("enemy_extra_damage_rolls_if_magic_ring_equipped", "AnyRingMagicDamageExtraRollUnique__1",
                "base_enemy_extra_damage_rolls", -1,
                new StatConditions.Subject(Value: name => name == "magic_ring_equipped" ? 1 : null),
                new StatConditions.Subject(Value: name => name == "magic_ring_equipped" ? 0 : null)),
            ("item_found_quantity_+%_if_wearing_a_magic_item", "ItemQuantityWhileWearingAMagicItemUnique__1",
                "item_found_quantity_+%", 13,
                new StatConditions.Subject(Value: name => name == "equipped_magic" ? 1 : null),
                new StatConditions.Subject(Value: name => name == "equipped_magic" ? 0 : null)),
            ("item_found_rarity_+%_if_wearing_a_normal_item", "ItemRarityWhileWearingANormalItemUnique__1",
                "item_found_rarity_+%", 83,
                new StatConditions.Subject(Value: name => name == "equipped_normal" ? 1 : null),
                new StatConditions.Subject(Value: name => name == "equipped_normal" ? 0 : null)),
        };
        foreach (var entry in cases)
        {
            Assert.True(exists(entry.Source), entry.Source);
            Assert.True(exists(entry.Target), entry.Target);
            var item = WithMod(entry.Mod, entry.Value);
            Assert.Equal(StatConditions.State.Holds, StatConditions.Of(entry.Source, entry.Holds));
            Assert.Equal(StatConditions.State.Fails, StatConditions.Of(entry.Source, entry.Fails));
            Assert.Equal(entry.Target, StatConditions.Fold(entry.Source, exists));
            var held = ItemModStats.Sum([item], Program.Items.Mods, subject: entry.Holds, statExists: exists);
            Assert.Equal(entry.Value, held[entry.Target]);
            Assert.False(held.ContainsKey(entry.Source));
            var rejected = ItemModStats.Sum([item], Program.Items.Mods, subject: entry.Fails, statExists: exists);
            Assert.False(rejected.ContainsKey(entry.Source));
            Assert.False(rejected.ContainsKey(entry.Target));
            Assert.Equal(entry.Value, ItemModStats.Sum([item], Program.Items.Mods).GetValueOrDefault(entry.Source));
        }

        var uniques = new UniqueCatalog(Program.Items);
        foreach (var (name, source) in new[]
                 {
                     ("White Wind", "cold_damage_+%_while_off_hand_is_empty"),
                     ("White Wind", "spell_suppression_chance_%_while_off_hand_empty"),
                     ("Viridi's Veil", "enemy_extra_damage_rolls_if_magic_ring_equipped"),
                     ("The Ascetic", "item_found_quantity_+%_if_wearing_a_magic_item"),
                     ("The Ascetic", "item_found_rarity_+%_if_wearing_a_normal_item"),
                 })
        {
            var item = uniques.Create(name, new Random(1));
            Assert.NotNull(item);
            Assert.NotEqual(0, ItemModStats.Sum([item], Program.Items.Mods).GetValueOrDefault(source));
        }
    }

    /// <summary>An item carrying one explicit roll of the named mod, at the given value.</summary>
    static Item WithMod(string id, int value)
    {
        var mod = Mod(id).Roll(ModSection.Explicit, new Random(1));
        return new Item(1, ["Mods"], 0)
        {
            Rarity = ItemRarity.Unique,
            Mods = [mod with { Values = [.. mod.Values.Select(_ => value)] }],
        };
    }

    void Recent_events_expire_at_four_seconds_and_cache_inputs_track_condition_changes()
    {
        var events = new RecentEvents();
        events.Record(StatConditions.Crit, 10);
        Assert.Contains(StatConditions.Crit, events.Snapshot(13.999));
        Assert.Equal(14, events.NextExpiry(10));
        Assert.DoesNotContain(StatConditions.Crit, events.Snapshot(14));
        Assert.Null(events.NextExpiry(14));
        events.Record(StatConditions.BeenHit, 10);
        Assert.Contains(StatConditions.BeenHit, events.Snapshot(13.999));
        Assert.DoesNotContain(StatConditions.BeenHit, events.Snapshot(14));

        var baseline = CacheKey(new HashSet<string> { "grace" }, new HashSet<string> { StatConditions.Crit }, 14, 600);
        Assert.False(baseline.Matches(CacheKey(new HashSet<string>(), new HashSet<string> { StatConditions.Crit }, 14, 600)));
        Assert.False(baseline.Matches(CacheKey(new HashSet<string> { "grace" }, new HashSet<string>(), null, 600)));
        Assert.False(baseline.Matches(CacheKey(new HashSet<string> { "grace" }, new HashSet<string> { StatConditions.Crit }, 14, 599)));

        static global::Game.StatSourcesKey CacheKey(IReadOnlySet<string> auras, IReadOnlySet<string> recent, double? expiry, int strength) =>
            new([], new Dictionary<ushort, ushort>(), [], new Dictionary<uint, int>(), 1, false, 1, null, [], 0, [],
                auras, recent, expiry, strength, 0, 0, 1, 1);
    }

    void A_stats_condition_is_decided_from_the_state_the_server_keeps()
    {
        // A stat without a condition always counts, even with everything known.
        Assert.Equal(StatConditions.State.None, StatConditions.Of("maximum_life", StatConditions.Subject.Unknown));
        Assert.True(StatConditions.Counts(StatConditions.State.None));

        // The weapon and shield conditions follow the current weapons.
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("attack_speed_while_dual_wielding_+%", new StatConditions.Subject(DualWield: true)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("attack_speed_while_dual_wielding_+%", new StatConditions.Subject(DualWield: false)));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("critical_strike_chance_while_wielding_shield_+%", new StatConditions.Subject(Shield: true)));

        // An aura condition follows the auras running now; "X_and_Y" needs both.
        var grace = new HashSet<string> { "grace" };
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("chance_to_evade_attacks_%_while_affected_by_grace", new StatConditions.Subject(Auras: grace)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("x_while_affected_by_grace_and_determination", new StatConditions.Subject(Auras: grace)));

        // A "recently" condition follows the events the subject reports for the last four seconds, and reads a "not X"
        // id as the absence of X. Its key is the event, not the verb the id happens to spell it with.
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_you_have_crit_recently", out string crit, out bool negated));
        Assert.Equal(StatConditions.Crit, crit);
        Assert.False(negated);
        var hitAndCrit = new HashSet<string> { StatConditions.HitEnemy, StatConditions.Crit };
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("damage_+%_if_you_have_crit_recently", new StatConditions.Subject(Recent: hitAndCrit)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("spell_damage_+%_if_you_have_crit_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy })));
        // A bare "hit" is a hit received. The explicit enemy-hit spellings still mean a hit dealt, so the two directions
        // cannot satisfy each other's recently conditions.
        Assert.True(StatConditions.TryRecentEvent("evasion_+%_if_hit_recently", out string receivedHit, out bool receivedNegated));
        Assert.Equal(StatConditions.BeenHit, receivedHit);
        Assert.False(receivedNegated);
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_hit_an_enemy_recently", out string dealtHit, out _));
        Assert.Equal(StatConditions.HitEnemy, dealtHit);
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_you_have_not_hit_an_enemy_recently",
            out string notDealtHit, out bool notDealtHitNegated));
        Assert.Equal(StatConditions.HitEnemy, notDealtHit);
        Assert.True(notDealtHitNegated);
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_enemy_hit_recently", out string otherDealtHit, out _));
        Assert.Equal(StatConditions.HitEnemy, otherDealtHit);
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("evasion_+%_if_hit_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy })));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("evasion_+%_if_hit_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.BeenHit })));
        Assert.True(StatConditions.TryRecentEvent("damage_taken_+%_if_not_hit_recently_final",
            out string notReceivedHit, out bool notReceivedNegated));
        Assert.Equal(StatConditions.BeenHit, notReceivedHit);
        Assert.True(notReceivedNegated);
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("damage_taken_+%_if_not_hit_recently_final",
                new StatConditions.Subject(Recent: new HashSet<string>())));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("damage_taken_+%_if_not_hit_recently_final",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.BeenHit })));
        // "if enemy not killed recently" holds exactly when the kill event is absent.
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("attack_speed_+%_if_enemy_not_killed_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy })));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("attack_speed_+%_if_enemy_not_killed_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.KilledEnemy })));

        // An "if you have at least N X" condition compares the attribute the subject reads.
        Assert.True(StatConditions.TryThreshold("attack_speed_+%_if_you_have_at_least_600_strength",
            out string attribute, out int needed));
        Assert.Equal("strength", attribute);
        Assert.Equal(600, needed);
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("attack_speed_+%_if_you_have_at_least_600_strength",
                new StatConditions.Subject(Value: n => n == "strength" ? 650 : 0)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("attack_speed_+%_if_you_have_at_least_600_strength",
                new StatConditions.Subject(Value: n => n == "strength" ? 599 : 0)));

        // Per-charge ids belong to ChargeEffects, which expands them against the counts already: this layer leaves them
        // alone so the same value is never counted twice, whatever the subject knows.
        Assert.True(StatConditions.NamesACharge("attack_speed_+%_per_frenzy_charge"));
        Assert.True(StatConditions.NamesACharge("add_power_charge_on_kill"));
        Assert.Equal(StatConditions.State.NotModelled,
            StatConditions.Of("attack_speed_+%_per_frenzy_charge", StatConditions.Subject.Unknown));
        Assert.Null(StatConditions.Fold("attack_speed_+%_per_frenzy_charge", _ => true));

        // A condition whose subject the server does not model (a shrine, elusive, a league buff) stays unmodelled, so
        // its stat keeps counting rather than being silently dropped; the same holds with no subject at all.
        foreach (var id in new[]
                 {
                     "movement_speed_+%_while_affected_by_elusive",
                     "x_while_you_have_a_shrine_buff",
                     "attack_speed_while_dual_wielding_+%",
                     "damage_+%_if_you_have_crit_recently",
                 })
        {
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(id, StatConditions.Subject.Unknown));
            Assert.True(StatConditions.Counts(StatConditions.State.NotModelled));
        }
    }

    void The_shield_condition_is_answered_in_every_spelling_the_tables_use()
    {
        // The 3.29.3b tables spell the shield condition two ways: "_while_holding_a_shield" (18 ids) and
        // "_while_holding_shield" (29 ids, the jewel/shield mods such as Battlemage's). Both name the same subject, so
        // both must be read from Subject.Shield — a spelling that is missed falls through to NotModelled and the mod is
        // counted unconditionally, i.e. a shield-conditional jewel would grant its value with no shield equipped.
        foreach (string id in new[]
                 {
                     "spell_damage_+%_while_holding_shield",
                     "attack_speed_+%_while_holding_shield",
                     "maximum_life_+%_while_holding_shield",
                     "spell_critical_strike_chance_+%_while_holding_shield",
                     "attack_damage_+%_while_holding_a_shield",
                     "critical_strike_chance_while_wielding_shield_+%",
                 })
        {
            Assert.Equal(StatConditions.State.Holds, StatConditions.Of(id, new StatConditions.Subject(Shield: true)));
            Assert.Equal(StatConditions.State.Fails, StatConditions.Of(id, new StatConditions.Subject(Shield: false)));
            // No subject at all still keeps the old behaviour: unmodelled, so the stat is left as it was.
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(id, StatConditions.Subject.Unknown));
        }

        // The plain name the two spellings both fold onto is the stat the pools read.
        Assert.Equal("spell_damage_+%", StatConditions.PlainOf("spell_damage_+%_while_holding_shield"));
        Assert.Equal("attack_damage_+%", StatConditions.PlainOf("attack_damage_+%_while_holding_a_shield"));
    }

    void A_shield_condition_that_holds_counts_on_its_plain_stat_and_not_without_a_shield()
    {
        var exists = (string n) => Program.Stats.Has(n);

        // Battlemage's (ShieldSpellDamageJewel): spell_damage_+%_while_holding_shield. The plain name is a real stat the
        // damage pipeline reads, so a shield folds it there; with no shield nothing counts.
        const string jewel = "ShieldSpellDamageJewel";
        Assert.Contains(Mod(jewel).Stats, s => s.Id == "spell_damage_+%_while_holding_shield");
        var item = WithMod(jewel, 20);
        var on = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: true), statExists: exists);
        Assert.Equal(20, on.GetValueOrDefault("spell_damage_+%"));
        Assert.False(on.ContainsKey("spell_damage_+%_while_holding_shield"));
        var off = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: false), statExists: exists);
        Assert.Equal(0, off.GetValueOrDefault("spell_damage_+%"));
        Assert.False(off.ContainsKey("spell_damage_+%_while_holding_shield"));
        // Without a subject (every pre-existing caller) the mod keeps its old behaviour: counted on its own id.
        Assert.Equal(20, ItemModStats.Sum([item], Program.Items.Mods)
            .GetValueOrDefault("spell_damage_+%_while_holding_shield"));

        // A shield id that already has a consumer of its own is gated but never folded, or the consumer would double
        // count it. Charging (ShieldAttackSpeedJewel): attack_speed_+%_while_holding_shield, which Progression's
        // attack-speed block already reads under a shield. With a shield it counts only on its own id.
        var dedicated = WithMod("ShieldAttackSpeedJewel", 5);
        var dedicatedOn = ItemModStats.Sum([dedicated], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: true), statExists: exists);
        Assert.Equal(5, dedicatedOn.GetValueOrDefault("attack_speed_+%_while_holding_shield"));
        Assert.Equal(0, dedicatedOn.GetValueOrDefault("attack_speed_+%"));
        Assert.Equal(0, ItemModStats.Sum([dedicated], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: false), statExists: exists)
            .GetValueOrDefault("attack_speed_+%_while_holding_shield"));
    }

    void A_held_condition_counts_on_its_plain_stat_and_nothing_changes_without_a_subject()
    {
        // A real jewel roll whose plain name the damage pipeline reads.
        const string id = "SynthesisImplicitSpellDamageWithDualWieldJewel1";
        Assert.Contains(Mod(id).Stats, s => s.Id == "spell_damage_+%_while_dual_wielding");
        var item = WithMod(id, 30);
        var exists = (string n) => Program.Stats.Has(n);

        // Dual wielding: the value lands on the plain stat; not dual wielding: nothing at all.
        var on = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(DualWield: true), statExists: exists);
        Assert.Equal(30, on.GetValueOrDefault("spell_damage_+%"));
        Assert.False(on.ContainsKey("spell_damage_+%_while_dual_wielding"));
        var off = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(DualWield: false), statExists: exists);
        Assert.Equal(0, off.GetValueOrDefault("spell_damage_+%"));

        // A "recently" roll does the same: the plain name while the event is inside the window, nothing outside it.
        const string recentId = "IncreasedDamageIfFrozenRecentlyUnique__1";
        Assert.Equal("damage_+%", StatConditions.PlainOf("damage_+%_if_you_have_frozen_enemy_recently"));
        Assert.Contains(Mod(recentId).Stats, s => s.Id == "damage_+%_if_you_have_frozen_enemy_recently");
        var recentItem = WithMod(recentId, 20);
        var froze = StatConditions.Of("damage_+%_if_you_have_frozen_enemy_recently",
            new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.FrozeEnemy }));
        Assert.Equal(StatConditions.State.Holds, froze);

        // Kintsugi uses "evasion" in its stat id, but Progression's real evasion consumer reads "evasion_rating".
        const string kintsugi = "IncreasedEvasionIfHitRecentlyUnique___1";
        Assert.Contains(Mod(kintsugi).Stats, s => s.Id == "evasion_+%_if_hit_recently");
        Assert.Equal("evasion_rating_+%", StatConditions.Fold("evasion_+%_if_hit_recently", exists));
        var kintsugiItem = WithMod(kintsugi, 100);
        var kintsugiHit = ItemModStats.Sum([kintsugiItem], Program.Items.Mods,
            subject: new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.BeenHit }),
            statExists: exists);
        Assert.Equal(100, kintsugiHit.GetValueOrDefault("evasion_rating_+%"));
        Assert.False(kintsugiHit.ContainsKey("evasion_+%_if_hit_recently"));
        var enemyHitOnly = ItemModStats.Sum([kintsugiItem], Program.Items.Mods,
            subject: new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy }),
            statExists: exists);
        Assert.Equal(0, enemyHitOnly.GetValueOrDefault("evasion_rating_+%"));

        // The synthetic command uses the same accepted-incoming-hit commit as a monster contact. A failed transfer
        // invalidates retired work without forgetting history; an actual lifecycle clear removes it.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-conditional-item-stats-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            var conn = new ClientState(new(1, "received-hit", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900,
            };
            Assert.True(Game.TrySyntheticEnemyHit(conn, GameServer.Core.World.DamageType.Physical, 1,
                block: false, spell: false, suppress: false, out _));
            double now = conn.Clock.Elapsed.TotalSeconds;
            Assert.Equal(1, conn.RecentEnemyHits.Count(now));
            Assert.True(conn.RecentEvents.Happened(StatConditions.BeenHit, now));
            conn.InvalidateIncomingHits(clear: false);
            Assert.Equal(1, conn.RecentEnemyHits.Count(now));
            Assert.True(conn.RecentEvents.Happened(StatConditions.BeenHit, now));
            conn.InvalidateIncomingHits();
            Assert.Equal(0, conn.RecentEnemyHits.Count(now));
            Assert.False(conn.RecentEvents.Happened(StatConditions.BeenHit, now));
        }
        finally
        {
            TestEnvironment.ReleaseSaves(path);
        }

        // A stat with a consumer of its own is never folded, or its mod would count twice.
        var dedicated = WithMod("BlockWhileDualWieldingUniqueDagger3", 10);
        Assert.Equal(10, ItemModStats.Sum([dedicated], Program.Items.Mods,
            subject: new StatConditions.Subject(DualWield: true), statExists: exists)
            .GetValueOrDefault("block_while_dual_wielding_%"));

        // No subject and no catalog, as every pre-existing caller passes: nothing changes at all.
        Assert.Equal(30, ItemModStats.Sum([item], Program.Items.Mods)
            .GetValueOrDefault("spell_damage_+%_while_dual_wielding"));
        Assert.Equal(10, ItemModStats.Sum([dedicated], Program.Items.Mods)
            .GetValueOrDefault("block_while_dual_wielding_%"));

        // An unconditional mod is untouched by any subject.
        var plain = WithMod(Program.Items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == "base_maximum_life").Id, 42);
        foreach (var subject in new[]
                 {
                     StatConditions.Subject.Unknown,
                     new StatConditions.Subject(DualWield: true),
                     new StatConditions.Subject(DualWield: false, Shield: true),
                 })
            Assert.Equal(42, ItemModStats.Sum([plain], Program.Items.Mods, subject: subject)
                .GetValueOrDefault("base_maximum_life"));
    }

    void Existing_owner_predicates_wire_exact_events_and_final_consumers()
    {
        var exists = (string name) => Program.Stats.Has(name);
        IReadOnlySet<string> Recent(params string[] keys) => new HashSet<string>(keys, StringComparer.Ordinal);

        // Real table ids and their final consumer names. These are deliberately not per-item text branches: the
        // aliases are the Stats rows consumed by movement, projectile, recovery, conversion, avoidance and damage.
        var movement = WithMod("MovementVelocityIfVaalSkillUsedRecentlyUnique__1_", 10);
        var gained = WithMod("LifeGainOnHitIfVaalSkillUsedRecentlyUnique__1", 10);
        var beltimber = WithMod("NumberOfProjectilesIfUsedAMovementSkillRecentlyUnique__1", 2);
        var hrimnor = WithMod("AvoidFreezeAndChillIfFireSkillUsedRecentlyUnique__1", 100);
        var fullEs = WithMod("DamageTakenOnFullESUnique__1", 15);
        var vaal = Recent(StatConditions.UsedVaalSkill);
        Assert.Equal(10, ItemModStats.Sum([movement], Program.Items.Mods, subject: new(Recent: vaal), statExists: exists)
            .GetValueOrDefault("base_movement_velocity_+%"));
        Assert.Equal(10, ItemModStats.Sum([gained], Program.Items.Mods, subject: new(Recent: vaal), statExists: exists)
            .GetValueOrDefault("base_life_gain_per_target"));
        var movedProjectiles = ItemModStats.Sum([beltimber], Program.Items.Mods,
            subject: new(Recent: Recent(StatConditions.UsedMovementSkill)), statExists: exists);
        Assert.Equal(2, movedProjectiles.GetValueOrDefault("number_of_additional_projectiles"));
        var profile = GatewayProjectileModifiers.Resolve(new ProjectileProfile("conditional-recent") { Count = 1 },
            name => movedProjectiles.GetValueOrDefault(name), attack: false);
        Assert.Equal(3, profile.Count);
        Assert.Empty(ItemModStats.Sum([beltimber], Program.Items.Mods,
            subject: new(Recent: Recent(StatConditions.UsedFireSkill)), statExists: exists));

        var avoided = ItemModStats.Sum([hrimnor], Program.Items.Mods,
            subject: new(Recent: Recent(StatConditions.UsedFireSkill)), statExists: exists);
        Assert.Equal(100, avoided.GetValueOrDefault("base_avoid_freeze_%"));
        Assert.Equal(100, avoided.GetValueOrDefault("base_avoid_chill_%"));
        Assert.False(avoided.ContainsKey("avoid_freeze_and_chill_%"));
        var target = new PlayerElementalAilments();
        target.Apply(DamagePacket.Of(DamageType.Cold, 1000, 1000), DamagePacket.Of(DamageType.Cold, 1000, 1000),
            critical: true, maxLife: 100, now: 0, get: name => avoided.GetValueOrDefault(name),
            random: new Random(1));
        Assert.Equal((0, false), (target.Snapshot(0).Chill, target.Snapshot(0).Freeze));

        var fullFlags = new Dictionary<string, bool>
        {
            ["_while_es_full"] = true, ["_on_full_energy_shield"] = true, ["_while_es_leeching"] = false,
        };
        var damageTaken = ItemModStats.Sum([fullEs], Program.Items.Mods, subject: new(Flags: fullFlags), statExists: exists);
        Assert.Equal(15, damageTaken.GetValueOrDefault("base_damage_taken_+%"));
        Assert.False(damageTaken.ContainsKey("damage_taken_+%"));
        Assert.Equal(15, PlayerDamageTaken.IncreasedPct(DamageType.Physical, melee: false,
            name => damageTaken.GetValueOrDefault(name)));
        var nomicMove = WithMod("MovementVelocityWhileOnFullEnergyShieldUniqueBootsDex8", 20);
        Assert.Equal(20, ItemModStats.Sum([nomicMove], Program.Items.Mods,
            subject: new(Flags: fullFlags), statExists: exists).GetValueOrDefault("base_movement_velocity_+%"));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("movement_velocity_+%_on_full_energy_shield", new(Flags: new Dictionary<string, bool>
            {
                ["_on_full_energy_shield"] = false,
            })));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("shock_effect_+%_while_es_leeching", new(Flags: new Dictionary<string, bool>
            {
                ["_while_es_leeching"] = true,
            })));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("unaffected_by_shock_while_es_leeching", new(Flags: new Dictionary<string, bool>
            {
                ["_while_es_leeching"] = false,
            })));

        // Arborix has a real Dash executor in Combat/Messages: exact effect ids commit UsedDash only after its admitted
        // action. Both the positive and negative four-second families fold onto their existing plain consumers.
        var dashActive = new StatConditions.Subject(Recent: Recent(StatConditions.UsedDash));
        var dashAbsent = new StatConditions.Subject(Recent: Recent(StatConditions.HitEnemy));
        foreach (var (mod, targetName, value) in new[]
                 {
                     ("AttackSpeedIfHaventUsedDashRecentlyUnique__1", "attack_speed_+%", 20),
                     ("ArrowsIfHaventUsedDashRecentlyUnique__1", "number_of_additional_arrows", 2),
                     ("EvasionRatingIfUsedDashRecentlyUnique__1", "evasion_rating_+%", 100),
                     ("MovementSpeedIfUsedDashRecentlyUnique__1", "base_movement_velocity_+%", 20),
                 })
        {
            var item = WithMod(mod, value);
            bool negative = mod.Contains("Havent", StringComparison.Ordinal);
            Assert.Equal(negative ? 0 : value, ItemModStats.Sum([item], Program.Items.Mods,
                subject: dashActive, statExists: exists).GetValueOrDefault(targetName));
            Assert.Equal(negative ? value : 0, ItemModStats.Sum([item], Program.Items.Mods,
                subject: dashAbsent, statExists: exists).GetValueOrDefault(targetName));
        }

        foreach (var (mod, targetName, eventKey, value) in new[]
                 {
                     ("PhysicalAddedAsFireIfUsedRubyFlaskRecentlyUnique__1",
                         "physical_damage_%_to_add_as_fire", StatConditions.UsedRubyFlask, 30),
                     ("PhysicalAddedAsColdIfUsedSapphireFlaskRecentlyUnique__1",
                         "physical_damage_%_to_add_as_cold", StatConditions.UsedSapphireFlask, 30),
                     ("PhysicalAddedAsLightningIfUsedTopazFlaskRecentlyUnique__1",
                         "physical_damage_%_to_add_as_lightning", StatConditions.UsedTopazFlask, 30),
                     ("PhysicalAddedAsChaosIfUsedAmethystFlaskRecentlyUnique__1",
                         "physical_damage_%_to_add_as_chaos", StatConditions.UsedAmethystFlask, 50),
                 })
        {
            var item = WithMod(mod, value);
            var held = ItemModStats.Sum([item], Program.Items.Mods, subject: new(Recent: Recent(eventKey)),
                statExists: exists);
            Assert.True(held.GetValueOrDefault(targetName) > 0);
            Assert.Empty(ItemModStats.Sum([item], Program.Items.Mods,
                subject: new(Recent: Recent(StatConditions.HitEnemy)), statExists: exists));
        }

        // Explicit eight-second crit rows share one timestamp but not the ordinary four-second Crit expiry. A
        // non-critical hit arms Duskdawn independently; a later critical hit extends both critical windows.
        var events = new RecentEvents();
        const double fractionalAt = .1;
        double eightExpiry = fractionalAt + RecentEvents.EightSecondWindow;
        events.Record("eight-second-boundary", fractionalAt, RecentEvents.EightSecondWindow);
        Assert.True(events.Happened("eight-second-boundary", eightExpiry - .001));
        Assert.False(events.Happened("eight-second-boundary", eightExpiry));
        Assert.Equal(eightExpiry, events.NextExpiry(fractionalAt));
        Assert.Null(events.NextExpiry(eightExpiry));
        events.Clear();
        events.RecordHit(10, critical: true);
        Assert.True(events.Happened(StatConditions.Crit, 13.999));
        Assert.False(events.Happened(StatConditions.Crit, 14));
        Assert.True(events.Happened(StatConditions.Crit8Seconds, 17.999));
        Assert.False(events.Happened(StatConditions.Crit8Seconds, 18));
        events.RecordCriticalOutcome(15, critical: false);
        Assert.True(events.Happened(StatConditions.NonCrit, 18.999));
        Assert.False(events.Happened(StatConditions.NonCrit, 19));
        events.RecordCriticalOutcome(16, critical: true);
        Assert.Equal(19, events.NextExpiry(16));
        Assert.Equal(24, events.NextExpiry(20));
        var eight = new StatConditions.Subject(Recent: Recent(StatConditions.Crit8Seconds));
        Assert.Equal(200, ItemModStats.Sum([WithMod("SpellDamageIfYouHaveCritRecentlyUnique__1", 200)],
            Program.Items.Mods, subject: eight, statExists: exists).GetValueOrDefault("spell_damage_+%"));
        const string dotSource = "dot_multiplier_+_if_crit_in_past_8_seconds";
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of(dotSource, eight));
        Assert.Equal("dot_multiplier_+", StatConditions.Fold(dotSource, exists));
        Assert.Equal(40, ItemModStats.Sum([WithMod("DamageOverTimeMultiplierIfCrit8SecondsUnique__1_", 40)],
            Program.Items.Mods, subject: eight, statExists: exists).GetValueOrDefault("dot_multiplier_+"));
        Assert.Equal(120, ItemModStats.Sum([WithMod("LifeRegenerationIfCrit8SecondsUnique__1", 120)],
            Program.Items.Mods, subject: eight, statExists: exists)
            .GetValueOrDefault("life_regeneration_rate_per_minute_%"));
        Assert.Equal(60, ItemModStats.Sum([WithMod("CritMultiIfDealtNonCritRecentlyUnique__2", 60)],
            Program.Items.Mods, subject: new(Recent: Recent(StatConditions.NonCrit)), statExists: exists)
            .GetValueOrDefault("base_critical_strike_multiplier_+"));

        events.Clear();
        Assert.Empty(events.Snapshot(16));
        Assert.Null(events.NextExpiry(16));
    }

    void Physical_aegis_uses_its_real_grant_for_absorption_recharge_and_conditions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-physical-aegis-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string character = "PhysicalAegis" + Guid.NewGuid().ToString("N");

        ClientState NewConnection(uint ticket, bool hardMode = false, string? characterName = null)
        {
            string name = characterName ?? character;
            var connection = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), ticket, 1)
            {
                Character = name,
                PlayerLife = 1000,
                PlayerMana = 1000,
                PlayerSent = true,
                Instance = new(),
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League(
                        hardMode ? "Ruthless" : "test", hardMode ? "Ruthless" : "Standard", false, false)
                    { DataSet = hardMode ? "HardMode" : null },
                    new HashSet<string>(["Game"])),
            };
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                connection.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            return connection;
        }

        try
        {
            var conn = NewConnection(1);
            var items = (ItemCatalog)global::Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            var uniques = new UniqueCatalog(items);
            Item Ghastly() => uniques.Create("The Ghastly Theatre", new Random(504))!;
            var shield = Ghastly();
            var fireballGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            shield = shield with
            {
                Sockets = [new ItemSocket(SocketColour.White, fireballGem)],
                Links = [1],
                Mods = [.. shield.Mods, Mod("CriticalChanceAgainstBlindedEnemiesUnique__1")
                    .Roll(ModSection.Explicit, new Random(505))],
            };
            foreach (var old in inventory.Items(PlayerInventories.Offhand1)) inventory.Remove(PlayerInventories.Offhand1, old.Id);
            inventory.Add(PlayerInventories.Offhand1, 0, 0, shield);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            conn.EnergyShield.SetMaximum(200, refill: true);
            conn.Ward.SetMaximum(100);
            conn.Ward.Restore();

            // The trigger-only row is not published as a manual Actor skill, but the real GEPL/dat identity is present.
            var raw = global::Game.AllWornGrantedSkillsOf(conn);
            var source = Assert.Single(raw, g => g.EffectId == "PhysicalAegis");
            Assert.Equal((52147, 8958, 20, false, false),
                (source.EffectsPerLevelRow, source.Effect, source.Level, source.ManuallyCast, source.Support));
            Assert.DoesNotContain(global::Game.WornGrantedSkillsOf(conn), g => g.EffectId == "PhysicalAegis");
            Assert.Contains(shield.Mods, mod => mod.Hash == Mod("TriggeredPhysicalAegisSkillUnique__1").Hash);
            Assert.Contains(shield.Mods, mod => mod.Hash == Mod("AttackAndCastSpeedWithoutPhysicalAegisUnique__1").Hash);
            Assert.Contains(shield.Mods, mod => mod.Hash == Mod("CriticalStrikeChanceWithoutPhysicalAegisUnique__1").Hash);
            Assert.Contains(shield.Mods, mod => mod.Hash == Mod("NearbyEnemiesAreBlindedPhysicalAegisUnique__1").Hash);

            var aegis = global::Game.PhysicalAegisStatus(conn, 0);
            Assert.Equal((true, true, 1000, 1000, 10.0, source),
                (aegis.Exists, aegis.Active, aegis.Current, aegis.Maximum, aegis.RechargeDelay, aegis.Source));
            using var wire = new Wire();
            Assert.NotNull(global::Game.PhysicalAegisPresentation(conn));
            Assert.Equal((global::Game.PhysicalAegisBuffRow, 1000),
                (aegis.StageRow, aegis.PresentedCurrent));
            Assert.NotEqual((ushort)0, aegis.StageBuff);
            Assert.NotEqual((ushort)0, aegis.PersistBuff);
            var fireballSkill = Assert.Single(global::Game.GemSkillEffects(conn),
                g => global::Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball");
            // The debug producer shares the same typed-hit owner used by ordinary monster contacts.
            var debug = NewConnection(10);
            debug.Pools = global::Game.PoolsFor(debug, 100);
            var debugAegis = global::Game.PhysicalAegisStatus(debug, conn.Clock.Elapsed.TotalSeconds);
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(debug, DamageType.Physical, 100, block: false, spell: false,
                suppress: false, out var debugHit));
            Assert.Equal(1000 - debugHit.Damage, debugAegis.Current);

            // Mixed physical/lightning: once Aegis consumes all physical, the 50% lightning Mana-before-Life share
            // applies only to the remaining lightning packet. Without Aegis, the original 50/50 packet weights it down.
            const string lightningMana = "lightning_damage_%_taken_from_mana_before_life";
            var manaConn = NewConnection(11);
            manaConn.Pools = global::Game.PoolsFor(manaConn, 100);
            manaConn.Pools = manaConn.Pools with { EnergyShield = 0, Ward = 0, Mana = 1000 };
            manaConn.EnergyShield.SetMaximum(0, refill: true);
            manaConn.Ward.SetMaximum(0);
            manaConn.PlayerMana = 1000;
            manaConn.PlayerLife = 1000;
            var manaStats = global::Game.CharacterPlayerStats(manaConn);
            manaStats[Program.Stats.Key(lightningMana)!.Value] = 50;
            var mixed = DamagePacket.Of(DamageType.Physical, 100, 100)
                .Add(DamageType.Lightning, new DamageRange(100, 100));
            int lifeLoss = global::Game.DamagePlayerHitTyped(manaConn, 100, 100, 0,
                manaConn.Clock.Elapsed.TotalSeconds, manaStats, mixed);
            manaConn.PlayerLife -= (uint)lifeLoss;
            Assert.Equal((950u, 950u, 50), (manaConn.PlayerMana, manaConn.PlayerLife, lifeLoss));

            var noAegis = NewConnection(12, characterName: "NoAegis" + Guid.NewGuid().ToString("N"));
            noAegis.Pools = global::Game.PoolsFor(noAegis, 100);
            noAegis.Pools = noAegis.Pools with { EnergyShield = 0, Ward = 0, Mana = 1000 };
            noAegis.EnergyShield.SetMaximum(0, refill: true);
            noAegis.Ward.SetMaximum(0);
            noAegis.PlayerMana = 1000;
            noAegis.PlayerLife = 1000;
            Assert.False(global::Game.PhysicalAegisStatus(noAegis, noAegis.Clock.Elapsed.TotalSeconds).Exists);
            var noAegisStats = global::Game.CharacterPlayerStats(noAegis);
            noAegisStats[Program.Stats.Key(lightningMana)!.Value] = 50;
            lifeLoss = global::Game.DamagePlayerHitTyped(noAegis, 100, 100, 0,
                noAegis.Clock.Elapsed.TotalSeconds, noAegisStats, mixed);
            noAegis.PlayerLife -= (uint)lifeLoss;
            Assert.Equal((950u, 850u, 150), (noAegis.PlayerMana, noAegis.PlayerLife, lifeLoss));

            // A fully prevented block deals zero and preserves the shield. A real Ghastly block with Glancing Blows
            // still delivers 65% of the matching unblocked mitigated damage, and that accepted damage consumes the Aegis.
            var manaAegis = global::Game.PhysicalAegisStatus(manaConn, manaConn.Clock.Elapsed.TotalSeconds);
            manaConn.Pools = manaConn.Pools with { BlockChance = 100 };
            manaConn.IncomingHitRandom = new Random(1);
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(manaConn, DamageType.Physical, 100, block: true, spell: false,
                suppress: false, out var zeroBlocked));
            Assert.Equal((true, 0, 900, 950u, 950u),
                (zeroBlocked.Blocked, zeroBlocked.Damage, manaAegis.Current, zeroBlocked.Mana, zeroBlocked.Life));
            var blockControl = NewConnection(13);
            blockControl.Pools = global::Game.PoolsFor(blockControl, 100);
            blockControl.IncomingHitRandom = new Random(1);
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(blockControl, DamageType.Physical, 100, block: false, spell: false,
                suppress: false, out var unblocked));
            manaConn.PlayerStats[Program.Stats.Key("keystone_glancing_blows")!.Value] = 1;
            manaConn.IncomingHitRandom = new Random(1);
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(manaConn, DamageType.Physical, 100, block: true, spell: false,
                suppress: false, out var glancingBlocked));
            Assert.True(unblocked.Damage > 0);
            Assert.Equal((int)Math.Round(unblocked.Damage * 0.65), glancingBlocked.Damage);
            Assert.Equal((true, 900 - glancingBlocked.Damage, 950u, 950u),
                (glancingBlocked.Blocked, manaAegis.Current, glancingBlocked.Mana, glancingBlocked.Life));

            // A timed hit-Blind survives depletion. The Aegis owns a separate Blind instance and accuracy source.
            const uint blindedId = 504;
            var blinded = new SpawnedMonster("aegis blind", new PlayerWalk(conn.Layout.SpawnX, conn.Layout.SpawnY, 0),
                0, 0, 1000);
            const uint aegisOnlyId = 505;
            var aegisOnly = new SpawnedMonster("aegis only", new PlayerWalk(conn.Layout.SpawnX, conn.Layout.SpawnY, 0),
                0, 0, 1000);
            var blindNow = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
            {
                conn.Monsters[blindedId] = blinded;
                conn.Monsters[aegisOnlyId] = aegisOnly;
            }
            Assert.True(global::Game.InflictMonsterBlind(conn, blindedId, blinded, blindNow, (100, 0, 60)));
            global::Game.EnemyDebuffsTick("test", wire.Stream, wire.Ciphers, conn, blindNow)
                .GetAwaiter().GetResult();
            Assert.True(aegis.Blind.Blinded(blindedId));
            Assert.True(aegis.Blind.Blinded(aegisOnlyId));
            Assert.True(global::Game.Blinded(conn, blindedId));
            Assert.True(global::Game.Blinded(conn, aegisOnlyId));
            Assert.True(global::Game.MonsterBlindRating(conn, blindedId, blinded, 100, blindNow) < 100);
            Assert.True(global::Game.MonsterBlindRating(conn, aegisOnlyId, aegisOnly, 100, blindNow) < 100);

            var stats = global::Game.CharacterPlayerStats(conn);
            int attackSpeed = conn.Pools.AttackSpeedPct;
            double attackSeconds = global::Game.WeaponFor(conn).AttackSeconds(stats);
            int castMs = global::Game.CastMs(conn, fireballSkill.Effect, fireballSkill.Id)!.Value;
            int criticalChance = global::Game.SupportedCriticalChance(conn, fireballSkill.Id);
            int? vsBlindedActive = global::Game.SupportedCriticalChanceVsBlinded(conn, fireballSkill.Id, release: false);
            Assert.NotNull(vsBlindedActive);
            Assert.True(vsBlindedActive.Value > criticalChance);
            Assert.Equal(vsBlindedActive.Value, global::Game.CriticalChanceOn(conn, aegisOnlyId,
                criticalChance, vsBlindedActive));
            var blindTaken = new Dictionary<uint, int>
            {
                [Program.Stats.Key(global::Game.FromBlindedDamageTakenStat)!.Value] = 20,
            };
            Assert.Equal(20, global::Game.DamageTakenFromBlindedPct(conn, aegisOnlyId, blindTaken));

            // Final readers revalidate geometry even while the presentation membership still says blinded.
            aegisOnly.Walk.Teleport(conn.Layout.SpawnX + 1000, conn.Layout.SpawnY + 1000, blindNow);
            Assert.True(aegis.Blind.Blinded(aegisOnlyId));
            Assert.False(global::Game.PhysicalAegisBlinds(conn, aegisOnlyId));
            aegisOnly.Walk.Teleport(conn.Layout.SpawnX, conn.Layout.SpawnY, blindNow);
            Assert.True(global::Game.PhysicalAegisBlinds(conn, aegisOnlyId));
            conn.Pools = conn.Pools with { AuraAreaPct = 1000 };
            Assert.Equal(global::Game.PhysicalAegisNearbyRadius,
                global::Game.PhysicalAegisRadius(conn, aegis));
            aegisOnly.Walk.Teleport(conn.Layout.SpawnX + 55, conn.Layout.SpawnY, blindNow);
            Assert.False(global::Game.PhysicalAegisBlinds(conn, aegisOnlyId));
            aegisOnly.Walk.Teleport(conn.Layout.SpawnX, conn.Layout.SpawnY, blindNow);

            // Nonphysical hit, DoT and a fully prevented zero-damage physical hit do not consume or break the Aegis.
            var fire = DamagePacket.Of(DamageType.Fire, 40, 40);
            global::Game.DamagePlayerHitTyped(conn, 0, 40, 0, 1, stats, fire);
            Assert.Equal(1000, aegis.Current);
            double dotCarry = 0;
            IncomingResourceDefences.DamageOverTime(conn.EnergyShield, 25, 0, 1.1, ref dotCarry);
            Assert.Equal(1000, aegis.Current);
            global::Game.DamagePlayerHitTyped(conn, 0, 0, 0, 1.2, stats,
                DamagePacket.Of(DamageType.Physical, 100, 100));
            Assert.Equal(1000, aegis.Current);
            conn.EnergyShield.SetMaximum(200, refill: true);
            conn.Ward.SetMaximum(100);
            conn.Ward.Restore();

            // Mitigation precedes Aegis; its overflow then reaches Ward, and only that overflow reaches ES/Mana/Life.
            Assert.Equal(0, global::Game.DamagePlayerHitTyped(conn, 250, 0, 0, 2, stats,
                DamagePacket.Of(DamageType.Physical, 250, 250)));
            Assert.Equal((750, true, 100u, 200u, conn.PlayerLife),
                (aegis.Current, aegis.Active, conn.Ward.Current, conn.EnergyShield.Current, conn.PlayerLife));
            Assert.Equal(0, global::Game.DamagePlayerHitTyped(conn, 800, 100, 0, 3, stats,
                DamagePacket.Of(DamageType.Physical, 800, 800).Add(DamageType.Fire, new DamageRange(100, 100))));
            Assert.Equal((0, true, 0u, 150u, conn.PlayerLife),
                (aegis.Current, aegis.Depleted, conn.Ward.Current, conn.EnergyShield.Current, conn.PlayerLife));

            // The real depleted conditions reach both speed consumers and the final critical calculation immediately.
            // The cached Blind receipts are still present, but every final Blind reader revalidates depletion first.
            var depletedStats = global::Game.CharacterPlayerStats(conn);
            Assert.True(aegis.Blind.Blinded(aegisOnlyId));
            Assert.False(global::Game.Blinded(conn, aegisOnlyId));
            Assert.Equal(100, global::Game.MonsterBlindRating(conn, aegisOnlyId, aegisOnly, 100,
                conn.Clock.Elapsed.TotalSeconds));
            Assert.True(global::Game.Blinded(conn, blindedId));
            int depletedCritical = global::Game.SupportedCriticalChance(conn, fireballSkill.Id);
            Assert.Equal(depletedCritical, global::Game.CriticalChanceOn(conn, aegisOnlyId, depletedCritical,
                global::Game.SupportedCriticalChanceVsBlinded(conn, fireballSkill.Id, release: false)));
            Assert.Equal(0, global::Game.DamageTakenFromBlindedPct(conn, aegisOnlyId, blindTaken));
            global::Game.EnemyDebuffsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds)
                .GetAwaiter().GetResult();
            Assert.False(aegis.Blind.Blinded(blindedId));
            Assert.False(aegis.Blind.Blinded(aegisOnlyId));
            Assert.True(global::Game.Blinded(conn, blindedId));
            Assert.True(global::Game.MonsterBlindRating(conn, blindedId, blinded, 100,
                conn.Clock.Elapsed.TotalSeconds) < 100);
            Assert.True(conn.Pools.AttackSpeedPct > attackSpeed);
            Assert.True(global::Game.WeaponFor(conn).AttackSeconds(depletedStats) < attackSeconds);
            Assert.True(global::Game.CastMs(conn, fireballSkill.Effect, fireballSkill.Id)!.Value < castMs);
            Assert.True(depletedCritical > criticalChance);
            double conditionNow = conn.Clock.Elapsed.TotalSeconds;
            Assert.Equal(1, global::Game.ItemConditionFlags(conn, conditionNow)
                .Where(kv => kv.Key == "_while_you_have_depleted_physical_aegis").Select(kv => kv.Value ? 1 : 0).Single());
            Assert.NotNull(global::Game.PhysicalAegisPresentation(conn));
            Assert.Equal(global::Game.PhysicalAegisDepletedBuffRow, aegis.StageRow);

            // Only the Aegis clock advances below. Keep unrelated Fortification reads on the fixture's actual clock.
            using var fortificationClock = global::Game.FortificationOf(conn).Capture(conditionNow);
            // A stat/notification query at the boundary does not refill. The owner tick restores exactly at the deadline.
            double deadline = aegis.RestoresAt;
            Assert.Equal(13.0, deadline);
            global::Game.DamagePlayerHitTyped(conn, 100, 0, 0, 4, depletedStats,
                DamagePacket.Of(DamageType.Physical, 100, 100));
            Assert.Equal((0, deadline), (aegis.Current, aegis.RestoresAt));
            Assert.True(global::Game.PhysicalAegisStatus(conn, deadline).Depleted);
            Assert.False(global::Game.AdvancePhysicalAegis(conn, deadline - 0.001));
            Assert.True(global::Game.AdvancePhysicalAegis(conn, deadline));
            Assert.Equal((1000, true), (aegis.Current, aegis.Active));
            Assert.NotNull(global::Game.PhysicalAegisPresentation(conn));
            Assert.Equal(global::Game.PhysicalAegisBuffRow, aegis.StageRow);
            Assert.Equal(0, global::Game.ItemConditionFlags(conn, deadline)
                .Where(kv => kv.Key == "_while_you_have_active_physical_aegis").Select(kv => kv.Value ? 0 : 1).Single());

            // Removing the source before its deadline cannot recharge or publish the old state.
            global::Game.DamagePlayerHitTyped(conn, 100, 0, 0, 20, depletedStats,
                DamagePacket.Of(DamageType.Physical, 100, 100));
            double lostSourceDeadline = aegis.RestoresAt;
            foreach (var old in inventory.Items(PlayerInventories.Offhand1)) inventory.Remove(PlayerInventories.Offhand1, old.Id);
            Assert.False(global::Game.AdvancePhysicalAegis(conn, lostSourceDeadline));
            Assert.False(aegis.Exists);
            Assert.NotNull(global::Game.PhysicalAegisPresentation(conn));
            Assert.Equal((ushort)0, aegis.StageBuff);

            // Re-equipping creates a new source-owned full Aegis instead of restoring the old host's value.
            inventory.Add(PlayerInventories.Offhand1, 0, 0, Ghastly());
            var replacement = global::Game.PhysicalAegisStatus(conn, 22);
            Assert.Equal((true, 1000), (replacement.Exists, replacement.Current));
            Assert.NotEqual(source.Identity, replacement.Source!.Value.Identity);
            // The Blind consumer is gated by the exact Ghastly modifier, not by the Physical Aegis effect identity.
            var replacementItem = inventory.Items(PlayerInventories.Offhand1).Single();
            Assert.NotNull(inventory.Change(PlayerInventories.Offhand1, replacementItem.Id, item => item with
            {
                Mods = [.. item.Mods.Where(mod => mod.Hash != Mod(
                    "NearbyEnemiesAreBlindedPhysicalAegisUnique__1").Hash)],
            }));
            global::Game.CharacterPlayerStats(conn);
            Assert.True(global::Game.PhysicalAegisStatus(conn, 22).Active);
            Assert.False(global::Game.PhysicalAegisBlinds(conn, aegisOnlyId));

            // Requirement loss and module retirement drop the source-owned instance; restoring the source creates a
            // fresh full state rather than reviving the retired value.
            conn.Pools = conn.Pools with { Level = 1 };
            global::Game.CharacterPlayerStats(conn);
            Assert.False(global::Game.PhysicalAegisStatus(conn, 22).Exists);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            Assert.Equal((true, 1000),
                (global::Game.PhysicalAegisStatus(conn, 22).Exists, global::Game.PhysicalAegisStatus(conn, 22).Current));
            conn.RetireResourceEffects();
            Assert.False(replacement.Exists);
            Assert.Equal((true, 1000),
                (global::Game.PhysicalAegisStatus(conn, 23).Exists, global::Game.PhysicalAegisStatus(conn, 23).Current));

            // The resource owner retires on death and a live query afterwards installs the still-equipped grant at full.
            global::Game.PhysicalAegisPresentation(conn);
            conn.PlayerLife = 0;
            Assert.False(replacement.Exists);
            Assert.False(global::Game.PhysicalAegisStatus(conn, 23).Exists);
            conn.PlayerLife = 1;
            Assert.Equal((true, 1000), (global::Game.PhysicalAegisStatus(conn, 24).Exists,
                global::Game.PhysicalAegisStatus(conn, 24).Current));

            // A successful area handoff carries the remaining capacity and deadline as part of the same resource owner.
            double carryNow = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) global::Game.DamagePlayerHitTyped(conn, 300, 0, 0, carryNow, depletedStats,
                DamagePacket.Of(DamageType.Physical, 300, 300));
            var carriedState = (Current: conn.PhysicalAegis.Current, Remaining: conn.PhysicalAegis.Capture(carryNow)!.Remaining,
                Identity: conn.PhysicalAegis.Source!.Value.Identity);
            global::Game.CarryResourceEffects(conn, 2);
            var destination = NewConnection(2);
            destination.Pools = global::Game.PoolsFor(destination, 100);
            global::Game.RestoreResourceEffects(destination);
            double destinationAt = carryNow + 1;
            global::Game.ActivateResourceEffects(destination, destinationAt);
            var carried = global::Game.PhysicalAegisStatus(destination, destinationAt);
            Assert.Equal((carriedState.Current, carriedState.Identity),
                (carried.Current, carried.Source!.Value.Identity));
            Assert.Equal(carriedState.Remaining, carried.RestoresAt - destinationAt, 2);

            // A fresh relog/mode connection derives a new full Aegis from the current item-grant catalog.
            var relog = NewConnection(3);
            relog.Pools = global::Game.PoolsFor(relog, 100);
            Assert.Equal((true, 1000),
                (global::Game.PhysicalAegisStatus(relog, 0).Exists, global::Game.PhysicalAegisStatus(relog, 0).Current));
            var ruthless = NewConnection(4, hardMode: true);
            ruthless.Pools = global::Game.PoolsFor(ruthless, 100);
            var ruthlessAegis = global::Game.PhysicalAegisStatus(ruthless, 0);
            Assert.Equal((true, true), (ruthlessAegis.Exists, ruthlessAegis.Source!.Value.Ruthless));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    void Doppelganger_guise_owns_its_toggle_and_final_consumers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-unhinged-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string character = "Unhinged" + Guid.NewGuid().ToString("N");
        ClientState NewConnection(uint ticket, string? name = null)
        {
            name ??= character;
            var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), ticket, 1)
            {
                Character = name, PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true, Instance = new(),
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                    new HashSet<string>(["Game"])),
            };
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            return conn;
        }
        try
        {
            using var wire = new Wire();
            var conn = NewConnection(31);
            var items = (ItemCatalog)global::Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            Item Guise() => new UniqueCatalog(items).Create("Doppelgänger Guise", new Random(504))!;
            var guise = Guise();
            foreach (var old in inventory.Items(PlayerInventories.BodyArmour))
                inventory.Remove(PlayerInventories.BodyArmour, old.Id);
            inventory.Add(PlayerInventories.BodyArmour, 0, 0, guise);
            conn.Pools = global::Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            conn.EnergyShield.SetMaximum(0, refill: true);
            conn.Ward.SetMaximum(0);

            var raw = Assert.Single(global::Game.AllWornGrantedSkillsOf(conn), g => g.EffectId == "Unhinge");
            Assert.Equal((69315, 11721, 20, true, false),
                (raw.EffectsPerLevelRow, raw.Effect, raw.Level, raw.ManuallyCast, raw.Support));
            var actor = Assert.Single(global::Game.GemSkillEffects(conn), g => g.Effect == raw.Effect);
            Assert.Equal(20, actor.Level);
            Assert.NotNull(SingleUseCooldown.ForSkill(global::Game.SkillsFor(conn), Program.Stats,
                raw.Effect, raw.Level, global::Game.CharacterPlayerStats(conn),
                global::Game.SupportedSkill(conn, actor.Id).Stats));
            Assert.False(global::Game.Unhinged(conn));
            Assert.True(global::Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)["_while_not_unhinged"]);

            // Actual actor admission: the first cast toggles, the next stay refused until 5000 ms, then the exact
            // boundary admits the ticket and CastGemSkill executes the toggle.
            SingleUseCooldown? firstUsePlan = null;
            bool CastToggle(ClientState target, bool expected)
            {
                var current = global::Game.GemSkillEffects(target).Single(g => g.Effect == raw.Effect);
                var plan = global::Game.CooldownFor(target, current.Effect, current.Level, current.Quality);
                Assert.NotNull(plan);
                CooldownUseTicket? ticket = null;
                bool admitted = global::Game.TryBeginSkillCooldownUse(target, current.Id, current.Effect, plan,
                    instant: true, () => { }, out ticket);
                Assert.Equal(expected, admitted);
                if (!admitted) return false;
                firstUsePlan ??= plan;
                Assert.True(global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, target, current.Id,
                    (0, 0), (0, 0), cooldownUse: ticket).GetAwaiter().GetResult());
                return true;
            }
            Assert.True(CastToggle(conn, expected: true));
            double readyAt = conn.SkillReadyAt[raw.Effect];
            Assert.NotNull(firstUsePlan);
            Assert.Equal(5000, firstUsePlan.Value.BaseMilliseconds);
            Assert.True(firstUsePlan.Value.RecoveryRate > 0);
            Assert.True(global::Game.Unhinged(conn));
            Assert.NotEqual((ushort)0, conn.Unhinged.Buff);
            Assert.False(CastToggle(conn, expected: false));
            lock (conn.Monsters)
            {
                var cooldown = conn.SingleUseCooldowns[raw.Effect];
                Assert.Equal(firstUsePlan.Value.RecoveryRate, cooldown.Rate);
                cooldown.Refresh(readyAt - .001, firstUsePlan.Value.RecoveryRate);
                Assert.InRange(cooldown.RemainingBaseMilliseconds, .999, 1.001);
                Assert.False(cooldown.Ready);
            }
            Assert.False(CastToggle(conn, expected: false));
            lock (conn.Monsters)
            {
                conn.SingleUseCooldowns[raw.Effect].Refresh(readyAt, firstUsePlan.Value.RecoveryRate);
                Assert.True(conn.SingleUseCooldowns[raw.Effect].Ready);
            }
            Assert.True(CastToggle(conn, expected: true));
            Assert.False(global::Game.Unhinged(conn));
            Assert.True(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, actor.Id, actor.Effect,
                () => true).GetAwaiter().GetResult());
            var unhinged = global::Game.CharacterPlayerStats(conn);
            int Crit(string name) => Program.Stats.Key(name) is uint key ? unhinged.GetValueOrDefault(key) : 0;
            Assert.InRange(Crit(global::Game.UnhingedCriticalFinal), 40, 60);
            Assert.Equal(0, Crit(global::Game.SaneDamageFinal));
            int expectedFinal = CriticalChanceFinal.Project(500, 50, Crit, null);
            Assert.True(expectedFinal > CriticalChanceFinal.Project(500, 50, _ => 0, null));
            Assert.Equal(expectedFinal, AttackBaseCriticalStrike.ChanceOfBase(500, 50, Crit));
            Assert.Equal(CriticalChanceFinal.Project(500, 0, Crit, null), SpellCriticalStrike.Chance(500, Crit));

            Assert.Equal(60, global::Game.ApplyUnhingedTaken(100, DamageType.Physical,
                name => name == global::Game.SaneDamageFinal ? -40 : 0));
            Assert.Equal(100, global::Game.ApplyUnhingedTaken(100, DamageType.Fire,
                name => name == global::Game.SaneDamageFinal ? -40 : 0));
            double carry = 0;
            int dot = IncomingResourceDefences.TakenOverTime(100, _ => 0, ref carry, finalTakenPct: -40);
            Assert.Equal(60, dot);

            Assert.True(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, actor.Id, actor.Effect,
                () => true).GetAwaiter().GetResult());
            Assert.False(global::Game.Unhinged(conn));
            var sane = global::Game.CharacterPlayerStats(conn);
            int Sane(string name) => Program.Stats.Key(name) is uint key ? sane.GetValueOrDefault(key) : 0;
            Assert.InRange(Sane(global::Game.SaneDamageFinal), -40, -30);
            Assert.Equal(0, Sane(global::Game.UnhingedCriticalFinal));
            int physicalTaken = global::Game.DamagePlayerHitTyped(conn, 100, 0, 0, 1, sane,
                DamagePacket.Of(DamageType.Physical, 100, 100));
            int fireTaken = global::Game.DamagePlayerHitTyped(conn, 0, 100, 0, 1, sane,
                DamagePacket.Of(DamageType.Fire, 100, 100));
            int chaosTaken = global::Game.DamagePlayerHitTyped(conn, 0, 0, 100, 1, sane,
                DamagePacket.Of(DamageType.Chaos, 100, 100));
            Assert.InRange((physicalTaken, chaosTaken), (60, 60), (70, 70));
            Assert.Equal(100, fireTaken);

            // An accepted hit starts 10% maximum Life over one second even when Energy Shield absorbs all damage.
            void RunResourceTick(ClientState target, double from)
            {
                PlayerUpdatesState? updates = null;
                var schedule = new OneStepSchedule(from, () => updates!.Stop.Cancel());
                updates = new PlayerUpdatesState(lifetime, schedule);
                target.PlayerUpdatesState = updates;
                Assert.True(target.PlayerUpdatesWorker.TryStart(target, lifetime, null, updates, out long generation));
                updates.Generation = generation;
                try
                {
                    global::Game.RunResourceRecovery("test", wire.Stream, wire.Ciphers, target, updates)
                        .GetAwaiter().GetResult();
                    Assert.True(target.PlayerUpdatesWorker.Current(lifetime, null, updates, generation));
                }
                finally
                {
                    target.PlayerUpdatesWorker.Complete(lifetime, null, updates, generation);
                    target.RetirePlayerUpdates();
                }
            }
            conn.EnergyShield.SetMaximum(200, refill: true);
            uint lowLife = conn.PlayerMaxLife / 2;
            conn.PlayerLife = lowLife - 100;
            conn.IncomingHitRandom = new Random(1);
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(conn, DamageType.Physical, 20, block: false, spell: false,
                suppress: false, out var absorbed));
            Assert.True(absorbed.Damage > 0);
            Assert.Equal(lowLife - 100, conn.PlayerLife);
            Assert.True(conn.EnergyShield.Current < conn.EnergyShield.Maximum);
            double hitAt = conn.Clock.Elapsed.TotalSeconds;
            double expectedRate = conn.Unhinged.HitRecovery.Rate(hitAt);
            Assert.InRange(expectedRate, conn.PlayerMaxLife * .09, conn.PlayerMaxLife * .12);
            conn.PetrifiedBlood.Activate(actor.Id, actor.Effect, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 0,
                manaPercent: 0, lowLifeThresholdPct: 50, conn.NextBuff++, hitAt);
            RunResourceTick(conn, hitAt);
            Assert.Equal(lowLife, conn.PlayerLife);
            Assert.True(conn.PetrifiedBlood.Active);

            // Reserved Life is the real recovery cap, not merely a panel value.
            conn.PetrifiedBlood.Clear();
            conn.EnergyShield.SetMaximum(0, refill: true);
            conn.PlayerStats[Program.Stats.Key(LifeReservation.ItemPercentStat)!.Value] = 60;
            uint reservedCap = (uint)((long)conn.PlayerMaxLife * 40 / 100);
            conn.PlayerLife = reservedCap - 10;
            Assert.Equal(reservedCap, global::Game.LifeCap(conn));
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(conn, DamageType.Physical, 1, block: false, spell: false,
                suppress: false, out _));
            hitAt = conn.Clock.Elapsed.TotalSeconds;
            RunResourceTick(conn, hitAt);
            Assert.Equal(reservedCap, conn.PlayerLife);

            // Zealot's Oath converts the same one-second recovery into Energy Shield through the production tick.
            conn.PlayerStats.Remove(Program.Stats.Key(LifeReservation.ItemPercentStat)!.Value);
            conn.PlayerStats[Program.Stats.Key("keystone_zealots_oath")!.Value] = 1;
            conn.PlayerLife = 500;
            conn.EnergyShield.SetMaximum(200, refill: false);
            Assert.Equal(0u, conn.EnergyShield.Current);
            Assert.True(ConditionalSourceTestDrivers.TryDevEnemyHit(conn, DamageType.Physical, 1, block: false, spell: false,
                suppress: false, out _));
            hitAt = conn.Clock.Elapsed.TotalSeconds;
            double oathRate = conn.Unhinged.HitRecovery.Rate(hitAt);
            Assert.InRange(oathRate, conn.PlayerMaxLife * .09, conn.PlayerMaxLife * .12);
            RunResourceTick(conn, hitAt);
            Assert.Equal(500u, conn.PlayerLife);
            Assert.InRange(conn.EnergyShield.Current, (uint)Math.Floor(oathRate), (uint)Math.Ceiling(oathRate));
            conn.PlayerStats.Remove(Program.Stats.Key("keystone_zealots_oath")!.Value);
            conn.EnergyShield.SetMaximum(0, refill: true);

            // The boolean destroys only the accepted killer's hit corpse without inventing secondary damage. The
            // production hit mutation preserves the decision even when the toggle changes before kill processing.
            Assert.True(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, actor.Id, actor.Effect,
                () => true).GetAwaiter().GetResult());
            Assert.True(global::Game.Unhinged(conn));
            var monsterCatalog = new MonsterCatalog();
            var variety = monsterCatalog.All.First(v => v.Supported);
            uint nextCorpse = 900;
            (uint Id, SpawnedMonster Monster) Corpse(bool dead = false)
            {
                uint id = nextCorpse++;
                var corpse = new SpawnedMonster($"unhinged corpse {nextCorpse}",
                    new(conn.Layout.SpawnX, conn.Layout.SpawnY, 0), 0, 0, 100)
                {
                    Hostile = true, Dead = dead, Life = dead ? 0u : 100u, Variety = variety, NoRewards = true,
                };
                conn.Monsters[id] = corpse;
                return (id, corpse);
            }
            bool Toggle()
            {
                Assert.True(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, actor.Id, actor.Effect,
                    () => true).GetAwaiter().GetResult());
                return global::Game.Unhinged(conn);
            }
            bool HitKill(uint id, SpawnedMonster corpse, bool toggleBeforeKill = false)
            {
                MonsterHit hit;
                lock (conn.Monsters)
                {
                    hit = global::Game.ApplyMonsterHitLocked(conn, corpse, 100)!.Value;
                    Assert.Equal(0u, hit.Life);
                    Assert.True(corpse.Dead);
                }
                if (toggleBeforeKill) Toggle();
                global::Game.MonsterKilled("test", wire.Stream, wire.Ciphers, conn, id, corpse)
                    .GetAwaiter().GetResult();
                return corpse.CorpseConsumed;
            }
            var (hitId, hitCorpse) = Corpse();
            Assert.True(HitKill(hitId, hitCorpse));
            Assert.True(global::Game.Unhinged(conn));
            var (dotId, dotCorpse) = Corpse(dead: true);
            global::Game.DotKilled("test", wire.Stream, wire.Ciphers, conn, dotId, dotCorpse)
                .GetAwaiter().GetResult();
            Assert.False(dotCorpse.CorpseConsumed);

            // Lethal hit while insane accepts destruction; turning sane before MonsterKilled cannot revoke it.
            Assert.True(global::Game.Unhinged(conn));
            var (acceptedInsaneId, acceptedInsaneCorpse) = Corpse();
            Assert.True(HitKill(acceptedInsaneId, acceptedInsaneCorpse, toggleBeforeKill: true));
            Assert.False(global::Game.Unhinged(conn));

            // Lethal hit while sane refuses destruction; turning insane before MonsterKilled cannot grant it.
            Assert.False(global::Game.Unhinged(conn));
            var (acceptedSaneId, acceptedSaneCorpse) = Corpse();
            Assert.False(HitKill(acceptedSaneId, acceptedSaneCorpse, toggleBeforeKill: true));
            Assert.True(global::Game.Unhinged(conn));

            // Buff row 1723 cancels on death; resurrection installs the still-equipped source sane.
            conn.PlayerLife = 0;
            Assert.False(global::Game.Unhinged(conn));
            conn.PlayerLife = 1;
            Assert.False(global::Game.Unhinged(conn));
            Assert.True(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, actor.Id, actor.Effect,
                () => true).GetAwaiter().GetResult());
            Assert.True(global::Game.Unhinged(conn));

            // Removal invalidates the old Actor identity; re-equip installs a fresh sane source.
            foreach (var old in inventory.Items(PlayerInventories.BodyArmour))
                inventory.Remove(PlayerInventories.BodyArmour, old.Id);
            Assert.False(global::Game.Unhinged(conn));
            Assert.False(conn.Unhinged.Exists);
            Assert.False(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, actor.Id, actor.Effect,
                () => true).GetAwaiter().GetResult());
            Assert.NotNull(global::Game.UnhingedPresentation(conn));
            inventory.Add(PlayerInventories.BodyArmour, 0, 0, Guise());
            var replacementActor = Assert.Single(global::Game.GemSkillEffects(conn), g => g.Effect == raw.Effect);
            Assert.False(global::Game.Unhinged(conn));
            Assert.True(global::Game.ToggleUnhinged("test", wire.Stream, wire.Ciphers, conn, replacementActor.Id,
                replacementActor.Effect, () => true).GetAwaiter().GetResult());
            conn.RetireResourceEffects();
            Assert.False(conn.Unhinged.Exists);
            Assert.False(global::Game.Unhinged(conn));
            var relog = NewConnection(32, character);
            relog.Pools = global::Game.PoolsFor(relog, 100);
            Assert.False(global::Game.Unhinged(relog));
            var fresh = NewConnection(33, "Fresh" + Guid.NewGuid().ToString("N"));
            fresh.Pools = global::Game.PoolsFor(fresh, 100);
            Assert.False(global::Game.Unhinged(fresh));
            Assert.False(fresh.Unhinged.Exists);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    void Scorpion_call_uses_the_real_shared_stance_owner()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"localexile-scorpion-{Guid.NewGuid():N}.db");
        Saves.Open(savePath);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string character = "Scorpion" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), 1, 1)
            {
                Character = character, PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true,
                Instance = new(), Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                    new HashSet<string>(["Game"])),
            };
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;

            var items = (ItemCatalog)global::Game.ItemsFor(conn)!;
            var inventory = (PlayerInventories)global::Game.ItemsOf(conn)!;
            var uniques = new UniqueCatalog(items);
            var arrowGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemBurningArrow")!);
            var meleeGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemGroundSlam")!);
            var heavyGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemHeavyStrike")!);
            var spellGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var determinationGem = ItemCatalog.Create(
                items.Find("Metadata/Items/Gems/SkillGemDetermination")!);
            var stanceGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemBloodAndSand")!);
            var fleshGem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFleshAndStone")!);
            var bow = ItemCatalog.Create(items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!)! with
            {
                Sockets = [new ItemSocket(SocketColour.White, arrowGem),
                    new ItemSocket(SocketColour.White, determinationGem)], Links = [1, 1],
            };
            var scorpion = uniques.Create("Scorpion's Call", new Random(505))!;
            var helmet = ItemCatalog.Create(items.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, stanceGem),
                    new ItemSocket(SocketColour.White, meleeGem)], Links = [1, 1],
            };
            foreach (byte row in new[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Helmet })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, bow);
            inventory.Add(PlayerInventories.Offhand1, 0, 0, scorpion);
            inventory.Add(PlayerInventories.Helmet, 0, 0, helmet);
            global::Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = global::Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            using var wire = new Wire();

            string ModStat(string id) => Assert.Single(Mod(id).Stats).Id;
            string projectile = ModStat("ProjectileDamageBloodStanceUnique__1");
            string regeneration = ModStat("LifeRegenerationBloodStanceUnique__1");
            string area = ModStat("AreaOfEffectSandStanceUnique__1");
            var evasionMod = Assert.Single(Mod("EvasionRatingSandStanceUnique__1").Stats);
            string evasion = evasionMod.Id;
            string attackSpeed = ModStat("AttackSpeedChangedStanceUnique__1");
            Assert.Contains("blood", projectile, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sand", area, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stance", attackSpeed, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(evasionMod.Min, 700, 1000);
            Assert.InRange(evasionMod.Max, 700, 1000);

            var arrow = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "BurningArrow");
            var melee = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "GroundSlam");
            var stance = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == StanceCatalog.BloodAndSand);
            var determination = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "Determination");
            var determinationCost = global::Game.SupportedReservationCost(conn, determination.Id)!.Value;
            Assert.Equal(5000, determinationCost.ManaPercent);
            Dictionary<uint, int> Stats() => global::Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            double Regen() => PanelStats.LifePerMinute(name => Program.Stats.Key(name) is uint key
                ? Stats().GetValueOrDefault(key) : 0, conn.Pools.Life);
            double Projectile() => global::Game.SupportedProjectileHit(conn, arrow.Id, area: false).MinTotal;
            double MeleeHit() => global::Game.SupportedHit(conn, melee.Id).MinTotal;
            int MeleeRadius() => global::Game.SkillRadius(conn,
                global::Game.SupportedSkill(conn, melee.Id).Stats, spell: false, melee.Effect);

            // Blood is the no-gem/default state, and the item's Blood consumers reach the real pipelines.
            int baseEvasion = conn.Pools.Evasion;
            double bloodRegen = Regen();
            double bloodProjectile = Projectile();
            double implicitBloodHit = MeleeHit();
            int bloodRadius = MeleeRadius();
            int bloodAttackSpeed = conn.Pools.AttackSpeedPct;
            Assert.True(bloodRegen >= 9000);
            Assert.True(bloodProjectile > 0);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance, conn.Clock.Elapsed.TotalSeconds));

            double? stanceAcceptedAt = null;
            CooldownUseTicket? lastStanceUse = null;
            bool CastStance(ClientState target, ushort skillId, bool expectAdmission = true)
            {
                var supported = global::Game.SupportedSkill(target, skillId);
                var cooldown = global::Game.CooldownFor(target, supported.Effect, supported.Level, supported.Quality);
                CooldownUseTicket? ticket = null;
                bool admitted = global::Game.TryBeginSkillCooldownUse(target, skillId, supported.Effect, cooldown,
                    instant: true, () => { }, out ticket);
                if (!expectAdmission) return admitted;
                Assert.True(admitted);
                lastStanceUse = ticket;
                stanceAcceptedAt = ticket!.Stance?.AcceptedAt;
                return global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, target, skillId, (0, 0), (0, 0),
                    cooldownUse: ticket).GetAwaiter().GetResult();
            }
            Task ExtraAuraInstantUse(ClientState target) => global::Game.UseInstantSkill("test", wire.Stream,
                wire.Ciphers, target, determination.Id, 0x1000,
                target.Walk.Position(target.Clock.Elapsed.TotalSeconds));
            uint nextPeer = 4;
            ClientState NewStanceConnection(string name, params ItemSocket[] sockets)
            {
                var target = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), nextPeer++, 1)
                {
                    Character = name, PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true,
                    Instance = new(), Experience = CharacterStats.ExperienceFor(100),
                    Pools = new(100, 1000, 1000, int.MaxValue, 0,
                        Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                        new HashSet<string>(["Game"])),
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    target.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                var targetItems = (ItemCatalog)global::Game.ItemsFor(target)!;
                var targetInventory = (PlayerInventories)global::Game.ItemsOf(target)!;
                foreach (var old in targetInventory.Items(PlayerInventories.Helmet))
                    targetInventory.Remove(PlayerInventories.Helmet, old.Id);
                targetInventory.Add(PlayerInventories.Helmet, 0, 0,
                    ItemCatalog.Create(targetItems.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
                    {
                        Sockets = [.. sockets],
                        Links = [.. sockets.Select(_ => (byte)1)],
                    });
                global::Game.EquipmentStatSources(target, targetInventory.EquipmentSnapshot());
                target.Pools = global::Game.PoolsFor(target, 100);
                target.PlayerLife = target.Pools.Life;
                target.PlayerMana = target.Pools.Mana;
                return target;
            }
            ClientState RecreatedStanceConnection(ClientState original)
            {
                var target = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), nextPeer++, 1)
                {
                    Character = original.Character, PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true,
                    Instance = new(), Experience = original.Experience,
                    Pools = new(100, 1000, 1000, int.MaxValue, 0,
                        Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = original.LeagueModule,
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    target.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                global::Game.EquipmentStatSources(target,
                    ((PlayerInventories)global::Game.ItemsOf(target)!).EquipmentSnapshot());
                target.Pools = global::Game.PoolsFor(target, 100);
                target.PlayerLife = target.Pools.Life;
                target.PlayerMana = target.Pools.Mana;
                return target;
            }

            // Refused first activation spends neither cooldown nor action, reserves nothing and records nothing.
            int actions = conn.ActionCount;
            conn.PlayerMana = 0;
            Assert.False(CastStance(conn, stance.Id, expectAdmission: false));
            Assert.Equal(actions, conn.ActionCount);
            Assert.Empty(conn.SingleUseCooldowns);
            Assert.DoesNotContain(stance.Effect, conn.SkillReadyAt);
            Assert.Empty(conn.Auras);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance, conn.Clock.Elapsed.TotalSeconds));
            conn.PlayerMana = conn.Pools.Mana;

            Assert.True(CastStance(conn, stance.Id));
            var owner = Assert.Single(conn.Auras.Values);
            Assert.Equal(StanceCatalog.Blood, owner.EffectId);
            var firstStanceUse = lastStanceUse!;
            Assert.True(firstStanceUse.Stance!.Current(conn));
            var firstStanceOwner = owner.StanceOwner;
            Assert.NotNull(firstStanceOwner);
            double activeBloodHit = MeleeHit();
            int activeBloodRadius = MeleeRadius();
            uint reserved = (uint)global::Game.ReservedMana(conn);
            Assert.True(reserved > 0);
            Assert.Equal(conn.Pools.Mana - reserved, conn.PlayerMana);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance, conn.Clock.Elapsed.TotalSeconds));
            Assert.Contains(StanceCatalog.Blood, CharacterSaves.Load(character)!.ActiveAuras!);

            // A published instance is bound to this exact committed aura, not just the socketed wire ID.
            conn.ActionCount++;
            Assert.False(firstStanceUse.Stance.Current(conn));
            conn.ActionCount--;
            conn.PlayerLife = 0;
            Assert.False(firstStanceUse.Stance.Current(conn));
            conn.PlayerLife = conn.Pools.Life;
            Assert.True(firstStanceUse.Stance.Current(conn));

            // A repeated native use is refused by the shared cooldown and cannot claim a stance change.
            Assert.False(global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, stance.Id,
                (0, 0), (0, 0)).GetAwaiter().GetResult());
            Assert.Equal(StanceCatalog.Blood, Assert.Single(conn.Auras.Values).EffectId);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance, conn.Clock.Elapsed.TotalSeconds));

            // Determination's real 50% reservation makes the total exceed the remaining mana. Switching the existing
            // 80% stance still spends nothing because its additional cost is zero.
            ExtraAuraInstantUse(conn).GetAwaiter().GetResult();
            var extraAura = Assert.Single(conn.Auras.Values, a => a.SkillId == determination.Id);
            Assert.Equal("Determination", extraAura.EffectId);
            Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId));
            Assert.DoesNotContain(conn.Auras.Values, a => a.SkillId == determination.Id && StanceCatalog.IsStance(a.EffectId));
            var beforeRefresh = Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId));
            global::Game.SendSkills("test", wire.Stream, wire.Ciphers, conn).GetAwaiter().GetResult();
            var refreshedOwner = Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId));
            Assert.NotSame(beforeRefresh, refreshedOwner);
            Assert.Same(firstStanceOwner, refreshedOwner.StanceOwner);
            Assert.True(firstStanceUse.Stance.Current(conn));
            uint reservedWithOther = (uint)global::Game.ReservedMana(conn);
            Assert.True(reservedWithOther > conn.PlayerMana);

            // The same source now alternates stance without reserving again or toggling the skill off.
            lock (conn.Monsters) { conn.SingleUseCooldowns.Clear(); conn.SkillReadyAt.Clear(); }
            conn.RecentEvents.Clear();
            uint manaBeforeSwitch = conn.PlayerMana;
            Assert.True(CastStance(conn, stance.Id));
            owner = Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId));
            Assert.Equal(StanceCatalog.Sand, owner.EffectId);
            Assert.Equal(reservedWithOther, (uint)global::Game.ReservedMana(conn));
            Assert.Equal(manaBeforeSwitch, conn.PlayerMana);
            Assert.False(firstStanceUse.Stance.Current(conn));
            Assert.NotSame(firstStanceOwner, owner.StanceOwner);
            double changedExpiry = stanceAcceptedAt!.Value + 4;
            Assert.True(conn.RecentEvents.Happened(StatConditions.ChangedStance, changedExpiry - .001));
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance, changedExpiry));

            double sandRegen = Regen();
            double sandProjectile = Projectile();
            double sandHit = MeleeHit();
            int sandRadius = MeleeRadius();
            Assert.InRange(bloodRegen - sandRegen, 9000, 12000);
            Assert.InRange(bloodProjectile / sandProjectile, 1.40, 1.60);
            Assert.True(conn.Pools.Evasion > baseEvasion);
            Assert.InRange(activeBloodHit / implicitBloodHit, 1.09, 1.11);
            Assert.InRange(sandHit / activeBloodHit, .85, .87);
            Assert.True(activeBloodRadius < bloodRadius);
            Assert.True(sandRadius > bloodRadius);
            Assert.InRange(conn.Pools.AttackSpeedPct - bloodAttackSpeed, 20, 30);
            Assert.DoesNotContain(global::Game.SupportedSkill(conn, arrow.Id).Stats.Keys,
                key => (Program.Stats.Name(key) ?? "").Contains("blood", StringComparison.Ordinal)
                    && (Program.Stats.Name(key) ?? "").EndsWith("_final", StringComparison.Ordinal));
            uint manaBeforeExtraAuraOff = conn.PlayerMana;
            ExtraAuraInstantUse(conn).GetAwaiter().GetResult();
            Assert.DoesNotContain(conn.Auras.Values, a => a.SkillId == determination.Id);
            Assert.Equal(reserved, (uint)global::Game.ReservedMana(conn));
            Assert.Equal(manaBeforeExtraAuraOff, conn.PlayerMana);

            // The production recovery loop observes the same regeneration consumer, not only a folded dictionary.
            int RecoverOnce(ClientState target)
            {
                target.PlayerLife = Math.Max(1, target.Pools.Life - 100);
                uint beforeTick = target.PlayerLife;
                PlayerUpdatesState updates = null!;
                updates = new PlayerUpdatesState(lifetime, new OneStepSchedule(target.Clock.Elapsed.TotalSeconds,
                    () => updates.Stop.Cancel()));
                target.PlayerUpdatesState = updates;
                Assert.True(target.PlayerUpdatesWorker.TryStart(target, lifetime, null, updates, out long generation));
                updates.Generation = generation;
                try
                {
                    global::Game.RunResourceRecovery("test", wire.Stream, wire.Ciphers, target, updates)
                        .GetAwaiter().GetResult();
                }
                finally
                {
                    target.PlayerUpdatesWorker.Complete(lifetime, null, updates, generation);
                    target.RetirePlayerUpdates();
                }
                return target.PlayerLife <= beforeTick ? 0 : 1;
            }
            Assert.Equal(0, RecoverOnce(conn));
            lock (conn.Monsters) { conn.SingleUseCooldowns.Clear(); conn.SkillReadyAt.Clear(); }
            Assert.True(CastStance(conn, stance.Id));
            Assert.Equal(StanceCatalog.Blood,
                Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId)).EffectId);
            Assert.Equal(1, RecoverOnce(conn));

            // Source removal releases the reservation and returns to implicit Blood; the old identity cannot revive.
            conn.RecentEvents.Clear();
            ushort oldStanceId = stance.Id;
            var oldUse = lastStanceUse!;
            var oldSource = oldUse.Stance!.Source;
            var oldAura = oldUse.Stance.Aura;
            Assert.Equal(oldSource.Identity, oldUse.Stance.Source.Identity);
            Assert.Same(oldAura.StanceOwner, conn.Auras[oldStanceId].StanceOwner);
            Assert.True(oldUse.Stance.Current(conn));
            inventory.Remove(PlayerInventories.Helmet, inventory.Items(PlayerInventories.Helmet).Single().Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.Empty(conn.Auras);
            Assert.False(oldUse.Stance.Current(conn));
            Assert.False(global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, oldStanceId,
                (0, 0), (0, 0)).GetAwaiter().GetResult());
            Assert.Equal(0, global::Game.ReservedMana(conn));
            Assert.DoesNotContain(StanceCatalog.Blood,
                CharacterSaves.Load(character)!.ActiveAuras ?? []);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance, conn.Clock.Elapsed.TotalSeconds));
            Assert.True(Regen() >= 9000);

            inventory.Add(PlayerInventories.Helmet, 0, 0, helmet);
            var replacement = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == StanceCatalog.BloodAndSand);
            var replacementSource = global::Game.CurrentStanceSourceLocked(conn, replacement.Id, replacement.Effect);
            Assert.NotNull(replacementSource);
            Assert.Equal(replacement.Id, replacementSource.Source.Id);
            Assert.False(oldUse.Stance.Current(conn));
            Assert.Empty(conn.Auras);
            Assert.True(Regen() >= 9000);

            // A real mace setup accepts Ground Slam through the production cast entry; the stance output still reaches
            // that legal melee-area consumer while non-area melee and spell/projectile skills remain isolated.
            var maceBase = items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/OneHandMace3");
            Assert.NotNull(maceBase);
            var mace = ItemCatalog.Create(maceBase!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, meleeGem),
                    new ItemSocket(SocketColour.White, heavyGem),
                    new ItemSocket(SocketColour.White, spellGem),
                    new ItemSocket(SocketColour.White, fleshGem)], Links = [1, 1, 1, 1],
            };
            var stanceHelmet = helmet with
            {
                Sockets = [new ItemSocket(SocketColour.White, stanceGem)], Links = [1],
            };
            inventory.Remove(PlayerInventories.Weapon1, inventory.Items(PlayerInventories.Weapon1).Single().Id);
            inventory.Remove(PlayerInventories.Offhand1, inventory.Items(PlayerInventories.Offhand1).Single().Id);
            inventory.Remove(PlayerInventories.Helmet, inventory.Items(PlayerInventories.Helmet).Single().Id);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, mace);
            inventory.Add(PlayerInventories.Helmet, 0, 0, stanceHelmet);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.Empty(conn.Auras);
            lock (conn.Monsters) { conn.SingleUseCooldowns.Clear(); conn.SkillReadyAt.Clear(); }
            Assert.True(CastStance(conn, replacement.Id));
            double bloodAndSandOnlyHit = MeleeHit();
            var flesh = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == StanceCatalog.FleshAndStone);
            conn.RecentEvents.Clear();
            uint beforeJoinMana = conn.PlayerMana;
            uint beforeJoinReserved = (uint)global::Game.ReservedMana(conn);
            Assert.True(CastStance(conn, flesh.Id));
            var joinedOwners = conn.Auras.Values.Where(a => StanceCatalog.IsStance(a.EffectId)).ToArray();
            Assert.Equal(2, joinedOwners.Length);
            var joinedBloodAndSand = Assert.Single(joinedOwners,
                a => StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.BloodAndSand);
            var joinedFlesh = Assert.Single(joinedOwners,
                a => StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.FleshAndStone);
            Assert.Equal(StanceCatalog.Blood, joinedBloodAndSand.EffectId);
            Assert.Equal(StanceCatalog.FleshAndStone, joinedFlesh.EffectId);
            Assert.Equal(StanceCatalog.BloodSelection, joinedBloodAndSand.Stance);
            Assert.Equal(StanceCatalog.BloodSelection, joinedFlesh.Stance);
            Assert.NotNull(joinedBloodAndSand.StanceOwner);
            Assert.NotNull(joinedFlesh.StanceOwner);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance,
                conn.Clock.Elapsed.TotalSeconds));
            uint joinedReserved = (uint)global::Game.ReservedMana(conn);
            Assert.Equal(joinedReserved - beforeJoinReserved, beforeJoinMana - conn.PlayerMana);

            var beforeSharedSwitch = joinedOwners.ToDictionary(a => a.SkillId, a => a.StanceOwner);
            lock (conn.Monsters) { conn.SingleUseCooldowns.Clear(); conn.SkillReadyAt.Clear(); }
            conn.RecentEvents.Clear();
            uint sharedMana = conn.PlayerMana;
            Assert.True(CastStance(conn, replacement.Id));
            joinedOwners = conn.Auras.Values.Where(a => StanceCatalog.IsStance(a.EffectId)).ToArray();
            Assert.All(joinedOwners, a => Assert.Equal(StanceCatalog.SandSelection, a.Stance));
            Assert.All(joinedOwners, a => Assert.NotSame(beforeSharedSwitch[a.SkillId], a.StanceOwner));
            Assert.Equal(joinedReserved, (uint)global::Game.ReservedMana(conn));
            Assert.Equal(sharedMana, conn.PlayerMana);
            Assert.True(conn.RecentEvents.Happened(StatConditions.ChangedStance,
                conn.Clock.Elapsed.TotalSeconds));

            lock (conn.Monsters) { conn.SingleUseCooldowns.Clear(); conn.SkillReadyAt.Clear(); }
            conn.RecentEvents.Clear();
            sharedMana = conn.PlayerMana;
            Assert.True(CastStance(conn, flesh.Id));
            joinedOwners = conn.Auras.Values.Where(a => StanceCatalog.IsStance(a.EffectId)).ToArray();
            Assert.All(joinedOwners, a => Assert.Equal(StanceCatalog.BloodSelection, a.Stance));
            Assert.Equal(joinedReserved, (uint)global::Game.ReservedMana(conn));
            Assert.Equal(sharedMana, conn.PlayerMana);
            Assert.True(conn.RecentEvents.Happened(StatConditions.ChangedStance,
                conn.Clock.Elapsed.TotalSeconds));
            Assert.Equal(bloodAndSandOnlyHit, MeleeHit());

            var legalMelee = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "GroundSlam");
            var nonAreaMelee = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "HeavyStrike");
            var spell = Assert.Single(global::Game.GemSkillEffects(conn), g =>
                global::Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball");
            Assert.Null(global::Game.GemSkillInstanceDisabled(conn, legalMelee.Effect, legalMelee.Id));
            Assert.True(global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, legalMelee.Id,
                (0, 0), (1, 0)).GetAwaiter().GetResult());
            Dictionary<uint, int> stanceOwn = [];
            global::Game.ApplyBloodAndSandMeleeStats(conn, legalMelee.Effect, stanceOwn);
            Assert.NotEmpty(stanceOwn);
            var stanceOwner = Assert.Single(conn.Auras.Values,
                a => StanceCatalog.SourceFamily(a.EffectId) == StanceCatalog.BloodAndSand);
            var stanceSource = global::Game.SupportedSkill(conn, stanceOwner.SkillId);
            int SourceValue(string stat) => Program.Stats.Key(stat) is uint key
                ? stanceSource.Stats.GetValueOrDefault(key) : 0;
            int expectedMore = (int)Math.Round((SourceValue(
                    "blood_sand_stance_melee_skills_area_of_effect_+%_final")
                + SourceValue("blood_sand_stance_melee_skills_area_of_effect_+%_final_in_blood_stance"))
                * Math.Max(0, 1 + SourceValue("blood_sand_stance_buff_effect_+%") / 100.0),
                MidpointRounding.AwayFromZero);
            uint moreKey = Program.Stats.Key("blood_sand_stance_melee_skills_area_of_effect_+%_final")!.Value;
            Assert.Equal(expectedMore, stanceOwn[moreKey]);
            uint increasedKey = Program.Stats.Key(SkillArea.BaseIncreased)!.Value;
            Assert.False(stanceOwn.ContainsKey(increasedKey));
            int AreaGet(string stat) => stat == SkillArea.BaseIncreased ? 50
                : Program.Stats.Key(stat) is uint key ? stanceOwn.GetValueOrDefault(key) : 0;
            Assert.Equal(1.5 * (1 + expectedMore / 100.0), SkillArea.Modifier(AreaGet, spell: false));
            Dictionary<uint, int> nonAreaOwn = [];
            global::Game.ApplyBloodAndSandMeleeStats(conn, nonAreaMelee.Effect, nonAreaOwn);
            Assert.Empty(nonAreaOwn);
            Dictionary<uint, int> spellOwn = [];
            global::Game.ApplyBloodAndSandMeleeStats(conn, spell.Effect, spellOwn);
            Assert.Empty(spellOwn);

            // Removing one source retains the other owner and its selection. Removing the last owner returns to
            // implicit Blood without publishing a stance change.
            conn.RecentEvents.Clear();
            var reducedMace = mace with { Sockets = mace.Sockets.Take(3).ToArray(), Links = [1, 1, 1] };
            global::Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1,
                global::Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            global::Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, reducedMace);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId));
            Assert.Equal(StanceCatalog.BloodAndSand,
                StanceCatalog.SourceFamily(Assert.Single(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId)).EffectId));
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance,
                conn.Clock.Elapsed.TotalSeconds));

            global::Game.ItemsOf(conn).Remove(PlayerInventories.Helmet,
                global::Game.ItemsOf(conn).Items(PlayerInventories.Helmet).Single().Id);
            global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, conn, inventory).GetAwaiter().GetResult();
            Assert.DoesNotContain(conn.Auras.Values, a => StanceCatalog.IsStance(a.EffectId));
            Assert.Equal(StanceCatalog.BloodSelection, conn.Stance);
            Assert.False(conn.RecentEvents.Happened(StatConditions.ChangedStance,
                conn.Clock.Elapsed.TotalSeconds));

            // Flesh and Stone is an independent stance source. Its real admission refuses at zero mana without
            // claiming a cooldown/action or persisted intent, then the funded activation and Sand toggle work alone.
            string fleshOnlyCharacter = "FleshOnly" + Guid.NewGuid().ToString("N");
            var fleshOnly = NewStanceConnection(fleshOnlyCharacter,
                new ItemSocket(SocketColour.White, fleshGem));
            var fleshOnlySkill = Assert.Single(global::Game.GemSkillEffects(fleshOnly), g =>
                global::Game.SkillsFor(fleshOnly).EffectId(g.Effect) == StanceCatalog.FleshAndStone);
            int fleshActions = fleshOnly.ActionCount, fleshBuffs = fleshOnly.NextBuff;
            uint fleshLife = fleshOnly.PlayerLife, fleshMana = fleshOnly.PlayerMana = 0;
            fleshOnly.RecentEvents.Clear();
            Assert.False(CastStance(fleshOnly, fleshOnlySkill.Id, expectAdmission: false));
            Assert.Equal(fleshActions, fleshOnly.ActionCount);
            Assert.Equal(fleshBuffs, fleshOnly.NextBuff);
            Assert.Equal(fleshLife, fleshOnly.PlayerLife);
            Assert.Equal(fleshMana, fleshOnly.PlayerMana);
            Assert.Equal(0, global::Game.ReservedMana(fleshOnly));
            Assert.Empty(fleshOnly.Auras);
            Assert.Empty(fleshOnly.SingleUseCooldowns);
            Assert.DoesNotContain(fleshOnlySkill.Effect, fleshOnly.SkillReadyAt);
            Assert.Empty(fleshOnly.RecentEvents.Snapshot(fleshOnly.Clock.Elapsed.TotalSeconds));
            Assert.DoesNotContain(CharacterSaves.Load(fleshOnlyCharacter)?.ActiveAuras ?? [],
                id => StanceCatalog.SourceFamily(id) == StanceCatalog.FleshAndStone);

            fleshOnly.PlayerMana = fleshOnly.Pools.Mana;
            Assert.True(CastStance(fleshOnly, fleshOnlySkill.Id));
            var fleshOnlyOwner = Assert.Single(fleshOnly.Auras.Values);
            Assert.Equal(StanceCatalog.FleshAndStone, fleshOnlyOwner.EffectId);
            Assert.Equal(StanceCatalog.BloodSelection, fleshOnlyOwner.Stance);
            Assert.NotNull(fleshOnlyOwner.StanceOwner);
            Assert.Contains(StanceCatalog.FleshAndStone,
                CharacterSaves.Load(fleshOnlyCharacter)!.ActiveAuras!);

            lock (fleshOnly.Monsters) { fleshOnly.SingleUseCooldowns.Clear(); fleshOnly.SkillReadyAt.Clear(); }
            fleshOnly.RecentEvents.Clear();
            uint fleshOnlyMana = fleshOnly.PlayerMana;
            Assert.True(CastStance(fleshOnly, fleshOnlySkill.Id));
            fleshOnlyOwner = Assert.Single(fleshOnly.Auras.Values);
            Assert.Equal(StanceCatalog.FleshAndStone, fleshOnlyOwner.EffectId);
            Assert.Equal(StanceCatalog.SandSelection, fleshOnlyOwner.Stance);
            Assert.Equal(fleshOnlyMana, fleshOnly.PlayerMana);
            var fleshOnlySaved = CharacterSaves.Load(fleshOnlyCharacter)!;
            Assert.Equal(StanceCatalog.SandSelection, fleshOnlySaved.Stance);
            Assert.Contains(StanceCatalog.FleshAndStone, fleshOnlySaved.ActiveAuras!);

            var recreatedFleshOnly = RecreatedStanceConnection(fleshOnly);
            var restoredFleshOnly = global::Game.RestoreRememberedAuras(recreatedFleshOnly,
                fleshOnlySaved.ActiveAuras!);
            fleshOnlyOwner = Assert.Single(restoredFleshOnly);
            Assert.Single(recreatedFleshOnly.Auras.Values);
            Assert.Equal(StanceCatalog.FleshAndStone, fleshOnlyOwner.EffectId);
            Assert.Equal(StanceCatalog.SandSelection, fleshOnlyOwner.Stance);
            Assert.NotNull(fleshOnlyOwner.StanceOwner);
            Assert.Equal(StanceCatalog.SandSelection, recreatedFleshOnly.Stance);
            Assert.DoesNotContain(restoredFleshOnly,
                aura => StanceCatalog.SourceFamily(aura.EffectId) == StanceCatalog.BloodAndSand);

            // Legacy intent order survives without a canonical selection, while an explicit canonical Blood
            // overrides the saved Sand alias before both independent source families are admitted.
            string combinedCharacter = "StanceRestore" + Guid.NewGuid().ToString("N");
            var combinedSetup = NewStanceConnection(combinedCharacter,
                new ItemSocket(SocketColour.White, stanceGem),
                new ItemSocket(SocketColour.White, fleshGem));
            CharacterSaves.ClearActiveAuras(combinedCharacter);
            CharacterSaves.SetActiveStance(combinedCharacter, null);
            CharacterSaves.SetAuraActive(combinedCharacter, StanceCatalog.Sand, true);
            CharacterSaves.SetAuraActive(combinedCharacter, StanceCatalog.FleshAndStone, true);
            var legacyIntents = CharacterSaves.Load(combinedCharacter)!.ActiveAuras!;
            Assert.Equal([StanceCatalog.Sand, StanceCatalog.FleshAndStone], legacyIntents);

            var legacyCombined = RecreatedStanceConnection(combinedSetup);
            var restoredLegacy = global::Game.RestoreRememberedAuras(legacyCombined, legacyIntents);
            Assert.Equal([StanceCatalog.Sand, StanceCatalog.FleshAndStone],
                restoredLegacy.Select(aura => aura.EffectId));
            Assert.All(restoredLegacy, aura => Assert.Equal(StanceCatalog.SandSelection, aura.Stance));
            Assert.All(restoredLegacy, aura => Assert.NotNull(aura.StanceOwner));
            Assert.Equal(StanceCatalog.SandSelection, legacyCombined.Stance);

            CharacterSaves.SetActiveStance(combinedCharacter, StanceCatalog.BloodSelection);
            var canonicalIntents = CharacterSaves.Load(combinedCharacter)!.ActiveAuras!;
            var canonicalCombined = RecreatedStanceConnection(legacyCombined);
            var restoredCanonical = global::Game.RestoreRememberedAuras(canonicalCombined, canonicalIntents);
            Assert.Equal([StanceCatalog.Blood, StanceCatalog.FleshAndStone],
                restoredCanonical.Select(aura => aura.EffectId));
            Assert.All(restoredCanonical, aura => Assert.Equal(StanceCatalog.BloodSelection, aura.Stance));
            Assert.All(restoredCanonical, aura => Assert.NotNull(aura.StanceOwner));
            Assert.Equal(StanceCatalog.BloodSelection, canonicalCombined.Stance);

            // A high-level gem raises all requirements of its host, so keep it in its own helmet. The later
            // low-level copy in separate gloves remains eligible when only the low host's actual needs are met.
            var stanceType = items.Gem(items.Find("Metadata/Items/Gems/SkillGemBloodAndSand")!)!;
            var ineligibleStanceGem = stanceGem with
            {
                GemExperience = stanceType.ExperienceFor(stanceType.MaxLevel),
            };
            string duplicateCharacter = "StanceDuplicate" + Guid.NewGuid().ToString("N");
            var duplicate = NewStanceConnection(duplicateCharacter,
                new ItemSocket(SocketColour.White, ineligibleStanceGem));
            var duplicateItems = (ItemCatalog)global::Game.ItemsFor(duplicate)!;
            var duplicateInventory = (PlayerInventories)global::Game.ItemsOf(duplicate)!;
            var highHost = Assert.Single(duplicateInventory.Items(PlayerInventories.Helmet));
            foreach (var old in duplicateInventory.Items(PlayerInventories.Gloves))
                duplicateInventory.Remove(PlayerInventories.Gloves, old.Id);
            var glovesType = duplicateItems.Find("Metadata/Items/Armours/Gloves/GlovesInt1")!;
            var lowHost = duplicateInventory.Add(PlayerInventories.Gloves, 0, 0,
                ItemCatalog.Create(glovesType) with
                {
                    Sockets = [new ItemSocket(SocketColour.White, stanceGem)],
                    Links = [1],
                });
            global::Game.CharacterStatSources(duplicate);

            var lowRequirements = global::Game.GemAttributeRequirements(duplicate, stanceGem,
                stanceType, stance.Effect);
            var ineligibleRequirements = global::Game.GemAttributeRequirements(duplicate, ineligibleStanceGem,
                stanceType, stance.Effect);
            var initialSkills = global::Game.GemSkillEffects(duplicate).Where(g =>
                global::Game.SkillsFor(duplicate).EffectId(g.Effect) == StanceCatalog.BloodAndSand).ToArray();
            Assert.Equal(2, initialSkills.Length);
            var ineligibleDuplicate = Assert.Single(initialSkills, g =>
                global::Game.SupportedSkill(duplicate, g.Id).Host == highHost.Id);
            var eligibleDuplicate = Assert.Single(initialSkills, g =>
                global::Game.SupportedSkill(duplicate, g.Id).Host == lowHost.Id);
            Assert.Null(global::Game.GemSkillInstanceDisabled(duplicate, ineligibleDuplicate.Effect,
                ineligibleDuplicate.Id));
            Assert.Null(global::Game.GemSkillInstanceDisabled(duplicate, eligibleDuplicate.Effect,
                eligibleDuplicate.Id));

            var (lowHostStr, lowHostDex, lowHostInt) = duplicateItems.Requirements(glovesType);
            int lowStr = Math.Max(lowRequirements.Str, lowHostStr);
            int lowDex = Math.Max(lowRequirements.Dex, lowHostDex);
            int lowInt = Math.Max(lowRequirements.Int, lowHostInt);
            Assert.True(ineligibleRequirements.Str > lowStr
                || ineligibleRequirements.Dex > lowDex
                || ineligibleRequirements.Int > lowInt);
            duplicate.PlayerStats[Program.Stats.Key("virtual_strength_for_requirements")!.Value] =
                lowStr;
            duplicate.PlayerStats[Program.Stats.Key("virtual_dexterity_for_requirements")!.Value] =
                lowDex;
            duplicate.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] =
                lowInt;
            global::Game.CharacterStatSources(duplicate);
            var loweredRequirements = global::Game.CurrentGemRequirements(duplicate);
            Assert.Equal(lowStr, loweredRequirements.Strength);
            Assert.Equal(lowDex, loweredRequirements.Dexterity);
            Assert.Equal(lowInt, loweredRequirements.Intelligence);
            Assert.Contains(highHost.Id, loweredRequirements.Inactive);
            Assert.DoesNotContain(lowHost.Id, loweredRequirements.Inactive);
            Assert.True(global::Game.IsInactive(duplicate, highHost.Id));
            Assert.False(global::Game.IsInactive(duplicate, lowHost.Id));
            Assert.NotNull(global::Game.GemSkillInstanceDisabled(duplicate, ineligibleDuplicate.Effect,
                ineligibleDuplicate.Id));
            Assert.Null(global::Game.GemSkillInstanceDisabled(duplicate, eligibleDuplicate.Effect,
                eligibleDuplicate.Id));
            var loweredSkills = global::Game.GemSkillEffects(duplicate).Where(g =>
                global::Game.SkillsFor(duplicate).EffectId(g.Effect) == StanceCatalog.BloodAndSand).ToArray();
            Assert.Equal(eligibleDuplicate.Id, Assert.Single(loweredSkills).Id);

            CharacterSaves.ClearActiveAuras(duplicateCharacter);
            CharacterSaves.SetActiveStance(duplicateCharacter, StanceCatalog.BloodSelection);
            CharacterSaves.SetAuraActive(duplicateCharacter, StanceCatalog.Blood, true);
            var savedDuplicate = CharacterSaves.Load(duplicateCharacter)!;
            Assert.Equal(StanceCatalog.BloodSelection, savedDuplicate.Stance);
            Assert.Contains(StanceCatalog.Blood, savedDuplicate.ActiveAuras!);
            var restoredDuplicate = global::Game.RestoreRememberedAuras(duplicate, savedDuplicate.ActiveAuras!);
            var duplicateOwner = Assert.Single(restoredDuplicate);
            Assert.Single(duplicate.Auras.Values);
            Assert.Equal(eligibleDuplicate.Id, duplicateOwner.SkillId);
            Assert.Equal(StanceCatalog.Blood, duplicateOwner.EffectId);
            Assert.Equal(StanceCatalog.BloodSelection, duplicateOwner.Stance);
            Assert.NotNull(duplicateOwner.StanceOwner);
            Assert.Equal(StanceCatalog.BloodSelection, duplicate.Stance);
            Assert.DoesNotContain(restoredDuplicate, aura => aura.SkillId == ineligibleDuplicate.Id);
            Assert.False(CastStance(duplicate, ineligibleDuplicate.Id, expectAdmission: false));
            Assert.Same(duplicateOwner, Assert.Single(duplicate.Auras.Values));

            var helmetType = duplicateItems.ByHash(highHost.Item.TypeHash)!;
            var (highHostStr, highHostDex, highHostInt) = duplicateItems.Requirements(helmetType);
            duplicate.PlayerStats[Program.Stats.Key("virtual_strength_for_requirements")!.Value] =
                Math.Max(ineligibleRequirements.Str, highHostStr);
            duplicate.PlayerStats[Program.Stats.Key("virtual_dexterity_for_requirements")!.Value] =
                Math.Max(ineligibleRequirements.Dex, highHostDex);
            duplicate.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] =
                Math.Max(ineligibleRequirements.Int, highHostInt);
            global::Game.CharacterStatSources(duplicate);
            var raisedRequirements = global::Game.CurrentGemRequirements(duplicate);
            Assert.DoesNotContain(highHost.Id, raisedRequirements.Inactive);
            Assert.DoesNotContain(lowHost.Id, raisedRequirements.Inactive);
            var raisedSkills = global::Game.GemSkillEffects(duplicate).Where(g =>
                global::Game.SkillsFor(duplicate).EffectId(g.Effect) == StanceCatalog.BloodAndSand).ToArray();
            Assert.Equal(2, raisedSkills.Length);
            Assert.Null(global::Game.GemSkillInstanceDisabled(duplicate, ineligibleDuplicate.Effect,
                ineligibleDuplicate.Id));
            int duplicateActions = duplicate.ActionCount;
            Assert.False(CastStance(duplicate, ineligibleDuplicate.Id, expectAdmission: false));
            Assert.Equal(duplicateActions, duplicate.ActionCount);
            Assert.Same(duplicateOwner, Assert.Single(duplicate.Auras.Values));

            // Arrogance-supported Blood and Sand must leave at least 1 current Life. The world-facing 0x191 entry
            // refuses equality without creating a cooldown use/action/native buff, while paying 1 more Life admits and
            // leaves exactly 1; changing to the already-reserved stance remains valid at that low Life.
            string lifeCharacter = "StanceLife" + Guid.NewGuid().ToString("N");
            var lifeConn = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), 3, 1)
            {
                Character = lifeCharacter, PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true,
                Instance = new(), Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                    new HashSet<string>(["Game"])),
            };
            foreach (string stat in new[] { "virtual_strength_for_requirements",
                         "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                lifeConn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
            var lifeItems = (ItemCatalog)global::Game.ItemsFor(lifeConn)!;
            var lifeInventory = (PlayerInventories)global::Game.ItemsOf(lifeConn)!;
            foreach (var old in lifeInventory.Items(PlayerInventories.Helmet))
                lifeInventory.Remove(PlayerInventories.Helmet, old.Id);
            var lifeStanceGem = ItemCatalog.Create(lifeItems.Find("Metadata/Items/Gems/SkillGemBloodAndSand")!);
            var lifeArroganceGem = ItemCatalog.Create(lifeItems.Find("Metadata/Items/Gems/SupportGemBloodMagic")!);
            var lifeHelmet = ItemCatalog.Create(lifeItems.Find("Metadata/Items/Armours/Helmets/HelmetInt1")!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, lifeStanceGem),
                    new ItemSocket(SocketColour.White, lifeArroganceGem)],
                Links = [2],
            };
            var lifeHelmetPlacement = lifeInventory.Add(PlayerInventories.Helmet, 0, 0, lifeHelmet);
            global::Game.EquipmentStatSources(lifeConn, lifeInventory.EquipmentSnapshot());
            lifeConn.Pools = global::Game.PoolsFor(lifeConn, 100);
            lifeConn.PlayerLife = global::Game.LifeCap(lifeConn);
            lifeConn.PlayerMana = (uint)global::Game.UnreservedMana(lifeConn);
            var lifeStance = Assert.Single(global::Game.GemSkillEffects(lifeConn), g =>
                global::Game.SkillsFor(lifeConn).EffectId(g.Effect) == StanceCatalog.BloodAndSand);
            var lifeCost = global::Game.SupportedReservationCost(lifeConn, lifeStance.Id)!.Value;
            Assert.Equal(0, lifeCost.ManaPercent);
            Assert.True(lifeCost.LifeFlat > 0 || lifeCost.LifePercent > 0);
            Game.StanceAdmissionPlan? fullLifePlan;
            lock (lifeConn.Monsters)
                Assert.True(global::Game.TryPlanStanceLocked(lifeConn, lifeStance.Id, lifeStance.Effect,
                    lifeConn.Clock.Elapsed.TotalSeconds, out fullLifePlan));
            uint additionalLife = lifeConn.PlayerLife - fullLifePlan!.LifeAfter;
            Assert.True(additionalLife > 0);
            void ClearLifeCooldowns()
            {
                lock (lifeConn.Monsters)
                {
                    lifeConn.SingleUseCooldowns.Clear();
                    lifeConn.SkillReadyAt.Clear();
                }
            }
            Task LifeInstantUse(ushort id) => global::Game.UseInstantSkill("test", wire.Stream, wire.Ciphers,
                lifeConn, id, 0x1000, lifeConn.Walk.Position(lifeConn.Clock.Elapsed.TotalSeconds));

            lifeConn.PlayerLife = additionalLife;
            ClearLifeCooldowns();
            lifeConn.RecentEvents.Clear();
            int lifeActions = lifeConn.ActionCount, lifeBuffs = lifeConn.NextBuff;
            uint lifeMana = lifeConn.PlayerMana;
            var noAckGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            Assert.True(noAckGate.Wait(0));
            var equalityRefusal = LifeInstantUse(lifeStance.Id);
            Assert.True(equalityRefusal.IsCompleted, "Life equality waited for an acknowledgement");
            Assert.Equal(0, noAckGate.CurrentCount);
            noAckGate.Release();
            equalityRefusal.GetAwaiter().GetResult();
            Assert.Equal(additionalLife, lifeConn.PlayerLife);
            Assert.Equal(lifeMana, lifeConn.PlayerMana);
            Assert.Equal(lifeActions, lifeConn.ActionCount);
            Assert.Equal(lifeBuffs, lifeConn.NextBuff);
            Assert.Empty(lifeConn.Auras);
            Assert.Empty(lifeConn.SingleUseCooldowns);
            Assert.DoesNotContain(lifeStance.Effect, lifeConn.SkillReadyAt);
            Assert.Empty(lifeConn.RecentEvents.Snapshot(lifeConn.Clock.Elapsed.TotalSeconds));

            lifeConn.PlayerLife = additionalLife + 1;
            ClearLifeCooldowns();
            var liveGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            Assert.True(liveGate.Wait(0));
            var liveUse = LifeInstantUse(lifeStance.Id);
            Assert.False(liveUse.IsCompleted, "the accepted 0x191 did not hold its deferred output");
            var liveAura = Assert.Single(lifeConn.Auras.Values);
            Assert.Equal(1u, lifeConn.PlayerLife);
            liveGate.Release();
            liveUse.GetAwaiter().GetResult();
            Assert.Equal(1u, lifeConn.PlayerLife);
            lock (lifeConn.Monsters)
                Assert.Equal(lifeStance.Id, Assert.Single(lifeConn.SingleUseCooldowns).Value.NativeSkillId);

            // No additional reservation is needed to change the selected stance, so 1 current Life stays valid.
            ClearLifeCooldowns();
            lifeConn.RecentEvents.Clear();
            LifeInstantUse(lifeStance.Id).GetAwaiter().GetResult();
            Assert.Equal(1u, lifeConn.PlayerLife);
            Assert.NotEqual(liveAura.EffectId, Assert.Single(lifeConn.Auras.Values).EffectId);
            double switchAt = lifeConn.Clock.Elapsed.TotalSeconds;
            double switchExpiry = lifeConn.RecentEvents.NextExpiry(switchAt)!.Value;
            Assert.True(switchExpiry > switchAt);

            void StaleLifeOutput(string name, Action change, Action clear)
            {
                ClearLifeCooldowns();
                lifeConn.RecentEvents.Clear();
                lifeConn.PlayerLife = 1;
                LifeInstantUse(lifeStance.Id).GetAwaiter().GetResult();
                ClearLifeCooldowns();
                lifeConn.RecentEvents.Clear();
                var outputGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(outputGate.Wait(0));
                var pending = LifeInstantUse(lifeStance.Id);
                Assert.False(pending.IsCompleted, $"{name}: the accepted use did not reach deferred output");
                var committedAura = Assert.Single(lifeConn.Auras.Values);
                Assert.Equal(1u, lifeConn.PlayerLife);
                double committedAt = lifeConn.Clock.Elapsed.TotalSeconds;
                double committedExpiry = lifeConn.RecentEvents.NextExpiry(committedAt)!.Value;
                SingleUseCooldownState committedState;
                lock (lifeConn.Monsters)
                    committedState = Assert.Single(lifeConn.SingleUseCooldowns).Value;
                int committedVersion = committedState.UseVersion;
                change();
                int expectedActions = lifeConn.ActionCount;
                outputGate.Release();
                pending.GetAwaiter().GetResult();
                Assert.True(committedVersion == committedState.UseVersion, $"{name}: the use was reserved again");
                Assert.True(committedState.NativeSkillId == 0, $"{name}: stale output confirmed the native use");
                Assert.True(committedExpiry == lifeConn.RecentEvents.NextExpiry(committedAt),
                    $"{name}: the recent event moved");
                Assert.True(lifeConn.RecentEvents.Happened(StatConditions.ChangedStance, committedExpiry - .001));
                if (name != "source removal")
                    Assert.Same(committedAura.StanceOwner, Assert.Single(lifeConn.Auras.Values).StanceOwner);
                Assert.Equal(expectedActions, lifeConn.ActionCount);
                clear();
            }

            StaleLifeOutput("later action", () => { lock (lifeConn.Monsters) lifeConn.ActionCount++; }, () => { });
            StaleLifeOutput("retired", () => lifeConn.RageRetired = true, () => lifeConn.RageRetired = false);
            StaleLifeOutput("module", () => global::Game.CombatLifetime = new ModuleLifetime("stale"),
                () => global::Game.CombatLifetime = lifetime);

            foreach (var (name, apply, restore) in new (string, Action, Action)[]
                     {
                         ("absence", () => lifeConn.PlayerLife = 1, () => { }),
                         ("death", () => lifeConn.PlayerLife = 0, () => lifeConn.PlayerLife = 1),
                         ("pause", () => lifeConn.Clock.Stop(), () => lifeConn.Clock.Start()),
                     })
            {
                ClearLifeCooldowns();
                lifeConn.RecentEvents.Clear();
                apply();
                int actionsBefore = lifeConn.ActionCount, buffsBefore = lifeConn.NextBuff;
                uint lifeBefore = lifeConn.PlayerLife;
                var auraBefore = Assert.Single(lifeConn.Auras.Values);
                var noUseGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(noUseGate.Wait(0));
                var noUse = LifeInstantUse(name == "absence" ? (ushort)0xfffe : lifeStance.Id);
                Assert.True(noUse.IsCompleted, $"{name} waited for output");
                Assert.Equal(0, noUseGate.CurrentCount);
                noUseGate.Release();
                noUse.GetAwaiter().GetResult();
                Assert.Equal(lifeBefore, lifeConn.PlayerLife);
                Assert.Equal(actionsBefore, lifeConn.ActionCount);
                Assert.Equal(buffsBefore, lifeConn.NextBuff);
                Assert.Same(auraBefore.StanceOwner, Assert.Single(lifeConn.Auras.Values).StanceOwner);
                Assert.Empty(lifeConn.SingleUseCooldowns);
                Assert.Empty(lifeConn.SkillReadyAt);
                Assert.Empty(lifeConn.RecentEvents.Snapshot(lifeConn.Clock.Elapsed.TotalSeconds));
                restore();
            }

            using var lifeWire = new Wire();
            ClearLifeCooldowns();
            lifeConn.RecentEvents.Clear();
            lifeConn.PlayerLife = 1;
            LifeInstantUse(lifeStance.Id).GetAwaiter().GetResult();
            ClearLifeCooldowns();
            lifeConn.RecentEvents.Clear();
            var sourceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            Assert.True(sourceGate.Wait(0));
            var staleSource = LifeInstantUse(lifeStance.Id);
            Assert.False(staleSource.IsCompleted, "the accepted source-removal use did not reach deferred output");
            var staleAura = Assert.Single(lifeConn.Auras.Values);
            double staleAt = lifeConn.Clock.Elapsed.TotalSeconds;
            double staleExpiry = lifeConn.RecentEvents.NextExpiry(staleAt)!.Value;
            SingleUseCooldownState staleState;
            lock (lifeConn.Monsters) staleState = Assert.Single(lifeConn.SingleUseCooldowns).Value;
            int staleVersion = staleState.UseVersion;
            lifeInventory.Remove(PlayerInventories.Helmet, lifeHelmetPlacement.Id);
            global::Game.SendEquipment("test", lifeWire.Stream, lifeWire.Ciphers, lifeConn, lifeInventory)
                .GetAwaiter().GetResult();
            Assert.Empty(lifeConn.Auras);
            sourceGate.Release();
            staleSource.GetAwaiter().GetResult();
            Assert.Empty(lifeConn.Auras);
            Assert.Equal(staleVersion, staleState.UseVersion);
            Assert.Equal(0, staleState.NativeSkillId);
            Assert.Equal(staleExpiry, lifeConn.RecentEvents.NextExpiry(staleAt));
            PlayerInventories.Forget(lifeCharacter);
            global::Game.InactiveItems.TryRemove(lifeCharacter, out _);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(savePath);
        }
    }

    sealed class OneStepSchedule(double start, Action stop) : IPlayerUpdateSchedule
    {
        int waits;
        public double Now(ClientState conn) => start;
        public double ResourceNow(ClientState conn) => start + 1;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (waits++ == 0) return Task.CompletedTask;
            stop();
            return Task.CompletedTask;
        }
    }

    sealed class AdvancingOneStepSchedule : IPlayerUpdateSchedule
    {
        readonly double start;
        readonly Action stop;
        int waits;
        double now;
        public AdvancingOneStepSchedule(double start, Action stop)
        {
            this.start = start;
            this.stop = stop;
            now = start;
        }
        public double Now(ClientState conn) => now;
        public double ResourceNow(ClientState conn) => now;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (waits++ == 0)
            {
                now = start + 1;
                return Task.CompletedTask;
            }
            stop();
            return Task.CompletedTask;
        }
    }

    sealed class ThresholdRoll(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }

    sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => value;
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly Task readerDrain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            readerDrain = Drain(reader);
        }

        static async Task Drain(TcpClient connection)
        {
            try
            {
                await connection.GetStream().CopyToAsync(System.IO.Stream.Null).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
            listener.Stop();
            readerDrain.GetAwaiter().GetResult();
        }
    }
}
