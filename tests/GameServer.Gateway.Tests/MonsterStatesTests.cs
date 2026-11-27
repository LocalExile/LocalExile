using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// S5 <c>.ot</c> StateMachine shared-state metadata (<c>object_type_state_meta</c>, object-types v14),
/// data-driven default-visibility overrides, bespoke boss controller precedence, and runtime
/// <see cref="MonsterStateMachine"/> <c>0x2bc</c> snapshot encoding.
/// </summary>
public sealed class MonsterStatesTests
{
    [Fact]
    public void StateMeta_catalog_default_visibility_heist_parity_and_boss_precedence()
    {
        // 1. object_type_state_meta loads eagerly via ClientTables & MonsterCatalog with zero Lazy<>.
        Assert.True(ClientTables.Has("object_type_state_meta"));
        var catalog = new MonsterCatalog();
        Assert.Null(catalog.Error);

        // BasiliskFemaleViper: arrow_visible is a client GEAL self-writer (never lock bit 0x01),
        // use_arrow2 is a client listener, and visible disables rendering at default 0 -> enable 1.
        var viper = Assert.IsType<MonsterCatalog.Variety>(
            catalog.Find("Metadata/Monsters/AtlasExiles/BasiliskInfluenceMonsters/BasiliskFemaleViper"));
        Assert.True(viper.StateMachineComponentIndex >= 0);
        Assert.Equal("StateMachine", viper.ComponentsOrBase[viper.StateMachineComponentIndex]);
        Assert.True(viper.StateMeta.TryGetValue("arrow_visible", out var arrowMeta));
        Assert.True(arrowMeta.HasClientListener);
        Assert.True(arrowMeta.HasClientWriter);
        Assert.True(viper.StateMeta.TryGetValue("visible", out var viperVisibleMeta));
        Assert.True(viperVisibleMeta.DisablesRenderingAtDefault);
        Assert.Equal(1, viperVisibleMeta.EnableValue);

        // MonsterStates.DefaultVisibleStates turns visible 0 -> 1 without touching arrow_visible.
        var viperDefaults = Assert.IsType<StateMachineData>(MonsterStates.DefaultVisibleStates(viper, viper.StateMachine));
        Assert.True(viperDefaults.TryGet("visible", out int visibleVal));
        Assert.Equal(1, visibleVal);
        Assert.Contains(viperDefaults.IndexOf("visible"), viperDefaults.Overridden);
        Assert.DoesNotContain(viperDefaults.IndexOf("arrow_visible"), viperDefaults.Overridden);

        // LegionTemplarGeneral: holy_blast has a client listener and NO client GEAL self-writer.
        var templar = Assert.IsType<MonsterCatalog.Variety>(
            catalog.Find("Metadata/Monsters/LegionLeague/LegionTemplarGeneral"));
        Assert.True(templar.StateMeta.TryGetValue("holy_blast", out var holyBlastMeta));
        Assert.True(holyBlastMeta.HasClientListener);
        Assert.False(holyBlastMeta.HasClientWriter);
        Assert.False(holyBlastMeta.DisablesRenderingAtDefault);
        // Zero-alloc pass-through when a variety has no default-hidden states.
        Assert.Same(templar.StateMachine, MonsterStates.DefaultVisibleStates(templar, templar.StateMachine));

        // Heist comment-in-string quirk (client 0x141e4d930): Heist monsters must retain their 9th shared state.
        var heistMob = catalog.All.FirstOrDefault(v =>
            v.Chain.Any(c => c.Contains("HeistMonsterBase", StringComparison.OrdinalIgnoreCase))
            && v.StateMachine is not null);
        Assert.NotNull(heistMob);
        Assert.True(heistMob!.StateMachine!.SharedStates.Count >= 9);
        Assert.Contains(heistMob.StateMachine.Names!, n => n.StartsWith("/*Most monsters", StringComparison.Ordinal));

        // Bespoke boss controllers are isolated from S5 default-visibility fallback via Game.IsBossControllerVariety.
        var doedreSoul = Assert.IsType<MonsterCatalog.Variety>(catalog.Find("Metadata/Monsters/Doedre/DoedreSoul"));
        var sirus = Assert.IsType<MonsterCatalog.Variety>(catalog.Find("Metadata/Monsters/AtlasExiles/AtlasExile5"));
        Assert.True(Game.IsBossControllerVariety(doedreSoul));
        Assert.True(Game.IsBossControllerVariety(sirus));
        Assert.False(Game.IsBossControllerVariety(viper));
    }

    [Fact]
    public void MonsterStateMachine_TrySet_is_noop_when_unchanged_syncs_origins_and_encodes_0x2bc_wire_flags()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var templar = Assert.IsType<MonsterCatalog.Variety>(
            Game.Monsters.Find("Metadata/Monsters/LegionLeague/LegionTemplarGeneral"));
        Assert.NotNull(templar.StateMachine);
        Assert.True(templar.StateMachineComponentIndex >= 0);

