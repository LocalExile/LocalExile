using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData("ground", 0x401)]
    [InlineData("monster", 0x400)]
    [InlineData("monster", 0x401)]
    public async Task Early_supported_attack_keeps_the_pending_action_without_echo_or_payment(string route, int flags)
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 90, god: true);
        foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements" })
            game.State.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
        // Slow both timelines equally to leave a generous deterministic margin for real handler dispatch.
        game.State.PlayerStats[Program.Stats.Key("action_speed_-%")!.Value] = 99;
        game.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Butcher Sword")!));
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = new[] { "Heavy Strike", "Faster Attacks Support" }.Select(name =>
            {
                var gem = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with
                {
                    GemVariant = gem.Variant.Index,
                    GemExperience = Program.Items.Gem(gem.Type)!.ExperienceFor(name == "Heavy Strike" ? 1 : 20),
                });
            }).ToArray(), Links = [2],
        });
        var gem = Assert.Single(game.GemEffects());
        Assert.Single(Program.SupportedSkill(game.State, gem.Id).Supports);
        await game.PrimeTimingPools();
        game.State.Monsters[0x8123] = new SpawnedMonster("pending swing target", new WalkEstimator(34, 32, 0), 0, 0, 1000);
        // Resolve the handler's lazy catalogs before exercising its much narrower timing boundary.
        game.State.NextAttackAt = game.State.Clock.Elapsed.TotalSeconds + 1000;
        Assert.Empty((await game.TimingInput(route, gem.Id, 0x8123, (ushort)flags)).Bytes);
        double supported = ProgramValue<double>("SupportedAttackSeconds", game.State, gem.Id);
        double unsupported = Program.SkillAttackSeconds(game.State, gem.Effect, gem.Level);
        Assert.True(unsupported > supported);
        double remaining = (unsupported + supported) * AttackTiming.EarlyAllowance / 2;
        Assert.True(remaining > supported * AttackTiming.EarlyAllowance);
        Assert.True(remaining < unsupported * AttackTiming.EarlyAllowance);

        game.State.ActionCount = 17;
        game.State.ChannelSkill = 0x222;
        game.State.ChannelSkillSequence = 0x1234;
        game.State.NextChannelHitAt = 456;
        uint life = game.State.PlayerLife, mana = game.State.PlayerMana;
        var walk = game.State.Walk.Destination;
        double next = game.State.NextAttackAt = game.State.Clock.Elapsed.TotalSeconds + remaining;

        var reply = await game.TimingInput(route, gem.Id, 0x8123, (ushort)flags);

        Assert.Null(reply.Error);
        Assert.Empty(reply.Bytes);
        Assert.Equal(17, game.State.ActionCount);
        Assert.Equal((ushort)0x222, game.State.ChannelSkill);
        Assert.Equal((ushort)0x1234, game.State.ChannelSkillSequence);
        Assert.Equal(456, game.State.NextChannelHitAt);
        Assert.Equal(next, game.State.NextAttackAt);
        Assert.Equal(walk, game.State.Walk.Destination);
        Assert.Equal(life, game.State.PlayerLife);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Equal(1000u, game.State.Monsters[0x8123].Life);
    }
}
