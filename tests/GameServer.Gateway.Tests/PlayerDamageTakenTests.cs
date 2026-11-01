using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PlayerDamageTakenTests
{
    static Func<string, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    static DamagePacket Hit(params (DamageType Type, int Amount)[] parts) =>
        parts.Aggregate(DamagePacket.Empty, (hit, p) => hit.Add(p.Type, new DamageRange(p.Amount, p.Amount)));

    // Each type shifts its share of its original amount once, in any direction; the source keeps the rest, and a total
    // over 100% adds damage instead of being scaled down. After mitigation each type takes its flat damage, its increased
    // damage taken and, when blocked, its share; mana takes its share of what reaches Life.
    [Fact]
    public void A_hit_shifts_once_then_takes_flat_increased_blocked_and_mana_before_life_damage()
    {
        Assert.All(PlayerDamageTaken.StatNames, name => Assert.NotNull(Program.Stats.Key(name)));

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

        // Generic, typed and elemental increases add; chaos is not elemental.
        var increased = With(("base_damage_taken_+%", 10), ("lightning_damage_taken_+%", 6), ("elemental_damage_taken_+%", -5));
        Assert.Equal(11, PlayerDamageTaken.IncreasedPct(DamageType.Lightning, increased));
        Assert.Equal(5, PlayerDamageTaken.IncreasedPct(DamageType.Fire, increased));
        Assert.Equal(10, PlayerDamageTaken.IncreasedPct(DamageType.Chaos, increased));

        // "-7 Physical Damage taken from Attack Hits" counts only for attacks; fire has its own flat stat.
        var flat = With(("physical_attack_damage_taken_+", -7), ("physical_damage_taken_+", -3), ("fire_damage_taken_+", -15));
        Assert.Equal(-10, PlayerDamageTaken.Flat(DamageType.Physical, attack: true, flat));
        Assert.Equal(-3, PlayerDamageTaken.Flat(DamageType.Physical, attack: false, flat));
        Assert.Equal(-15, PlayerDamageTaken.Flat(DamageType.Fire, attack: true, flat));
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

        // With no ward or energy shield, the hit pool takes Mind Over Matter's 40 of a 100 hit from mana.
        var conn = new ClientState(new(1, "mana-before-life", 100, 100, 100, 100, null, []), 0, 1);
        var stats = new Dictionary<uint, int> { [Program.Stats.Key("keystone_mana_shield")!.Value] = 1 };
        lock (conn.Monsters)
        {
            conn.PlayerMana = 100;
            Assert.Equal(60, global::Game.DamagePlayerHit(conn, 100, 0, conn.Clock.Elapsed.TotalSeconds, stats,
                Hit((DamageType.Physical, 100))));
            Assert.Equal(60u, conn.PlayerMana);
        }
    }

    // Chill reads the damage taken, so physical taken as cold chills; ignite reads the damage dealt, so fire taken as
    // physical still ignites on a critical strike (wiki Damage taken as, "Ailments and damage over time").
    [Fact]
    public void Ailments_read_the_shifted_damage_taken_but_ignite_reads_the_damage_dealt()
    {
        var conn = new ClientState(new(1, "taken-as", 100, 100, 100, 100, null, []), 0, 1);
        lock (conn.Monsters) { conn.Pools = conn.Pools with { Life = 1000 }; conn.PlayerLife = 1000; }
        var stats = new Dictionary<uint, int>();
        double now = conn.Clock.Elapsed.TotalSeconds;

        var physical = Hit((DamageType.Physical, 100));
        lock (conn.Monsters) global::Game.ApplyPlayerAilmentsLocked(conn, physical, physical, false, false, now, stats);
        Assert.Equal(0, conn.ElementalAilments.Snapshot(now).Chill);
        var asCold = Hit((DamageType.Physical, 50), (DamageType.Cold, 50));
        lock (conn.Monsters) global::Game.ApplyPlayerAilmentsLocked(conn, physical, asCold, false, false, now, stats);
        Assert.Equal(15, conn.ElementalAilments.Snapshot(now).Chill);   // floor(50 × 0.05^0.4)

        conn.ElementalAilments.Clear();
        var fire = Hit((DamageType.Fire, 100));
        lock (conn.Monsters) global::Game.ApplyPlayerAilmentsLocked(conn, fire, Hit((DamageType.Physical, 100)), true, false, now, stats);
        var ailments = conn.ElementalAilments.Snapshot(now);
        Assert.True(ailments.Ignite);
        Assert.Equal(0, ailments.Chill);
        Assert.False(ailments.Freeze);
    }
}
