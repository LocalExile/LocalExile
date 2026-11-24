using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WarcryBuffTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time = 1;
        public double Now(Player _) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            Time += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

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

        var previous = Game.AreaUpdateContext.Value;
        var clock = new Clock();
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            using var modifiers = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (owner == player) values[Stat.BasePhysicalDamageReductionRating] = 100;
                return values;
            });
            var body = new SpawnedMonster("ally", new(100, 100, 0), 10, 3, 100) { OwnerId = player.Id, Hostile = false };
            player.Area.Monsters[900] = body;
            var ally = new GolemState.Golem(900, body, Game.GolemData.Find("SummonStoneGolem")!, 1);
            var before = Game.BuildPoolsFor(player, 100);
            Game.CharacterPlayerStats(player);
            Game.ApplyWarcryBuffLocked(player, "SeismicCry", gem.Stats, playerStats, (100, 100), 60, clock.Time, 4, 1);
            Assert.True(stats[0].Value > 0);
            Assert.Equal(stats[0].Value, Game.MinionAuraStats(player, ally)[stats[0].Key]);
            Assert.True(player.Area.Buffs.Has(player.Id, 1544, player.Id));
            Assert.True(player.Pools.Armour > before.Armour);

            id = Assert.Single(f.Equip(("Ancestral Cry", 1)));
            var ancestral = Game.SupportedSkill(player, id);
            clock.Time = 2;
            Game.ApplyWarcryBuffLocked(player, "AncestralCry", ancestral.Stats, playerStats, (100, 100), 60, clock.Time, 5, 1);
            Assert.Equal(before.FireMaxResistance + 3, player.Pools.FireMaxResistance);
            Assert.True(player.Pools.Armour > before.Armour);

            clock.Time = 5;
            Game.ExpireWarcryBuffs(player);
            Assert.Equal(before.Armour, player.Pools.Armour);
            Assert.Equal(0, Game.MinionAuraStats(player, ally).GetValueOrDefault(stats[0].Key));
            Assert.Equal(before.FireMaxResistance + 3, player.Pools.FireMaxResistance);
            clock.Time = 7;
            Game.ExpireWarcryBuffs(player);
            Assert.Equal(before.FireMaxResistance, player.Pools.FireMaxResistance);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
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
        var after = Game.CharacterPlayerStats(player);
        Assert.Equal(before.GetValueOrDefault(Stat.DamagePlusPct), after.GetValueOrDefault(Stat.DamagePlusPct));
        Assert.Equal(before.GetValueOrDefault(Stat.BaseManaRegenerationRatePerMinute), after.GetValueOrDefault(Stat.BaseManaRegenerationRatePerMinute));
    }
}
