using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FlasksTests
{
    // Small Life Flask (Flasks.dat, ComponentCharges): 70 life over 3 s, 7 of 21 charges per use.
    static readonly Flasks.Flask SmallLife = new(70, 0, 3.0, 7, 21);
    static Item LifeFlask(int charges) => new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = charges };

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void The_small_flasks_come_from_Flasks_dat_and_ComponentCharges()
    {
        var catalog = new ItemCatalog(Data());

        Assert.Equal(SmallLife, catalog.FlaskOf(catalog.Find("Metadata/Items/Flasks/FlaskLife1")!));
        Assert.Equal(new Flasks.Flask(0, 50, 3.0, 6, 24), catalog.FlaskOf(catalog.Find("Metadata/Items/Flasks/FlaskMana1")!));
        Assert.Null(catalog.FlaskOf(catalog.Row(384)!));   // Driftwood Wand
    }

    [Fact]
    public void A_flask_buff_carries_its_duration_as_seconds_and_its_flask_bar_cell_as_source_skill()
    {
        // flask_effect_life for the flask in cell 1: flags bit1, f32 3.0 (0x40400000) at +0x68, not 3000 milliseconds;
        // source skill 0x0001 (the cell); then the recovery per minute and the empty list.
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0002" + "0005" + "00001000" + "0001" + "02" + "40400000" + "00000578" + "00000000",
            Convert.ToHexString(Flasks.Buff(0x1000, 2, 5, 1, 3.0, [1400])));
        // Without a duration (Game_FLASK_BUFF_DURATION=0) the client keeps +infinity.
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0002" + "0079" + "00001000" + "0002" + "00" + "00000000",
            Convert.ToHexString(Flasks.Buff(0x1000, 2, 121, 2, null)));
    }

    [Fact]
    public void A_use_spends_its_charges_and_needs_enough_of_them()
    {
        Assert.Equal(14, Flasks.Spend(LifeFlask(21), SmallLife)!.Charges);
        Assert.Equal(0, Flasks.Spend(LifeFlask(7), SmallLife)!.Charges);
        Assert.Null(Flasks.Spend(LifeFlask(6), SmallLife));
    }

    [Fact]
    public void Charges_come_back_up_to_the_maximum()
    {
        // A town fills every flask (int.MaxValue); a gain never goes past the maximum.
        Assert.Equal(21, Flasks.Gain(LifeFlask(3), SmallLife, int.MaxValue).Charges);
        Assert.Equal(21, Flasks.Gain(LifeFlask(20), SmallLife, 6).Charges);
        Assert.Equal(7, Flasks.Gain(LifeFlask(1), SmallLife, 6).Charges);
    }

    static Flasks.Effect Life(byte slot, double started, ushort buff = 1) =>
        new(slot, buff, 5, SmallLife.LifePerUse / SmallLife.Seconds, 0, started, started + SmallLife.Seconds);

    [Fact]
    public void A_flask_recovers_evenly_over_its_time_and_then_ends()
    {
        var effects = new Flasks.Effects();
        effects.Start(Life(0, started: 10));

        var (life, mana, ended) = effects.Advance(10, 11.5);
        Assert.Equal(35, life, 6);
        Assert.Equal(0, mana);
        Assert.Empty(ended);

        // A late tick past the end only counts up to the end.
        (life, _, ended) = effects.Advance(11.5, 14);
        Assert.Equal(35, life, 6);
        Assert.Equal(0, Assert.Single(ended).Slot);
        Assert.Equal(0, effects.Advance(14, 15).Life);
    }

    [Fact]
    public void Recovery_flasks_queue_the_highest_rate_first()
    {
        // poewiki "Recovery from flasks": "Recovery flasks queue up if multiple flasks are active. The effect with the
        // highest regeneration rate is always applied first."
        var effects = new Flasks.Effects();
        var small = Life(0, started: 0, buff: 1);                              // 70 over 3 s: 23.3 per second
        var medium = new Flasks.Effect(1, 2, 5, 150 / 3.5, 0, 0.5, 4);         // 150 over 3.5 s: 42.9 per second
        Assert.Null(effects.Start(small));
        Assert.Equal(70.0 / 3 * 0.5, effects.Advance(0, 0.5).Life, 6);
        effects.Start(medium);
        // The faster one recovers now; the small one waits with its 2.5 s.
        Assert.Equal(150 / 3.5, effects.Advance(0.5, 1.5).Life, 6);
        Assert.Equal(2.5, effects.Remaining(small), 6);
        // The medium one ends at 4 s; then the small one goes on for its 2.5 s, to 6.5 s.
        var (life, _, ended) = effects.Advance(1.5, 5);
        Assert.Equal(150 / 3.5 * 2.5 + 70.0 / 3, life, 6);
        Assert.Equal([medium], ended);
        (life, _, ended) = effects.Advance(5, 10);
        Assert.Equal(70.0 / 3 * 1.5, life, 6);
        Assert.Equal([small], ended);
    }

    [Fact]
    public void Life_and_mana_queues_are_apart_and_some_effects_do_not_queue()
    {
        var effects = new Flasks.Effects();
        effects.Start(Life(0, started: 0, buff: 1));
        effects.Start(Life(1, started: 0, buff: 2));
        effects.Start(new Flasks.Effect(4, 3, 9, 0, 50 / 3.0, 0, 3));                       // mana: its own queue
        effects.Start(new Flasks.Effect(2, 0, 0, 8, 0, 0, 10));                             // a suffix's extra life: no queue
        effects.Start(new Flasks.Effect(3, 4, 9, 0, 10, 0, 3, ManaStays: true, NoQueue: true));   // Enduring
        var (life, mana, _) = effects.Advance(0, 1);
        Assert.Equal(70.0 / 3 + 8, life, 6);        // one small life flask at a time, and the extra life
        Assert.Equal(50 / 3.0 + 10, mana, 6);       // the mana flask and the Enduring one together
        // A second use of the same flask queues another effect.
        effects.Start(Life(0, started: 1, buff: 5));
        Assert.Equal(70.0 / 3 + 8, effects.Advance(1, 2).Life, 6);
    }

    [Fact]
    public void Effects_can_be_ended_early()
    {
        var effects = new Flasks.Effects();
        effects.Start(Life(0, started: 0));
        effects.Start(new Flasks.Effect(4, 2, 9, 0, 50 / 3.0, 0, 3));

        var full = effects.End(e => e.LifePerSecond > 0);

        Assert.Equal(0, Assert.Single(full).Slot);
        var (life, mana, _) = effects.Advance(0, 3);
        Assert.Equal(0, life);
        Assert.Equal(50, mana, 6);
    }
}
