using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class DoryaniPrototypeTests
{
    [Fact]
    public async Task Doryanis_prototype_sets_nearby_enemy_lightning_resistance_including_zero_and_caps()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var statOverrides = new Dictionary<uint, int> { [Stat.NearbyEnemiesLightningResistEqualToYours] = 1 };
        IDisposable? statHook = null;
        var player = runtime.PlayerOf(new(), wire, "Doryani" + Guid.NewGuid().ToString("N"));
        try
        {
            player.PlayerLife = 1000;
            player.Pools = new(60, 1000, 1000, int.MaxValue, 0, LightningResistance: -50, LightningMaxResistance: 75);
            player.Clock.Stop();
            player.Walk.Teleport(100, 100, 0);
            statHook = GameEvents.PlayerStatSources.Add("Game", (p, sources) =>
            {
                if (!ReferenceEquals(p, player) || statOverrides.Count == 0) return sources;
                var copy = new Dictionary<uint, int>(sources);
                foreach (var (key, value) in statOverrides) copy[key] = value;
                return copy;
            });

            // Nearby monster (50 cells <= 60-cell aura) and distant monster (70 cells > 60-cell aura),
            // both with +40% lightning resistance from mods.
            var near = new SpawnedMonster("near", new(150, 100, 0), 0, 0, 1000) { Hostile = true };
            var far = new SpawnedMonster("far", new(170, 100, 0), 0, 0, 1000) { Hostile = true };
            var resistStats = new Dictionary<uint, int> { [Stat.BaseLightningDamageResistancePct] = 40 };
            near.SetTemporaryStats("resist", resistStats);
            far.SetTemporaryStats("resist", resistStats);
            player.Area.Monsters[10] = near;
            player.Area.Monsters[11] = far;
            player.Area.Grid.Set(new(player.Id), (100, 100));
            player.Area.Grid.Set(new(10), (150, 100));
            player.Area.Grid.Set(new(11), (170, 100));
            player.Area.Zones.Update(moved: true);

            int baseNear = Game.MonsterHitResistance(near, DamageType.Lightning, default).Value;
            Assert.Equal(40, baseNear);

            // -50% player lightning resistance overrides nearby monster and preserves penetration;
            // distant monster (>60 cells) keeps its own resistance.
            await Game.EnemyDebuffsTick(player, 1);
            player.Area.Zones.Update(moved: true);
            await Game.EnemyDebuffsTick(player, 1.1);
            var pen = new Penetration.PerType(0, 0, 15, 0, 0);
            var hitNear = Game.MonsterHitResistance(near, DamageType.Lightning, pen);
            Assert.Equal(-50, hitNear.Value);
            Assert.Equal(15, hitNear.Penetration);
            Assert.Equal(baseNear, Game.MonsterHitResistance(far, DamageType.Lightning, default).Value);

            // 0% player lightning resistance must STILL override nearby enemies to 0% (not drop the aura entry).
            player.Pools = player.Pools with { LightningResistance = 0 };
            await Game.EnemyDebuffsTick(player, 2);
            Assert.Equal(0, Game.MonsterHitResistance(near, DamageType.Lightning, default).Value);

            // Overcapped player resistance (+120% with 75% max) caps to the player's effective 75%.
            player.Pools = player.Pools with { LightningResistance = 120 };
            await Game.EnemyDebuffsTick(player, 3);
            Assert.Equal(75, Game.MonsterHitResistance(near, DamageType.Lightning, default).Value);

            // Removing the chest stat clears the override and restores the monster's own resistance.
            statOverrides.Clear();
            await Game.EnemyDebuffsTick(player, 4);
            Assert.Equal(baseNear, Game.MonsterHitResistance(near, DamageType.Lightning, default).Value);

            // Re-enable and verify ForgetSourceValues cleans up on area leave.
            statOverrides[Stat.NearbyEnemiesLightningResistEqualToYours] = 1;
            player.Pools = player.Pools with { LightningResistance = -40 };
            await Game.EnemyDebuffsTick(player, 5);
            player.Area.Zones.Update(moved: true);
            await Game.EnemyDebuffsTick(player, 5.1);
            Assert.Equal(-40, Game.MonsterHitResistance(near, DamageType.Lightning, default).Value);
            Game.ForgetSourceValues(player);
            Assert.Equal(baseNear, Game.MonsterHitResistance(near, DamageType.Lightning, default).Value);
        }
        finally
        {
            player.Closed = true;
            statHook?.Dispose();
        }
    }
}
