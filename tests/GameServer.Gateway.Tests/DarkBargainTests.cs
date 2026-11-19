using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class DarkBargainTests
{
    [Fact]
    public void ChainStartsNearTheAimThenJumpsToTheNearestUnusedSkeletonAndNeverPaysTheLastPoint()
    {
        (uint, (int, int), uint, uint)[] skeletons =
        [
            (1, (100, 100), 1000, 1000), (2, (120, 100), 50, 1000), (3, (150, 100), 1000, 1000),
            (4, (100 + Game.DarkBargainChainRadius * 4, 100), 1000, 1000),
        ];
        var blasts = Game.DarkBargainChain(skeletons, aim: (105, 100), player: (0, 0), percent: 12, chains: 2);
        Assert.Equal([1u, 2u, 3u], blasts.Select(b => b.Skeleton));
        // 12% of 1000 is 120; the hurt skeleton keeps its last point (49 of its 50).
        Assert.Equal([120, 49, 120], blasts.Select(b => b.Sacrificed));
        Assert.Equal([1u, 2u], Game.DarkBargainChain(skeletons, (105, 100), (0, 0), 12, chains: 1).Select(b => b.Skeleton));
        // No skeleton near the aim: the one nearest the player starts it. None near either: the caster pays instead.
        Assert.Equal(3u, Game.DarkBargainChain(skeletons, (5000, 5000), (155, 100), 12, 0).Single().Skeleton);
        Assert.Empty(Game.DarkBargainChain(skeletons, (5000, 5000), (5000, 5000), 12, 2));
        Assert.Empty(Game.DarkBargainChain([], (105, 100), (100, 100), 12, 2));
    }

    [Fact]
    public async Task CasterPaysAShareOfMaximumLifeButNeverItsLastPointAndTheBlastUsesItsOwnRadius()
    {
        Assert.Equal(360, Game.DarkBargainLifePaid(3000, 3000, 12));
        Assert.Equal(99, Game.DarkBargainLifePaid(100, 3000, 12));
        Assert.Equal(0, Game.DarkBargainLifePaid(1, 3000, 12));
        Assert.Equal(0, Game.DarkBargainLifePaid(0, 3000, 12));
        Assert.Equal(26, SkillArea.BaseRadius("DarkPact", _ => 0));
        Assert.Equal(26, SkillArea.BaseRadius("DarkPactRoyale", _ => 0));

        using var battle = new ArrowRainAndMirageTests.Battle("Dark Bargain");
        var conn = battle.Conn;
        conn.Clock.Start();
        var items = Game.ItemsOf(conn);
        var host = items.EquipmentSnapshot().Single();
        var staff = Game.Uniques.Create("Pledge of Hands", new Random(731))!;
        items.Remove(host.Row, host.Item.Id);
        items.Add(host.Row, 0, 0, staff with { Sockets = host.Item.Item.Sockets, Links = host.Item.Item.Links });
        conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        foreach (string edge in new[] { "minimum", "maximum" })
            conn.PlayerStats[Program.Stats.Key($"spell_{edge}_added_cold_damage")!.Value] = 1000;
        var gem = Game.GemSkillEffects(conn).Single();
        Assert.Equal(2, Game.SpellRepeats(conn, gem.Id, gem.Effect));
        var sacrifice = new Game.SacrificeUse(conn, gem.Id, gem.Effect, gem.Gem);
        int paid = Game.DarkBargainLifePaid(conn.PlayerLife, conn.PlayerMaxLife, 12);
        var inputs = Game.SupportedHitInputsFor(conn, gem.Id, area: true, projectile: false)!;
        var damage = Game.SupportedHitFrom(inputs with { Raw = inputs.Raw.Add(DamageType.Chaos, new(paid, paid)) }, suppress: false);
        var inner = conn.Monsters[300] = new("28 cells", new(128, 100, 0), 0, 0, 100000) { Hostile = true };
        var outer = conn.Monsters[301] = new("30 cells", new(130, 100, 0), 0, 0, 100000) { Hostile = true };
        for (int repeat = 0; repeat <= 2; repeat++)
        {
            uint centreLife = battle.Enemy.Life, innerLife = inner.Life, outerLife = outer.Life;
            await Game.DarkBargainBlast("test", battle.Stream, battle.Ciphers, conn, gem.Id, gem, (100, 100), paid,
                self: true, repeat, finalSpellRepeat: repeat == 2, criticalRoll: null, sacrifice, damageMore: 1.56);
            Assert.Equal(repeat >= 1, inner.Life < innerLife);
            Assert.Equal(repeat == 2, outer.Life < outerLife);
            var range = damage.Scale(1.56 * (1 + 0.1 * repeat));
            Assert.InRange((double)(centreLife - battle.Enemy.Life), Math.Floor(range.MinTotal) - 1, Math.Ceiling(range.MaxTotal) + 1);
        }
    }
}
