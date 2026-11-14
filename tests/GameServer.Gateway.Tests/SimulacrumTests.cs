using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Delirium;

namespace GameServer.Gateway.Tests;

public sealed class SimulacrumTests
{
    [Fact]
    public void Selected_recipe_uses_only_its_fragment_and_keeps_six_entries()
    {
        var slot = new MapDevice.Slot(InstanceEntities.ObjectTypeHash(SimulacrumRules.Fragment), "MapFragment", "Simulacrum", 3065, [], 0);
        var conn = new ClientState(new AreaLayout(1, "test", 32, 32, 16, 16, (_, _) => true, []), 1, 1);
        var request = new MapOpening(conn, [slot], false, null, "unresolved") { Target = 45986 };
        SimulacrumRules.Resolve(request, new Random(42));
        var opening = Assert.IsType<MapDevice.Opening>(request.Opening);
        Assert.Contains(opening.Area.Id, SimulacrumRules.ArenaIds);
        Assert.NotEqual("AnyAfflictionEndgameArea", opening.Area.Id);
        Assert.Equal(5, SimulacrumRules.Arenas().Select(a => a.Hash16).Distinct().Count());
        Assert.True(request.ReturnToSource);
        var mixed = new MapOpening(conn, [slot with { TypeHash = 123, Class = "MapFragment" }, slot], false, null, "") { Target = 45986 };
        SimulacrumRules.Resolve(mixed, new Random(42));
        Assert.NotNull(mixed.Opening);
        Assert.Equal(new[] { 1 }, mixed.ConsumeSlots);
        Assert.Same(MapMods.None, mixed.Opening!.Mods);
        var missing = new MapOpening(conn, [], false, null, "") { Target = 45986 };
        SimulacrumRules.Resolve(missing, new Random(42));
        Assert.Null(missing.Opening);
        const string owner = "SimulacrumPortalRegression";
        try
        {
            MapDevice.Activate(owner, opening.Area, returnTo: new(1, 123, 10, 10));
            MapDevice.Entered(owner, opening.Area, 456);
            for (int i = 0; i < 6; i++)
            {
                var portal = MapDevice.TakePortal(owner, i);
                Assert.NotNull(portal);
                Assert.Equal(456ul, portal.Value.Map.Instance);
                Assert.Equal(5 - i, portal.Value.Left);
            }
            Assert.NotNull(MapDevice.InInstance(opening.Area.Hash16, 456));
        }
        finally { MapDevice.Close(owner); }
    }

