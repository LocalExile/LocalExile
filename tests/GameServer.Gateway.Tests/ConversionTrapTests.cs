using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ConversionTrapTests
{
    [Fact]
    public async Task Conversion_trap_changes_a_normal_enemys_allegiance_and_target_until_the_effect_expires()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Conversion Trap", 1)));
        var supported = Game.SupportedSkill(player, skill);
        Assert.True(supported.RequirementsMet);
        uint mana = player.PlayerMana;
        uint cost = Game.ManaCost(Program.Stats, Game.SkillsFor(player), supported.Effect, supported.Level, 0);
        var clock = new AreaClock();
        var prior = Game.AreaUpdateContext.Value;
        var outgoing = Program.Outgoing;
        var packets = new List<byte[]>();
        Program.Outgoing = (recipient, bytes) => { if (ReferenceEquals(recipient, player)) packets.Add(bytes.ToArray()); };
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var placed = await Throw(player, skill, preflight: true);
            Assert.Equal(mana - cost, player.PlayerMana);
            Assert.Equal(1, Game.SingleUseState(player, supported.Effect, skill)!.SpentUses);

            var converted = SpawnEnemy(player, 704, placed.At, variety: Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"));
            converted.Target = player;
            var enemy = SpawnEnemy(player, 705, (placed.At.X + 25, placed.At.Y));
            clock.At = placed.Armed + .01;
            packets.Clear();
            await Tick(player, clock.At);
            double convertedAt = clock.At;
            Assert.Empty(player.Placed.All);
            Assert.False(converted.Hostile);
            Assert.True(converted.OriginallyHostile);
            Assert.True(Assert.Single(SavedMonster.Save([converted], clock.At)).Hostile);
            Assert.Equal(converted.MaxLife, converted.Life);
            Assert.Null(converted.Target);
            Assert.True(enemy.Hostile);
            Assert.True(player.Area.Buffs.HasRow(converted.ObjectId, 165));
            byte[] setTeamPlayer = InstanceProtocol.SetTeam(converted.ObjectId, 0);
            Assert.Equal(new byte[] { 0x01, 0xbe, 0x00, 0x00, 0x02, 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, setTeamPlayer);
            Assert.Contains(packets, p => p.AsSpan().IndexOf(setTeamPlayer) >= 0);

            // Rooted / immobile converted monsters must not be ordered to walk or chase out-of-reach hostiles.
            var cadence = new MonsterAiCadence();
            converted.Walk.BlockMovementUntil(clock.At + .1, clock.At);
            var rootedPlan = Game.PlanMonsterAi(player, cadence, clock.At);
            Assert.Empty(rootedPlan.Moves);
            Assert.Empty(rootedPlan.MinionSwings);

            // Once in reach, multiplayer arbitration assigns the converted monster to exactly one nearby living player.
            clock.At += .25;
            enemy.Walk.Teleport(placed.At.X + 4, placed.At.Y, clock.At);
            var peer = new Player(player.Layout, 1, 2)
            {
                Instance = player.Instance,
                PlayerLife = 10000,
                AiEnabled = true,
                Connection = { PlayerSent = true },
            };
            peer.Walk.Teleport(placed.At.X, placed.At.Y, clock.At);
            player.Instance.AddPlayer(peer);
            var plan = Game.PlanMonsterAi(player, cadence, clock.At);
            var peerPlan = Game.PlanMonsterAi(peer, new MonsterAiCadence(), clock.At);
            Assert.Equal(1, plan.MinionSwings.Count + peerPlan.MinionSwings.Count);
            var swing = plan.MinionSwings.Count == 1 ? Assert.Single(plan.MinionSwings) : Assert.Single(peerPlan.MinionSwings);
            Assert.Same(converted, swing.Monster);
            Assert.Equal(enemy.ObjectId, swing.TargetId);
            Assert.DoesNotContain(plan.Swings, order => ReferenceEquals(order.Monster, converted));
            Assert.NotNull(Game.MinionHitTargetLocked(player, converted.ObjectId, converted, enemy.ObjectId, enemy,
                clock.At, new Random(42), checkReach: true, accuracy: 100_000));
            Assert.True(enemy.Life < enemy.MaxLife);

            int duration = supported.Stats[Stat.BaseSkillEffectDuration];
            double expiry = convertedAt + duration / 1000d;
            clock.At = expiry - .05;
            Game.AreaAilmentChangesLocked(player, clock.At);
            Assert.False(converted.Hostile);
            clock.At = expiry + .01;
            var expiryPackets = Game.AreaAilmentChangesLocked(player, clock.At);
            byte[] setTeamHostile = InstanceProtocol.SetTeam(converted.ObjectId, 1);
            Assert.Equal(new byte[] { 0x01, 0xbe, 0x00, 0x00, 0x02, 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }, setTeamHostile);
            Assert.Contains(expiryPackets, p => p.Packet.SequenceEqual(setTeamHostile));
            player.Instance.RemovePlayer(peer.Id);
            Game.PlanMonsterAi(player, cadence, clock.At);
            Assert.True(converted.Hostile);
            Assert.Same(player, converted.Target);
            Assert.False(player.Area.Buffs.HasRow(converted.ObjectId, 165));
            Assert.False(player.Minions.Attacks.ContainsKey(converted.ObjectId));
            Assert.False(peer.Minions.Attacks.ContainsKey(converted.ObjectId));
        }
        finally
        {
            Program.Outgoing = outgoing;
            Game.AreaUpdateContext.Value = prior;
        }
    }

    [Fact]
    public async Task Conversion_trap_leaves_a_unique_hostile_and_does_not_trigger_at_expiry()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Conversion Trap", 1)));
        var clock = new AreaClock();
        var prior = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var placed = await Throw(player, skill, preflight: true);
            var unique = SpawnEnemy(player, 704, placed.At, MonsterRarity.Unique);
            var nearby = SpawnEnemy(player, 705, (placed.At.X + 1, placed.At.Y));
            clock.At = placed.Armed + .01;
            await Tick(player, clock.At);
            Assert.Empty(player.Placed.All);
            Assert.True(unique.Hostile);
            Assert.Equal(unique.MaxLife, unique.Life);
            Assert.True(nearby.Hostile);
            Assert.False(player.Area.Buffs.HasRow(unique.ObjectId, 165));

            player.Area.Monsters.Clear();
            clock.At += 10;
            placed = await Throw(player, skill, preflight: false);
            clock.At = placed.Until - .05;
            await Tick(player, clock.At);
            Assert.Same(placed, Assert.Single(player.Placed.All.Values));
            var late = SpawnEnemy(player, 706, placed.At);
            clock.At = placed.Until;
            await Tick(player, clock.At);
            Assert.Empty(player.Placed.All);
            Assert.True(late.Hostile);
            Assert.Equal(late.MaxLife, late.Life);

            // Dying before conversion expires clears TemporaryAllyUntil and buff 165 without emitting SetTeam(1) for a corpse.
            player.Area.Monsters.Clear();
            clock.At += 10;
            placed = await Throw(player, skill, preflight: false);
            var dying = SpawnEnemy(player, 707, placed.At);
            clock.At = placed.Armed + .01;
            await Tick(player, clock.At);
            Assert.True(dying.TemporarilyAllied);
            Assert.True(player.Area.Buffs.HasRow(dying.ObjectId, 165));
            dying.Dead = true;
            dying.Life = 0;
            var deathPackets = Game.AreaAilmentChangesLocked(player, clock.At + .05);
            Assert.Equal(0d, dying.TemporaryAllyUntil);
            Assert.False(player.Area.Buffs.HasRow(dying.ObjectId, 165));
            Assert.DoesNotContain(deathPackets, p => p.Packet.SequenceEqual(InstanceProtocol.SetTeam(dying.ObjectId, 1)));
        }
        finally { Game.AreaUpdateContext.Value = prior; }
    }

    static async Task<PlacedSkills.Placed> Throw(Player player, ushort skill, bool preflight)
    {
        var supported = Game.SupportedSkill(player, skill);
        CooldownUseTicket? ticket = null;
        if (preflight)
        {
            Assert.True(Game.TryBeginSkillCooldownUse(player, skill, supported.Effect, Game.CooldownForSkill(player, skill),
                instant: false, () => { }, out ticket, actionToken: player.ActionCount));
            Assert.NotNull(ticket);
        }
        var from = player.Walk.Position(Game.AreaTime(player));
        Assert.True(await Game.CastGemSkill(player, skill, from, (from.X + 12, from.Y),
            actionToken: preflight ? player.ActionCount : null, cooldownUse: ticket));
        return Assert.Single(player.Placed.All.Values);
    }

    static SpawnedMonster SpawnEnemy(Player player, uint id, (int X, int Y) at, MonsterRarity rarity = MonsterRarity.Normal,
        MonsterCatalog.Variety? variety = null)
    {
        var monster = new SpawnedMonster("conversion target", new(at.X, at.Y, 0), 60, 4, 1000)
        {
            ObjectId = id,
            Rarity = rarity,
            Variety = variety,
            Attack = new(0x2909, .5, 10, DamagePacket.Of(DamageType.Physical, new(10, 10))),
        };
        player.Area.Monsters.Add(id, monster);
        return monster;
    }

    static Task Tick(Player player, double now) =>
        (Task)typeof(Game).GetMethod("PlacedSkillsTick", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [player, now])!;

    sealed class AreaClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
