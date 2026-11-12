using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    sealed class PhasingRoll(int value) : Random
    {
        public override double NextDouble() => Math.Clamp(value / 100d, 0, .999999);
        public override int Next(int maxValue) => Math.Clamp(value, 0, maxValue - 1);
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }

    void Qualified_phasing_sources_reach_native_pathing_and_projectile_consumers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-phasing-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        string character = "SourcePhase" + Guid.NewGuid().ToString("N");
        var priorLogic = Program.GameLogicOrNull; var priorLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            PlayerInventories.SetClass(character, 1);
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), 1, 1)
            {
                Character = character, ClassRow = 1, Instance = new(), PlayerLife = 1000,
                PlayerSent = true, PlayerObjectCreated = true, Pools = new(100, 1000, 1000, 0, 0),
                Experience = CharacterStats.ExperienceFor(100),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            var inventory = Game.ItemsOf(conn);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            var uniques = new UniqueCatalog(Program.Items);
            ItemMod Minimum(string id) => Program.Items.Mods.ById(id)!.Roll(ModSection.Explicit, new Random(504))
                with { Values = [Program.Items.Mods.ById(id)!.Stats[0].Min] };
            foreach (byte row in new byte[] { 6, 7 })
                inventory.Add(row, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!)
                    with { Rarity = ItemRarity.Rare, Mods = [Minimum("Strength10"), Minimum("Dexterity10"), Minimum("Intelligence10")] });
            inventory.Add(PlayerInventories.Gloves, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/Gloves/GlovesStr1")!)
                    with { Rarity = ItemRarity.Rare, Mods = [Minimum("Dexterity10")] });
            InventoryItem Wear(string name, byte row)
            {
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
                var placed = inventory.Add(row, 0, 0, uniques.Create(name, new Random(504))!);
                conn.Pools = Game.PoolsFor(conn, 100);
                Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, placed.Id)); return placed;
            }
            Dictionary<uint, int> Read() => Game.CharacterPlayerStats(conn);
            int Value(IReadOnlyDictionary<uint, int> stats, string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            var tiki = Wear("Araku Tiki", 5);
            var voidwalker = Wear("Voidwalker", PlayerInventories.Boots);
            var body = new SpawnedMonster("phase-blocker", new(50, 50, 8), 60, 2, 23);
            conn.Monsters[0x4050] = body;
            conn.SetBlocker(0x4051, 60, 60, 4);
            conn.PlayerLife = conn.PlayerMaxLife;
            Read();
            Assert.False(conn.IsPhasing);
            Assert.True(conn.Blockers.Count > conn.StationaryBlockers.Count);
            conn.PlayerLife = conn.PlayerMaxLife / 2;
            var low = Read();
            Assert.True(conn.IsPhasing);
            Assert.Equal(conn.StationaryBlockers.Count, conn.Blockers.Count);
            Assert.Equal(1, Value(low, "virtual_phase_through_objects"));
            Assert.Equal(5, Value(low, "projectile_number_of_targets_to_pierce"));
            foreach (bool attack in new[] { false, true })
                Assert.Equal(5, GatewayProjectileModifiers.Resolve(new ProjectileProfile("source-phase"),
                    n => Value(low, n), attack).Pierce);
            conn.PlayerLife = conn.PlayerMaxLife;
            var high = Read();
            Assert.False(conn.IsPhasing);
            Assert.Equal(0, Value(high, "projectile_number_of_targets_to_pierce"));
            conn.PlayerLife = conn.PlayerMaxLife / 2;
            Read(); Assert.True(conn.IsPhasing);
            inventory.Remove(5, tiki.Id);
            Read(); Assert.False(conn.IsPhasing);
            conn.PlayerLife = conn.PlayerMaxLife;
            Game.ChargesOnKill(conn, new SpawnedMonster("owned kill", new(50, 50, 8), 60, 2, 0));
            var killed = Read();
            Assert.True(conn.IsPhasing);
            Assert.Equal(5, Value(killed, "projectile_number_of_targets_to_pierce"));
            conn.Experience = CharacterStats.ExperienceFor(1);
            conn.Pools = Game.PoolsFor(conn, 1);
            Read(); Assert.False(conn.IsPhasing);
            conn.Experience = CharacterStats.ExperienceFor(100);
            conn.Pools = Game.PoolsFor(conn, 100);
            Read(); Assert.True(conn.IsPhasing);
            conn.RecentEvents.Clear();
            Read(); Assert.False(conn.IsPhasing);
            inventory.Remove(PlayerInventories.Boots, voidwalker.Id);
            Assert.Equal(0, Value(Read(), "projectile_number_of_targets_to_pierce"));
            tiki = Wear("Araku Tiki", 5);
            voidwalker = Wear("Voidwalker", PlayerInventories.Boots);
            conn.PlayerLife = conn.PlayerMaxLife;
            Read(); Assert.False(conn.IsPhasing);
            var coward = Wear("Coward's Legacy", PlayerInventories.Belt);
            var countedLow = Read();
            Assert.True(conn.IsPhasing); // The qualified curse counts as low Life even at actual full Life.
            Assert.Equal(1, Value(countedLow, "virtual_phase_through_objects"));
            inventory.Remove(PlayerInventories.Belt, coward.Id);
            Read(); Assert.False(conn.IsPhasing);
            conn.PlayerLife = conn.PlayerMaxLife / 2;
            low = Read();
            int Get(string name) => Value(low, name);
            Assert.Equal(20, Get("base_avoid_projectiles_%_chance"));
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100));
            Assert.Equal(0, PlayerDamageTaken.Avoid(hit, Get, new PhasingRoll(19), out var avoided, projectile: true).MinTotal);
            Assert.Equal(0, PlayerDamageTaken.Landed(hit, Get, avoided).MinTotal);
            Assert.Equal(200, PlayerDamageTaken.Avoid(hit, Get, new PhasingRoll(20), out _, projectile: true).MinTotal);
            Assert.Equal(200, PlayerDamageTaken.Avoid(hit, Get, new PhasingRoll(19), out _, projectile: false).MinTotal);
            conn.PlayerLife = conn.PlayerMaxLife;
            low = Read();
            Assert.Equal(200, PlayerDamageTaken.Avoid(hit, Get, new PhasingRoll(0), out _, projectile: true).MinTotal);
            conn.PlayerLife = conn.PlayerMaxLife / 2;
            Wear("Three-step Assault", PlayerInventories.Boots);
            low = Read();
            foreach (var kind in Enum.GetValues<PlayerElementalAilments.Kind>())
            {
                string name = "avoid_" + kind.ToString().ToLowerInvariant() + "_%";
                Assert.Equal(30, Get(name));
                conn.ElementalAilments.Clear();
                double at = conn.Clock.Elapsed.TotalSeconds;
                conn.ElementalAilments.Inflict(kind, 10, 2, at, Get, new PhasingRoll(29));
                Assert.Equal(0, conn.ElementalAilments.Magnitude(kind, at));
                conn.ElementalAilments.Inflict(kind, 10, 2, at, Get, new PhasingRoll(30));
                Assert.True(conn.ElementalAilments.Magnitude(kind, at) > 0,
                    $"{kind}: base={Get(name)}, alias={Get("avoid_" + kind.ToString().ToLowerInvariant() + "_%")}, immune={Get("immune_to_" + kind.ToString().ToLowerInvariant())}, duration={Get("self_" + kind.ToString().ToLowerInvariant() + "_duration_-%")}");
            }
            conn.ElementalAilments.Clear();
            foreach (var (shield, type) in new[] { ("Mistwall", DamageType.Fire), ("Replica Mistwall", DamageType.Physical) })
            {
                Wear(shield, PlayerInventories.Offhand1);
                low = Read();
                Assert.True(PlayerDamageTaken.AvoidPct(type, Get) is >= 8 and <= 15,
                    $"{shield}: phase={conn.IsPhasing}, life={conn.PlayerLife}/{conn.PlayerMaxLife}, values=" +
                    string.Join(",", low.Where(p => Program.Stats.Name(p.Key)?.Contains("avoid_") == true)
                        .Select(p => Program.Stats.Name(p.Key) + "=" + p.Value)));
                if (type == DamageType.Fire)
                {
                    Assert.Equal(PlayerDamageTaken.AvoidPct(type, Get), PlayerDamageTaken.AvoidPct(DamageType.Cold, Get));
                    Assert.Equal(PlayerDamageTaken.AvoidPct(type, Get), PlayerDamageTaken.AvoidPct(DamageType.Lightning, Get));
                    Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Physical, Get));
                }
                conn.PlayerLife = conn.PlayerMaxLife;
                low = Read();
                Assert.Equal(0, PlayerDamageTaken.AvoidPct(type, Get));
                conn.PlayerLife = conn.PlayerMaxLife / 2;
            }
            inventory.Remove(5, tiki.Id);
            conn.PlayerLife = conn.PlayerMaxLife;
            Read(); Assert.False(conn.IsPhasing);
            conn.IncomingHitRandom = new PhasingRoll(0);
            Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 10,
                block: true, spell: false, suppress: false, out var blocked));
            Assert.True(blocked.Blocked);
            Read(); Assert.True(conn.IsPhasing);
            conn.RecentEvents.Clear();
            Read(); Assert.False(conn.IsPhasing);
            Existing_resource_conditions_reach_ailment_consumers(conn, Wear, Minimum);
            Qualified_attribute_and_level_damage_sources_reach_attack_and_spell_consumers(conn, Wear);
            Qualified_attribute_coefficients_reach_final_offence(conn, Wear);
            Qualified_level_sources_reach_final_consumers(conn, Wear);
            Her_embrace_uses_accepted_ignites_and_qualified_source_lifetimes(conn, Wear);
            Elusive_uses_actual_critical_sources_decay_and_conditional_consumers(conn, Wear);
            Qualified_avatar_rotation_and_permanent_sources_reach_final_consumers(conn, Wear);
            Qualified_charge_degeneration_reaches_resources(conn, Wear);
            Qualified_charge_triggers_and_shield_leech_use_live_state(conn, Wear);
            Qualified_charge_defence_coefficients_reach_hit_consumers(conn, Wear);
            Qualified_charge_damage_sources_reach_incoming_and_attack_consumers(conn, Wear);
            Qualified_low_life_resistances_reach_pools(conn, Wear);
            Blood_dance_target_life_uses_pre_hit_state_and_frozen_charges(conn, Wear);
            Qualified_vaal_use_and_during_flask_sources_have_final_consumers(conn, Wear);
            Explicit_curse_cast_histories_reach_actual_item_consumers(conn, Wear);
            Qualified_flat_cost_sources_reach_skill_panels_and_payment(conn);
            Each_equipped_item_uses_qualified_counts_for_cost_and_recovery(conn);
            Equipment_collections_reach_actual_helmet_life_and_regeneration(conn);
            Qualified_maximum_block_flags_reach_pools_and_real_blocks(conn, Wear);
            Blinded_item_conditions_use_the_existing_timed_owner_and_charge_consumer(conn, Wear);
            Qualified_item_ignore_resistance_reaches_hit_mitigation(conn);
            Maximum_endurance_source_owns_onslaught_and_zero_maximum(conn, Wear);
            Life_mana_and_fortify_sources_share_the_existing_onslaught_owner(conn, Wear);
            Chilled_staff_sources_reach_existing_shock_avoidance(conn, Wear);
            Recent_hit_life_loss_integrates_windows_and_qualified_sources(conn, Wear);
            Qualified_low_life_claw_damage_reaches_the_attacking_hand(conn, Wear);
            Qualified_low_mana_spell_damage_taken_reaches_incoming_hits(conn, Wear);
            conn.PlayerLife = 0;
            Read(); Assert.False(conn.IsPhasing);
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            PlayerInventories.Forget(character); Game.InactiveItems.TryRemove(character, out _);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = priorLifetime;
            Program.SetGameLogic(priorLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
