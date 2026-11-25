using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RationalDoctrineTests
{
    [Fact]
    public async Task Sublime_Vision_applies_the_Precision_final_multiplier_to_attack_and_spell_chances()
    {
        var uniques = new UniqueCatalog(Program.Items);
        var definition = Assert.IsType<UniqueCatalog.Definition>(uniques.Find("Sublime Vision"));
        var mod = Program.Items.Mods.ById("SublimeVisionPrecision")!;
        var jewel = Assert.IsType<Item>(Enumerable.Range(1, 512).Select(seed => uniques.Create(definition, new Random(seed)))
            .FirstOrDefault(candidate => candidate?.Mods.Any(roll => roll.Hash == mod.Hash) == true));
        var modifier = Assert.Single(jewel.Mods, roll => roll.Hash == mod.Hash);
        Assert.Equal(25, modifier.Values[0]);
        var control = jewel with { Mods = [.. jewel.Mods.Select(roll => roll.Hash != mod.Hash ? roll
            : roll with { Values = [0, .. roll.Values.Skip(1)] })] };
        var baseline = await Chances(control, active: true);
        var active = await Chances(jewel, active: true);
        Assert.Equal(await Chances(control, active: false), await Chances(jewel, active: false));
        Assert.InRange(active.Spell, (int)Math.Round(baseline.Spell * 1.25) - 1, (int)Math.Round(baseline.Spell * 1.25) + 1);
        Assert.InRange(active.Attack, (int)Math.Round(baseline.Attack * 1.25) - 1, (int)Math.Round(baseline.Attack * 1.25) + 1);
        Assert.True(active.Spell > baseline.Spell && active.Attack > baseline.Attack);
        Console.WriteLine($"Precision crit chances (spell, attack): {baseline} -> {active}");

        async Task<(int Spell, int Attack)> Chances(Item equipped, bool active)
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            uint main = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, main, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            fixture.Equip(("Ice Nova", 1), ("Double Strike", 1), ("Precision", 1));
            var gems = Game.GemSkillEffects(player).ToDictionary(skill => skill.Name);
            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            items.Add(PlayerInventories.PassiveJewels, slot, 0, equipped);
            var aura = gems["Precision"];
            if (active)
                Assert.True(await Game.ToggleAura(player, aura.Id, aura.Effect, aura.Name,
                    Game.SupportedReservationCost(player, aura.Id)!.Value, aura.Level, aura.Quality));
            Assert.Equal(active, Game.AffectingAuras(player).Any(a => a.SkillId == aura.Id));
            return (Game.SupportedCriticalChance(player, gems["Ice Nova"].Id),
                Game.SupportedCriticalChance(player, gems["Double Strike"].Id));
        }
    }

    static int Stat(Item item, string id) =>
        item.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id == id).Values[0];

    [Fact]
    public void Rational_Doctrine_exposes_its_ground_mods_and_attribute_rules()
    {
        var jewel = Game.CreateUniqueJewel("Rational Doctrine");
        Assert.NotNull(jewel);
        Assert.Equal(18, Stat(jewel!, "ConsecratedGroundStationarySTRHighestUnique__1"));
        Assert.Equal(25, Stat(jewel!, "ProfaneGroundCriticalStrikeINTHighestUnique__1"));
        Assert.Equal(4000, Stat(jewel!, "ConsecratedGroundLingersUnique__1"));
        Assert.Equal(4000, Stat(jewel!, "ProfaneGroundLingersUnique__1"));

        var stats = new Dictionary<uint, int>
        {
            [global::Stat.Strength] = 100, [global::Stat.Dexterity] = 50, [global::Stat.Intelligence] = 99,
            [FlaskGrounds.ProfaneChanceStat] = 25,
        };
        Assert.Equal(0, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));
        stats[global::Stat.Intelligence] = 100;
        Assert.Equal(25, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));
        stats[global::Stat.Intelligence] = 101;
        Assert.Equal(25, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));

        var player = new Player(new(1, "rational", 100, 100, 100, 100, null, []), 0, 1);
        player.FlaskGrounds.SetProfane(7, inside: true, alive: true, now: 1, () => 99);
        Assert.Equal(900, global::Game.CriticalChanceOn(player, 7, 500, null, new(900, null, null)));
        Assert.Equal(500, global::Game.CriticalChanceOn(player, 8, 500, null, new(900, null, null)));
    }

    [Fact]
    public void Profane_ground_applies_to_monsters_and_lingers_after_they_leave()
    {
        Assert.True(FlaskGrounds.LoadArt("data").ContainsKey(FlaskGrounds.Kind.ProfaneGround));
        Assert.Equal((ushort)1569, FlaskGrounds.ProfaneBuffRow);
        var made = FlaskGrounds.ProfaneGround((3, 4), now: 10);
        Assert.Equal(FlaskGrounds.Kind.ProfaneGround, made.Kind);
        Assert.Equal(10, made.Radius);
        Assert.Equal(14, made.Ends);
        var state = new FlaskGrounds.State();
        state.Configure(stationaryRadius: 0, lingerMs: 0, profaneLingerMs: 4000);
        const double now = 10;
        Assert.True(state.Add(new(FlaskGrounds.Kind.ProfaneGround, 10, 10, 10, now + 4)));

        var added = state.SetProfane(1, inside: true, alive: true, now, () => 42);
        Assert.NotNull(added);
        Assert.Equal((ushort)42, added!.Value.Buff);
        Assert.True(added.Value.Added);
        Assert.True(state.Profane(1));
        Assert.Null(state.SetProfane(1, inside: false, alive: true, now + 3.9, () => 43));
        Assert.True(state.Profane(1));
        var removed = state.SetProfane(1, inside: false, alive: true, now + 4, () => 43);
        Assert.NotNull(removed);
        Assert.Equal((ushort)42, removed!.Value.Buff);
        Assert.False(removed.Value.Added);
        Assert.False(state.Profane(1));

        var second = state.SetProfane(2, inside: true, alive: true, now, () => 44);
        Assert.NotNull(second);
        Assert.True(second!.Value.Added);
        var dead = state.SetProfane(2, inside: false, alive: false, now, () => 45);
        Assert.NotNull(dead);
        Assert.Equal((ushort)44, dead!.Value.Buff);
        Assert.False(dead.Value.Added);

        var consecrated = new FlaskGrounds.State();
        consecrated.Configure(stationaryRadius: 18, lingerMs: 4000);
        Assert.True(consecrated.Add(new(FlaskGrounds.Kind.ConsecratedGround, 10, 10, 18, 100)));
        Assert.True(consecrated.OnConsecrated(alive: true, (10, 10), now));
        Assert.True(consecrated.OnConsecrated(alive: true, (100, 100), now + 3.9));
        Assert.False(consecrated.OnConsecrated(alive: true, (100, 100), now + 4));
    }
}
