using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

// Wildspeaker node 24394, Scourge of the Wilds: additive minion-damage modifiers also apply to the owner at 150%,
// including the node's per-5-Dex input on the final Dexterity.
public sealed class WildspeakerWildsTests
{
    static IDisposable MinionTransfer(Player player, bool skillTransfer, bool withShare = true) =>
        GameEvents.PlayerStatSources.Add("Game", (owner, sources) =>
        {
            if (!ReferenceEquals(owner, player)) return sources;
            if (withShare) sources[Stat.AdditiveMinionDamageModifiersApplyToYouAt150PctValue] = 1;
            sources[Stat.MinionDamagePlusPct] = 15;
            sources[Stat.MinionDamagePlusPctPer5Dex] = 1;
            if (skillTransfer) sources[Stat.DominatingBlowAndAbsolutionAdditiveMinionDamageModifiersApplyToYouAt150PctValue] = 1;
            return sources;
        });

    static double Hit(Player player, ushort skill)
    {
        var inputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(player, skill));
        return Game.SupportedHitFrom(inputs, suppress: false)[DamageType.Physical].Max;
    }

    static double Raw(Player player, ushort skill) =>
        Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(player, skill)).Raw[DamageType.Physical].Max;

    [Fact]
    public void Node_24394_transfers_its_final_Dexterity_input_and_scales_a_real_hit()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        player.Pools = Game.PoolsFor(player, player.Pools.Level);
        ushort skill = Assert.Single(fixture.Equip(("Heavy Strike", 1)));
        var before = Game.CharacterPlayerStats(player);
        double beforeHit = Hit(player, skill);

        player.Character.Passives.Add(24394);
        player.Pools = Game.PoolsFor(player, player.Pools.Level);
        var node = Game.CharacterPlayerStats(player);
        Assert.Equal(before.GetValueOrDefault(Stat.Dexterity) + 100, node.GetValueOrDefault(Stat.Dexterity));
        Assert.Equal(1, node.GetValueOrDefault(Stat.MinionDamagePlusPctPer5Dex));
        long additive = node.GetValueOrDefault(Stat.MinionDamagePlusPct)
            + (long)node.GetValueOrDefault(Stat.MinionDamagePlusPctPer5Dex) * (node.GetValueOrDefault(Stat.Dexterity) / 5);
        int transfer = (int)(additive * 150 / 100);
        Assert.Equal(before.GetValueOrDefault(Stat.DamagePlusPct) + transfer, node.GetValueOrDefault(Stat.DamagePlusPct));

        double nodeHit = Hit(player, skill);
        // The hit already has other increased damage (Heavy Strike quality), so the transfer adds on top of that raw base.
        Assert.Equal(beforeHit + Raw(player, skill) * transfer / 100, nodeHit, 9);

        player.Character.Passives.Remove(24394);
        player.Pools = Game.PoolsFor(player, player.Pools.Level);
        Assert.Equal(before.GetValueOrDefault(Stat.DamagePlusPct), Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.DamagePlusPct));
        Assert.Equal(beforeHit, Hit(player, skill), 9);
    }

    [Fact]
    public void Combined_plain_and_per_Dex_transfer_rounds_once_and_skills_do_not_double_count_it()
    {
        static (int Damage, int SkillDamage) Read(bool skillTransfer)
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            player.Pools = player.Pools with { Dexterity = 105 };
            using var hook = MinionTransfer(player, skillTransfer);
            ushort skill = Assert.Single(fixture.Equip(("Absolution", 1)));
            int characterDamage = Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.DamagePlusPct);
            var inputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(player, skill));
            return (characterDamage, inputs.Get(Stat.DamagePlusPct));
        }

        var plain = Read(skillTransfer: false);
        var strongest = Read(skillTransfer: true);
        // 15 plain + 1 per 5 Dex at 105 Dexterity = 15 + 21 = 36; the transfer is 36 * 150 / 100 = 54.
        Assert.Equal(54, plain.Damage);
        Assert.Equal(plain.Damage, plain.SkillDamage);
        Assert.Equal(plain.Damage, strongest.Damage);
        Assert.Equal(plain.Damage, strongest.SkillDamage);
    }
}
