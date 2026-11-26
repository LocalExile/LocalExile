namespace GameServer.Gateway.Tests;

public sealed class VaalImmortalCallTests
{
    [Fact]
    public async Task Vaal_immortal_call_spends_its_souls_and_charges_and_holds_life_at_one()
    {
        using var f = new MinionUtilityTests.Fixture();
        using var clock = new CastClock(f);
        // 1000 more of each attribute so CurrentGemRequirements meets Vaal Immortal Call's Strength requirement.
        f.Player.God = true;
        f.Equip(("Vaal Immortal Call", 1));
        var gem = Game.GemSkillEffects(f.Player).Single(g => Game.SkillsFor(f.Player).EffectId(g.Effect) == "VaalImmortalCall");
        Game.RefreshVaalSouls(f.Player);
        var plan = Game.CooldownFor(f.Player, gem.Effect, gem.Level, gem.Quality);
        bool Admit(out CooldownUseTicket? ticket) => Game.TryBeginSkillCooldownUse(f.Player, gem.Id, gem.Effect, plan, false,
            () => f.Player.ActionCount++, out ticket);
        // No souls: the use is refused before an action, a cost or a reservation.
        Assert.False(Admit(out _));
        for (int i = 0; i < 100; i++) Assert.True(f.Player.VaalSouls.Gain());
        var charges = Game.ChargesOf(f.Player);
        var limits = charges.Limits(ChargeType.Endurance);
        charges.State.Set(ChargeType.Endurance, limits.Minimum + 5, 0, limits);
        Assert.True(Admit(out var ticket));
        Assert.True(await Game.CastGemSkill(f.Player, gem.Id, (100, 100), (120, 100),
            cooldownUse: ticket));
        double castAt = Game.AreaTime(f.Player);
        Assert.Equal(0u, Assert.Single(f.Player.VaalSouls.Read()).Current);
        Assert.InRange(f.Player.VaalPreventionUntil - castAt, 7.4, 8.1);
        Assert.Equal(limits.Minimum, charges.Effective(ChargeType.Endurance));
        Assert.NotEmpty(Game.VaalImmortalCallPresentation(f.Player, castAt));
        // Level 1: a 0.4 s base, +100% duration per Endurance Charge removed (five), so about 2.4 s.
        Assert.True(Game.VaalImmortalCallActive(f.Player, castAt));
        Assert.False(Game.VaalImmortalCallActive(f.Player, castAt + 2.5));
        // A lethal hit and a lethal damage tick cannot take the last point of Life while the buff runs.
        f.Player.PlayerLife = 100;
        Assert.Equal(99, Game.DamagePlayerHitTyped(f.Player, 100_000, 0, 0, castAt, new Dictionary<uint, int>(),
            GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100_000, 100_000)));
        Assert.Equal(1u, Game.VaalImmortalCallLife(f.Player, 0, castAt));
        Assert.Equal(0u, Game.VaalImmortalCallLife(f.Player, 0, castAt + 2.5));
    }

    [Fact]
    public async Task Life_lost_on_kill_respects_vaal_protection_and_notifies_death_once()
    {
        using var f = new MinionUtilityTests.Fixture();
        using var clock = new CastClock(f);
        var player = f.Player;
        player.God = true;
        f.Equip(("Vaal Immortal Call", 1));
        var gem = Game.GemSkillEffects(player).Single(g => Game.SkillsFor(player).EffectId(g.Effect) == "VaalImmortalCall");
        Game.RefreshVaalSouls(player);
        for (int i = 0; i < 100; i++) Assert.True(player.VaalSouls.Gain());
        var plan = Game.CooldownFor(player, gem.Effect, gem.Level, gem.Quality);
        Assert.True(Game.TryBeginSkillCooldownUse(player, gem.Id, gem.Effect, plan, false,
            () => player.ActionCount++, out var ticket));
        Assert.True(await Game.CastGemSkill(player, gem.Id, (100, 100), (120, 100), cooldownUse: ticket));

        async Task Kill(uint id)
        {
            var corpse = new SpawnedMonster("zombie", new(100, 100, clock.At), 0, 0, 100)
            {
                ObjectId = id, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite"),
                NoRewards = true, Dead = true, Life = 0,
            };
            Assert.NotNull(corpse.Variety);
            f.Player.Area.Monsters[id] = corpse;
            await Game.MonsterKilled(f.Player, id, corpse);
        }

        player.PlayerLife = 50;
        Game.LeechOf(player).OnKill = new(-100, 0, 0, 0, 0, 0);
        await Kill(901);
        Assert.Equal(1u, f.Player.PlayerLife);
        Assert.Equal(0, f.Player.PlayerDeaths);

        clock.At += 1;
        using (Game.ProjectileMutation(player)) { }
        await Kill(902);
        Assert.Equal(0u, f.Player.PlayerLife);
        Assert.Equal(1, f.Player.PlayerDeaths);
        int deathEpoch = f.Player.DeathNotificationEpoch;
        await Kill(903);
        Assert.Equal(deathEpoch, f.Player.DeathNotificationEpoch);
        Assert.Equal(1, f.Player.PlayerDeaths);
    }

    sealed class CastClock : IPlayerUpdateSchedule, IDisposable
    {
        readonly PlayerUpdatesState? previous = Game.AreaUpdateContext.Value;

        public CastClock(MinionUtilityTests.Fixture fixture) =>
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, this);

        public double At { get; set; } = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
        public void Dispose() => Game.AreaUpdateContext.Value = previous;
    }
}
