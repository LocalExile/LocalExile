using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class VigilantStrikeFortifyTests
{
    [Fact]
    public void VigilantStrike_UsesItsEightSecondFortifyGrant()
    {
        int effect = Program.Skills.EffectRow("VigilantStrike");
        var own = Program.Skills.EffectStats(effect, 1).ToDictionary(stat => stat.Item1, stat => stat.Item2);
        Assert.Equal(8000, own[FortificationSession.DirectMeleeHitDurationMilliseconds]);

        var session = new FortificationSession();
        int Get(uint key) => own.GetValueOrDefault(key);

        Assert.True(session.Hit(0, 200m, 1000m, MonsterRarity.Normal, Get, new Random(1)));
        Assert.Equal(8, session.Read(0).NextExpiry!.Value, precision: 6);
        Assert.Equal(8, session.Read(0).LatestExpiry!.Value, precision: 6);
        Assert.True(session.Read(0).Uncapped > 0);

        long firstGrant = session.Read(0).Uncapped;
        Assert.True(session.Hit(3, 200m, 1000m, MonsterRarity.Normal, Get, new Random(1)));
        Assert.Equal(8, session.Read(3).NextExpiry!.Value, precision: 6);
        Assert.Equal(11, session.Read(3).LatestExpiry!.Value, precision: 6);
        Assert.True(session.Read(3).Uncapped > firstGrant);

        Assert.Equal(firstGrant, session.Read(8).Uncapped);
        Assert.Equal(11, session.Read(8).NextExpiry!.Value, precision: 6);
        Assert.Equal(11, session.Read(8).LatestExpiry!.Value, precision: 6);
        Assert.Equal(0, session.Read(11).Current);
        Assert.Null(session.Read(11).NextExpiry);
        Assert.Null(session.Read(11).LatestExpiry);
    }

    [Fact]
    public void FortifyHitKeepsDefaultDurationAndGuardsGranting()
    {
        static int Get(IReadOnlyDictionary<uint, int> values, uint key) => values.GetValueOrDefault(key);
        const decimal damage = 200m;

        var ordinary = new FortificationSession();
        var ordinaryValues = new Dictionary<uint, int> { [FortificationSession.OnMeleeHit] = 1 };
        Assert.True(ordinary.Hit(0, damage, 1000m, MonsterRarity.Normal,
            key => Get(ordinaryValues, key), new Random(1)));
        Assert.Equal(FortificationState.BaseDurationSeconds, ordinary.Read(0).NextExpiry!.Value, precision: 6);

        var modified = new FortificationSession();
        var modifiedValues = new Dictionary<uint, int>
        {
            [FortificationSession.DirectMeleeHitDurationMilliseconds] = 8000,
            [FortificationState.DurationStat] = 25,
        };
        Assert.True(modified.Hit(0, damage, 1000m, MonsterRarity.Normal,
            key => Get(modifiedValues, key), new Random(1)));
        Assert.Equal(10, modified.Read(0).NextExpiry!.Value, precision: 6);

        var guarded = new FortificationSession();
        var guardedValues = new Dictionary<uint, int>
        {
            [FortificationSession.DirectMeleeHitDurationMilliseconds] = 8000,
            [FortificationSession.CannotGain] = 1,
        };
        Assert.False(guarded.Hit(0, damage, 1000m, MonsterRarity.Normal,
            key => Get(guardedValues, key), new Random(1)));
        guardedValues.Remove(FortificationSession.CannotGain);
        Assert.False(guarded.Hit(0, 0, 1000m, MonsterRarity.Normal,
            key => Get(guardedValues, key), new Random(1)));

        var throttled = new FortificationSession();
        var direct = new Dictionary<uint, int>
        {
            [FortificationSession.DirectMeleeHitDurationMilliseconds] = 8000,
        };
        Assert.True(throttled.Hit(0, damage, 1000m, MonsterRarity.Normal,
            key => Get(direct, key), new Random(1)));
        Assert.False(throttled.Hit(.05, damage, 1000m, MonsterRarity.Normal,
            key => Get(direct, key), new Random(1)));
    }
}
