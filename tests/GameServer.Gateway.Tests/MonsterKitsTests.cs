using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Monster kits with EmptyAction* combos: every skill main played stays playable, a built combo parent fires
/// its children by S→C 0x1ce on AreaTime and stops when its action does, and kit choice keeps MinRange and cooldown groups.</summary>
public sealed class MonsterKitsTests
{
    [Fact]
    public async Task Kits_keep_mains_skills_and_combos_fire_their_children()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var catalog = Game.SkillsFor(player);
        MonsterCatalog.Variety Variety(string id) => Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(id));

        // 1. Ordinary kits: the fire-throwing Cannibal keeps its throw (it carries triggered_skill_use_animation_event),
        //    and an unmodded spawn gets the rounded hit main gave it, reusing the cached wire skills.
        var cannibal = Variety("Metadata/Monsters/Cannibal/CannibalMaleThrowFire");
        var template = MonsterKits.TemplateFor(player, cannibal, 3);
        Assert.Contains(template.BaseKit, k => catalog.EffectId(k.Effect) == "ScavengerThrowFire");
        var unmodded = MonsterKits.ForOrdinary(player, cannibal, 3, -1, MonsterModEffects.None, applyAuraStats: false).Kit;
        Assert.Same(template.UnmoddedKit, unmodded);
        Assert.All(unmodded, k => Assert.Equal(Math.Round(k.Attack.Damage.MinTotal), k.Attack.Damage.MinTotal));
        Assert.Same(template.BaseSkills, MonsterKits.SkillsForOrdinary(player, cannibal, 3, unmodded));

        // 2. A combo child leaves a kit only when a parent in it built from it: the Harvest bear's Trample is bound to a
        //    parent that does not build, so it stays its own skill as on main.
        var bear = Variety("Metadata/Monsters/LeagueHarvest/Red/HarvestBearBoss");
        int trample = catalog.EffectRow("HarvestBearBossTrample");
        Assert.True(MonsterCombos.IsComboChild(catalog, bear, trample));
        Assert.Contains(BossKits.Kit(player, bear, 60, unique: true), k => k.Effect == trample);

        // 3. Daresso's Reave: one parent, its four ordinal GeometryAttack children out of the kit but on the actor (0x4400+).
        var daresso = Variety("Metadata/Monsters/Daresso/DaressoBoss");
        int reave = catalog.EffectRow("EmptyActionAttackDaressoReave");
        var kit = BossKits.Kit(player, daresso, 40, Game.MavenNucleus.CombatEffect(catalog, daresso), unique: true);
        var reaveEntry = Assert.Single(kit, k => k.Effect == reave);
        var steps = reaveEntry.Attack.ComboSteps!;
        Assert.Equal(["DaressoReaveGeometryAttack1", "DaressoReaveGeometryAttack2", "DaressoReaveGeometryAttack3", "DaressoReaveGeometryAttack4"],
            steps.Select(s => catalog.EffectId(s.ChildEffect)));
        Assert.All(steps, s => Assert.DoesNotContain(kit, k => k.Effect == s.ChildEffect));
        var actorSkills = MonsterCombos.WithChildSkills(player, daresso, 40, kit, BossKits.SkillsOf(player, daresso, 40, kit));
        Assert.All(steps, s => Assert.Contains(actorSkills, a => a.Id == s.ChildAttack.SkillId && a.Id >= MonsterCombos.ChildSkillIdBase));
        // A projectile step sends no 0x1ce, so the server draws it (the Harvest Chieftain's mortar keeps no variation).
        var chieftain = Variety("Metadata/Monsters/LeagueHarvest/Red/HarvestChieftainT2_");
        int mortar = catalog.EffectRow("HarvestChieftainVolatileMortar");
        var mortarSteps = MonsterCombos.For(catalog, chieftain).Parents.Keys
            .Select(p => MonsterCombos.BuildParentAttack(player, chieftain, 60, p, 0x4000, new()))
            .SelectMany(a => a?.ComboSteps ?? []).Where(s => s.ChildEffect == mortar).ToList();
        Assert.NotEmpty(mortarSteps);
        Assert.All(mortarSteps, s => Assert.Equal(0, s.ChildAttack.Projectile!.Variation));

        // 4. S→C 0x1ce with a custom origin: flags 0x2000 | 0x80 | 0x20, origin before target.
        byte[] pkt = InstanceProtocol.InstantAction(0x3010, 0x4400, 120, 80, origin: (105, 95), targetObjectId: 0x1000,
            sequence: 0x0042, triggered: true);
        Assert.Equal((ushort)(0x2000 | 0x0080 | 0x0020), BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(16, 2)));
        Assert.Equal([(byte)0x80, (byte)105, (byte)0x80, (byte)95, (byte)0x80, (byte)120, (byte)0x80, (byte)80], pkt[22..]);

        // 5. The runner on a live connection: one 0x1ce per child, none once the action is cancelled, the monster dies
        //    or it flinches (startedAt in the past, so no waits).
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();
        using var writer = new TcpClient();
        writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await acceptTask;
        var captured = new MemoryStream();
        var drainTask = reader.GetStream().CopyToAsync(captured);
        player.Connection.Session = new("combo-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false));
        player.Walk.Teleport(108, 100, Game.AreaTime(player));
        uint id = await Game.SpawnMonster(player, daresso, 40, MonsterRarity.Unique, 100, 100, unrisen: false);
        var monster = player.Area.Monsters[id];
        Assert.All(steps, s => Assert.Contains(player.Instance.World.MonsterOrigins[id].Object.Skills!, a => a.Id == s.ChildAttack.SkillId));
        monster.ActionGeneration = 7;
        monster.Attack = reaveEntry.Attack;
        double now = Game.AreaTime(player);
        Task Run(int action, ushort seq) => MonsterCombos.RunComboAsync(player, id, monster, reaveEntry.Attack, action, seq,
            startedAt: now - 5.0, seconds: reaveEntry.Attack.Seconds, initialOrigin: (100, 100), initialAim: (108, 100));
        await Run(7, 0x15);
        monster.ActionGeneration = 8;
        await Run(7, 0x16);
        (monster.Dead, monster.Life) = (true, 0);
        await Run(8, 0x17);
        (monster.Dead, monster.Life, monster.FlinchUntil) = (false, 5000, Game.AreaTime(player) + 10.0);
        await Run(8, 0x18);
        writer.Client.Shutdown(SocketShutdown.Send);
        await drainTask;
        byte[] wire = captured.ToArray();
        new ConnectionCiphers(new byte[64], false).Send.Process(wire);
        Assert.Equal(steps.Select(s => s.ChildAttack.SkillId), TriggeredActions(wire, id));

        // 6. Kit choice: MinRange gates a skill up close; a cooldown group locks its members, and a shorter member used
        //    later never cuts the longer lock short.
        var melee = new MonsterAttack(0x4000, 1.0, 12, DamagePacket.Of(DamageType.Physical, 5, 10));
        var far = new MonsterAttack(0x4001, 1.0, 45, DamagePacket.Of(DamageType.Fire, 15, 25)) { Ranged = true, Spell = true };
        var groupA = new MonsterAttack(0x4002, 1.0, 40, DamagePacket.Of(DamageType.Cold, 20, 30)) { Ranged = true, Spell = true };
        var groupB = groupA with { SkillId = 0x4003 };
        var groupShort = groupA with { SkillId = 0x4004 };
        var chooser = new SpawnedMonster("test-kit", new PlayerWalk(100, 100, 15, (_, _) => true), aggroRange: 50, stopRange: 6, maxLife: 100)
        {
            Kit = [new(1001, melee, 0), new(1002, far, 5.0) { MinRange = 20 }, new(1003, groupA, 6.0) { CooldownGroup = 7 },
                new(1004, groupB, 6.0) { CooldownGroup = 7 }, new(1005, groupShort, 1.0) { CooldownGroup = 7 }],
            Attack = melee,
        };
        player.Area.Monsters.Add(0x5001, chooser);
        Assert.Equal(groupA.SkillId, BossKits.Choose(player, 0x5001, chooser, now: 10.0, distance: 10.0, cachedAreaLists: false)?.SkillId);
        BossKits.Consume(chooser, groupA, now: 10.0);
        BossKits.Consume(chooser, groupShort, now: 11.0);
        Assert.False(BossKits.Ready(chooser, groupB, now: 13.0));
        Assert.True(BossKits.Ready(chooser, groupB, now: 16.0));
        Assert.Equal(melee.SkillId, BossKits.Choose(player, 0x5001, chooser, now: 13.0, distance: 10.0, cachedAreaLists: false)?.SkillId);
        BossKits.Consume(chooser, melee, now: 13.0);
        Assert.Equal(far.SkillId, BossKits.Choose(player, 0x5001, chooser, now: 13.5, distance: 30.0, cachedAreaLists: false)?.SkillId);

        // 7. A module's effect filter reaches both kit builders and leaves no trace once disposed.
        var caster = Variety("Metadata/Monsters/Axis/AxisCasterSolarisChampion");
        int fireball = catalog.EffectRow("MonsterFireball3Parallel");
        using (MonsterKitHooks.AddEffectFilter((v, e, _) => !(v.Row == caster.Row && e == fireball)))
            Assert.DoesNotContain(MonsterKits.TemplateFor(player, caster, 60).BaseKit, k => k.Effect == fireball);
        Assert.False(MonsterKitHooks.HasEffectFilters);
        Assert.Contains(MonsterKits.TemplateFor(player, caster, 60).BaseKit, k => k.Effect == fireball);
    }

    /// <summary>The skill ids of the triggered S→C 0x1ce actions (flag 0x80) for <paramref name="objectId"/> in a decrypted stream.</summary>
    static List<ushort> TriggeredActions(byte[] wire, uint objectId)
    {
        var result = new List<ushort>();
        for (int i = 0; i + 18 <= wire.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i, 2)) != InstanceProtocol.InstantActionId
                || BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(i + 2, 4)) != objectId
                || BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(i + 6, 4)) != 0
                || BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i + 10, 2)) != 0
                || (BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i + 16, 2)) & 0x80) == 0) continue;
            result.Add(BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i + 12, 2)));
            i += 17;
        }
        return result;
    }
}
