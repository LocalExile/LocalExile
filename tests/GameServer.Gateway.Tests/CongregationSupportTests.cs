using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    [Fact]
    public async Task Congregation_uses_the_linked_summons_limit_and_updates_its_panel()
    {
        var conn = SpellTotemExecutionTests.Connection("Congregation" + Guid.NewGuid().ToString("N"));
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        string path = Path.Combine(Path.GetTempPath(), $"congregation-{Guid.NewGuid():N}.db");
        using var wire = new Wire();
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        conn.Summons.Restored = conn.Golems.Restored = true;
        var items = Game.ItemsOf(conn);
        try
        {
            Item Gem(string name, int quality = 0) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!)
                with { Quality = (byte)quality };
            var host = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!);
            var skeleton = Gem("SkillGemSummonSkeletons");
            var congregation = Gem("SupportGemCongregation", 20);
            var spirit = Gem("SkillGemSummonRagingSpirit");
            var flameGolem = Gem("SkillGemSummonFlameGolem");
            var purity = ItemCatalog.Create(Program.Items.FindGem("Herald of Purity")!.Value.Type);
            var worn = items.Add(PlayerInventories.BodyArmour, 0, 0, host with
            {
                Sockets = [new(SocketColour.White, skeleton), new(SocketColour.White, congregation),
                    new(SocketColour.White, flameGolem), new(SocketColour.White, purity),
                    new(SocketColour.White, spirit)], Links = [4, 1],
            });
            ushort Source(string effect) => Game.GemSkillEffects(conn)
                .Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            int Published(ushort source, string stat) => Game.PlayerSkills(conn).Single(s => s.Id == source)
                .Stats!.Single(p => p.Key == Program.Stats.Key(stat)).Value;
            ushort skill = Source("SummonSkeletons"), unlinked = Source("SummonRagingSpirit");
            var supported = Game.SupportedSkill(conn, skill);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportCongregation");
            Assert.Equal(6, Published(skill, "number_of_skeletons_allowed"));
            Assert.Equal(20, Published(unlinked, "number_of_raging_spirits_allowed"));
            var definition = Game.SummonData.Find("SummonSkeletons")!;
            for (int i = 0; i < 7; i++)
                Assert.NotEqual(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, skill, definition, (100, 100)));
            Assert.Equal(6, conn.Summons.Fighters.Count);
            Assert.All(conn.Summons.Fighters.Values, fighter =>
            {
                var mods = Game.GolemModifiers(conn, fighter, fighter.Player);
                Assert.Equal(10, mods.Effects["base_movement_velocity_+%"]);
            });

            ushort golemSource = Source("SummonFlameGolem");
            conn.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 2;
            conn.PlayerStats[Program.Stats.Key("double_golem_limit")!.Value] = 1;
            Assert.Equal(7, Published(golemSource, "number_of_golems_allowed"));
            Assert.NotEqual(0u, await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, golemSource,
                Game.GolemData.Find("SummonFlameGolem")!, (100, 100)));
            Assert.Equal(7, Assert.Single(conn.Golems.All.Values).Limit);

            ushort puritySource = Source("HeraldOfPurity");
            Assert.Equal(5, Published(puritySource, "number_of_champions_of_light_allowed"));
            var aura = Game.GemSkillEffects(conn).Single(g => g.Id == puritySource);
            var cost = Game.SupportedReservationCost(conn, puritySource)!.Value;
            Assert.True(await Game.ToggleAura("test", wire.Stream, wire.Ciphers, conn, puritySource, aura.Effect,
                aura.Name, cost, aura.Level, aura.Quality));
            var victim = new SpawnedMonster("test", new(100, 100, 0), 100, 100, 1);
            for (int i = 0; i < 6; i++) Game.ReservationKill(new(conn, victim, "test", (100, 100)));
            await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
            Assert.Equal(5, conn.Reservations.Fighters.Values.Count(f => f.Definition.Kind == "Purity"));
            await Game.DisableReservations("test", wire.Stream, wire.Ciphers, conn, "HeraldOfPurity");

            items.Change(PlayerInventories.BodyArmour, worn.Id, item => item with { Links = [1, 1, 1, 1, 1] });
            Assert.Equal(skill, Source("SummonSkeletons"));
            Assert.Equal(5, Published(skill, "number_of_skeletons_allowed"));
            Assert.NotEqual(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, skill, definition, (100, 100)));
            Assert.Equal(5, conn.Summons.Fighters.Count);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
