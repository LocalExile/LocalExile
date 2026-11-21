using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Shrines;

namespace GameServer.Gateway.Tests;

public sealed class HarbingerTests
{
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Scarabs/" + leaf));
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }

    static (int, int, int) Wave(HarbingerCatalog.HarbingerKind kind, int level) =>
        HarbingerCatalog.Profile(kind, level) is var p ? (p.BaseCount, p.ExtraCountChancePct, p.Permyriad) : default;

    [Fact]
    public void Harbinger_catalog_opening_and_shard_drops_honor_scarabs_stats_and_329_exclusions()
    {
        // All 28 Harbingers rows load (19 normal + 9 strong), level-filtered properly.
        Assert.Equal(28, HarbingerCatalog.All.Count);
        Assert.Equal(19, HarbingerCatalog.All.Count(e => !e.Strong));
        Assert.Equal(9, HarbingerCatalog.All.Count(e => e.Strong));
        Assert.DoesNotContain(HarbingerCatalog.Pool(20), e => e.Strong);
        Assert.Contains(HarbingerCatalog.Pool(75), e => e.Strong);

        // Wave profiles are the client's HarbingerSummonMonsters / 2 / Atlas rows for the monster level.
        Assert.Equal((6, 25, 410), Wave(HarbingerCatalog.HarbingerKind.Normal, 60));
        Assert.Equal((6, 25, 400), Wave(HarbingerCatalog.HarbingerKind.Strong, 60));
        Assert.Equal((8, 61, 181), Wave(HarbingerCatalog.HarbingerKind.Strong, 75));
        var kingWave = HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.King, 83);
        Assert.Equal((15, 15, 1), (kingWave.BaseCount, kingWave.MaxMagic, kingWave.MaxRare));
        var atlasShards = new MapMods(new Dictionary<string, int> { ["map_harbingers_drops_additional_currency_shards"] = 38 });
        Assert.Equal(6, HarbingerCatalog.RollShards(Program.Items, 75, HarbingerCatalog.NormalBaseTenths, 0, atlasShards, new Random(1)).Count);

        // Exactly 11 active 3.29 currency shards; disabled 3.29 shards (Horizon, Harbinger's, Engineer's) excluded.
        Assert.Equal(11, HarbingerCatalog.Shards.Count);
        string[] excluded =
        [
            "Metadata/Items/Currency/CurrencyRerollMapTypeShard",
            "Metadata/Items/Currency/CurrencyUpgradeMapTierShard",
            "Metadata/Items/Currency/CurrencyStrongboxQualityShard",
        ];
        foreach (string id in excluded)
            Assert.DoesNotContain(HarbingerCatalog.Shards, s => s.Id == id);
        foreach (var entry in HarbingerCatalog.Shards)
        {
            var shardType = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find(entry.Id));
            Assert.True(shardType.Supported);
            Assert.True(shardType.BecomesWhenFull);
            var fullOrb = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Row(shardType.FullStackRow));
            Assert.True(fullOrb.Supported);
        }

        // All 4 active 3.29 Harbinger scarabs are claimed as handled in MapOpening with no unhandled content.
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots =
        [
            Slot("ScarabHarbingerNew1"),
            Slot("ScarabHarbingerNew2"),
            Slot("ScarabHarbingerNew3"),
            Slot("ScarabHarbingerNew4"),
        ];
        var mods = catalog.Of(slots, Program.Items.Mods);
        Assert.Equal(3, mods.Get("map_num_extra_harbingers"));
        Assert.Equal(1, mods.Get("map_harbinger_scarab_empower"));
        Assert.Equal(50, mods.Get("map_harbingers_%_chance_to_be_replaced_as_atlas_boss"));
        Assert.Equal(100, mods.Get("map_harbinger_currency_shards_duplicate_chance"));

        var opening = new MapOpening(new(new(1, "harbinger", 100, 100, 10, 10, null, []), 1, 1), slots, false,
            new(new(1, "MapWorldsStrand", "Strand Map", 8) { Level = 75 }, mods), "");
        MapScarabContent.Describe(opening);
        MapHarbinger.Opening(opening);
        Assert.Contains(MapHarbinger.Content, opening.HandledContent);
        Assert.Empty(opening.UnhandledContent);

        // Warhoards duplication emits identical pairs; bonus tenths increase stack counts; single-type locks one shard.
        var warhoards = new MapMods(new Dictionary<string, int>
        {
            ["map_harbinger_currency_shards_duplicate_chance"] = 100,
            ["map_harbinger_additional_currency_shard_stack_tenths_to_drop"] = 8,
            ["map_harbinger_single_currency_type"] = 1,
        });
        var dupDrops = HarbingerCatalog.RollShards(Program.Items, 75, HarbingerCatalog.NormalBaseTenths, 0, warhoards, new Random(42));
        Assert.Equal(6, dupDrops.Count); // (22 + 8) / 10 = 3 stacks, duplicated -> 6 stacks
        Assert.Single(dupDrops.Select(d => d.Type.Id).Distinct());
        for (int i = 0; i < dupDrops.Count; i += 2)
            Assert.Equal((dupDrops[i].Type.Id, dupDrops[i].Item.StackSize), (dupDrops[i + 1].Type.Id, dupDrops[i + 1].Item.StackSize));

        // Full-currency upgrade stat converts rolled shard stacks into their full orb (StackSize 1, non-shard).
        var fullCurrencyMods = new MapMods(new Dictionary<string, int>
        {
            ["map_harbingers_have_chance_to_drop_full_currency"] = 3, // 75% per stack
            ["map_harbinger_lucky_shard_drops"] = 1,
        });
        var upgraded = HarbingerCatalog.RollShards(Program.Items, 75, HarbingerCatalog.KingBaseTenths, HarbingerCatalog.KingStackSizePct, fullCurrencyMods, new Random(7));
        Assert.InRange(upgraded.Count, 10, 11);
        Assert.Contains(upgraded, d => !d.Type.BecomesWhenFull && d.Item.StackSize == 1);
    }

    [GameFilesFact]
    public async Task Harbingers_spawn_summon_waves_lose_life_on_kills_spawn_obelisks_and_drop_duplicated_shards()
    {
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots =
        [
            Slot("ScarabHarbingerNew1"), // +3 harbingers
            Slot("ScarabHarbingerNew2"), // Empowered Obelisks
            Slot("ScarabHarbingerNew4"), // Warhoards 100% duplicate shards
        ];
        var scarabMods = catalog.Of(slots, Program.Items.Mods);
        // Add 1 guaranteed King Harbinger + cooldown speed + bonus tenths via composed stats so both regular and King
        // encounters and obelisk shard drops run deterministically without flakiness.
        var combined = new Dictionary<string, int>(scarabMods.Stats, StringComparer.Ordinal)
        {
            ["map_contains_X_additional_uber_harbingers"] = 1,
            ["map_harbinger_cooldown_speed_+%"] = 100,
            ["map_harbinger_additional_currency_shard_stack_tenths_to_drop"] = 5,
        };
        var mods = new MapMods(combined);

        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 75);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 8, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x4841, 75, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 75));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("HarbingerOwner", new(layout.Hash, area.Id, "Strand", 8) { Level = 75 }, mods));

        TestEnvironment.OpenOwnSaves("harbinger");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Conn;
        try
        {
            MapHarbinger.Encounter[] Encounters() => JsonSerializer.Deserialize<MapHarbinger.Encounter[]>(instance.ContentState["Harbinger/encounters"])!;

            await MapHarbinger.Enter(owner.Conn);
            var list = Encounters();
            Assert.Equal(4, list.Length); // 3 from scarab + 1 guaranteed King
            Assert.Contains(list, h => h.Kind == HarbingerCatalog.HarbingerKind.King && h.Variety == HarbingerCatalog.KingVariety);
            // Second entrant shares the same encounters without double-spawning.
            await MapHarbinger.Enter(peer.Conn);
            Assert.Equal(list.Select(h => h.Monster), Encounters().Select(h => h.Monster));

            var king = list.First(h => h.Kind == HarbingerCatalog.HarbingerKind.King);
            var normal = list.First(h => h.Kind != HarbingerCatalog.HarbingerKind.King);
            var kingBody = owner.Conn.Monsters[king.Monster];
            Assert.Equal((MonsterRarity.Unique, true, true, MapHarbinger.HarbingerMaxLife),
                (kingBody.Rarity, kingBody.Scripted, kingBody.NoRewards, kingBody.MaxLife));
            Assert.Equal(1, kingBody.EncounterStats!["base_cannot_be_damaged"]);

            // Non-runner peer standing on the King Harbinger does not activate it.
            peer.Conn.Walk.Teleport(king.X, king.Y, 0);
            await MapHarbinger.Tick(peer.Conn, 0.25);
            Assert.Equal(MapHarbinger.HarbingerState.Waiting, Encounters()[king.Index].State);

            // Runner approaches the King Harbinger: activates, spawns 3 invulnerable helpers and first wave of minions.
            owner.Conn.Walk.Teleport(king.X + 8, king.Y, 0);
            await MapHarbinger.Tick(owner.Conn, 0.25);
            king = Encounters()[king.Index];
            Assert.Equal(MapHarbinger.HarbingerState.Active, king.State);
            Assert.Equal(3, king.Helpers.Count);
            Assert.NotEmpty(king.Minions);
            Assert.All(king.Helpers, hid => Assert.Equal(1, owner.Conn.Monsters[hid].EncounterStats!["base_cannot_be_damaged"]));

            // Advance timer (zero real-time sleep): Empowered Obelisk spawns from Scarab of Obelisks.
            await MapHarbinger.Tick(owner.Conn, 5.0);
            king = Encounters()[king.Index];
            Assert.Single(king.Obelisks);
            uint obeliskId = king.Obelisks[0];
            var obeliskBody = owner.Conn.Monsters[obeliskId];
            Assert.Contains(obeliskBody.Variety!.Id, HarbingerCatalog.EmpoweredObeliskVarieties);

            // Killing the Empowered Obelisk drops duplicated currency shard stacks (Warhoards).
            int groundBeforeObelisk = instance.Ground.All().Count();
            lock (owner.Conn.Monsters) { obeliskBody.Life = 0; obeliskBody.Dead = true; }
            MapHarbinger.Kill(new(owner.Conn, obeliskBody, obeliskBody.Variety!.Id, (king.X, king.Y)));
            await MapHarbinger.Tick(owner.Conn, 0.25);
            var obeliskDrops = instance.Ground.All().Skip(groundBeforeObelisk).ToArray();
            Assert.True(obeliskDrops.Length >= 2 && obeliskDrops.Length % 2 == 0,
                $"Expected even (>=2) Warhoards-duplicated obelisk drops, got {obeliskDrops.Length}");

            // Now activate a Normal/Strong Harbinger, kill its summoned waves until defeated, and verify life-loss & shard drops.
            owner.Conn.Walk.Teleport(normal.X + 8, normal.Y, 0);
            await MapHarbinger.Tick(owner.Conn, 0.25);
            normal = Encounters()[normal.Index];
            Assert.Equal(MapHarbinger.HarbingerState.Active, normal.State);
            Assert.NotEmpty(normal.Minions);

            uint startLife = normal.Life;
            ShrineRegistry.RegisterDefaults();
            var massive = ShrineCatalog.Find("Massive")!;
            var shrine = new ShrineInstance { Definition = massive, At = (normal.X, normal.Y), Before = ShrineRegistry.Before(massive) };
            var ctx = new ShrineContext(owner.Conn, 0);
            var avatar = owner.Conn.Monsters[normal.Monster];
            foreach (var effect in shrine.Before) await effect.OnNearbyMonsterEnter(ctx, shrine, normal.Monster);
            Assert.Equal((16_000u, 16_000u), (avatar.Life, avatar.MaxLife));
            uint firstMinion = normal.Minions[0];
            var firstBody = owner.Conn.Monsters[firstMinion];
            lock (owner.Conn.Monsters) { firstBody.Life = 0; firstBody.Dead = true; }
            var wire = owner.Conn.Wire;
            var sent = new List<byte[]>();
            var previousOutput = Program.Outgoing;
            try
            {
                Program.Outgoing = (cipher, packet) => { if (cipher == wire.Ciphers) sent.Add(packet.ToArray()); };
                MapHarbinger.Kill(new(owner.Conn, firstBody, firstBody.Variety!.Id, (normal.X, normal.Y)));
                await MapHarbinger.Tick(owner.Conn, 0.25);
                await Game.SyncInstanceMonsters(wire.Remote, wire.Stream, wire.Ciphers, owner.Conn);
            }
            finally { Program.Outgoing = previousOutput; }
            uint remaining = Encounters()[normal.Index].Life;
            Assert.True(remaining < startLife, "Killing a summoned minion must reduce Harbinger life");
            Assert.Equal((uint)Math.Ceiling((double)remaining * avatar.MaxLife / startLife), avatar.Life);
            byte[] records = [.. sent.SelectMany(packet => packet)];
            int maximumAt = records.AsSpan().IndexOf(InstanceEntities.StatsUpdate(normal.Monster, Game.MonsterLifeAndScale(avatar)));
            int lifeAt = records.AsSpan().IndexOf(InstanceProtocol.LifeUpdate(normal.Monster, avatar.Life));
            Assert.True(maximumAt >= 0 && lifeAt > maximumAt);
            foreach (var effect in shrine.Before) await effect.OnNearbyMonsterLeave(ctx, shrine, normal.Monster);
            Assert.Equal((remaining, startLife), (avatar.Life, avatar.MaxLife));

            int groundBeforeDefeat = instance.Ground.All().Count();
            for (int guard = 0; guard < 120 && Encounters()[normal.Index].State != MapHarbinger.HarbingerState.Defeated; guard++)
            {
                var cur = Encounters()[normal.Index];
                foreach (uint mid in cur.Minions.ToArray())
                {
                    if (!owner.Conn.Monsters.TryGetValue(mid, out var mm) || mm.Dead) continue;
                    lock (owner.Conn.Monsters) { mm.Life = 0; mm.Dead = true; }
                    MapHarbinger.Kill(new(owner.Conn, mm, mm.Variety!.Id, (cur.X, cur.Y)));
                }
                await MapHarbinger.Tick(owner.Conn, 3.5);
            }

            var defeated = Encounters()[normal.Index];
            Assert.Equal(MapHarbinger.HarbingerState.Defeated, defeated.State);
            Assert.Equal(0u, defeated.Life);
            Assert.Empty(defeated.Obelisks);
            Assert.False(owner.Conn.Monsters.ContainsKey(normal.Monster));
            var harbingerDrops = instance.Ground.All().Skip(groundBeforeDefeat).ToArray();
            Assert.True(harbingerDrops.Length >= 4 && harbingerDrops.Length % 2 == 0,
                $"Expected even (>=4) Warhoards-duplicated Harbinger shard stacks, got {harbingerDrops.Length}");

            // Kept-instance restore: a stray unmanaged Harbinger copy is replaced without duplicating encounters.
            var strayTarget = list.First(h => h.Index != king.Index && h.Index != normal.Index);
            uint oldMonster = strayTarget.Monster;
            uint strayId;
            lock (owner.Conn.Monsters)
            {
                var kept = owner.Conn.Monsters[oldMonster];
                owner.Conn.Monsters.Remove(oldMonster);
                strayId = owner.Conn.Instance.World.NewMonsterId();
                owner.Conn.Monsters[strayId] = kept;
            }
            MapHarbinger.Leave(owner.Conn);
            await MapHarbinger.Enter(owner.Conn);
            var restored = Encounters()[strayTarget.Index];
            Assert.NotEqual(oldMonster, restored.Monster);
            Assert.False(owner.Conn.Monsters.ContainsKey(strayId));
            Assert.True(owner.Conn.Monsters[restored.Monster].Scripted);
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

    [Fact]
    public void Harbinger_skill_repeat_count_variety_kits_and_obelisk_auras_load_from_client_db()
    {
        // skill_repeat_count from HarbingerSummonMonsters / 2 / Atlas in client.db.
        Assert.Equal(1, HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.Normal, 20).RepeatCount);
        Assert.Equal(1, HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.Normal, 60).RepeatCount);
        Assert.Equal(2, HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.Normal, 75).RepeatCount);
        Assert.Equal(1, HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.Strong, 60).RepeatCount);
        Assert.Equal(2, HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.Strong, 75).RepeatCount);
        var kingProfile = HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.King, 83);
        Assert.Equal((2.0, 2), (kingProfile.CooldownSeconds, kingProfile.RepeatCount));

        // All 28 Harbinger varieties decode Summon + Convocation + disjoint skill IDs (>= 0x4040).
        foreach (var entry in HarbingerCatalog.All)
        {
            var kit = HarbingerCatalog.KitFor(entry.Variety);
            Assert.Contains(kit.Skills, s => s.Role == HarbingerCatalog.SkillRole.Summon);
            Assert.Contains(kit.Skills, s => s.Role == HarbingerCatalog.SkillRole.Convocation);
            Assert.All(kit.Skills, s => Assert.True(s.SkillId >= HarbingerCatalog.SkillIdBase));
            Assert.Equal(kit.Skills.Count, kit.Skills.Select(s => s.SkillId).Distinct().Count());
        }

        // Buff Harbingers carry their real EmptyActionSpellHarbingerBuffWeak/Strong skills.
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/Avatar6").Skills,
            s => s.Role == HarbingerCatalog.SkillRole.MinionBuffStrong);
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/Avatar7").Skills,
            s => s.Role == HarbingerCatalog.SkillRole.MinionBuffWeak);

        // King Harbinger and all 6 helpers (AvatarAtlasMinion1..6) decode their distinct kits.
        var kingKit = HarbingerCatalog.KitFor(HarbingerCatalog.KingVariety);
        Assert.Contains(kingKit.Skills, s => s.EffectId == HarbingerCatalog.SummonAtlasEffectId);
        Assert.Contains(kingKit.Skills, s => s.Role == HarbingerCatalog.SkillRole.WarpFlame);
        Assert.Contains(kingKit.Skills, s => s.Role == HarbingerCatalog.SkillRole.BallLightning);

        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/AvatarAtlasMinion1").Skills,
            s => s.EffectId == "AvatarFireMortarAtlas" && s.Role == HarbingerCatalog.SkillRole.FireMortar);
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/AvatarAtlasMinion2").Skills,
            s => s.EffectId == "HarbingerSpecialBeamCannonAtlas" && s.Role == HarbingerCatalog.SkillRole.BeamCannon);
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/AvatarAtlasMinion3").Skills,
            s => s.EffectId == "HarbingerBallLightningAtlas" && s.Role == HarbingerCatalog.SkillRole.BallLightning);
        Assert.Equal(3, HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/AvatarAtlasMinion4").Skills
            .Count(s => s.Role == HarbingerCatalog.SkillRole.AllyDamageAura));
        Assert.Equal(2, HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/AvatarAtlasMinion5").Skills
            .Count(s => s.Role == HarbingerCatalog.SkillRole.PlayerCurseAura));
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Avatar/AvatarAtlasMinion6").Skills,
            s => s.EffectId == "HarbingerActionSpeedAuraAtlas" && s.Role == HarbingerCatalog.SkillRole.AllyActionSpeedAura);

        // Empowered Obelisk totems: Scarab2 Ball Lightning nova, Scarab4 Warp Flame, Scarab3 Mods [5062, 6548, 5063].
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Totems/HarbingerTotemScarab2").Skills,
            s => s.EffectId == "HarbingerBallLightningTotem" && s.Role == HarbingerCatalog.SkillRole.BallLightning);
        Assert.Contains(HarbingerCatalog.KitFor("Metadata/Monsters/Totems/HarbingerTotemScarab4").Skills,
            s => s.EffectId == "HarbingerWarpFlameTotem" && s.Role == HarbingerCatalog.SkillRole.WarpFlame);
        var auraObelisk = HarbingerCatalog.KitFor("Metadata/Monsters/Totems/HarbingerTotemScarab3");
        Assert.Empty(auraObelisk.Skills);
        var dmgRed = Assert.Single(auraObelisk.ObeliskAuras, a => a.BuffRow == HarbingerCatalog.DamageReductionAuraBuffRow);
        Assert.Equal(-75, dmgRed.Stats["base_damage_taken_+%"]);
        Assert.True(dmgRed.Radius > 140, $"Expected scaled damage reduction aura radius > 140, got {dmgRed.Radius}");
        var speed = Assert.Single(auraObelisk.ObeliskAuras, a => a.BuffRow == HarbingerCatalog.ActionSpeedAuraBuffRow);
        Assert.Equal(-20, speed.Stats["action_speed_-%"]);
        Assert.True(speed.Radius > 180, $"Expected scaled action speed aura radius > 180, got {speed.Radius}");

        // Single-level Harbinger skills scale cleanly via GolemData.Skill (level 75 >> level 1).
        int mortarRow = Program.Skills.EffectRow("AvatarFireMortarAtlas");
        var mortarL1 = Game.GolemData.Skill(Program.Skills, mortarRow, 1);
        var mortarL75 = Game.GolemData.Skill(Program.Skills, mortarRow, 75);
        uint fireMaxKey = Program.Stats.Key("spell_maximum_base_fire_damage")!.Value;
        Assert.True(mortarL75.Stats[fireMaxKey] * mortarL75.Multiplier > mortarL1.Stats[fireMaxKey] * mortarL1.Multiplier * 10);
    }

    sealed class CaptureWire(TcpClient writer, TcpClient reader, ClientState conn, MemoryStream captured, Task drain) : IAsyncDisposable
    {
        bool finished;
        public ClientState Conn => conn;
        public async Task<byte[]> FinishAndCaptureAsync()
        {
            if (!finished)
            {
                finished = true;
                conn.Closed = true;
                try { writer.Client.Shutdown(SocketShutdown.Send); } catch { }
                await drain;
            }
            lock (captured) return captured.ToArray();
        }
        public static async Task<CaptureWire> Open(InstanceState instance, AreaLayout layout, uint seed)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            var writer = new TcpClient();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var captured = new MemoryStream();
            var clientCiphers = new ConnectionCiphers(new byte[64], true);
            var conn = new ClientState(layout, 1, seed)
            {
                Character = "HarbSkill" + Guid.NewGuid().ToString("N"), Instance = instance,
                PlayerLife = 5000, PlayerMana = 1000, PlayerSent = true, Pools = new(75, 5000, 1000, 1000, 0),
                LeagueModule = new(new GameServer.Core.Accounts.League("harb-skills", "Standard", false, false),
                    new HashSet<string>(["Game", "harb-skills"])),
                Wire = ("harb-skills", writer.GetStream(), new ConnectionCiphers(new byte[64], false)),
            };
            var drain = Task.Run(async () =>
            {
                byte[] buf = new byte[4096];
                try
                {
                    int n;
                    while ((n = await reader.GetStream().ReadAsync(buf)) > 0)
                    {
                        clientCiphers.Receive.Process(buf.AsSpan(0, n));
                        lock (captured) captured.Write(buf, 0, n);
                    }
                }
                catch { }
            });
            return new(writer, reader, conn, captured, drain);
        }
        public async ValueTask DisposeAsync()
        {
            Conn.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }

    static bool WireContainsAction0x1ab(byte[] bytes, uint objectId)
    {
        // ActionStarted (0x01ab) starts with big-endian [0x01, 0xab] followed by big-endian objectId and u32 0.
        for (int i = 0; i <= bytes.Length - 10; i++)
        {
            if (bytes[i] == 0x01 && bytes[i + 1] == 0xab
                && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 2, 4)) == objectId
                && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 6, 4)) == 0)
                return true;
        }
        return false;
    }

    [Fact]
    public async Task Harbinger_repeat_waves_helper_skills_warp_and_obelisk_auras_execute_and_clean_up()
    {
        var layout = new AreaLayout(1, "MapWorldsStrand", 300, 300, 150, 150, (_, _) => true, []);
        var instance = new InstanceState();
        var mods = new MapMods(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["map_harbinger_cooldown_speed_+%"] = 100,
        });
        instance.OpenedWith(new("HarbSkillsOwner", new(layout.Hash, layout.Id, "Strand", 8) { Level = 75 }, mods));

        TestEnvironment.OpenOwnSaves("harbinger-skills");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        await using var wire = await CaptureWire.Open(instance, layout, 0x5544);
        var conn = wire.Conn;
        IDisposable? tankHook = Program.Stats.Key("base_maximum_life") is uint maxLifeKey
            ? GameEvents.PlayerStatSources.Add("harb-skills", (c, s) =>
            {
                if (ReferenceEquals(c, conn)) s[maxLifeKey] = s.GetValueOrDefault(maxLifeKey) + 100_000;
                return s;
            })
            : null;
        conn.Pools = Game.PoolsFor(conn, 75);
        conn.PlayerLife = conn.PlayerMaxLife;
        instance.World.Runner = conn;
        try
        {
            // Seed two deterministic encounters:
            // 0 = King Harbinger with explicit helpers (Minion1 FireMortar, Minion2 BeamCannon, Minion5 Curses, Minion6 ActionSpeedAura)
            //     and explicit Empowered Obelisks (Scarab2 BallLightning, Scarab3 Aura, Scarab4 WarpFlame).
            // 1 = Avatar6 (EmptyActionSpellHarbingerBuffStrong).
            async Task<uint> SpawnHelper(string varietyId, int x, int y, bool invulnerable = true)
            {
                var v = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(varietyId));
                var w = conn.Wire;
                return await Game.SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, v, 75,
                    invulnerable ? MonsterRarity.Unique : MonsterRarity.Normal, x, y, name: null, alone: true, unrisen: false,
                    owned: (_, m) =>
                    {
                        if (invulnerable) m.EncounterStats = new Dictionary<string, int>(StringComparer.Ordinal) { ["base_cannot_be_damaged"] = 1 };
                        m.NoRewards = true;
                    },
                    scripted: true);
            }

            uint m1 = await SpawnHelper("Metadata/Monsters/Avatar/AvatarAtlasMinion1", 145, 150);
            uint m2 = await SpawnHelper("Metadata/Monsters/Avatar/AvatarAtlasMinion2", 155, 150);
            uint m5 = await SpawnHelper("Metadata/Monsters/Avatar/AvatarAtlasMinion5", 150, 145);
            uint m6 = await SpawnHelper("Metadata/Monsters/Avatar/AvatarAtlasMinion6", 150, 155);
            uint ob2 = await SpawnHelper("Metadata/Monsters/Totems/HarbingerTotemScarab2", 142, 152, invulnerable: false);
            uint ob3 = await SpawnHelper("Metadata/Monsters/Totems/HarbingerTotemScarab3", 158, 152, invulnerable: false);
            uint ob4 = await SpawnHelper("Metadata/Monsters/Totems/HarbingerTotemScarab4", 150, 158, invulnerable: false);

            var seeded = new[]
            {
                new MapHarbinger.Encounter
                {
                    Index = 0, Kind = HarbingerCatalog.HarbingerKind.King, Variety = HarbingerCatalog.KingVariety,
                    X = 150, Y = 150, State = MapHarbinger.HarbingerState.Waiting, Life = MapHarbinger.HarbingerMaxLife,
                    Helpers = [m1, m2, m5, m6], Obelisks = [ob2, ob3, ob4], ObelisksSummoned = 3,
                },
                new MapHarbinger.Encounter
                {
                    Index = 1, Kind = HarbingerCatalog.HarbingerKind.Normal, Variety = "Metadata/Monsters/Avatar/Avatar6",
                    X = 160, Y = 150, State = MapHarbinger.HarbingerState.Waiting, Life = MapHarbinger.HarbingerMaxLife,
                },
            };
            instance.ContentState["Harbinger/encounters"] = JsonSerializer.Serialize(seeded);
            await MapHarbinger.Enter(conn);

            MapHarbinger.Encounter[] Encounters() =>
                JsonSerializer.Deserialize<MapHarbinger.Encounter[]>(instance.ContentState["Harbinger/encounters"])!;

            var king = Encounters()[0];
            var buffer = Encounters()[1];
            Assert.NotEqual(0u, king.Monster);
            Assert.NotEqual(0u, buffer.Monster);

            // 1. Repeat-wave summoning (skill_repeat_count = 2 => 3 sub-waves per activation).
            conn.Walk.Teleport(152, 150, 0);
            await MapHarbinger.Tick(conn, 0.2);
            king = Encounters()[0];
            int wave1 = king.Minions.Count;
            Assert.InRange(wave1, 1, 10);

            await MapHarbinger.Tick(conn, 0.4); // sub-wave 2 (repeat 1)
            king = Encounters()[0];
            int wave2 = king.Minions.Count;
            Assert.True(wave2 > wave1, $"Expected repeat sub-wave 2 to add minions ({wave1} -> {wave2})");

            await MapHarbinger.Tick(conn, 0.4); // sub-wave 3 (repeat 2)
            king = Encounters()[0];
            int wave3 = king.Minions.Count;
            Assert.True(wave3 > wave2, $"Expected repeat sub-wave 3 to add minions ({wave2} -> {wave3})");
            Assert.True(wave3 <= HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.King, 75).AliveCap);

            // 2. Obelisk Scarab3 ally auras (-75% damage taken, -20% action speed) + Minion5 curse aura active!
            uint sampleMinionId = king.Minions[0];
            var sampleMinion = conn.Monsters[sampleMinionId];
            Assert.Equal(-75, sampleMinion.Effects["base_damage_taken_+%"]);
            Assert.True(sampleMinion.Effects["action_speed_-%"] <= -20,
                $"Expected obelisk action_speed_-% <= -20, got {sampleMinion.Effects["action_speed_-%"]}");

            var playerCurseStats = MapHarbinger.PlayerStats(conn, []);
            Assert.True(playerCurseStats.Count > 0, "Expected Minion5 Enfeeble/TemporalChains curse aura stats on nearby player");

            // An immune player is not cursed, and is cursed again once the immunity ends.
            conn.Immune = true;
            await MapHarbinger.Tick(conn, 0.01);
            Assert.Empty(MapHarbinger.PlayerStats(conn, []));
            conn.Immune = false;
            await MapHarbinger.Tick(conn, 0.01);
            Assert.NotEmpty(MapHarbinger.PlayerStats(conn, []));

            // 3. Advance ticks so King Harbinger, Avatar6, helpers (FireMortar, BeamCannon, ActionSpeedAura) and
            //    Empowered Obelisks (Scarab2 BallLightning, Scarab4 WarpFlame) cast their real skills.
            uint lifeBefore = conn.PlayerLife;
            for (int step = 0; step < 12; step++)
                await MapHarbinger.Tick(conn, 0.5);

            // Verify King Harbinger warped away from its initial (150, 150) cell near its anchor.
            var kingPos = conn.Monsters[king.Monster].Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            Assert.NotEqual((150, 150), kingPos);

            // Verify Avatar6 buffed its living minions with damage_+% >= 60 and base_actor_scale_+% == 25.
            var bufferEnc = Encounters()[1];
            Assert.NotEmpty(bufferEnc.Minions);
            Assert.Contains(bufferEnc.Minions, mid =>
                conn.Monsters[mid].Effects["damage_+%"] >= 60 && conn.Monsters[mid].Effects.ActorScalePct == 25);

            // Verify King helper Minion6 applied HarbingerActionSpeedAuraAtlas (-30 action_speed_-% on top of -20 obelisk).
            Assert.True(sampleMinion.Effects["action_speed_-%"] <= -50,
                $"Expected combined obelisk (-20) + Minion6 (-30) action_speed_-% <= -50, got {sampleMinion.Effects["action_speed_-%"]}");

            // Verify BeamCannon delayed impact / WarpFlame burning ground dealt real hostile damage to the player.
            Assert.True(conn.PlayerLife < lifeBefore, $"Expected hostile Harbinger/helper/obelisk skills to damage player ({lifeBefore} -> {conn.PlayerLife})");

            // 4. Killing HarbingerTotemScarab3 immediately removes its damage-reduction & action-speed aura stats!
            var ob3Body = conn.Monsters[ob3];
            lock (conn.Monsters) { ob3Body.Life = 0; ob3Body.Dead = true; }
            MapHarbinger.Kill(new(conn, ob3Body, ob3Body.Variety!.Id, (158, 152)));
            await MapHarbinger.Tick(conn, 0.1);
            Assert.Equal(0, sampleMinion.Effects["base_damage_taken_+%"]);
            Assert.DoesNotContain(sampleMinion.TemporaryStats.Keys, k => k.StartsWith($"harbinger:obelisk:{ob3}:", StringComparison.Ordinal));

            // 5. Leaving cleans up player curse stats (zero-alloc early-out restored).
            MapHarbinger.Leave(conn);
            var afterLeave = MapHarbinger.PlayerStats(conn, []);
            Assert.Empty(afterLeave);

            // 6. Verify real 0x1ab action packets were emitted on the decrypted wire for Harbinger, helpers, and spell obelisks.
            byte[] captured = await wire.FinishAndCaptureAsync();
            Assert.True(WireContainsAction0x1ab(captured, king.Monster), "Expected 0x1ab for King Harbinger");
            Assert.True(WireContainsAction0x1ab(captured, m1), "Expected 0x1ab for AvatarAtlasMinion1 (AvatarFireMortarAtlas)");
            Assert.True(WireContainsAction0x1ab(captured, m2), "Expected 0x1ab for AvatarAtlasMinion2 (HarbingerSpecialBeamCannonAtlas)");
            Assert.True(WireContainsAction0x1ab(captured, m6), "Expected 0x1ab for AvatarAtlasMinion6 (HarbingerActionSpeedAuraAtlas)");
            Assert.True(WireContainsAction0x1ab(captured, ob2), "Expected 0x1ab for HarbingerTotemScarab2 (HarbingerBallLightningTotem)");
            Assert.True(WireContainsAction0x1ab(captured, ob4), "Expected 0x1ab for HarbingerTotemScarab4 (HarbingerWarpFlameTotem)");
        }
        finally
        {
            tankHook?.Dispose();
            MapHarbinger.Leave(conn);
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
