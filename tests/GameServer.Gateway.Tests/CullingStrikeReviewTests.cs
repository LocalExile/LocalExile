using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The review of the target-state culling PR: the cached culling context must reach every victim of an area hit,
// recovery must follow the shared pool rules, and Righteous Fire is a supported burning source.
public sealed class CullingStrikeReviewTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name)!.Value, s => s.Value);

    static ClientState Player(uint life = 1000, uint mana = 1000) =>
        new ClientState(new(1, "cull-review", 300, 300, 100, 100, null, []), 1, 1)
        {
            Pools = new(1, life, mana, 0, 0), PlayerLife = life, PlayerMana = mana,
        };

    static Game.Cull Plain => new(0, 0);

    [Fact]
    public void Later_area_victims_keep_the_cached_culling_context_and_recovery()
    {
        var conn = Player();
        conn.PlayerLife = 500; conn.PlayerMana = 500;
        var stats = With(("culling_strike_vs_cursed_enemies", 1), ("life_gained_on_cull", 50), ("mana_gained_on_cull", 50));
        SpawnedMonster target(string label)
        {
            var m = new SpawnedMonster(label, new(100, 100, 0), 0, 0, 1000);
            m.Curses.Put(new MonsterCurses.Curse(1, "Enfeeble", 1, 1, 1, 0, 100, false, false, new Dictionary<string, int>()), 1);
            return m;
        }

        // The first target has a freshly built snapshot; later ones pass null for Rampage but keep the attack's stats.
        Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, target("first"), 900, Plain, player: stats, cullPlayer: stats).Life);
        Assert.Equal(550u, conn.PlayerLife);
        Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, target("second"), 900, Plain, player: null, cullPlayer: stats).Life);
        Assert.Equal(600u, conn.PlayerLife);
        Assert.Equal(600u, conn.PlayerMana);
    }

    [Fact]
    public void Flat_life_on_cull_uses_the_shared_recovery_limits()
    {
        var gain = With(("life_gained_on_cull", 150));
        var cull = new Game.Cull(10, 0);
        SpawnedMonster target() => new("cull", new(100, 100, 0), 0, 0, 1000);

        {
            var conn = Player(); conn.PlayerLife = 490;
            conn.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
                manaPercent: 3500, lowLifeThresholdPct: 40, buff: 7, now: 0);
            Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, target(), 900, cull, cullPlayer: gain).Life);
            Assert.Equal(490u, conn.PlayerLife); // above Low Life, so Life recovery stops where it is
        }
        {
            var conn = Player(); conn.PlayerLife = 490;
            var blocked = With(("life_gained_on_cull", 150), ("cannot_recover_life", 1));
            Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, target(), 900, cull, cullPlayer: blocked).Life);
            Assert.Equal(490u, conn.PlayerLife);
        }
    }

    [Fact]
    public void Flat_mana_on_cull_signals_the_client_even_when_it_reaches_full()
    {
        var conn = Player();
        conn.PlayerMana = 950;
        var gain = With(("mana_gained_on_cull", 100));
        var leech = Game.LeechOf(conn);
        leech.InstantManaChanged = false;
        Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, new("cull", new(100, 100, 0), 0, 0, 1000), 900,
            new Game.Cull(10, 0), cullPlayer: gain).Life);
        Assert.Equal(1000u, conn.PlayerMana);
        Assert.True(leech.InstantManaChanged);
    }

    [Fact]
    public void Righteous_fire_burning_enemies_are_culled_by_the_burning_condition()
    {
        var conn = Player();
        var monster = new SpawnedMonster("rf burn", new(100, 100, 0), 0, 0, 1000);
        conn.Monsters[42] = monster;
        var rf = new RighteousFireState(1, 2, null, 3, 0, 0, 10);
        rf.Auras[42] = 4;
        conn.RighteousFire = rf;
        var stats = With(("culling_strike_on_burning_enemies", 1));
        Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, monster, 900, Plain, cullPlayer: stats).Life);
        // The DoT tick itself never goes through this hit path; only a hit against the burning enemy culls.
        conn.Monsters.Clear(); conn.RighteousFire = null;
        Assert.Equal(100u, Game.TakeOwnerHitLocked(conn, new("no longer burning", new(100, 100, 0), 0, 0, 1000),
            900, Plain, cullPlayer: stats).Life);
    }
}
