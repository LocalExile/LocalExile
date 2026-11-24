using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The other warcries grant the buffs their own BuffDefinitions rows settle (Seismic, Ancestral, Infernal,
// Battlemage's Cry), in the shape Intimidating Cry's executor uses. The rows are not IsRecovery, so the buff
// carries no values; the stats ride the player's stats through the WarcryBuffs step.
public sealed class WarcryBuffTests
{
    [Fact]
    public void Seismic_Cry_grants_its_row_stats_with_the_rows_own_value_count()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        ushort id = Assert.Single(f.Equip(("Seismic Cry", 1)));
        var gem = Game.SupportedSkill(player, id);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        var playerStats = new Dictionary<uint, int> { [Stat.WarcriesHaveInfinitePower] = 1 };

        // Row 1544 (seismic_cry) carries exactly two GrantedStats, in this order, at 25 power (five whole
        // 5-power steps): 5 x 15% increased Armour (final) and 5 x 50% increased Stun Threshold.
        var stats = Game.WarcryBuffStats("SeismicCry", 25, 1.0, Own);
        Assert.NotNull(stats);
        Assert.Equal(2, stats.Length);
        Assert.Equal(Stat.ArmourPlusPctFinalFromSeismicCryBuff, stats[0].Key);
        Assert.Equal(5 * Own(Stat.SeismicCryArmourPlusPctFinalPer5PowerUpToCap), stats[0].Value);
        Assert.Equal(Stat.StunThresholdPlusPct, stats[1].Key);
        Assert.Equal(5 * Own(Stat.SeismicCryStunThresholdPlusPctPer5PowerUpToCap), stats[1].Value);

        var before = Game.CharacterPlayerStats(player);
        double now = 1;
        Game.ApplyWarcryBuffLocked(player, "SeismicCry", gem.Stats, playerStats, (100, 100), 60, now, 4.0, 1.0);
        Assert.True(Game.WarcryBuffActive(player));
        Assert.Equal(stats, Game.WarcryBuffStatsOf(player));
        Assert.True(player.Area.Buffs.Has(player.Id, 1544, player.Id));
        var after = Game.CharacterPlayerStats(player);
        Assert.Equal(before.GetValueOrDefault(Stat.ArmourPlusPctFinalFromSeismicCryBuff) + stats[0].Value,
            after.GetValueOrDefault(Stat.ArmourPlusPctFinalFromSeismicCryBuff));
        Assert.Equal(before.GetValueOrDefault(Stat.StunThresholdPlusPct) + stats[1].Value,
            after.GetValueOrDefault(Stat.StunThresholdPlusPct));

        // Past the deadline the stats leave.
        Game.WarcryBuffsOf(player).Until = now - 1;
        Assert.False(Game.WarcryBuffActive(player));
        Assert.Empty(Game.WarcryBuffStatsOf(player));
        var expired = Game.CharacterPlayerStats(player);
        Assert.Equal(before.GetValueOrDefault(Stat.ArmourPlusPctFinalFromSeismicCryBuff),
            expired.GetValueOrDefault(Stat.ArmourPlusPctFinalFromSeismicCryBuff));
        Assert.Equal(before.GetValueOrDefault(Stat.StunThresholdPlusPct),
            expired.GetValueOrDefault(Stat.StunThresholdPlusPct));
    }

    [Fact]
    public void Rallying_Cry_Generals_Cry_and_Vengeful_Cry_grant_no_buff()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var playerStats = new Dictionary<uint, int> { [Stat.WarcriesHaveInfinitePower] = 1 };

        // Rallying Cry's row (399) does not settle its values, General's Cry has no buff row at all, and
        // Vengeful Cry's row (2476) is a retaliation buff with no warcry handling here.
        Assert.Null(Game.WarcryBuffRow("RallyingCry"));
        Assert.Null(Game.WarcryBuffRow("GeneralsCry"));
        Assert.Null(Game.WarcryBuffRow("VengefulCry"));
        Assert.Null(Game.WarcryBuffStats("RallyingCry", 25, 1.0, _ => 0));
        Assert.Null(Game.WarcryBuffStats("GeneralsCry", 25, 1.0, _ => 0));
        Assert.Null(Game.WarcryBuffStats("VengefulCry", 25, 1.0, _ => 0));

        var before = Game.CharacterPlayerStats(player);
        Game.ApplyWarcryBuffLocked(player, "RallyingCry", new Dictionary<uint, int>(), playerStats, (100, 100), 60, 1, 4.0, 1.0);
        Game.ApplyWarcryBuffLocked(player, "GeneralsCry", new Dictionary<uint, int>(), playerStats, (100, 100), 60, 1, 4.0, 1.0);
        Game.ApplyWarcryBuffLocked(player, "VengefulCry", new Dictionary<uint, int>(), playerStats, (100, 100), 60, 1, 4.0, 1.0);
        Assert.False(Game.WarcryBuffActive(player));
        Assert.Empty(Game.WarcryBuffStatsOf(player));
        var after = Game.CharacterPlayerStats(player);
        Assert.Equal(before.GetValueOrDefault(Stat.DamagePlusPct), after.GetValueOrDefault(Stat.DamagePlusPct));
        Assert.Equal(before.GetValueOrDefault(Stat.BaseManaRegenerationRatePerMinute), after.GetValueOrDefault(Stat.BaseManaRegenerationRatePerMinute));
    }
}
