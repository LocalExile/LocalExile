using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData("ground", "cast_speed_+%", -100)]
    [InlineData("monster", "cast_speed_+%", -100)]
    [InlineData("self", "cast_speed_+%", -100)]
    [InlineData("instant", "cast_speed_+%", -100)]
    [InlineData("ground", "action_speed_-%", 100)]
    [InlineData("monster", "action_speed_-%", 100)]
    [InlineData("self", "action_speed_-%", 100)]
    [InlineData("instant", "action_speed_-%", 100)]
    public async Task Impossible_spell_handler_preserves_the_current_action_walk_cost_cooldown_and_echo(
        string route, string input, int value)
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 24, god: true);
        EquipBlueGems(game, "Flame Dash");
        var gem = game.GemEffects().Single(g => g.Effect == Program.Skills.EffectRow("FlameDash"));
        Assert.False(Program.Skills.IsInstant(gem.Effect));
        Assert.True(Program.Skills.CooldownMs(gem.Effect, gem.Level) > 0);
        game.State.PlayerStats[Program.Stats.Key(input)!.Value] = value;
        Assert.False((await game.Cast(gem.Name)).Accepted);
        // The cooldown preflight rejects before CastGemSkill, so explicitly prime the existing ES reply cache.
        await game.PrimeTimingPools();
        game.State.ActionCount = 17;
        game.State.ChannelSkill = 0x222;
        game.State.ChannelSkillSequence = 0x1234;
        game.State.NextAttackAt = 123;
        game.State.NextChannelHitAt = 456;
        game.State.SkillReadyAt[Program.Skills.EffectRow("Haste")] = 789;
        uint monsterId = 0x8123;
        game.State.Monsters[monsterId] = new SpawnedMonster("preflight target", new WalkEstimator(100, 32, 0), 0, 0, 1000);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        game.State.Walk.SetSpeed(37, now);
        game.State.Walk.Order(120, 32, now);
        var destination = game.State.Walk.Destination;
        double arrives = game.State.Walk.ArrivesAt;
        var sample = game.State.Walk.Position(now + .5);
        uint mana = game.State.PlayerMana, life = game.State.PlayerLife;
        var cooldowns = game.State.SkillReadyAt.OrderBy(x => x.Key).ToArray();
        string saved = game.SavedJson;

        var result = await game.TimingInput(route, gem.Id, monsterId);

        Assert.Equal(17, game.State.ActionCount);
        Assert.Equal((ushort)0x222, game.State.ChannelSkill);
        Assert.Equal((ushort)0x1234, game.State.ChannelSkillSequence);
        Assert.Equal(123, game.State.NextAttackAt);
        Assert.Equal(456, game.State.NextChannelHitAt);
        Assert.Equal(destination, game.State.Walk.Destination);
        Assert.Equal(arrives, game.State.Walk.ArrivesAt);
        Assert.Equal(sample, game.State.Walk.Position(now + .5));
        Assert.Equal(37, game.State.Walk.Speed);
        Assert.Equal(cooldowns, game.State.SkillReadyAt.OrderBy(x => x.Key).ToArray());
        Assert.False(game.State.SkillReadyAt.ContainsKey(gem.Effect));
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Equal(life, game.State.PlayerLife);
        Assert.Equal(saved, game.SavedJson);
        Assert.Empty(game.State.Auras);
        // Ground rejection resyncs the predicted mana cost. The single-use cooldown
        // preflight refuses an impossible instant-route request before CastGemSkill, without a resync or action echo.
        byte[] expected = route == "ground"
            ? InstanceProtocol.LifeUpdate(Program.PlayerId, mana, 0, pool: 1) : [];
        Assert.Equal(expected, result.Bytes);
    }

    [Fact]
    public void Shared_preflight_uses_actual_quality_and_preserves_instant_and_attack_eligibility()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 24, god: true);
        var lightning = Program.Items.FindGem("Lightning Warp")!.Value;
        var haste = Program.Items.FindGem("Haste")!.Value;
        var burning = Program.Items.FindGem("Burning Arrow")!.Value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = new[] { (lightning, 20), (haste, 0), (burning, 0) }.Select(x =>
                new ItemSocket(SocketColour.Blue, ItemCatalog.Create(x.Item1.Type) with
                    { GemVariant = x.Item1.Variant.Index, Quality = (byte)x.Item2 })).ToArray(),
            Links = [0, 1, 2],
        });
        game.State.PlayerStats[Program.Stats.Key("cast_speed_+%")!.Value] = -100;
        var warp = game.GemEffects().Single(g => g.Effect == Program.Skills.EffectRow("LightningWarp"));
        Assert.Equal(20, warp.Quality);
        var player = Program.CharacterPlayerStats(game.State);
        Assert.Null(Program.Skills.CastTimeMs(warp.Effect, warp.Level, 0, player));
        Assert.Equal(3000, Program.Skills.CastTimeMs(warp.Effect, warp.Level, warp.Quality, player));
        foreach (var grant in game.GemEffects())
            Assert.Null(ProgramValue<string?>("GemDisabled", game.State, grant.Effect, grant.Id));
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> PrimeTimingPools() => Exchange(async (stream, cipher) =>
        {
            await ProgramValue<Task>("SendPlayerMana", "timing-preflight-prime", stream, cipher, State, null!);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> TimingInput(string route, ushort skill, uint monster) => Exchange(async (stream, cipher) =>
        {
            if (route == "ground")
            {
                byte[] bytes = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId).U32(96).U32(32)
                    .U16(skill).U16(0).U16(0x401).ToArray();
                await ProgramValue<Task>("OnClientMessages", "timing-preflight", stream, cipher,
                    new (ushort, byte[])[] { (InstanceProtocol.UseSkillAtLocationId, bytes) }, Array.Empty<byte>(), State, 0L);
            }
            else if (route == "instant")
                await ProgramValue<Task>("UseInstantSkill", "timing-preflight", stream, cipher, State, skill,
                    Program.PlayerId, State.Walk.Position(State.Clock.Elapsed.TotalSeconds));
            else
                await ProgramValue<Task>("OnUseObject", "timing-preflight", stream, cipher, State,
                    new InstanceTransfer.UseSkillOnObject(route == "self" ? Program.PlayerId : monster, skill, 0, 0x401),
                    State.Clock.Elapsed.TotalSeconds);
            return null;
        });
    }
}
