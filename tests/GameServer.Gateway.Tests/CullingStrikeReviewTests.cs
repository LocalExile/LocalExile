using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The review of the target-state culling PR: the cached culling context must reach every victim of an area hit,
// recovery must follow the shared pool rules, and Righteous Fire is a supported burning source.
public sealed class CullingStrikeReviewTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Stat.Of(s.Name), s => s.Value);

    static Player Player(uint life = 1000, uint mana = 1000) =>
        new Player(new(1, "cull-review", 300, 300, 100, 100, null, []), 1, 1)
        {
            Pools = new(1, life, mana, 0, 0), PlayerLife = life, PlayerMana = mana,
        };

    static SpawnedMonster Target(string label) => new(label, new(100, 100, 0), 0, 0, 1000);

    static Game.Cull Plain => new(0, 0);

    [Fact]
    public void Conditional_culling_reaches_every_area_victim_and_righteous_fire_burning_enemies()
    {
        {
            var player = Player();
            player.PlayerLife = 500; player.PlayerMana = 500;
            var stats = With(("culling_strike_vs_cursed_enemies", 1), ("life_gained_on_cull", 50), ("mana_gained_on_cull", 50));
            SpawnedMonster cursed(string label)
            {
                var m = Target(label);
                m.Curses.Put(new MonsterCurses.Curse(1, "Enfeeble", 1, 1, 1, 0, 100, false, false, new Dictionary<uint, int>()), 1);
                return m;
            }

            // The first target has a freshly built snapshot; later ones pass null for Rampage but keep the attack's stats.
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, cursed("first"), 900, Plain, playerStats: stats, cullPlayer: stats).Life);
            Assert.Equal(550u, player.PlayerLife);
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, cursed("second"), 900, Plain, playerStats: null, cullPlayer: stats).Life);
            Assert.Equal(600u, player.PlayerLife);
            Assert.Equal(600u, player.PlayerMana);
        }
        {
            var player = Player();
            var monster = Target("rf burn");
            player.Area.Monsters[42] = monster;
            var rf = new RighteousFireState(1, 2, 3, 0, 0) { Radius = 10 };
            rf.Auras[42] = 4;
            player.RighteousFire = rf;
            var stats = With(("culling_strike_on_burning_enemies", 1));
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, monster, 900, Plain, cullPlayer: stats).Life);
            // The DoT tick itself never goes through this hit path; only a hit against the burning enemy culls.
            player.Area.Monsters.Clear(); player.RighteousFire = null;
            Assert.Equal(100u, Game.TakeOwnerHitLocked(player, Target("no longer burning"), 900, Plain, cullPlayer: stats).Life);
        }
    }

    [Fact]
    public void Flat_life_and_mana_on_cull_follow_the_shared_recovery_rules()
    {
        var gain = With(("life_gained_on_cull", 150));
        var cull = new Game.Cull(10, 0);
        {
            var player = Player(); player.PlayerLife = 490;
            player.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
                manaPercent: 3500, lowLifeThresholdPct: 50, buff: 7, now: 0);
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, Target("cull"), 900, cull, cullPlayer: gain).Life);
            Assert.Equal(500u, player.PlayerLife); // shared recovery stops at Low Life
        }
        {
            var player = Player(); player.PlayerLife = 590;
            player.Auras[1] = new AuraCatalog.Aura(1, 1, "Arrogance", "Arrogance", 1, 1, 0, 4000, ReservesLife: true);
            Assert.Equal(600u, player.PlayerUnreservedLife);
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, Target("cull"), 900, cull, cullPlayer: gain).Life);
            Assert.Equal(600u, player.PlayerLife); // a gain cannot cross reserved Life
        }
        {
            var player = Player(); player.PlayerLife = 490;
            var blocked = With(("life_gained_on_cull", 150), ("cannot_recover_life", 1));
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, Target("cull"), 900, cull, cullPlayer: blocked).Life);
            Assert.Equal(490u, player.PlayerLife);
        }
        {
            // Mana reaching full still signals the client, so later regeneration cannot hide a missing update.
            var player = Player(); player.PlayerMana = 950;
            var leech = Game.LeechOf(player);
            leech.InstantManaChanged = false;
            Assert.Equal(0u, Game.TakeOwnerHitLocked(player, Target("cull"), 900, cull,
                cullPlayer: With(("mana_gained_on_cull", 100))).Life);
            Assert.Equal(1000u, player.PlayerMana);
            Assert.True(leech.InstantManaChanged);
        }
    }
}
