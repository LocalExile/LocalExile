using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class RationalDoctrineTests
{
    [Fact]
    public async Task Sublime_Vision_modifiers_require_the_matching_aura()
    {
        await Haste_Sublime_Vision_applies_action_speed_to_paid_casts_while_active();
        await Sublime_Vision_anger_hatred_and_wrath_enable_their_alternate_ailments();
    }

    static async Task Haste_Sublime_Vision_applies_action_speed_to_paid_casts_while_active()
    {
        var ordinary = await Cast(false, true);
        var active = await Cast(true, true);
        var inactive = await Cast(true, false);
        Assert.Equal(1, ordinary.Factor, 6);
        Assert.Equal(1.1, active.Factor, 6);
        Assert.Equal(1, inactive.Factor, 6);
        Assert.InRange(active.Duration, ordinary.Duration / 1.1 - .04, ordinary.Duration / 1.1 + .04);
        Assert.True(active.Duration < ordinary.Duration);

        static async Task<(double Factor, double Duration)> Cast(bool withSpeed, bool active)
        {
            var clock = new AuraContactClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            EquipSublime(player, "Haste", [withSpeed ? 10 : 0, 1, 20]);
            fixture.Equip(("Haste", 1), ("Clarity", 1), ("Ice Nova", 18));
            var gems = Game.GemSkillEffects(player).ToDictionary(skill => skill.Name);
            var before = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                player.Walk.Teleport(100, 100, clock.At);
                if (active)
                {
                    var haste = gems["Haste"];
                    Assert.True(await Game.ToggleAura(player, haste.Id, haste.Effect, haste.Name,
                        Game.SupportedReservationCost(player, haste.Id)!.Value, haste.Level, haste.Quality));
                }
                var other = gems["Clarity"];
                Assert.False(await Game.ToggleAura(player, other.Id, other.Effect, other.Name,
                    Game.SupportedReservationCost(player, other.Id)!.Value, other.Level, other.Quality));
                double factor = Game.PlayerActionFactor(player);
                double start = clock.At;
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, gems["Ice Nova"].Id, (100, 100), (100, 100)));
                Assert.True(player.PlayerMana < mana);
                return (factor, clock.At - start);
            }
            finally { Game.AreaUpdateContext.Value = before; }
        }
    }

    static void EquipSublime(Player player, string auraName, int[] values)
    {
        var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Sublime Vision", new Random(8)));
        var mod = Program.Items.Mods.ById("SublimeVision" + auraName)!;
        const byte slot = 1;
        player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
        Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0,
            jewel with { Mods = [new(ModSection.Explicit, mod.Hash, values)] });
    }

    sealed class AuraContactClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    static async Task Sublime_Vision_anger_hatred_and_wrath_enable_their_alternate_ailments()
    {
        foreach (var (name, element, kind) in new[]
        {
            ("Anger", DamageType.Fire, Game.AlternateKind.Scorch),
            ("Hatred", DamageType.Cold, Game.AlternateKind.Brittle),
            ("Wrath", DamageType.Lightning, Game.AlternateKind.Sap),
        })
        {
            Assert.Equal(0, await Hit(name, element, kind, false));
            Assert.True(await Hit(name, element, kind, true) > 0);
        }

        static async Task<double> Hit(string name, DamageType element, Game.AlternateKind kind, bool active)
        {
            var clock = new AuraContactClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            EquipSublime(player, name, [1, 1, 20]);
            fixture.Equip((name, 1), ("Ice Nova", 18));
            var gems = Game.GemSkillEffects(player).ToDictionary(skill => skill.Name);
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(player, owner)) values[global::Stat.GlobalCannotCrit] = 1;
                return values;
            });
            using var witness = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, packet) =>
                ReferenceEquals(player, owner) ? DamagePacket.Of(element, 4000, 4000) : packet);
            const uint id = 906;
            var target = new SpawnedMonster("alternate ailment witness", new(104, 100, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = true };
            player.Area.Monsters[id] = target;
            player.Area.Grid.Set(new(id), (104, 100));
            var before = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                player.Walk.Teleport(100, 100, clock.At);
                var aura = gems[name];
                if (active)
                    Assert.True(await Game.ToggleAura(player, aura.Id, aura.Effect, aura.Name,
                        Game.SupportedReservationCost(player, aura.Id)!.Value, aura.Level, aura.Quality));
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, gems["Ice Nova"].Id, (100, 100), (104, 100), id));
                Assert.True(player.PlayerMana < mana);
                Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                return target.RuleAilments.Magnitude((int)kind, clock.At);
            }
            finally { Game.AreaUpdateContext.Value = before; }
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
