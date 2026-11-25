using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PlayerDamageTakenTests
{
    static async Task Watchers_Eye_starts_four_seconds_of_mana_recoup_from_a_hit_under_Clarity()
    {
        var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Watcher's Eye", new Random(17)));
        var mod = Program.Items.Mods.ById("ClarityDamageTakenGainedAsMana")!;
        foreach (var (equipped, active) in new[] { (false, true), (true, false), (true, true) })
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, jewel with
            { Mods = equipped ? [new(ModSection.Explicit, mod.Hash, [18])] : [] });
            ushort id = Assert.Single(fixture.Equip(("Clarity", 1)));
            var aura = Assert.Single(Game.GemSkillEffects(player));
            if (active)
                Assert.True(await Game.ToggleAura(player, id, aura.Effect, aura.Name,
                    Game.SupportedReservationCost(player, id)!.Value, aura.Level, aura.Quality));
            Assert.Equal(active, Game.AffectingAuras(player).Any(a => a.SkillId == id));
            Game.RecomputePoolsAndSpeed(player);
            player.PlayerLife = player.PlayerMaxLife;
            player.PlayerMana = 20;
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, new RecoupClock());
            try
            {
                double now = Game.AreaTime(player);
                Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 400, block: false, spell: true,
                    suppress: false, out var hit));
                Assert.Equal(400, hit.Damage);
                Assert.Equal(20u, player.PlayerMana);
                var recoup = Game.ResourceEffectsOf(player).Mana;
                Assert.Equal(equipped && active ? 18 : 0, recoup.Take(now + 1), 6);
                Assert.Equal(equipped && active ? 54 : 0, recoup.Take(now + 4), 6);
                Assert.Equal(0, recoup.Take(now + 5), 6);
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    sealed class RecoupClock : IPlayerUpdateSchedule
    {
        public double Now(Player player) => 10;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    static async Task Clarity_Watchers_Eye_adds_seven_percent_of_maximum_mana_as_energy_shield()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var items = Game.ItemsOf(player);
        var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Watcher's Eye", new Random(8)));
        ushort hash = Program.Items.Mods.ById("ClarityManaAddedAsEnergyShield")!.Hash;
        const byte slot = 1;
        player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
        items.Add(PlayerInventories.PassiveJewels, slot, 0, jewel with { Mods = [new(ModSection.Explicit, hash, [7])] });
        fixture.Equip(("Clarity", 1));
        var aura = Assert.Single(Game.GemSkillEffects(player));
        Game.RecomputePoolsAndSpeed(player);
        uint mana = player.Pools.Mana;
        uint baseline = player.EnergyShield.Maximum;
        var reservation = Game.SupportedReservationCost(player, aura.Id)!.Value;
        Assert.True(await Game.ToggleAura(player, aura.Id, aura.Effect, aura.Name, reservation, aura.Level, aura.Quality));
        Assert.Equal(mana, player.Pools.Mana);
        Assert.Equal(7, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.ManaPctToAddAsEnergyShield));
        uint added = (uint)(mana * .07m * (100 + player.Pools.EnergyShieldPct) / 100);
        Assert.Equal(baseline + added, player.EnergyShield.Maximum);
        Assert.True(await Game.ToggleAura(player, aura.Id, aura.Effect, aura.Name, reservation, aura.Level, aura.Quality));
        Assert.Equal(baseline, player.EnergyShield.Maximum);
    }

    static Func<uint, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => Stat.Of(s.Stat) == name).Sum(s => s.Value);

    static DamagePacket Hit(params (DamageType Type, int Amount)[] parts) =>
        parts.Aggregate(DamagePacket.Empty, (hit, p) => hit.Add(p.Type, new DamageRange(p.Amount, p.Amount)));

    // Returns the given rolls in order and counts them.
    sealed class Rolls(params int[] values) : Random
    {
        public int Drawn { get; private set; }
        public override int Next(int minValue, int maxValue) => values[Drawn++];
    }

    // Each type shifts its share of its original amount once, in any direction; the source keeps the rest, and a total
    // over 100% adds damage instead of being scaled down. Each type taken may then be avoided. After mitigation each type
    // takes its flat damage, its increased damage taken and, when blocked, its share; mana takes its share of what
    // reaches Life.
    [Fact]
    public void A_hit_shifts_once_then_avoids_and_takes_flat_increased_blocked_and_mana_damage()
    {
        Assert.All(PlayerDamageTaken.StatNames, name => Assert.True(StatCatalog.IsClientId(name)));

        var physical = Hit((DamageType.Physical, 100));
        Assert.Same(physical, PlayerDamageTaken.Shift(physical, With()));

        var shifted = PlayerDamageTaken.Shift(physical,
            With(("physical_damage_taken_%_as_fire", 10), ("physical_damage_taken_%_as_cold", 15),
                ("physical_hit_and_dot_damage_%_taken_as_fire", 5)));
        Assert.Equal(70, shifted[DamageType.Physical].Min, 6);
        Assert.Equal(15, shifted[DamageType.Fire].Min, 6);
        Assert.Equal(15, shifted[DamageType.Cold].Min, 6);

        var over = PlayerDamageTaken.Shift(physical,
            With(("physical_damage_taken_%_as_lightning", 60), ("physical_damage_taken_%_as_chaos", 60)));
        Assert.Equal(0, over[DamageType.Physical].Min, 6);
        Assert.Equal(120, over.MinTotal, 6);

        // The Rat Cage's 100% fire as physical and 50% physical as fire swap at once: nothing shifts back.
        var swapped = PlayerDamageTaken.Shift(Hit((DamageType.Physical, 100), (DamageType.Fire, 100)),
            With(("base_fire_hit_damage_taken_%_as_physical", 100), ("physical_damage_taken_%_as_fire", 50)));
        Assert.Equal(150, swapped[DamageType.Physical].Min, 6);
        Assert.Equal(50, swapped[DamageType.Fire].Min, 6);

        // The negated stat is stored as -10; elemental and two-element stats shift each of their types.
        var elemental = PlayerDamageTaken.Shift(Hit((DamageType.Fire, 100), (DamageType.Cold, 100), (DamageType.Lightning, 100)),
            With(("base_fire_hit_damage_taken_%_as_physical_value_negated", -10), ("elemental_damage_taken_%_as_chaos", 20),
                ("cold_and_lightning_damage_taken_%_as_fire", 50)));
        Assert.Equal(10, elemental[DamageType.Physical].Min, 6);
        Assert.Equal(70 + 50 + 50, elemental[DamageType.Fire].Min, 6);
        Assert.Equal(30, elemental[DamageType.Cold].Min, 6);
        Assert.Equal(30, elemental[DamageType.Lightning].Min, 6);
        Assert.Equal(60, elemental[DamageType.Chaos].Min, 6);

        // "Damage of each Type" adds to each type's own chance, capped at 75%. Each type rolls once; one without a chance
        // draws nothing, so the incoming-hit rolls after it stay in order.
        Assert.Equal(75, PlayerDamageTaken.AvoidPct(DamageType.Fire, With(("avoid_fire_damage_%", 70), ("avoid_damage_%", 10))));
        Assert.Equal(10, PlayerDamageTaken.AvoidPct(DamageType.Cold, With(("avoid_fire_damage_%", 70), ("avoid_damage_%", 10))));
        var rolls = new Rolls(99, 0);   // the packet's order: cold (kept at 99), then fire (avoided at 0)
        var kept = PlayerDamageTaken.Avoid(Hit((DamageType.Physical, 100), (DamageType.Fire, 100), (DamageType.Cold, 100)),
            With(("avoid_fire_damage_%", 75), ("avoid_cold_damage_%", 10)), rolls, out var avoided);
        Assert.Equal(100, kept[DamageType.Physical].Min, 6);
        Assert.Equal(0, kept[DamageType.Fire].Min, 6);
        Assert.Equal(100, kept[DamageType.Cold].Min, 6);
        Assert.Equal([DamageType.Fire], avoided);
        Assert.Equal(2, rolls.Drawn);
        Assert.Same(physical, PlayerDamageTaken.Avoid(physical, With(), new Rolls(), out var none));
        Assert.Empty(none);

        // The damage dealt that lands: avoided fire is gone, and fire half taken as physical keeps the half that is not
        // avoided.
        var dealt = Hit((DamageType.Fire, 100), (DamageType.Cold, 50));
        var landed = PlayerDamageTaken.Landed(dealt, With(), [DamageType.Fire]);
        Assert.Equal(0, landed[DamageType.Fire].Min, 6);
        Assert.Equal(50, landed[DamageType.Cold].Min, 6);
        Assert.Equal(50, PlayerDamageTaken.Landed(dealt, With(("base_fire_hit_damage_taken_%_as_physical", 50)),
            [DamageType.Physical])[DamageType.Fire].Min, 6);
        Assert.Same(dealt, PlayerDamageTaken.Landed(dealt, With(), []));

        // Generic, typed and elemental increases add, and a melee hit's too; chaos is not elemental.
        var increased = With(("base_damage_taken_+%", 10), ("lightning_damage_taken_+%", 6), ("elemental_damage_taken_+%", -5),
            ("melee_damage_taken_+%", 60));
        Assert.Equal(11, PlayerDamageTaken.IncreasedPct(DamageType.Lightning, melee: false, increased));
        Assert.Equal(5, PlayerDamageTaken.IncreasedPct(DamageType.Fire, melee: false, increased));
        Assert.Equal(10, PlayerDamageTaken.IncreasedPct(DamageType.Chaos, melee: false, increased));
        Assert.Equal(70, PlayerDamageTaken.IncreasedPct(DamageType.Chaos, melee: true, increased));

        // "-7 Physical Damage taken from Attack Hits" counts only for attacks; each type has its own flat stat.
        var flat = With(("physical_attack_damage_taken_+", -7), ("physical_damage_taken_+", -3), ("fire_damage_taken_+", -15),
            ("chaos_damage_taken_+", 20));
        Assert.Equal(-10, PlayerDamageTaken.Flat(DamageType.Physical, attack: true, flat));
        Assert.Equal(-3, PlayerDamageTaken.Flat(DamageType.Physical, attack: false, flat));
        Assert.Equal(-15, PlayerDamageTaken.Flat(DamageType.Fire, attack: true, flat));
        Assert.Equal(20, PlayerDamageTaken.Flat(DamageType.Chaos, attack: false, flat));
        Assert.Equal(0, PlayerDamageTaken.Flat(DamageType.Cold, attack: true, flat));

        // A blocked hit deals nothing by default, 10% with the Ritual gauntlets' implicit, all its elemental damage
        // with The Flawed Refuge's, 65% more with Glancing Blows, and never less than nothing.
        Assert.Equal(0, PlayerDamageTaken.BlockedPct(DamageType.Physical, With()));
        var blocked = With(("base_block_%_damage_taken", 10), ("block_%_damage_taken_from_elemental", 100));
        Assert.Equal(10, PlayerDamageTaken.BlockedPct(DamageType.Physical, blocked));
        Assert.Equal(110, PlayerDamageTaken.BlockedPct(DamageType.Cold, blocked));
        Assert.Equal(75, PlayerDamageTaken.BlockedPct(DamageType.Physical,
            With(("base_block_%_damage_taken", 10), ("keystone_glancing_blows", 1))));
        Assert.Equal(0, PlayerDamageTaken.BlockedPct(DamageType.Chaos, With(("base_block_%_damage_taken", -20))));

        // Mind Over Matter's 40% adds to the other shares, a typed share to its type, and each caps at 100%. Mana takes the
        // non-chaos part's share by the hit's proportions, rounded up, and never more mana than there is.
        var mom = With(("keystone_mana_shield", 1), ("base_damage_removed_from_mana_before_life_%", 10),
            ("lightning_damage_%_taken_from_mana_before_life", 30), ("chaos_damage_%_taken_from_mana_before_life", 100));
        Assert.Equal(50, PlayerDamageTaken.ManaBeforeLifePct(DamageType.Fire, mom));
        Assert.Equal(80, PlayerDamageTaken.ManaBeforeLifePct(DamageType.Lightning, mom));
        Assert.Equal(100, PlayerDamageTaken.ManaBeforeLifePct(DamageType.Chaos, mom));
        Assert.Equal(65, PlayerDamageTaken.FromMana(100, 0, Hit((DamageType.Fire, 50), (DamageType.Lightning, 50)), 1000, mom));
        Assert.Equal(50 + 20, PlayerDamageTaken.FromMana(100, 20, Hit((DamageType.Fire, 100), (DamageType.Chaos, 20)), 1000, mom));
        Assert.Equal(30, PlayerDamageTaken.FromMana(100, 0, Hit((DamageType.Fire, 100)), 30, mom));
        Assert.Equal(14, PlayerDamageTaken.FromMana(33, 0, Hit((DamageType.Physical, 33)), 1000, With(("keystone_mana_shield", 1))));
        Assert.Equal(0, PlayerDamageTaken.FromMana(100, 0, Hit((DamageType.Fire, 100)), 1000, With()));

        // With no ward or energy shield, the hit pool takes Mind Over Matter's 40 of a 100 hit from mana. The pool only takes
        // hits while the game's rules are active and the player lives.
        var player = new Player(new(1, "mana-before-life", 100, 100, 100, 100, null, []), 0, 1) { PlayerLife = 100 };
        var stats = new Dictionary<uint, int> { [Stat.KeystoneManaShield] = 1 };
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = global::Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        global::Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            player.PlayerMana = 100;
            Assert.Equal(60, global::Game.DamagePlayerHit(player, 100, 0, player.Clock.Elapsed.TotalSeconds, stats,
                Hit((DamageType.Physical, 100))));
            Assert.Equal(60u, player.PlayerMana);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    // A synthetic contact -- which is how a shrine's and a strongbox daemon's spell damage is committed -- goes through
    // the player's whole defence as a monster's hit does, and "damage taken as" splits the hit before any mitigation.
    // With 50% of Physical Damage from Hits taken as Fire and 50% Fire Resistance, 200 physical is 100 physical +
    // 100 fire at half = 150; the hit that ignored the shift took 200.
    [Fact]
    public async Task A_synthetic_spell_hit_takes_physical_damage_as_fire_and_meets_the_fire_resistance()
    {
        var plain = SyntheticPhysicalHit("plain", shifted: false, armour: 0, out var plainPlayer);
        var asFire = SyntheticPhysicalHit("asfire", shifted: true, armour: 0, out var asFirePlayer);
        Assert.Equal(200, plain.Damage);
        Assert.Equal(800u, plainPlayer.PlayerLife);
        Assert.Equal(150, asFire.Damage);
        Assert.Equal(850u, asFirePlayer.PlayerLife);
        Assert.Equal(150, (int)asFire.Taken[DamageType.Fire].Min + (int)asFire.Taken[DamageType.Physical].Min);

        await Watchers_Eye_starts_four_seconds_of_mana_recoup_from_a_hit_under_Clarity();
        await Clarity_Watchers_Eye_adds_seven_percent_of_maximum_mana_as_energy_shield();
    }

    // Armour is one decision for the hit but reduces only the physical part: with half of the physical hit taken as fire,
    // the fire part meets resistance alone, whatever the armour (a fire part is not armoured, and a part the shift made
    // physical would be).
    [Fact]
    public void A_synthetic_hit_with_a_fire_part_lets_armour_reduce_only_the_physical_part()
    {
        var hit = SyntheticPhysicalHit("armour", shifted: true, armour: 1_000_000, out var player);
        int expectedPhysical = DamageModel.Reduce(100, DamageModel.ArmourReduction(1_000_000, 100));
        Assert.Equal(100, (int)hit.Taken[DamageType.Fire].Min);
        Assert.Equal(expectedPhysical, (int)hit.Taken[DamageType.Physical].Min);
        Assert.Equal(100 + expectedPhysical, hit.Damage);
        Assert.Equal(1000u - (uint)hit.Damage, player.PlayerLife);
    }

    static Game.SyntheticEnemyHit SyntheticPhysicalHit(string name, bool shifted, int armour, out Player player)
    {
        TestEnvironment.OpenOwnSaves("synthetic-taken-as-" + name);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        try
        {
            var created = new Player(new(1, "synthetic-taken-as", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = { Name = "SyntheticTakenAs" + name },
                PlayerLife = 1000, PlayerMana = 100, Instance = new(), EvasionEntropy = 100,
                IncomingHitRandom = new Random(7),
                Pools = new(1, 1000, 100, 0, 0, Armour: armour, FireResistance: shifted && armour == 0 ? 50 : 0),
            };
            player = created;
            uint takenAs = Program.Stats.Key("physical_damage_taken_%_as_fire")!.Value;
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (shifted && ReferenceEquals(owner, created)) values[takenAs] = 50;
                return values;
            });
            Assert.True(Game.TrySyntheticEnemyHit(created, DamageType.Physical, 200, block: false, spell: true,
                suppress: false, out var hit));
            return hit;
        }
        finally
        {
            Program.SetGameLogic(previousLogic!);
            Game.CombatLifetime = previousLifetime;
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    // Chill reads the damage taken, so physical taken as cold chills; ignite reads the damage dealt, so fire taken as
    // physical still ignites on a critical strike (wiki Damage taken as, "Ailments and damage over time"), but only the
    // part that lands: avoided fire cannot ignite.
    [Fact]
    public void Ailments_read_the_shifted_damage_taken_but_ignite_reads_the_damage_dealt_that_lands()
    {
        var player = new Player(new(1, "taken-as", 100, 100, 100, 100, null, []), 0, 1);
        player.Pools = player.Pools with { Life = 1000 }; player.PlayerLife = 1000;
        var stats = new Dictionary<uint, int>();
        double now = player.Clock.Elapsed.TotalSeconds;

        var physical = Hit((DamageType.Physical, 100));
        global::Game.ApplyPlayerAilmentsLocked(player, physical, physical, false, false, now, stats);
        Assert.Equal(0, player.ElementalAilments.Snapshot(now).Chill);
        var asCold = Hit((DamageType.Physical, 50), (DamageType.Cold, 50));
        global::Game.ApplyPlayerAilmentsLocked(player, physical, asCold, false, false, now, stats);
        Assert.Equal(15, player.ElementalAilments.Snapshot(now).Chill);   // floor(50 × 0.05^0.4)

        player.ElementalAilments.Clear();
        var fire = Hit((DamageType.Fire, 100));
        global::Game.ApplyPlayerAilmentsLocked(player, fire, Hit((DamageType.Physical, 100)), true, false, now, stats);
        var ailments = player.ElementalAilments.Snapshot(now);
        Assert.True(ailments.Ignite);
        Assert.Equal(0, ailments.Chill);
        Assert.False(ailments.Freeze);

        player.ElementalAilments.Clear();
        var cold = Hit((DamageType.Cold, 50));
        var landed = PlayerDamageTaken.Landed(Hit((DamageType.Fire, 100), (DamageType.Cold, 50)), With(), [DamageType.Fire]);
        global::Game.ApplyPlayerAilmentsLocked(player, landed, cold, true, false, now, stats);
        Assert.False(player.ElementalAilments.Snapshot(now).Ignite);
        Assert.True(player.ElementalAilments.Snapshot(now).Chill > 0);
    }
}
