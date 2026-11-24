using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    [Fact]
    public async Task Congregation_uses_the_linked_summons_limit_and_updates_its_panel()
    {
        var player = SpellTotemExecutionTests.PlayerOf("Congregation" + Guid.NewGuid().ToString("N"));
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        string path = Path.Combine(Path.GetTempPath(), $"congregation-{Guid.NewGuid():N}.db");
        using var wire = new Wire();
        player.Connection.Session = new("test", wire.Stream, wire.Ciphers);
        player.Connection.PlayerSent = true;
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        player.Summons.Restored = player.Golems.Restored = true;
        var items = Game.ItemsOf(player);
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
            ushort Source(string effect) => Game.GemSkillEffects(player)
                .Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            int Published(ushort source, string stat) => Game.PlayerSkills(player).Single(s => s.Id == source)
                .Stats!.Single(p => p.Key == Program.Stats.Key(stat)).Value;
            ushort skill = Source("SummonSkeletons"), unlinked = Source("SummonRagingSpirit");
            var supported = Game.SupportedSkill(player, skill);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportCongregation");
            Assert.Equal(6, Published(skill, "number_of_skeletons_allowed"));
            Assert.Equal(20, Published(unlinked, "number_of_raging_spirits_allowed"));
            var definition = Game.SummonData.Find("SummonSkeletons")!;
            for (int i = 0; i < 7; i++)
                Assert.NotEqual(0u, await Game.SummonFighter(player, skill, definition, (100, 100)));
            Assert.Equal(6, player.Summons.Fighters.Count);
            Assert.All(player.Summons.Fighters.Values, fighter =>
            {
                var mods = Game.GolemModifiers(player, fighter, fighter.Player);
                Assert.Equal(10, mods.Effects[Stat.BaseMovementVelocityPlusPct]);
            });

            ushort golemSource = Source("SummonFlameGolem");
            var golemSupport = Game.SupportedSkill(player, golemSource).Stats;
            Assert.Equal(7, Game.GolemSummonLimit(1, golemSupport, new Dictionary<uint, int>
            {
                [Stat.NumberOfGolemsAllowed] = 2, [Stat.DoubleGolemLimit] = 1,
            }));
            int publishedGolemLimit = Published(golemSource, "number_of_golems_allowed");
            Assert.NotEqual(0u, await Game.SummonGolem(player, golemSource,
                Game.GolemData.Find("SummonFlameGolem")!, (100, 100)));
            Assert.Equal(publishedGolemLimit, Assert.Single(player.Golems.All.Values).Limit);

            ushort puritySource = Source("HeraldOfPurity");
            Assert.Equal(5, Published(puritySource, "number_of_champions_of_light_allowed"));
            var aura = Game.GemSkillEffects(player).Single(g => g.Id == puritySource);
            var cost = Game.SupportedReservationCost(player, puritySource)!.Value;
            Assert.True(await Game.ToggleAura(player, puritySource, aura.Effect,
                aura.Name, cost, aura.Level, aura.Quality));
            var victim = new SpawnedMonster("test", new(100, 100, 0), 100, 100, 1);
            for (int i = 0; i < 6; i++) Game.ReservationKill(new(player, victim, "test", (100, 100)));
            await Game.ReservationSkillsTick(player, Game.AreaTime(player));
            Assert.Equal(5, player.Reservations.Fighters.Values.Count(f => f.Definition.Kind == "Purity"));

            items.Change(PlayerInventories.BodyArmour, worn.Id, item => item with { Links = [1, 1, 1, 1, 1] });
            Assert.Equal(skill, Source("SummonSkeletons"));
            Assert.Equal(5, Published(skill, "number_of_skeletons_allowed"));
            player.Summons.RefreshAt = 0;
            await Game.SummonsTick(player, Game.AreaTime(player));
            Assert.Equal(5, player.Summons.Fighters.Count);
            await Game.ReservationSkillsTick(player, Game.AreaTime(player) + .5);
            Assert.Equal(4, player.Reservations.Fighters.Values.Count(f => f.Definition.Kind == "Purity"));
            await Game.DisableReservations(player, "HeraldOfPurity");
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