    [Fact]
    public void Wave_waits_for_the_last_spawn_and_enemy_and_cannot_pay_twice_after_reentry()
    {
        var run = new DeliriumModule.Run { Wave = 15, Active = true, Budget = 3, Spawned = 2, Monsters = [42] };
        Assert.False(run.TryComplete());
        run.Spawned = 3;
        Assert.False(run.TryComplete());
        var instance = new InstanceState();
        instance.ContentState["test"] = JsonSerializer.Serialize(run);
        var restored = JsonSerializer.Deserialize<DeliriumModule.Run>(instance.ContentState["test"])!;
        Assert.False(restored.TryComplete());
        restored.Monsters.Remove(42);
        Assert.True(restored.TryComplete());
        Assert.False(restored.TryComplete());
        var again = JsonSerializer.Deserialize<DeliriumModule.Run>(JsonSerializer.Serialize(restored))!;
        Assert.False(again.TryComplete());
        Assert.False(again.Active);
        Assert.True(again.Rewarded);
        Assert.Equal(75, SimulacrumRules.Level(1));
        Assert.Equal(83, SimulacrumRules.Level(15));
        Assert.Equal(84, SimulacrumRules.RewardLevel(15));
        Assert.Equal(100, SimulacrumRules.Delirious(15));
        Assert.Equal(7, SimulacrumRules.RewardTypes(15));

        var idle = DeliriumModule.StarterStates(new() { Wave = 8 });
        var active = DeliriumModule.StarterStates(new() { Wave = 9, Active = true });
        Assert.Equal(new[] { "active", "wave", "hello", "goodbye", "play_interlude" }, idle.Names);
        Assert.Equal(new[] { 0, 8, 0, 0, 0 }, idle.SharedStates); // Native prompt adds one: next wave 9/15.
        Assert.Equal(new[] { 1, 8, 0, 0, 0 }, active.SharedStates);
        Assert.Equal(new[] { 0, 14, 0, 1, 1 }, DeliriumModule.StarterStates(again).SharedStates);
        Assert.False(QuestObject.CanSerialize(DeliriumModule.StarterComponents));
        Assert.True(QuestObject.CanSerialize(DeliriumModule.StarterComponents, haveStateMachine: true));
        var blob = new QuestObject(10, 20, DeliriumModule.StarterComponents) { StateMachine = idle }.Blob();
        Assert.Equal(5, blob[21]); // Five shared states, not the local affliction_endgame_initiator selector.
        Assert.Equal(0, blob[22]); // No shared timers.
        Assert.All(idle.Overridden, i => Assert.InRange(i, 0, 4));
        Assert.Equal(5, SimulacrumRules.ArenaIds.Select(DeliriumModule.StarterType).Distinct().Count());

        var hidden = new StateMachineData(new[] { 0, 0 }, 0, new[] { "visible", "delayed_death" });
        var shown = DeliriumModule.MonsterStates(hidden)!;
        Assert.Equal(new[] { 1, 0 }, shown.SharedStates);
        Assert.Contains(0, shown.Overridden);
        Assert.Equal(0, hidden.SharedStates[0]); // Catalog defaults stay immutable.
        var population = new DeliriumModule.Run { Active = true, Budget = 150, NextSpawnAt = 1 };
        Assert.Equal(0, population.SpawnAllowance(.99, 15, 100));
        Assert.Equal(15, population.SpawnAllowance(1, 15, 100));
        population.Monsters = Enumerable.Range(1, 95).Select(i => (uint)i).ToHashSet();
        Assert.Equal(5, population.SpawnAllowance(1, 15, 100));
        population.Monsters.Add(96); population.Monsters.Add(97); population.Monsters.Add(98);
        population.Monsters.Add(99); population.Monsters.Add(100);
        Assert.Equal(0, population.SpawnAllowance(1, 15, 100));
        population.Monsters.Clear(); population.Spawned = 147;
        Assert.Equal(3, population.SpawnAllowance(1, 15, 100));
        var progress = new DeliriumModule.Run { Active = true, ProgressGoal = 5, SpawnPoints = 5, Monsters = [1, 2], MonsterPoints = new() { [1] = 1, [2] = 4 } };
        Assert.Equal(0, progress.SpawnAllowance(0, 15, 100));
        Assert.False(progress.TryComplete());
        Assert.True(progress.RecordKill(2));
        Assert.False(progress.RecordKill(2));
        Assert.Equal(4, progress.KillPoints);
        Assert.False(progress.TryComplete());
        var resumed = JsonSerializer.Deserialize<DeliriumModule.Run>(JsonSerializer.Serialize(progress))!;
        Assert.True(resumed.RecordKill(1));
        Assert.True(resumed.TryComplete());
        Assert.False(resumed.TryComplete());
        Assert.Equal(7190, Enumerable.Range(1, 15).Sum(SimulacrumRules.Population));

        // The next wave destroys every item on the ground: rewards, a player's own drop and one only its owner sees.
        var ground = new InstanceState().Ground;
        var loot = new Item(1, [], 0);
        ground.Add(new WorldItemObject(10, 10, loot));
        ground.Add(new WorldItemObject(11, 10, loot), new object());
        ground.Add(new WorldItemObject(12, 10, loot), new object(), shared: false);
        Assert.Equal(3, DeliriumModule.ClearGround(ground).Count);
        Assert.Empty(ground.All());
        Assert.Empty(DeliriumModule.ClearGround(ground));
    }
}
