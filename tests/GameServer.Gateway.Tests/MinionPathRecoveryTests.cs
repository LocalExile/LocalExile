namespace GameServer.Gateway.Tests;

public sealed class MinionPathRecoveryTests
{
    [Fact]
    public async Task A_follower_with_no_path_does_not_send_a_walking_action_and_can_retry_when_a_path_opens()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Raise Zombie", 20));
        var variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
        f.Player.Area.Monsters[901] = new("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = variety, Level = 70 };
        var id = await f.Summon("RaiseZombie", 901);
        var fighter = f.Player.Summons.Fighters[id];
        f.Player.Use(f.Player.Layout with { Walkable = (_, _) => true });
        f.Player.Walk.Teleport(150, 100, 0);
        fighter.Monster.Walk = new(100, 100, 8, (x, y) => x == 100 && y == 100);
        f.Player.Minions.OutputQueue.Clear();
        Game.GolemStepLocked(f.Player, fighter, 10);
        Assert.Equal((100, 100), fighter.Monster.Walk.Destination);
        Assert.DoesNotContain(f.Player.Minions.OutputQueue, o => o.Packet.Length > 1 && o.Packet[0] == 1 && o.Packet[1] == 0xab);
        fighter.Monster.Walk = new(100, 100, 8);
        f.Player.Minions.OutputQueue.Clear();
        Game.GolemStepLocked(f.Player, fighter, 11);
        Assert.NotEqual((100, 100), fighter.Monster.Walk.Destination);
        Assert.Contains(f.Player.Minions.OutputQueue, o => o.Packet.Length > 1 && o.Packet[0] == 1 && o.Packet[1] == 0xab);
    }

    [Fact]
    public void Stone_golem_placement_clears_player_body_skitterbot_ailment_uses_stat_names_and_fire_trap_uses_native_throw()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Player.Use(f.Player.Layout with { Walkable = (_, _) => true, Clearance = null });
        f.Player.Walk.Teleport(100, 100, 0);

        // Stone Golem (ObjectSize = 3 -> bodySize = 2) must spawn outside the player's collision hull (Chebyshev >= 4).
        var spot = Game.NextToPlayer(f.Player, bodySize: 2);
        Assert.True(Math.Max(Math.Abs(spot.X - 100), Math.Abs(spot.Y - 100)) >= 4, $"expected Chebyshev >= 4, got {spot}");

        // SkitterAilmentStat must resolve minion_chill_effect_+% via stat name, not numeric stat ID.
        var skitterMonster = new SpawnedMonster("skitter", new(100, 100, 0), 0, 0, 100) { Hostile = false };
        var skitterDef = new GolemCatalog.Definition(0, "SummonSkitterbots", "SkitterCold",
            "Metadata/Monsters/Skitterbot/SkitterbotCold", 0, [], false, false, IsGolem: false);
        var skitter = new GolemState.Golem(902, skitterMonster, skitterDef, 0)
        {
            Player = new() { [Stat.Of("minion_chill_effect_+%")] = 40 },
        };
        Assert.Equal(40, Game.SkitterAilmentStat(skitter, Stat.VirtualChillEffectPlusPct));

        // PlacedThrowRecords must let BeThrown drive flight natively without appending an overwriting 0x01ba MoveTo.
        var records = Game.PlacedThrowRecords(f.Player, 903, (100, 100), (125, 100), 0.975);
        Assert.Single(records);
        Assert.Equal(0x01, records[0][0]);
        Assert.Equal(0xab, records[0][1]);
        Assert.DoesNotContain(records, r => r.Length >= 2 && r[0] == 0x01 && r[1] == 0xba);

        // Fire Trap base radius matches native TrapGroundFire (16).
        ushort fireTrap = f.Equip(("Fire Trap", 1)).Single();
        var gem = Game.GemSkillEffects(f.Player).Single(g => g.Id == fireTrap);
        Assert.Equal(16, Game.SkillRadius(f.Player, new Dictionary<uint, int>(), spell: true, gem.Effect));
    }
}
