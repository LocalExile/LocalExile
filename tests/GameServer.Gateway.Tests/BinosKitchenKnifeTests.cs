using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

// Bino's Kitchen Knife (Modules/Core/Game/Combat/PoisonSpreadOnKill.cs): its definition has the spread mod, and a kill of
// a poisoned enemy with it worn spreads the strongest stack within 3 metres and starts the wielder's regeneration.
public sealed class BinosKitchenKnifeTests
{
    [Fact]
    public void Killing_a_poisoned_enemy_spreads_its_strongest_poison_and_regenerates_life()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-binos-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 1, 1)
            {
                Character = "Binos" + Guid.NewGuid().ToString("N"), ClassRow = 1, PlayerSent = true, Instance = new(),
            };
            conn.Clock.Reset();
            var items = Game.ItemsFor(conn);
            var knife = new UniqueCatalog(items, "data").Create("Bino's Kitchen Knife", new Random(1));
            Assert.NotNull(knife);
            Assert.Contains(knife.Mods, m => m.Hash == items.Mods.ById("PoisonSpreadAndHealOnPoisonedKillUniqueDagger8")!.Hash);
            conn.PlayerStats[Program.Stats.Key("additional_dexterity")!.Value] = 300;
            conn.PlayerStats[Program.Stats.Key("additional_intelligence")!.Value] = 300;
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, knife);
            conn.Pools = Game.PoolsFor(conn, 85);
            conn.PlayerLife = conn.Pools.Life;
            _ = Game.CharacterStatSources(conn);

            double t = Game.ProjectileWorldTime(conn);
            var (x, y) = conn.Walk.Position(t);
            SpawnedMonster At(int dx, bool hostile = true) => new("enemy", new PlayerWalk(x + dx, y, 0), 100, 8, 100000) { Life = 100000, Hostile = hostile };
            var dead = At(0);
            var near = At(25);
            var far = At(60);
            var immune = At(15);
            immune.TemporaryStats["poison immunity"] = new Dictionary<string, int> { ["cannot_be_poisoned"] = 1 };
            var ally = At(10, false); ally.Life = 100;
            var farAlly = At(60, false); farAlly.Life = 100;
            var scaledAlly = At(5, false); scaledAlly.Life = 100;
            scaledAlly.TemporaryStats["recovery"] = new Dictionary<string, int>
            { ["life_regeneration_rate_+%"] = 100, ["life_recovery_rate_+%"] = 50 };
            var preventedAlly = At(5, false); preventedAlly.Life = 100;
            preventedAlly.TemporaryStats["no regeneration"] = new Dictionary<string, int> { ["no_life_regeneration"] = 1 };
            dead.Poisons.Add(t, 50, 4);
            dead.Poisons.Add(t, 200, 3);
            dead.Dead = true; dead.Life = 0;
            dead.Poisons.ClearAtDeath(t);
            lock (conn.Monsters)
            {
                conn.Monsters[1] = dead; conn.Monsters[2] = near; conn.Monsters[3] = far;
                conn.Monsters[4] = immune; conn.Monsters[5] = ally; conn.Monsters[6] = farAlly;
                conn.Monsters[7] = scaledAlly; conn.Monsters[8] = preventedAlly;
            }

            Game.SpreadPoisonOnKill(new KillContext(conn, dead, "enemy", (x, y)) { CommittedOutput = [] });

            Assert.Equal(1, near.Poisons.Stacks(t));
            Assert.Equal(200, near.Poisons.TotalDps(t));
            Assert.Equal(t + 2, near.Poisons.Until, 3);
            Assert.False(far.Poisons.Poisoned);
            Assert.False(immune.Poisons.Poisoned);
            Assert.False(ally.Poisons.Poisoned);
            // Allied monsters (including minions) use the shared regeneration step. The grant is not multiplied by
            // how many players tick a shared instance, and it stops exactly at its deadline inside a longer step.
            Game.RegenerateMonstersLocked(conn.Monsters, .5, t + .5);
            Assert.Equal(300u, ally.Life);
            Game.RegenerateMonstersLocked(conn.Monsters, .5, t + .5);
            Assert.Equal(300u, ally.Life);
            Game.RegenerateMonstersLocked(conn.Monsters, 2.5, t + 3);
            Assert.Equal(900u, ally.Life);
            Assert.Equal(100u, farAlly.Life);
            Assert.Equal(2500u, scaledAlly.Life);
            Assert.Equal(100u, preventedAlly.Life);
            // A kill that is not the player's (a minion's) spreads nothing.
            Game.SpreadPoisonOnKill(new KillContext(conn, dead, "enemy", (x, y)) { PlayerKill = false, CommittedOutput = [] });
            Assert.Equal(1, near.Poisons.Stacks(t));
            // A direct killing hit snapshots poison immediately; expiry during deferred kill output cannot erase it.
            near.Poisons.Add(t, 999, .1);
            Assert.NotNull(Game.ApplyMonsterHitLocked(conn, near, 100000));
            near.Poisons.Advance(t + 5, 1);
            Assert.Equal(999, near.Poisons.Strongest(t + 5)!.Value.DamagePerSecond);

            uint regen = Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value;
            int before = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(regen);
            Assert.NotNull(Game.SpreadPoisonRegenerationPresentation(conn));
            Assert.Equal(before + SpreadPoisonRegeneration.PerMinute, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(regen));
            // Death ends it.
            conn.PlayerLife = 0;
            Assert.NotNull(Game.SpreadPoisonRegenerationPresentation(conn));
            Assert.False(Game.SpreadPoisonRegenerationOf(conn).Applied);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
    [Fact]
    public void Spread_preserves_the_lethal_poison_and_does_not_apply_faster_damage_twice()
    {
        var global = new Dictionary<string, int>
        {
            ["poison_duration_+%"] = 50, ["base_all_ailment_duration_+%"] = 25,
            ["faster_poison_%"] = 100, ["damaging_ailments_deal_damage_+%_faster"] = 50,
        };
        Assert.Equal(3.5, Game.SpreadPoisonSeconds(global.GetValueOrDefault));
        var poison = new MonsterPoisons();
        poison.Add(0, 200, .2);
        poison.Add(0, 10, 4);
        // The stronger stack delivers the fatal damage before expiring inside this tick. The kill callback may
        // run after the tick or a network await; the surviving weak stack must not replace the lethal snapshot.
        Assert.Equal(47, poison.Advance(.75, 1, lifeAtStart: 30));
        poison.ClearAtDeath(.75, poisonDeath: true);
        Assert.True(poison.DiedPoisoned);
        Assert.Equal(200, poison.Strongest(10)!.Value.DamagePerSecond);
        poison.Clear();
        Assert.Null(poison.Strongest(10));
        poison.Add(10, 50, 1);
        poison.RememberDeath(10);
        poison.Add(11, 25, 1);
        Assert.Equal(25, poison.Strongest(11)!.Value.DamagePerSecond);
        poison.RememberDeath(15);
        Assert.Null(poison.Strongest(15));
        var expired = new MonsterPoisons();
        expired.Add(0, 200, .2);
        expired.Advance(.7, 1);
        expired.Clear();
        Assert.Null(expired.Strongest(.7));

        var regen = new SpreadPoisonRegeneration();
        ushort next = 1;
        regen.Grant(0, 2, trackRecovery: true);
        Assert.NotNull(regen.Present(0, true, () => next++));
        regen.Grant(.5, 2, trackRecovery: true);
        Assert.Null(regen.Present(.5, true, () => next++));
        // A small extension is republished at the previous visible deadline instead of allowing an early icon expiry.
        Assert.NotNull(regen.Present(2, true, () => next++));
        Assert.Equal(1000, regen.Recover(3));
        Assert.Equal(0, regen.Recover(3));
        regen.Grant(4, 2, trackRecovery: true);
        Assert.Equal(400, regen.Recover(5));
        Assert.NotNull(regen.Present(5, false, () => next++));
        Assert.Equal(0, regen.Recover(6));
    }

}
