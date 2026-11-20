using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Shrines;

namespace GameServer.Gateway.Tests;

public sealed class TormentTests
{
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Scarabs/" + leaf));
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }

    [GameFilesFact]
    public async Task Tormented_spirits_roll_from_the_scarabs_touch_possess_pay_out_and_come_back_with_a_kept_instance()
    {
        Assert.Equal(22, TormentCatalog.Pool(70, unusual: false).Count);
        Assert.Equal(["Metadata/Monsters/Spirit/TormentedBetrayer", "Metadata/Monsters/Spirit/TormentedFelon",
            "Metadata/Monsters/Spirit/TormentedTrespasser"], TormentCatalog.Pool(70, unusual: true).Select(s => s.Variety).Order());
        Assert.Equal((0, 120, 880), (TormentCatalog.LifePct(55), TormentCatalog.LifePct(61), TormentCatalog.LifePct(83)));

        // Two Torment Scarabs (4 spirits each) and the Peculiarity's unusual spirits: every stat has a provider.
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots = [Slot("ScarabTormentNew1"), Slot("ScarabTormentNew1"), Slot("ScarabTormentNew2")];
        var mods = catalog.Of(slots, Program.Items.Mods);
        Assert.Equal(8, mods.Get("map_spawn_extra_torment_spirits"));
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var opening = new MapOpening(new(new(1, "torment", 100, 100, 10, 10, null, []), 1, 1), slots, false,
            new(new(1, area.Id, "Strand Map", 3) { Level = 70 }, mods), "");
        MapScarabContent.Describe(opening);
        MapTorment.Opening(opening);
        Assert.Contains("Torment", opening.HandledContent);
        Assert.Empty(opening.UnhandledContent);

        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1, false));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7041, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("TormentOwner", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("torment");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var life = MonsterModEffects.HandOverMore([MapTorment.LifeStat], [], [MapTorment.TaperStat]);
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Conn;
        try
        {
            MapTorment.Spirit[] Spirits() => JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])!;
            await MapTorment.Enter(owner.Conn);
            var spirits = Spirits();
            Assert.InRange(spirits.Length, 8, 10);
            Assert.All(spirits, s => Assert.True(TormentCatalog.ByRow(s.Row)!.Unusual));
            await MapTorment.Enter(peer.Conn);
            Assert.Equal(spirits.Select(s => s.Monster), Spirits().Select(s => s.Monster));
            var spirit = spirits[0];
            var body = owner.Conn.Monsters[spirit.Monster];
            Assert.Equal((MonsterRarity.Unique, true), (body.Rarity, body.Scripted));
            Assert.Equal(-90, body.Effects[MapTorment.TaperStat]);
            Assert.Equal(0.1, body.Effects.DamageTaken(false), 6);

            // The peer is not the monster runner: standing on a spirit, its tick wakes nothing.
            peer.Conn.Walk.Teleport(spirits[1].X, spirits[1].Y, 0);
            await MapTorment.Tick(peer.Conn, 0.25);
            Assert.Equal(MapTorment.SpiritState.Waiting, Spirits()[1].State);

            // A normal and a rare monster where the spirit is, and the player close enough to wake it: in one tick it
            // touches the normal one and goes into the rare one.
            var w = owner.Conn.Wire;
            var member = Game.Packs.For(area.Id)!.Scattered.SelectMany(p => p.Pack.Entries)
                .Select(e => Game.Monsters.Find(e.Variety.ToString())).First(v => v is { Supported: true })!;
            uint normal = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, member, 70, MonsterRarity.Normal,
                spirit.X, spirit.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
            uint rare = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, member, 70, MonsterRarity.Rare,
                spirit.X, spirit.Y, unrisen: false, placement: Game.MonsterPlacement.Established);
            ShrineRegistry.RegisterDefaults();
            var massive = ShrineCatalog.Find("Massive")!;
            var shrine = new ShrineInstance { Definition = massive, At = (spirit.X, spirit.Y), Before = ShrineRegistry.Before(massive) };
            var shrineContext = new ShrineContext(owner.Conn, 0);
            int initialScale = owner.Conn.Monsters[rare].Effects.ActorScalePct;
            foreach (var effect in shrine.Before) await effect.OnNearbyMonsterEnter(shrineContext, shrine, rare);
            uint scale = Program.Stats.Key("actor_scale_+%")!.Value;
            Assert.Contains((scale, initialScale + 20), instance.World.MonsterOrigins[rare].Object.Stats!);
            owner.Conn.Walk.Teleport(spirit.X + 10, spirit.Y, 0);
            await MapTorment.Tick(owner.Conn, 0.25);
            Assert.Equal(MapTorment.SpiritState.Possessed, Spirits()[0].State);
            Assert.Equal(1, Spirits()[0].Touches);
            Assert.False(owner.Conn.Monsters.ContainsKey(spirit.Monster));
            var touched = owner.Conn.Monsters[normal];
            var possessed = owner.Conn.Monsters[rare];
            Assert.True(TormentCatalog.Has(touched.Mods, grip: false));
            Assert.True(TormentCatalog.Has(possessed.Mods, grip: true));
            Assert.Equal(2500, touched.Effects["from_league_item_quantity_+%_permyriad"]);
            Assert.True(possessed.Effects["from_league_item_rarity_+%_permyriad"] >= 3000);
            Assert.Equal(touched.Mods.Mods, instance.World.MonsterOrigins[normal].Object.Mods);

            // The spirit took the host while Massive was active. A later viewer sees the size and life after claim.
            uint enlarged = possessed.MaxLife;
            int enlargedScale = possessed.Effects.ActorScalePct;
            foreach (var effect in shrine.Before) await effect.OnActivation(shrineContext, shrine);
            var origin = instance.World.MonsterOrigins[rare].Object;
            Assert.True(possessed.MaxLife < enlarged);
            Assert.Contains((StatKeys.MaximumLife, (int)possessed.MaxLife), origin.Stats!);
            Assert.Contains((scale, enlargedScale - 20), origin.Stats!);
            var basic = possessed.Kit.Count > 0 ? possessed.Kit[0].Attack : possessed.Attack!;
            var expected = InstanceEntities.StatsUpdate(rare, [(StatKeys.MaximumLife, (int)possessed.MaxLife),
                (StatKeys.MovementVelocityPct, possessed.Effects.MovementPct), (scale, enlargedScale - 20), .. basic.Stats()]);
            // Another spawner's explicit life and size are also part of its creation, independent of the shrine.
            uint custom = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, member, 70, MonsterRarity.Normal,
                spirit.X, spirit.Y, mods: MonsterModSet.None, unrisen: false,
                extraStats: [(scale, 25), (StatKeys.MaximumLife, 1234567)], placement: Game.MonsterPlacement.Established);
            byte[] Creation(uint id)
            {
                var created = instance.World.MonsterOrigins[id];
                var actor = owner.Conn.Monsters[id];
                var at = actor.Walk.Position(owner.Conn.Clock.Elapsed.TotalSeconds);
                return InstanceEntities.CreateObject(id, created.Type,
                    (created.Object with { GridX = at.X, GridY = at.Y, Life = actor.Life }).Blob());
            }
            var create = Creation(rare);
            var customCreate = Creation(custom);
            var previousOutgoing = Program.Outgoing;
            var recorded = new List<byte[]>();
            Program.InGame.TryAdd(owner.Conn, 0);
            try
            {
                var otherWire = peer.Conn.Wire;
                Program.Outgoing = (ciphers, packet) => { if (ReferenceEquals(ciphers, otherWire.Ciphers)) recorded.Add(packet.ToArray()); };
                await Game.SyncInstanceMonsters(otherWire.Remote, otherWire.Stream, otherWire.Ciphers, peer.Conn);
                Assert.Contains(recorded, packet => packet.AsSpan().IndexOf(create) >= 0);
                Assert.Contains(recorded, packet => packet.AsSpan().IndexOf(customCreate) >= 0);
                recorded.Clear();
                await MapTorment.Tick(peer.Conn, 0.25);
                Assert.Contains(recorded, packet => packet.AsSpan().IndexOf(expected) >= 0);
            }
            finally
            {
                Program.Outgoing = previousOutgoing;
                Program.InGame.TryRemove(owner.Conn, out _);
            }

            // A kill (any kind: GameEvents.Kill) claims the payout once; the kill's own drop roll still sees the mark,
            // and the next tick drops the possessed rare's two rares and a slain spirit's rare.
            int ground = instance.Ground.All().Count();
            var killed = new KillContext(owner.Conn, possessed, possessed.Variety!.Id, (spirit.X, spirit.Y)) { PlayerKill = false };
            MapTorment.Kill(killed);
            MapTorment.Kill(killed);
            await MapTorment.Tick(owner.Conn, 0.25);
            Assert.InRange(instance.Ground.All().Skip(ground).Count(g => g.Item.Item.Rarity is ItemRarity.Rare or ItemRarity.Unique), 2, 3);
            ground = instance.Ground.All().Count();
            var other = owner.Conn.Monsters[spirits[1].Monster];
            MapTorment.Kill(new(owner.Conn, other, other.Variety!.Id, (spirits[1].X, spirits[1].Y)));
            await MapTorment.Tick(owner.Conn, 0.25);
            Assert.Equal(MapTorment.SpiritState.Slain, Spirits()[1].State);
            Assert.Contains(instance.Ground.All().Skip(ground), g => g.Item.Item.Rarity is ItemRarity.Rare or ItemRarity.Unique);

            // A kept instance brings its monsters back under new ids and without their script: the spirits that are
            // still waiting are made again, the stray copy goes.
            uint waiting = spirits[2].Monster;
            uint stray, again;
            int touchedMods = touched.Mods.Mods.Count;
            lock (owner.Conn.Monsters)
            {
                var kept = owner.Conn.Monsters[waiting];
                owner.Conn.Monsters.Remove(waiting);
                stray = owner.Conn.Instance.World.NewMonsterId();
                owner.Conn.Monsters[stray] = kept;
                owner.Conn.Monsters.Remove(normal);
                again = owner.Conn.Instance.World.NewMonsterId();
                owner.Conn.Monsters[again] = touched;
            }
            MapTorment.Leave(owner.Conn);
            await MapTorment.Enter(owner.Conn);
            var respawned = Spirits()[2];
            Assert.NotEqual(waiting, respawned.Monster);
            Assert.True(owner.Conn.Monsters[respawned.Monster].Scripted);
            Assert.False(owner.Conn.Monsters.ContainsKey(stray));
            // The touched monster came back with its Touch in its kept mods: marked again, not touched again.
            await MapTorment.Tick(owner.Conn, 0.25);
            Assert.Contains(again, JsonSerializer.Deserialize<Dictionary<uint, JsonElement>>(instance.ContentState["Torment/marks"])!.Keys);
            Assert.Equal(touchedMods, touched.Mods.Mods.Count);

            // A Martyr's Touch grants its level-one death skill to both the living actor and a restored actor.
            var martyrArea = new InstanceState();
            martyrArea.OpenedWith(instance.Map!);
            var martyr = TormentCatalog.All.Single(s => s.Variety == "Metadata/Monsters/Spirit/TormentedMartyr");
            int x = layout.SpawnX, y = layout.SpawnY;
            martyrArea.ContentState["Torment/spirits"] = JsonSerializer.Serialize(new[] { new MapTorment.Spirit { Row = martyr.Row, X = x, Y = y } });
            await using var witness = await ScarabEncounterTests.Wire.Open(martyrArea, layout, seed);
            var c = witness.Conn;
            martyrArea.World.Runner = c;
            c.Clock.Start();
            c.Walk.Teleport(x, y, 0);
            var zombie = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            var wire = c.Wire;
            uint hostId = await Game.SpawnMonster(wire.Remote, wire.Stream, wire.Ciphers, c, zombie, 70, MonsterRarity.Normal,
                x, y, mods: MonsterModSet.None, unrisen: false, placement: Game.MonsterPlacement.Established);
            var before = martyrArea.World.MonsterOrigins[hostId].Object.Skills!.Select(s => (s.Id, s.EffectsPerLevelRow)).ToArray();
            uint deathRow = (uint)Assert.Single(Program.Items.Mods.ById("TormentTouchedMonsterMartyr")!.GrantedEffectLevels);
            Assert.DoesNotContain(before, s => s.EffectsPerLevelRow == deathRow);
            await MapTorment.Enter(c);
            await MapTorment.Tick(c, .25);
            var host = c.Monsters[hostId];
            var live = martyrArea.World.MonsterOrigins[hostId].Object.Skills!;
            var grant = Assert.Single(live, s => s.EffectsPerLevelRow == deathRow);
            Assert.Equal(before, live.Where(s => s.Id != grant.Id).Select(s => (s.Id, s.EffectsPerLevelRow)));
            Assert.DoesNotContain(host.Kit, s => s.Attack.SkillId == grant.Id);
            var saved = Assert.Single(SavedMonster.Save([host], 0));
            uint restoredId = await Game.RestoreMonster(wire.Remote, wire.Stream, wire.Ciphers, c, zombie, saved);
            var restored = martyrArea.World.MonsterOrigins[restoredId].Object.Skills!;
            Assert.Equal(live.Select(s => (s.Id, s.EffectsPerLevelRow)), restored.Select(s => (s.Id, s.EffectsPerLevelRow)));
        }
        finally
        {
            PlayerInventories.Forget(owner.Conn.Character!);
            PlayerInventories.Forget(peer.Conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [GameFilesFact]
    public async Task Atlas_and_map_torment_stats_possess_rares_bosses_and_players_and_dig_up_a_monster()
    {
        // The Atlas tree's Seance and Speaker of the Dead (composed as the tree's stats), and map stats (the Idols' are
        // the same stats, added to the map's): an extra spirit, the Enthralled boss, spirit speed, Blessed possessed
        // monsters and the Torment Scarab's release and dig.
        int Key(string stat) => (int)Program.Stats.Key(stat)!.Value;
        var atlas = new Dictionary<uint, int>
        {
            [(uint)Key("map_rare_monsters_possessed_with_touched_minions_chance_%")] = 100,
            [(uint)Key("map_rare_monsters_possessed_with_touched_minions_num")] = 1,
            [(uint)Key("atlas_keystone_speaker_of_the_dead")] = 1,
        };
        var mods = new MapMods(new Dictionary<string, int>
        {
            ["map_spawn_extra_torment_spirits"] = 1, ["map_boss_is_possessed"] = 1, ["map_tormented_spirits_movement_speed_+%"] = 30,
            ["map_monster_damage_taken_+%_while_possessed"] = 20, ["map_tormented_spirit_release_and_dig_%_chance"] = 100,
            ["map_boss_damage_+%"] = 30, ["map_boss_attack_and_cast_speed_+%"] = 20, ["map_monsters_%_physical_damage_to_add_as_lightning"] = 40,
        });
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, atlas, new HashSet<string>(), -1, false));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7042, 70, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 70));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("TormentAtlas", new(layout.Hash, area.Id, "Strand", 3) { Level = 70 }, mods));

        TestEnvironment.OpenOwnSaves("torment-atlas");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        using var life = MonsterModEffects.HandOverMore([MapTorment.LifeStat], [], [MapTorment.TaperStat]);
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Conn;
        try
        {
            await MapTorment.Enter(owner.Conn);
            var spirit = JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])![0];
            Assert.Equal("1", instance.ContentState["Torment/seance"]);
            var body = owner.Conn.Monsters[spirit.Monster];
            Assert.True(body.Effects["base_movement_velocity_+%"] >= 20); // the map's 30, less a spirit's own 10

            // Far from the spirit: a rare with a minion (Seance) and the map's boss (Enthralled).
            var w = owner.Conn.Wire;
            var packs = Game.Packs.For(area.Id)!;
            var member = packs.Scattered.SelectMany(p => p.Pack.Entries).Select(e => Game.Monsters.Find(e.Variety.ToString()))
                .First(v => v is { Supported: true })!;
            var bossVariety = packs.Bosses.Select(row => Game.Monsters.Find(row.ToString())).Single(v => v?.Name == "Massier")!;
            var far = layout.Near((spirit.X > layout.GridW / 2 ? spirit.X - 150 : spirit.X + 150, spirit.Y));
            uint minion = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, member, 70, MonsterRarity.Normal,
                far.X + 3, far.Y, unrisen: false);
            uint rare = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, member, 70, MonsterRarity.Rare,
                far.X, far.Y, unrisen: false);
            uint boss = await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, bossVariety, 70, MonsterRarity.Unique,
                far.X, far.Y + 20, unrisen: false);
            var massier = owner.Conn.Monsters[boss];
            var second = Assert.Single(massier.Kit, k => k.Effect == 1329);
            massier.Attack = second.Attack;
            BossKits.Consume(massier, second.Attack, 8);
            var here = massier.Walk.Position(0);
            owner.Conn.Walk.Teleport(here.X, here.Y, 0);
            Game.PlaceLockstepChase(owner.Conn, massier, massier.ActionGeneration, 0);
            Assert.Equal(0d, massier.ClientSwingAt);
            owner.Conn.Walk.Teleport(spirit.X, spirit.Y, 0);
            await MapTorment.Tick(owner.Conn, 0.25);
            Assert.True(TormentCatalog.Has(owner.Conn.Monsters[rare].Mods, grip: true));
            Assert.True(owner.Conn.Monsters[rare].Effects["base_damage_taken_+%"] >= 20);
            Assert.True(TormentCatalog.Has(owner.Conn.Monsters[minion].Mods, grip: false));
            Assert.True(TormentCatalog.Has(massier.Mods, grip: true));
            var selected = massier.Kit.Single(k => k.Effect == second.Effect);
            Assert.Same(selected.Attack, massier.Attack);
            Assert.True(selected.Attack.Damage.MinTotal > second.Attack.Damage.MinTotal);
            Assert.True(selected.Attack.Seconds < second.Attack.Seconds);
            Assert.Same(second.Attack, massier.ClientSwingStats!.Value.Attack);
            Assert.Equal(second.Attack.Seconds, massier.ClientSwingStats.Value.Seconds);
            Assert.Equal(8 + second.CooldownSeconds, massier.KitReadyAt[second.Effect]);
            var raw = BossKits.AttackFor(owner.Conn, bossVariety, 70, selected.Effect, selected.Attack.SkillId);
            Assert.Equal(massier.Mods.Effects.Hit(raw.Damage).Components, selected.Attack.Damage.Components);

            var saved = Assert.Single(SavedMonster.Save([massier], 0));
            uint copy = await Game.RestoreMonster(w.Remote, w.Stream, w.Ciphers, owner.Conn, bossVariety, saved);
            var restored = owner.Conn.Monsters[copy];
            Assert.Equal((massier.MaxLife, massier.Life), (restored.MaxLife, restored.Life));
            Assert.Equal(massier.Kit.Select(k => (k.Effect, k.Attack.SkillId, k.Attack.Seconds, k.CooldownSeconds)),
                restored.Kit.Select(k => (k.Effect, k.Attack.SkillId, k.Attack.Seconds, k.CooldownSeconds)));
            var shown = instance.World.MonsterOrigins[boss].Object;
            var rebuilt = instance.World.MonsterOrigins[copy].Object;
            foreach (var (live, kept) in massier.Kit.Zip(restored.Kit))
            {
                Assert.Equal(live.Attack.Damage.Components, kept.Attack.Damage.Components);
                var skill = shown.Skills!.Single(s => s.Id == live.Attack.SkillId);
                Assert.Equal(rebuilt.Skills!.Single(s => s.Id == skill.Id).Stats, skill.Stats);
                Assert.Contains((StatKeys.MainHandAttackDurationMs, (int)Math.Round(live.Attack.Seconds * 1000)), skill.Stats!);
            }
            Assert.Equal(rebuilt.Stats!.Single(s => s.Key == StatKeys.MainHandMinTotal),
                shown.Stats!.Single(s => s.Key == StatKeys.MainHandMinTotal));
            Assert.Equal("0", instance.ContentState["Torment/seance"]);

            // Speaker of the Dead: the spirit the player stands on goes into the player, who gets its Possessed buff.
            var after = JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])![0];
            Assert.Equal(MapTorment.SpiritState.Possessed, after.State);
            Assert.False(owner.Conn.Monsters.ContainsKey(spirit.Monster));
            var player = MapTorment.PlayerStats(owner.Conn, []);
            Assert.True(player.GetValueOrDefault((uint)Key("attack_speed_+%")) >= 35);
            Assert.DoesNotContain((uint)Key("monster_is_possessed"), player.Keys);
            MapTorment.Leave(owner.Conn);
            Assert.Empty(MapTorment.PlayerStats(owner.Conn, []));

            // The possessed rare dies: its spirit is set free and digs up another possessed monster.
            int spirits = JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])!.Length;
            var possessed = owner.Conn.Monsters[rare];
            possessed.Dead = true;
            MapTorment.Kill(new(owner.Conn, possessed, possessed.Variety!.Id, (far.X, far.Y)));
            // The freed spirit is active and walks: a monster's order waits for a client that is not paused.
            owner.Conn.Clock.Start();
            await MapTorment.Tick(owner.Conn, 0.25);
            Assert.Equal(spirits + 1, JsonSerializer.Deserialize<MapTorment.Spirit[]>(instance.ContentState["Torment/spirits"])!.Length);
            Assert.Contains(owner.Conn.Monsters, p => p.Key > boss && !p.Value.Dead && p.Value.Rarity == MonsterRarity.Rare
                && TormentCatalog.Has(p.Value.Mods, grip: true));
        }
        finally
        {
            PlayerInventories.Forget(owner.Conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
