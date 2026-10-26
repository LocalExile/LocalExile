using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Flask charge gains (FlaskCharges): poewiki's Flask page ("Flask charges") with the monster rarity mods of Mods.dat.
public class FlaskChargesTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ModCatalog> Mods = new(() => new ItemCatalog(Data()).Mods);
    static Dictionary<string, int> Local(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);
    static Func<string, int> Character(params (string Stat, int Value)[] stats) => name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    [Fact]
    public void Kills_give_1_3_5_6_and_11_charges_by_rarity_from_the_monster_mods()
    {
        // Mods.dat MonsterMagic6 / MonsterRare6 / MonsterUnique6: +250 / +500 / +1000% flask charges granted.
        Assert.Equal(1, FlaskCharges.KillCharges(MonsterRarity.Normal, Mods.Value));
        Assert.Equal(3.5, FlaskCharges.KillCharges(MonsterRarity.Magic, Mods.Value));
        Assert.Equal(6, FlaskCharges.KillCharges(MonsterRarity.Rare, Mods.Value));
        Assert.Equal(11, FlaskCharges.KillCharges(MonsterRarity.Unique, Mods.Value));
    }

    [Fact]
    public void Increased_charges_gained_and_charge_recovery_multiply()
    {
        Assert.Equal(1, FlaskCharges.GainMultiplier(Local(), Character()));
        // 20% increased flask charges gained (gear) and Perpetual's 50% increased charge recovery: 1.2 × 1.5.
        Assert.Equal(1.8, FlaskCharges.GainMultiplier(Local(("local_charges_added_+%", 50)), Character(("flask_charges_gained_+%", 20))), 6);
    }

    [Fact]
    public void Partial_charges_are_kept_to_two_decimals()
    {
        var carry = new FlaskCharges.Carry();
        // Three normal kills at 21% increased charges gained: 1.21 each.
        Assert.Equal(1, carry.Take(7, 1.21));
        Assert.Equal(1, carry.Take(7, 1.21));
        Assert.Equal(0.42, carry.Partial(7), 6);
        Assert.Equal(1, carry.Take(7, 1.21));
        Assert.Equal(0.63, carry.Partial(7), 6);
        // A magic kill's 3.5 on top: 4.13, 4 now.
        Assert.Equal(4, carry.Take(7, 3.5));
        Assert.Equal(0.13, carry.Partial(7), 6);
        // Another flask keeps its own.
        Assert.Equal(0, carry.Take(8, 0.5));
        Assert.Equal(0.5, carry.Partial(8));
    }

    [Fact]
    public void Crit_charges_share_one_cooldown_of_0_132_seconds()
    {
        var carry = new FlaskCharges.Carry();
        Assert.True(carry.TryCrit(10));
        Assert.False(carry.TryCrit(10.1));
        Assert.False(carry.TryCrit(10.131));
        Assert.True(carry.TryCrit(10.132));
        // At most ~7.5 per second.
        int given = 0;
        for (double t = 20; t < 21; t += 0.01) if (carry.TryCrit(t)) given++;
        Assert.Equal(8, given);
    }

    [Fact]
    public void A_crit_gives_the_flasks_whose_chance_rolls_one_charge_with_the_shared_cooldown()
    {
        var carry = new FlaskCharges.Carry();
        // Flask 1: Surgeon's 35% chance; flask 2: none; flask 3: 20% but full. Rolls below the chance hit.
        (uint, int, bool)[] bar = [(1, 35, false), (2, 0, false), (3, 20, true)];
        var rolls = new Queue<int>([10, 5]);
        Assert.Equal(new Dictionary<uint, double> { [1] = 1, [3] = 1 }, FlaskCharges.OnCrit(bar, (0, 0), carry, 1.0, _ => rolls.Dequeue()));
        // Within the cooldown: nothing, and no roll.
        Assert.False(carry.CritReady(1.1));
        Assert.Empty(FlaskCharges.OnCrit(bar, (0, 0), carry, 1.1, _ => throw new InvalidOperationException()));
        // A roll above the chance: no charge.
        Assert.Empty(FlaskCharges.OnCrit(bar, (0, 0), carry, 2.0, _ => 99));
        // The character's chance (one charge) and sure charges go to the first flask that is not full.
        Assert.Equal(new Dictionary<uint, double> { [2] = 3 }, FlaskCharges.OnCrit([(2, 0, false), (4, 0, false)], (50, 2), carry, 3.0, _ => 0));
        Assert.Equal(new Dictionary<uint, double> { [4] = 1 }, FlaskCharges.OnCrit([(2, 0, true), (4, 0, false)], (0, 1), carry, 4.0, _ => 99));
        // No source of crit charges: the cooldown does not start.
        var idle = new FlaskCharges.Carry();
        Assert.Empty(FlaskCharges.OnCrit([(1, 0, false)], (0, 0), idle, 5.0, _ => 0));
        Assert.True(idle.CritReady(5.0));
        Assert.True(idle.TryCrit(5.0));
    }

    [Fact]
    public void Dealing_a_crit_reads_the_deal_stats_not_the_take_ones()
    {
        static Func<string, int> Character(params (string Stat, int Value)[] stats) => name => stats.Where(s => s.Stat == name).Sum(s => s.Value);
        // "Flasks gain a Charge when you take a Critical Strike": not for the player's own crits.
        Assert.Equal((0, 0), FlaskCharges.CritSources(Character(("gain_flask_charge_when_crit_%", 100), ("gain_flask_charge_when_crit_amount", 3)), precision: false));
        // "#% chance to gain a Flask Charge when you deal a Critical Strike", "Gain # Flask Charges when you deal a Critical Strike".
        Assert.Equal((30, 0), FlaskCharges.CritSources(Character(("gain_flask_chance_on_crit_%", 30)), false));
        Assert.Equal((0, 2), FlaskCharges.CritSources(Character(("recharge_flasks_on_crit", 2)), false));
        // "… while affected by Precision": only with Precision running.
        var precision = Character(("recharge_flasks_on_crit_while_affected_by_precision", 1));
        Assert.Equal((0, 0), FlaskCharges.CritSources(precision, precision: false));
        Assert.Equal((0, 1), FlaskCharges.CritSources(precision, precision: true));
        // Frenzy charges do not exist here.
        Assert.Equal((0, 0), FlaskCharges.CritSources(Character(("gain_flask_charge_on_crit_chance_%_while_at_maximum_frenzy_charges", 50)), false));
    }

    [Fact]
    public void Flasks_gain_charges_every_3_seconds_by_kind()
    {
        static Func<string, int> Character(params (string Stat, int Value)[] stats) => name => stats.Where(s => s.Stat == name).Sum(s => s.Value);
        var c = Character(("flask_charges_recovered_per_3_seconds", 1), ("utility_flask_charges_recovered_per_3_seconds", 2),
            ("life_flask_charges_recovered_per_3_seconds", 3), ("life_flask_charges_recovered_per_3_seconds_on_low_life", 4),
            ("mana_flasks_gain_X_charges_every_3_seconds", 5));
        Assert.Equal(1 + 2, FlaskCharges.PerThreeSeconds(c, life: false, mana: false, utility: true, lowLife: true));
        Assert.Equal(1 + 3, FlaskCharges.PerThreeSeconds(c, life: true, mana: false, utility: false, lowLife: false));
        Assert.Equal(1 + 3 + 4, FlaskCharges.PerThreeSeconds(c, life: true, mana: false, utility: false, lowLife: true));
        Assert.Equal(1 + 5, FlaskCharges.PerThreeSeconds(c, life: false, mana: true, utility: false, lowLife: true));
        Assert.Equal(1 + 3 + 5, FlaskCharges.PerThreeSeconds(c, life: true, mana: true, utility: false, lowLife: false));   // a hybrid flask
    }

    [Fact]
    public void A_flask_equipped_in_a_hostile_area_starts_empty()
    {
        var flask = new Item(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = 60 };
        Assert.Equal(0, FlaskCharges.WhenEquipped(flask, safeArea: false).Charges);
        Assert.Same(flask, FlaskCharges.WhenEquipped(flask, safeArea: true));
    }
}
