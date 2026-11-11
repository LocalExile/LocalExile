using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task AvianSharingFixture(ClientState template, ModuleLifetime lifetime)
    {
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var priorContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        using var sourceWire = new Wire();
        using var receiverWire = new Wire();
        var instance = new InstanceState();
        var names = new List<string>();
        var uniques = new UniqueCatalog(Program.Items);
        ClientState Make(string label, Wire wire, bool nest)
        {
            string name = label + Guid.NewGuid().ToString("N");
            names.Add(name);
            PlayerInventories.SetClass(name, 1);
            CharacterSaves.Write(name, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                Experience: CharacterStats.ExperienceFor(100)));
            var conn = new ClientState(template.Layout, 1, 1)
            {
                Character = name, CharacterId = Guid.NewGuid(), Account = label, ClassRow = 1,
                League = "Standard", LeagueForm = "Standard", Instance = instance,
                PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
                Wire = (label, wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0), PlayerLife = 1,
            };
            var items = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                         PlayerInventories.Helmet, PlayerInventories.BodyArmour, PlayerInventories.Gloves,
                         PlayerInventories.Boots, 5, 6, 7, 10 })
                foreach (var old in items.Items(row)) items.Remove(row, old.Id);
            items.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            items.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            items.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            items.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Saqawal's Talons", new Random(504))!);
            if (nest) items.Add(PlayerInventories.BodyArmour, 0, 0, uniques.Create("Saqawal's Nest", new Random(504))!);
            else
            {
                items.Add(PlayerInventories.Gloves, 0, 0, uniques.Create("Saqawal's Winds", new Random(504))!);
                items.Add(PlayerInventories.Weapon1, 0, 0, uniques.Create("Dying Breath", new Random(504))!);
            }
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            conn.Walk.Teleport(nest ? 100 : 101, 100, schedule.At);
            return conn;
        }
        var source = Make("AvianSender", sourceWire, true);
        var receiver = Make("AvianReceiver", receiverWire, false);
        try
        {
            int Stat(ClientState c, string name) => Program.Stats.Key(name) is uint key
                ? Game.CharacterPlayerStats(c).GetValueOrDefault(key) : 0;
            async Task Toggle(ClientState c, Wire wire)
            {
                var skill = Assert.Single(Game.GemSkillEffects(c), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheAvian");
                Assert.True(await Game.ToggleAura("avian-sharing", wire.Stream, wire.Ciphers, c, skill.Id, skill.Effect,
                    skill.Name, Game.SupportedReservationCost(c, skill.Id)!.Value, skill.Level, skill.Quality));
            }
            void Publish(int x = 100)
            {
                var auras = Game.SharedAvianAuras(source);
                Assert.Single(auras);
                instance.PublishPlayer(new(source.PeerId, 0, null!, x, 100, source.PlayerLife, source) { Auras = auras });
                // Another equal sender cannot stack the same received family a second time.
                instance.PublishPlayer(new(source.PeerId + 1000, 0, null!, x, 100, source.PlayerLife, source) { Auras = auras });
                Game.WithOwnAuraChanges(receiver, () => []);
            }
            var golem = new GolemState.Golem(0xA720, null!, new(0, "", "Stone", "", 0, [], false, false, false), 1);
            int Minion(ClientState c, string name)
            {
                golem.Auras = Game.MinionAuraStats(c, golem);
                return Game.GolemModifiers(c, golem, Game.CharacterPlayerStats(c)).Effects[name];
            }
            int baseMove = receiver.Pools.MovementVelocityPct;
            int baseCold = Stat(receiver, "spell_minimum_added_cold_damage");
            await Toggle(source, sourceWire);
            Publish();
            Assert.Equal(20, Stat(source, "chance_to_deal_double_damage_%"));
            Assert.Equal(40, Minion(source, "chance_to_deal_double_damage_%"));
            Assert.Equal(20, Stat(receiver, "chance_to_deal_double_damage_%"));
            Assert.Equal(20, Stat(receiver, "minion_chance_to_deal_double_damage_%"));
            Assert.Equal(1, Stat(receiver, "has_avians_might"));
            Assert.Equal(0, Stat(receiver, "has_avians_flight"));
            Assert.Equal(40, Minion(receiver, "chance_to_deal_double_damage_%"));
            Assert.True(Stat(receiver, "spell_minimum_added_cold_damage") > baseCold);
            Assert.Equal(baseMove, receiver.Pools.MovementVelocityPct);
            // Own weaker Might and two stronger received Might sources still produce one strongest player buff.
            await Toggle(receiver, receiverWire);
            Assert.Equal(20, Stat(receiver, "chance_to_deal_double_damage_%"));
            Assert.Equal(40, Minion(receiver, "chance_to_deal_double_damage_%"));
            await Toggle(receiver, receiverWire);
            Assert.Equal(20, Stat(receiver, "chance_to_deal_double_damage_%"));
            schedule.At = source.Reservations.AvianDeadline;
            Game.ReconcileOwnAuras(source, validateSkills: true);
            Publish();
            Assert.Equal(0, Stat(receiver, "chance_to_deal_double_damage_%"));
            Assert.Equal(0, Stat(receiver, "has_avians_might"));
            Assert.Equal(1, Stat(receiver, "has_avians_flight"));
            Assert.Equal(20, Stat(receiver, "minion_movement_speed_+%"));
            Assert.Equal(baseMove + 20, receiver.Pools.MovementVelocityPct);
            Assert.Equal(40, Minion(receiver, "base_movement_velocity_+%"));
            Assert.Equal(baseCold, Stat(receiver, "spell_minimum_added_cold_damage"));
            Publish(1000);
            Assert.Empty(receiver.ReceivedAuras);
            Assert.Equal(baseMove, receiver.Pools.MovementVelocityPct);
            Assert.Equal(0, Stat(receiver, "has_avians_flight"));
            Publish();
            Assert.NotEmpty(receiver.ReceivedAuras);
            source.Closed = true;
            Game.WithOwnAuraChanges(receiver, () => []);
            Assert.Empty(receiver.ReceivedAuras);
            Assert.Equal(0, Stat(receiver, "minion_movement_speed_+%"));
            Assert.Empty(source.PlayerStats);
            Assert.Empty(receiver.PlayerStats);
        }
        finally
        {
            source.Closed = receiver.Closed = true;
            Game.AreaUpdateContext.Value = priorContext;
            foreach (string name in names) PlayerInventories.Forget(name);
        }
    }
}
