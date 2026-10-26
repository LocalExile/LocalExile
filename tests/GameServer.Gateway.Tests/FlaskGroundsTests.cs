namespace GameServer.Gateway.Tests;

// What Stibnite, Sulphur and Basalt Flasks make on use (FlaskGrounds): smoke cloud (Blind), consecrated ground, taunt.
public class FlaskGroundsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));

    static List<(FlaskGrounds.Kind, int)> OnUse(string flask)
    {
        var item = ItemCatalog.Create(Catalog.Value.Find(flask)!);
        return [.. FlaskGrounds.OnUse(ItemModStats.Local(item, Catalog.Value.Mods))];
    }

    [Fact]
    public void The_flasks_implicits_make_their_grounds_with_the_tables_radii()
    {
        // BaseItemTypes implicits: UtilityFlaskSmokeCloud 16, UtilityFlaskConsecrate 18, UtilityFlaskTaunt_ 30.
        Assert.Equal([(FlaskGrounds.Kind.SmokeCloud, 16)], OnUse("Stibnite Flask"));
        Assert.Equal([(FlaskGrounds.Kind.ConsecratedGround, 18)], OnUse("Sulphur Flask"));
        Assert.Equal([(FlaskGrounds.Kind.Taunt, 30)], OnUse("Basalt Flask"));
        Assert.Empty(OnUse("Quicksilver Flask"));
        // Aquamarine's chilled ground is not made (monsters cannot be chilled in step with the client yet).
        Assert.Empty(OnUse("Aquamarine Flask"));
    }

    [Fact]
    public void The_ground_art_comes_from_GroundEffects()
    {
        var art = FlaskGrounds.LoadArt(Data());
        Assert.Equal(("Metadata/Effects/Spells/ground_effects_v2/smoke_blind/smoke_blind.ao", 120), art[FlaskGrounds.Kind.SmokeCloud]);
        Assert.Equal(("Metadata/Effects/Spells/ground_effects_v2/consecrated/consecrated_01.ao", 130), art[FlaskGrounds.Kind.ConsecratedGround]);
    }

    [Fact]
    public void Monsters_in_the_smoke_are_blinded_20_percent_less_accuracy_and_evasion()
    {
        var state = new FlaskGrounds.State();
        Assert.True(state.Add(new FlaskGrounds.Ground(FlaskGrounds.Kind.SmokeCloud, 100, 100, 16, Ends: 8)));
        Assert.True(state.Covers(FlaskGrounds.Kind.SmokeCloud, (110, 112)));    // 15.6 cells
        Assert.False(state.Covers(FlaskGrounds.Kind.SmokeCloud, (112, 112)));   // 16.97 cells
        Assert.False(state.Covers(FlaskGrounds.Kind.ConsecratedGround, (100, 100)));

        ushort next = 40;
        Assert.Equal((ushort)40, state.SetBlinded(0x3000, true, () => next++)!.Value.Buff);
        Assert.Null(state.SetBlinded(0x3000, true, () => next++));
        Assert.Equal(800, state.Accuracy(0x3000, 1000));
        Assert.Equal(160, state.Evasion(0x3000, 200));
        Assert.Equal(1000, state.Accuracy(0x3001, 1000));   // another monster outside
        Assert.Equal(((ushort)40, false), state.SetBlinded(0x3000, false, () => next++));
        Assert.Equal(1000, state.Accuracy(0x3000, 1000));
    }

    [Fact]
    public void On_consecrated_ground_the_player_regenerates_5_percent_of_life_per_second()
    {
        var state = new FlaskGrounds.State();
        state.Add(new FlaskGrounds.Ground(FlaskGrounds.Kind.ConsecratedGround, 50, 50, 18, Ends: 8));
        Assert.Empty(state.PlayerStats());
        Assert.True(state.SetConsecrated(true, () => 7)!.Value.Added);
        var stats = state.PlayerStats();
        var names = new StatCatalog(Data());
        var raw = UtilityFlasks.RawStats(stats, names).ToDictionary(r => names.Name(r.Key), r => r.Value);
        // 300% per minute of 1000 maximum life: 3000 per minute, 50 per second.
        Assert.Equal(3000, PanelStats.LifePerMinute(name => raw.GetValueOrDefault(name), 1000));
        Assert.Equal(-50, raw["curse_effect_on_self_+%"]);
        Assert.Equal(((ushort)7, false), state.SetConsecrated(false, () => 8));
        Assert.Empty(state.PlayerStats());
    }

    [Fact]
    public void Grounds_end_with_their_time_and_the_ticker_stops_when_nothing_is_left()
    {
        var state = new FlaskGrounds.State();
        Assert.True(state.Add(new FlaskGrounds.Ground(FlaskGrounds.Kind.SmokeCloud, 0, 0, 16, Ends: 8, Object: 0x3100)));
        Assert.False(state.Add(new FlaskGrounds.Ground(FlaskGrounds.Kind.Taunt, 0, 0, 30, Ends: 3)));   // the ticker runs already
        Assert.False(state.StopTickerIfIdle());
        Assert.Equal(FlaskGrounds.Kind.Taunt, Assert.Single(state.Expire(3)).Kind);
        Assert.Equal((uint)0x3100, Assert.Single(state.Expire(8)).Object);
        state.SetBlinded(0x3000, true, () => 1);
        Assert.False(state.StopTickerIfIdle());   // a blinded monster still has its buff to lose
        state.SetBlinded(0x3000, false, () => 2);
        Assert.True(state.StopTickerIfIdle());
        Assert.True(state.Add(new FlaskGrounds.Ground(FlaskGrounds.Kind.SmokeCloud, 0, 0, 16, Ends: 20)));   // a new one starts one
    }
}
