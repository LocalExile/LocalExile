using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Shrines;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class ShrineTests
{
    [Fact]
    public async Task Shrine_data_and_shared_lifecycle_keep_effects_owned_and_reversible()
    {
        Assert.Empty(ShrineCatalog.Skipped);
        Assert.Equal(68, ShrineCatalog.All.Count);

        var acceleration = ShrineCatalog.Find("Acceleration")!;
        Assert.Equal(45, acceleration.Duration);
        Assert.Equal(241, acceleration.BuffRow);
        Assert.Equal("shrine_acceleration", acceleration.BuffId);
        // BuffDefinitions 241's GrantedStats are action_speed_-%, base_projectile_speed_+% and has_shrine_effect;
        // BuffTemplates 383 gives -50, 80 and 1. The client key is the Stats row + 1.
        Assert.Equal([(348u, -50), (178u, 80), (5819u, 1)], acceleration.Stats);

        // The Divine shrine lasts 20 s where the other standard rows last 45.
        Assert.Equal(20, ShrineCatalog.Find("GodMode")!.Duration);
        Assert.Equal(45, ShrineCatalog.Find("Brutal")!.Duration);

        // The monster buff is its own template and values: Acceleration's monsters take -60 action speed and 100
        // projectile speed where the player takes -50 and 80.
        Assert.NotNull(acceleration.Monster);
        Assert.Equal((ushort)384, acceleration.Monster!.TemplateRow);
        Assert.Equal("acceleration_monster", acceleration.Monster.Template);
        Assert.Equal([(348u, -60), (178u, 100), (5819u, 1)], acceleration.Monster.Stats);
        Assert.Empty(acceleration.GuardPacks);

        // Guard packs are data: only the boss and influence shrines have them (Crusader: four packs).
        Assert.Equal(4, ShrineCatalog.Find("Crusader")!.GuardPacks.Count);
        Assert.Equal("atlasexileinfluence-crusader-shrine1", ShrineCatalog.Find("Crusader")!.GuardPacks[0].Id);

        // A map rolls only the fifteen basic shrines: the apparition rows wait for their scarab, and the lesser and
        // greater rows are skills, not map shrine types.
        Assert.Equal(15, ShrineCatalog.Standard.Count);
        Assert.Equal(["Acceleration", "Lightning", "Cold", "Criticals", "Brutal", "Regen", "Massive", "Defence",
            "Resistance", "GodMode", "Multi", "Skeletons", "Smoke", "Resonating", "Gloom"],
            ShrineCatalog.Standard.Select(s => s.Id));
        Assert.Equal(8, ShrineCatalog.ApparitionIds.Length);
        Assert.DoesNotContain(ShrineCatalog.Standard, s => ShrineCatalog.ApparitionIds.Contains(s.Id));
        Assert.DoesNotContain(ShrineCatalog.Standard, s => s.Lesser || s.GuardPacks.Count > 0 || s.Duration == 0);
        Assert.Equal(ShrineCatalog.Skipped, []);
        Guards_come_from_the_shrines_own_pack_rows_or_the_areas_and_keep_clear_of_entrances();
        await Lifecycle();
        NovaSpells();
        await LesserLifecycle();
    }

    [Fact]
    public void Shrines_plan_from_walkable_room_lines_and_write_the_ten_client_components()
    {
        // data/object-types.json lists Positioned, BaseEvents, Stats, Animated, Buffs, Life, ObjectMagicProperties,
        // Shrine, MinimapIcon and InteractionAction as the shrine's deserialized components (read = 10).
        Assert.Equal(10, ShrineObject.Components.Length);
        Assert.Equal(["Positioned", "BaseEvents", "Stats", "Animated", "Buffs", "Life", "ObjectMagicProperties",
            "Shrine", "MinimapIcon", "InteractionAction"], ShrineObject.Components);

        // Two header bytes, 18 Positioned, 10 Stats, 1 Animated, 2 Buffs, 55 Life, 4 magic properties, 2 Shrine,
        // 1 MinimapIcon, 1 InteractionAction, 1 trailing task count.
        var blob = new ShrineObject(120, 45).Blob();
        Assert.Equal(98, blob.Length);
        Assert.Equal(0, blob[0]);
        Assert.Equal(0, blob[^1]);
        // The Shrine component starts with the Shrines row the client looks up for the icon.
        Assert.Equal(0, blob[92]);
        Assert.Equal(11, new ShrineObject(120, 45, Row: 11).Blob()[92]);
        Assert.Equal([.. ShrineObject.Components, "Transitionable", "DiesAfterTime"], ShrineObject.LesserComponents);
        var lesser = new ShrineObject(120, 45, Row: 24, Lesser: true).Blob();
        Assert.Equal(99, lesser.Length);
        Assert.Equal(24, lesser[92]);
        Assert.Equal(1, lesser[^2]); // Untransitioned: native states are one-based; state 0 crashes post-create.

        // A shrine room line is a shrine only on walkable ground, and the same layout and seed roll the same types.
        bool Walkable(int x, int y) => x == 5 && y == 5;
        const string Statue = "Metadata/Shrines/generic_shrine.ao";
        PlacedTileObject Line(string type, int x, int y) => new(type, Statue, x, y, 0.5f, 0f);
        var lines = new[] { Line(ShrinePlacement.Type, 5, 5), Line(ShrinePlacement.Type, 5, 5), Line(ShrinePlacement.Type, 0, 0) };
        var plan = ShrinePlacement.Plan(lines, Walkable, seed: 7);
        Assert.Single(plan);
        Assert.Equal((5, 5), (plan[0].X, plan[0].Y));
        Assert.Equal(0.5f, plan[0].Rotation);
        // The statue is the room line's model: the shrine type has no animated_object of its own.
        Assert.Equal(Statue, plan[0].Model);
        var many = Enumerable.Range(1, 30).Select(i => Line(ShrinePlacement.Type, i, 5)).ToArray();
        Assert.Equal(30, ShrinePlacement.Plan(many, (_, _) => true, 7).Count);
        Each_shrine_gets_its_own_guard_pack_around_its_own_cell();
        Assert.Contains(plan[0].Shrine, ShrineCatalog.Standard);
        Assert.Equal(plan, ShrinePlacement.Plan(lines, Walkable, seed: 7));
        Assert.Empty(ShrinePlacement.Plan(lines, Walkable, seed: 7, limit: 0));
        // A room line with no model of its own still shows the shrine's default statue.
        var bare = ShrinePlacement.Plan([new(ShrinePlacement.Type, "", 5, 5, 0f, 0f)], Walkable, seed: 7);
        Assert.Equal(ShrinePlacement.DefaultModel, bare[0].Model);
    }

    static void Guards_come_from_the_shrines_own_pack_rows_or_the_areas_and_keep_clear_of_entrances()
    {
        // A basic shrine names no pack: it is guarded by one of the area's packs, one pack, at that pack's own count,
        // normal rarity (no Atlas passive reaches a campaign area).
        var basic = ShrineGuards.Resolve(ShrineCatalog.Find("Acceleration")!, mapArea: false, _ => null).Rule;
        Assert.True(basic.Required);
        Assert.True(basic.AreaPool);
        Assert.Empty(basic.Packs);
        Assert.Null(basic.Fixed);
        Assert.Equal(MonsterRarity.Normal, basic.Rarity);

        // A shrine the data gives packs takes its count from them: Brine King's five rows roll 2–9, the Conquerors' 2–7.
        var brine = ShrineGuards.Resolve(ShrineCatalog.Find("BrineKing")!, mapArea: true, _ => null).Rule;
        Assert.False(brine.AreaPool);
        Assert.Equal(5, brine.Packs.Count);
        Assert.Equal((4, 9), (brine.Min, brine.Max));
        Assert.Equal((3, 7), (ShrineGuards.Resolve(ShrineCatalog.Find("Crusader")!, true, _ => null).Rule.Min,
            ShrineGuards.Resolve(ShrineCatalog.Find("Crusader")!, true, _ => null).Rule.Max));

        // A shrine too close to the entrance or a transition is not guarded (the 1.0.0d note): within 25 cells.
        Assert.True(ShrineGuards.TooNearEntrance((30, 30), (10, 30), [(200, 200)], 25));
        Assert.True(ShrineGuards.TooNearEntrance((30, 30), (200, 200), [(10, 30)], 25));
        Assert.False(ShrineGuards.TooNearEntrance((100, 30), (10, 30), [(200, 200)], 25));
    }

    static void Each_shrine_gets_its_own_guard_pack_around_its_own_cell()
    {
        // Two shrines in one area, each guarded by one pack of the area's own monsters, members within 9 cells of the
        // shrine and never stacked on each other: the packs stay their own.
        var pack = new PackCatalog.Pack(0, "Test-Pack", 3, 4, 0, 0, [], [(1, 1)], new Dictionary<int, int>());
        var area = new PackCatalog.AreaPacks("Test", 10, [(pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);
        var rule = ShrineGuards.Resolve(ShrineCatalog.Find("Acceleration")!, false, _ => null).Rule;
        bool Walkable(int x, int y) => x > 0 && y > 0 && x < 99 && y < 99;
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(area, 100, 100, Walkable, (50, 50), [], [], seed: 11, _ => true, notes,
            shrineGuards: [("Acceleration", rule, [], 20, 20), ("Brutal", rule, [], 80, 80)]);
        var guards = spawns.Where(s => s.Pack == "Test-Pack").ToList();
        foreach (var cell in new[] { (20, 20), (80, 80) })
        {
            var mine = spawns.Where(s => Math.Abs(s.X - cell.Item1) <= AreaPopulation.ShrineGuardRadius
                && Math.Abs(s.Y - cell.Item2) <= AreaPopulation.ShrineGuardRadius).ToList();
            Assert.InRange(mine.Count, 3, 4);
            Assert.All(mine, s => Assert.Equal("Test-Pack", s.Pack));
            Assert.All(mine, s => Assert.Equal(MonsterRarity.Normal, s.Rarity));
            // Around the shrine, never on it: no guard inside the shrine's object size (the statue and its base).
            double Away(int x, int y) => Math.Sqrt(Math.Pow(x - cell.Item1, 2) + Math.Pow(y - cell.Item2, 2));
            Assert.All(mine, s => Assert.True(Away(s.X, s.Y) >= ShrinePlacement.ObjectSize));
            // Evenly around it: two bearings apart where a pack of four is split, never two on the same side of the
            // statue (their cells' sum, x + y, differs: one west-north, one east-south).
            if (mine.Count >= 3)
                Assert.True(mine.Select(s => s.X - cell.Item1).Max() - mine.Select(s => s.X - cell.Item1).Min()
                    >= ShrinePlacement.ObjectSize);
        }
        // No member stands twice in one cell, and each shrine's pack is its own: the scattered packs keep their 28-cell
        // spacing from both shrine centres, so these members are the guards.
        Assert.Equal(guards.Count, guards.Select(s => (s.X, s.Y)).Distinct().Count());
        Assert.Equal(2, notes.Count(n => n.StartsWith("shrine ")));
    }
    sealed class ProbeEffect : IShrineEffect
    {
        public int Applied, Expired, Unloaded;
        public Task OnBuffApplied(ShrineContext ctx, ShrineInstance shrine) { Applied++; return Task.CompletedTask; }
        public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) { Expired++; return Task.CompletedTask; }
        public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine) { Unloaded++; return Task.CompletedTask; }
    }

    static void NovaSpells()
    {
        var skills = Program.Skills;
        var ice = ShrineNovaRules.Describe(skills, true, false);
        var shock = ShrineNovaRules.Describe(skills, false, false);
        var claimed = ShrineNovaRules.Describe(skills, false, true);
        Assert.Equal((101, 29, 15), (ice.Effect, ice.Radius, ice.RadiusPercent));
        Assert.Equal((1067, 52, 100, 5), (shock.Effect, shock.Radius, shock.RadiusPercent, shock.GroundCount));
        Assert.Equal((1068, 19, -25, 1), (claimed.Effect, claimed.Radius, claimed.RadiusPercent, claimed.GroundCount));
        Assert.Equal(6, shock.GroundRadius);
        Assert.Equal(4, shock.GroundSeconds);
        Assert.Equal(ice.CastSeconds, ShrineNovaRules.HitDelay(ice, 0));
        Assert.Equal(ice.CastSeconds + .1, ShrineNovaRules.HitDelay(ice, ice.Radius / 2.0), 6);
        Assert.Equal(ice.CastSeconds + .2, ShrineNovaRules.HitDelay(ice, ice.Radius), 6);
        Assert.Equal(shock.CastSeconds, ShrineNovaRules.HitDelay(shock, shock.Radius));
        Assert.Equal((ushort)0xc563, skills.ActionHash(shock.Effect));
        Assert.NotEqual(skills.ActionHash(skills.EffectRow("ShockNova")), skills.ActionHash(shock.Effect));
    }

    static async Task LesserLifecycle()
    {
        var trigger = new LesserShrineTrigger();
        trigger.Observe("gull-1", 10, 20);
        Assert.False(trigger.TryUse(29.999, 20));
        Assert.True(trigger.TryUse(30, 20));
        Assert.False(trigger.TryUse(30, 20));
        trigger.Rebase(35, 100);
        Assert.False(trigger.TryUse(114.999, 20));
        Assert.True(trigger.TryUse(115, 20));
        trigger.Observe("", 116, 0);
        Assert.False(trigger.TryUse(200, 20));
        trigger.Observe("gull-1", 200, 20);
        Assert.False(trigger.TryUse(219.999, 20));
        trigger.Observe("gull-2", 300, 20);
        trigger.Observe("gull-2", 310, 10); // Recovery changes retain the half-completed cooldown.
        Assert.False(trigger.TryUse(314.999, 10));
        Assert.True(trigger.TryUse(315, 10));
        trigger.Observe("gull-2", 320, double.PositiveInfinity);
        Assert.False(trigger.TryUse(400, double.PositiveInfinity));
        trigger.Observe("gull-2", 400, 10);
        Assert.True(trigger.TryUse(405, 10));

        var instance = new InstanceState();
        await using var wire = await ShrineWire.Open(instance);
        var conn = wire.Conn;
        using var stats = GameEvents.PlayerStatSources.Add("shrine-regression", ShrineManager.AddStats);
        try
        {
            var opening = new MapDevice.Opening(new(1, "test", "Test", 1), new MapMods(new Dictionary<string, int>
                { ["map_num_extra_apparition_shrines"] = 2 }));
            var request = new MapOpening(conn, [], false, opening, "");
            ShrineMapAdmission.Check(request);
            Assert.Null(request.Opening);
            Assert.Contains("not been consumed", request.Reason);
            var ordinary = new MapOpening(conn, [], false, opening with { Mods = MapMods.None }, "");
            ShrineMapAdmission.Check(ordinary);
            Assert.NotNull(ordinary.Opening);

            await ShrineManager.SpawnLesser(conn, (100, 100), 10, "AccelerationLesser");
            var kept = Assert.Single(instance.ContentState, p => p.Key.StartsWith("Shrines/lesser/"));
            uint first = uint.Parse(kept.Key.Split('/')[^1]);
            await ShrineManager.LeaveArea(conn);
            await ShrineManager.SpawnArea(conn); // Unclaimed object survives a presentation rebuild, with its original deadline.
            Assert.Equal(kept.Value, instance.ContentState[kept.Key]);
            Assert.True(await ShrineManager.Use(conn, new(first, 0, 1, 0), 0));
            Assert.DoesNotContain(instance.ContentState.Keys, k => k.StartsWith("Shrines/lesser/"));
            Assert.NotEmpty(ShrineManager.AddStats(conn, []));
            await ShrineManager.SpawnLesser(conn, (100, 100), 10, "AccelerationLesser");
            uint second = uint.Parse(Assert.Single(instance.ContentState, p => p.Key.StartsWith("Shrines/lesser/")).Key.Split('/')[^1]);
            Assert.NotEqual(first, second); // A new kill may make a fresh shrine on the same cell.
            Assert.True(await ShrineManager.Use(conn, new(second, 0, 2, 0), 0));
            Assert.DoesNotContain(instance.ContentState.Keys, k => k.StartsWith("Shrines/lesser/"));
            await ShrineManager.SpawnLesser(conn, (100, 100), 10, "AccelerationLesser");
            var expired = Assert.Single(instance.ContentState, p => p.Key.StartsWith("Shrines/lesser/"));
            uint expiredId = uint.Parse(expired.Key.Split('/')[^1]);
            await ShrineManager.LeaveArea(conn);
            instance.ContentState[expired.Key] = "{\"Type\":\"AccelerationLesser\",\"X\":100,\"Y\":100,\"Until\":0}";
            await ShrineManager.SpawnArea(conn);
            Assert.DoesNotContain(instance.ContentState.Keys, k => k.StartsWith("Shrines/lesser/"));
            Assert.False(await ShrineManager.Use(conn, new(expiredId, 0, 3, 0), 0));
        }
        finally { await ShrineManager.LeaveArea(conn); }
    }

    static async Task Lifecycle()
    {
        ShrineRegistry.RegisterDefaults();
        var effects = new List<ProbeEffect>();
        ShrineRegistry.Register(new ShrineDefinition { Id = "Acceleration", Duration = 45,
            AfterActivation = [() => { var e = new ProbeEffect(); effects.Add(e); return e; }] });
        var instance = new InstanceState();
        await using var owner = await ShrineWire.Open(instance);
        await using var peer = await ShrineWire.Open(instance);
        var conn = owner.Conn;
        using var stats = GameEvents.PlayerStatSources.Add("shrine-regression", ShrineManager.AddStats);
        try
        {
            await ShrineManager.SpawnArea(peer.Conn);
            Assert.Equal(1, await ShrineManager.SpawnAroundPlayer(conn, ["Acceleration"]));
            uint shrine = uint.Parse(instance.ContentState["Shrines/id/112/100"]);
            conn.Walk.Teleport(112, 100, 0);
            peer.Conn.Walk.Teleport(112, 100, 0);
            Assert.True(await ShrineManager.Use(conn, new(shrine, 0, 1, 0), 0));
            Assert.True(await ShrineManager.Use(peer.Conn, new(shrine, 0, 1, 0), 0));
            Assert.Equal(1, effects.Sum(e => e.Applied));
            Assert.Equal(1, ShrineManager.AddStats(conn, []).GetValueOrDefault(Program.Stats.Key("has_shrine_effect")!.Value));
            // A second shrine replaces the old effect, including its owned objects.
            await ShrineManager.SpawnAroundPlayer(conn, ["Acceleration"]);
            uint second = uint.Parse(instance.ContentState["Shrines/id/124/100"]);
            conn.Walk.Teleport(124, 100, 0);
            await ShrineManager.Use(conn, new(second, 0, 2, 0), 0);
            Assert.Equal(2, effects.Sum(e => e.Applied));
            Assert.Equal(1, effects.Sum(e => e.Expired));
            conn.PlayerLife = 0;
            await ShrineManager.Tick(conn);
            Assert.Equal(2, effects.Sum(e => e.Expired));
            Assert.Empty(ShrineManager.AddStats(conn, []));
            conn.PlayerLife = 50;
            await ShrineManager.LeaveArea(conn);
            await ShrineManager.LeaveArea(peer.Conn);
            // Re-entry uses the same instance's claim ledger, including after the module discards its views.
            conn.Walk.Teleport(100, 100, 0);
            await ShrineManager.SpawnAroundPlayer(conn, ["Acceleration"]);
            conn.Walk.Teleport(112, 100, 0);
            await ShrineManager.Use(conn, new(shrine, 0, 3, 0), 0);
            Assert.Equal(2, effects.Sum(e => e.Applied));

            ShrineRegistry.RegisterDefaults();
            var monster = new SpawnedMonster("aura target", new(112, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000);
            conn.Monsters[0x7070] = monster;
            var definition = ShrineCatalog.Find("Resistance")!;
            ShrineInstance Make() => new() { Definition = definition, At = (112, 100), Before = ShrineRegistry.Before(definition) };
            var first = Make(); var overlapping = Make();
            var ctx = new ShrineContext(conn, 0);
            foreach (var effect in first.Before) await effect.OnNearbyMonsterEnter(ctx, first, 0x7070);
            foreach (var effect in overlapping.Before) await effect.OnNearbyMonsterEnter(ctx, overlapping, 0x7070);
            Assert.Equal(50, monster.Effects["base_resist_all_elements_%"]);
            Assert.Equal(85, Game.MonsterHitResistance(monster, DamageType.Fire, default).Maximum);
            var encounterStats = new Dictionary<string, int> { ["damage_+%"] = 23 };
            monster.EncounterStats = encounterStats;
            foreach (var effect in first.Before) await effect.OnActivation(ctx, first);
            Assert.Equal(50, monster.Effects["base_resist_all_elements_%"]);
            foreach (var effect in overlapping.Before) await effect.OnMapUnload(ctx, overlapping);
            Assert.Equal(0, monster.Effects["base_resist_all_elements_%"]);
            Assert.Equal(23, monster.Effects["damage_+%"]);
            // The Acceleration aura adds 60% action speed; a 30% chill subtracts from that increase.
            encounterStats["action_speed_-%"] = -60;
            monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 10, 0, _ => 0, _ => 0, 1, 30);
            Assert.Equal(1.3, Game.MonsterActionFactor(monster, 0), 8);
            encounterStats["action_speed_cannot_be_reduced_below_base"] = 1;
            Assert.Equal(1.3, Game.MonsterActionFactor(monster, 0), 8);
            monster.ReceivedActionFactor = .5; monster.ReceivedActionUntil = 10;
            Assert.Equal(1, Game.MonsterActionFactor(monster, 0), 8);
            encounterStats.Remove("action_speed_-%");
            encounterStats.Remove("action_speed_cannot_be_reduced_below_base");
            monster.ReceivedActionUntil = 0;
            var divine = ShrineCatalog.Find("GodMode")!;
            var god = new ShrineInstance { Definition = divine, At = (112, 100), Before = ShrineRegistry.Before(divine) };
            foreach (var effect in god.Before) await effect.OnNearbyMonsterEnter(ctx, god, 0x7070);
            Assert.Null(Game.ApplyMonsterHitLocked(conn, monster, 100));
            Assert.Equal(0, Game.MonsterDotTaken(monster, DamageType.Fire));
            foreach (var effect in god.Before) await effect.OnActivation(ctx, god);
            Assert.True(Game.MonsterDotTaken(monster, DamageType.Fire) > 0);

            // Gloom and physical explode modifiers both contribute, each retaining its own mitigation type.
            using var explosion = GameEvents.PlayerStatSources.Add("shrine-explosion-regression", (c, values) =>
            {
                if (ReferenceEquals(c, conn))
                {
                    values[Program.Stats.Key("enemies_explode_for_%_life_as_physical_damage")!.Value] = 10;
                    values[Program.Stats.Key("explode_enemies_for_25%_life_as_chaos_on_kill_chance_%")!.Value] = 100;
                }
                return values;
            });
            var corpse = new SpawnedMonster("corpse", new(112, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000)
                { Dead = true, Life = 0 };
            conn.Monsters[0x7071] = corpse;
            monster.Life = monster.MaxLife = 10000;
            await Game.ExplodeOnKill(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, 0x7071, corpse, (112, 100));
            Assert.Equal(9650u, monster.Life);
        }
        finally
        {
            await ShrineManager.LeaveArea(conn);
            await ShrineManager.LeaveArea(peer.Conn);
            ShrineRegistry.RegisterDefaults();
        }
    }

    sealed class ShrineWire(TcpClient writer, TcpClient reader, ClientState conn, Task drain) : IAsyncDisposable
    {
        public ClientState Conn => conn;
        public static async Task<ShrineWire> Open(InstanceState instance)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var conn = new ClientState(new(0, "ShrineLifecycleTest", 1000, 1000, 100, 100, (_, _) => true, []), 0, 1)
            {
                Instance = instance, PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true,
                Pools = new(1, 1000, 1000, 1000, 0),
                LeagueModule = new(new GameServer.Core.Accounts.League("shrine-test", "Standard", false, false),
                    new HashSet<string>(["Game", "shrine-regression", "shrine-explosion-regression"])),
                Wire = ("shrine-regression", writer.GetStream(), new ConnectionCiphers(new byte[64], false)),
            };
            return new(writer, reader, conn, reader.GetStream().CopyToAsync(Stream.Null));
        }
        public async ValueTask DisposeAsync()
        {
            conn.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }
}
