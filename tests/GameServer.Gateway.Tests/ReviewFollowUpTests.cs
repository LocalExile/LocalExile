using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Follow-ups from the #849 review notes on the merged #819/#824 and #823 changes: one Arcane Surge helper for the
// support and generic sources (no leftover values, the support's 0.25 s refresh limit), Onslaught refresh
// throttling, and knockback that reads the skill's own stats and skips the states HitReaction and the AI skip.
public sealed class ReviewFollowUpTests
{
    static Player PlayerOf()
    {
        var player = new Player(new(1, "review-followup", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "ReviewFollowUpChar" },
            PlayerLife = 1000,
            PlayerMana = 100,
            Instance = new()
        };
        player.Area.Monsters[1] = new SpawnedMonster("target", player.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100);
        return player;
    }

    [Fact]
    public void Arcane_surge_shares_one_helper_and_onslaught_does_not_readd_every_hit()
    {
        var player = PlayerOf();
        // A support surge with stronger values expires; a later generic surge must not inherit them (the old generic
        // path used Math.Max against the stale state).
        Game.GainArcaneSurgeLocked(player, 0, 4000, 35, 40);
        Assert.Equal(4, player.SupportGems.ArcaneSurgeUntil);
        Game.GainArcaneSurgeLocked(player, 10);
        Assert.Equal(14, player.SupportGems.ArcaneSurgeUntil);
        Assert.Equal(Game.ArcaneSurgeCastSpeed, player.SupportGems.ArcaneSurgeCastSpeedPct);
        Assert.Equal(Game.ArcaneSurgeManaRegen, player.SupportGems.ArcaneSurgeManaRegenPct);
        // The shown deadline only moves by a further gain after the support's 0.25 s refresh limit.
        double shown = player.SupportGems.ArcaneSurgeShownUntil;
        player.SupportGems.ArcaneSurgeRefresh = false;
        Game.GainArcaneSurgeLocked(player, 10.1);
        Assert.False(player.SupportGems.ArcaneSurgeRefresh);
        Assert.Equal(shown, player.SupportGems.ArcaneSurgeShownUntil);

        // Onslaught: a hit every 0.1 s extends Until, but the shown buff is re-added at most once a second, and the
        // stats change only when the buff starts.
        var onslaught = Game.KillOnslaughtOf(player);
        onslaught.HitMs = 4000;
        int adds = 0, statsUpdates = 0;
        for (int i = 0; i <= 30; i++)
        {
            double now = i * .1;
            onslaught.Hit(now);
            if (onslaught.Present(now, alive: true, hidden: false) is { } change)
            {
                if (change.Seconds is not null) { onslaught.Buff = 1; adds++; }
                if (change.Stats) statsUpdates++;
            }
        }
        Assert.InRange(adds, 1, 4);
        Assert.Equal(1, statsUpdates);
    }

    [Fact]
    public void Knockback_uses_the_skill_stats_and_skips_scripted_states()
    {
        // The wiki's minimum 0.4 m is four cells, and knockback_distance_+% scales from there.
        Assert.Equal((0, 9), Game.KnockbackDestination((0, 0), (0, 5), 4, (_, _) => true));

        // The skill's own Knockback Support stats (25-44% chance, 50% increased distance) add to the character's.
        var player = PlayerOf();
        var target = player.Area.Monsters[1];
        uint chance = Stat.BaseGlobalChanceToKnockbackPct;
        uint distance = Stat.KnockbackDistancePlusPct;
        var skill = new Dictionary<uint, int> { [chance] = 100, [distance] = 50 };
        Assert.True(Game.KnockbackOnHitLocked(player, target, new Dictionary<uint, int>(), skill, 0, new Random(1)));
        Assert.Equal((111, 100), target.Walk.Position(0));
        Assert.Equal((111, 100), target.KnockbackPending);

        // Staged, scripted and asleep monsters are skipped, as HitReaction and the AI skip them.
        foreach (var skipped in new[]
        {
            new SpawnedMonster("stage", player.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100) { StageRunning = true },
            new SpawnedMonster("scripted", player.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100) { Scripted = true },
            new SpawnedMonster("asleep", player.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100) { Asleep = true },
        })
        {
            Assert.False(Game.KnockbackOnHitLocked(player, skipped, new Dictionary<uint, int>(), skill, 0, new Random(1)));
            Assert.Null(skipped.KnockbackPending);
        }
    }
}