        const uint monsterId = 0x7701;
        var initialStates = templar.StateMachine!;
        int holyIdx = initialStates.IndexOf("holy_blast");
        Assert.True(holyIdx >= 0);
        Assert.Equal(0, initialStates.SharedStates[holyIdx]);

        var monster = new SpawnedMonster("Templar", new PlayerWalk(100, 100, 15, (_, _) => true), 60, 5, 1000)
        {
            Variety = templar,
            States = initialStates,
        };
        var monsterObj = new MonsterObject(100, 100, 1000, templar.ComponentsOrBase, templar.AnimatedObject,
            MonsterRarity.Unique, templar.Name, initialStates);
        player.Instance.World.MonsterOrigins[monsterId] = new(0x12345678u, monsterObj, (ushort)templar.Row, 80, MonsterRarity.Unique);

        // 1. Unknown state or unchanged server-owned state is a zero-alloc no-op.
        Assert.False(MonsterStateMachine.TrySet(player, monsterId, monster, "nonexistent_state", 1, out var noPacket));
        Assert.Null(noPacket);
        Assert.False(MonsterStateMachine.TrySet(player, monsterId, monster, "holy_blast", 0, out noPacket));
        Assert.Null(noPacket);
        Assert.Same(initialStates, monster.States);

        // 2. Real transition updates SpawnedMonster.States + MonsterOrigins and produces exact 0x2bc wire bytes.
        Assert.True(MonsterStateMachine.TrySet(player, monsterId, monster, "holy_blast", 1, out var packet));
        Assert.NotNull(packet);
        Assert.True(monster.States!.TryGet("holy_blast", out int newVal));
        Assert.Equal(1, newVal);
        Assert.Same(monster.States, player.Instance.World.MonsterOrigins[monsterId].Object.StateMachine);

        // Verify S->C 0x2bc wire layout (InstanceEntities.ComponentUpdate: u16 0x02bc BE, u32 id BE, u32 orderKey,
        // u16 checkpoint, u16 blobLen BE, u8 compIdx, then StateMachine blob).
        Assert.Equal(0x02, packet![0]);
        Assert.Equal(0xbc, packet[1]);
        Assert.Equal((byte)templar.StateMachineComponentIndex, packet[14]);
        // StateMachine blob starts at offset 15: u8 stateCount, u8 timerCount, then per-state (compactInt value + u8 flags).
        Assert.Equal((byte)initialStates.SharedStates.Count, packet[15]);
        Assert.Equal((byte)initialStates.SharedTimers, packet[16]);
        // Every shared state on LegionTemplarGeneral is small (0 or 1 -> 1-byte compact int), so entry i is at 17 + 2*i.
        int flagOffset = 17 + 2 * holyIdx + 1;
        Assert.Equal(0x02, packet[flagOffset]);          // bit 0x02 (override) set
        Assert.Equal(0, packet[flagOffset] & 0x01);      // bit 0x01 (client GEAL lockout) NEVER set

        // 3. Setting the same value again is a no-op; multi-state span overload transitions back to 0 atomically.
        Assert.False(MonsterStateMachine.TrySet(player, monsterId, monster, "holy_blast", 1, out noPacket));
        Assert.Null(noPacket);

        ReadOnlySpan<(string Name, int Value)> reset = [("holy_blast", 0), ("missing", 9)];
        Assert.True(MonsterStateMachine.TrySet(player, monsterId, monster, reset, out var resetPacket));
        Assert.NotNull(resetPacket);
        Assert.True(monster.States.TryGet("holy_blast", out int afterReset));
        Assert.Equal(0, afterReset);

        // 4. MonsterKitHooks.AddActionLifecycleObserver registers, fires, and detaches cleanly on Dispose.
        var phases = new List<(int Effect, MonsterKitHooks.ActionLifecyclePhase Phase)>();
        using (MonsterKitHooks.AddActionLifecycleObserver((_, _, _, eff, phase, _) => phases.Add((eff, phase))))
        {
            Assert.True(MonsterKitHooks.HasActionLifecycleObservers);
            MonsterKitHooks.NotifyActionLifecycle(player, monsterId, monster, 42, MonsterKitHooks.ActionLifecyclePhase.Started, 1.0);
            MonsterKitHooks.NotifyActionLifecycle(player, monsterId, monster, 42, MonsterKitHooks.ActionLifecyclePhase.Ended, 2.0);
        }
        Assert.False(MonsterKitHooks.HasActionLifecycleObservers);
        Assert.Equal([(42, MonsterKitHooks.ActionLifecyclePhase.Started), (42, MonsterKitHooks.ActionLifecyclePhase.Ended)], phases);
    }
}
