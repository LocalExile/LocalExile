using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

/// <summary>
/// S3 <c>EmptyAction*</c> combo resolution, <c>.ao</c> duplicate-event deduplication, <c>0x2bb</c> child-skill
/// registration, <c>0x1ce</c> triggered-action execution on <c>AreaTime</c>, and clean interrupt/abort handling.
/// </summary>
public sealed class MonsterCombosTests
{
    [Fact]
    public void Tier2_combo_resolution_dedupe_wire_registration_and_guard_invariants()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var catalog = Game.SkillsFor(player);

        // 1. Wire check: S->C 0x1ce origin overload sets flags |= 0x2000 | 0x80 | 0x20 and encodes originX/Y before targetX/Y.
        byte[] pkt = InstanceProtocol.InstantAction(0x3010, 0x4400, 120, 80, origin: (105, 95),
            targetObjectId: 0x1000, sequence: 0x0042, triggered: true);
        Assert.Equal(InstanceProtocol.InstantActionId, BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(0, 2)));
        Assert.Equal(0x3010u, BinaryPrimitives.ReadUInt32BigEndian(pkt.AsSpan(2, 4)));
        Assert.Equal((ushort)0x4400, BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(12, 2)));
        Assert.Equal((ushort)0x0042, BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(14, 2)));
        ushort wireFlags = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(16, 2));
        Assert.Equal((ushort)(0x2000 | 0x0080 | 0x0020), wireFlags);
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32BigEndian(pkt.AsSpan(18, 4)));
        // CompactInt: [-64, 63] is 1 byte, [64, 8191] is 2 bytes (0x80|hi, lo). All 4 coords (105,95,120,80) are 2 bytes each -> 22 + 8 = 30 B.
        Assert.Equal(30, pkt.Length);
        Assert.Equal([(byte)0x80, (byte)105, (byte)0x80, (byte)95, (byte)0x80, (byte)120, (byte)0x80, (byte)80], pkt[22..]);

        // 2. Daresso (Rule 2c ordinal series + shadow "contact" suppression + mandatory 0x2bb child skill registration).
        var daresso = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Daresso/DaressoBoss"));
        var daressoCombos = MonsterCombos.For(catalog, daresso);
        Assert.True(daressoCombos.HasAny);
        int reaveParent = Assert.Single(daressoCombos.Parents.Keys);
        Assert.Equal("EmptyActionAttackDaressoReave", catalog.EffectId(reaveParent));
        var reaveEvents = daressoCombos.Parents[reaveParent].Events;
        Assert.Equal(4, reaveEvents.Count);
        Assert.All(reaveEvents, e => Assert.Equal("reave", e.EventName));
        Assert.Equal(
            ["DaressoReaveGeometryAttack1", "DaressoReaveGeometryAttack2", "DaressoReaveGeometryAttack3", "DaressoReaveGeometryAttack4"],
            reaveEvents.Select(e => catalog.EffectId(e.ChildEffect)));
        Assert.All(reaveEvents, e => Assert.Equal(MonsterComboDispatch.TriggeredAction, e.Dispatch));

        var daressoKit = BossKits.Kit(player, daresso, 40, Game.MavenNucleus.CombatEffect(catalog, daresso), unique: true);
        var reaveEntry = Assert.Single(daressoKit, k => k.Effect == reaveParent);
        Assert.True(reaveEntry.Attack.InPlace);
        Assert.Equal(4, reaveEntry.Attack.ComboSteps!.Count);
        // Bound combo children stay out of top-level AI kit, while staying registered on Actor 0x2bb at 0x4400+.
        foreach (int childEffect in daressoCombos.BoundChildren)
            Assert.DoesNotContain(daressoKit, k => k.Effect == childEffect);
        var daressoKitSkills = BossKits.SkillsOf(player, daresso, 40, daressoKit);
        Assert.Equal(daressoKit.Count, daressoKitSkills.Count);
        var daressoActorSkills = MonsterCombos.WithChildSkills(player, daresso, 40, daressoKit, daressoKitSkills);
        foreach (var step in reaveEntry.Attack.ComboSteps)
        {
            Assert.True(step.ChildAttack.SkillId >= MonsterCombos.ChildSkillIdBase);
            Assert.Contains(daressoActorSkills, s => s.Id == step.ChildAttack.SkillId);
        }

        // 3. Legion Generals: Vox ComboDash (GeometryAttack + StartFace/EndFace/Move controls) & Aukuna (Projectile + GeometrySpell).
        var vox = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/LegionLeague/LegionTemplarGeneral"));
        var voxCombos = MonsterCombos.For(catalog, vox);
        int comboDash = catalog.EffectRow("EmptyActionLegionTemplarGeneralComboDash");
        Assert.True(voxCombos.Parents.TryGetValue(comboDash, out var dashCombo));
        Assert.Equal(["left", "right", "dash"], dashCombo.Events.Select(e => e.EventName));
        Assert.Contains(dashCombo.Controls, c => c.Kind == "StartFaceEventType");
        Assert.Contains(dashCombo.Controls, c => c.Kind == "MoveEventType" && c.TerrainClamped && c.Fraction > 0 && c.Fraction <= 1.0);
        // Non-projectile area/melee/travel ActionTypes must dispatch as TriggeredAction (0x1ce), not Projectile.
        Assert.Equal(MonsterComboDispatch.TriggeredAction, MonsterCombos.DispatchFor("MonsterCleave"));
        Assert.Equal(MonsterComboDispatch.TriggeredAction, MonsterCombos.DispatchFor("MonsterShockNova"));
        Assert.Equal(MonsterComboDispatch.TriggeredAction, MonsterCombos.DispatchFor("MonsterFlameblast"));

        var aukuna = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/LegionLeague/LegionMarakethGeneral"));
        var aukunaCombos = MonsterCombos.For(catalog, aukuna);
        int multiShoot = catalog.EffectRow("EmptyActionLegionMultipleShoot");
        int screech = catalog.EffectRow("EmptyActionMarakethGeneralScreech");
        Assert.True(aukunaCombos.Parents.TryGetValue(multiShoot, out var msCombo));
        Assert.All(msCombo.Events, e => Assert.Equal(MonsterComboDispatch.Projectile, e.Dispatch));
        Assert.True(aukunaCombos.Parents.TryGetValue(screech, out var scCombo));
        Assert.All(scCombo.Events, e => Assert.Equal(MonsterComboDispatch.TriggeredAction, e.Dispatch));

        // 4. Zero false positives & Animation == NULL safety: utility parents skipped, Melee/Fireball preserved, Guard G1 blocks Tier-A bosses.
        var solarisCaster = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/AxisCasterSolarisChampion"));
        Assert.False(MonsterCombos.For(catalog, solarisCaster).HasAny);
        int fireball3 = catalog.EffectRow("MonsterFireball3Parallel");
        Assert.Null(SkillCatalog.EffectAnimationName(fireball3));
        Assert.False(MonsterCombos.IsComboChild(catalog, solarisCaster, fireball3));

        var shaper = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(AtlasBosses.Shaper));
        Assert.True(Game.IsBossControllerVariety(shaper));
        Assert.False(MonsterCombos.For(catalog, shaper).HasAny);

        // 5. Exact (name, ms, rounded_pos) .ao duplicate dedupe: removes identical duplicates while preserving distinct-pos same-ms shots.
        var voxThrowPool = catalog.MonsterContactPool(vox.Id, "Throw");
        Assert.NotNull(voxThrowPool);
        var voxRaw = voxThrowPool![0].Events;
        var voxDedup = MonsterCombos.DedupeEvents(voxRaw);
        Assert.Equal(62, voxRaw.Count(e => e.Name == "trigger_proj"));
        Assert.Equal(31, voxDedup.Count(e => e.Name == "trigger_proj"));

        var hyrri = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/LegionLeague/LegionKaruiGeneral"));
        var hyrriPool = catalog.MonsterContactPool(hyrri.Id, "HeavyStrike");
        Assert.NotNull(hyrriPool);
        var hyrriRaw = hyrriPool![0].Events;
        var hyrriDedup = MonsterCombos.DedupeEvents(hyrriRaw);
        Assert.Equal(30, hyrriRaw.Count(e => e.Name == "trigger_proj"));
        Assert.Equal(10, hyrriDedup.Count(e => e.Name == "trigger_proj"));

        var synthetic = MonsterCombos.DedupeEvents([
            new("trigger_proj", 500, new ContactPoint(10f, -50f, 0f)),
            new("trigger_proj", 500, new ContactPoint(10f, -50f, 0f)), // exact duplicate -> dropped
            new("trigger_proj", 500, new ContactPoint(-10f, -50f, 0f)), // distinct pos at same ms -> preserved!
        ]);
        Assert.Equal(2, synthetic.Count);
    }

    [Fact]
    public async Task Combo_runner_fires_0x1ce_and_projectiles_on_AreaTime_and_aborts_on_interrupt()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
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

        var daresso = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Daresso/DaressoBoss"));
        var catalog = Game.SkillsFor(player);
        var kit = BossKits.Kit(player, daresso, 40, Game.MavenNucleus.CombatEffect(catalog, daresso), unique: true);
        int reaveEffect = catalog.EffectRow("EmptyActionAttackDaressoReave");
        var reaveEntry = Assert.Single(kit, k => k.Effect == reaveEffect);

        uint id = await Game.SpawnMonster(player, daresso, 40, MonsterRarity.Unique, 100, 100, unrisen: false);
        var monster = player.Area.Monsters[id];
        // Verify SpawnMonster registered all 4 TriggeredAction combo children (0x4400..0x4403) onto MonsterObject.Skills (0x2bb).
        var actorSkills = player.Instance.World.MonsterOrigins[id].Object.Skills;
        Assert.NotNull(actorSkills);
        foreach (var step in reaveEntry.Attack.ComboSteps!)
            Assert.Contains(actorSkills!, s => s.Id == step.ChildAttack.SkillId);

        monster.ActionGeneration = 7;
        monster.Attack = reaveEntry.Attack;

        // 1. Execute combo with startedAt in the past so all AreaTime clip timestamps resolve immediately (zero sleeps).
        double now = Game.AreaTime(player);
        await MonsterCombos.RunComboAsync(player, id, monster, reaveEntry.Attack, action: 7, seq: 0x0015,
            startedAt: now - 5.0, seconds: reaveEntry.Attack.Seconds, initialOrigin: (100, 100), initialAim: (108, 100));

        // 2. Abort on cancel (ActionGeneration bumped), death, and stun (FlinchUntil > now): none may emit 0x1ce.
        monster.ActionGeneration = 8;
        await MonsterCombos.RunComboAsync(player, id, monster, reaveEntry.Attack, action: 7, seq: 0x0016,
            startedAt: now - 5.0, seconds: reaveEntry.Attack.Seconds, initialOrigin: (100, 100), initialAim: (108, 100));

        monster.Dead = true;
        monster.Life = 0;
        await MonsterCombos.RunComboAsync(player, id, monster, reaveEntry.Attack, action: 8, seq: 0x0017,
            startedAt: now - 5.0, seconds: reaveEntry.Attack.Seconds, initialOrigin: (100, 100), initialAim: (108, 100));

        monster.Dead = false;
        monster.Life = 5000;
        monster.FlinchUntil = Game.AreaTime(player) + 10.0;
        await MonsterCombos.RunComboAsync(player, id, monster, reaveEntry.Attack, action: 8, seq: 0x0018,
            startedAt: now - 5.0, seconds: reaveEntry.Attack.Seconds, initialOrigin: (100, 100), initialAim: (108, 100));

        // Shut down the write half cleanly so CopyToAsync drains to exact EOF with zero sleeps.
        writer.Client.Shutdown(SocketShutdown.Send);
        await drainTask;

        // Decrypt the Salsa20 stream with the matching key and verify exactly the 4 Reave GeometryAttack child skill ids (0x4400..0x4403) — 0 from aborted steps!
        byte[] wire = captured.ToArray();
        new ConnectionCiphers(new byte[64], false).Send.Process(wire);
        var seenTriggeredSkills = FindInstantActionSkills(wire, id);
        Assert.Equal(4, seenTriggeredSkills.Count);
        Assert.Equal(reaveEntry.Attack.ComboSteps!.Select(s => s.ChildAttack.SkillId), seenTriggeredSkills);
    }

    static List<ushort> FindInstantActionSkills(byte[] wire, uint objectId)
    {
        // Scans unencrypted outbound frames for S->C 0x1ce (InstantAction) targeting objectId with triggered bit 0x80 set.
        var result = new List<ushort>();
        for (int i = 0; i + 18 <= wire.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i, 2)) != InstanceProtocol.InstantActionId) continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(i + 2, 4)) != objectId) continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(i + 6, 4)) != 0) continue;
            if (BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i + 10, 2)) != 0) continue;
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i + 16, 2));
            if ((flags & 0x80) == 0) continue;
            result.Add(BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i + 12, 2)));
            i += 17;
        }
        return result;
    }
}
