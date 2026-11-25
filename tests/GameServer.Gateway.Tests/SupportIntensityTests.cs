using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SupportIntensityTests
{
    [Fact]
    public async Task Intensity_caps_while_casting_and_falls_with_movement_and_teleport()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new CastClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skillId = Assert.Single(fixture.Equip(("Ice Nova", 1), ("Intensify Support", 1)));
            var gem = Game.SupportedSkill(player, skillId);
            const uint area = Stat.SupportSpellBoostAreaOfEffectPlusPctFinal;
            int Modifier() => Game.SupportedSkill(player, skillId).Stats.GetValueOrDefault(area);
            int Radius() => Game.SkillRadius(player, Game.SupportedSkill(player, skillId).Stats, spell: true, gem.Effect);
            int baseline = Radius();
            Assert.Equal(0, Modifier());

            int perStack = gem.Stats[Stat.SupportSpellBoostAreaOfEffectPlusPctFinalPerCharge];
            Assert.Equal(-15, perStack);
            for (int cast = 1; cast <= 4; cast++)
            {
                Assert.True(await Game.CastGemSkill(player, skillId, (100, 100), (100, 100)));
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(perStack * Math.Min(3, cast), Modifier());
            }
            Assert.True(Radius() < baseline);

            player.Walk.SetSpeed(40, clock.At);
            player.Walk.Order(155, 100, clock.At);
            clock.At += .51;
            var here = player.Walk.Position(clock.At);
            player.Walk.Teleport(here.X, here.Y, clock.At); // stop the walk at its current cell
            Assert.Equal(2 * perStack, Modifier());
            clock.At += 1;
            Assert.Equal(2 * perStack, Modifier());
            player.Walk.Teleport(170, 100, clock.At);
            Assert.Equal(0, Modifier());
            Assert.Equal(baseline, Radius());

            skillId = Assert.Single(fixture.Equip(("Desecrate", 1), ("Intensify Support", 1)));
            Assert.Equal(1, Game.SupportedSkill(player, skillId).Stats.GetValueOrDefault(Stat.VirtualSkillGainsIntensity));
            Assert.True(await Game.CastGemSkill(player, skillId, (170, 100), (170, 100)));
            Assert.Equal(perStack, Modifier());
            AmplificationRodSupports(fixture);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static void AmplificationRodSupports(TriggeredSummonTests.Fixture fixture)
    {
        var player = fixture.Player;
        var items = Game.ItemsOf(player);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        var rod = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Amplification Rod", new Random(17)));
        items.Change(PlayerInventories.Weapon1, host.Id, _ => rod);
        fixture.Equip(("Ball Lightning", 20), ("Faster Casting Support", 20), ("Arc", 20));
        var linked = Assert.Single(items.Items(PlayerInventories.Weapon1)).Item with { Links = [2, 1] };
        var novaType = Program.Items.FindGem("Ice Nova")!.Value;
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Sockets = [new(SocketColour.White, ItemCatalog.Create(novaType.Type) with { GemVariant = novaType.Variant.Index }),
                linked.Sockets[1]],
            Links = [2],
        });

        const string echo = "Spell Echo Support", intensity = "Intensify Support";
        const string destruction = "Controlled Destruction Support", faster = "Faster Casting Support";
        int Rank(string skill, string support)
        {
            int effect = Program.Items.FindGem(support)!.Value.Variant.Effect;
            ushort id = Game.GemSkillEffects(player).Single(g => g.Name == skill).Id;
            return Game.SupportedSkill(player, id).Supports.SingleOrDefault(s => s.Effect.Row == effect)?.Gem.Level ?? 0;
        }
        items.Change(PlayerInventories.Weapon1, host.Id, _ => linked with { Mods = [] });
        Assert.Equal((0, 0, 0, 20), (Rank("Ball Lightning", echo), Rank("Ball Lightning", intensity),
            Rank("Ball Lightning", destruction), Rank("Ball Lightning", faster)));
        Assert.Equal(0, Rank("Arc", echo));

        items.Change(PlayerInventories.Weapon1, host.Id, _ => linked);
        var fixedRanks = (Rank("Ball Lightning", echo), Rank("Ball Lightning", intensity), Rank("Ball Lightning", destruction));
        int arcEcho = Rank("Arc", echo);
        Assert.Equal((0, 0, 0, 20), (Rank("Ice Nova", echo), Rank("Ice Nova", intensity),
            Rank("Ice Nova", destruction), Rank("Ice Nova", faster)));

        var levelMod = Program.Items.Mods.All.First(mod =>
            mod.GrantedEffectLevels.Count > 0 && mod.Stats.Any(stat => stat.Id == Stat.LocalDisplaySocketedGemsGetFasterCastLevel));
        items.Change(PlayerInventories.Weapon1, host.Id, _ => linked with
        {
            Mods = [new ItemMod(ModSection.Explicit, levelMod.Hash, levelMod.Stats.Select(stat =>
                stat.Id == Stat.LocalDisplaySocketedGemsGetFasterCastLevel ? 7 : stat.Min).ToArray())],
        });
        int variableLevel = Rank("Arc", faster);
        Console.WriteLine($"Amplification Rod: Ball Lightning {fixedRanks}, Arc echo {arcEcho}, variable Faster Casting {variableLevel}");
        Assert.Equal((10, 10, 10), fixedRanks);
        Assert.Equal(10, arcEcho);
        Assert.Equal(7, variableLevel);
    }

    [Fact]
    public async Task Pinpoint_uses_zero_intensity_for_the_first_volley_and_gains_on_release()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skillId = Assert.Single(fixture.Equip(("Fireball", 1)));
        var control = Game.SupportedHitInputsFor(player, skillId, area: true, projectile: true)!;
        double baseline = Game.SupportedHitFrom(control, suppress: false).MaxTotal;
        Assert.True(baseline > 0);

        skillId = Assert.Single(fixture.Equip(("Fireball", 1), ("Pinpoint Support", 1)));
        var linked = Game.SupportedHitInputsFor(player, skillId, area: true, projectile: true)!;
        Assert.True(linked.Projectile);
        Assert.Equal(-19, linked.Get(Stat.SupportGreaterProjectileIntensityProjectileDamagePlusPctFinal));
        Assert.Equal(baseline * .81, Game.SupportedHitFrom(linked, suppress: false).MaxTotal, 8);
        Assert.Equal(Game.SupportedHitFrom(control with { Projectile = false }, suppress: false).MaxTotal,
            Game.SupportedHitFrom(linked with { Projectile = false }, suppress: false).MaxTotal, 8);

        var clock = new CastClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        try
        {
            int Extra() => Game.SupportedSkill(player, skillId).Stats.GetValueOrDefault(Stat.NumberOfAdditionalProjectiles);
            Assert.Equal(3, Extra());
            Assert.Equal(4, await Volley(1));
            Assert.Equal(2, Extra());
            Assert.Equal(3, await Volley(2));
            Assert.Equal(1, Extra());

            async Task<int> Volley(ushort sequence)
            {
                Assert.True(await Game.CastGemSkill(player, skillId, (100, 100), (190, 100), sequence: sequence));
                for (int step = 0; step < 200; ++step)
                {
                    clock.At += .01;
                    fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                    int count = fixture.Session.Runtime.Projectiles.Count(p => p.Use.Sequence == sequence);
                    if (count > 0) return count;
                }
                return 0;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class CastClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
